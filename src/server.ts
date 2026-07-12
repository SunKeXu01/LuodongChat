import { randomUUID } from "node:crypto";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import { pathToFileURL } from "node:url";
import {
  extractBearerKey,
  hashGatewayKey,
  StaticGatewayKeyVerifier,
  type GatewayKeyLimitProvider,
  type GatewayKeyVerifier,
} from "./auth.js";
import { loadConfig, type GatewayConfig } from "./config.js";
import { InMemoryLimiter, type RequestLimiter } from "./limiter.js";
import { InMemoryRequestLedger, type RequestLedger } from "./ledger.js";
import { UpstreamPool } from "./upstream-pool.js";
import { createRuntimeDependencies } from "./runtime.js";

const MAX_REQUEST_BYTES = 10 * 1024 * 1024;
const RETRYABLE_STATUS = new Set([429, 500, 502, 503, 504]);
const LANDING_PAGE = `<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>ChatGPT 连接器</title>
  <style>
    :root{color-scheme:light dark;font-family:system-ui,-apple-system,"Segoe UI",sans-serif}
    body{margin:0;min-height:100vh;display:grid;place-items:center;background:#f5f7fb;color:#111827}
    main{width:min(520px,calc(100% - 48px));padding:48px;border:1px solid #e5e7eb;border-radius:24px;background:white;box-shadow:0 18px 50px #11182712}
    h1{margin:0 0 16px;font-size:32px}.status{display:flex;gap:10px;align-items:center;color:#047857;font-weight:600}.dot{width:10px;height:10px;border-radius:50%;background:#10b981;box-shadow:0 0 0 5px #10b98120}
    p{line-height:1.7;color:#4b5563}.meta{margin-top:32px;padding-top:24px;border-top:1px solid #e5e7eb;font-size:14px;color:#6b7280}
    @media(prefers-color-scheme:dark){body{background:#0b1020;color:#f9fafb}main{background:#111827;border-color:#263244}p,.meta{color:#9ca3af}.meta{border-color:#263244}}
  </style>
</head>
<body><main><h1>ChatGPT 连接器</h1><div class="status"><span class="dot"></span>服务运行正常</div><p>面向 Codex 的一键模型连接工具。Windows 客户端正在开发中，将提供安全配置、自动备份和离线恢复能力。</p><div class="meta">网关：520skx.com · Responses API</div></main></body>
</html>`;

function json(res: ServerResponse, status: number, body: unknown): void {
  res.writeHead(status, { "content-type": "application/json; charset=utf-8" });
  res.end(JSON.stringify(body));
}

async function readBody(req: IncomingMessage): Promise<Buffer> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    size += buffer.length;
    if (size > MAX_REQUEST_BYTES) throw new Error("request_too_large");
    chunks.push(buffer);
  }
  return Buffer.concat(chunks);
}

function safeSubject(key: string): string {
  return hashGatewayKey(key).slice(0, 16);
}

export interface GatewayServerOptions {
  ledger?: RequestLedger;
  limiter?: RequestLimiter;
  keyVerifier?: GatewayKeyVerifier;
  keyLimitProvider?: GatewayKeyLimitProvider;
}

export function createGatewayServer(config: GatewayConfig, options: GatewayServerOptions = {}) {
  const limiter = options.limiter ?? new InMemoryLimiter(config.requestsPerMinute, config.maxConcurrentRequests);
  const upstreamPool = new UpstreamPool(config.upstreamApiKeys);
  const ledger = options.ledger ?? new InMemoryRequestLedger();
  const keyVerifier = options.keyVerifier ?? new StaticGatewayKeyVerifier(config.gatewayKeyHashes);

  return createServer(async (req, res) => {
    const requestId = req.headers["x-request-id"]?.toString() || randomUUID();
    res.setHeader("x-request-id", requestId);

    if (req.method === "GET" && req.url === "/healthz") {
      return json(res, 200, { status: "ok" });
    }
    if (req.method === "GET" && req.url === "/") {
      res.writeHead(200, {
        "content-type": "text/html; charset=utf-8",
        "content-security-policy": "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'",
        "referrer-policy": "no-referrer",
        "x-content-type-options": "nosniff",
        "x-frame-options": "DENY",
      });
      return res.end(LANDING_PAGE);
    }
    const isResponsesRoute = req.url === "/responses" || req.url === "/v1/responses";
    if (req.method !== "POST" || !isResponsesRoute) {
      return json(res, 404, { error: { code: "not_found", message: "Route not found" } });
    }

    const gatewayKey = extractBearerKey(req.headers.authorization);
    if (!gatewayKey || !await keyVerifier.verify(gatewayKey)) {
      return json(res, 401, { error: { code: "invalid_gateway_key", message: "Gateway key is invalid or revoked" } });
    }

    const keyHash = hashGatewayKey(gatewayKey);
    const limits = await options.keyLimitProvider?.getLimits(keyHash);
    if (limits?.dailyLimitExceeded) {
      return json(res, 429, { error: { code: "daily_quota_exceeded", message: "Daily gateway quota exceeded" } });
    }

    const permit = await limiter.acquire(safeSubject(gatewayKey), limits ? {
      requestsPerMinute: limits.requestsPerMinute,
      maxConcurrent: limits.maxConcurrentRequests,
    } : undefined);
    if (!permit.ok) {
      const code = permit.reason === "rate" ? "rate_limit_exceeded" : "concurrency_limit_exceeded";
      return json(res, 429, { error: { code, message: "Gateway request limit exceeded" } });
    }

    const startedAt = Date.now();
    let ledgerStarted = false;
    let attempts = 0;
    try {
      await ledger.startRequest({ requestId, gatewayKeyHash: keyHash, startedAt: new Date(startedAt) });
      ledgerStarted = true;
      const body = await readBody(req);
      let upstream: Response | undefined;
      let selectedCredentialId: string | undefined;
      const attemptedCredentialIds = new Set<string>();
      for (let attempt = 1; attempt <= 2; attempt += 1) {
        let credential = upstreamPool.acquire(attemptedCredentialIds);
        if (!credential && attemptedCredentialIds.size > 0) credential = upstreamPool.acquire();
        if (!credential) break;
        selectedCredentialId = credential.id;
        attemptedCredentialIds.add(credential.id);
        attempts = attempt;
        const attemptStartedAt = Date.now();
        try {
          upstream = await fetch(`${config.upstreamBaseUrl}${config.upstreamResponsesPath}`, {
            method: "POST",
            headers: {
              authorization: `Bearer ${credential.apiKey}`,
              "content-type": req.headers["content-type"] ?? "application/json",
              "x-request-id": requestId,
            },
            body: new Uint8Array(body),
            signal: AbortSignal.timeout(config.upstreamTimeoutMs),
          });
          const retryable = RETRYABLE_STATUS.has(upstream.status);
          const credentialRejected = upstream.status === 401 || upstream.status === 403;
          if (credentialRejected) {
            upstreamPool.recordFatalFailure(credential.id);
          } else if (retryable) {
            upstreamPool.recordFailure(credential.id, true);
          } else {
            upstreamPool.recordSuccess(credential.id);
          }
          console.info(JSON.stringify({
            event: "upstream_attempt",
            requestId,
            attempt,
            credentialId: credential.id.slice(0, 12),
            status: upstream.status,
            retryable: retryable || credentialRejected,
            durationMs: Date.now() - attemptStartedAt,
          }));
          await ledger.recordAttempt({
            requestId,
            attempt,
            credentialId: credential.id,
            startedAt: new Date(attemptStartedAt),
            endedAt: new Date(),
            statusCode: upstream.status,
            retryable: retryable || credentialRejected,
          });
          if ((!retryable && !credentialRejected) || attempt === 2) break;
          await upstream.body?.cancel();
        } catch (error) {
          upstreamPool.recordFailure(credential.id, true);
          console.warn(JSON.stringify({
            event: "upstream_attempt",
            requestId,
            attempt,
            credentialId: credential.id.slice(0, 12),
            error: error instanceof Error ? error.name : "unknown",
            retryable: true,
            durationMs: Date.now() - attemptStartedAt,
          }));
          await ledger.recordAttempt({
            requestId,
            attempt,
            credentialId: credential.id,
            startedAt: new Date(attemptStartedAt),
            endedAt: new Date(),
            errorClass: error instanceof Error ? error.name : "unknown",
            retryable: true,
          });
          if (attempt === 2) throw error;
        }
      }

      if (!upstream) throw new Error("upstream_unavailable");
      res.statusCode = upstream.status;
      for (const name of ["content-type", "cache-control", "openai-processing-ms"]) {
        const value = upstream.headers.get(name);
        if (value) res.setHeader(name, value);
      }
      if (!upstream.body) {
        res.end();
        await ledger.finishRequest({
          requestId,
          status: upstream.ok ? "completed" : "failed",
          endedAt: new Date(),
          statusCode: upstream.status,
          attempts,
          errorClass: upstream.ok ? undefined : `upstream_http_${upstream.status}`,
        });
        return;
      }
      res.once("close", () => upstream?.body?.cancel().catch(() => undefined));
      await pipeline(Readable.fromWeb(upstream.body as never), res);
      await ledger.finishRequest({
        requestId,
        status: upstream.ok ? "completed" : "failed",
        endedAt: new Date(),
        statusCode: upstream.status,
        attempts,
        errorClass: upstream.ok ? undefined : `upstream_http_${upstream.status}`,
      });
      console.info(JSON.stringify({ requestId, status: upstream.status, attempts, credentialId: selectedCredentialId?.slice(0, 12), durationMs: Date.now() - startedAt }));
    } catch (error) {
      const tooLarge = error instanceof Error && error.message === "request_too_large";
      if (!res.headersSent) {
        json(res, tooLarge ? 413 : 502, {
          error: {
            code: tooLarge ? "request_too_large" : "upstream_unavailable",
            message: tooLarge ? "Request body is too large" : "Upstream service is temporarily unavailable",
          },
        });
      } else {
        res.destroy();
      }
      console.error(JSON.stringify({ requestId, error: error instanceof Error ? error.name : "unknown", durationMs: Date.now() - startedAt }));
      if (ledgerStarted) {
        await ledger.finishRequest({
          requestId,
          status: "failed",
          endedAt: new Date(),
          attempts,
          errorClass: tooLarge ? "request_too_large" : error instanceof Error ? error.name : "unknown",
        }).catch(() => undefined);
      }
    } finally {
      await permit.release();
    }
  });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const config = loadConfig();
  const runtime = await createRuntimeDependencies(config);
  const server = createGatewayServer(config, runtime).listen(config.port, () => {
    console.info(JSON.stringify({ event: "gateway_started", port: config.port }));
  });
  const shutdown = async () => {
    server.close();
    await runtime.close();
  };
  process.once("SIGINT", shutdown);
  process.once("SIGTERM", shutdown);
}

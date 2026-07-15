import { randomBytes, randomUUID } from "node:crypto";
import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import { pathToFileURL } from "node:url";
import { createReadStream } from "node:fs";
import { readFile, stat } from "node:fs/promises";
import { join } from "node:path";
import {
  extractBearerKey,
  hashGatewayKey,
  StaticGatewayKeyVerifier,
  verifyGatewayKey,
  type GatewayKeyLimitProvider,
  type GatewayKeyVerifier,
} from "./auth.js";
import { loadConfig, type GatewayConfig } from "./config.js";
import { InMemoryLimiter, type RequestLimiter } from "./limiter.js";
import { InMemoryRequestLedger, type RequestLedger } from "./ledger.js";
import { UpstreamPool } from "./upstream-pool.js";
import { createRuntimeDependencies } from "./runtime.js";
import type { AdminRepository } from "./admin.js";
import { ADMIN_HTML, ADMIN_JS } from "./admin-assets.js";
import { AdminLoginGuard, type AdminLoginProtector } from "./admin-security.js";
import { EnrollmentService } from "./self-service.js";
import type { ChatSyncRepository, SyncedMessage } from "./sync.js";

const MAX_REQUEST_BYTES = 10 * 1024 * 1024;
const RETRYABLE_STATUS = new Set([429, 500, 502, 503, 504]);
const LANDING_PAGE = `<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>泺栋chat</title>
  <style>
    :root{color-scheme:light dark;font-family:system-ui,-apple-system,"Segoe UI",sans-serif}
    body{margin:0;min-height:100vh;display:grid;place-items:center;background:#f5f7fb;color:#111827}
    main{width:min(520px,calc(100% - 48px));padding:48px;border:1px solid #e5e7eb;border-radius:24px;background:white;box-shadow:0 18px 50px #11182712}
    h1{margin:0 0 16px;font-size:32px}.status{display:flex;gap:10px;align-items:center;color:#047857;font-weight:600}.dot{width:10px;height:10px;border-radius:50%;background:#10b981;box-shadow:0 0 0 5px #10b98120}
    p{line-height:1.7;color:#4b5563}.meta{margin-top:32px;padding-top:24px;border-top:1px solid #e5e7eb;font-size:14px;color:#6b7280}
    @media(prefers-color-scheme:dark){body{background:#0b1020;color:#f9fafb}main{background:#111827;border-color:#263244}p,.meta{color:#9ca3af}.meta{border-color:#263244}}
  </style>
</head>
<body><main><h1>泺栋chat</h1><div class="status"><span class="dot"></span>服务运行正常</div><p>独立的 GPT-5.6 对话客户端。使用邮箱账号登录，无需安装官方 ChatGPT，也无需配置 API 密钥。</p><div class="meta">Windows · Android · 跨端同步</div></main></body>
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

async function readJsonObject(req: IncomingMessage): Promise<Record<string, unknown>> {
  const body = await readBody(req);
  const value: unknown = JSON.parse(body.toString("utf8"));
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("invalid_json");
  return value as Record<string, unknown>;
}

function positiveAdminInteger(value: unknown, maximum = 1_000_000): number | null {
  return Number.isSafeInteger(value) && Number(value) > 0 && Number(value) <= maximum ? Number(value) : null;
}

function safeSubject(key: string): string {
  return hashGatewayKey(key).slice(0, 16);
}

export interface GatewayServerOptions {
  ledger?: RequestLedger;
  limiter?: RequestLimiter;
  keyVerifier?: GatewayKeyVerifier;
  keyLimitProvider?: GatewayKeyLimitProvider;
  adminRepository?: AdminRepository;
  adminLoginProtector?: AdminLoginProtector;
  enrollmentService?: EnrollmentService;
  chatSyncRepository?: ChatSyncRepository;
}

export function createGatewayServer(config: GatewayConfig, options: GatewayServerOptions = {}) {
  const limiter = options.limiter ?? new InMemoryLimiter(config.requestsPerMinute, config.maxConcurrentRequests);
  const upstreamPool = new UpstreamPool(config.upstreams ?? config.upstreamApiKeys);
  const ledger = options.ledger ?? new InMemoryRequestLedger();
  const keyVerifier = options.keyVerifier ?? new StaticGatewayKeyVerifier(config.gatewayKeyHashes);
  const adminLoginGuard = options.adminLoginProtector ?? new AdminLoginGuard();

  return createServer(async (req, res) => {
    const requestId = req.headers["x-request-id"]?.toString() || randomUUID();
    res.setHeader("x-request-id", requestId);

    if (req.method === "GET" && req.url === "/healthz") {
      return json(res, 200, { status: "ok", version: config.version ?? "unknown" });
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
    if (req.url?.startsWith("/enrollment/")) {
      res.setHeader("cache-control", "no-store");
      return json(res, 410, { error: { code: "account_login_required", message: "请使用客户端邮箱登录，网关密钥不再向用户发放" } });
    }
    const releaseRoot = process.env.CLIENT_RELEASE_ROOT?.trim();
    if (req.method === "GET" && req.url === "/client/update.json" && releaseRoot) {
      try {
        const manifest = await readFile(join(releaseRoot, "update.json"));
        res.writeHead(200, { "content-type": "application/json; charset=utf-8", "cache-control": "no-cache", "x-content-type-options": "nosniff" });
        return res.end(manifest);
      } catch { return json(res, 404, { error: { code: "client_release_unavailable" } }); }
    }
    const clientAsset = req.method === "GET" && req.url === "/client/download/LuodongChat.exe" ? "LuodongChat.exe"
      : req.method === "GET" && req.url === "/client/download/LuodongChat.exe.sha256" ? "LuodongChat.exe.sha256"
      : req.method === "GET" && req.url === "/client/download/LuodongChat.apk" ? "LuodongChat.apk"
      : req.method === "GET" && req.url === "/client/download/LuodongChat.apk.sha256" ? "LuodongChat.apk.sha256"
      : req.method === "GET" && req.url === "/client/download/ChatGPTConnector.exe" ? "ChatGPTConnector.exe"
      : req.method === "GET" && req.url === "/client/download/ChatGPTConnector.exe.sha256" ? "ChatGPTConnector.exe.sha256"
      : req.method === "GET" && req.url === "/client/download/ChatGPTConnector.apk" ? "ChatGPTConnector.apk"
      : req.method === "GET" && req.url === "/client/download/ChatGPTConnector.apk.sha256" ? "ChatGPTConnector.apk.sha256" : null;
    if (clientAsset && releaseRoot) {
      try {
        const path = join(releaseRoot, clientAsset);
        const metadata = await stat(path);
        res.writeHead(200, {
          "content-type": clientAsset.endsWith(".exe") ? "application/vnd.microsoft.portable-executable"
            : clientAsset.endsWith(".apk") ? "application/vnd.android.package-archive" : "text/plain; charset=utf-8",
          "content-length": metadata.size,
          "cache-control": "public, max-age=300",
          "content-disposition": `attachment; filename="${clientAsset}"`,
          "x-content-type-options": "nosniff",
        });
        return createReadStream(path).pipe(res);
      } catch { return json(res, 404, { error: { code: "client_release_unavailable" } }); }
    }
    if (req.method === "GET" && req.url === "/enrollment/status") {
      res.setHeader("cache-control", "no-store");
      return json(res, 200, { enabled: Boolean(options.enrollmentService) });
    }
    if (req.method === "POST" && req.url === "/account/code" && options.enrollmentService) {
      try {
        const input = await readJsonObject(req);
        const email = typeof input.email === "string" ? EnrollmentService.normalizeEmail(input.email) : null;
        if (!email) return json(res, 400, { error: { code: "invalid_email", message: "请输入有效的邮箱地址" } });
        const clientAddress = req.headers["x-real-ip"]?.toString().trim() || req.socket.remoteAddress || "unknown";
        const result = await options.enrollmentService.requestCode(email, hashGatewayKey(clientAddress).slice(0, 16));
        if (result === "rate_limited") {
          res.setHeader("retry-after", "60");
          return json(res, 429, { error: { code: "account_rate_limited", message: "验证码发送过于频繁，请稍后再试" } });
        }
        res.setHeader("cache-control", "no-store");
        return json(res, 202, { status: "code_sent", expiresInSeconds: 600 });
      } catch (error) {
        if (error instanceof SyntaxError || (error instanceof Error && error.message === "invalid_json")) return json(res, 400, { error: { code: "invalid_json" } });
        return json(res, 503, { error: { code: "email_unavailable", message: "验证码邮件暂时无法发送" } });
      }
    }
    if (req.method === "POST" && req.url === "/account/verify" && options.enrollmentService) {
      const input = await readJsonObject(req);
      const email = typeof input.email === "string" ? EnrollmentService.normalizeEmail(input.email) : null;
      const code = typeof input.code === "string" && /^\d{6}$/.test(input.code) ? input.code : null;
      if (!email || !code) return json(res, 400, { error: { code: "invalid_verification_input", message: "请输入邮箱和 6 位验证码" } });
      const result = await options.enrollmentService.verifyAndLogin(email, code);
      res.setHeader("cache-control", "no-store");
      if (result.status === "authenticated") return json(res, 200, result);
      if (result.status === "disabled") return json(res, 403, { error: { code: "account_disabled", message: "账号已停用" } });
      return json(res, 401, { error: { code: `verification_${result.status}`, message: "验证码无效或已过期" } });
    }
    if (req.method === "POST" && req.url === "/account/register" && options.enrollmentService) {
      const input = await readJsonObject(req);
      const email = typeof input.email === "string" ? EnrollmentService.normalizeEmail(input.email) : null;
      const code = typeof input.code === "string" && /^\d{6}$/.test(input.code) ? input.code : null;
      const password = typeof input.password === "string" ? EnrollmentService.validatePassword(input.password) : null;
      if (!email) return json(res, 400, { error: { code: "invalid_email", message: "请输入有效的邮箱地址" } });
      if (!password) return json(res, 400, { error: { code: "invalid_password", message: "密码应为 8 至 128 个字符，并同时包含字母和数字" } });
      if (!code) return json(res, 400, { error: { code: "invalid_verification_input", message: "请输入 6 位验证码" } });
      const result = await options.enrollmentService.registerWithPassword(email, code, password);
      res.setHeader("cache-control", "no-store");
      if (result.status === "authenticated") return json(res, 200, result);
      if (result.status === "already_registered") return json(res, 409, { error: { code: "account_already_registered", message: "该邮箱已注册，请直接登录；如忘记密码可使用验证码重置" } });
      if (result.status === "disabled") return json(res, 403, { error: { code: "account_disabled", message: "账号已停用" } });
      return json(res, 401, { error: { code: `verification_${result.status}`, message: "验证码无效或已过期" } });
    }
    if (req.method === "POST" && req.url === "/account/password/reset" && options.enrollmentService) {
      const input = await readJsonObject(req);
      const email = typeof input.email === "string" ? EnrollmentService.normalizeEmail(input.email) : null;
      const code = typeof input.code === "string" && /^\d{6}$/.test(input.code) ? input.code : null;
      const password = typeof input.password === "string" ? EnrollmentService.validatePassword(input.password) : null;
      if (!email) return json(res, 400, { error: { code: "invalid_email", message: "请输入有效的邮箱地址" } });
      if (!password) return json(res, 400, { error: { code: "invalid_password", message: "密码应为 8 至 128 个字符，并同时包含字母和数字" } });
      if (!code) return json(res, 400, { error: { code: "invalid_verification_input", message: "请输入 6 位验证码" } });
      const result = await options.enrollmentService.resetPassword(email, code, password);
      res.setHeader("cache-control", "no-store");
      if (result.status === "authenticated") return json(res, 200, result);
      if (result.status === "not_registered") return json(res, 404, { error: { code: "account_not_registered", message: "该邮箱尚未注册，请先注册账号" } });
      if (result.status === "disabled") return json(res, 403, { error: { code: "account_disabled", message: "账号已停用" } });
      return json(res, 401, { error: { code: `verification_${result.status}`, message: "验证码无效或已过期" } });
    }
    if (req.method === "POST" && req.url === "/account/login" && options.enrollmentService) {
      const input = await readJsonObject(req);
      const email = typeof input.email === "string" ? EnrollmentService.normalizeEmail(input.email) : null;
      const password = typeof input.password === "string" ? EnrollmentService.validatePassword(input.password) : null;
      if (!email || !password) return json(res, 400, { error: { code: "invalid_login", message: "邮箱或密码不正确" } });
      const result = await options.enrollmentService.loginWithPassword(email, password);
      res.setHeader("cache-control", "no-store");
      if (result.status === "authenticated") return json(res, 200, result);
      if (result.status === "disabled") return json(res, 403, { error: { code: "account_disabled", message: "账号已停用" } });
      if (result.status === "locked") {
        res.setHeader("retry-after", "900");
        return json(res, 429, { error: { code: "password_temporarily_locked", message: "密码错误次数过多，请 15 分钟后再试或使用验证码登录" } });
      }
      return json(res, 401, { error: { code: "invalid_credentials", message: "邮箱或密码不正确" } });
    }
    if (req.url?.startsWith("/account/") && options.enrollmentService) {
      const accessToken = extractBearerKey(req.headers.authorization);
      if (!accessToken?.startsWith("usr_")) return json(res, 401, { error: { code: "login_required", message: "请先登录" } });
      if (req.method === "GET" && req.url === "/account/profile") {
        const profile = await options.enrollmentService.authenticate(accessToken);
        return profile ? json(res, 200, profile) : json(res, 401, { error: { code: "session_expired", message: "登录已过期，请重新登录" } });
      }
      if (req.method === "PATCH" && req.url === "/account/profile") {
        const input = await readJsonObject(req);
        const nickname = typeof input.nickname === "string" ? input.nickname.trim() : "";
        if (nickname.length < 2 || nickname.length > 20) return json(res, 400, { error: { code: "invalid_nickname", message: "网名应为 2 至 20 个字符" } });
        const profile = await options.enrollmentService.updateProfile(accessToken, nickname);
        return profile ? json(res, 200, profile) : json(res, 401, { error: { code: "session_expired" } });
      }
      if (req.method === "PUT" && req.url === "/account/avatar") {
        const input = await readJsonObject(req);
        const mediaType = typeof input.mediaType === "string" ? input.mediaType : "";
        const dataBase64 = typeof input.dataBase64 === "string" ? input.dataBase64.replace(/\s/g, "") : "";
        let avatar: Buffer | null = null;
        try { avatar = Buffer.from(dataBase64, "base64"); } catch { }
        const signatureValid = avatar && avatar.length > 0 && avatar.length <= 512 * 1024 && (
          (mediaType === "image/jpeg" && avatar[0] === 0xff && avatar[1] === 0xd8 && avatar[2] === 0xff)
          || (mediaType === "image/png" && avatar.subarray(0, 8).equals(Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])))
          || (mediaType === "image/webp" && avatar.subarray(0, 4).toString("ascii") === "RIFF" && avatar.subarray(8, 12).toString("ascii") === "WEBP")
        );
        if (!signatureValid || !/^[A-Za-z0-9+/]+={0,2}$/.test(dataBase64)) {
          return json(res, 400, { error: { code: "invalid_avatar", message: "头像必须是小于 512 KB 的 JPG、PNG 或 WebP 图片" } });
        }
        const profile = await options.enrollmentService.updateAvatar(accessToken, mediaType, dataBase64);
        return profile ? json(res, 200, profile) : json(res, 401, { error: { code: "session_expired" } });
      }
      if (req.method === "POST" && req.url === "/account/logout") {
        await options.enrollmentService.logout(accessToken);
        return json(res, 200, { status: "logged_out" });
      }
    }
    if (req.url?.startsWith("/sync/") && options.enrollmentService && options.chatSyncRepository) {
      const accessToken = extractBearerKey(req.headers.authorization);
      const account = accessToken?.startsWith("usr_") ? await options.enrollmentService.authenticate(accessToken) : null;
      if (!account) return json(res, 401, { error: { code: "login_required", message: "请先登录" } });
      res.setHeader("cache-control", "no-store");
      if (req.method === "GET" && req.url.startsWith("/sync/state")) {
        const parsed = new URL(req.url, "http://localhost");
        const sinceValue = parsed.searchParams.get("since");
        const since = sinceValue ? new Date(sinceValue) : new Date(0);
        if (Number.isNaN(since.getTime())) return json(res, 400, { error: { code: "invalid_sync_cursor", message: "同步时间无效" } });
        return json(res, 200, await options.chatSyncRepository.getChanges(account.id, since));
      }
      if (req.method === "POST" && req.url === "/sync/conversations") {
        const input = await readJsonObject(req);
        const id = typeof input.id === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(input.id) ? input.id : null;
        const title = typeof input.title === "string" ? input.title.trim() : "";
        if (!id || title.length < 1 || title.length > 120) return json(res, 400, { error: { code: "invalid_conversation" } });
        const conversation = await options.chatSyncRepository.upsertConversation(account.id, id, title);
        return conversation ? json(res, 200, conversation) : json(res, 409, { error: { code: "conversation_conflict" } });
      }
      const deleteMatch = /^\/sync\/conversations\/([0-9a-f-]{36})$/.exec(req.url);
      if (req.method === "DELETE" && deleteMatch) {
        const deleted = await options.chatSyncRepository.deleteConversation(account.id, deleteMatch[1]!);
        return deleted ? json(res, 200, { status: "deleted" }) : json(res, 404, { error: { code: "conversation_not_found" } });
      }
      if (req.method === "POST" && req.url === "/sync/messages") {
        const input = await readJsonObject(req);
        const uuid = (value: unknown) => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value) ? value : null;
        const id = uuid(input.id); const conversationId = uuid(input.conversationId);
        const role = typeof input.role === "string" && ["user", "assistant", "system"].includes(input.role) ? input.role as SyncedMessage["role"] : null;
        const content = typeof input.content === "string" ? input.content.trim() : "";
        const clientCreatedAt = typeof input.clientCreatedAt === "string" ? new Date(input.clientCreatedAt) : new Date();
        if (!id || !conversationId || !role || content.length < 1 || content.length > 32_000 || Number.isNaN(clientCreatedAt.getTime())) {
          return json(res, 400, { error: { code: "invalid_message" } });
        }
        const message = await options.chatSyncRepository.appendMessage(account.id, { id, conversationId, role, content, clientCreatedAt });
        return message ? json(res, 200, message) : json(res, 404, { error: { code: "conversation_not_found" } });
      }
    }
    const adminEnabled = (config.adminKeyHashes?.size ?? 0) > 0 && options.adminRepository;
    if (req.method === "GET" && req.url === "/admin" && adminEnabled) {
      res.writeHead(200, {
        "content-type": "text/html; charset=utf-8",
        "cache-control": "no-store",
        "content-security-policy": "default-src 'none'; style-src 'unsafe-inline'; script-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'",
        "referrer-policy": "no-referrer",
        "x-content-type-options": "nosniff",
        "x-frame-options": "DENY",
      });
      return res.end(ADMIN_HTML);
    }
    if (req.method === "GET" && req.url === "/admin/app.js" && adminEnabled) {
      res.writeHead(200, { "content-type": "text/javascript; charset=utf-8", "cache-control": "no-store", "x-content-type-options": "nosniff" });
      return res.end(ADMIN_JS);
    }
    if (req.url?.startsWith("/admin/api/") && adminEnabled) {
      const adminKey = extractBearerKey(req.headers.authorization);
      const clientAddress = req.headers["x-real-ip"]?.toString().trim()
        || req.socket.remoteAddress || "unknown";
      const ipFingerprint = hashGatewayKey(clientAddress).slice(0, 16);
      const retryAfter = await adminLoginGuard.retryAfterSeconds(ipFingerprint);
      if (retryAfter > 0) {
        res.setHeader("retry-after", String(retryAfter));
        return json(res, 429, { error: { code: "admin_temporarily_blocked", message: "Too many failed login attempts" } });
      }
      if (!adminKey || !verifyGatewayKey(adminKey, config.adminKeyHashes ?? new Set())) {
        const blockedFor = await adminLoginGuard.recordFailure(ipFingerprint);
        await options.adminRepository!.recordLogin(
          false, adminKey ? hashGatewayKey(adminKey).slice(0, 16) : "missing", ipFingerprint,
        ).catch(() => undefined);
        if (blockedFor > 0) res.setHeader("retry-after", String(blockedFor));
        return json(res, 401, { error: { code: "invalid_admin_key", message: "Administrator key is invalid" } });
      }
      if (req.method === "POST" && req.url === "/admin/api/session") {
        await adminLoginGuard.recordSuccess(ipFingerprint);
        const actorFingerprint = hashGatewayKey(adminKey).slice(0, 16);
        await options.adminRepository!.recordLogin(true, actorFingerprint, ipFingerprint);
        res.setHeader("cache-control", "no-store");
        return json(res, 200, { status: "authenticated", expiresInSeconds: 900 });
      }
      res.setHeader("cache-control", "no-store");
      if (req.method === "GET" && req.url === "/admin/api/summary") return json(res, 200, { ...(await options.adminRepository!.getSummary()), version: config.version ?? "unknown" });
      if (req.method === "GET" && req.url === "/admin/api/keys") return json(res, 200, await options.adminRepository!.listKeys());
      if (req.method === "GET" && req.url === "/admin/api/observability") return json(res, 200, await options.adminRepository!.getObservability());
      if (req.method === "GET" && req.url === "/admin/api/upstreams") {
        const persisted = new Map((await options.adminRepository!.getUpstreamStats()).map((item) => [item.credentialId, item]));
        return json(res, 200, upstreamPool.snapshot().map((item) => ({
          credentialId: item.id.slice(0, 12),
          health: item.health,
          consecutiveFailures: item.consecutiveFailures,
          inFlight: item.inFlight,
          attempts: persisted.get(item.id)?.attempts ?? 0,
          failures: persisted.get(item.id)?.failures ?? 0,
          retryableAttempts: persisted.get(item.id)?.retryableAttempts ?? 0,
          averageDurationMs: persisted.get(item.id)?.averageDurationMs ?? null,
        })));
      }
      if (req.method === "GET" && req.url === "/admin/api/deployments") {
        return json(res, 200, await options.adminRepository!.listDeployments());
      }
      const actorFingerprint = hashGatewayKey(adminKey).slice(0, 16);
      try {
        if (req.method === "POST" && req.url === "/admin/api/deployments/rollback") {
          const accepted = await options.adminRepository!.requestRollback(actorFingerprint);
          return accepted
            ? json(res, 202, { status: "rollback_queued" })
            : json(res, 409, { error: { code: "rollback_unavailable", message: "A rollback is already pending or no deployment is available" } });
        }
        if (req.method === "POST" && req.url === "/admin/api/keys") {
          const input = await readJsonObject(req);
          const requestsPerMinute = positiveAdminInteger(input.requestsPerMinute, 10_000);
          const maxConcurrentRequests = positiveAdminInteger(input.maxConcurrentRequests, 1_000);
          if (!requestsPerMinute || !maxConcurrentRequests) {
            return json(res, 400, { error: { code: "invalid_admin_input", message: "Key limits are invalid" } });
          }
          const plaintextKey = `gw_${randomBytes(24).toString("hex")}`;
          const prefix = plaintextKey.slice(0, 11);
          await options.adminRepository!.createKey(
            { dailyLimit: null, requestsPerMinute, maxConcurrentRequests, expiresInDays: null },
            hashGatewayKey(plaintextKey), prefix, actorFingerprint,
          );
          return json(res, 201, { key: plaintextKey, prefix });
        }
        const quotaMatch = /^\/admin\/api\/keys\/(gw_[a-f\d]{8})\/quota$/.exec(req.url);
        if (req.method === "PUT" && quotaMatch) {
          const input = await readJsonObject(req);
          const requestsPerMinute = positiveAdminInteger(input.requestsPerMinute, 10_000);
          const maxConcurrentRequests = positiveAdminInteger(input.maxConcurrentRequests, 1_000);
          if (!requestsPerMinute || !maxConcurrentRequests) {
            return json(res, 400, { error: { code: "invalid_admin_input", message: "Key limits are invalid" } });
          }
          const updated = await options.adminRepository!.updateQuota(
            quotaMatch[1]!, { dailyLimit: null, requestsPerMinute, maxConcurrentRequests }, actorFingerprint,
          );
          return updated ? json(res, 200, { status: "updated" }) : json(res, 404, { error: { code: "key_not_found" } });
        }
        const revokeMatch = /^\/admin\/api\/keys\/(gw_[a-f\d]{8})\/revoke$/.exec(req.url);
        if (req.method === "POST" && revokeMatch) {
          const revoked = await options.adminRepository!.revokeKey(revokeMatch[1]!, actorFingerprint);
          return revoked ? json(res, 200, { status: "revoked" }) : json(res, 404, { error: { code: "key_not_found" } });
        }
      } catch (error) {
        if (error instanceof SyntaxError || (error instanceof Error && error.message === "invalid_json")) {
          return json(res, 400, { error: { code: "invalid_json", message: "Request body must be a JSON object" } });
        }
        throw error;
      }
    }
    const isResponsesRoute = req.url === "/responses" || req.url === "/v1/responses";
    if (req.method !== "POST" || !isResponsesRoute) {
      return json(res, 404, { error: { code: "not_found", message: "Route not found" } });
    }

    const credential = extractBearerKey(req.headers.authorization);
    let requestUserId: string | undefined;
    let keyHash: string | undefined;
    let subject: string;
    let limits;
    if (credential?.startsWith("usr_") && options.enrollmentService) {
      const account = await options.enrollmentService.authenticate(credential);
      if (!account) return json(res, 401, { error: { code: "session_expired", message: "登录已过期，请重新登录" } });
      requestUserId = account.id;
      subject = `user:${account.id}`;
      const accountLimits = options.enrollmentService.getRequestLimits();
      if (options.keyLimitProvider?.getLimitsForUser) {
        limits = await options.keyLimitProvider.getLimitsForUser(account.id);
        if (!limits) return json(res, 403, { error: { code: "account_gateway_disabled", message: "账号连接权限已停用" } });
      } else {
        limits = {
          requestsPerMinute: accountLimits.requestsPerMinute,
          maxConcurrentRequests: accountLimits.maxConcurrentRequests,
          dailyLimit: accountLimits.dailyLimit,
        };
      }
    } else {
      if (!credential || !await keyVerifier.verify(credential)) {
        return json(res, 401, { error: { code: "invalid_gateway_key", message: "Gateway key is invalid or revoked" } });
      }
      keyHash = hashGatewayKey(credential);
      subject = safeSubject(credential);
      limits = await options.keyLimitProvider?.getLimits(keyHash);
    }
    const permit = await limiter.acquire(subject, limits ? {
      requestsPerMinute: limits.requestsPerMinute,
      maxConcurrent: limits.maxConcurrentRequests,
      dailyLimit: limits.dailyLimit ?? undefined,
    } : undefined);
    if (!permit.ok) {
      const code = permit.reason === "rate" ? "rate_limit_exceeded"
        : permit.reason === "daily" ? "daily_quota_exceeded" : "concurrency_limit_exceeded";
      return json(res, 429, { error: { code, message: "Gateway request limit exceeded" } });
    }

    const startedAt = Date.now();
    let ledgerStarted = false;
    let attempts = 0;
    try {
      await ledger.startRequest({ requestId, gatewayKeyHash: keyHash, userId: requestUserId, startedAt: new Date(startedAt) });
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
          const baseUrl = credential.baseUrl ?? config.upstreamBaseUrl;
          const responsesPath = credential.responsesPath ?? config.upstreamResponsesPath;
          upstream = await fetch(`${baseUrl}${responsesPath}`, {
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

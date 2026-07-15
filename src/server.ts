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

const MAX_REQUEST_BYTES = 10 * 1024 * 1024;
const RETRYABLE_STATUS = new Set([429, 500, 502, 503, 504]);
function landingPage(userAgent = ""): string {
  const isAndroid = /android/i.test(userAgent);
  const isWindows = /windows/i.test(userAgent);
  const recommended = isAndroid
    ? {
        label: "下载 Android 版",
        href: "https://oss.520skx.com/latest/LuodongChat.apk",
        meta: "v1.4 · Android 10+ · 约 2.4 MB",
        file: "LuodongChat.apk",
        icon: "◆",
      }
    : isWindows
      ? {
          label: "下载 Windows 安装版",
          href: "https://oss.520skx.com/latest/LuodongChat-Setup.exe",
          meta: "v1.4 · Windows 10/11 · 64 位 · 约 58 MB",
          file: "LuodongChat-Setup.exe",
          icon: "⊞",
        }
      : {
          label: "查看可用版本",
          href: "#downloads",
          meta: "目前支持 Windows 10/11 与 Android 10+",
          file: "",
          icon: "↓",
        };
  const recommendation = isAndroid || isWindows ? "适合当前设备" : "选择对应平台";
  const downloadAttributes = recommended.file ? ` data-download data-file="${recommended.file}"` : "";

  return `<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
  <meta name="description" content="泺栋 Chat 是轻量、安全的独立 GPT 对话客户端。邮箱登录，无需 API Key，对话记录仅保存在本机。">
  <title>泺栋 Chat - 轻量、安全的独立 GPT 对话客户端</title>
  <style>
    :root{color-scheme:light dark;font-family:Inter,ui-sans-serif,system-ui,-apple-system,"Segoe UI",sans-serif;-webkit-text-size-adjust:100%;--ink:#111827;--muted:#667085;--line:#e2e8f0;--surface:#fff;--soft:#f1f4f8;--brand:#142033;--accent:#12b886}
    *{box-sizing:border-box}
    html{scroll-behavior:smooth}
    body{margin:0;min-height:100vh;min-height:100dvh;padding:max(20px,env(safe-area-inset-top)) max(18px,env(safe-area-inset-right)) max(20px,env(safe-area-inset-bottom)) max(18px,env(safe-area-inset-left));background:#f5f7fa;color:var(--ink)}
    main{width:min(980px,100%);margin:0 auto;padding:30px 36px 24px;border:1px solid var(--line);border-radius:28px;background:var(--surface);box-shadow:0 12px 36px #1420330a}
    .topbar{display:flex;align-items:center;justify-content:space-between;gap:20px}.brand{display:flex;align-items:center;gap:12px}.brand-mark{display:grid;place-items:center;width:42px;height:42px;border-radius:14px;background:var(--brand);color:#6ee7dc;font:800 17px/1 ui-monospace,SFMono-Regular,Menlo,monospace;letter-spacing:-2px}.brand-name{font-size:19px;font-weight:760;letter-spacing:-.02em}.status{display:flex;align-items:center;gap:8px;padding:7px 11px;border:1px solid #b7eadc;border-radius:999px;background:#effcf7;color:#08785d;text-decoration:none;font-size:13px;font-weight:650;white-space:nowrap}.status:hover{border-color:#7ed7c3}.dot{width:8px;height:8px;border-radius:50%;background:var(--accent);box-shadow:0 0 0 4px #12b88618}
    .hero{max-width:700px;padding:30px 0 22px}h1{margin:0;font-size:clamp(40px,4vw,52px);line-height:1.08;letter-spacing:-.03em}.lead{margin:14px 0 0;max-width:680px;color:var(--muted);font-size:16px;line-height:1.65}
    .primary-area{display:grid;grid-template-columns:minmax(0,1.9fr) minmax(170px,.8fr);align-items:center;gap:18px;padding:16px;border:1px solid var(--line);border-radius:20px;background:var(--soft)}.primary-download{display:flex;align-items:center;gap:14px;min-height:72px;padding:12px 17px;border-radius:14px;background:var(--brand);box-shadow:0 7px 18px #1420331c;color:#fff;text-decoration:none;transition:transform .2s ease,box-shadow .2s ease,filter .2s ease;touch-action:manipulation}.primary-download:hover{transform:translateY(-2px);box-shadow:0 11px 24px #14203326;filter:brightness(1.06)}.primary-download:active{transform:scale(.99)}.platform-icon{display:grid;place-items:center;flex:0 0 auto;width:38px;height:38px;border-radius:10px;background:#ffffff15;color:#7fe1d6;font-size:23px}.download-copy{display:flex;min-width:0;flex:1;flex-direction:column;gap:5px}.download-copy strong{font-size:16px}.download-copy small{color:#cbd5e1;font-size:12px;line-height:1.35}.arrow{flex:0 0 auto;font-size:22px}.recommendation{padding:4px 8px}.recommendation span{display:block;color:var(--muted);font-size:12px}.recommendation strong{display:block;margin-top:5px;font-size:14px}.download-feedback{min-height:17px;margin-top:5px;color:#08785d!important}
    .trust{display:grid;grid-template-columns:repeat(3,1fr);gap:18px;margin:13px 0 3px;padding:10px 4px}.trust-item{display:flex;align-items:flex-start;gap:9px;min-width:0;color:var(--ink);text-decoration:none}.trust-icon{flex:0 0 auto;color:var(--accent);font-size:15px;font-weight:800;line-height:1.4}.trust-copy strong{display:block;font-size:13px}.trust-copy small{display:block;margin-top:3px;color:var(--muted);font-size:11px;line-height:1.35}.trust-item[href]:hover strong{text-decoration:underline}
    details{margin-top:4px;border-top:1px solid var(--line);border-bottom:1px solid var(--line)}summary{display:flex;align-items:center;justify-content:space-between;gap:18px;padding:14px 6px;cursor:pointer;list-style:none;touch-action:manipulation}summary::-webkit-details-marker{display:none}.summary-copy strong{display:block;font-size:14px}.summary-copy small{display:block;margin-top:4px;color:var(--muted);font-size:11px}.chevron{color:#788397;font-size:20px;transition:transform .2s ease}details[open] .chevron{transform:rotate(180deg)}.download-list{display:grid;grid-template-columns:repeat(4,1fr);gap:10px;padding:2px 0 16px;animation:reveal .2s ease}.download-option{display:flex;min-width:0;flex-direction:column;gap:6px;padding:13px;border:1px solid var(--line);border-radius:14px;background:var(--surface);color:var(--ink);text-decoration:none;transition:transform .2s ease,border-color .2s ease,background .2s ease}.download-option:hover{transform:translateY(-1px);border-color:#a9b5c5;background:#fafcfd}.download-option strong{font-size:13px}.download-option span{color:var(--muted);font-size:11px;line-height:1.45}@keyframes reveal{from{opacity:0;transform:translateY(-5px)}to{opacity:1;transform:none}}
    .privacy-note{display:flex;align-items:flex-start;gap:11px;margin-top:13px;padding:12px 16px;border-radius:14px;background:#f0faf8;color:#315d56}.privacy-note strong{display:block;color:#174e45;font-size:13px}.privacy-note p{margin:3px 0 0;font-size:12px;line-height:1.5}.privacy-note a{display:inline-block;margin-top:4px;color:#08785d;font-size:12px;font-weight:650;text-decoration:none}.privacy-note a:hover{text-decoration:underline}.shield{font-size:18px;line-height:1.4}.footer{display:flex;align-items:center;justify-content:space-between;gap:18px;margin-top:16px;color:#7a8494;font-size:12px}.footer nav{display:flex;flex-wrap:wrap;gap:7px 17px}.footer a{color:var(--muted);text-decoration:none}.footer a:hover{text-decoration:underline}.version-date{white-space:nowrap}
    a:focus-visible,summary:focus-visible{outline:3px solid #5ab9b0;outline-offset:3px}
    @media(max-width:680px){body{padding:max(10px,env(safe-area-inset-top)) max(8px,env(safe-area-inset-right)) max(10px,env(safe-area-inset-bottom)) max(8px,env(safe-area-inset-left));background:var(--surface)}main{padding:22px 18px 20px;border-radius:20px;box-shadow:none}.brand-mark{width:40px;height:40px;border-radius:12px}.brand-name{font-size:17px}.status{padding:6px 9px;font-size:12px}.hero{padding:30px 0 22px}h1{font-size:34px}.lead{font-size:15px}.primary-area{grid-template-columns:1fr;padding:13px}.primary-download{min-height:70px}.recommendation{padding:0 3px}.trust{grid-template-columns:1fr;gap:11px;padding-block:12px}.download-list{grid-template-columns:1fr 1fr}.privacy-note{padding:12px 13px}.footer{align-items:flex-start;flex-direction:column}.version-date{white-space:normal}}
    @media(max-width:420px){main{padding-inline:15px}.topbar{align-items:flex-start;gap:10px}.brand{gap:9px}.status{margin-top:3px}.status .status-text{display:none}h1{font-size:31px}.download-list{grid-template-columns:1fr}.summary-copy small{max-width:250px}}
    @media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}.primary-download,.download-option,.chevron,.download-list{transition:none;animation:none}}
    @media(prefers-color-scheme:dark){:root{--ink:#f3f6fa;--muted:#9ba6b6;--line:#2a3547;--surface:#111827;--soft:#182235;--brand:#e9eef5}body{background:#0b1020}main{box-shadow:none}.brand-mark{background:#253147}.status{border-color:#175e50;background:#102b27;color:#78ddc7}.primary-download{color:#111827}.platform-icon{background:#14203312;color:#08785d}.download-copy small{color:#475569}.download-option:hover{border-color:#53627a;background:#182235}.privacy-note{background:#102925;color:#9fd6cc}.privacy-note strong{color:#d4f3ed}.privacy-note a{color:#78ddc7}}
  </style>
</head>
<body><main>
  <header class="topbar"><div class="brand"><div class="brand-mark" aria-hidden="true">›_</div><div class="brand-name">泺栋 Chat</div></div><a class="status" href="/healthz" title="查看服务状态"><span class="dot"></span><span class="status-text">服务正常</span></a></header>
  <section class="hero"><h1>轻量、独立的<br>GPT 对话客户端</h1><p class="lead">使用邮箱账号直接登录，无需安装官方客户端，也无需配置 API Key。打开软件，即可开始对话。</p></section>
  <section class="primary-area" aria-label="推荐下载"><a class="primary-download" href="${recommended.href}"${downloadAttributes}><span class="platform-icon" aria-hidden="true">${recommended.icon}</span><span class="download-copy"><strong>${recommended.label}</strong><small>${recommended.meta}${recommended.file ? ` · ${recommended.file.endsWith(".apk") ? ".apk" : ".exe"}` : ""}</small></span><span class="arrow" aria-hidden="true">→</span></a><div class="recommendation"><span>推荐版本</span><strong>${recommendation}</strong><span class="download-feedback" aria-live="polite"></span></div></section>
  <div class="trust" aria-label="产品特性"><div class="trust-item"><span class="trust-icon">✓</span><span class="trust-copy"><strong>本地存储</strong><small>对话仅保存在设备中</small></span></div><a class="trust-item" href="https://oss.520skx.com/latest/LuodongChat-Setup.exe.sha256"><span class="trust-icon">✓</span><span class="trust-copy"><strong>文件安全校验 →</strong><small>查看 Windows SHA-256</small></span></a><div class="trust-item"><span class="trust-icon">↻</span><span class="trust-copy"><strong>自动更新</strong><small>自动获取稳定版本</small></span></div></div>
  <details id="downloads"><summary><span class="summary-copy"><strong>其他下载方式</strong><small>Windows 便携版、Android APK 与历史版本</small></span><span class="chevron" aria-hidden="true">⌄</span></summary><div class="download-list"><a class="download-option" data-download data-file="LuodongChat-Setup.exe" href="https://oss.520skx.com/latest/LuodongChat-Setup.exe"><strong>Windows 安装版 →</strong><span>快捷方式与自动更新 · 约 58 MB</span></a><a class="download-option" data-download data-file="LuodongChat-portable.zip" href="https://oss.520skx.com/latest/LuodongChat-portable.zip"><strong>Windows 便携版 →</strong><span>解压即用，数据存放本地 · 约 59 MB</span></a><a class="download-option" data-download data-file="LuodongChat.apk" href="https://oss.520skx.com/latest/LuodongChat.apk"><strong>Android APK →</strong><span>Android 10 及以上 · 约 2.4 MB</span></a><a class="download-option" href="https://github.com/SunKeXu01/LuodongChat/releases/latest"><strong>历史版本 →</strong><span>版本记录与全部校验文件</span></a></div></details>
  <aside class="privacy-note"><span class="shield" aria-hidden="true">🛡</span><div><strong>隐私与账号安全</strong><p>对话记录仅保存在你的设备中，不上传至泺栋 Chat 服务器。</p><a href="https://github.com/SunKeXu01/LuodongChat/blob/main/docs/DATA_MODEL.md">了解登录与数据处理方式 →</a></div></aside>
  <footer class="footer"><nav><a href="https://github.com/SunKeXu01/LuodongChat/blob/main/docs/DATA_MODEL.md">隐私说明</a><a href="https://github.com/SunKeXu01/LuodongChat#Windows-使用方式">安装帮助</a><a href="https://github.com/SunKeXu01/LuodongChat/releases/latest">更新记录</a><a href="https://github.com/SunKeXu01/LuodongChat">GitHub</a><a href="https://github.com/SunKeXu01/LuodongChat/issues">联系我们</a></nav><span class="version-date">v1.4 · 2026-07-15</span></footer>
</main><script src="/assets/landing.js" defer></script></body>
</html>`;
}

const LANDING_JS = `document.querySelectorAll("[data-download]").forEach((link)=>link.addEventListener("click",()=>{const feedback=document.querySelector(".download-feedback");if(!feedback)return;const file=link.dataset.file||"安装包";feedback.textContent="正在开始下载："+file;window.setTimeout(()=>{feedback.textContent=""},5000)}));`;

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

function requestsWebSearch(body: Buffer): boolean {
  try {
    const value: unknown = JSON.parse(body.toString("utf8"));
    if (!value || typeof value !== "object" || Array.isArray(value)) return false;
    const tools = (value as Record<string, unknown>).tools;
    return Array.isArray(tools) && tools.some((tool) =>
      tool && typeof tool === "object" && (tool as Record<string, unknown>).type === "web_search");
  } catch { return false; }
}

function requestsImageGeneration(body: Buffer): boolean {
  try {
    const value: unknown = JSON.parse(body.toString("utf8"));
    if (!value || typeof value !== "object" || Array.isArray(value)) return false;
    const tools = (value as Record<string, unknown>).tools;
    return Array.isArray(tools) && tools.some((tool) =>
      tool && typeof tool === "object" && (tool as Record<string, unknown>).type === "image_generation");
  } catch { return false; }
}

function latestUserPrompt(body: Buffer): string {
  const value: unknown = JSON.parse(body.toString("utf8"));
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("invalid_image_request");
  const input = (value as Record<string, unknown>).input;
  if (typeof input === "string" && input.trim()) return input.trim();
  if (!Array.isArray(input)) throw new Error("invalid_image_request");
  for (let index = input.length - 1; index >= 0; index -= 1) {
    const item = input[index];
    if (!item || typeof item !== "object" || (item as Record<string, unknown>).role !== "user") continue;
    const content = (item as Record<string, unknown>).content;
    if (typeof content === "string" && content.trim()) return content.trim();
  }
  throw new Error("invalid_image_request");
}

async function generateImage(
  config: NonNullable<GatewayConfig["imageGeneration"]>, prompt: string, timeoutMs: number,
): Promise<string> {
  const response = await fetch(`${config.baseUrl}/images/generations`, {
    method: "POST",
    headers: { authorization: `Bearer ${config.apiKey}`, "content-type": "application/json" },
    body: JSON.stringify({ model: config.model, prompt, size: "1024x1024" }),
    signal: AbortSignal.timeout(timeoutMs),
  });
  if (!response.ok) { await response.body?.cancel(); throw new Error(`image_api_http_${response.status}`); }
  const payload = await response.json() as { data?: { b64_json?: string }[] };
  const item = payload.data?.[0];
  if (item?.b64_json) return item.b64_json;
  throw new Error("image_api_empty_result");
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
}

export function createGatewayServer(config: GatewayConfig, options: GatewayServerOptions = {}) {
  const limiter = options.limiter ?? new InMemoryLimiter(config.requestsPerMinute, config.maxConcurrentRequests);
  const configuredUpstreams = config.upstreams ?? config.upstreamApiKeys;
  const webSearchUpstreamIndex = config.webSearchUpstreamIndex ?? Math.max(0, configuredUpstreams.length - 1);
  const imageGenerationUpstreamIndex = config.imageGenerationUpstreamIndex ?? Math.max(0, configuredUpstreams.length - 1);
  const upstreamPool = new UpstreamPool(configuredUpstreams.map((item, index) =>
    typeof item === "string"
      ? { apiKey: item, supportsWebSearch: index === webSearchUpstreamIndex, supportsImageGeneration: index === imageGenerationUpstreamIndex }
      : { ...item, supportsWebSearch: index === webSearchUpstreamIndex, supportsImageGeneration: index === imageGenerationUpstreamIndex }));
  const ledger = options.ledger ?? new InMemoryRequestLedger();
  const keyVerifier = options.keyVerifier ?? new StaticGatewayKeyVerifier(config.gatewayKeyHashes);
  const adminLoginGuard = options.adminLoginProtector ?? new AdminLoginGuard();

  return createServer(async (req, res) => {
    const requestId = req.headers["x-request-id"]?.toString() || randomUUID();
    res.setHeader("x-request-id", requestId);

    if (req.method === "GET" && req.url === "/healthz") {
      return json(res, 200, { status: "ok", version: config.version ?? "unknown" });
    }
    if (req.method === "GET" && req.url === "/assets/landing.js") {
      res.writeHead(200, {
        "cache-control": "public, max-age=3600",
        "content-type": "application/javascript; charset=utf-8",
        "x-content-type-options": "nosniff",
      });
      return res.end(LANDING_JS);
    }
    if (req.method === "GET" && req.url === "/") {
      res.writeHead(200, {
        "content-type": "text/html; charset=utf-8",
        "content-security-policy": "default-src 'none'; style-src 'unsafe-inline'; script-src 'self'; base-uri 'none'; frame-ancestors 'none'",
        "referrer-policy": "no-referrer",
        "vary": "User-Agent",
        "x-content-type-options": "nosniff",
        "x-frame-options": "DENY",
      });
      return res.end(landingPage(req.headers["user-agent"]?.toString()));
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
    const clientAsset = req.method === "GET" && req.url === "/client/download/LuodongChat-Setup.exe" ? "LuodongChat-Setup.exe"
      : req.method === "GET" && req.url === "/client/download/LuodongChat-Setup.exe.sha256" ? "LuodongChat-Setup.exe.sha256"
      : req.method === "GET" && req.url === "/client/download/LuodongChat.exe" ? "LuodongChat.exe"
      : req.method === "GET" && req.url === "/client/download/LuodongChat.exe.sha256" ? "LuodongChat.exe.sha256"
      : req.method === "GET" && req.url === "/client/download/LuodongChat.apk" ? "LuodongChat.apk"
      : req.method === "GET" && req.url === "/client/download/LuodongChat.apk.sha256" ? "LuodongChat.apk.sha256"
      : req.method === "GET" && req.url === "/client/download/ChatGPTConnector.exe" ? "LuodongChat.exe"
      : req.method === "GET" && req.url === "/client/download/ChatGPTConnector.exe.sha256" ? "LuodongChat.exe.sha256"
      : req.method === "GET" && req.url === "/client/download/ChatGPTConnector.apk" ? "LuodongChat.apk"
      : req.method === "GET" && req.url === "/client/download/ChatGPTConnector.apk.sha256" ? "LuodongChat.apk.sha256" : null;
    if (clientAsset) {
      const downloadBaseUrl = process.env.CLIENT_DOWNLOAD_BASE_URL?.trim();
      if (downloadBaseUrl) {
        res.writeHead(302, {
          location: `${downloadBaseUrl.replace(/\/$/, "")}/${clientAsset}`,
          "cache-control": "public, max-age=300",
          "x-content-type-options": "nosniff",
        });
        return res.end();
      }
    }
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
    if (req.url?.startsWith("/sync/") && options.enrollmentService) {
      const accessToken = extractBearerKey(req.headers.authorization);
      const account = accessToken?.startsWith("usr_") ? await options.enrollmentService.authenticate(accessToken) : null;
      if (!account) return json(res, 401, { error: { code: "login_required", message: "请先登录" } });
      res.setHeader("cache-control", "no-store");
      if (req.method === "GET" && req.url.startsWith("/sync/state")) {
        return json(res, 200, { conversations: [], messages: [], serverTime: new Date().toISOString(), storage: "local_only" });
      }
      if (req.method === "POST" && req.url === "/sync/conversations") {
        const input = await readJsonObject(req);
        const id = typeof input.id === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(input.id) ? input.id : null;
        const title = typeof input.title === "string" ? input.title.trim() : "";
        if (!id || title.length < 1 || title.length > 120) return json(res, 400, { error: { code: "invalid_conversation" } });
        const now = new Date().toISOString();
        return json(res, 200, { id, title, createdAt: now, updatedAt: now, deletedAt: null, storage: "local_only" });
      }
      const deleteMatch = /^\/sync\/conversations\/([0-9a-f-]{36})$/.exec(req.url);
      if (req.method === "DELETE" && deleteMatch) {
        return json(res, 200, { status: "not_stored" });
      }
      if (req.method === "POST" && req.url === "/sync/messages") {
        const input = await readJsonObject(req);
        const uuid = (value: unknown) => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value) ? value : null;
        const id = uuid(input.id); const conversationId = uuid(input.conversationId);
        const role = typeof input.role === "string" && ["user", "assistant", "system"].includes(input.role) ? input.role : null;
        const content = typeof input.content === "string" ? input.content.trim() : "";
        const clientCreatedAt = typeof input.clientCreatedAt === "string" ? new Date(input.clientCreatedAt) : new Date();
        if (!id || !conversationId || !role || content.length < 1 || content.length > 32_000 || Number.isNaN(clientCreatedAt.getTime())) {
          return json(res, 400, { error: { code: "invalid_message" } });
        }
        return json(res, 200, { id, conversationId, role, content, clientCreatedAt: clientCreatedAt.toISOString(), storage: "local_only" });
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
      const requiresWebSearch = requestsWebSearch(body);
      const requiresImageGeneration = requestsImageGeneration(body);
      if (requiresImageGeneration && config.imageGeneration) {
        attempts = 1;
        const image = await generateImage(config.imageGeneration, latestUserPrompt(body), config.upstreamTimeoutMs);
        const event = JSON.stringify({
          type: "response.completed",
          response: { output: [{ type: "image_generation_call", result: image }] },
        });
        res.writeHead(200, { "content-type": "text/event-stream; charset=utf-8", "cache-control": "no-cache" });
        res.end(`data: ${event}\n\ndata: [DONE]\n\n`);
        await ledger.finishRequest({ requestId, status: "completed", endedAt: new Date(), statusCode: 200, attempts });
        return;
      }
      let upstream: Response | undefined;
      let selectedCredentialId: string | undefined;
      const attemptedCredentialIds = new Set<string>();
      for (let attempt = 1; attempt <= 2; attempt += 1) {
        let credential = upstreamPool.acquire(attemptedCredentialIds, Date.now(), requiresWebSearch, requiresImageGeneration);
        if (!credential && attemptedCredentialIds.size > 0) credential = upstreamPool.acquire(new Set(), Date.now(), requiresWebSearch, requiresImageGeneration);
        if (!credential)
        {
          if ((requiresWebSearch || requiresImageGeneration) && attempts === 0)
          {
            const code = requiresImageGeneration ? "image_generation_unavailable" : "web_search_unavailable";
            await ledger.finishRequest({ requestId, status: "failed", endedAt: new Date(), attempts: 0, errorClass: code });
            return json(res, 422, { error: { code, message: requiresImageGeneration ? "Image generation is temporarily unavailable" : "Web search is temporarily unavailable" } });
          }
          break;
        }
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
      if (requiresWebSearch && (upstream.status === 401 || upstream.status === 403 || RETRYABLE_STATUS.has(upstream.status)))
      {
        await upstream.body?.cancel();
        await ledger.finishRequest({
          requestId,
          status: "failed",
          endedAt: new Date(),
          statusCode: upstream.status,
          attempts,
          errorClass: "web_search_unavailable",
        });
        return json(res, 422, { error: { code: "web_search_unavailable", message: "Web search is temporarily unavailable" } });
      }
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

import assert from "node:assert/strict";
import { createServer } from "node:http";
import test from "node:test";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { hashGatewayKey } from "../src/auth.js";
import type { GatewayConfig } from "../src/config.js";
import { createGatewayServer } from "../src/server.js";
import { InMemoryRequestLedger } from "../src/ledger.js";
import type { EnrollmentService } from "../src/self-service.js";
import { AttachmentStore } from "../src/attachments.js";

async function listen(server: ReturnType<typeof createServer>): Promise<number> {
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("missing server address");
  return address.port;
}

test("serves the public client update manifest and fixed release assets", async (t) => {
  const directory = await mkdtemp(join(tmpdir(), "connector-release-"));
  const previous = process.env.CLIENT_RELEASE_ROOT;
  const previousDownloadBase = process.env.CLIENT_DOWNLOAD_BASE_URL;
  process.env.CLIENT_RELEASE_ROOT = directory;
  delete process.env.CLIENT_DOWNLOAD_BASE_URL;
  await writeFile(join(directory, "update.json"), '{"version":"0.1.0-preview.2"}');
  await writeFile(join(directory, "ChatGPTConnector.exe.sha256"), "abc123\n");
  await writeFile(join(directory, "ChatGPTConnector.apk"), "apk-bytes");
  await writeFile(join(directory, "LuodongChat.exe.sha256"), "def456\n");
  await writeFile(join(directory, "LuodongChat-Setup.exe"), "setup-bytes");
  await writeFile(join(directory, "LuodongChat-Setup.exe.sha256"), "setup456\n");
  await writeFile(join(directory, "LuodongChat.apk"), "luodong-apk");
  t.after(async () => {
    if (previous === undefined) delete process.env.CLIENT_RELEASE_ROOT; else process.env.CLIENT_RELEASE_ROOT = previous;
    if (previousDownloadBase === undefined) delete process.env.CLIENT_DOWNLOAD_BASE_URL; else process.env.CLIENT_DOWNLOAD_BASE_URL = previousDownloadBase;
    await rm(directory, { recursive: true, force: true });
  });
  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: "https://upstream.invalid", upstreamApiKey: "unused", upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set([hashGatewayKey("gw_test")]),
    requestsPerMinute: 10, maxConcurrentRequests: 2, upstreamTimeoutMs: 5_000,
  };
  const gateway = createGatewayServer(config);
  const port = await listen(gateway);
  t.after(() => gateway.close());
  const manifest = await fetch(`http://127.0.0.1:${port}/client/update.json`);
  assert.equal(manifest.status, 200);
  assert.equal(manifest.headers.get("cache-control"), "no-cache");
  assert.deepEqual(await manifest.json(), { version: "0.1.0-preview.2" });
  const checksum = await fetch(`http://127.0.0.1:${port}/client/download/ChatGPTConnector.exe.sha256`);
  assert.equal(await checksum.text(), "def456\n");
  const apk = await fetch(`http://127.0.0.1:${port}/client/download/ChatGPTConnector.apk`);
  assert.equal(apk.headers.get("content-type"), "application/vnd.android.package-archive");
  assert.equal(await apk.text(), "luodong-apk");
  const currentApk = await fetch(`http://127.0.0.1:${port}/client/download/LuodongChat.apk`);
  assert.equal(currentApk.headers.get("content-type"), "application/vnd.android.package-archive");
  assert.equal(await currentApk.text(), "luodong-apk");
  const setup = await fetch(`http://127.0.0.1:${port}/client/download/LuodongChat-Setup.exe`);
  assert.equal(setup.headers.get("content-type"), "application/vnd.microsoft.portable-executable");
  assert.equal(await setup.text(), "setup-bytes");
});

test("redirects stable and legacy client downloads to OSS when configured", async (t) => {
  const previous = process.env.CLIENT_DOWNLOAD_BASE_URL;
  process.env.CLIENT_DOWNLOAD_BASE_URL = "https://downloads.example/latest/";
  t.after(() => {
    if (previous === undefined) delete process.env.CLIENT_DOWNLOAD_BASE_URL; else process.env.CLIENT_DOWNLOAD_BASE_URL = previous;
  });
  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: "https://upstream.invalid", upstreamApiKey: "unused", upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set(), requestsPerMinute: 10,
    maxConcurrentRequests: 2, upstreamTimeoutMs: 5_000,
  };
  const gateway = createGatewayServer(config);
  const port = await listen(gateway);
  t.after(() => gateway.close());
  for (const path of ["LuodongChat-Setup.exe", "ChatGPTConnector.apk"]) {
    const response = await fetch(`http://127.0.0.1:${port}/client/download/${path}`, { redirect: "manual" });
    assert.equal(response.status, 302);
    assert.equal(response.headers.get("location"), path === "ChatGPTConnector.apk"
      ? "https://downloads.example/latest/LuodongChat.apk"
      : `https://downloads.example/latest/${path}`);
  }
});

test("rejects malformed account email before requesting delivery", async (t) => {
  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: "https://upstream.invalid", upstreamApiKey: "unused", upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set(), requestsPerMinute: 10,
    maxConcurrentRequests: 2, upstreamTimeoutMs: 5_000,
  };
  let deliveries = 0;
  const enrollmentService = { requestCode: async () => { deliveries += 1; return "sent"; } } as unknown as EnrollmentService;
  const gateway = createGatewayServer(config, { enrollmentService });
  const port = await listen(gateway);
  t.after(() => gateway.close());
  const response = await fetch(`http://127.0.0.1:${port}/account/code`, {
    method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ email: "user@example" }),
  });
  assert.equal(response.status, 400);
  assert.equal(deliveries, 0);
});

test("returns a clear conflict when registering an existing account", async (t) => {
  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: "https://upstream.invalid", upstreamApiKey: "unused", upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set(), requestsPerMinute: 10,
    maxConcurrentRequests: 2, upstreamTimeoutMs: 5_000,
  };
  const enrollmentService = {
    registerWithPassword: async () => ({ status: "already_registered" }),
  } as unknown as EnrollmentService;
  const gateway = createGatewayServer(config, { enrollmentService });
  const port = await listen(gateway);
  t.after(() => gateway.close());
  const response = await fetch(`http://127.0.0.1:${port}/account/register`, {
    method: "POST", headers: { "content-type": "application/json" },
    body: JSON.stringify({ email: "user@example.com", password: "Secure123", code: "123456" }),
  });
  assert.equal(response.status, 409);
  assert.equal((await response.json() as { error: { code: string } }).error.code, "account_already_registered");
});

test("authenticates and proxies a Responses request", async (t) => {
  const upstream = createServer(async (req, res) => {
    assert.equal(req.url, "/responses");
    assert.equal(req.headers.authorization, "Bearer upstream-secret");
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(Buffer.from(chunk));
    assert.deepEqual(JSON.parse(Buffer.concat(chunks).toString()), { model: "test-model", input: "hello" });
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ id: "resp_test", status: "completed" }));
  });
  const upstreamPort = await listen(upstream);
  t.after(() => upstream.close());

  const config: GatewayConfig = {
    port: 0,
    upstreamBaseUrl: `http://127.0.0.1:${upstreamPort}`,
    upstreamApiKey: "upstream-secret",
    upstreamApiKeys: ["upstream-secret"],
    upstreamResponsesPath: "/responses",
    gatewayKeyHashes: new Set([hashGatewayKey("gw_test_secret")]),
    requestsPerMinute: 10,
    maxConcurrentRequests: 2,
    upstreamTimeoutMs: 5_000,
  };
  const ledger = new InMemoryRequestLedger();
  const gateway = createGatewayServer(config, { ledger });
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());

  const unauthorized = await fetch(`http://127.0.0.1:${gatewayPort}/responses`, { method: "POST" });
  assert.equal(unauthorized.status, 401);

  const response = await fetch(`http://127.0.0.1:${gatewayPort}/responses`, {
    method: "POST",
    headers: { authorization: "Bearer gw_test_secret", "content-type": "application/json" },
    body: JSON.stringify({ model: "test-model", input: "hello" }),
  });
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { id: "resp_test", status: "completed" });
  assert.match(response.headers.get("x-request-id") ?? "", /^[0-9a-f-]{36}$/);
  assert.equal(ledger.requests.size, 1);
  assert.equal(ledger.attempts.length, 1);
  const recorded = [...ledger.requests.values()][0];
  assert.equal(recorded?.status, "completed");
  assert.equal(recorded?.attempts, 1);
  assert.equal(JSON.stringify({ requests: [...ledger.requests.values()], attempts: ledger.attempts }).includes("hello"), false);
});

test("uploads an account attachment and injects it into the latest user message", async (t) => {
  const directory = await mkdtemp(join(tmpdir(), "connector-attachments-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const upstream = createServer(async (req, res) => {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(Buffer.from(chunk));
    const body = JSON.parse(Buffer.concat(chunks).toString()) as {
      attachment_ids?: unknown; input: { role: string; content: { type: string; filename?: string; file_data?: string; text?: string }[] }[];
    };
    assert.equal(body.attachment_ids, undefined);
    const content = body.input.at(-1)?.content ?? [];
    assert.equal(content[0]?.type, "input_text");
    assert.equal(content[1]?.type, "input_file");
    assert.equal(content[1]?.filename, "notes.pdf");
    assert.match(content[1]?.file_data ?? "", /^data:application\/pdf;base64,/);
    res.writeHead(200, { "content-type": "application/json" });
    res.end('{"status":"completed"}');
  });
  const upstreamPort = await listen(upstream);
  t.after(() => upstream.close());
  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: `http://127.0.0.1:${upstreamPort}`, upstreamApiKey: "upstream-secret", upstreamApiKeys: ["upstream-secret"],
    upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set(), requestsPerMinute: 10, maxConcurrentRequests: 2, upstreamTimeoutMs: 5_000,
  };
  const enrollmentService = { authenticate: async (token: string) => token === "usr_test" ? { id: "account-1" } : null, getRequestLimits: () => ({ requestsPerMinute: 10, maxConcurrentRequests: 2, dailyLimit: null }) } as unknown as EnrollmentService;
  const gateway = createGatewayServer(config, { enrollmentService, attachmentStore: new AttachmentStore(directory) });
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());

  const form = new FormData();
  form.append("file", new Blob([Buffer.from("%PDF-1.4\nattachment marker")], { type: "application/pdf" }), "notes.pdf");
  const upload = await fetch(`http://127.0.0.1:${gatewayPort}/v1/attachments`, { method: "POST", headers: { authorization: "Bearer usr_test" }, body: form });
  assert.equal(upload.status, 201);
  const uploaded = await upload.json() as { id: string };
  assert.match(uploaded.id, /^att_[a-f\d]{32}$/);

  const response = await fetch(`http://127.0.0.1:${gatewayPort}/v1/responses`, {
    method: "POST", headers: { authorization: "Bearer usr_test", "content-type": "application/json" },
    body: JSON.stringify({ model: "gpt-5.6", input: [{ role: "user", content: "请总结附件" }], attachment_ids: [uploaded.id] }),
  });
  assert.equal(response.status, 200);
});

test("rejects unsupported and signature-mismatched attachments", async (t) => {
  const directory = await mkdtemp(join(tmpdir(), "connector-attachments-invalid-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: "https://upstream.invalid", upstreamApiKey: "unused", upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set(), requestsPerMinute: 10, maxConcurrentRequests: 2, upstreamTimeoutMs: 5_000,
  };
  const enrollmentService = { authenticate: async () => ({ id: "account-1" }) } as unknown as EnrollmentService;
  const gateway = createGatewayServer(config, { enrollmentService, attachmentStore: new AttachmentStore(directory) });
  const port = await listen(gateway);
  t.after(() => gateway.close());
  for (const [name, type, data, expected] of [
    ["malware.exe", "application/octet-stream", "MZ", "attachment_type_not_allowed"],
    ["fake.png", "image/png", "not a png", "attachment_signature_mismatch"],
  ] satisfies [string, string, string, string][]) {
    const form = new FormData();
    form.append("file", new Blob([data], { type }), name);
    const response = await fetch(`http://127.0.0.1:${port}/v1/attachments`, { method: "POST", headers: { authorization: "Bearer usr_test" }, body: form });
    assert.equal(response.status, 400);
    assert.equal((await response.json() as { error: { code: string } }).error.code, expected);
  }
});

test("uses an authenticated account session without exposing a gateway key", async (t) => {
  const upstream = createServer(async (req, res) => {
    for await (const _chunk of req) { /* consume request */ }
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ id: "resp_account", status: "completed" }));
  });
  const upstreamPort = await listen(upstream);
  t.after(() => upstream.close());
  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: `http://127.0.0.1:${upstreamPort}`, upstreamApiKey: "upstream-secret",
    upstreamApiKeys: ["upstream-secret"], upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set(),
    requestsPerMinute: 10, maxConcurrentRequests: 2, upstreamTimeoutMs: 5_000,
  };
  const profile = { id: "00000000-0000-4000-8000-000000000002", email: "user@example.com", nickname: "user", avatarMediaType: null, avatarBase64: null, balanceMicrounits: 0 };
  const enrollmentService = {
    authenticate: async (token: string) => token === "usr_valid" ? profile : null,
    getRequestLimits: () => ({ dailyLimit: null, requestsPerMinute: 10, maxConcurrentRequests: 2, expiresInDays: null }),
  } as unknown as EnrollmentService;
  const ledger = new InMemoryRequestLedger();
  const gateway = createGatewayServer(config, { enrollmentService, ledger });
  const port = await listen(gateway);
  t.after(() => gateway.close());

  const expired = await fetch(`http://127.0.0.1:${port}/responses`, { method: "POST", headers: { authorization: "Bearer usr_expired" } });
  assert.equal(expired.status, 401);
  const response = await fetch(`http://127.0.0.1:${port}/responses`, {
    method: "POST", headers: { authorization: "Bearer usr_valid", "content-type": "application/json" },
    body: JSON.stringify({ model: "gpt-5.6-sol", input: "hello" }),
  });
  assert.equal(response.status, 200);
  const recorded = [...ledger.requests.values()][0];
  assert.equal(recorded?.userId, profile.id);
  assert.equal(recorded?.gatewayKeyHash, undefined);
});

test("keeps legacy sync clients compatible without storing conversations", async (t) => {
  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: "http://127.0.0.1:1", upstreamApiKey: "unused", upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set(), requestsPerMinute: 10,
    maxConcurrentRequests: 2, upstreamTimeoutMs: 100,
  };
  const enrollmentService = {
    authenticate: async (token: string) => token === "usr_valid" ? { id: "00000000-0000-4000-8000-000000000003", email: "local@example.com", nickname: "local", avatarMediaType: null, avatarBase64: null, balanceMicrounits: 0 } : null,
  } as unknown as EnrollmentService;
  const gateway = createGatewayServer(config, { enrollmentService });
  const port = await listen(gateway);
  t.after(() => gateway.close());
  const unauthorized = await fetch(`http://127.0.0.1:${port}/sync/state`);
  assert.equal(unauthorized.status, 401);
  const response = await fetch(`http://127.0.0.1:${port}/sync/state`, { headers: { authorization: "Bearer usr_valid" } });
  assert.equal(response.status, 200);
  const body = await response.json() as { conversations: unknown[]; messages: unknown[]; storage: string };
  assert.deepEqual(body.conversations, []);
  assert.deepEqual(body.messages, []);
  assert.equal(body.storage, "local_only");
});

test("serves a safe public landing page", async (t) => {
  const config: GatewayConfig = {
    port: 0,
    upstreamBaseUrl: "http://127.0.0.1:1",
    upstreamApiKey: "unused",
    upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses",
    gatewayKeyHashes: new Set([hashGatewayKey("gw_test_secret")]),
    requestsPerMinute: 10,
    maxConcurrentRequests: 2,
    upstreamTimeoutMs: 100,
  };
  const gateway = createGatewayServer(config);
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gatewayPort}/`);
  assert.equal(response.status, 200);
  assert.match(response.headers.get("content-type") ?? "", /^text\/html/);
  assert.equal(response.headers.get("x-frame-options"), "DENY");
  assert.equal(response.headers.get("vary"), "User-Agent");
  const page = await response.text();
  const packageMetadata = JSON.parse(await readFile(join(process.cwd(), "package.json"), "utf8")) as { version: string };
  const releaseVersion = packageMetadata.version.replace(/\.0$/, "");
  assert.match(page, /泺栋 Chat/);
  assert.ok(page.includes(`oss.520skx.com/latest/LuodongChat-${releaseVersion}-win-x64-setup.exe`));
  assert.ok(page.includes(`oss.520skx.com/latest/LuodongChat-${releaseVersion}-win-x64-portable.zip`));
  assert.ok(page.includes(`oss.520skx.com/latest/LuodongChat-${releaseVersion}-win-arm64-setup.exe`));
  assert.match(page, /oss\.520skx\.com\/latest\/LuodongChat\.apk/);
  assert.match(page, /github\.com\/SunKeXu01\/LuodongChat\/releases\/latest/);
  assert.match(page, /viewport-fit=cover/);
  assert.match(page, /选择适合你的版本/);
  assert.match(page, /历史对话不会在泺栋 Chat 服务器持久化/);
  assert.match(page, /自动联网搜索/);
  assert.match(page, /支持图片生成/);
  assert.match(page, /办公、学习与创作的/);
  assert.match(page, /客户端界面预览/);
  assert.match(page, /安装包暂未代码签名/);
  assert.match(page, /Apple 芯片虚拟机及 Windows ARM 设备请选择 ARM64 版/);
  assert.match(page, /\/assets\/landing\.css/);
  assert.match(page, /\/assets\/landing\.js/);

  const landingStyles = await fetch(`http://127.0.0.1:${gatewayPort}/assets/landing.css`);
  assert.equal(landingStyles.status, 200);
  assert.match(landingStyles.headers.get("content-type") ?? "", /^text\/css/);
  assert.match(await landingStyles.text(), /\.product-window/);

  const landingScript = await fetch(`http://127.0.0.1:${gatewayPort}/assets/landing.js`);
  assert.equal(landingScript.status, 200);
  assert.match(landingScript.headers.get("content-type") ?? "", /^application\/javascript/);
  assert.match(await landingScript.text(), /正在开始下载/);

  const windowsResponse = await fetch(`http://127.0.0.1:${gatewayPort}/`, {
    headers: { "user-agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" },
  });
  assert.match(await windowsResponse.text(), /下载 Windows x64 版/);

  const androidResponse = await fetch(`http://127.0.0.1:${gatewayPort}/`, {
    headers: { "user-agent": "Mozilla/5.0 (Linux; Android 15; Mobile)" },
  });
  assert.match(await androidResponse.text(), /下载 Android 稳定版/);
});

test("does not expose the legacy user gateway-key enrollment API", async (t) => {
  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: "http://127.0.0.1:1", upstreamApiKey: "unused", upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set(), requestsPerMinute: 10,
    maxConcurrentRequests: 2, upstreamTimeoutMs: 100,
  };
  const gateway = createGatewayServer(config);
  const port = await listen(gateway);
  t.after(() => gateway.close());
  const response = await fetch(`http://127.0.0.1:${port}/enrollment/status`);
  assert.equal(response.status, 410);
  assert.equal(JSON.stringify(await response.json()).includes("gw_"), false);
});

test("protects the read-only admin dashboard API with a separate key", async (t) => {
  const adminKey = "admin_test_secret";
  const config: GatewayConfig = {
    port: 0,
    upstreamBaseUrl: "http://127.0.0.1:1",
    upstreamApiKey: "unused",
    upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses",
    gatewayKeyHashes: new Set([hashGatewayKey("gw_test_secret")]),
    adminKeyHashes: new Set([hashGatewayKey(adminKey)]),
    requestsPerMinute: 10,
    maxConcurrentRequests: 2,
    upstreamTimeoutMs: 100,
  };
  const createdKeys: Array<{ prefix: string; actor: string }> = [];
  const adminRepository = {
    getSummary: async () => ({ requestsToday: 4, completedToday: 3, failedToday: 1, activeKeys: 2 }),
    listKeys: async () => [],
    getObservability: async () => ({ hourly: [], errors: [], audit: [] }),
    getUpstreamStats: async () => [],
    listDeployments: async () => [],
    requestRollback: async () => true,
    createKey: async (_input: unknown, _hash: string, prefix: string, actor: string) => { createdKeys.push({ prefix, actor }); },
    updateQuota: async () => true,
    revokeKey: async () => true,
    recordLogin: async () => undefined,
  };
  const gateway = createGatewayServer(config, { adminRepository });
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());

  const page = await fetch(`http://127.0.0.1:${gatewayPort}/admin`);
  assert.equal(page.status, 200);
  assert.match(page.headers.get("content-security-policy") ?? "", /script-src 'self'/);

  const unauthorized = await fetch(`http://127.0.0.1:${gatewayPort}/admin/api/summary`);
  assert.equal(unauthorized.status, 401);
  const authorized = await fetch(`http://127.0.0.1:${gatewayPort}/admin/api/summary`, {
    headers: { authorization: `Bearer ${adminKey}` },
  });
  assert.equal(authorized.status, 200);
  assert.deepEqual(await authorized.json(), { requestsToday: 4, completedToday: 3, failedToday: 1, activeKeys: 2, version: "unknown" });
  assert.equal(authorized.headers.get("cache-control"), "no-store");

  const created = await fetch(`http://127.0.0.1:${gatewayPort}/admin/api/keys`, {
    method: "POST",
    headers: { authorization: `Bearer ${adminKey}`, "content-type": "application/json" },
    body: JSON.stringify({ requestsPerMinute: 30, maxConcurrentRequests: 2 }),
  });
  assert.equal(created.status, 201);
  const createdBody = await created.json() as { key: string; prefix: string };
  assert.match(createdBody.key, /^gw_[a-f\d]{48}$/);
  assert.equal(createdBody.prefix, createdBody.key.slice(0, 11));
  assert.deepEqual(createdKeys, [{ prefix: createdBody.prefix, actor: hashGatewayKey(adminKey).slice(0, 16) }]);
});

test("isolates a rejected credential and fails over before streaming", async (t) => {
  const seenCredentials: string[] = [];
  const upstream = createServer(async (req, res) => {
    const authorization = req.headers.authorization ?? "";
    seenCredentials.push(authorization);
    for await (const _chunk of req) { /* consume request */ }
    if (authorization === "Bearer rejected-key") {
      res.writeHead(401, { "content-type": "application/json" });
      return res.end(JSON.stringify({ error: { message: "invalid key" } }));
    }
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ id: "resp_failover", status: "completed" }));
  });
  const upstreamPort = await listen(upstream);
  t.after(() => upstream.close());

  const config: GatewayConfig = {
    port: 0,
    upstreamBaseUrl: `http://127.0.0.1:${upstreamPort}`,
    upstreamApiKey: "rejected-key",
    upstreamApiKeys: ["rejected-key", "healthy-key"],
    upstreamResponsesPath: "/responses",
    gatewayKeyHashes: new Set([hashGatewayKey("gw_test_secret")]),
    requestsPerMinute: 10,
    maxConcurrentRequests: 2,
    upstreamTimeoutMs: 5_000,
  };
  const gateway = createGatewayServer(config);
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gatewayPort}/responses`, {
    method: "POST",
    headers: { authorization: "Bearer gw_test_secret", "content-type": "application/json" },
    body: JSON.stringify({ model: "test-model", input: "hello" }),
  });
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { id: "resp_failover", status: "completed" });
  assert.deepEqual(seenCredentials, ["Bearer rejected-key", "Bearer healthy-key"]);
});

test("fails over between credentials hosted on different upstream endpoints", async (t) => {
  const rejected = createServer(async (req, res) => {
    assert.equal(req.url, "/responses");
    for await (const _chunk of req) { /* consume request */ }
    res.writeHead(401, { "content-type": "application/json" });
    res.end(JSON.stringify({ error: { message: "invalid key" } }));
  });
  const healthy = createServer(async (req, res) => {
    assert.equal(req.url, "/v1/responses");
    assert.equal(req.headers.authorization, "Bearer secondary-key");
    for await (const _chunk of req) { /* consume request */ }
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ id: "resp_secondary", status: "completed" }));
  });
  const rejectedPort = await listen(rejected);
  const healthyPort = await listen(healthy);
  t.after(() => { rejected.close(); healthy.close(); });

  const config: GatewayConfig = {
    port: 0,
    upstreamBaseUrl: `http://127.0.0.1:${rejectedPort}`,
    upstreamApiKey: "rejected-key",
    upstreamApiKeys: ["rejected-key"],
    upstreamResponsesPath: "/responses",
    upstreams: [
      { baseUrl: `http://127.0.0.1:${rejectedPort}`, responsesPath: "/responses", apiKey: "rejected-key" },
      { baseUrl: `http://127.0.0.1:${healthyPort}`, responsesPath: "/v1/responses", apiKey: "secondary-key" },
    ],
    gatewayKeyHashes: new Set([hashGatewayKey("gw_test_secret")]),
    requestsPerMinute: 10,
    maxConcurrentRequests: 2,
    upstreamTimeoutMs: 5_000,
  };
  const gateway = createGatewayServer(config);
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gatewayPort}/responses`, {
    method: "POST",
    headers: { authorization: "Bearer gw_test_secret", "content-type": "application/json" },
    body: JSON.stringify({ model: "test-model", input: "hello" }),
  });
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { id: "resp_secondary", status: "completed" });
});

test("routes web search requests to the verified capable upstream", async (t) => {
  let ordinaryCalls = 0;
  const ordinary = createServer(async (req, res) => {
    ordinaryCalls += 1;
    for await (const _chunk of req) { /* consume request */ }
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ id: "ordinary" }));
  });
  const search = createServer(async (req, res) => {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(Buffer.from(chunk));
    const body = JSON.parse(Buffer.concat(chunks).toString()) as { tools?: { type?: string }[] };
    assert.equal(body.tools?.[0]?.type, "web_search");
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ output: [{ type: "web_search_call", status: "completed" }] }));
  });
  const ordinaryPort = await listen(ordinary);
  const searchPort = await listen(search);
  t.after(() => { ordinary.close(); search.close(); });

  const config: GatewayConfig = {
    port: 0,
    upstreamBaseUrl: `http://127.0.0.1:${ordinaryPort}`,
    upstreamApiKey: "ordinary-key",
    upstreamApiKeys: ["ordinary-key"],
    upstreamResponsesPath: "/responses",
    upstreams: [
      { baseUrl: `http://127.0.0.1:${ordinaryPort}`, responsesPath: "/responses", apiKey: "ordinary-key" },
      { baseUrl: `http://127.0.0.1:${searchPort}`, responsesPath: "/responses", apiKey: "search-key" },
    ],
    webSearchUpstreamIndex: 1,
    gatewayKeyHashes: new Set([hashGatewayKey("gw_search")]),
    requestsPerMinute: 10,
    maxConcurrentRequests: 2,
    upstreamTimeoutMs: 5_000,
  };
  const gateway = createGatewayServer(config);
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gatewayPort}/responses`, {
    method: "POST",
    headers: { authorization: "Bearer gw_search", "content-type": "application/json" },
    body: JSON.stringify({ model: "gpt-5.6-sol", tools: [{ type: "web_search" }], input: "latest" }),
  });
  assert.equal(response.status, 200);
  assert.equal(ordinaryCalls, 0);
  assert.equal((await response.json() as { output: { type: string }[] }).output[0]?.type, "web_search_call");
});

test("fails over across credentials on the same web-search provider", async (t) => {
  const ordinary = createServer(async (req, res) => {
    for await (const _chunk of req) { /* consume request */ }
    res.writeHead(500).end();
  });
  const search = createServer(async (req, res) => {
    for await (const _chunk of req) { /* consume request */ }
    if (req.headers.authorization === "Bearer rejected-search-key") {
      res.writeHead(403, { "content-type": "application/json" });
      res.end(JSON.stringify({ error: { message: "model forbidden" } }));
      return;
    }
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ output: [{ type: "web_search_call", status: "completed" }] }));
  });
  const ordinaryPort = await listen(ordinary);
  const searchPort = await listen(search);
  t.after(() => { ordinary.close(); search.close(); });
  const searchBaseUrl = `http://127.0.0.1:${searchPort}`;
  const config: GatewayConfig = {
    port: 0,
    upstreamBaseUrl: `http://127.0.0.1:${ordinaryPort}`,
    upstreamApiKey: "ordinary-key",
    upstreamApiKeys: ["ordinary-key"],
    upstreamResponsesPath: "/responses",
    upstreams: [
      { baseUrl: `http://127.0.0.1:${ordinaryPort}`, responsesPath: "/responses", apiKey: "ordinary-key" },
      { baseUrl: searchBaseUrl, responsesPath: "/responses", apiKey: "rejected-search-key" },
      { baseUrl: searchBaseUrl, responsesPath: "/responses", apiKey: "working-search-key" },
    ],
    webSearchUpstreamIndex: 2,
    gatewayKeyHashes: new Set([hashGatewayKey("gw_search_failover")]),
    requestsPerMinute: 10,
    maxConcurrentRequests: 2,
    upstreamTimeoutMs: 5_000,
  };
  const gateway = createGatewayServer(config);
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gatewayPort}/responses`, {
    method: "POST",
    headers: { authorization: "Bearer gw_search_failover", "content-type": "application/json" },
    body: JSON.stringify({ model: "gpt-5.6", tools: [{ type: "web_search" }], input: "latest" }),
  });

  assert.equal(response.status, 200);
  assert.equal((await response.json() as { output: { type: string }[] }).output[0]?.type, "web_search_call");
});

test("routes image generation requests to the verified capable upstream", async (t) => {
  let ordinaryCalls = 0;
  const ordinary = createServer(async (req, res) => {
    ordinaryCalls += 1;
    for await (const _chunk of req) { /* consume request */ }
    res.writeHead(200).end();
  });
  const images = createServer(async (req, res) => {
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(Buffer.from(chunk));
    const body = JSON.parse(Buffer.concat(chunks).toString()) as { model?: string; prompt?: string };
    assert.equal(req.url, "/v1/images/generations");
    assert.equal(body.model, "gpt-image-2");
    assert.equal(body.prompt, "draw");
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ data: [{ b64_json: "aGVsbG8=" }] }));
  });
  const ordinaryPort = await listen(ordinary);
  const imagePort = await listen(images);
  t.after(() => { ordinary.close(); images.close(); });

  const config: GatewayConfig = {
    port: 0,
    upstreamBaseUrl: `http://127.0.0.1:${ordinaryPort}`,
    upstreamApiKey: "ordinary-key",
    upstreamApiKeys: ["ordinary-key"],
    upstreamResponsesPath: "/responses",
    upstreams: [
      { baseUrl: `http://127.0.0.1:${ordinaryPort}`, responsesPath: "/responses", apiKey: "ordinary-key" },
      { baseUrl: `http://127.0.0.1:${imagePort}`, responsesPath: "/responses", apiKey: "image-key" },
    ],
    imageGenerationUpstreamIndex: 1,
    imageGeneration: { baseUrl: `http://127.0.0.1:${imagePort}/v1`, apiKey: "image-key", model: "gpt-image-2" },
    gatewayKeyHashes: new Set([hashGatewayKey("gw_images")]),
    requestsPerMinute: 10,
    maxConcurrentRequests: 2,
    upstreamTimeoutMs: 5_000,
  };
  const gateway = createGatewayServer(config);
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());

  const response = await fetch(`http://127.0.0.1:${gatewayPort}/responses`, {
    method: "POST",
    headers: { authorization: "Bearer gw_images", "content-type": "application/json" },
    body: JSON.stringify({ model: "gpt-5.6-sol", tools: [{ type: "image_generation" }], input: "draw" }),
  });
  assert.equal(response.status, 200);
  assert.equal(ordinaryCalls, 0);
  assert.match(await response.text(), /image_generation_call.*aGVsbG8=/);
});

test("sends uploaded images to the edit endpoint as generation references", async (t) => {
  const directory = await mkdtemp(join(tmpdir(), "connector-image-reference-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const images = createServer(async (req, res) => {
    assert.equal(req.url, "/v1/images/edits");
    assert.match(req.headers["content-type"] ?? "", /^multipart\/form-data;/);
    const chunks: Buffer[] = [];
    for await (const chunk of req) chunks.push(Buffer.from(chunk));
    const form = await new Response(new Uint8Array(Buffer.concat(chunks)), {
      headers: { "content-type": req.headers["content-type"]! },
    }).formData();
    assert.equal(form.get("model"), "gpt-image-2");
    assert.equal(form.get("prompt"), "基于参考图生成新图");
    assert.equal(form.get("size"), "1024x1024");
    const references = form.getAll("image[]");
    assert.equal(references.length, 1);
    const reference = references[0];
    assert.ok(reference && typeof reference !== "string");
    assert.equal(reference.name, "reference.png");
    assert.equal(reference.type, "image/png");
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ data: [{ b64_json: "aGVsbG8=" }] }));
  });
  const imagePort = await listen(images);
  t.after(() => images.close());

  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: "https://upstream.invalid", upstreamApiKey: "unused", upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set(), requestsPerMinute: 10,
    maxConcurrentRequests: 2, upstreamTimeoutMs: 5_000,
    imageGeneration: { baseUrl: `http://127.0.0.1:${imagePort}/v1`, apiKey: "image-key", model: "gpt-image-2" },
  };
  const enrollmentService = {
    authenticate: async (token: string) => token === "usr_test" ? { id: "account-1" } : null,
    getRequestLimits: () => ({ requestsPerMinute: 10, maxConcurrentRequests: 2, dailyLimit: null }),
  } as unknown as EnrollmentService;
  const gateway = createGatewayServer(config, { enrollmentService, attachmentStore: new AttachmentStore(directory) });
  const gatewayPort = await listen(gateway);
  t.after(() => gateway.close());

  const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00]);
  const uploadForm = new FormData();
  uploadForm.append("file", new Blob([png], { type: "image/png" }), "reference.png");
  const upload = await fetch(`http://127.0.0.1:${gatewayPort}/v1/attachments`, {
    method: "POST", headers: { authorization: "Bearer usr_test" }, body: uploadForm,
  });
  assert.equal(upload.status, 201);
  const uploaded = await upload.json() as { id: string };

  const response = await fetch(`http://127.0.0.1:${gatewayPort}/v1/responses`, {
    method: "POST", headers: { authorization: "Bearer usr_test", "content-type": "application/json" },
    body: JSON.stringify({
      model: "gpt-5.6-sol", tools: [{ type: "image_generation", action: "edit" }],
      input: [{ role: "user", content: "基于参考图生成新图" }], attachment_ids: [uploaded.id],
    }),
  });
  assert.equal(response.status, 200);
  assert.match(await response.text(), /image_generation_call.*aGVsbG8=/);
});

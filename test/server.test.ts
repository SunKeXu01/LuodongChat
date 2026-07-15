import assert from "node:assert/strict";
import { createServer } from "node:http";
import test from "node:test";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { hashGatewayKey } from "../src/auth.js";
import type { GatewayConfig } from "../src/config.js";
import { createGatewayServer } from "../src/server.js";
import { InMemoryRequestLedger } from "../src/ledger.js";
import type { EnrollmentService } from "../src/self-service.js";

async function listen(server: ReturnType<typeof createServer>): Promise<number> {
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("missing server address");
  return address.port;
}

test("serves the public client update manifest and fixed release assets", async (t) => {
  const directory = await mkdtemp(join(tmpdir(), "connector-release-"));
  const previous = process.env.CLIENT_RELEASE_ROOT;
  process.env.CLIENT_RELEASE_ROOT = directory;
  await writeFile(join(directory, "update.json"), '{"version":"0.1.0-preview.2"}');
  await writeFile(join(directory, "ChatGPTConnector.exe.sha256"), "abc123\n");
  await writeFile(join(directory, "ChatGPTConnector.apk"), "apk-bytes");
  await writeFile(join(directory, "LuodongChat.exe.sha256"), "def456\n");
  await writeFile(join(directory, "LuodongChat.apk"), "luodong-apk");
  t.after(async () => {
    if (previous === undefined) delete process.env.CLIENT_RELEASE_ROOT; else process.env.CLIENT_RELEASE_ROOT = previous;
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
  assert.equal(await checksum.text(), "abc123\n");
  const apk = await fetch(`http://127.0.0.1:${port}/client/download/ChatGPTConnector.apk`);
  assert.equal(apk.headers.get("content-type"), "application/vnd.android.package-archive");
  assert.equal(await apk.text(), "apk-bytes");
  const currentApk = await fetch(`http://127.0.0.1:${port}/client/download/LuodongChat.apk`);
  assert.equal(currentApk.headers.get("content-type"), "application/vnd.android.package-archive");
  assert.equal(await currentApk.text(), "luodong-apk");
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

test("scopes cross-device sync calls to the signed-in account", async (t) => {
  const config: GatewayConfig = {
    port: 0, upstreamBaseUrl: "http://127.0.0.1:1", upstreamApiKey: "unused", upstreamApiKeys: ["unused"],
    upstreamResponsesPath: "/responses", gatewayKeyHashes: new Set(), requestsPerMinute: 10,
    maxConcurrentRequests: 2, upstreamTimeoutMs: 100,
  };
  const userId = "00000000-0000-4000-8000-000000000003";
  const enrollmentService = {
    authenticate: async (token: string) => token === "usr_valid" ? { id: userId, email: "sync@example.com", nickname: "sync", avatarMediaType: null, avatarBase64: null, balanceMicrounits: 0 } : null,
  } as unknown as EnrollmentService;
  const calls: string[] = [];
  const chatSyncRepository = {
    getChanges: async (owner: string) => { calls.push(owner); return { conversations: [], messages: [], serverTime: new Date(0).toISOString() }; },
    upsertConversation: async () => null,
    deleteConversation: async () => false,
    appendMessage: async () => null,
  };
  const gateway = createGatewayServer(config, { enrollmentService, chatSyncRepository });
  const port = await listen(gateway);
  t.after(() => gateway.close());
  const unauthorized = await fetch(`http://127.0.0.1:${port}/sync/state`);
  assert.equal(unauthorized.status, 401);
  const response = await fetch(`http://127.0.0.1:${port}/sync/state`, { headers: { authorization: "Bearer usr_valid" } });
  assert.equal(response.status, 200);
  assert.deepEqual(calls, [userId]);
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
  assert.match(await response.text(), /泺栋chat/);
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

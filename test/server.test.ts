import assert from "node:assert/strict";
import { createServer } from "node:http";
import test from "node:test";
import { hashGatewayKey } from "../src/auth.js";
import type { GatewayConfig } from "../src/config.js";
import { createGatewayServer } from "../src/server.js";
import { InMemoryRequestLedger } from "../src/ledger.js";

async function listen(server: ReturnType<typeof createServer>): Promise<number> {
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("missing server address");
  return address.port;
}

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
  assert.match(await response.text(), /ChatGPT 连接器/);
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
  const adminRepository = {
    getSummary: async () => ({ requestsToday: 4, completedToday: 3, failedToday: 1, activeKeys: 2 }),
    listKeys: async () => [],
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
  assert.deepEqual(await authorized.json(), { requestsToday: 4, completedToday: 3, failedToday: 1, activeKeys: 2 });
  assert.equal(authorized.headers.get("cache-control"), "no-store");
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

import assert from "node:assert/strict";
import test from "node:test";
import {
  extractBearerKey,
  hashGatewayKey,
  PostgresGatewayKeyLimitProvider,
  PostgresGatewayKeyVerifier,
  verifyGatewayKey,
} from "../src/auth.js";

test("extracts a bearer gateway key", () => {
  assert.equal(extractBearerKey("Bearer gw_test_secret"), "gw_test_secret");
  assert.equal(extractBearerKey("Basic abc"), null);
});

test("verifies only a matching key hash", () => {
  const hashes = new Set([hashGatewayKey("gw_test_secret")]);
  assert.equal(verifyGatewayKey("gw_test_secret", hashes), true);
  assert.equal(verifyGatewayKey("wrong", hashes), false);
});

test("verifies active database keys and falls back to static keys", async () => {
  const calls: Array<{ sql: string; values: readonly unknown[] }> = [];
  const database = {
    rowCount: 1,
    async query(sql: string, values: readonly unknown[]) {
      calls.push({ sql, values });
      return { rowCount: this.rowCount };
    },
  };
  const fallback = { verify: async (key: string) => key === "gw_static" };
  const verifier = new PostgresGatewayKeyVerifier(database, fallback);

  assert.equal(await verifier.verify("gw_database"), true);
  assert.equal(calls[0]?.values[0], hashGatewayKey("gw_database"));
  assert.match(calls[0]?.sql ?? "", /status = 'active'/);

  database.rowCount = 0;
  assert.equal(await verifier.verify("gw_static"), true);
  assert.equal(await verifier.verify("gw_revoked"), false);
});

test("loads per-key limits and daily quota state", async () => {
  const calls: string[] = [];
  const database = {
    async query(sql: string) {
      calls.push(sql);
      return {
        rowCount: 1,
        rows: [{ requests_per_minute: 12, max_concurrent_requests: 3, daily_request_limit: 100 }],
      };
    },
  };
  const provider = new PostgresGatewayKeyLimitProvider(database, 30, 2);
  assert.deepEqual(await provider.getLimits(hashGatewayKey("gw_test")), {
    requestsPerMinute: 12,
    maxConcurrentRequests: 3,
    dailyLimit: 100,
  });
});

test("requires an active account when loading account-owned limits", async () => {
  let sql = "";
  const provider = new PostgresGatewayKeyLimitProvider({
    async query(value: string) { sql = value; return { rows: [] }; },
  }, 30, 2);
  assert.equal(await provider.getLimitsForUser("00000000-0000-4000-8000-000000000001"), null);
  assert.match(sql, /JOIN users AS owner/);
  assert.match(sql, /owner\.status = 'active'/);
});

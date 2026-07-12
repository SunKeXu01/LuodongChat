import assert from "node:assert/strict";
import test from "node:test";
import { PostgresAdminRepository } from "../src/admin.js";
import { ADMIN_JS } from "../src/admin-assets.js";

test("ships syntactically valid browser JavaScript", () => {
  assert.doesNotThrow(() => new Function(ADMIN_JS));
});

test("maps metadata-only admin statistics", async () => {
  const responses = [
    { rows: [{ requests_today: "5", completed_today: "4", failed_today: "1", active_keys: "2" }] },
    { rows: [{ key_prefix: "gw_12345678", status: "active", daily_request_limit: 100, requests_per_minute: null, max_concurrent_requests: 2, used_today: "5", expires_at: null }] },
  ];
  const db = { query: async () => responses.shift() ?? { rows: [] } };
  const repository = new PostgresAdminRepository(db);
  assert.deepEqual(await repository.getSummary(), { requestsToday: 5, completedToday: 4, failedToday: 1, activeKeys: 2 });
  assert.deepEqual(await repository.listKeys(), [{
    prefix: "gw_12345678",
    status: "active",
    dailyLimit: 100,
    requestsPerMinute: null,
    maxConcurrentRequests: 2,
    usedToday: 5,
    expiresAt: null,
  }]);
});

test("writes key changes and audit metadata in one statement", async () => {
  const calls: Array<{ sql: string; values?: readonly unknown[] }> = [];
  const db = {
    async query(sql: string, values?: readonly unknown[]) {
      calls.push({ sql, values });
      return { rows: [{ key_prefix: "gw_12345678" }], rowCount: 1 };
    },
  };
  const repository = new PostgresAdminRepository(db);
  await repository.createKey(
    { dailyLimit: 100, requestsPerMinute: 30, maxConcurrentRequests: 2, expiresInDays: 30 },
    "a".repeat(64), "gw_12345678", "actor123",
  );
  assert.match(calls[0]?.sql ?? "", /admin_audit_log/);
  assert.equal(calls[0]?.values?.includes("gw_test_plaintext"), false);
  assert.equal(await repository.updateQuota("gw_12345678", { dailyLimit: 200, requestsPerMinute: 20, maxConcurrentRequests: 1 }, "actor123"), true);
  assert.equal(await repository.revokeKey("gw_12345678", "actor123"), true);
  assert.match(calls[2]?.sql ?? "", /key_revoked/);
});

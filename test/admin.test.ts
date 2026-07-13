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
    { rows: [{ key_prefix: "gw_12345678", status: "active", daily_request_limit: null, requests_per_minute: null, max_concurrent_requests: 2, used_today: "5", expires_at: null }] },
  ];
  const db = { query: async () => responses.shift() ?? { rows: [] } };
  const repository = new PostgresAdminRepository(db);
  assert.deepEqual(await repository.getSummary(), { requestsToday: 5, completedToday: 4, failedToday: 1, activeKeys: 2 });
  assert.deepEqual(await repository.listKeys(), [{
    prefix: "gw_12345678",
    status: "active",
    dailyLimit: null,
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
    { dailyLimit: null, requestsPerMinute: 30, maxConcurrentRequests: 2, expiresInDays: null },
    "a".repeat(64), "gw_12345678", "actor123",
  );
  assert.match(calls[0]?.sql ?? "", /admin_audit_log/);
  assert.equal(calls[0]?.values?.includes("gw_test_plaintext"), false);
  assert.equal(await repository.updateQuota("gw_12345678", { dailyLimit: null, requestsPerMinute: 20, maxConcurrentRequests: 1 }, "actor123"), true);
  assert.equal(await repository.revokeKey("gw_12345678", "actor123"), true);
  assert.match(calls[2]?.sql ?? "", /key_revoked/);
  await repository.recordLogin(false, "actor123", "ip123");
  assert.equal(calls[3]?.values?.includes("login_failed"), true);
});

test("maps hourly, error, and audit metadata", async () => {
  const now = new Date("2026-07-12T08:00:00Z");
  const responses = [
    { rows: [{ hour: now, total: "3", completed: "2", failed: "1", average_duration_ms: "1250" }] },
    { rows: [{ code: "502", count: "1" }] },
    { rows: [{ actor_fingerprint: "actor123", action: "key_created", target_key_prefix: "gw_12345678", created_at: now }] },
  ];
  const repository = new PostgresAdminRepository({ query: async () => responses.shift() ?? { rows: [] } });
  assert.deepEqual(await repository.getObservability(), {
    hourly: [{ hour: now.toISOString(), total: 3, completed: 2, failed: 1, averageDurationMs: 1250 }],
    errors: [{ code: "502", count: 1 }],
    audit: [{ actor: "actor123", action: "key_created", targetPrefix: "gw_12345678", createdAt: now.toISOString() }],
  });
});

test("maps metadata-only upstream attempt statistics", async () => {
  const repository = new PostgresAdminRepository({ query: async () => ({ rows: [{
    credential_fingerprint: "a".repeat(64), attempts: "8", failures: "2",
    retryable_attempts: "3", average_duration_ms: "450",
  }] }) });
  assert.deepEqual(await repository.getUpstreamStats(), [{
    credentialId: "a".repeat(64), attempts: 8, failures: 2, retryableAttempts: 3, averageDurationMs: 450,
  }]);
});

test("maps deployment history and queues one rollback at a time", async () => {
  const now = new Date("2026-07-12T09:00:00Z");
  const responses = [
    { rows: [{ id: "deployment-id", git_sha: "abc123", status: "completed", started_at: now, completed_at: now }] },
    { rows: [{ id: "request-id" }], rowCount: 1 },
  ];
  const repository = new PostgresAdminRepository({ query: async () => responses.shift() ?? { rows: [] } });
  assert.deepEqual(await repository.listDeployments(), [{
    id: "deployment-id", gitSha: "abc123", status: "completed",
    startedAt: now.toISOString(), completedAt: now.toISOString(),
  }]);
  assert.equal(await repository.requestRollback("actor123"), true);
});

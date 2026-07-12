import assert from "node:assert/strict";
import test from "node:test";
import { PostgresAdminRepository } from "../src/admin.js";

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

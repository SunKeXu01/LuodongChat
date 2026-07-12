import assert from "node:assert/strict";
import test from "node:test";
import { AdminLoginGuard, RedisAdminLoginGuard } from "../src/admin-security.js";

test("temporarily blocks repeated administrator login failures", () => {
  const guard = new AdminLoginGuard(3, 1_000, 5_000);
  assert.equal(guard.recordFailure("ip", 0), 0);
  assert.equal(guard.recordFailure("ip", 100), 0);
  assert.equal(guard.recordFailure("ip", 200), 5);
  assert.equal(guard.retryAfterSeconds("ip", 1_200), 4);
  assert.equal(guard.retryAfterSeconds("ip", 5_201), 0);
});

test("clears failures after a successful administrator login", () => {
  const guard = new AdminLoginGuard(2, 1_000, 5_000);
  guard.recordFailure("ip", 0);
  guard.recordSuccess("ip");
  assert.equal(guard.recordFailure("ip", 100), 0);
});

test("stores administrator login protection in Redis", async () => {
  const calls: Array<{ keys: string[]; arguments: string[] }> = [];
  const results = [0, 1800, 1];
  const redis = {
    async eval(_script: string, options: { keys: string[]; arguments: string[] }) {
      calls.push(options);
      return results.shift() ?? 0;
    },
  };
  const guard = new RedisAdminLoginGuard(redis, 5, 900_000, 1_800_000);
  assert.equal(await guard.retryAfterSeconds("iphash"), 0);
  assert.equal(await guard.recordFailure("iphash"), 1800);
  await guard.recordSuccess("iphash");
  assert.deepEqual(calls[1]?.keys, ["admin-login:iphash:failures", "admin-login:iphash:blocked"]);
  assert.deepEqual(calls[1]?.arguments, ["900000", "5", "1800000"]);
});

import assert from "node:assert/strict";
import test from "node:test";
import { AdminLoginGuard } from "../src/admin-security.js";

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

import assert from "node:assert/strict";
import test from "node:test";
import { InMemoryLimiter } from "../src/limiter.js";

test("enforces concurrency and releases permits", () => {
  const limiter = new InMemoryLimiter(10, 1);
  const first = limiter.acquire("user", { now: 0 });
  assert.equal(first.ok, true);
  assert.deepEqual(limiter.acquire("user", { now: 1 }), { ok: false, reason: "concurrency" });
  if (first.ok) first.release();
  assert.equal(limiter.acquire("user", { now: 2 }).ok, true);
});

test("enforces the per-minute request limit", () => {
  const limiter = new InMemoryLimiter(1, 2);
  const first = limiter.acquire("user", { now: 0 });
  if (first.ok) first.release();
  assert.deepEqual(limiter.acquire("user", { now: 1 }), { ok: false, reason: "rate" });
  assert.equal(limiter.acquire("user", { now: 60_001 }).ok, true);
});

test("enforces and resets the daily request limit atomically", () => {
  const limiter = new InMemoryLimiter(30, 2);
  const first = limiter.acquire("user", { dailyLimit: 1, now: 0 });
  assert.equal(first.ok, true);
  if (first.ok) first.release();
  assert.deepEqual(limiter.acquire("user", { dailyLimit: 1, now: 1 }), { ok: false, reason: "daily" });
  assert.equal(limiter.acquire("user", { dailyLimit: 1, now: 86_400_000 }).ok, true);
});

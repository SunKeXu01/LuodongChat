import assert from "node:assert/strict";
import test from "node:test";
import { RedisRequestLimiter, type RedisScriptClient } from "../src/limiter.js";

class FakeRedis implements RedisScriptClient {
  readonly calls: Array<{ script: string; keys: string[]; arguments: string[] }> = [];
  constructor(private readonly results: number[]) {}
  async eval(script: string, options: { keys: string[]; arguments: string[] }): Promise<number> {
    this.calls.push({ script, ...options });
    const result = this.results.shift();
    if (result === undefined) throw new Error("No fake Redis result");
    return result;
  }
}

test("acquires and idempotently releases a distributed permit", async () => {
  const redis = new FakeRedis([1, 0]);
  const limiter = new RedisRequestLimiter(redis, 30, 2);
  const permit = await limiter.acquire("user-hash");
  assert.equal(permit.ok, true);
  if (permit.ok) {
    await permit.release();
    await permit.release();
  }
  assert.equal(redis.calls.length, 2);
  assert.deepEqual(redis.calls[0]?.keys, ["gateway:{user-hash}:requests", "gateway:{user-hash}:concurrent"]);
  assert.deepEqual(redis.calls[0]?.arguments.slice(0, 3), ["30", "2", "60000"]);
});

test("maps Redis rate and concurrency rejection codes", async () => {
  const redis = new FakeRedis([-1, -2]);
  const limiter = new RedisRequestLimiter(redis, 1, 1);
  assert.deepEqual(await limiter.acquire("user"), { ok: false, reason: "rate" });
  assert.deepEqual(await limiter.acquire("user"), { ok: false, reason: "concurrency" });
});

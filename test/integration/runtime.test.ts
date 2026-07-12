import assert from "node:assert/strict";
import test from "node:test";
import pg from "pg";
import { createClient } from "redis";
import { hashGatewayKey, PostgresGatewayKeyLimitProvider, PostgresGatewayKeyVerifier } from "../../src/auth.js";
import { RedisRequestLimiter, type RedisScriptClient } from "../../src/limiter.js";
import { runMigrations } from "../../src/migrate.js";

const databaseUrl = process.env.INTEGRATION_DATABASE_URL;
const redisUrl = process.env.INTEGRATION_REDIS_URL;
const integrationAvailable = Boolean(databaseUrl && redisUrl);

test("applies migrations idempotently and verifies database-backed keys", { skip: !integrationAvailable }, async () => {
  assert.ok(databaseUrl);
  const first = await runMigrations(databaseUrl);
  assert.deepEqual(first, ["001_initial.sql", "002_gateway_key_limits.sql", "003_admin_audit.sql", "004_deployment_history.sql", "005_self_service_enrollment.sql"]);
  assert.deepEqual(await runMigrations(databaseUrl), []);

  const pool = new pg.Pool({ connectionString: databaseUrl });
  try {
    const plaintext = "gw_integration_test";
    await pool.query(`WITH owner AS (INSERT INTO users DEFAULT VALUES RETURNING id)
      INSERT INTO gateway_keys
        (user_id, key_hash, key_prefix, daily_request_limit, requests_per_minute, max_concurrent_requests)
      SELECT id, decode($1, 'hex'), 'gw_integra', 2, 7, 1 FROM owner`, [hashGatewayKey(plaintext)]);
    assert.equal(await new PostgresGatewayKeyVerifier(pool).verify(plaintext), true);
    assert.deepEqual(await new PostgresGatewayKeyLimitProvider(pool, 30, 2).getLimits(hashGatewayKey(plaintext)), {
      requestsPerMinute: 7,
      maxConcurrentRequests: 1,
      dailyLimit: 2,
    });
  } finally {
    await pool.end();
  }
});

test("enforces the daily quota atomically in Redis", { skip: !integrationAvailable }, async () => {
  assert.ok(redisUrl);
  const redis = createClient({ url: redisUrl });
  await redis.connect();
  try {
    await redis.flushDb();
    const limiter = new RedisRequestLimiter(redis as unknown as RedisScriptClient, 30, 2);
    const first = await limiter.acquire("integration", { dailyLimit: 1, now: 0 });
    assert.equal(first.ok, true);
    if (first.ok) await first.release();
    assert.deepEqual(await limiter.acquire("integration", { dailyLimit: 1, now: 1 }), { ok: false, reason: "daily" });
  } finally {
    await redis.quit();
  }
});

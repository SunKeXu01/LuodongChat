import assert from "node:assert/strict";
import test from "node:test";
import pg from "pg";
import { createClient } from "redis";
import { hashGatewayKey, PostgresGatewayKeyLimitProvider, PostgresGatewayKeyVerifier } from "../../src/auth.js";
import { RedisRequestLimiter, type RedisScriptClient } from "../../src/limiter.js";
import { runMigrations } from "../../src/migrate.js";
import { PostgresEnrollmentRepository } from "../../src/self-service.js";

const databaseUrl = process.env.INTEGRATION_DATABASE_URL;
const redisUrl = process.env.INTEGRATION_REDIS_URL;
const integrationAvailable = Boolean(databaseUrl && redisUrl);

test("applies migrations idempotently and verifies database-backed keys", { skip: !integrationAvailable }, async () => {
  assert.ok(databaseUrl);
  const first = await runMigrations(databaseUrl);
  assert.deepEqual(first, ["001_initial.sql", "002_gateway_key_limits.sql", "003_admin_audit.sql", "004_deployment_history.sql", "005_self_service_enrollment.sql", "006_unlimited_key_policy.sql", "007_user_accounts.sql", "008_cross_device_chat_sync.sql", "009_one_gateway_key_per_account.sql"]);
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

test("creates and cancels a self-service enrollment challenge", { skip: !databaseUrl }, async () => {
  assert.ok(databaseUrl);
  await runMigrations(databaseUrl);
  const pool = new pg.Pool({ connectionString: databaseUrl });
  try {
    const repository = new PostgresEnrollmentRepository(pool);
    assert.equal(await repository.createChallenge("a".repeat(64), "b".repeat(64), "ip-test"), "created");
    await repository.cancelLatestChallenge("a".repeat(64));
    const result = await pool.query("SELECT count(*) FROM enrollment_challenges WHERE identity_hash = decode($1, 'hex')", ["a".repeat(64)]);
    assert.equal(Number(result.rows[0].count), 0);
  } finally {
    await pool.end();
  }
});

test("creates and authenticates a passwordless user account", { skip: !databaseUrl }, async () => {
  assert.ok(databaseUrl);
  await runMigrations(databaseUrl);
  const pool = new pg.Pool({ connectionString: databaseUrl });
  try {
    const repository = new PostgresEnrollmentRepository(pool);
    const profile = await repository.login(
      "c".repeat(64), "account@example.com", hashGatewayKey("usr_integration"),
      hashGatewayKey("gw_account_integration"), "gw_account", { dailyLimit: null, requestsPerMinute: 30, maxConcurrentRequests: 2, expiresInDays: null },
    );
    assert.ok(profile);
    assert.equal(profile.email, "account@example.com");
    await repository.login(
      "c".repeat(64), "account@example.com", hashGatewayKey("usr_integration_second"),
      hashGatewayKey("gw_account_second"), "gw_account", { dailyLimit: null, requestsPerMinute: 30, maxConcurrentRequests: 2, expiresInDays: null },
    );
    const keys = await pool.query(
      `SELECT count(*) AS count FROM gateway_keys key JOIN users ON users.id = key.user_id
       WHERE users.email = $1 AND key.status = 'active'`, ["account@example.com"],
    );
    assert.equal(Number(keys.rows[0]?.count), 1);
    assert.equal((await repository.authenticate(hashGatewayKey("usr_integration")))?.email, "account@example.com");
    assert.equal((await repository.updateProfile(hashGatewayKey("usr_integration"), "测试用户"))?.nickname, "测试用户");
    await repository.logout(hashGatewayKey("usr_integration"));
    assert.equal(await repository.authenticate(hashGatewayKey("usr_integration")), null);
    await pool.query("UPDATE users SET status = 'disabled' WHERE email = $1", ["account@example.com"]);
    assert.equal(await repository.authenticate(hashGatewayKey("usr_integration_second")), null);
    assert.equal(await new PostgresGatewayKeyLimitProvider(pool, 30, 2).getLimitsForUser(profile.id), null);
    assert.equal(await repository.login(
      "c".repeat(64), "account@example.com", hashGatewayKey("usr_integration_third"),
      hashGatewayKey("gw_account_third"), "gw_account", { dailyLimit: null, requestsPerMinute: 30, maxConcurrentRequests: 2, expiresInDays: null },
    ), null);
  } finally { await pool.end(); }
});

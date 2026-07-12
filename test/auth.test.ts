import assert from "node:assert/strict";
import test from "node:test";
import { extractBearerKey, hashGatewayKey, PostgresGatewayKeyVerifier, verifyGatewayKey } from "../src/auth.js";

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

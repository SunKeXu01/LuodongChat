import assert from "node:assert/strict";
import test from "node:test";
import { extractBearerKey, hashGatewayKey, verifyGatewayKey } from "../src/auth.js";

test("extracts a bearer gateway key", () => {
  assert.equal(extractBearerKey("Bearer gw_test_secret"), "gw_test_secret");
  assert.equal(extractBearerKey("Basic abc"), null);
});

test("verifies only a matching key hash", () => {
  const hashes = new Set([hashGatewayKey("gw_test_secret")]);
  assert.equal(verifyGatewayKey("gw_test_secret", hashes), true);
  assert.equal(verifyGatewayKey("wrong", hashes), false);
});

import assert from "node:assert/strict";
import test from "node:test";
import { UpstreamPool } from "../src/upstream-pool.js";

test("balances credentials and never exposes keys in snapshots", () => {
  const pool = new UpstreamPool(["secret-a", "secret-b"]);
  const first = pool.acquire();
  const second = pool.acquire();
  assert.ok(first && second);
  assert.notEqual(first.id, second.id);
  assert.equal(JSON.stringify(pool.snapshot()).includes("secret"), false);
  pool.recordSuccess(first.id);
  pool.recordSuccess(second.id);
});

test("opens a failing credential and recovers it through half-open", () => {
  const pool = new UpstreamPool(["secret"], 2, 100);
  const first = pool.acquire(undefined, 0);
  assert.ok(first);
  pool.recordFailure(first.id, true, 0);
  const second = pool.acquire(undefined, 1);
  assert.ok(second);
  pool.recordFailure(second.id, true, 1);
  assert.equal(pool.acquire(undefined, 50), null);
  const probe = pool.acquire(undefined, 101);
  assert.ok(probe);
  assert.equal(pool.snapshot()[0]?.health, "half_open");
  pool.recordSuccess(probe.id);
  assert.equal(pool.snapshot()[0]?.health, "healthy");
});

test("does not degrade a credential for non-retryable errors", () => {
  const pool = new UpstreamPool(["secret"]);
  const credential = pool.acquire();
  assert.ok(credential);
  pool.recordFailure(credential.id, false);
  assert.equal(pool.snapshot()[0]?.health, "healthy");
});

test("returns the endpoint attached to each credential without exposing it in snapshots", () => {
  const pool = new UpstreamPool([{
    apiKey: "secondary-secret",
    baseUrl: "https://secondary.example/v1",
    responsesPath: "/responses",
  }]);
  const credential = pool.acquire();
  assert.ok(credential);
  assert.equal(credential.baseUrl, "https://secondary.example/v1");
  assert.equal(credential.responsesPath, "/responses");
  assert.equal(JSON.stringify(pool.snapshot()).includes("secondary.example"), false);
});

test("routes web search only to a capable upstream", () => {
  const pool = new UpstreamPool([
    { apiKey: "ordinary", supportsWebSearch: false },
    { apiKey: "search", supportsWebSearch: true },
  ]);
  const selected = pool.acquire(new Set(), Date.now(), true);
  assert.ok(selected);
  assert.equal(selected.apiKey, "search");
  pool.recordSuccess(selected.id);
});

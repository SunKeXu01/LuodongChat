import assert from "node:assert/strict";
import test from "node:test";
import { restoreManagedFields } from "../src/config-merge.js";

test("restores managed fields while preserving unrelated user changes", () => {
  const before = { model: "original", theme: "dark" };
  const applied = { model: "gpt-5.5", theme: "dark", model_providers: { Connector: { base_url: "https://gateway" } } };
  const current = { ...applied, theme: "light", notifications: true };
  const result = restoreManagedFields(before, applied, current, ["model", "model_providers.Connector"]);
  assert.deepEqual(result.merged, { model: "original", theme: "light", model_providers: {}, notifications: true });
  assert.deepEqual(result.conflicts, []);
});

test("reports a conflict when an externally modified managed field diverges", () => {
  const before = { model: "original" };
  const applied = { model: "gpt-5.5" };
  const current = { model: "user-choice" };
  const result = restoreManagedFields(before, applied, current, ["model"]);
  assert.deepEqual(result.merged, current);
  assert.deepEqual(result.conflicts, ["model"]);
  assert.deepEqual(result.restoredPaths, []);
});

test("removes a field introduced by the connector", () => {
  const result = restoreManagedFields({}, { provider: "Connector" }, { provider: "Connector", theme: "dark" }, ["provider"]);
  assert.deepEqual(result.merged, { theme: "dark" });
});

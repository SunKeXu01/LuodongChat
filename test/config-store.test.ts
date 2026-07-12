import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { AtomicConfigStore } from "../src/config-store.js";

test("backs up, verifies, and atomically replaces a config file", async (t) => {
  const directory = await mkdtemp(join(tmpdir(), "connector-config-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const target = join(directory, "config.toml");
  await writeFile(target, 'model = "original"\n', { mode: 0o600 });
  const store = new AtomicConfigStore("0.1.0-test");
  const metadata = await store.replaceWithBackup(target, 'model = "gpt-5.5"\n', ["model"]);

  assert.equal(await readFile(target, "utf8"), 'model = "gpt-5.5"\n');
  assert.equal(await readFile(metadata.backupPath, "utf8"), 'model = "original"\n');
  assert.equal(await store.verifyBackup(metadata), true);
  assert.deepEqual(metadata.managedPaths, ["model"]);
});

test("refuses to write while another configuration operation holds the lock", async (t) => {
  const directory = await mkdtemp(join(tmpdir(), "connector-lock-"));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const target = join(directory, "config.toml");
  await writeFile(target, "original");
  const stateDirectory = join(directory, ".chatgpt-connector");
  await mkdir(stateDirectory);
  await writeFile(join(stateDirectory, "config.lock"), "held");

  const store = new AtomicConfigStore("0.1.0-test");
  await assert.rejects(store.replaceWithBackup(target, "changed", ["model"]), /config_locked/);
  assert.equal(await readFile(target, "utf8"), "original");
});

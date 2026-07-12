import assert from "node:assert/strict";
import test from "node:test";
import { PostgresRequestLedger, type SqlClient } from "../src/ledger.js";

class RecordingClient implements SqlClient {
  readonly calls: Array<{ sql: string; values: readonly unknown[] }> = [];
  async query(sql: string, values: readonly unknown[]): Promise<{ rowCount: number }> {
    this.calls.push({ sql, values });
    return { rowCount: 1 };
  }
}

test("writes request metadata with parameterized SQL", async () => {
  const db = new RecordingClient();
  const ledger = new PostgresRequestLedger(db);
  const requestId = "00000000-0000-4000-8000-000000000001";
  const startedAt = new Date("2026-07-12T00:00:00Z");
  await ledger.startRequest({ requestId, gatewayKeyHash: "a".repeat(64), startedAt });
  await ledger.recordAttempt({
    requestId,
    attempt: 1,
    credentialId: "b".repeat(64),
    startedAt,
    endedAt: new Date("2026-07-12T00:00:01Z"),
    statusCode: 200,
    retryable: false,
  });
  await ledger.finishRequest({
    requestId,
    status: "completed",
    endedAt: new Date("2026-07-12T00:00:02Z"),
    statusCode: 200,
    attempts: 1,
  });

  assert.equal(db.calls.length, 3);
  assert.equal(db.calls[0]?.sql.includes("decode($2, 'hex')"), true);
  assert.equal(db.calls[1]?.sql.includes("VALUES\n         ($1::uuid"), true);
  assert.equal(db.calls[2]?.sql.includes("WHERE id = $1::uuid"), true);
  assert.equal(JSON.stringify(db.calls).includes("prompt"), false);
});

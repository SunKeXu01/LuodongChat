import assert from "node:assert/strict";
import test from "node:test";
import { PostgresChatSyncRepository } from "../src/sync.js";

test("caps synchronized message pages and returns them in chronological order", async () => {
  const calls: string[] = [];
  const database = {
    async query(sql: string) {
      calls.push(sql);
      if (sql.includes("server_time")) return { rows: [{ server_time: new Date(0) }] };
      if (sql.includes("FROM chat_messages")) {
        return { rows: [
          { id: "2", conversation_id: "c", role: "assistant", content: "later", client_created_at: new Date(2), updated_at: new Date(2) },
          { id: "1", conversation_id: "c", role: "user", content: "earlier", client_created_at: new Date(1), updated_at: new Date(1) },
        ] };
      }
      return { rows: [] };
    },
  };
  const state = await new PostgresChatSyncRepository(database).getChanges("user", new Date(0));
  assert.match(calls.find((sql) => sql.includes("FROM chat_messages")) ?? "", /LIMIT 100/);
  assert.deepEqual(state.messages.map((message) => message.content), ["earlier", "later"]);
});

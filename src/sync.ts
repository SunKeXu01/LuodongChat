export interface SyncedConversation {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  deletedAt: string | null;
}

export interface SyncedMessage {
  id: string;
  conversationId: string;
  role: "user" | "assistant" | "system";
  content: string;
  clientCreatedAt: string;
  updatedAt: string;
}

export interface ChatSyncState {
  conversations: SyncedConversation[];
  messages: SyncedMessage[];
  serverTime: string;
}

interface SqlClient {
  query(sql: string, values?: readonly unknown[]): Promise<{ rows: Array<Record<string, unknown>>; rowCount?: number | null }>;
}

export interface ChatSyncRepository {
  getChanges(userId: string, since: Date): Promise<ChatSyncState>;
  upsertConversation(userId: string, id: string, title: string): Promise<SyncedConversation | null>;
  deleteConversation(userId: string, id: string): Promise<boolean>;
  appendMessage(userId: string, input: { id: string; conversationId: string; role: SyncedMessage["role"]; content: string; clientCreatedAt: Date }): Promise<SyncedMessage | null>;
}

export class PostgresChatSyncRepository implements ChatSyncRepository {
  constructor(private readonly db: SqlClient) {}

  async getChanges(userId: string, since: Date): Promise<ChatSyncState> {
    const [conversations, messages, clock] = await Promise.all([
      this.db.query(
        `SELECT id, title, created_at, updated_at, deleted_at
         FROM chat_conversations
         WHERE user_id = $1 AND updated_at > $2
         ORDER BY updated_at, id LIMIT 500`, [userId, since],
      ),
      this.db.query(
        `SELECT id, conversation_id, role, content, client_created_at, updated_at
         FROM chat_messages
         WHERE user_id = $1 AND updated_at > $2
         ORDER BY updated_at DESC, id DESC LIMIT 100`, [userId, since],
      ),
      this.db.query("SELECT now() AS server_time"),
    ]);
    return {
      conversations: conversations.rows.map(mapConversation),
      messages: messages.rows.map(mapMessage).reverse(),
      serverTime: new Date(String(clock.rows[0]!.server_time)).toISOString(),
    };
  }

  async upsertConversation(userId: string, id: string, title: string): Promise<SyncedConversation | null> {
    const result = await this.db.query(
      `INSERT INTO chat_conversations (id, user_id, title)
       VALUES ($1::uuid, $2::uuid, $3)
       ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, updated_at = now(), deleted_at = NULL
       WHERE chat_conversations.user_id = EXCLUDED.user_id
       RETURNING id, title, created_at, updated_at, deleted_at`, [id, userId, title],
    );
    return result.rows[0] ? mapConversation(result.rows[0]) : null;
  }

  async deleteConversation(userId: string, id: string): Promise<boolean> {
    const result = await this.db.query(
      `UPDATE chat_conversations SET deleted_at = now(), updated_at = now()
       WHERE id = $1::uuid AND user_id = $2::uuid AND deleted_at IS NULL`, [id, userId],
    );
    return (result.rowCount ?? 0) > 0;
  }

  async appendMessage(userId: string, input: { id: string; conversationId: string; role: SyncedMessage["role"]; content: string; clientCreatedAt: Date }): Promise<SyncedMessage | null> {
    const result = await this.db.query(
      `INSERT INTO chat_messages (id, conversation_id, user_id, role, content, client_created_at)
       SELECT $1::uuid, conversation.id, $2::uuid, $4, $5, $6
       FROM chat_conversations conversation
       WHERE conversation.id = $3::uuid AND conversation.user_id = $2::uuid AND conversation.deleted_at IS NULL
       ON CONFLICT (id) DO NOTHING
       RETURNING id, conversation_id, role, content, client_created_at, updated_at`,
      [input.id, userId, input.conversationId, input.role, input.content, input.clientCreatedAt],
    );
    if (result.rows[0]) return mapMessage(result.rows[0]);
    const existing = await this.db.query(
      `SELECT id, conversation_id, role, content, client_created_at, updated_at
       FROM chat_messages WHERE id = $1::uuid AND user_id = $2::uuid`, [input.id, userId],
    );
    return existing.rows[0] ? mapMessage(existing.rows[0]) : null;
  }
}

function mapConversation(row: Record<string, unknown>): SyncedConversation {
  return {
    id: String(row.id), title: String(row.title),
    createdAt: new Date(String(row.created_at)).toISOString(),
    updatedAt: new Date(String(row.updated_at)).toISOString(),
    deletedAt: row.deleted_at ? new Date(String(row.deleted_at)).toISOString() : null,
  };
}

function mapMessage(row: Record<string, unknown>): SyncedMessage {
  return {
    id: String(row.id), conversationId: String(row.conversation_id),
    role: String(row.role) as SyncedMessage["role"], content: String(row.content),
    clientCreatedAt: new Date(String(row.client_created_at)).toISOString(),
    updatedAt: new Date(String(row.updated_at)).toISOString(),
  };
}

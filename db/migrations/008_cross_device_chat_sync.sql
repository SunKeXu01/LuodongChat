CREATE TABLE chat_conversations (
  id uuid PRIMARY KEY,
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  title text NOT NULL CHECK (char_length(title) BETWEEN 1 AND 120),
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  deleted_at timestamptz
);

CREATE INDEX chat_conversations_user_updated_idx
  ON chat_conversations (user_id, updated_at DESC);

CREATE TABLE chat_messages (
  id uuid PRIMARY KEY,
  conversation_id uuid NOT NULL REFERENCES chat_conversations(id) ON DELETE CASCADE,
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  role text NOT NULL CHECK (role IN ('user', 'assistant', 'system')),
  content text NOT NULL CHECK (char_length(content) BETWEEN 1 AND 32000),
  client_created_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX chat_messages_user_updated_idx
  ON chat_messages (user_id, updated_at, id);
CREATE INDEX chat_messages_conversation_created_idx
  ON chat_messages (conversation_id, client_created_at, id);

COMMENT ON TABLE chat_conversations IS 'User-owned conversation metadata synchronized across signed-in clients.';
COMMENT ON TABLE chat_messages IS 'User-owned chat content synchronized across signed-in clients. Never exposed across accounts.';

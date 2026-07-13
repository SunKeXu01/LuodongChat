ALTER TABLE users
  ADD COLUMN email text,
  ADD COLUMN nickname text,
  ADD COLUMN avatar_media_type text,
  ADD COLUMN avatar_data bytea;

CREATE UNIQUE INDEX users_email_idx ON users (lower(email)) WHERE email IS NOT NULL;

CREATE TABLE user_sessions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  token_hash bytea NOT NULL UNIQUE,
  expires_at timestamptz NOT NULL,
  last_used_at timestamptz NOT NULL DEFAULT now(),
  revoked_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX user_sessions_user_idx ON user_sessions (user_id, created_at DESC);

CREATE TABLE user_wallets (
  user_id uuid PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
  balance_microunits bigint NOT NULL DEFAULT 0 CHECK (balance_microunits >= 0),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE wallet_transactions (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id uuid NOT NULL REFERENCES users(id),
  amount_microunits bigint NOT NULL,
  transaction_type text NOT NULL,
  request_id uuid REFERENCES user_requests(id),
  description text NOT NULL,
  idempotency_key text UNIQUE,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX wallet_transactions_user_created_idx ON wallet_transactions (user_id, created_at DESC);

COMMENT ON TABLE user_sessions IS 'Hashed passwordless-login session tokens; plaintext tokens are never stored.';
COMMENT ON TABLE wallet_transactions IS 'Append-only user balance ledger. Amounts are integer microunits.';

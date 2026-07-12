ALTER TABLE users
  ADD COLUMN identity_hash bytea;

CREATE UNIQUE INDEX users_identity_hash_idx
  ON users (identity_hash)
  WHERE identity_hash IS NOT NULL;

CREATE TABLE enrollment_challenges (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  identity_hash bytea NOT NULL,
  code_hash bytea NOT NULL,
  ip_fingerprint text NOT NULL,
  expires_at timestamptz NOT NULL,
  attempts integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 5),
  consumed_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX enrollment_challenges_identity_created_idx
  ON enrollment_challenges (identity_hash, created_at DESC);
CREATE INDEX enrollment_challenges_ip_created_idx
  ON enrollment_challenges (ip_fingerprint, created_at DESC);

COMMENT ON COLUMN users.identity_hash IS 'SHA-256 normalized login identity; plaintext email is not retained.';
COMMENT ON TABLE enrollment_challenges IS 'Short-lived, hashed email verification codes. Plaintext codes are never stored.';

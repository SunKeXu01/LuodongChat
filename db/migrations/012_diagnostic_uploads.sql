CREATE TABLE diagnostic_uploads (
  id text PRIMARY KEY CHECK (id ~ '^DG-[0-9]{8}-[A-Z0-9]{5}$'),
  user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  app_version text NOT NULL,
  platform text NOT NULL,
  error_code text NOT NULL,
  manifest jsonb NOT NULL DEFAULT '{}'::jsonb,
  package_data bytea NOT NULL,
  package_size integer NOT NULL CHECK (package_size BETWEEN 1 AND 20971520),
  status text NOT NULL DEFAULT 'available' CHECK (status IN ('processing','available','rejected','deleted')),
  created_at timestamptz NOT NULL DEFAULT now(),
  expires_at timestamptz NOT NULL DEFAULT (now() + interval '7 days'),
  deleted_at timestamptz
);
CREATE INDEX diagnostic_uploads_user_created_idx ON diagnostic_uploads (user_id, created_at DESC);
CREATE INDEX diagnostic_uploads_expiry_idx ON diagnostic_uploads (expires_at) WHERE deleted_at IS NULL;
COMMENT ON TABLE diagnostic_uploads IS 'User-approved, redacted diagnostic packages. Excluded from long-term application backups.';

ALTER TABLE users
  ADD COLUMN password_hash text,
  ADD COLUMN password_failed_attempts integer NOT NULL DEFAULT 0 CHECK (password_failed_attempts BETWEEN 0 AND 5),
  ADD COLUMN password_locked_until timestamptz;

COMMENT ON COLUMN users.password_hash IS 'Versioned scrypt password verifier. Plaintext passwords are never stored.';
COMMENT ON COLUMN users.password_locked_until IS 'Temporary account lock after repeated password failures.';

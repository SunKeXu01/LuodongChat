ALTER TABLE users
  DROP COLUMN IF EXISTS password_locked_until,
  DROP COLUMN IF EXISTS password_failed_attempts,
  DROP COLUMN IF EXISTS password_hash;

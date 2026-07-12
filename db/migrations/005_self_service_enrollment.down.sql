DROP TABLE IF EXISTS enrollment_challenges;
DROP INDEX IF EXISTS users_identity_hash_idx;
ALTER TABLE users DROP COLUMN IF EXISTS identity_hash;

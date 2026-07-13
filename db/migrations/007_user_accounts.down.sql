DROP TABLE IF EXISTS wallet_transactions;
DROP TABLE IF EXISTS user_wallets;
DROP TABLE IF EXISTS user_sessions;
DROP INDEX IF EXISTS users_email_idx;
ALTER TABLE users
  DROP COLUMN IF EXISTS avatar_data,
  DROP COLUMN IF EXISTS avatar_media_type,
  DROP COLUMN IF EXISTS nickname,
  DROP COLUMN IF EXISTS email;

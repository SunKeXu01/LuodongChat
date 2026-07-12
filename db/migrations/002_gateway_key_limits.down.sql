DROP INDEX IF EXISTS gateway_keys_active_hash_idx;
ALTER TABLE gateway_keys
  DROP COLUMN IF EXISTS daily_request_limit,
  DROP COLUMN IF EXISTS max_concurrent_requests,
  DROP COLUMN IF EXISTS requests_per_minute;

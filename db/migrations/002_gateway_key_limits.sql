ALTER TABLE gateway_keys
  ADD COLUMN requests_per_minute integer CHECK (requests_per_minute > 0),
  ADD COLUMN max_concurrent_requests integer CHECK (max_concurrent_requests > 0),
  ADD COLUMN daily_request_limit integer CHECK (daily_request_limit > 0);

CREATE INDEX gateway_keys_active_hash_idx
  ON gateway_keys (key_hash)
  WHERE status = 'active';

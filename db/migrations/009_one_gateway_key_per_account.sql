WITH ranked AS (
  SELECT id, row_number() OVER (PARTITION BY user_id ORDER BY created_at DESC, id DESC) AS position
  FROM gateway_keys
  WHERE status = 'active'
)
UPDATE gateway_keys key
SET status = 'revoked', revoked_at = COALESCE(revoked_at, now())
FROM ranked
WHERE key.id = ranked.id AND ranked.position > 1;

CREATE UNIQUE INDEX gateway_keys_one_active_user_idx
  ON gateway_keys (user_id)
  WHERE status = 'active';

COMMENT ON INDEX gateway_keys_one_active_user_idx IS
  'Each account has exactly one active internal gateway credential; plaintext is never returned to clients.';

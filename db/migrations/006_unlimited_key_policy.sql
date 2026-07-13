-- Active user keys are permanent and have no daily request quota.
UPDATE gateway_keys
SET expires_at = NULL,
    daily_request_limit = NULL
WHERE status = 'active';

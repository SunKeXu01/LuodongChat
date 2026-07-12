CREATE TABLE admin_audit_log (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    actor_fingerprint text NOT NULL,
    action text NOT NULL,
    target_key_prefix text,
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX admin_audit_log_created_idx ON admin_audit_log (created_at DESC);

COMMENT ON TABLE admin_audit_log IS 'Administrative metadata only. Plaintext keys and request content are forbidden.';

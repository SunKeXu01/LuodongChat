CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TYPE gateway_key_status AS ENUM ('active', 'revoked', 'expired');
CREATE TYPE credential_health AS ENUM ('healthy', 'degraded', 'open', 'half_open', 'disabled', 'auth_failed', 'insufficient_balance', 'expired');
CREATE TYPE request_status AS ENUM ('in_progress', 'completed', 'failed', 'cancelled');

CREATE TABLE users (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    status text NOT NULL DEFAULT 'active',
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE gateway_keys (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL REFERENCES users(id),
    key_hash bytea NOT NULL UNIQUE,
    key_prefix text NOT NULL,
    status gateway_key_status NOT NULL DEFAULT 'active',
    expires_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    revoked_at timestamptz
);

CREATE TABLE upstreams (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name text NOT NULL UNIQUE,
    base_url text NOT NULL,
    responses_path text NOT NULL DEFAULT '/responses',
    enabled boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE upstream_credentials (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    upstream_id uuid NOT NULL REFERENCES upstreams(id),
    encrypted_secret bytea NOT NULL,
    secret_fingerprint text NOT NULL UNIQUE,
    health credential_health NOT NULL DEFAULT 'healthy',
    consecutive_failures integer NOT NULL DEFAULT 0 CHECK (consecutive_failures >= 0),
    open_until timestamptz,
    in_flight integer NOT NULL DEFAULT 0 CHECK (in_flight >= 0),
    last_success_at timestamptz,
    last_failure_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE user_requests (
    id uuid PRIMARY KEY,
    user_id uuid REFERENCES users(id),
    gateway_key_id uuid REFERENCES gateway_keys(id),
    external_model text,
    status request_status NOT NULL DEFAULT 'in_progress',
    started_at timestamptz NOT NULL,
    first_byte_at timestamptz,
    ended_at timestamptz,
    status_code integer,
    error_class text,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    input_tokens bigint CHECK (input_tokens >= 0),
    output_tokens bigint CHECK (output_tokens >= 0),
    cached_input_tokens bigint CHECK (cached_input_tokens >= 0),
    user_charge_microunits bigint CHECK (user_charge_microunits >= 0),
    client_version text,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE upstream_attempts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    request_id uuid NOT NULL REFERENCES user_requests(id),
    attempt_number integer NOT NULL CHECK (attempt_number > 0),
    upstream_id uuid REFERENCES upstreams(id),
    credential_id uuid REFERENCES upstream_credentials(id),
    credential_fingerprint text NOT NULL,
    started_at timestamptz NOT NULL,
    first_byte_at timestamptz,
    ended_at timestamptz NOT NULL,
    status_code integer,
    error_class text,
    retryable boolean NOT NULL,
    output_started boolean NOT NULL DEFAULT false,
    input_tokens bigint CHECK (input_tokens >= 0),
    output_tokens bigint CHECK (output_tokens >= 0),
    estimated_cost_microunits bigint CHECK (estimated_cost_microunits >= 0),
    UNIQUE (request_id, attempt_number)
);

CREATE INDEX user_requests_user_started_idx ON user_requests (user_id, started_at DESC);
CREATE INDEX user_requests_status_started_idx ON user_requests (status, started_at DESC);
CREATE INDEX upstream_attempts_request_idx ON upstream_attempts (request_id, attempt_number);
CREATE INDEX upstream_credentials_health_idx ON upstream_credentials (upstream_id, health, open_until);

COMMENT ON TABLE user_requests IS 'Metadata only. Prompt and model output must not be stored here.';
COMMENT ON COLUMN upstream_credentials.encrypted_secret IS 'Application-encrypted secret; plaintext is forbidden.';

-- PostgreSQL runs this file directly when initializing a fresh container.
-- Record the baseline so the application migrator starts with the next migration.
CREATE TABLE IF NOT EXISTS schema_migrations (
    name text PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO schema_migrations (name) VALUES ('001_initial.sql')
ON CONFLICT (name) DO NOTHING;

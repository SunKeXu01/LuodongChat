CREATE TYPE deployment_status AS ENUM ('completed', 'failed', 'rolled_back');
CREATE TYPE deployment_request_status AS ENUM ('pending', 'processing', 'completed', 'failed');

CREATE TABLE deployment_history (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    git_sha text NOT NULL,
    status deployment_status NOT NULL,
    previous_image text,
    deployed_image text,
    started_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    details jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE deployment_control_requests (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    action text NOT NULL CHECK (action = 'rollback'),
    status deployment_request_status NOT NULL DEFAULT 'pending',
    requested_by text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    processed_at timestamptz,
    error_message text
);

CREATE INDEX deployment_history_started_idx ON deployment_history (started_at DESC);
CREATE INDEX deployment_control_pending_idx ON deployment_control_requests (created_at) WHERE status = 'pending';

COMMENT ON TABLE deployment_control_requests IS 'Privileged deployment actions requested by authenticated administrators and executed by the host worker.';

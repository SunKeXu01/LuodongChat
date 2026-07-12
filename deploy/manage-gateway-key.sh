#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR="${APP_DIR:-/app/chatgpt_connector}"
COMPOSE_FILE="$APP_DIR/deploy/compose.server.yaml"
ENV_FILE="$APP_DIR/.env"

usage() {
  printf 'Usage: %s create [daily-limit] | list | quota <key-prefix> <daily-limit> <rpm> <concurrent> | revoke <key-prefix>\n' "$0" >&2
  exit 2
}

psql_connector() {
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres \
    psql --username connector --dbname connector --no-psqlrc "$@"
}

case "${1:-}" in
  create)
    daily_limit="${2:-100}"
    [[ "$daily_limit" =~ ^[1-9][0-9]*$ ]] || usage
    key="gw_$(openssl rand -hex 24)"
    hash="$(printf '%s' "$key" | sha256sum | cut -d' ' -f1)"
    prefix="${key:0:11}"
    psql_connector --set=ON_ERROR_STOP=1 --set=hash="$hash" --set=prefix="$prefix" --set=daily_limit="$daily_limit" <<'SQL' >/dev/null
WITH new_user AS (
  INSERT INTO users DEFAULT VALUES RETURNING id
)
INSERT INTO gateway_keys (user_id, key_hash, key_prefix, daily_request_limit)
SELECT id, decode(:'hash', 'hex'), :'prefix', :'daily_limit'::integer FROM new_user;
SQL
    printf 'Gateway key created. Copy it now; it will not be shown again.\n%s\n' "$key"
    ;;
  list)
    psql_connector --set=ON_ERROR_STOP=1 --pset=pager=off \
      --command="SELECT key.key_prefix, key.status, key.daily_request_limit AS daily_limit, key.requests_per_minute AS rpm, key.max_concurrent_requests AS concurrent, count(request.id) FILTER (WHERE request.started_at >= date_trunc('day', now())) AS used_today, key.expires_at FROM gateway_keys key LEFT JOIN user_requests request ON request.gateway_key_id = key.id GROUP BY key.id ORDER BY key.created_at DESC;"
    ;;
  quota)
    [[ $# -eq 5 ]] || usage
    prefix="$2"
    daily_limit="$3"
    rpm="$4"
    concurrent="$5"
    [[ "$daily_limit" =~ ^[1-9][0-9]*$ && "$rpm" =~ ^[1-9][0-9]*$ && "$concurrent" =~ ^[1-9][0-9]*$ ]] || usage
    psql_connector --set=ON_ERROR_STOP=1 --set=prefix="$prefix" --set=daily_limit="$daily_limit" --set=rpm="$rpm" --set=concurrent="$concurrent" <<'SQL'
UPDATE gateway_keys
SET daily_request_limit = :'daily_limit'::integer,
    requests_per_minute = :'rpm'::integer,
    max_concurrent_requests = :'concurrent'::integer
WHERE key_prefix = :'prefix';
SQL
    ;;
  revoke)
    [[ -n "${2:-}" ]] || usage
    prefix="$2"
    psql_connector --set=ON_ERROR_STOP=1 --set=prefix="$prefix" <<'SQL'
UPDATE gateway_keys
SET status = 'revoked', revoked_at = now()
WHERE key_prefix = :'prefix' AND status = 'active';
SQL
    ;;
  *) usage ;;
esac

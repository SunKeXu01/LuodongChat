#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR="${APP_DIR:-/app/chatgpt_connector}"
COMPOSE_FILE="$APP_DIR/deploy/compose.server.yaml"
ENV_FILE="$APP_DIR/.env"

usage() {
  printf 'Usage: %s create | list | revoke <key-prefix>\n' "$0" >&2
  exit 2
}

psql_connector() {
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres \
    psql --username connector --dbname connector --no-psqlrc "$@"
}

case "${1:-}" in
  create)
    key="gw_$(openssl rand -hex 24)"
    hash="$(printf '%s' "$key" | sha256sum | cut -d' ' -f1)"
    prefix="${key:0:11}"
    psql_connector --set=ON_ERROR_STOP=1 --set=hash="$hash" --set=prefix="$prefix" <<'SQL' >/dev/null
WITH new_user AS (
  INSERT INTO users DEFAULT VALUES RETURNING id
)
INSERT INTO gateway_keys (user_id, key_hash, key_prefix)
SELECT id, decode(:'hash', 'hex'), :'prefix' FROM new_user;
SQL
    printf 'Gateway key created. Copy it now; it will not be shown again.\n%s\n' "$key"
    ;;
  list)
    psql_connector --set=ON_ERROR_STOP=1 --pset=pager=off \
      --command="SELECT key_prefix, status, expires_at, created_at, revoked_at FROM gateway_keys ORDER BY created_at DESC;"
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

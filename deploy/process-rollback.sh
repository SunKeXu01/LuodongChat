#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR="${APP_DIR:-/app/chatgpt_connector}"
COMPOSE_FILE="$APP_DIR/deploy/compose.server.yaml"
ENV_FILE="$APP_DIR/.env"
LOCK_FILE="/var/lock/chatgpt-connector-deploy.lock"

exec 9>"$LOCK_FILE"
flock -n 9 || exit 0
cd "$APP_DIR"

psql_command() {
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres \
    psql -U connector -d connector -v ON_ERROR_STOP=1 "$@"
}

request_id="$(psql_command -qAtc "WITH candidate AS (
  SELECT id FROM deployment_control_requests WHERE status = 'pending' ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED
) UPDATE deployment_control_requests request SET status = 'processing'
FROM candidate WHERE request.id = candidate.id RETURNING request.id;")"
[[ -n "$request_id" ]] || exit 0

rollback_failed() {
  status=$?
  psql_command -v request_id="$request_id" -v message="Rollback failed with exit status $status" -c \
    "UPDATE deployment_control_requests SET status = 'failed', processed_at = now(), error_message = :'message' WHERE id = :'request_id'::uuid;" >/dev/null || true
  printf 'Rollback request %s failed on %s.\n' "$request_id" "$(hostname)" \
    | "$APP_DIR/deploy/send-alert.sh" "ChatGPT Connector rollback failed" "rollback" || true
  exit "$status"
}
trap rollback_failed ERR

docker image inspect deploy-gateway:rollback >/dev/null
current_image="$(docker image inspect --format '{{.Id}}' deploy-gateway:latest)"
rollback_image="$(docker image inspect --format '{{.Id}}' deploy-gateway:rollback)"
docker tag deploy-gateway:rollback deploy-gateway:latest
docker tag "$current_image" deploy-gateway:rollback
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --no-deps --force-recreate gateway

healthy=false
for _ in $(seq 1 30); do
  health="$(docker inspect --format '{{.State.Health.Status}}' deploy-gateway-1 2>/dev/null || true)"
  [[ "$health" == "healthy" ]] && { healthy=true; break; }
  sleep 2
done
[[ "$healthy" == true ]]
curl --fail --silent --show-error --max-time 15 https://520skx.com/healthz >/dev/null

psql_command -v request_id="$request_id" -v deployed_image="$rollback_image" -v previous_image="$current_image" -c \
  "UPDATE deployment_control_requests SET status = 'completed', processed_at = now() WHERE id = :'request_id'::uuid;
   INSERT INTO deployment_history (git_sha, status, previous_image, deployed_image, completed_at, details)
   VALUES ('rollback', 'rolled_back', :'previous_image', :'deployed_image', now(), jsonb_build_object('requestId', :'request_id'));" >/dev/null

printf 'Rollback request %s completed successfully.\n' "$request_id"

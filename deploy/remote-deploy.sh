#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR="${APP_DIR:-/app/chatgpt_connector}"
COMPOSE_FILE="$APP_DIR/deploy/compose.server.yaml"
ENV_FILE="$APP_DIR/.env"
LOCK_FILE="/var/lock/chatgpt-connector-deploy.lock"

exec 9>"$LOCK_FILE"
flock -n 9 || { printf 'Another deployment is already running.\n' >&2; exit 1; }

deployment_failed() {
  status=$?
  printf 'Automated deployment failed on %s at %s. Exit status: %s\n' "$(hostname)" "$(date --iso-8601=seconds)" "$status" \
    | "$APP_DIR/deploy/send-alert.sh" "ChatGPT Connector deployment failed" "deployment" || true
  exit "$status"
}
trap deployment_failed ERR

cd "$APP_DIR"
test -s "$ENV_FILE"

install -m 644 "$APP_DIR/deploy/systemd/chatgpt-connector-alert-check.service" /etc/systemd/system/
install -m 644 "$APP_DIR/deploy/systemd/chatgpt-connector-alert-check.timer" /etc/systemd/system/
install -m 644 "$APP_DIR/deploy/systemd/chatgpt-connector-alert@.service" /etc/systemd/system/
install -m 644 "$APP_DIR/deploy/systemd/chatgpt-connector-backup.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now chatgpt-connector-alert-check.timer

systemctl start chatgpt-connector-backup.service

previous_image="$(docker image inspect --format '{{.Id}}' deploy-gateway:latest 2>/dev/null || true)"
if [[ -n "$previous_image" ]]; then
  docker tag "$previous_image" deploy-gateway:rollback
fi

docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" build gateway
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" run --rm --no-deps gateway node dist/src/migrate.js
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --no-deps gateway

healthy=false
for _ in $(seq 1 30); do
  status="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' deploy-gateway-1 2>/dev/null || true)"
  if [[ "$status" == "healthy" ]]; then
    healthy=true
    break
  fi
  sleep 2
done

if [[ "$healthy" != true ]]; then
  docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" logs --tail=100 gateway >&2 || true
  if docker image inspect deploy-gateway:rollback >/dev/null 2>&1; then
    printf 'New gateway failed health checks; rolling back the application image.\n' >&2
    docker tag deploy-gateway:rollback deploy-gateway:latest
    docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d --no-deps --force-recreate gateway
  fi
  exit 1
fi

curl --fail --silent --show-error --max-time 15 https://520skx.com/healthz >/dev/null
docker image prune --force --filter 'until=168h' >/dev/null
printf 'Production deployment completed successfully.\n'

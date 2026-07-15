#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR="${APP_DIR:-/app/chatgpt_connector}"
COMPOSE_FILE="$APP_DIR/deploy/compose.server.yaml"
ENV_FILE="$APP_DIR/.env"
LOCK_FILE="/var/lock/chatgpt-connector-deploy.lock"
RELEASE_VERSION="${APP_VERSION:-unknown}"
deployment_id=""

exec 9>"$LOCK_FILE"
flock -n 9 || { printf 'Another deployment is already running.\n' >&2; exit 1; }

deployment_failed() {
  status=$?
  if [[ -n "$deployment_id" ]]; then
    docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres psql -U connector -d connector \
      -v deployment_id="$deployment_id" >/dev/null 2>&1 <<'SQL' || true
UPDATE deployment_history SET status = 'failed', completed_at = now() WHERE id = :'deployment_id'::uuid;
SQL
  fi
  printf 'ChatGPT Connector 自动部署失败。\n服务器：%s\n时间：%s\n退出状态：%s\n' "$(hostname)" "$(date --iso-8601=seconds)" "$status" \
    | "$APP_DIR/deploy/send-alert.sh" "ChatGPT Connector 自动部署失败" "deployment" || true
  exit "$status"
}
trap deployment_failed ERR

cd "$APP_DIR"
test -s "$ENV_FILE"
install -d -o connector-deploy -g connector-deploy -m 755 /app/module/client-releases

install -m 644 "$APP_DIR/deploy/systemd/chatgpt-connector-alert-check.service" /etc/systemd/system/
install -m 644 "$APP_DIR/deploy/systemd/chatgpt-connector-alert-check.timer" /etc/systemd/system/
install -m 644 "$APP_DIR/deploy/systemd/chatgpt-connector-alert@.service" /etc/systemd/system/
install -m 644 "$APP_DIR/deploy/systemd/chatgpt-connector-backup.service" /etc/systemd/system/
install -m 644 "$APP_DIR/deploy/systemd/chatgpt-connector-rollback.service" /etc/systemd/system/
install -m 644 "$APP_DIR/deploy/systemd/chatgpt-connector-rollback.timer" /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now chatgpt-connector-alert-check.timer
systemctl enable --now chatgpt-connector-rollback.timer

systemctl start chatgpt-connector-backup.service

previous_image="$(docker image inspect --format '{{.Id}}' deploy-gateway:latest 2>/dev/null || true)"
if [[ -n "$previous_image" ]]; then
  docker tag "$previous_image" deploy-gateway:rollback
fi

docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" build gateway
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" run --rm --no-deps gateway node dist/src/migrate.js
deployment_id="$(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres psql -U connector -d connector -qAt \
  -v git_sha="$RELEASE_VERSION" -v previous_image="$previous_image" <<'SQL'
INSERT INTO deployment_history (git_sha, status, previous_image)
VALUES (:'git_sha', 'completed', NULLIF(:'previous_image', '')) RETURNING id;
SQL
)"
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

curl --fail --silent --show-error --max-time 15 https://luodongchat.com/healthz >/dev/null
deployed_image="$(docker image inspect --format '{{.Id}}' deploy-gateway:latest)"
docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres psql -U connector -d connector \
  -v deployment_id="$deployment_id" -v deployed_image="$deployed_image" >/dev/null <<'SQL'
UPDATE deployment_history SET deployed_image = :'deployed_image', completed_at = now()
WHERE id = :'deployment_id'::uuid;
SQL
docker image prune --force --filter 'until=168h' >/dev/null
printf 'Production deployment completed successfully.\n'

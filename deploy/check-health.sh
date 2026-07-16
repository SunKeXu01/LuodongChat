#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR="${APP_DIR:-/app/chatgpt_connector}"
COMPOSE_FILE="$APP_DIR/deploy/compose.server.yaml"
ENV_FILE="$APP_DIR/.env"

curl --fail --silent --show-error --max-time 10 https://520skx.com/healthz >/dev/null

containers="$(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" ps --quiet)"
if [[ -z "$containers" ]]; then
  printf 'No connector containers are running.\n' >&2
  exit 1
fi

unhealthy="$(docker inspect --format '{{.Name}} {{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' \
  $containers | awk '$2 != "healthy"' || true)"
if [[ -n "$unhealthy" ]]; then
  printf 'One or more connector containers are not healthy:\n%s\n' "$unhealthy" >&2
  exit 1
fi

printf 'ChatGPT Connector health check passed.\n'

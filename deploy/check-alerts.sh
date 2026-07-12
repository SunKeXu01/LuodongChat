#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR="${APP_DIR:-/app/chatgpt_connector}"
COMPOSE_FILE="$APP_DIR/deploy/compose.server.yaml"
ENV_FILE="$APP_DIR/.env"
issues=()

if ! curl --fail --silent --show-error --max-time 10 https://520skx.com/healthz >/dev/null; then
  issues+=("Public gateway health check failed")
fi

disk_used="$(df --output=pcent / | tail -n 1 | tr -dc '0-9')"
if (( disk_used >= 85 )); then
  issues+=("Root filesystem usage is ${disk_used}%")
fi

if ! openssl x509 -checkend $((14 * 86400)) -noout -in /etc/letsencrypt/live/520skx.com/fullchain.pem >/dev/null 2>&1; then
  issues+=("TLS certificate expires in less than 14 days")
fi

latest_backup="$(find /app/module/backups/postgres -type f -name 'connector-*.sql.gz' -printf '%T@\n' 2>/dev/null | sort -nr | head -n 1)"
now="$(date +%s)"
if [[ -z "$latest_backup" ]] || (( now - ${latest_backup%.*} > 36 * 3600 )); then
  issues+=("No successful PostgreSQL backup was created in the last 36 hours")
fi

read -r total failed < <(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres \
  psql -U connector -d connector -At -F ' ' -c \
  "SELECT count(*), count(*) FILTER (WHERE status = 'failed') FROM user_requests WHERE started_at >= now() - interval '15 minutes';")
if (( total >= 5 && failed >= 3 && failed * 100 / total >= 20 )); then
  issues+=("Gateway error rate is $((failed * 100 / total))% (${failed}/${total}) over the last 15 minutes")
fi

if (( ${#issues[@]} > 0 )); then
  {
    printf 'ChatGPT Connector production alert on %s\n\n' "$(hostname)"
    printf -- '- %s\n' "${issues[@]}"
    printf '\nTime: %s\n' "$(date --iso-8601=seconds)"
  } | "$APP_DIR/deploy/send-alert.sh" "ChatGPT Connector production alert" "production-monitor"
  exit 1
fi

printf 'Production alert checks passed.\n'

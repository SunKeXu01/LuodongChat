#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR="${APP_DIR:-/app/chatgpt_connector}"
COMPOSE_FILE="$APP_DIR/deploy/compose.server.yaml"
ENV_FILE="$APP_DIR/.env"
issues=()

if ! curl --fail --silent --show-error --max-time 10 https://luodongchat.com/healthz >/dev/null; then
  issues+=("公网网关健康检查失败")
fi

disk_used="$(df --output=pcent / | tail -n 1 | tr -dc '0-9')"
if (( disk_used >= 85 )); then
  issues+=("服务器根磁盘使用率已达到 ${disk_used}%")
fi

if ! openssl x509 -checkend $((14 * 86400)) -noout -in /etc/nginx/ssl/luodongchat.com.pem >/dev/null 2>&1; then
  issues+=("TLS 证书将在 14 天内到期")
fi

latest_backup="$(find /app/module/backups/postgres -type f -name 'connector-*.sql.gz' -printf '%T@\n' 2>/dev/null | sort -nr | head -n 1)"
now="$(date +%s)"
if [[ -z "$latest_backup" ]] || (( now - ${latest_backup%.*} > 36 * 3600 )); then
  issues+=("最近 36 小时没有成功的 PostgreSQL 备份")
fi

read -r total failed < <(docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres \
  psql -U connector -d connector -At -F ' ' -c \
  "SELECT count(*), count(*) FILTER (WHERE status = 'failed') FROM user_requests WHERE started_at >= now() - interval '15 minutes';")
if (( total >= 5 && failed >= 3 && failed * 100 / total >= 20 )); then
  issues+=("最近 15 分钟网关错误率为 $((failed * 100 / total))%（${failed}/${total}）")
fi

if (( ${#issues[@]} > 0 )); then
  {
    printf 'ChatGPT Connector 生产环境告警\n\n服务器：%s\n\n' "$(hostname)"
    printf -- '- %s\n' "${issues[@]}"
    printf '\n时间：%s\n' "$(date --iso-8601=seconds)"
  } | "$APP_DIR/deploy/send-alert.sh" "ChatGPT Connector 生产环境告警" "production-monitor"
  exit 1
fi

printf 'Production alert checks passed.\n'

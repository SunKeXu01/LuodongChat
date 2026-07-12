#!/usr/bin/env bash
set -Eeuo pipefail

APP_DIR="${APP_DIR:-/app/chatgpt_connector}"
BACKUP_DIR="${BACKUP_DIR:-/app/module/backups/postgres}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"
COMPOSE_FILE="$APP_DIR/deploy/compose.server.yaml"
ENV_FILE="$APP_DIR/.env"

umask 077
mkdir -p "$BACKUP_DIR"

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
target="$BACKUP_DIR/connector-$timestamp.sql.gz"
temporary="$target.tmp"

cleanup() {
  rm -f "$temporary"
}
trap cleanup EXIT

docker compose --env-file "$ENV_FILE" -f "$COMPOSE_FILE" exec -T postgres \
  pg_dump --username connector --dbname connector --clean --if-exists --no-owner --no-privileges \
  | gzip -9 > "$temporary"

gzip -t "$temporary"
test -s "$temporary"
mv "$temporary" "$target"
trap - EXIT

find "$BACKUP_DIR" -type f -name 'connector-*.sql.gz' -mtime "+$RETENTION_DAYS" -delete
printf 'Created PostgreSQL backup: %s\n' "$target"

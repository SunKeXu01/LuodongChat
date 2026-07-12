# Operations runbook

## Health

The public health endpoint is `https://520skx.com/healthz`. On the server:

```bash
systemctl status chatgpt-connector-health.timer
journalctl -u chatgpt-connector-health.service --since today
docker compose --env-file /app/chatgpt_connector/.env \
  -f /app/chatgpt_connector/deploy/compose.server.yaml ps
```

Always pass `--env-file`; otherwise Compose substitutes blank values while parsing the deployment file.

## PostgreSQL backups

The backup timer runs daily and writes mode-`600` compressed SQL dumps under `/app/module/backups/postgres`. Backups older than 14 days are removed automatically.

```bash
systemctl status chatgpt-connector-backup.timer
systemctl start chatgpt-connector-backup.service
journalctl -u chatgpt-connector-backup.service -n 50
```

To restore a selected backup during a maintenance window:

```bash
gzip -dc /app/module/backups/postgres/connector-YYYYMMDDTHHMMSSZ.sql.gz | \
  docker compose --env-file /app/chatgpt_connector/.env \
    -f /app/chatgpt_connector/deploy/compose.server.yaml exec -T postgres \
    psql --username connector --dbname connector
```

Stop gateway traffic before restoring. A restore replaces database objects because dumps include `--clean --if-exists`.

## Firewall

The host firewall permits only OpenSSH, HTTP, and HTTPS. PostgreSQL, Redis, and the gateway bind inside Docker or to loopback and must not be exposed publicly.

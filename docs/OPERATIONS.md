# Operations runbook

## Health

The primary public health endpoint is `https://520skx.com/healthz`; `https://luodongchat.com/healthz` is the synchronized secondary-domain endpoint. On the server:

```bash
systemctl status chatgpt-connector-health.timer
journalctl -u chatgpt-connector-health.service --since today
docker compose --env-file /app/chatgpt_connector/.env \
  -f /app/chatgpt_connector/deploy/compose.server.yaml ps
```

Always pass `--env-file`; otherwise Compose substitutes blank values while parsing the deployment file.

## Automated deployment

The `Deploy production` GitHub Actions workflow runs only after a successful `Build and test` workflow on `main`. It checks out the exact tested commit, uses a pinned SSH host key and the restricted `connector-deploy` account, synchronizes release files while preserving `.env`, and invokes the single allow-listed privileged deployment script through `sudo`.

The remote deployment takes an exclusive lock, starts a PostgreSQL backup, preserves the previous gateway image, builds the new image, applies migrations under the migration advisory lock, recreates only the gateway, and verifies internal and public health. A failed gateway health check restores the previous application image; forward-only database migrations must remain backward compatible.

Required repository Actions secrets:

- `DEPLOY_SSH_KEY`: dedicated private key used only for production deployment;
- `DEPLOY_KNOWN_HOSTS`: pinned `known_hosts` entry for the production server.
- `ANDROID_SIGNING_KEY_BASE64`: base64-encoded Android release keystore;
- `ANDROID_SIGNING_STORE_PASSWORD`: Android release keystore password;
- `ANDROID_SIGNING_KEY_ALIAS`: Android release key alias;
- `ANDROID_SIGNING_KEY_PASSWORD`: Android release private-key password.

## Email alerts

SMTP credentials live only in `/etc/chatgpt-connector/alert.env` with mode `600`. The five-minute alert timer checks public health, root disk usage, TLS certificate lifetime, backup freshness, and the 15-minute gateway error rate. Backup and deployment failures invoke the same sender. A per-alert cooldown prevents repeated email storms.

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

## Gateway keys

Database-backed keys take effect immediately and can be revoked without restarting the gateway. The plaintext is shown only once during creation; only its SHA-256 hash and short prefix are stored.

### Account login and internal credentials

Configure `SELF_SERVICE_ENABLED=true` and the SMTP variables only in the server `.env`. The clients request a six-digit email code through `/account/code` and exchange it for an account session through `/account/verify`. Codes expire after ten minutes, are stored only as SHA-256 hashes, permit five attempts, and are rate-limited by identity and IP fingerprint.

Each account owns exactly one active internal gateway credential. Only its SHA-256 hash and short prefix are stored; the generated plaintext is discarded and never returned to Windows or Android. Legacy `/enrollment/*` key-issuing endpoints return HTTP 410.

```bash
/app/chatgpt_connector/deploy/manage-gateway-key.sh create 100
/app/chatgpt_connector/deploy/manage-gateway-key.sh list
/app/chatgpt_connector/deploy/manage-gateway-key.sh quota gw_12345678 500 30 2
/app/chatgpt_connector/deploy/manage-gateway-key.sh revoke gw_12345678
```

Keys configured through `GATEWAY_KEY_HASHES` remain available as an emergency compatibility fallback. Rotate that fallback through the root-owned `.env` file when database-backed administration is established.

## Admin dashboard

Set `ADMIN_KEY_HASHES` to one or more SHA-256 hashes of dedicated administrator keys, then recreate the gateway container. Open `https://520skx.com/admin` and enter the corresponding plaintext administrator key. Do not reuse a user gateway key.

The dashboard exposes metadata only: aggregate request states, key prefixes, status, limits, today's request count, and expiry. Administrators can create a key, update its quota, or revoke it. A new plaintext key is returned once and is never persisted. Mutations write a metadata-only audit record containing the administrator fingerprint, action, target prefix, and limits.

Operational views include hourly request counts for the last 24 hours, completed/failed totals, average request duration, error distribution, and the latest 50 administrative audit events.

Responses include `Cache-Control: no-store`; prompt and response bodies are never queried or displayed.

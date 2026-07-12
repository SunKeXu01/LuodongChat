# ChatGPT Connector

ChatGPT Connector is currently in preview validation. The repository contains a Codex-oriented API gateway, its deployment stack, and a Windows desktop configuration client.

## Implemented prototype

- `POST /responses` and `POST /v1/responses` streaming proxy
- SHA-256 hashed user gateway key verification
- in-memory per-key request and concurrency limits
- one retry before response streaming begins for retryable upstream failures
- multiple upstream credentials with least-loaded selection
- degraded/open/half-open credential health states and automatic recovery
- immediate credential isolation and failover on upstream 401/403
- metadata-only record for every upstream attempt
- request ledger abstraction with metadata-only in-memory validation
- initial PostgreSQL schema for identities, credentials, requests, attempts, usage, and cost
- configuration backup, integrity verification, atomic replacement, and three-way restore primitives
- request IDs and metadata-only logs
- `GET /healthz` health check

The deployed preview includes PostgreSQL request metadata, Redis-backed distributed limits, upstream credential failover, and a Windows client. It is not yet a signed production release; accounting reconciliation, multi-provider routing, automatic updates, and broader Windows compatibility testing remain pending.

## Local development

Requires Node.js 22 or newer and pnpm.

```bash
pnpm install
cp .env.example .env
pnpm test
pnpm typecheck
pnpm build
```

Generate a development key hash without printing it into application logs:

```bash
node -e "const c=require('node:crypto'); console.log(c.createHash('sha256').update(process.argv[1]).digest('hex'))" gw_test_change_me
```

Export the values from `.env`, then run `pnpm dev`. The prototype intentionally does not load `.env` automatically, so production and local secret injection use the same explicit mechanism.

## Safety boundaries

- Never commit `.env`, gateway keys, or upstream credentials.
- The proxy does not log request bodies or response bodies.
- A retry only occurs before any upstream response body is sent to the caller.
- The in-memory limiter is for one-process validation only.

## Container stack

`compose.yaml` defines the gateway, PostgreSQL 17, and Redis 7.4 with health checks and persistent volumes. The gateway binds to localhost by default; TLS termination should be added at the deployment edge.

```bash
docker compose up --build
```

When `DATABASE_URL` is set, the gateway verifies PostgreSQL connectivity at startup and uses the persistent request ledger. When `REDIS_URL` is set, it connects to Redis and enables distributed request/concurrency limits. Without either variable, local development falls back to in-memory implementations.

Apply pending migrations under a PostgreSQL advisory lock with `pnpm migrate` after building. Each migration name is recorded in `schema_migrations` and will not be applied twice.

## Windows client

The Windows client is under `client/` and targets .NET 10 LTS with WPF. `ChatGPTConnector.Core` contains platform-independent configuration planning and safe file installation primitives; its tests can run on macOS or Windows.

```bash
dotnet test client/ChatGPTConnector.Core.Tests/ChatGPTConnector.Core.Tests.csproj
dotnet build client/ChatGPTConnector.App/ChatGPTConnector.App.csproj
```

The WPF preview verifies the gateway, previews managed configuration changes, creates integrity-checked backups, applies Codex configuration atomically, and restores managed fields with conflict detection.

## Download a Windows preview

Every push to `main` runs gateway and .NET tests and creates a Windows x64 self-contained preview package:

- For normal downloads, open [Releases](https://github.com/SunKeXu01/ChatGPTConnector/releases) and download the ZIP attached to the latest preview release.
- For development builds, use the Actions artifact steps below.

1. Open the repository's **Actions** page and select the latest successful **Build and test** run.
2. Download the `ChatGPTConnector-0.1.0-preview.1-win-x64` artifact.
3. Extract the artifact, verify the included ZIP with its `.sha256` file, then extract the ZIP.
4. Run `ChatGPTConnector.exe` on Windows 10/11 x64.

The preview is unsigned. Windows may identify it as an unrecognized application; only test artifacts downloaded from this repository's own Actions run.

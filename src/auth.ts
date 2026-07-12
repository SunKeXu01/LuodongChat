import { createHash, timingSafeEqual } from "node:crypto";

export function hashGatewayKey(key: string): string {
  return createHash("sha256").update(key, "utf8").digest("hex");
}

export function extractBearerKey(header: string | undefined): string | null {
  if (!header) return null;
  const match = /^Bearer\s+(.+)$/i.exec(header);
  return match?.[1]?.trim() || null;
}

export function verifyGatewayKey(key: string, allowedHashes: ReadonlySet<string>): boolean {
  const actual = Buffer.from(hashGatewayKey(key), "hex");
  for (const hash of allowedHashes) {
    if (!/^[a-f\d]{64}$/i.test(hash)) continue;
    const expected = Buffer.from(hash, "hex");
    if (actual.length === expected.length && timingSafeEqual(actual, expected)) return true;
  }
  return false;
}

export interface GatewayKeyVerifier {
  verify(key: string): Promise<boolean>;
}

export class StaticGatewayKeyVerifier implements GatewayKeyVerifier {
  constructor(private readonly allowedHashes: ReadonlySet<string>) {}

  async verify(key: string): Promise<boolean> {
    return verifyGatewayKey(key, this.allowedHashes);
  }
}

export interface AuthSqlClient {
  query(sql: string, values: readonly unknown[]): Promise<{
    rowCount?: number | null;
    rows?: Array<Record<string, unknown>>;
  }>;
}

export interface GatewayKeyLimits {
  requestsPerMinute: number;
  maxConcurrentRequests: number;
  dailyLimitExceeded: boolean;
}

export interface GatewayKeyLimitProvider {
  getLimits(keyHash: string): Promise<GatewayKeyLimits | null>;
}

export class PostgresGatewayKeyVerifier implements GatewayKeyVerifier {
  constructor(
    private readonly db: AuthSqlClient,
    private readonly fallback?: GatewayKeyVerifier,
  ) {}

  async verify(key: string): Promise<boolean> {
    const result = await this.db.query(
      `SELECT 1
       FROM gateway_keys AS key
       JOIN users AS owner ON owner.id = key.user_id
       WHERE key.key_hash = decode($1, 'hex')
         AND key.status = 'active'
         AND owner.status = 'active'
         AND (key.expires_at IS NULL OR key.expires_at > now())
       LIMIT 1`,
      [hashGatewayKey(key)],
    );
    if ((result.rowCount ?? 0) > 0) return true;
    return this.fallback?.verify(key) ?? false;
  }
}

export class PostgresGatewayKeyLimitProvider implements GatewayKeyLimitProvider {
  constructor(
    private readonly db: AuthSqlClient,
    private readonly defaultRequestsPerMinute: number,
    private readonly defaultMaxConcurrentRequests: number,
  ) {}

  async getLimits(keyHash: string): Promise<GatewayKeyLimits | null> {
    const result = await this.db.query(
      `SELECT
         COALESCE(key.requests_per_minute, $2::integer) AS requests_per_minute,
         COALESCE(key.max_concurrent_requests, $3::integer) AS max_concurrent_requests,
         key.daily_request_limit IS NOT NULL AND
           (SELECT count(*) FROM user_requests request
            WHERE request.gateway_key_id = key.id
              AND request.started_at >= date_trunc('day', now())) >= key.daily_request_limit AS daily_limit_exceeded
       FROM gateway_keys AS key
       WHERE key.key_hash = decode($1, 'hex')
       LIMIT 1`,
      [keyHash, this.defaultRequestsPerMinute, this.defaultMaxConcurrentRequests],
    );
    const row = result.rows?.[0];
    if (!row) return null;
    return {
      requestsPerMinute: Number(row.requests_per_minute),
      maxConcurrentRequests: Number(row.max_concurrent_requests),
      dailyLimitExceeded: row.daily_limit_exceeded === true,
    };
  }
}

export interface AdminSummary {
  requestsToday: number;
  completedToday: number;
  failedToday: number;
  activeKeys: number;
}

export interface AdminKeyRow {
  prefix: string;
  status: string;
  dailyLimit: number | null;
  requestsPerMinute: number | null;
  maxConcurrentRequests: number | null;
  usedToday: number;
  expiresAt: string | null;
}

export interface AdminRepository {
  getSummary(): Promise<AdminSummary>;
  listKeys(): Promise<AdminKeyRow[]>;
  createKey(input: AdminKeyInput, keyHash: string, keyPrefix: string, actorFingerprint: string): Promise<void>;
  updateQuota(keyPrefix: string, input: AdminQuotaInput, actorFingerprint: string): Promise<boolean>;
  revokeKey(keyPrefix: string, actorFingerprint: string): Promise<boolean>;
}

export interface AdminQuotaInput {
  dailyLimit: number;
  requestsPerMinute: number;
  maxConcurrentRequests: number;
}

export interface AdminKeyInput extends AdminQuotaInput {
  expiresInDays: number | null;
}

interface AdminSqlClient {
  query(sql: string, values?: readonly unknown[]): Promise<{ rows: Array<Record<string, unknown>>; rowCount?: number | null }>;
}

export class PostgresAdminRepository implements AdminRepository {
  constructor(private readonly db: AdminSqlClient) {}

  async getSummary(): Promise<AdminSummary> {
    const result = await this.db.query(`SELECT
      count(*) FILTER (WHERE started_at >= date_trunc('day', now())) AS requests_today,
      count(*) FILTER (WHERE started_at >= date_trunc('day', now()) AND status = 'completed') AS completed_today,
      count(*) FILTER (WHERE started_at >= date_trunc('day', now()) AND status = 'failed') AS failed_today,
      (SELECT count(*) FROM gateway_keys WHERE status = 'active' AND (expires_at IS NULL OR expires_at > now())) AS active_keys
    FROM user_requests`);
    const row = result.rows[0] ?? {};
    return {
      requestsToday: Number(row.requests_today ?? 0),
      completedToday: Number(row.completed_today ?? 0),
      failedToday: Number(row.failed_today ?? 0),
      activeKeys: Number(row.active_keys ?? 0),
    };
  }

  async listKeys(): Promise<AdminKeyRow[]> {
    const result = await this.db.query(`SELECT
      key.key_prefix,
      key.status::text,
      key.daily_request_limit,
      key.requests_per_minute,
      key.max_concurrent_requests,
      count(request.id) FILTER (WHERE request.started_at >= date_trunc('day', now())) AS used_today,
      key.expires_at
    FROM gateway_keys key
    LEFT JOIN user_requests request ON request.gateway_key_id = key.id
    GROUP BY key.id
    ORDER BY key.created_at DESC`);
    return result.rows.map((row) => ({
      prefix: String(row.key_prefix),
      status: String(row.status),
      dailyLimit: row.daily_request_limit === null ? null : Number(row.daily_request_limit),
      requestsPerMinute: row.requests_per_minute === null ? null : Number(row.requests_per_minute),
      maxConcurrentRequests: row.max_concurrent_requests === null ? null : Number(row.max_concurrent_requests),
      usedToday: Number(row.used_today ?? 0),
      expiresAt: row.expires_at instanceof Date ? row.expires_at.toISOString() : row.expires_at ? String(row.expires_at) : null,
    }));
  }

  async createKey(input: AdminKeyInput, keyHash: string, keyPrefix: string, actorFingerprint: string): Promise<void> {
    await this.db.query(
      `WITH new_user AS (
         INSERT INTO users DEFAULT VALUES RETURNING id
       ), new_key AS (
         INSERT INTO gateway_keys
           (user_id, key_hash, key_prefix, daily_request_limit, requests_per_minute, max_concurrent_requests, expires_at)
         SELECT id, decode($1, 'hex'), $2, $3, $4, $5,
           CASE WHEN $6::integer IS NULL THEN NULL ELSE now() + make_interval(days => $6::integer) END
         FROM new_user
         RETURNING key_prefix
       )
       INSERT INTO admin_audit_log (actor_fingerprint, action, target_key_prefix, details)
       SELECT $7, 'key_created', key_prefix,
         jsonb_build_object('dailyLimit', $3::integer, 'requestsPerMinute', $4::integer,
           'maxConcurrentRequests', $5::integer, 'expiresInDays', $6::integer)
       FROM new_key`,
      [keyHash, keyPrefix, input.dailyLimit, input.requestsPerMinute, input.maxConcurrentRequests,
        input.expiresInDays, actorFingerprint],
    );
  }

  async updateQuota(keyPrefix: string, input: AdminQuotaInput, actorFingerprint: string): Promise<boolean> {
    const result = await this.db.query(
      `WITH updated AS (
         UPDATE gateway_keys SET
           daily_request_limit = $2,
           requests_per_minute = $3,
           max_concurrent_requests = $4
         WHERE key_prefix = $1
         RETURNING key_prefix
       ), audited AS (
         INSERT INTO admin_audit_log (actor_fingerprint, action, target_key_prefix, details)
         SELECT $5, 'quota_updated', key_prefix,
           jsonb_build_object('dailyLimit', $2::integer, 'requestsPerMinute', $3::integer,
             'maxConcurrentRequests', $4::integer)
         FROM updated
       ) SELECT key_prefix FROM updated`,
      [keyPrefix, input.dailyLimit, input.requestsPerMinute, input.maxConcurrentRequests, actorFingerprint],
    );
    return (result.rowCount ?? result.rows.length) > 0;
  }

  async revokeKey(keyPrefix: string, actorFingerprint: string): Promise<boolean> {
    const result = await this.db.query(
      `WITH revoked AS (
         UPDATE gateway_keys SET status = 'revoked', revoked_at = now()
         WHERE key_prefix = $1 AND status = 'active'
         RETURNING key_prefix
       ), audited AS (
         INSERT INTO admin_audit_log (actor_fingerprint, action, target_key_prefix)
         SELECT $2, 'key_revoked', key_prefix FROM revoked
       ) SELECT key_prefix FROM revoked`,
      [keyPrefix, actorFingerprint],
    );
    return (result.rowCount ?? result.rows.length) > 0;
  }
}

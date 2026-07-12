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

export interface AdminObservability {
  hourly: Array<{ hour: string; total: number; completed: number; failed: number; averageDurationMs: number | null }>;
  errors: Array<{ code: string; count: number }>;
  audit: Array<{ actor: string; action: string; targetPrefix: string | null; createdAt: string }>;
}

export interface AdminUpstreamStat {
  credentialId: string;
  attempts: number;
  failures: number;
  retryableAttempts: number;
  averageDurationMs: number | null;
}

export interface AdminDeployment {
  id: string;
  gitSha: string;
  status: string;
  startedAt: string;
  completedAt: string | null;
}

export interface AdminRepository {
  getSummary(): Promise<AdminSummary>;
  listKeys(): Promise<AdminKeyRow[]>;
  getObservability(): Promise<AdminObservability>;
  getUpstreamStats(): Promise<AdminUpstreamStat[]>;
  listDeployments(): Promise<AdminDeployment[]>;
  requestRollback(actorFingerprint: string): Promise<boolean>;
  createKey(input: AdminKeyInput, keyHash: string, keyPrefix: string, actorFingerprint: string): Promise<void>;
  updateQuota(keyPrefix: string, input: AdminQuotaInput, actorFingerprint: string): Promise<boolean>;
  revokeKey(keyPrefix: string, actorFingerprint: string): Promise<boolean>;
  recordLogin(success: boolean, actorFingerprint: string, ipFingerprint: string): Promise<void>;
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

  async getObservability(): Promise<AdminObservability> {
    const [hourlyResult, errorsResult, auditResult] = await Promise.all([
      this.db.query(`WITH hours AS (
        SELECT generate_series(date_trunc('hour', now()) - interval '23 hours', date_trunc('hour', now()), interval '1 hour') AS hour
      ) SELECT hours.hour, count(request.id) AS total,
        count(request.id) FILTER (WHERE request.status = 'completed') AS completed,
        count(request.id) FILTER (WHERE request.status = 'failed') AS failed,
        round(avg(extract(epoch FROM (request.ended_at - request.started_at)) * 1000)) AS average_duration_ms
      FROM hours LEFT JOIN user_requests request
        ON request.started_at >= hours.hour AND request.started_at < hours.hour + interval '1 hour'
      GROUP BY hours.hour ORDER BY hours.hour`),
      this.db.query(`SELECT COALESCE(status_code::text, error_class, 'unknown') AS code, count(*) AS count
        FROM user_requests
        WHERE started_at >= now() - interval '24 hours' AND status = 'failed'
        GROUP BY COALESCE(status_code::text, error_class, 'unknown')
        ORDER BY count(*) DESC, code LIMIT 10`),
      this.db.query(`SELECT actor_fingerprint, action, target_key_prefix, created_at
        FROM admin_audit_log ORDER BY created_at DESC LIMIT 50`),
    ]);
    return {
      hourly: hourlyResult.rows.map((row) => ({
        hour: row.hour instanceof Date ? row.hour.toISOString() : String(row.hour),
        total: Number(row.total ?? 0),
        completed: Number(row.completed ?? 0),
        failed: Number(row.failed ?? 0),
        averageDurationMs: row.average_duration_ms === null ? null : Number(row.average_duration_ms),
      })),
      errors: errorsResult.rows.map((row) => ({ code: String(row.code), count: Number(row.count ?? 0) })),
      audit: auditResult.rows.map((row) => ({
        actor: String(row.actor_fingerprint),
        action: String(row.action),
        targetPrefix: row.target_key_prefix ? String(row.target_key_prefix) : null,
        createdAt: row.created_at instanceof Date ? row.created_at.toISOString() : String(row.created_at),
      })),
    };
  }

  async getUpstreamStats(): Promise<AdminUpstreamStat[]> {
    const result = await this.db.query(`SELECT credential_fingerprint,
      count(*) AS attempts,
      count(*) FILTER (WHERE status_code >= 400 OR error_class IS NOT NULL) AS failures,
      count(*) FILTER (WHERE retryable) AS retryable_attempts,
      round(avg(extract(epoch FROM (ended_at - started_at)) * 1000)) AS average_duration_ms
    FROM upstream_attempts
    WHERE started_at >= now() - interval '24 hours'
    GROUP BY credential_fingerprint ORDER BY attempts DESC`);
    return result.rows.map((row) => ({
      credentialId: String(row.credential_fingerprint),
      attempts: Number(row.attempts ?? 0),
      failures: Number(row.failures ?? 0),
      retryableAttempts: Number(row.retryable_attempts ?? 0),
      averageDurationMs: row.average_duration_ms === null ? null : Number(row.average_duration_ms),
    }));
  }

  async listDeployments(): Promise<AdminDeployment[]> {
    const result = await this.db.query(`SELECT id, git_sha, status::text, started_at, completed_at
      FROM deployment_history ORDER BY started_at DESC LIMIT 20`);
    return result.rows.map((row) => ({
      id: String(row.id),
      gitSha: String(row.git_sha),
      status: String(row.status),
      startedAt: row.started_at instanceof Date ? row.started_at.toISOString() : String(row.started_at),
      completedAt: row.completed_at instanceof Date ? row.completed_at.toISOString() : row.completed_at ? String(row.completed_at) : null,
    }));
  }

  async requestRollback(actorFingerprint: string): Promise<boolean> {
    const result = await this.db.query(
      `WITH allowed AS (
         SELECT $1::text AS actor
         WHERE NOT EXISTS (SELECT 1 FROM deployment_control_requests WHERE status IN ('pending', 'processing'))
           AND EXISTS (SELECT 1 FROM deployment_history WHERE status = 'completed')
       ), requested AS (
         INSERT INTO deployment_control_requests (action, requested_by)
         SELECT 'rollback', actor FROM allowed RETURNING id
       ), audited AS (
         INSERT INTO admin_audit_log (actor_fingerprint, action, details)
         SELECT $1, 'rollback_requested', jsonb_build_object('requestId', id::text) FROM requested
       ) SELECT id FROM requested`,
      [actorFingerprint],
    );
    return (result.rowCount ?? result.rows.length) > 0;
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

  async recordLogin(success: boolean, actorFingerprint: string, ipFingerprint: string): Promise<void> {
    await this.db.query(
      `INSERT INTO admin_audit_log (actor_fingerprint, action, details)
       VALUES ($1, $2, jsonb_build_object('ipFingerprint', $3::text))`,
      [actorFingerprint, success ? "login_succeeded" : "login_failed", ipFingerprint],
    );
  }
}

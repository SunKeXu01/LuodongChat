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
}

interface AdminSqlClient {
  query(sql: string, values?: readonly unknown[]): Promise<{ rows: Array<Record<string, unknown>> }>;
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
}

export type RequestStatus = "in_progress" | "completed" | "failed";

export interface RequestStart {
  requestId: string;
  gatewayKeyHash: string;
  startedAt: Date;
}

export interface SqlClient {
  query(sql: string, values: readonly unknown[]): Promise<{ rowCount?: number | null }>;
}

export class PostgresRequestLedger implements RequestLedger {
  constructor(private readonly db: SqlClient) {}

  async startRequest(record: RequestStart): Promise<void> {
    await this.db.query(
      `INSERT INTO user_requests (id, user_id, gateway_key_id, status, started_at)
       SELECT $1::uuid, key.user_id, key.id, 'in_progress', $3::timestamptz
       FROM (SELECT 1) AS seed
       LEFT JOIN gateway_keys AS key ON key.key_hash = decode($2, 'hex')`,
      [record.requestId, record.gatewayKeyHash, record.startedAt],
    );
  }

  async recordAttempt(record: AttemptRecord): Promise<void> {
    await this.db.query(
      `INSERT INTO upstream_attempts
         (request_id, attempt_number, credential_id, credential_fingerprint, started_at, ended_at, status_code, error_class, retryable)
       VALUES
         ($1::uuid, $2, (SELECT id FROM upstream_credentials WHERE secret_fingerprint = $3), $3, $4, $5, $6, $7, $8)`,
      [record.requestId, record.attempt, record.credentialId, record.startedAt, record.endedAt,
        record.statusCode ?? null, record.errorClass ?? null, record.retryable],
    );
  }

  async finishRequest(record: RequestFinish): Promise<void> {
    const result = await this.db.query(
      `UPDATE user_requests
       SET status = $2::request_status, ended_at = $3, status_code = $4, attempt_count = $5, error_class = $6
       WHERE id = $1::uuid`,
      [record.requestId, record.status, record.endedAt, record.statusCode ?? null, record.attempts,
        record.errorClass ?? null],
    );
    if (result.rowCount === 0) throw new Error(`Unknown request: ${record.requestId}`);
  }
}

export interface AttemptRecord {
  requestId: string;
  attempt: number;
  credentialId: string;
  startedAt: Date;
  endedAt: Date;
  statusCode?: number;
  errorClass?: string;
  retryable: boolean;
}

export interface RequestFinish {
  requestId: string;
  status: Exclude<RequestStatus, "in_progress">;
  endedAt: Date;
  statusCode?: number;
  attempts: number;
  errorClass?: string;
}

export interface RequestLedger {
  startRequest(record: RequestStart): Promise<void>;
  recordAttempt(record: AttemptRecord): Promise<void>;
  finishRequest(record: RequestFinish): Promise<void>;
}

export class InMemoryRequestLedger implements RequestLedger {
  readonly requests = new Map<string, RequestStart & Partial<RequestFinish>>();
  readonly attempts: AttemptRecord[] = [];

  async startRequest(record: RequestStart): Promise<void> {
    this.requests.set(record.requestId, { ...record });
  }

  async recordAttempt(record: AttemptRecord): Promise<void> {
    this.attempts.push({ ...record });
  }

  async finishRequest(record: RequestFinish): Promise<void> {
    const existing = this.requests.get(record.requestId);
    if (!existing) throw new Error(`Unknown request: ${record.requestId}`);
    this.requests.set(record.requestId, { ...existing, ...record });
  }
}

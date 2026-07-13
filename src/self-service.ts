import { createHash, randomBytes, randomInt, timingSafeEqual } from "node:crypto";
import nodemailer from "nodemailer";
import { hashGatewayKey } from "./auth.js";

export interface SelfServiceKeyDefaults {
  dailyLimit: number;
  requestsPerMinute: number;
  maxConcurrentRequests: number;
  expiresInDays: number;
}

export interface EnrollmentRepository {
  createChallenge(identityHash: string, codeHash: string, ipFingerprint: string): Promise<"created" | "rate_limited">;
  cancelLatestChallenge(identityHash: string): Promise<void>;
  verifyChallenge(identityHash: string, codeHash: string, consume: boolean): Promise<"verified" | "invalid" | "expired" | "locked">;
  issueKey(identityHash: string, keyHash: string, keyPrefix: string, defaults: SelfServiceKeyDefaults, rotate: boolean): Promise<"created" | "active_key_exists">;
  hasActiveKey(identityHash: string): Promise<boolean>;
}

interface SqlClient {
  connect(): Promise<{ query(sql: string, values?: readonly unknown[]): Promise<{ rows: Array<Record<string, unknown>>; rowCount?: number | null }>; release(): void }>;
  query(sql: string, values?: readonly unknown[]): Promise<{ rows: Array<Record<string, unknown>>; rowCount?: number | null }>;
}

export class PostgresEnrollmentRepository implements EnrollmentRepository {
  constructor(private readonly db: SqlClient) {}

  async createChallenge(identityHash: string, codeHash: string, ipFingerprint: string): Promise<"created" | "rate_limited"> {
    const recent = await this.db.query(
      `SELECT
         count(*) FILTER (WHERE identity_hash = decode($1, 'hex')) AS identity_count,
         count(*) FILTER (WHERE ip_fingerprint = $2) AS ip_count,
         bool_or(identity_hash = decode($1, 'hex') AND created_at > now() - interval '60 seconds') AS too_soon
       FROM enrollment_challenges
       WHERE created_at > now() - interval '1 hour'`,
      [identityHash, ipFingerprint],
    );
    const row = recent.rows[0] ?? {};
    if (Boolean(row.too_soon) || Number(row.identity_count ?? 0) >= 5 || Number(row.ip_count ?? 0) >= 20) return "rate_limited";
    await this.db.query(
      `INSERT INTO enrollment_challenges (identity_hash, code_hash, ip_fingerprint, expires_at)
       VALUES (decode($1, 'hex'), decode($2, 'hex'), $3, now() + interval '10 minutes')`,
      [identityHash, codeHash, ipFingerprint],
    );
    return "created";
  }

  async cancelLatestChallenge(identityHash: string): Promise<void> {
    await this.db.query(
      `DELETE FROM enrollment_challenges WHERE id = (
         SELECT id FROM enrollment_challenges
         WHERE identity_hash = decode($1, 'hex') AND consumed_at IS NULL
         ORDER BY created_at DESC LIMIT 1
       )`, [identityHash],
    );
  }

  async verifyChallenge(identityHash: string, codeHash: string, consume: boolean): Promise<"verified" | "invalid" | "expired" | "locked"> {
    const client = await this.db.connect();
    try {
      await client.query("BEGIN");
      const result = await client.query(
        `SELECT id, encode(code_hash, 'hex') AS code_hash, expires_at, attempts
         FROM enrollment_challenges
         WHERE identity_hash = decode($1, 'hex') AND consumed_at IS NULL
         ORDER BY created_at DESC LIMIT 1 FOR UPDATE`,
        [identityHash],
      );
      const row = result.rows[0];
      if (!row) { await client.query("COMMIT"); return "invalid"; }
      if (Number(row.attempts) >= 5) { await client.query("COMMIT"); return "locked"; }
      if (new Date(String(row.expires_at)).getTime() <= Date.now()) { await client.query("COMMIT"); return "expired"; }
      const expected = Buffer.from(String(row.code_hash), "hex");
      const supplied = Buffer.from(codeHash, "hex");
      if (expected.length !== supplied.length || !timingSafeEqual(expected, supplied)) {
        await client.query("UPDATE enrollment_challenges SET attempts = attempts + 1 WHERE id = $1", [row.id]);
        await client.query("COMMIT");
        return Number(row.attempts) + 1 >= 5 ? "locked" : "invalid";
      }
      if (consume) await client.query("UPDATE enrollment_challenges SET consumed_at = now() WHERE id = $1", [row.id]);
      await client.query("COMMIT");
      return "verified";
    } catch (error) {
      await client.query("ROLLBACK").catch(() => undefined);
      throw error;
    } finally { client.release(); }
  }

  async hasActiveKey(identityHash: string): Promise<boolean> {
    const result = await this.db.query(
      `SELECT 1 FROM gateway_keys key JOIN users ON users.id = key.user_id
       WHERE users.identity_hash = decode($1, 'hex') AND key.status = 'active'
         AND (key.expires_at IS NULL OR key.expires_at > now()) LIMIT 1`, [identityHash],
    );
    return (result.rowCount ?? result.rows.length) > 0;
  }

  async issueKey(identityHash: string, keyHash: string, keyPrefix: string, defaults: SelfServiceKeyDefaults, rotate: boolean): Promise<"created" | "active_key_exists"> {
    const client = await this.db.connect();
    try {
      await client.query("BEGIN");
      const user = await client.query(
        `INSERT INTO users (identity_hash) VALUES (decode($1, 'hex'))
         ON CONFLICT (identity_hash) WHERE identity_hash IS NOT NULL
         DO UPDATE SET updated_at = now() RETURNING id`, [identityHash],
      );
      const userId = user.rows[0]!.id;
      const active = await client.query("SELECT 1 FROM gateway_keys WHERE user_id = $1 AND status = 'active' AND (expires_at IS NULL OR expires_at > now()) FOR UPDATE", [userId]);
      if ((active.rowCount ?? active.rows.length) > 0 && !rotate) { await client.query("ROLLBACK"); return "active_key_exists"; }
      if (rotate) await client.query("UPDATE gateway_keys SET status = 'revoked', revoked_at = now() WHERE user_id = $1 AND status = 'active'", [userId]);
      await client.query(
        `INSERT INTO gateway_keys (user_id, key_hash, key_prefix, daily_request_limit, requests_per_minute, max_concurrent_requests, expires_at)
         VALUES ($1, decode($2, 'hex'), $3, $4, $5, $6, now() + ($7 * interval '1 day'))`,
        [userId, keyHash, keyPrefix, defaults.dailyLimit, defaults.requestsPerMinute, defaults.maxConcurrentRequests, defaults.expiresInDays],
      );
      await client.query("COMMIT");
      return "created";
    } catch (error) {
      await client.query("ROLLBACK").catch(() => undefined);
      throw error;
    } finally { client.release(); }
  }
}

export interface EnrollmentMailer { sendCode(email: string, code: string): Promise<void> }

export class SmtpEnrollmentMailer implements EnrollmentMailer {
  private readonly transport;
  constructor(host: string, port: number, secure: boolean, user: string, password: string, private readonly from: string) {
    this.transport = nodemailer.createTransport({ host, port, secure, auth: { user, pass: password } });
  }
  async sendCode(email: string, code: string): Promise<void> {
    await this.transport.sendMail({
      from: this.from, to: email, subject: "ChatGPT 连接器验证码",
      text: `你的验证码是：${code}\n\n验证码 10 分钟内有效。若非本人操作，请忽略本邮件。`,
    });
  }
}

export class EnrollmentService {
  constructor(private readonly repository: EnrollmentRepository, private readonly mailer: EnrollmentMailer, private readonly defaults: SelfServiceKeyDefaults) {}
  static normalizeEmail(value: string): string | null {
    const normalized = value.trim().toLowerCase();
    return /^[^\s@]{1,64}@[^\s@]{1,190}$/.test(normalized) && normalized.length <= 254 ? normalized : null;
  }
  private static digest(value: string): string { return createHash("sha256").update(value).digest("hex"); }
  async requestCode(email: string, ipFingerprint: string): Promise<"sent" | "rate_limited"> {
    const code = String(randomInt(0, 1_000_000)).padStart(6, "0");
    const identityHash = EnrollmentService.digest(email);
    const status = await this.repository.createChallenge(identityHash, EnrollmentService.digest(code), ipFingerprint);
    if (status === "rate_limited") return status;
    try { await this.mailer.sendCode(email, code); }
    catch (error) {
      await this.repository.cancelLatestChallenge(identityHash).catch(() => undefined);
      throw error;
    }
    return "sent";
  }
  async verifyAndIssue(email: string, code: string, rotate: boolean): Promise<{ status: "created"; key: string; prefix: string } | { status: "invalid" | "expired" | "locked" | "active_key_exists" }> {
    const identityHash = EnrollmentService.digest(email);
    const hasActiveKey = await this.repository.hasActiveKey(identityHash);
    const verified = await this.repository.verifyChallenge(identityHash, EnrollmentService.digest(code), rotate || !hasActiveKey);
    if (verified !== "verified") return { status: verified };
    if (hasActiveKey && !rotate) return { status: "active_key_exists" };
    const key = `gw_${randomBytes(24).toString("hex")}`;
    const prefix = key.slice(0, 11);
    const issued = await this.repository.issueKey(identityHash, hashGatewayKey(key), prefix, this.defaults, rotate);
    return issued === "created" ? { status: "created", key, prefix } : { status: issued };
  }
}

import { createHash, randomBytes, randomInt, timingSafeEqual } from "node:crypto";
import nodemailer from "nodemailer";
import { hashGatewayKey } from "./auth.js";

export interface SelfServiceKeyDefaults {
  dailyLimit: number | null;
  requestsPerMinute: number;
  maxConcurrentRequests: number;
  expiresInDays: number | null;
}

export interface AccountProfile {
  id: string;
  email: string;
  nickname: string;
  avatarMediaType: string | null;
  avatarBase64: string | null;
  balanceMicrounits: number;
}

export interface EnrollmentRepository {
  createChallenge(identityHash: string, codeHash: string, ipFingerprint: string): Promise<"created" | "rate_limited">;
  cancelLatestChallenge(identityHash: string): Promise<void>;
  verifyChallenge(identityHash: string, codeHash: string, consume: boolean): Promise<"verified" | "invalid" | "expired" | "locked">;
  login(identityHash: string, email: string, sessionHash: string, keyHash: string, keyPrefix: string, defaults: SelfServiceKeyDefaults): Promise<AccountProfile | null>;
  authenticate(sessionHash: string): Promise<AccountProfile | null>;
  updateProfile(sessionHash: string, nickname: string): Promise<AccountProfile | null>;
  updateAvatar(sessionHash: string, mediaType: string, dataBase64: string): Promise<AccountProfile | null>;
  logout(sessionHash: string): Promise<void>;
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

  async login(identityHash: string, email: string, sessionHash: string, keyHash: string, keyPrefix: string, defaults: SelfServiceKeyDefaults): Promise<AccountProfile | null> {
    const client = await this.db.connect();
    try {
      await client.query("BEGIN");
      const user = await client.query(
        `INSERT INTO users (identity_hash, email, nickname) VALUES (decode($1, 'hex'), $2, $3)
         ON CONFLICT (identity_hash) WHERE identity_hash IS NOT NULL
         DO UPDATE SET email = EXCLUDED.email, nickname = COALESCE(users.nickname, EXCLUDED.nickname), updated_at = now()
         WHERE users.status = 'active'
         RETURNING id`, [identityHash, email, email.split("@")[0]!.slice(0, 20) || "GPT 用户"],
      );
      if (!user.rows[0]) { await client.query("ROLLBACK"); return null; }
      const userId = String(user.rows[0]!.id);
      await client.query(`INSERT INTO user_wallets (user_id) VALUES ($1) ON CONFLICT (user_id) DO NOTHING`, [userId]);
      await client.query(
        `INSERT INTO user_sessions (user_id, token_hash, expires_at)
         VALUES ($1, decode($2, 'hex'), now() + interval '30 days')`, [userId, sessionHash],
      );
      await client.query(
        `INSERT INTO gateway_keys (user_id, key_hash, key_prefix, daily_request_limit, requests_per_minute, max_concurrent_requests, expires_at)
         VALUES ($1, decode($2, 'hex'), $3, $4, $5, $6, NULL)
         ON CONFLICT (user_id) WHERE status = 'active' DO NOTHING`,
        [userId, keyHash, keyPrefix, defaults.dailyLimit, defaults.requestsPerMinute, defaults.maxConcurrentRequests],
      );
      const profile = await client.query(
        `SELECT users.id, users.email, users.nickname, users.avatar_media_type,
                encode(users.avatar_data, 'base64') AS avatar_base64, wallet.balance_microunits
         FROM users JOIN user_wallets wallet ON wallet.user_id = users.id WHERE users.id = $1`, [userId],
      );
      await client.query("COMMIT");
      return mapProfile(profile.rows[0]!);
    } catch (error) {
      await client.query("ROLLBACK").catch(() => undefined);
      throw error;
    } finally { client.release(); }
  }

  async authenticate(sessionHash: string): Promise<AccountProfile | null> {
    const result = await this.db.query(
      `UPDATE user_sessions session SET last_used_at = now()
       FROM users, user_wallets wallet
       WHERE session.token_hash = decode($1, 'hex') AND session.revoked_at IS NULL
         AND session.expires_at > now() AND users.id = session.user_id AND users.status = 'active'
         AND wallet.user_id = users.id
       RETURNING users.id, users.email, users.nickname, users.avatar_media_type,
                 encode(users.avatar_data, 'base64') AS avatar_base64, wallet.balance_microunits`, [sessionHash],
    );
    return result.rows[0] ? mapProfile(result.rows[0]) : null;
  }

  async updateProfile(sessionHash: string, nickname: string): Promise<AccountProfile | null> {
    const session = await this.db.query(
      `SELECT session.user_id FROM user_sessions session
       JOIN users ON users.id = session.user_id
       WHERE session.token_hash = decode($1, 'hex') AND session.revoked_at IS NULL
         AND session.expires_at > now() AND users.status = 'active'`, [sessionHash],
    );
    if (!session.rows[0]) return null;
    await this.db.query("UPDATE users SET nickname = $2, updated_at = now() WHERE id = $1", [session.rows[0].user_id, nickname]);
    return this.authenticate(sessionHash);
  }

  async updateAvatar(sessionHash: string, mediaType: string, dataBase64: string): Promise<AccountProfile | null> {
    const session = await this.db.query(
      `SELECT session.user_id FROM user_sessions session
       JOIN users ON users.id = session.user_id
       WHERE session.token_hash = decode($1, 'hex') AND session.revoked_at IS NULL
         AND session.expires_at > now() AND users.status = 'active'`, [sessionHash],
    );
    if (!session.rows[0]) return null;
    await this.db.query(
      "UPDATE users SET avatar_media_type = $2, avatar_data = decode($3, 'base64'), updated_at = now() WHERE id = $1",
      [session.rows[0].user_id, mediaType, dataBase64],
    );
    return this.authenticate(sessionHash);
  }

  async logout(sessionHash: string): Promise<void> {
    await this.db.query("UPDATE user_sessions SET revoked_at = now() WHERE token_hash = decode($1, 'hex')", [sessionHash]);
  }
}

function mapProfile(row: Record<string, unknown>): AccountProfile {
  return {
    id: String(row.id), email: String(row.email), nickname: String(row.nickname),
    avatarMediaType: row.avatar_media_type ? String(row.avatar_media_type) : null,
    avatarBase64: row.avatar_base64 ? String(row.avatar_base64).replace(/\s/g, "") : null,
    balanceMicrounits: Number(row.balance_microunits ?? 0),
  };
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
  async verifyAndLogin(email: string, code: string): Promise<
    { status: "authenticated"; accessToken: string; profile: AccountProfile }
    | { status: "invalid" | "expired" | "locked" | "disabled" }
  > {
    const identityHash = EnrollmentService.digest(email);
    const verified = await this.repository.verifyChallenge(identityHash, EnrollmentService.digest(code), true);
    if (verified !== "verified") return { status: verified };
    const accessToken = `usr_${randomBytes(32).toString("hex")}`;
    const internalKey = `gw_${randomBytes(24).toString("hex")}`;
    const profile = await this.repository.login(
      identityHash, email, hashGatewayKey(accessToken), hashGatewayKey(internalKey), internalKey.slice(0, 11), this.defaults,
    );
    if (!profile) return { status: "disabled" };
    return { status: "authenticated", accessToken, profile };
  }

  authenticate(accessToken: string): Promise<AccountProfile | null> { return this.repository.authenticate(hashGatewayKey(accessToken)); }
  getRequestLimits(): SelfServiceKeyDefaults { return { ...this.defaults }; }
  updateProfile(accessToken: string, nickname: string): Promise<AccountProfile | null> { return this.repository.updateProfile(hashGatewayKey(accessToken), nickname); }
  updateAvatar(accessToken: string, mediaType: string, dataBase64: string): Promise<AccountProfile | null> { return this.repository.updateAvatar(hashGatewayKey(accessToken), mediaType, dataBase64); }
  logout(accessToken: string): Promise<void> { return this.repository.logout(hashGatewayKey(accessToken)); }
}

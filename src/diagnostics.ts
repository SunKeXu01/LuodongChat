import { randomBytes } from "node:crypto";

export const MAX_DIAGNOSTIC_BYTES = 20 * 1024 * 1024;
export interface DiagnosticUploadInput { appVersion: string; platform: string; errorCode: string; manifest: Record<string, unknown>; data: Buffer }
export interface DiagnosticRecord { id: string; appVersion: string; platform: string; errorCode: string; packageSize: number; status: string; createdAt: string; expiresAt: string }
export interface DiagnosticRepository {
  create(userId: string, input: DiagnosticUploadInput): Promise<DiagnosticRecord>;
  list(userId: string): Promise<DiagnosticRecord[]>;
  delete(userId: string, id: string): Promise<boolean>;
  find(id: string): Promise<(DiagnosticRecord & { manifest: Record<string, unknown> }) | null>;
  purgeExpired(): Promise<number>;
}

interface SqlClient { query(sql: string, values?: readonly unknown[]): Promise<{ rows: Array<Record<string, unknown>>; rowCount?: number | null }> }

export class PostgresDiagnosticRepository implements DiagnosticRepository {
  constructor(private readonly db: SqlClient) {}
  async create(userId: string, input: DiagnosticUploadInput): Promise<DiagnosticRecord> {
    if (input.data.length < 1 || input.data.length > MAX_DIAGNOSTIC_BYTES) throw new Error("diagnostic_too_large");
    const id = `DG-${new Date().toISOString().slice(0, 10).replaceAll("-", "")}-${randomBytes(4).toString("hex").slice(0, 5).toUpperCase()}`;
    const result = await this.db.query(
      `INSERT INTO diagnostic_uploads (id,user_id,app_version,platform,error_code,manifest,package_data,package_size,status)
       VALUES ($1,$2,$3,$4,$5,$6::jsonb,$7,$8,'available')
       RETURNING id,app_version,platform,error_code,package_size,status,created_at,expires_at`,
      [id, userId, input.appVersion.slice(0, 40), input.platform.slice(0, 40), input.errorCode.slice(0, 80), JSON.stringify(input.manifest), input.data, input.data.length],
    );
    return mapRecord(result.rows[0]!);
  }
  async list(userId: string): Promise<DiagnosticRecord[]> {
    await this.purgeExpired();
    const result = await this.db.query(
      `SELECT id,app_version,platform,error_code,package_size,status,created_at,expires_at FROM diagnostic_uploads
       WHERE user_id=$1 AND deleted_at IS NULL ORDER BY created_at DESC LIMIT 50`, [userId]);
    return result.rows.map(mapRecord);
  }
  async delete(userId: string, id: string): Promise<boolean> {
    const result = await this.db.query(
      `UPDATE diagnostic_uploads SET status='deleted',deleted_at=now(),package_data='\\x'::bytea
       WHERE id=$1 AND user_id=$2 AND deleted_at IS NULL`, [id, userId]);
    return (result.rowCount ?? 0) > 0;
  }
  async find(id: string): Promise<(DiagnosticRecord & { manifest: Record<string, unknown> }) | null> {
    const result = await this.db.query(
      `SELECT id,app_version,platform,error_code,package_size,status,created_at,expires_at,manifest
       FROM diagnostic_uploads WHERE id=$1 AND deleted_at IS NULL AND expires_at>now()`, [id]);
    const row = result.rows[0];
    return row ? { ...mapRecord(row), manifest: (row.manifest ?? {}) as Record<string, unknown> } : null;
  }
  async purgeExpired(): Promise<number> {
    const result = await this.db.query(
      `UPDATE diagnostic_uploads SET status='deleted',deleted_at=now(),package_data='\\x'::bytea
       WHERE deleted_at IS NULL AND expires_at<=now()`);
    return result.rowCount ?? 0;
  }
}

function mapRecord(row: Record<string, unknown>): DiagnosticRecord {
  return { id:String(row.id), appVersion:String(row.app_version), platform:String(row.platform), errorCode:String(row.error_code),
    packageSize:Number(row.package_size), status:String(row.status), createdAt:new Date(String(row.created_at)).toISOString(), expiresAt:new Date(String(row.expires_at)).toISOString() };
}

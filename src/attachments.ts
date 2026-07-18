import { createHash, randomUUID } from "node:crypto";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { basename, extname, join } from "node:path";

export const MAX_DEFAULT_ATTACHMENT_BYTES = 20 * 1024 * 1024;
export const MAX_DOCUMENT_ATTACHMENT_BYTES = 40 * 1024 * 1024;
// This is also used as the HTTP request-body ceiling. Per-category validation
// below keeps images and other binary attachments at the smaller limit.
export const MAX_ATTACHMENT_BYTES = MAX_DOCUMENT_ATTACHMENT_BYTES;
export const MAX_ATTACHMENTS_TOTAL_BYTES = 48 * 1024 * 1024;
export const MAX_ATTACHMENTS_PER_OWNER = 10;
const ATTACHMENT_TTL_MS = 30 * 60 * 1000;

const EXTENSIONS = new Set([
  ".png", ".jpg", ".jpeg", ".webp", ".gif",
  ".mp4", ".mov", ".webm", ".mp3", ".wav", ".m4a", ".ogg",
  ".pdf", ".doc", ".docx", ".rtf", ".odt", ".ppt", ".pptx",
  ".xls", ".xlsx", ".csv", ".tsv", ".txt", ".md", ".json", ".xml", ".html",
  ".zip", ".rar", ".7z", ".tar", ".gz",
  ".cs", ".xaml", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".kt", ".go", ".rs",
  ".c", ".cpp", ".h", ".hpp", ".sql", ".sh", ".ps1", ".yml", ".yaml", ".toml", ".css", ".scss",
]);

const MIME_PREFIXES = ["image/", "video/", "audio/", "text/"];
const MIME_TYPES = new Set([
  "application/pdf", "application/json", "application/xml", "application/zip", "application/x-zip-compressed",
  "application/x-rar-compressed", "application/vnd.rar", "application/x-7z-compressed", "application/gzip",
  "application/msword", "application/rtf", "application/vnd.oasis.opendocument.text",
  "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  "application/vnd.ms-powerpoint", "application/vnd.openxmlformats-officedocument.presentationml.presentation",
  "application/vnd.ms-excel", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  "application/octet-stream",
]);

export interface StoredAttachment {
  id: string;
  owner: string;
  name: string;
  extension: string;
  mimeType: string;
  size: number;
  path: string;
  createdAt: number;
  expiresAt: number;
  digest: string;
}

export interface ResolvedAttachment {
  id: string;
  name: string;
  mimeType: string;
  size: number;
  data: Buffer;
}

function safeOriginalName(value: string): string {
  const cleaned = basename(value.replaceAll("\0", "")).replace(/[\u0000-\u001f\u007f]/g, "").trim();
  return (cleaned || "attachment").slice(0, 180);
}

function matchesSignature(extension: string, mimeType: string, data: Buffer): boolean {
  const starts = (...bytes: number[]) => bytes.every((value, index) => data[index] === value);
  if (extension === ".pdf") return starts(0x25, 0x50, 0x44, 0x46);
  if (extension === ".png") return starts(0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a);
  if (extension === ".jpg" || extension === ".jpeg") return starts(0xff, 0xd8, 0xff);
  if (extension === ".gif") return data.subarray(0, 6).toString("ascii") === "GIF87a" || data.subarray(0, 6).toString("ascii") === "GIF89a";
  if (extension === ".webp") return data.subarray(0, 4).toString("ascii") === "RIFF" && data.subarray(8, 12).toString("ascii") === "WEBP";
  if ([".docx", ".xlsx", ".pptx", ".odt", ".zip"].includes(extension)) return starts(0x50, 0x4b);
  if (extension === ".rar") return starts(0x52, 0x61, 0x72, 0x21);
  if (extension === ".7z") return starts(0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c);
  if (extension === ".gz") return starts(0x1f, 0x8b);
  if (mimeType.startsWith("image/") || mimeType === "application/pdf") return false;
  return true;
}

export function validateAttachment(nameValue: string, mimeValue: string, data: Buffer): { name: string; extension: string; mimeType: string } {
  const name = safeOriginalName(nameValue);
  const extension = extname(name).toLowerCase();
  const mimeType = (mimeValue || "application/octet-stream").toLowerCase().split(";")[0]!.trim();
  if (!extension || !EXTENSIONS.has(extension)) throw new Error("attachment_type_not_allowed");
  if (!MIME_PREFIXES.some((prefix) => mimeType.startsWith(prefix)) && !MIME_TYPES.has(mimeType))
    throw new Error("attachment_mime_not_allowed");
  if (data.length === 0) throw new Error("attachment_empty");
  const maximumBytes = attachmentCategory(mimeType, extension) === "document"
    ? MAX_DOCUMENT_ATTACHMENT_BYTES
    : MAX_DEFAULT_ATTACHMENT_BYTES;
  if (data.length > maximumBytes) throw new Error("attachment_too_large");
  if (!matchesSignature(extension, mimeType, data)) throw new Error("attachment_signature_mismatch");
  return { name, extension, mimeType };
}

export class AttachmentStore {
  private readonly items = new Map<string, StoredAttachment>();
  private readonly root: string;
  private readonly ready: Promise<void>;

  constructor(root = process.env.ATTACHMENT_TEMP_ROOT || join(tmpdir(), `luodong-chat-attachments-${process.pid}`)) {
    this.root = root;
    this.ready = rm(this.root, { recursive: true, force: true }).then(async () => { await mkdir(this.root, { recursive: true, mode: 0o700 }); });
  }

  async add(owner: string, nameValue: string, mimeValue: string, data: Buffer): Promise<StoredAttachment> {
    await this.ready;
    await this.cleanup();
    const active = [...this.items.values()].filter((item) => item.owner === owner).length;
    if (active >= MAX_ATTACHMENTS_PER_OWNER) throw new Error("attachment_count_exceeded");
    const validated = validateAttachment(nameValue, mimeValue, data);
    const digest = createHash("sha256").update(data).digest("hex");
    if ([...this.items.values()].some((item) => item.owner === owner && item.digest === digest)) throw new Error("attachment_duplicate");
    const id = `att_${randomUUID().replaceAll("-", "")}`;
    const path = join(this.root, id);
    await writeFile(path, data, { mode: 0o600, flag: "wx" });
    if ([...this.items.values()].filter((item) => item.owner === owner).length >= MAX_ATTACHMENTS_PER_OWNER) {
      await rm(path, { force: true });
      throw new Error("attachment_count_exceeded");
    }
    if ([...this.items.values()].some((item) => item.owner === owner && item.digest === digest)) {
      await rm(path, { force: true });
      throw new Error("attachment_duplicate");
    }
    const now = Date.now();
    const item: StoredAttachment = { id, owner, path, size: data.length, digest, createdAt: now, expiresAt: now + ATTACHMENT_TTL_MS, ...validated };
    this.items.set(id, item);
    return item;
  }

  async resolveMany(owner: string, ids: string[]): Promise<ResolvedAttachment[]> {
    await this.ready;
    await this.cleanup();
    if (ids.length > MAX_ATTACHMENTS_PER_OWNER) throw new Error("attachment_count_exceeded");
    const unique = new Set(ids);
    if (unique.size !== ids.length) throw new Error("attachment_duplicate");
    const resolved: ResolvedAttachment[] = [];
    for (const id of ids) {
      const item = this.items.get(id);
      if (!item || item.owner !== owner) throw new Error("attachment_not_found");
      resolved.push({ id, name: item.name, mimeType: item.mimeType, size: item.size, data: await readFile(item.path) });
    }
    return resolved;
  }

  async remove(owner: string, id: string): Promise<boolean> {
    await this.ready;
    const item = this.items.get(id);
    if (!item || item.owner !== owner) return false;
    this.items.delete(id);
    await rm(item.path, { force: true });
    return true;
  }

  async cleanup(now = Date.now()): Promise<void> {
    await this.ready;
    const expired = [...this.items.values()].filter((item) => item.expiresAt <= now);
    await Promise.all(expired.map(async (item) => {
      this.items.delete(item.id);
      await rm(item.path, { force: true });
    }));
  }

  async close(): Promise<void> {
    await this.ready;
    this.items.clear();
    await rm(this.root, { recursive: true, force: true });
  }
}

export function attachmentCategory(mimeType: string, extension: string): "image" | "video" | "audio" | "document" | "archive" | "code" | "other" {
  if (mimeType.startsWith("image/")) return "image";
  if (mimeType.startsWith("video/")) return "video";
  if (mimeType.startsWith("audio/")) return "audio";
  if ([".zip", ".rar", ".7z", ".tar", ".gz"].includes(extension)) return "archive";
  if ([".cs", ".xaml", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".kt", ".go", ".rs", ".c", ".cpp", ".h", ".hpp", ".sql", ".sh", ".ps1", ".yml", ".yaml", ".toml", ".css", ".scss"].includes(extension)) return "code";
  if ([".pdf", ".doc", ".docx", ".rtf", ".odt", ".ppt", ".pptx", ".xls", ".xlsx", ".csv", ".tsv", ".txt", ".md", ".json", ".xml", ".html"].includes(extension)) return "document";
  return "other";
}

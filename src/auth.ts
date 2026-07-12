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

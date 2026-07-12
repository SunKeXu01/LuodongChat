export interface GatewayConfig {
  port: number;
  upstreamBaseUrl: string;
  upstreamApiKey: string;
  upstreamApiKeys: readonly string[];
  upstreamResponsesPath: string;
  gatewayKeyHashes: ReadonlySet<string>;
  adminKeyHashes?: ReadonlySet<string>;
  requestsPerMinute: number;
  maxConcurrentRequests: number;
  upstreamTimeoutMs: number;
  version?: string;
  selfService?: {
    smtpHost: string;
    smtpPort: number;
    smtpSecure: boolean;
    smtpUser: string;
    smtpPassword: string;
    smtpFrom: string;
    dailyLimit: number;
    requestsPerMinute: number;
    maxConcurrentRequests: number;
    expiresInDays: number;
  };
}

function positiveInteger(name: string, fallback: number): number {
  const raw = process.env[name];
  const value = raw === undefined ? fallback : Number(raw);
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`${name} must be a positive integer`);
  }
  return value;
}

export function loadConfig(): GatewayConfig {
  const upstreamBaseUrl = process.env.UPSTREAM_BASE_URL?.replace(/\/$/, "");
  const upstreamApiKey = process.env.UPSTREAM_API_KEY;
  const upstreamResponsesPath = process.env.UPSTREAM_RESPONSES_PATH ?? "/v1/responses";
  if (!upstreamBaseUrl) throw new Error("UPSTREAM_BASE_URL is required");
  if (!upstreamApiKey) throw new Error("UPSTREAM_API_KEY is required");
  const upstreamApiKeys = (process.env.UPSTREAM_API_KEYS?.trim() || upstreamApiKey)
    .split(",")
    .map((value) => value.trim())
    .filter(Boolean);
  if (!upstreamResponsesPath.startsWith("/")) throw new Error("UPSTREAM_RESPONSES_PATH must start with /");

  const gatewayKeyHashes = new Set(
    (process.env.GATEWAY_KEY_HASHES ?? "")
      .split(",")
      .map((value) => value.trim().toLowerCase())
      .filter(Boolean),
  );
  if (gatewayKeyHashes.size === 0) {
    throw new Error("GATEWAY_KEY_HASHES must contain at least one SHA-256 hash");
  }
  const adminKeyHashes = new Set(
    (process.env.ADMIN_KEY_HASHES ?? "")
      .split(",")
      .map((value) => value.trim().toLowerCase())
      .filter((value) => /^[a-f\d]{64}$/.test(value)),
  );

  const selfServiceEnabled = process.env.SELF_SERVICE_ENABLED?.toLowerCase() === "true";
  const smtpHost = process.env.SMTP_HOST?.trim();
  const smtpUser = process.env.SMTP_USERNAME?.trim();
  const smtpPassword = process.env.SMTP_AUTH_CODE;
  const smtpFrom = process.env.SMTP_FROM?.trim();
  if (selfServiceEnabled && (!process.env.DATABASE_URL || !smtpHost || !smtpUser || !smtpPassword || !smtpFrom)) {
    throw new Error("Self-service requires DATABASE_URL and SMTP_HOST/SMTP_USERNAME/SMTP_AUTH_CODE/SMTP_FROM");
  }

  return {
    port: positiveInteger("PORT", 8787),
    upstreamBaseUrl,
    upstreamApiKey,
    upstreamApiKeys,
    upstreamResponsesPath,
    gatewayKeyHashes,
    adminKeyHashes,
    requestsPerMinute: positiveInteger("REQUESTS_PER_MINUTE", 30),
    maxConcurrentRequests: positiveInteger("MAX_CONCURRENT_REQUESTS", 2),
    upstreamTimeoutMs: positiveInteger("UPSTREAM_TIMEOUT_MS", 300_000),
    version: process.env.APP_VERSION?.trim() || "development",
    selfService: selfServiceEnabled ? {
      smtpHost: smtpHost!,
      smtpPort: positiveInteger("SMTP_PORT", 465),
      smtpSecure: process.env.SMTP_SECURE?.toLowerCase() !== "false",
      smtpUser: smtpUser!,
      smtpPassword: smtpPassword!,
      smtpFrom: smtpFrom!,
      dailyLimit: positiveInteger("SELF_SERVICE_DAILY_LIMIT", 100),
      requestsPerMinute: positiveInteger("SELF_SERVICE_REQUESTS_PER_MINUTE", 30),
      maxConcurrentRequests: positiveInteger("SELF_SERVICE_MAX_CONCURRENT", 2),
      expiresInDays: positiveInteger("SELF_SERVICE_KEY_EXPIRES_DAYS", 30),
    } : undefined,
  };
}

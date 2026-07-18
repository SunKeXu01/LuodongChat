export interface GatewayConfig {
  port: number;
  upstreamBaseUrl: string;
  upstreamApiKey: string;
  upstreamApiKeys: readonly string[];
  upstreamResponsesPath: string;
  upstreams?: readonly UpstreamEndpoint[];
  webSearchUpstreamIndex?: number;
  imageGenerationUpstreamIndex?: number;
  imageGeneration?: { baseUrl: string; apiKey: string; model: string };
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
    dailyLimit: number | null;
    requestsPerMinute: number;
    maxConcurrentRequests: number;
    expiresInDays: number | null;
  };
}

export interface UpstreamEndpoint {
  baseUrl: string;
  responsesPath: string;
  apiKey: string;
  model?: string;
  supportsWebSearch?: boolean;
  supportsImageGeneration?: boolean;
}

function optionalBoolean(value: unknown, name: string): boolean | undefined {
  if (value === undefined || value === null || value === "") return undefined;
  if (typeof value === "boolean") return value;
  if (typeof value === "string" && /^(true|false)$/i.test(value.trim())) return value.trim().toLowerCase() === "true";
  throw new Error(`${name} must be true or false`);
}

function parseAdditionalUpstreams(raw: string | undefined): UpstreamEndpoint[] {
  if (!raw?.trim()) return [];
  let value: unknown;
  try { value = JSON.parse(raw); }
  catch { throw new Error("UPSTREAM_ENDPOINTS_JSON must be valid JSON"); }
  if (!Array.isArray(value)) throw new Error("UPSTREAM_ENDPOINTS_JSON must be a JSON array");
  return value.map((item, index) => {
    if (!item || typeof item !== "object") throw new Error(`UPSTREAM_ENDPOINTS_JSON[${index}] must be an object`);
    const record = item as Record<string, unknown>;
    const baseUrl = typeof record.baseUrl === "string" ? record.baseUrl.trim().replace(/\/$/, "") : "";
    const responsesPath = typeof record.responsesPath === "string" ? record.responsesPath.trim() : "/v1/responses";
    const apiKey = typeof record.apiKey === "string" ? record.apiKey.trim() : "";
    const model = typeof record.model === "string" ? record.model.trim() : undefined;
    const supportsWebSearch = optionalBoolean(record.supportsWebSearch, `UPSTREAM_ENDPOINTS_JSON[${index}].supportsWebSearch`);
    const supportsImageGeneration = optionalBoolean(record.supportsImageGeneration, `UPSTREAM_ENDPOINTS_JSON[${index}].supportsImageGeneration`);
    let parsedUrl: URL;
    try { parsedUrl = new URL(baseUrl); }
    catch { throw new Error(`UPSTREAM_ENDPOINTS_JSON[${index}].baseUrl must be an absolute URL`); }
    if (!['http:', 'https:'].includes(parsedUrl.protocol)) throw new Error(`UPSTREAM_ENDPOINTS_JSON[${index}].baseUrl must use HTTP or HTTPS`);
    if (!responsesPath.startsWith("/")) throw new Error(`UPSTREAM_ENDPOINTS_JSON[${index}].responsesPath must start with /`);
    if (!apiKey) throw new Error(`UPSTREAM_ENDPOINTS_JSON[${index}].apiKey is required`);
    return {
      baseUrl,
      responsesPath,
      apiKey,
      ...(model ? { model } : {}),
      ...(supportsWebSearch === undefined ? {} : { supportsWebSearch }),
      ...(supportsImageGeneration === undefined ? {} : { supportsImageGeneration }),
    };
  });
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
  const primaryModel = process.env.UPSTREAM_MODEL?.trim();
  const primarySupportsWebSearch = optionalBoolean(process.env.UPSTREAM_SUPPORTS_WEB_SEARCH, "UPSTREAM_SUPPORTS_WEB_SEARCH");
  const primarySupportsImageGeneration = optionalBoolean(process.env.UPSTREAM_SUPPORTS_IMAGE_GENERATION, "UPSTREAM_SUPPORTS_IMAGE_GENERATION");
  const upstreams: UpstreamEndpoint[] = [
    ...upstreamApiKeys.map((apiKey) => ({
      baseUrl: upstreamBaseUrl,
      responsesPath: upstreamResponsesPath,
      apiKey,
      ...(primaryModel ? { model: primaryModel } : {}),
      ...(primarySupportsWebSearch === undefined ? {} : { supportsWebSearch: primarySupportsWebSearch }),
      ...(primarySupportsImageGeneration === undefined ? {} : { supportsImageGeneration: primarySupportsImageGeneration }),
    })),
    ...parseAdditionalUpstreams(process.env.UPSTREAM_ENDPOINTS_JSON),
  ];
  const configuredWebSearchIndex = process.env.WEB_SEARCH_UPSTREAM_INDEX;
  const webSearchUpstreamIndex = configuredWebSearchIndex === undefined
    ? Math.max(0, upstreams.length - 1)
    : Number(configuredWebSearchIndex);
  if (!Number.isSafeInteger(webSearchUpstreamIndex) || webSearchUpstreamIndex < 0 || webSearchUpstreamIndex >= upstreams.length) {
    throw new Error("WEB_SEARCH_UPSTREAM_INDEX must identify a configured upstream");
  }
  const configuredImageIndex = process.env.IMAGE_GENERATION_UPSTREAM_INDEX;
  const imageGenerationUpstreamIndex = configuredImageIndex === undefined
    ? Math.max(0, upstreams.length - 1)
    : Number(configuredImageIndex);
  if (!Number.isSafeInteger(imageGenerationUpstreamIndex) || imageGenerationUpstreamIndex < 0 || imageGenerationUpstreamIndex >= upstreams.length) {
    throw new Error("IMAGE_GENERATION_UPSTREAM_INDEX must identify a configured upstream");
  }

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
    upstreams,
    webSearchUpstreamIndex,
    imageGenerationUpstreamIndex,
    imageGeneration: process.env.IMAGE_API_BASE_URL?.trim() && process.env.IMAGE_API_KEY?.trim() ? {
      baseUrl: process.env.IMAGE_API_BASE_URL.trim().replace(/\/$/, ""),
      apiKey: process.env.IMAGE_API_KEY.trim(),
      model: process.env.IMAGE_API_MODEL?.trim() || "gpt-image-2",
    } : undefined,
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
      dailyLimit: null,
      requestsPerMinute: positiveInteger("SELF_SERVICE_REQUESTS_PER_MINUTE", 30),
      maxConcurrentRequests: positiveInteger("SELF_SERVICE_MAX_CONCURRENT", 2),
      expiresInDays: null,
    } : undefined,
  };
}

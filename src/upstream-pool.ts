import { createHash } from "node:crypto";

export type HealthState = "healthy" | "degraded" | "open" | "half_open";

export interface UpstreamCredential {
  id: string;
  apiKey: string;
  baseUrl?: string;
  responsesPath?: string;
}

interface CredentialState extends UpstreamCredential {
  health: HealthState;
  consecutiveFailures: number;
  openUntil: number;
  inFlight: number;
}

export interface CredentialSnapshot {
  id: string;
  health: HealthState;
  consecutiveFailures: number;
  inFlight: number;
}

export class UpstreamPool {
  private readonly credentials: CredentialState[];
  private cursor = 0;

  constructor(
    credentials: readonly (string | Omit<UpstreamCredential, "id">)[],
    private readonly failureThreshold = 3,
    private readonly cooldownMs = 30_000,
  ) {
    if (credentials.length === 0) throw new Error("At least one upstream credential is required");
    this.credentials = credentials.map((credential) => {
      const { apiKey, baseUrl, responsesPath } = typeof credential === "string"
        ? { apiKey: credential, baseUrl: undefined, responsesPath: undefined }
        : credential;
      return {
      id: createHash("sha256").update(apiKey).digest("hex"),
      apiKey,
      baseUrl,
      responsesPath,
      health: "healthy",
      consecutiveFailures: 0,
      openUntil: 0,
      inFlight: 0,
      };
    });
  }

  acquire(excludedIds: ReadonlySet<string> = new Set(), now = Date.now()): UpstreamCredential | null {
    for (const credential of this.credentials) {
      if (credential.health === "open" && now >= credential.openUntil) credential.health = "half_open";
    }

    const eligible = this.credentials.filter(
      (item) => item.health !== "open" && !excludedIds.has(item.id),
    );
    if (eligible.length === 0) return null;
    eligible.sort((a, b) => a.inFlight - b.inFlight);
    const minimumLoad = eligible[0]?.inFlight ?? 0;
    const leastLoaded = eligible.filter((item) => item.inFlight === minimumLoad);
    const selected = leastLoaded[this.cursor % leastLoaded.length];
    if (!selected) return null;
    this.cursor += 1;
    selected.inFlight += 1;
    return {
      id: selected.id,
      apiKey: selected.apiKey,
      baseUrl: selected.baseUrl,
      responsesPath: selected.responsesPath,
    };
  }

  recordSuccess(id: string): void {
    const item = this.find(id);
    item.inFlight = Math.max(0, item.inFlight - 1);
    item.consecutiveFailures = 0;
    item.openUntil = 0;
    item.health = "healthy";
  }

  recordFailure(id: string, retryable: boolean, now = Date.now()): void {
    const item = this.find(id);
    item.inFlight = Math.max(0, item.inFlight - 1);
    if (!retryable) return;
    item.consecutiveFailures += 1;
    if (item.consecutiveFailures >= this.failureThreshold) {
      item.health = "open";
      item.openUntil = now + this.cooldownMs;
    } else {
      item.health = "degraded";
    }
  }

  recordFatalFailure(id: string): void {
    const item = this.find(id);
    item.inFlight = Math.max(0, item.inFlight - 1);
    item.consecutiveFailures = this.failureThreshold;
    item.health = "open";
    item.openUntil = Number.POSITIVE_INFINITY;
  }

  snapshot(): CredentialSnapshot[] {
    return this.credentials.map(({ id, health, consecutiveFailures, inFlight }) => ({
      id,
      health,
      consecutiveFailures,
      inFlight,
    }));
  }

  private find(id: string): CredentialState {
    const item = this.credentials.find((credential) => credential.id === id);
    if (!item) throw new Error(`Unknown upstream credential: ${id}`);
    return item;
  }
}

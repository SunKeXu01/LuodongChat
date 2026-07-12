interface WindowState {
  startedAt: number;
  requests: number;
  concurrent: number;
  dailyStartedAt: number;
  dailyRequests: number;
}

export type LimitPermit = { ok: true; release: () => void | Promise<void> } | { ok: false; reason: "rate" | "concurrency" | "daily" };

export interface RequestLimitOptions {
  requestsPerMinute?: number;
  maxConcurrent?: number;
  now?: number;
  dailyLimit?: number;
}

export interface RequestLimiter {
  acquire(subject: string, options?: RequestLimitOptions): LimitPermit | Promise<LimitPermit>;
}

export class InMemoryLimiter implements RequestLimiter {
  private readonly states = new Map<string, WindowState>();

  constructor(
    private readonly requestsPerMinute: number,
    private readonly maxConcurrent: number,
  ) {}

  acquire(subject: string, options: RequestLimitOptions = {}): LimitPermit {
    const now = options.now ?? Date.now();
    const requestsPerMinute = options.requestsPerMinute ?? this.requestsPerMinute;
    const maxConcurrent = options.maxConcurrent ?? this.maxConcurrent;
    let state = this.states.get(subject);
    if (!state || now - state.startedAt >= 60_000) {
      state = {
        startedAt: now,
        requests: 0,
        concurrent: state?.concurrent ?? 0,
        dailyStartedAt: state?.dailyStartedAt ?? Math.floor(now / 86_400_000) * 86_400_000,
        dailyRequests: state?.dailyRequests ?? 0,
      };
      this.states.set(subject, state);
    }
    const dayStart = Math.floor(now / 86_400_000) * 86_400_000;
    if (state.dailyStartedAt !== dayStart) {
      state.dailyStartedAt = dayStart;
      state.dailyRequests = 0;
    }
    if (state.concurrent >= maxConcurrent) return { ok: false, reason: "concurrency" };
    if (state.requests >= requestsPerMinute) return { ok: false, reason: "rate" };
    if (options.dailyLimit !== undefined && state.dailyRequests >= options.dailyLimit) return { ok: false, reason: "daily" };

    state.requests += 1;
    state.dailyRequests += 1;
    state.concurrent += 1;
    let released = false;
    return {
      ok: true,
      release: () => {
        if (released) return;
        released = true;
        state.concurrent = Math.max(0, state.concurrent - 1);
      },
    };
  }
}

export interface RedisScriptClient {
  eval(script: string, options: { keys: string[]; arguments: string[] }): Promise<number>;
}

const ACQUIRE_SCRIPT = `
local requests = tonumber(redis.call('GET', KEYS[1]) or '0')
local concurrent = tonumber(redis.call('GET', KEYS[2]) or '0')
local daily = tonumber(redis.call('GET', KEYS[3]) or '0')
if requests >= tonumber(ARGV[1]) then return -1 end
if concurrent >= tonumber(ARGV[2]) then return -2 end
if tonumber(ARGV[5]) > 0 and daily >= tonumber(ARGV[5]) then return -3 end
redis.call('INCR', KEYS[1])
redis.call('PEXPIRE', KEYS[1], ARGV[3])
redis.call('INCR', KEYS[2])
redis.call('PEXPIRE', KEYS[2], ARGV[4])
if tonumber(ARGV[5]) > 0 then
  redis.call('INCR', KEYS[3])
  redis.call('PEXPIRE', KEYS[3], ARGV[6])
end
return 1
`;

const RELEASE_SCRIPT = `
local concurrent = tonumber(redis.call('GET', KEYS[1]) or '0')
if concurrent <= 1 then
  redis.call('DEL', KEYS[1])
  return 0
end
return redis.call('DECR', KEYS[1])
`;

export class RedisRequestLimiter implements RequestLimiter {
  constructor(
    private readonly redis: RedisScriptClient,
    private readonly requestsPerMinute: number,
    private readonly maxConcurrent: number,
    private readonly concurrencyTtlMs = 600_000,
  ) {}

  async acquire(subject: string, options: RequestLimitOptions = {}): Promise<LimitPermit> {
    const tag = `{${subject}}`;
    const requestKey = `gateway:${tag}:requests`;
    const concurrencyKey = `gateway:${tag}:concurrent`;
    const dailyKey = `gateway:${tag}:daily`;
    const now = options.now ?? Date.now();
    const dailyTtlMs = 86_400_000 - (now % 86_400_000);
    const result = await this.redis.eval(ACQUIRE_SCRIPT, {
      keys: [requestKey, concurrencyKey, dailyKey],
      arguments: [
        String(options.requestsPerMinute ?? this.requestsPerMinute),
        String(options.maxConcurrent ?? this.maxConcurrent),
        "60000",
        String(this.concurrencyTtlMs),
        String(options.dailyLimit ?? 0),
        String(dailyTtlMs),
      ],
    });
    if (result === -1) return { ok: false, reason: "rate" };
    if (result === -2) return { ok: false, reason: "concurrency" };
    if (result === -3) return { ok: false, reason: "daily" };
    if (result !== 1) throw new Error(`Unexpected Redis limiter result: ${result}`);

    let released = false;
    return {
      ok: true,
      release: async () => {
        if (released) return;
        released = true;
        await this.redis.eval(RELEASE_SCRIPT, { keys: [concurrencyKey], arguments: [] });
      },
    };
  }
}

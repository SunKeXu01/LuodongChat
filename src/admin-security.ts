interface AttemptState {
  failures: number[];
  blockedUntil: number;
}

export class AdminLoginGuard {
  private readonly attempts = new Map<string, AttemptState>();

  constructor(
    private readonly maxFailures = 5,
    private readonly windowMs = 15 * 60_000,
    private readonly blockMs = 30 * 60_000,
  ) {}

  retryAfterSeconds(subject: string, now = Date.now()): number {
    const state = this.attempts.get(subject);
    if (!state || state.blockedUntil <= now) return 0;
    return Math.ceil((state.blockedUntil - now) / 1_000);
  }

  recordFailure(subject: string, now = Date.now()): number {
    const state = this.attempts.get(subject) ?? { failures: [], blockedUntil: 0 };
    state.failures = state.failures.filter((timestamp) => now - timestamp < this.windowMs);
    state.failures.push(now);
    if (state.failures.length >= this.maxFailures) {
      state.blockedUntil = now + this.blockMs;
      state.failures = [];
    }
    this.attempts.set(subject, state);
    return this.retryAfterSeconds(subject, now);
  }

  recordSuccess(subject: string): void {
    this.attempts.delete(subject);
  }
}

export interface AdminLoginProtector {
  retryAfterSeconds(subject: string, now?: number): number | Promise<number>;
  recordFailure(subject: string, now?: number): number | Promise<number>;
  recordSuccess(subject: string): void | Promise<void>;
}

interface AdminRedisClient {
  eval(script: string, options: { keys: string[]; arguments: string[] }): Promise<number>;
}

const RETRY_AFTER_SCRIPT = `
local ttl = redis.call('PTTL', KEYS[1])
if ttl <= 0 then return 0 end
return math.ceil(ttl / 1000)
`;

const FAILURE_SCRIPT = `
local block_ttl = redis.call('PTTL', KEYS[2])
if block_ttl > 0 then return math.ceil(block_ttl / 1000) end
local failures = redis.call('INCR', KEYS[1])
if failures == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
if failures >= tonumber(ARGV[2]) then
  redis.call('DEL', KEYS[1])
  redis.call('SET', KEYS[2], '1', 'PX', ARGV[3])
  return math.ceil(tonumber(ARGV[3]) / 1000)
end
return 0
`;

const SUCCESS_SCRIPT = `return redis.call('DEL', KEYS[1], KEYS[2])`;

export class RedisAdminLoginGuard implements AdminLoginProtector {
  constructor(
    private readonly redis: AdminRedisClient,
    private readonly maxFailures = 5,
    private readonly windowMs = 15 * 60_000,
    private readonly blockMs = 30 * 60_000,
  ) {}

  async retryAfterSeconds(subject: string): Promise<number> {
    return this.redis.eval(RETRY_AFTER_SCRIPT, {
      keys: [`admin-login:${subject}:blocked`], arguments: [],
    });
  }

  async recordFailure(subject: string): Promise<number> {
    return this.redis.eval(FAILURE_SCRIPT, {
      keys: [`admin-login:${subject}:failures`, `admin-login:${subject}:blocked`],
      arguments: [String(this.windowMs), String(this.maxFailures), String(this.blockMs)],
    });
  }

  async recordSuccess(subject: string): Promise<void> {
    await this.redis.eval(SUCCESS_SCRIPT, {
      keys: [`admin-login:${subject}:failures`, `admin-login:${subject}:blocked`], arguments: [],
    });
  }
}

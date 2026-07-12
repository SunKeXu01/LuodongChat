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

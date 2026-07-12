import pg from "pg";
import { createClient } from "redis";
import type { GatewayConfig } from "./config.js";
import { PostgresRequestLedger, type RequestLedger, type SqlClient } from "./ledger.js";
import { RedisRequestLimiter, type RedisScriptClient, type RequestLimiter } from "./limiter.js";

export interface RuntimeDependencies {
  ledger?: RequestLedger;
  limiter?: RequestLimiter;
  close(): Promise<void>;
}

export async function createRuntimeDependencies(config: GatewayConfig): Promise<RuntimeDependencies> {
  const closers: Array<() => Promise<void>> = [];
  let ledger: RequestLedger | undefined;
  let limiter: RequestLimiter | undefined;

  if (process.env.DATABASE_URL) {
    const pool = new pg.Pool({ connectionString: process.env.DATABASE_URL, max: 10 });
    await pool.query("SELECT 1");
    ledger = new PostgresRequestLedger(pool as SqlClient);
    closers.push(async () => { await pool.end(); });
  }

  if (process.env.REDIS_URL) {
    const redis = createClient({ url: process.env.REDIS_URL });
    redis.on("error", (error) => console.error(JSON.stringify({ event: "redis_error", error: error.name })));
    await redis.connect();
    limiter = new RedisRequestLimiter(redis as unknown as RedisScriptClient, config.requestsPerMinute, config.maxConcurrentRequests);
    closers.push(async () => { await redis.quit(); });
  }

  return {
    ledger,
    limiter,
    close: async () => {
      for (const close of closers.reverse()) await close();
    },
  };
}

import pg from "pg";
import { createClient } from "redis";
import type { GatewayConfig } from "./config.js";
import { PostgresRequestLedger, type RequestLedger, type SqlClient } from "./ledger.js";
import { RedisRequestLimiter, type RedisScriptClient, type RequestLimiter } from "./limiter.js";
import {
  PostgresGatewayKeyLimitProvider,
  PostgresGatewayKeyVerifier,
  StaticGatewayKeyVerifier,
  type GatewayKeyLimitProvider,
  type GatewayKeyVerifier,
} from "./auth.js";
import { PostgresAdminRepository, type AdminRepository } from "./admin.js";
import { RedisAdminLoginGuard, type AdminLoginProtector } from "./admin-security.js";
import { EnrollmentService, PostgresEnrollmentRepository, SmtpEnrollmentMailer } from "./self-service.js";
import { PostgresDiagnosticRepository, type DiagnosticRepository } from "./diagnostics.js";

export interface RuntimeDependencies {
  ledger?: RequestLedger;
  limiter?: RequestLimiter;
  keyVerifier?: GatewayKeyVerifier;
  keyLimitProvider?: GatewayKeyLimitProvider;
  adminRepository?: AdminRepository;
  adminLoginProtector?: AdminLoginProtector;
  enrollmentService?: EnrollmentService;
  diagnosticRepository?: DiagnosticRepository;
  close(): Promise<void>;
}

export async function createRuntimeDependencies(config: GatewayConfig): Promise<RuntimeDependencies> {
  const closers: Array<() => Promise<void>> = [];
  let ledger: RequestLedger | undefined;
  let limiter: RequestLimiter | undefined;
  let keyVerifier: GatewayKeyVerifier | undefined;
  let keyLimitProvider: GatewayKeyLimitProvider | undefined;
  let adminRepository: AdminRepository | undefined;
  let adminLoginProtector: AdminLoginProtector | undefined;
  let enrollmentService: EnrollmentService | undefined;
  let diagnosticRepository: DiagnosticRepository | undefined;

  if (process.env.DATABASE_URL) {
    const pool = new pg.Pool({ connectionString: process.env.DATABASE_URL, max: 10 });
    await pool.query("SELECT 1");
    ledger = new PostgresRequestLedger(pool as SqlClient);
    keyVerifier = new PostgresGatewayKeyVerifier(pool, new StaticGatewayKeyVerifier(config.gatewayKeyHashes));
    keyLimitProvider = new PostgresGatewayKeyLimitProvider(pool, config.requestsPerMinute, config.maxConcurrentRequests);
    adminRepository = new PostgresAdminRepository(pool);
    diagnosticRepository = new PostgresDiagnosticRepository(pool);
    if (config.selfService) {
      enrollmentService = new EnrollmentService(
        new PostgresEnrollmentRepository(pool),
        new SmtpEnrollmentMailer(config.selfService.smtpHost, config.selfService.smtpPort, config.selfService.smtpSecure, config.selfService.smtpUser, config.selfService.smtpPassword, config.selfService.smtpFrom),
        config.selfService,
      );
    }
    closers.push(async () => { await pool.end(); });
  }

  if (process.env.REDIS_URL) {
    const redis = createClient({ url: process.env.REDIS_URL });
    redis.on("error", (error) => console.error(JSON.stringify({ event: "redis_error", error: error.name })));
    await redis.connect();
    limiter = new RedisRequestLimiter(redis as unknown as RedisScriptClient, config.requestsPerMinute, config.maxConcurrentRequests);
    adminLoginProtector = new RedisAdminLoginGuard(redis as unknown as RedisScriptClient);
    closers.push(async () => { await redis.quit(); });
  }

  return {
    ledger,
    limiter,
    keyVerifier,
    keyLimitProvider,
    adminRepository,
    adminLoginProtector,
    enrollmentService,
    diagnosticRepository,
    close: async () => {
      for (const close of closers.reverse()) await close();
    },
  };
}

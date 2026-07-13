import assert from "node:assert/strict";
import test from "node:test";
import { EnrollmentService, type EnrollmentMailer, type EnrollmentRepository } from "../src/self-service.js";

class MemoryEnrollmentRepository implements EnrollmentRepository {
  codeHash = "";
  identityHash = "";
  async createChallenge(identityHash: string, codeHash: string): Promise<"created"> { this.identityHash = identityHash; this.codeHash = codeHash; return "created"; }
  async cancelLatestChallenge(): Promise<void> { this.codeHash = ""; }
  async verifyChallenge(identityHash: string, codeHash: string): Promise<"verified" | "invalid"> { return identityHash === this.identityHash && codeHash === this.codeHash ? "verified" : "invalid"; }
  async login(_identityHash: string, email: string): Promise<import("../src/self-service.js").AccountProfile | null> { return { id: "user", email, nickname: "user", avatarMediaType: null, avatarBase64: null, balanceMicrounits: 0 }; }
  async authenticate(): Promise<null> { return null; }
  async updateProfile(): Promise<null> { return null; }
  async updateAvatar(): Promise<null> { return null; }
  async logout(): Promise<void> {}
}

class MemoryMailer implements EnrollmentMailer {
  code = "";
  async sendCode(email: string, code: string): Promise<void> { assert.equal(email, "user@example.com"); this.code = code; }
}

const defaults = { dailyLimit: null, requestsPerMinute: 30, maxConcurrentRequests: 2, expiresInDays: null };

test("normalizes email and rejects malformed addresses", () => {
  assert.equal(EnrollmentService.normalizeEmail(" User@Example.COM "), "user@example.com");
  assert.equal(EnrollmentService.normalizeEmail("not-an-email"), null);
});

test("emails a hashed one-time login code", async () => {
  const repository = new MemoryEnrollmentRepository();
  const mailer = new MemoryMailer();
  const service = new EnrollmentService(repository, mailer, defaults);
  assert.equal(await service.requestCode("user@example.com", "ip"), "sent");
  assert.match(mailer.code, /^\d{6}$/);
  assert.notEqual(repository.codeHash, mailer.code);
});

test("removes an unusable challenge when email delivery fails", async () => {
  const repository = new MemoryEnrollmentRepository();
  const service = new EnrollmentService(repository, { sendCode: async () => { throw new Error("smtp failed"); } }, defaults);
  await assert.rejects(service.requestCode("user@example.com", "ip"), /smtp failed/);
  assert.equal(repository.codeHash, "");
});

test("verifies email and creates an authenticated account session", async () => {
  const repository = new MemoryEnrollmentRepository();
  const mailer = new MemoryMailer();
  const service = new EnrollmentService(repository, mailer, defaults);
  await service.requestCode("user@example.com", "ip");
  const result = await service.verifyAndLogin("user@example.com", mailer.code);
  assert.equal(result.status, "authenticated");
  if (result.status === "authenticated") {
    assert.match(result.accessToken, /^usr_[a-f\d]{64}$/);
    assert.equal("gatewayKey" in result, false);
    assert.equal(result.profile.email, "user@example.com");
  }
});

test("does not issue a session when the account is disabled", async () => {
  const repository = new MemoryEnrollmentRepository();
  repository.login = async () => null;
  const mailer = new MemoryMailer();
  const service = new EnrollmentService(repository, mailer, defaults);
  await service.requestCode("user@example.com", "ip");
  assert.deepEqual(await service.verifyAndLogin("user@example.com", mailer.code), { status: "disabled" });
});

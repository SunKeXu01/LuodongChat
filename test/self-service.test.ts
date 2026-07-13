import assert from "node:assert/strict";
import test from "node:test";
import { EnrollmentService, type EnrollmentMailer, type EnrollmentRepository, type SelfServiceKeyDefaults } from "../src/self-service.js";

class MemoryEnrollmentRepository implements EnrollmentRepository {
  codeHash = "";
  identityHash = "";
  active = false;
  rotated = false;
  defaults?: SelfServiceKeyDefaults;
  async createChallenge(identityHash: string, codeHash: string): Promise<"created"> { this.identityHash = identityHash; this.codeHash = codeHash; return "created"; }
  async cancelLatestChallenge(): Promise<void> { this.codeHash = ""; }
  async verifyChallenge(identityHash: string, codeHash: string): Promise<"verified" | "invalid"> { return identityHash === this.identityHash && codeHash === this.codeHash ? "verified" : "invalid"; }
  async hasActiveKey(): Promise<boolean> { return this.active; }
  async issueKey(_identityHash: string, _keyHash: string, keyPrefix: string, defaults: SelfServiceKeyDefaults, rotate: boolean): Promise<"created"> {
    assert.match(keyPrefix, /^gw_[a-f\d]{8}$/); this.defaults = defaults; this.rotated = rotate; this.active = true; return "created";
  }
  async login(_identityHash: string, email: string): Promise<import("../src/self-service.js").AccountProfile> { return { id: "user", email, nickname: "user", avatarMediaType: null, avatarBase64: null, balanceMicrounits: 0 }; }
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

test("emails a hashed one-time code and issues a key", async () => {
  const repository = new MemoryEnrollmentRepository();
  const mailer = new MemoryMailer();
  const service = new EnrollmentService(repository, mailer, defaults);
  assert.equal(await service.requestCode("user@example.com", "ip"), "sent");
  assert.match(mailer.code, /^\d{6}$/);
  assert.notEqual(repository.codeHash, mailer.code);
  const result = await service.verifyAndIssue("user@example.com", mailer.code, false);
  assert.equal(result.status, "created");
  if (result.status === "created") assert.match(result.key, /^gw_[a-f\d]{48}$/);
  assert.equal(repository.defaults?.dailyLimit, null);
  assert.equal(repository.defaults?.expiresInDays, null);
});

test("requires explicit rotation when an active key exists", async () => {
  const repository = new MemoryEnrollmentRepository();
  const mailer = new MemoryMailer();
  const service = new EnrollmentService(repository, mailer, defaults);
  await service.requestCode("user@example.com", "ip");
  repository.active = true;
  assert.deepEqual(await service.verifyAndIssue("user@example.com", mailer.code, false), { status: "active_key_exists" });
  assert.equal((await service.verifyAndIssue("user@example.com", mailer.code, true)).status, "created");
  assert.equal(repository.rotated, true);
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
    assert.match(result.gatewayKey, /^gw_[a-f\d]{48}$/);
    assert.equal(result.profile.email, "user@example.com");
  }
});

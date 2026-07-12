import assert from "node:assert/strict";
import test from "node:test";
import { EnrollmentService, type EnrollmentMailer, type EnrollmentRepository, type SelfServiceKeyDefaults } from "../src/self-service.js";

class MemoryEnrollmentRepository implements EnrollmentRepository {
  codeHash = "";
  identityHash = "";
  active = false;
  rotated = false;
  async createChallenge(identityHash: string, codeHash: string): Promise<"created"> { this.identityHash = identityHash; this.codeHash = codeHash; return "created"; }
  async verifyChallenge(identityHash: string, codeHash: string): Promise<"verified" | "invalid"> { return identityHash === this.identityHash && codeHash === this.codeHash ? "verified" : "invalid"; }
  async hasActiveKey(): Promise<boolean> { return this.active; }
  async issueKey(_identityHash: string, _keyHash: string, keyPrefix: string, _defaults: SelfServiceKeyDefaults, rotate: boolean): Promise<"created"> {
    assert.match(keyPrefix, /^gw_[a-f\d]{8}$/); this.rotated = rotate; this.active = true; return "created";
  }
}

class MemoryMailer implements EnrollmentMailer {
  code = "";
  async sendCode(email: string, code: string): Promise<void> { assert.equal(email, "user@example.com"); this.code = code; }
}

const defaults = { dailyLimit: 100, requestsPerMinute: 30, maxConcurrentRequests: 2, expiresInDays: 30 };

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

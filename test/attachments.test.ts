import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  MAX_DEFAULT_ATTACHMENT_BYTES,
  MAX_DOCUMENT_ATTACHMENT_BYTES,
  MAX_ATTACHMENTS_TOTAL_BYTES,
  validateAttachment,
} from "../src/attachments.js";

describe("attachment size limits", () => {
  it("keeps room below the upstream 50 MB request limit", () => {
    assert.equal(MAX_DEFAULT_ATTACHMENT_BYTES, 20 * 1024 * 1024);
    assert.equal(MAX_DOCUMENT_ATTACHMENT_BYTES, 40 * 1024 * 1024);
    assert.equal(MAX_ATTACHMENTS_TOTAL_BYTES, 48 * 1024 * 1024);
  });

  it("accepts documents above 20 MB", () => {
    const data = Buffer.alloc(20 * 1024 * 1024 + 1, 0x20);
    assert.doesNotThrow(() => validateAttachment("report.txt", "text/plain", data));
  });

  it("keeps non-document attachments at 20 MB", () => {
    const data = Buffer.alloc(20 * 1024 * 1024 + 1);
    data.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
    assert.throws(
      () => validateAttachment("image.png", "image/png", data),
      /attachment_too_large/,
    );
  });

  it("rejects documents above 40 MB", () => {
    const data = Buffer.alloc(40 * 1024 * 1024 + 1, 0x20);
    assert.throws(
      () => validateAttachment("report.txt", "text/plain", data),
      /attachment_too_large/,
    );
  });
});

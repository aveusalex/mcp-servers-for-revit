import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "fs";
import os from "os";
import path from "path";

const OUT = fs.mkdtempSync(path.join(os.tmpdir(), "revit-mcp-out-"));
process.env.REVIT_MCP_OUT_DIR = OUT;

const {
  toToolResult,
  normalizeEnvelope,
  truncateArrays,
  errorEnvelope,
  okEnvelope,
  envelopeFromError,
  ToolFailure,
  defaultOutDir,
} = await import("../build/framework/index.js");

const textOf = (r) => r.content.find((c) => c.type === "text").text;

test("ok envelope becomes compact JSON text with isError false", () => {
  const r = toToolResult(okEnvelope({ count: 2, ids: [1, 2] }), "find_elements");
  assert.equal(r.isError, false);
  assert.equal(r.content.length, 1);
  const text = textOf(r);
  assert.ok(!text.includes("\n"), "compact JSON");
  assert.deepEqual(JSON.parse(text), { ok: true, data: { count: 2, ids: [1, 2] }, warnings: [], dryRun: false });
});

test("error envelope: isError, readable code/hint/recommendedTool, JSON kept", () => {
  const r = toToolResult(
    errorEnvelope("APPROVAL_REQUIRED", "Needs approval.", "Call propose_change_plan first.", "propose_change_plan"),
    "set_parameters"
  );
  assert.equal(r.isError, true);
  const [head, json] = textOf(r).split(/\n(?=\{)/);
  assert.match(head, /^Error APPROVAL_REQUIRED: Needs approval\./);
  assert.match(head, /Hint: Call propose_change_plan first\./);
  assert.match(head, /Recommended tool: propose_change_plan/);
  assert.equal(JSON.parse(json).error.code, "APPROVAL_REQUIRED");
});

test("normalizeEnvelope: framework envelope (camel or Pascal), legacy data, legacy failure", () => {
  const camel = normalizeEnvelope({ ok: true, data: { a: 1 }, changes: { created: [5], modified: [], deleted: [] }, warnings: [], dryRun: true });
  assert.equal(camel.dryRun, true);
  assert.deepEqual(camel.changes.created, [5]);

  const pascal = normalizeEnvelope({ Ok: false, Error: { Code: "NOT_FOUND", Message: "nope", Hint: "try x" }, Warnings: [] });
  assert.equal(pascal.ok, false);
  assert.deepEqual(pascal.error, { code: "NOT_FOUND", message: "nope", hint: "try x" });

  const legacy = normalizeEnvelope([{ id: 1 }]);
  assert.equal(legacy.ok, true);
  assert.deepEqual(legacy.data, [{ id: 1 }]);

  const failed = normalizeEnvelope({ Success: false, Message: "bad" });
  assert.equal(failed.ok, false);
  assert.equal(failed.error.message, "bad");
});

test("envelopeFromError keeps code and hint from thrown errors", () => {
  const e = envelopeFromError(new ToolFailure("NAME_COLLISION", "Taken", "Pick another name"));
  assert.deepEqual(e.error, { code: "NAME_COLLISION", message: "Taken", hint: "Pick another name" });
  const target = Object.assign(new Error("ambiguous"), { code: "AMBIGUOUS_TARGET" });
  assert.equal(envelopeFromError(target).error.code, "AMBIGUOUS_TARGET");
  assert.equal(envelopeFromError(new Error("boom")).error.code, "INTERNAL_ERROR");
});

test("truncateArrays cuts nested arrays above the limit with a marker", () => {
  const { value, truncated } = truncateArrays({ a: Array.from({ length: 5 }, (_, i) => i), b: { c: [1, 2] } }, 3);
  assert.equal(truncated, true);
  assert.deepEqual(value.a, [0, 1, 2, { _truncated: true, _total: 5 }]);
  assert.deepEqual(value.b.c, [1, 2]);
  assert.equal(truncateArrays([1, 2], 3).truncated, false);
});

test("arrays over 200 items are cut inline and the full result is spilled", () => {
  const ids = Array.from({ length: 450 }, (_, i) => ({ id: i }));
  const r = toToolResult(okEnvelope({ items: ids }), "find_elements");
  const payload = JSON.parse(textOf(r));
  assert.equal(payload.data.items.length, 201);
  assert.deepEqual(payload.data.items[200], { _truncated: true, _total: 450 });
  assert.ok(payload.fullResult.startsWith(OUT));
  assert.equal(JSON.parse(fs.readFileSync(payload.fullResult, "utf8")).data.items.length, 450);
});

test("results over 100 KB are spilled to <tool>-<timestamp>.json with a preview", () => {
  const big = { rows: Array.from({ length: 150 }, (_, i) => ({ i, text: "x".repeat(1000) })) };
  const r = toToolResult(okEnvelope(big), "export_room_data");
  const payload = JSON.parse(textOf(r));
  assert.equal(payload.data.spilled, true);
  assert.ok(payload.data.bytes > 100 * 1024);
  assert.match(path.basename(payload.data.path), /^export_room_data-.+\.json$/);
  assert.ok(payload.data.preview.length <= 2000);
  assert.ok(Buffer.byteLength(textOf(r)) < 10 * 1024);
  assert.equal(JSON.parse(fs.readFileSync(payload.data.path, "utf8")).data.rows.length, 150);
});

test("data.image becomes an image block plus metadata text", () => {
  const base64 = Buffer.from("fake-png-bytes").toString("base64");
  const r = toToolResult(okEnvelope({ viewId: 7, image: { base64, mimeType: "image/png", width: 10 } }), "export_view_image");
  const img = r.content.find((c) => c.type === "image");
  assert.equal(img.data, base64);
  assert.equal(img.mimeType, "image/png");
  const payload = JSON.parse(textOf(r));
  assert.equal(payload.data.image.mimeType, "image/png");
  assert.equal(payload.data.image.bytes, 14);
  assert.equal(payload.data.image.width, 10);
  assert.ok(!textOf(r).includes(base64));
});

test("default spill dir honours REVIT_MCP_OUT_DIR, else %LOCALAPPDATA%\\revit-mcp\\out", () => {
  assert.equal(defaultOutDir(), OUT);
  const saved = process.env.REVIT_MCP_OUT_DIR;
  delete process.env.REVIT_MCP_OUT_DIR;
  try {
    assert.ok(defaultOutDir().endsWith(path.join("revit-mcp", "out")));
  } finally {
    process.env.REVIT_MCP_OUT_DIR = saved;
  }
});

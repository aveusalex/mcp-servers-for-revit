import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import {
  classify,
  parseManifest,
  extractElementIds,
  maxRisk,
  needsApproval,
  LEGACY_TOOLS,
} from "../src/classify.js";
import { canonicalJson, canonicalHash } from "../src/canonical.js";
import { approvalMode, auditParamsMode } from "../src/config.js";
import { auditParams } from "../src/audit.js";

const here = path.dirname(fileURLToPath(import.meta.url));

test("every command in command.json has a legacy classification", () => {
  const cfg = JSON.parse(fs.readFileSync(path.join(here, "../../command.json"), "utf8"));
  for (const c of cfg.commands) {
    assert.ok(LEGACY_TOOLS[c.commandName], `missing legacy classification for ${c.commandName}`);
  }
});

test("legacy reads are read/none, additive writes low, deletes and bulk medium, code high", () => {
  assert.deepEqual(pick(classify("get_current_view_info", {})), { kind: "read", risk: "none", source: "legacy" });
  assert.deepEqual(pick(classify("create_level", {})), { kind: "write", risk: "low", source: "legacy" });
  assert.deepEqual(pick(classify("delete_element", {})), { kind: "write", risk: "medium", source: "legacy" });
  assert.deepEqual(pick(classify("tag_walls", {})), { kind: "write", risk: "medium", source: "legacy" });
  assert.deepEqual(pick(classify("send_code_to_revit", {})), { kind: "write", risk: "high", source: "legacy" });
  assert.equal(classify("create_level", {}).batchable, false);
});

test("operate_element risk depends on the action", () => {
  assert.equal(classify("operate_element", { data: { action: "Select", elementIds: [1] } }).risk, "low");
  assert.equal(classify("operate_element", { data: { action: "Delete", elementIds: [1] } }).risk, "medium");
});

test("unknown commands are fail-closed write/medium", () => {
  const c = classify("frobnicate_model", {});
  assert.deepEqual(pick(c), { kind: "write", risk: "medium", source: "unknown" });
  assert.ok(needsApproval(c.risk));
});

test("manifest wins over the legacy table and accepts string/object/array forms", () => {
  const tools = [
    { name: "set_parameters", kind: "write", risk: "medium", batchable: true, timeoutSeconds: 120 },
    { name: "delete_element", kind: "read", risk: "none" }, // contrived override
    { name: "weird", kind: "bogus", risk: "bogus" },
    { nope: true },
  ];
  for (const raw of [tools, { version: 1, tools }, JSON.stringify({ version: 1, tools })]) {
    const m = parseManifest(raw);
    assert.equal(m.size, 3);
    assert.deepEqual(pick(classify("set_parameters", {}, m)), { kind: "write", risk: "medium", source: "manifest" });
    assert.equal(classify("set_parameters", {}, m).timeoutSeconds, 120);
    assert.equal(classify("delete_element", {}, m).risk, "none");
    // Unrecognised kind/risk values are fail-closed too.
    assert.deepEqual(pick(classify("weird", {}, m)), { kind: "write", risk: "medium", source: "manifest" });
  }
  assert.equal(parseManifest("{not json").size, 0);
  assert.equal(parseManifest(null).size, 0);
});

test("a batch takes the highest step risk; nested or empty batches are high/medium", () => {
  const m = parseManifest([{ name: "pin", kind: "write", risk: "low" }]);
  const low = classify("execute_batch", { steps: [{ tool: "pin", args: {} }] }, m);
  assert.equal(low.risk, "low");
  assert.equal(low.kind, "write");
  const med = classify("execute_batch", { steps: [{ tool: "pin" }, { tool: "delete_element" }] }, m);
  assert.equal(med.risk, "medium");
  assert.equal(classify("execute_batch", { steps: [{ tool: "execute_batch" }] }, m).risk, "high");
  assert.equal(classify("execute_batch", { steps: [] }, m).risk, "medium");
  assert.equal(classify("execute_batch", { steps: [{ tool: "get_current_view_info" }] }).kind, "read");
});

test("maxRisk / needsApproval", () => {
  assert.equal(maxRisk(["none", "low"]), "low");
  assert.equal(maxRisk(["low", "high", "medium"]), "high");
  assert.equal(needsApproval("low"), false);
  assert.equal(needsApproval("medium"), true);
  assert.equal(needsApproval("high"), true);
  assert.equal(needsApproval("garbage"), true);
});

test("extractElementIds finds ids under id-like keys only", () => {
  const ids = extractElementIds({
    elementIds: [10, 11],
    data: { elementId: 12, hostId: 13, count: 99, nested: [{ ids: [14] }] },
    width: 3000,
  });
  assert.deepEqual(ids.sort((a, b) => a - b), [10, 11, 12, 13, 14]);
  assert.equal(extractElementIds({ elementIds: Array.from({ length: 900 }, (_, i) => i + 1) }).length, 500);
});

test("extractElementIds accepts numeric-string ids (framework tools) but not UniqueIds or link refs", () => {
  const ids = extractElementIds({
    elementIds: ["220128", "9c52afbd-bc95-46e1-99c7-e36a3633d730-00035be0", "12:34"],
    items: [{ elementId: "220129", value: "5" }],
  });
  assert.deepEqual(ids.sort((a, b) => a - b), [220128, 220129]);
});

test("canonical JSON sorts keys, normalises numbers and drops undefined", () => {
  assert.equal(canonicalJson({ b: 1, a: { d: 5.0, c: -0 } }), '{"a":{"c":0,"d":5},"b":1}');
  assert.equal(canonicalHash({ x: 1, y: undefined }), canonicalHash({ x: 1 }));
  assert.notEqual(canonicalHash({ x: "1" }), canonicalHash({ x: 1 }));
  assert.equal(canonicalJson([1, undefined, NaN]), "[1,null,null]");
});

test("config: approval and audit modes are fail-closed", () => {
  assert.equal(approvalMode(undefined), approvalMode(process.env.REVIT_MCP_APPROVAL));
  assert.equal(approvalMode("AUTO"), "auto");
  assert.equal(approvalMode("look_only"), "look_only");
  assert.equal(approvalMode("yolo"), "ask_first");
  assert.equal(approvalMode(""), "ask_first");
  assert.equal(auditParamsMode("full"), "full");
  assert.equal(auditParamsMode("whatever"), "hash");
  assert.deepEqual(Object.keys(auditParams({ a: 1 }, "hash")), ["paramsHash"]);
  assert.deepEqual(auditParams({ a: 1 }, "full"), { params: { a: 1 } });
});

function pick(c) {
  return { kind: c.kind, risk: c.risk, source: c.source };
}

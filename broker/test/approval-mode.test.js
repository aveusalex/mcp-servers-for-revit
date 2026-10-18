import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "fs";
import path from "path";
import { bootBroker, mcpClient, pluginClient, tempDir, cid } from "./helpers.js";
import { readApprovalSetting } from "../src/config.js";

/** Point the broker's state directory at a temp folder and write (or remove) the person's choice. */
function useStateDir() {
  const appData = tempDir("revit-mcp-approval-");
  const previous = process.env.APPDATA;
  process.env.APPDATA = appData;
  const file = path.join(appData, "revit-mcp", "approval-mode.json");
  fs.mkdirSync(path.dirname(file), { recursive: true });
  return {
    file,
    choose(mode, at = Date.now()) {
      fs.writeFileSync(file, JSON.stringify({ mode, changedBy: "revit-settings", changedAt: new Date(at).toISOString() }));
      // mtime must differ from the previous write for the cache to notice it.
      const t = new Date(at);
      fs.utimesSync(file, t, t);
    },
    restore() {
      if (previous === undefined) delete process.env.APPDATA;
      else process.env.APPDATA = previous;
    },
  };
}

test("approval mode chosen in Revit applies live, and is audited when it changes", async () => {
  const state = useStateDir();
  try {
    state.choose("ask_first", Date.now() - 10_000);
    const { broker, port, audit } = await bootBroker({ approvalMode: undefined });
    const plugin = await pluginClient(port);
    const mcp = await mcpClient(port);
    const del = () => mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "delete_element", params: { elementIds: [1] } });

    let r = await del();
    assert.equal(r.error.code, "APPROVAL_REQUIRED");

    state.choose("auto");
    r = await del();
    assert.equal(r.result.ok, true, "auto: medium runs without a plan");
    assert.ok(audit.entries.some((e) => e.event === "approval_mode_changed" && e.from === "ask_first" && e.to === "auto"));

    state.choose("look_only", Date.now() + 10_000);
    r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "create_level", params: {} });
    assert.equal(r.error.code, "LOOK_ONLY");
    assert.deepEqual(plugin.commands.map((c) => c.command), ["delete_element"]);
    await broker.stop();
  } finally {
    state.restore();
  }
});

test("a missing or broken approval file falls back to ask_first (fail-closed)", () => {
  const state = useStateDir();
  try {
    assert.equal(readApprovalSetting(), null);
    fs.writeFileSync(state.file, "{ not json");
    assert.equal(readApprovalSetting(), null);
    fs.writeFileSync(state.file, JSON.stringify({ mode: "yolo" }));
    fs.utimesSync(state.file, new Date(Date.now() + 5000), new Date(Date.now() + 5000));
    assert.equal(readApprovalSetting(), null);
  } finally {
    state.restore();
  }
});

test("get_session_context carries the approval mode for the AI", async () => {
  const state = useStateDir();
  try {
    state.choose("auto");
    const { broker, port } = await bootBroker({ approvalMode: undefined });
    await pluginClient(port, { handler: () => ({ result: { ok: true, data: { documents: [] } } }) });
    const mcp = await mcpClient(port);
    const r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "get_session_context", params: {} });
    assert.equal(r.result.data.approval.mode, "auto");
    assert.equal(r.result.data.approval.chosenInRevit, true);
    await broker.stop();
  } finally {
    state.restore();
  }
});

test("a failed plan step reports the tool's own reason", async () => {
  const { broker, port } = await bootBroker({ approvalMode: "auto" });
  await pluginClient(port, {
    handler: (msg) =>
      msg.command === "delete_element"
        ? { result: { ok: false, error: { code: "TRANSACTION_ROLLED_BACK", message: "Revit refused: the line is too short" } } }
        : { result: { ok: true } },
  });
  const mcp = await mcpClient(port);
  const created = await mcp.request({
    type: "plan_create",
    correlationId: cid(),
    docId: "doc-A",
    steps: [
      { tool: "create_level", args: { data: [] } },
      { tool: "delete_element", args: { elementIds: [1] } },
    ],
    summary: "x",
  });
  const r = await mcp.request({ type: "plan_execute", correlationId: cid(), planId: created.result.planId, planHash: created.result.planHash });
  const err = r.error ?? r.result?.error;
  assert.equal(err.code, "TRANSACTION_ROLLED_BACK");
  assert.match(err.message, /Step 1 \(delete_element\) failed: Revit refused: the line is too short/);
  await broker.stop();
});

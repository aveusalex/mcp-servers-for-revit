import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "fs";
import path from "path";
import { bootBroker, mcpClient, pluginClient, connect, waitFor, delay, cid, TOKEN } from "./helpers.js";
import { PlanStore } from "../src/plans.js";

/** Create a plan through the broker and return the response result. */
async function createPlan(mcp, docId, steps, summary = "Delete two walls") {
  const r = await mcp.request({ type: "plan_create", correlationId: cid("pc"), docId, steps, summary, reasoning: "test" });
  assert.equal(r.error, undefined, JSON.stringify(r.error));
  return r.result;
}

/** Have the fake plugin decide a plan it was sent. */
async function decide(plugin, planId, decision = "approved", hashOverride) {
  await waitFor(() => plugin.plansPending.some((p) => p.plan.planId === planId));
  const pending = plugin.plansPending.find((p) => p.plan.planId === planId);
  const c = cid("pd");
  plugin.send$({
    type: "plan_decision",
    correlationId: c,
    planId,
    planHash: hashOverride ?? pending.planHash,
    decision,
    approver: "alex",
    note: "ok",
  });
  return plugin.next((m) => m.type === "plan_decision_ack" && m.correlationId === c);
}

test("ask_first: none/low run directly, medium needs approval with the exact step", async () => {
  const { broker, port } = await bootBroker();
  const plugin = await pluginClient(port);
  const mcp = await mcpClient(port);

  let r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "get_current_view_info", params: {} });
  assert.equal(r.result.ok, true);
  r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "create_level", params: { data: [] } });
  assert.equal(r.result.ok, true);

  const params = { elementIds: [101, 102] };
  r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "delete_element", params });
  assert.equal(r.error.code, "APPROVAL_REQUIRED");
  assert.equal(r.error.recommendedTool, "propose_change_plan");
  assert.match(r.error.hint, /propose_change_plan/);
  assert.deepEqual(r.error.details.step, { tool: "delete_element", args: params });
  assert.equal(r.error.details.risk, "medium");
  // Nothing reached Revit for the gated command.
  assert.deepEqual(plugin.commands.map((c) => c.command), ["get_current_view_info", "create_level"]);
  // The plugin is told kind/risk in the envelope.
  assert.equal(plugin.commands[1].kind, "write");
  assert.equal(plugin.commands[1].risk, "low");

  // Unknown commands are fail-closed.
  r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "mystery_tool", params: {} });
  assert.equal(r.error.code, "APPROVAL_REQUIRED");
  await broker.stop();
});

test("the owning session's manifest drives classification", async () => {
  const { broker, port } = await bootBroker();
  await pluginClient(port, {
    tools: JSON.stringify({
      version: 1,
      tools: [
        { name: "mystery_tool", kind: "read", risk: "none" },
        { name: "set_parameters", kind: "write", risk: "medium" },
      ],
    }),
  });
  const mcp = await mcpClient(port);
  let r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "mystery_tool", params: {} });
  assert.equal(r.result.ok, true);
  r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "set_parameters", params: {} });
  assert.equal(r.error.code, "APPROVAL_REQUIRED");
  await broker.stop();
});

test("dryRun: trusted for framework tools only; dryRun=false matches a plan step without it", async () => {
  const { broker, port } = await bootBroker();
  const plugin = await pluginClient(port, { tools: [{ name: "set_parameters", kind: "write", risk: "medium" }] });
  const mcp = await mcpClient(port);
  const base = { type: "command", docId: "doc-A", command: "set_parameters" };
  // A framework dry run changes nothing: no approval.
  let r = await mcp.request({ ...base, correlationId: cid(), params: { elementIds: [1], dryRun: true } });
  assert.equal(r.result.ok, true);
  // A legacy command would ignore dryRun, so it is still gated.
  r = await mcp.request({ ...base, command: "delete_element", correlationId: cid(), params: { elementIds: [1], dryRun: true } });
  assert.equal(r.error.code, "APPROVAL_REQUIRED");
  // The real call (server injects dryRun:false) is gated, and the hint carries the exact step.
  r = await mcp.request({ ...base, correlationId: cid(), params: { elementIds: [1], dryRun: false } });
  assert.equal(r.error.code, "APPROVAL_REQUIRED");
  assert.deepEqual(r.error.details.step, { tool: "set_parameters", args: { elementIds: [1] } });
  assert.match(r.error.hint, /"tool":"set_parameters"/);
  assert.match(r.error.hint, /dryRun=true/);

  const plan = await createPlan(mcp, "doc-A", [{ tool: "set_parameters", args: { elementIds: [1], dryRun: false } }]);
  await decide(plugin, plan.planId);
  r = await mcp.request({ ...base, planId: plan.planId, correlationId: cid(), params: { elementIds: [1], dryRun: false } });
  assert.equal(r.result.ok, true);
  await broker.stop();
});

test("register with tools:null (command set not loaded yet) uses the legacy table until a manifest arrives", async () => {
  const { broker, port } = await bootBroker();
  const plugin = await pluginClient(port, { tools: null });
  const mcp = await mcpClient(port);
  let r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "find_elements", params: {} });
  assert.equal(r.error.code, "APPROVAL_REQUIRED"); // unknown -> fail-closed
  plugin.send$({
    type: "register",
    sessionId: "sess-1",
    documents: [{ docId: "doc-A", title: "Torre A" }],
    tools: JSON.stringify({ version: 1, tools: [{ name: "find_elements", kind: "read", risk: "none" }] }),
  });
  await delay(20);
  r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "find_elements", params: {} });
  assert.equal(r.result.ok, true);
  await broker.stop();
});

test("execute_batch (statically low) is gated by its riskiest step", async () => {
  const { broker, port } = await bootBroker();
  await pluginClient(port, {
    tools: [
      { name: "execute_batch", kind: "write", risk: "low", batchable: false },
      { name: "set_elements_pinned", kind: "write", risk: "low" },
      { name: "set_parameters", kind: "write", risk: "medium" },
    ],
  });
  const mcp = await mcpClient(port);
  const send = (steps) =>
    mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "execute_batch", params: { steps } });
  let r = await send([{ tool: "set_elements_pinned", args: { elementIds: [1] } }]);
  assert.equal(r.result.ok, true);
  r = await send([{ tool: "set_elements_pinned", args: {} }, { tool: "set_parameters", args: {} }]);
  assert.equal(r.error.code, "APPROVAL_REQUIRED");
  r = await send([{ tool: "not_in_manifest", args: {} }]);
  assert.equal(r.error.code, "APPROVAL_REQUIRED");
  await broker.stop();
});

test("a tool failure inside result (ok:false) marks the plan failed", async () => {
  const { broker, port } = await bootBroker();
  const plugin = await pluginClient(port, {
    handler: () => ({ result: { ok: false, error: { code: "NOT_FOUND", message: "no element" }, warnings: [] } }),
  });
  const mcp = await mcpClient(port);
  const plan = await createPlan(mcp, "doc-A", [{ tool: "delete_element", args: { elementIds: [1] } }]);
  await decide(plugin, plan.planId);
  const r = await mcp.request({ type: "plan_execute", correlationId: cid(), planId: plan.planId });
  assert.equal(r.result.ok, false);
  const g = await mcp.request({ type: "plan_get", correlationId: cid(), planId: plan.planId });
  assert.equal(g.result.status, "failed");
  await broker.stop();
});

test("a heartbeat without tools keeps the registered manifest", async () => {
  const { broker, port } = await bootBroker();
  const plugin = await pluginClient(port, { tools: [{ name: "mystery_tool", kind: "read", risk: "none" }] });
  plugin.send$({ type: "heartbeat", sessionId: "sess-1", documents: [{ docId: "doc-A", title: "Torre A" }] });
  const mcp = await mcpClient(port);
  await delay(20);
  const r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "mystery_tool", params: {} });
  assert.equal(r.result.ok, true);
  await broker.stop();
});

test("look_only refuses writes and write plans, allows reads", async () => {
  const { broker, port } = await bootBroker({ approvalMode: "look_only" });
  const plugin = await pluginClient(port);
  const mcp = await mcpClient(port);
  let r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "create_level", params: {} });
  assert.equal(r.error.code, "LOOK_ONLY");
  r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "list_revit_links", params: {} });
  assert.equal(r.result.ok, true);
  r = await mcp.request({
    type: "plan_create",
    correlationId: cid(),
    docId: "doc-A",
    steps: [{ tool: "delete_element", args: { elementIds: [1] } }],
    summary: "x",
  });
  assert.equal(r.error.code, "LOOK_ONLY");
  assert.deepEqual(plugin.commands.map((c) => c.command), ["list_revit_links"]);
  await broker.stop();
});

test("auto mode runs medium commands and auto-approves plans", async () => {
  const { broker, port } = await bootBroker({ approvalMode: "auto" });
  const plugin = await pluginClient(port);
  const mcp = await mcpClient(port);
  let r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "delete_element", params: { elementIds: [1] } });
  assert.equal(r.result.ok, true);
  const plan = await createPlan(mcp, "doc-A", [{ tool: "delete_element", args: { elementIds: [2] } }]);
  assert.equal(plan.status, "approved");
  assert.equal(plugin.plansPending.length, 0);
  await broker.stop();
});

test("full plan flow: create -> plan_pending -> decision -> wait -> execute -> proof (at most once)", async () => {
  const { broker, port, plansDir, audit } = await bootBroker();
  const plugin = await pluginClient(port);
  const mcp = await mcpClient(port);

  const steps = [{ tool: "delete_element", args: { elementIds: [101, 102] } }];
  const created = await createPlan(mcp, "doc-A", steps);
  assert.equal(created.status, "pending");
  assert.match(created.planId, /^plan_[0-9a-f]{12}$/);
  assert.equal(created.risk, "medium");
  assert.equal(created.planHash, PlanStore.hashOf("doc-A", steps, "Delete two walls"));
  assert.ok(fs.existsSync(path.join(plansDir, `${created.planId}.json`)));

  // The owning plugin gets the full plan, with highlightable ids.
  await waitFor(() => plugin.plansPending.length === 1);
  const pending = plugin.plansPending[0];
  assert.equal(pending.planHash, created.planHash);
  assert.deepEqual(pending.plan.steps[0].affectedElementIds, [101, 102]);
  assert.equal(pending.plan.docTitle, "Torre A");

  // Executing before the decision is refused.
  let r = await mcp.request({ type: "plan_execute", correlationId: cid(), planId: created.planId });
  assert.equal(r.error.code, "PLAN_NOT_APPROVED");

  // A short wait times out with the plan still pending.
  r = await mcp.request({ type: "plan_wait", correlationId: cid(), planId: created.planId, waitSeconds: 0.05 });
  assert.equal(r.result.status, "pending");

  // Long-poll resolves as soon as the human decides.
  const waitCid = cid("wait");
  mcp.send$({ type: "plan_wait", correlationId: waitCid, planId: created.planId, waitSeconds: 10 });
  const ack = await decide(plugin, created.planId);
  assert.equal(ack.ok, true);
  r = await mcp.next((m) => m.correlationId === waitCid);
  assert.equal(r.result.status, "approved");
  assert.equal(r.result.decision.approver, "alex");

  r = await mcp.request({ type: "plan_execute", correlationId: cid("exec"), planId: created.planId });
  assert.equal(r.error, undefined);
  assert.equal(r.result.ok, true);
  const sent = plugin.commands.at(-1);
  assert.equal(sent.command, "delete_element");
  assert.deepEqual(sent.params, { elementIds: [101, 102] });
  assert.equal(sent.planId, created.planId);

  r = await mcp.request({ type: "plan_get", correlationId: cid(), planId: created.planId });
  assert.equal(r.result.status, "executed");
  assert.equal(r.result.steps[0].status, "executed");
  const proof = JSON.parse(fs.readFileSync(path.join(plansDir, `${created.planId}.proof.json`), "utf8"));
  assert.equal(proof.approver, "alex");
  assert.equal(proof.planHash, created.planHash);
  assert.equal(proof.docId, "doc-A");
  assert.equal(proof.status, "executed");
  assert.equal(proof.mode, "single");

  // At most once: neither execute_plan nor a direct call can replay it.
  r = await mcp.request({ type: "plan_execute", correlationId: cid(), planId: created.planId });
  assert.equal(r.error.code, "PLAN_ALREADY_EXECUTED");
  r = await mcp.request({
    type: "command",
    correlationId: cid(),
    docId: "doc-A",
    command: "delete_element",
    params: { elementIds: [101, 102] },
    planId: created.planId,
  });
  assert.equal(r.error.code, "PLAN_ALREADY_EXECUTED");
  assert.equal(plugin.commands.filter((c) => c.command === "delete_element").length, 1);

  const events = audit.entries.map((e) => e.event);
  for (const ev of ["plan_created", "plan_decision", "plan_executed"]) assert.ok(events.includes(ev), ev);
  await broker.stop();
});

test("command + planId: canonical arg match, mismatch refused, document bound", async () => {
  const { broker, port } = await bootBroker();
  const plugin = await pluginClient(port, {
    documents: [
      { docId: "doc-A", title: "A", isActive: true },
      { docId: "doc-B", title: "B", isActive: false },
    ],
  });
  const mcp = await mcpClient(port);
  const plan = await createPlan(mcp, "doc-A", [
    { tool: "delete_element", args: { elementIds: [1], options: { a: 1, b: 2.0 } } },
  ]);
  await decide(plugin, plan.planId);

  const base = { type: "command", docId: "doc-A", command: "delete_element", planId: plan.planId };
  let r = await mcp.request({ ...base, correlationId: cid(), params: { elementIds: [2], options: { a: 1, b: 2 } } });
  assert.equal(r.error.code, "PLAN_STEP_MISMATCH");
  r = await mcp.request({ ...base, correlationId: cid(), docId: "doc-B", params: { options: { b: 2, a: 1 }, elementIds: [1] } });
  assert.equal(r.error.code, "PLAN_DOCUMENT_MISMATCH");
  // Same args, different key order and 2 vs 2.0: accepted.
  r = await mcp.request({ ...base, correlationId: cid(), params: { options: { b: 2, a: 1 }, elementIds: [1] } });
  assert.equal(r.result.ok, true);
  await broker.stop();
});

test("only the plugin owning the document can decide; hash must match", async () => {
  const { broker, port } = await bootBroker();
  const owner = await pluginClient(port, { sessionId: "owner", documents: [{ docId: "doc-A", title: "A" }] });
  const other = await pluginClient(port, { sessionId: "other", documents: [{ docId: "doc-Z", title: "Z" }] });
  const mcp = await mcpClient(port);
  const plan = await createPlan(mcp, "doc-A", [{ tool: "delete_element", args: { elementIds: [1] } }]);
  await waitFor(() => owner.plansPending.length === 1);
  assert.equal(other.plansPending.length, 0);

  // Another Revit session cannot approve it.
  const c1 = cid();
  other.send$({ type: "plan_decision", correlationId: c1, planId: plan.planId, planHash: plan.planHash, decision: "approved", approver: "eve" });
  let ack = await other.next((m) => m.correlationId === c1);
  assert.equal(ack.error.code, "NOT_DOCUMENT_OWNER");

  // An MCP client cannot send plan_decision at all.
  let r = await mcp.request({ type: "plan_decision", correlationId: cid(), planId: plan.planId, planHash: plan.planHash, decision: "approved" });
  assert.equal(r.error.code, "UNKNOWN_TYPE");

  // Wrong hash (the human saw a different plan) is refused.
  ack = await decide(owner, plan.planId, "approved", "deadbeef");
  assert.equal(ack.error.code, "PLAN_HASH_MISMATCH");

  r = await mcp.request({ type: "plan_get", correlationId: cid(), planId: plan.planId });
  assert.equal(r.result.status, "pending");

  // Rejection is final.
  ack = await decide(owner, plan.planId, "rejected");
  assert.equal(ack.ok, true);
  r = await mcp.request({ type: "plan_execute", correlationId: cid(), planId: plan.planId });
  assert.equal(r.error.code, "PLAN_REJECTED");
  ack = await decide(owner, plan.planId, "approved");
  assert.equal(ack.error.code, "PLAN_NOT_PENDING");
  await broker.stop();
});

test("plans expire after the TTL", async () => {
  const { broker, port } = await bootBroker({ planTtlMs: 80 });
  const plugin = await pluginClient(port);
  const mcp = await mcpClient(port);
  const plan = await createPlan(mcp, "doc-A", [{ tool: "delete_element", args: { elementIds: [1] } }]);
  await decide(plugin, plan.planId);
  await delay(120);
  const r = await mcp.request({ type: "plan_execute", correlationId: cid(), planId: plan.planId });
  assert.equal(r.error.code, "PLAN_EXPIRED");
  await broker.stop();
});

test("plans persist on disk and reload in a new store", async () => {
  const { broker, port, plansDir } = await bootBroker();
  await pluginClient(port);
  const mcp = await mcpClient(port);
  const plan = await createPlan(mcp, "doc-A", [{ tool: "delete_element", args: { elementIds: [1] } }]);
  await broker.stop();
  const store = new PlanStore({ dir: plansDir });
  assert.equal(store.get(plan.planId).planHash, plan.planHash);
  assert.equal(store.get("../../etc/passwd"), null);
});

test("a reconnecting plugin is re-sent its pending plans", async () => {
  const { broker, port } = await bootBroker();
  const first = await pluginClient(port);
  const mcp = await mcpClient(port);
  const plan = await createPlan(mcp, "doc-A", [{ tool: "delete_element", args: { elementIds: [1] } }]);
  await waitFor(() => first.plansPending.length === 1);
  first.close();
  await delay(30);
  const second = await pluginClient(port);
  await waitFor(() => second.plansPending.some((p) => p.plan.planId === plan.planId));
  await broker.stop();
});

test("multi-step plan with framework tools runs as ONE execute_batch", async () => {
  const { broker, port, plansDir } = await bootBroker();
  const plugin = await pluginClient(port, {
    tools: {
      version: 1,
      tools: [
        { name: "execute_batch", kind: "write", risk: "low", batchable: false },
        { name: "set_parameters", kind: "write", risk: "medium", batchable: true },
        { name: "set_elements_pinned", kind: "write", risk: "low", batchable: true },
      ],
    },
    handler: (msg) => ({ result: { ok: true, data: { steps: msg.params.steps?.length }, changes: { modified: [5] } } }),
  });
  const mcp = await mcpClient(port);
  const steps = [
    { tool: "set_elements_pinned", args: { elementIds: [5], pinned: true } },
    { tool: "set_parameters", args: { elementIds: [5], values: { Mark: "A" } } },
  ];
  const plan = await createPlan(mcp, "doc-A", steps);
  assert.equal(plan.risk, "medium");
  await decide(plugin, plan.planId);
  const r = await mcp.request({ type: "plan_execute", correlationId: cid(), planId: plan.planId });
  assert.equal(r.result.ok, true);
  assert.equal(plugin.commands.length, 1);
  const cmd = plugin.commands[0];
  assert.equal(cmd.command, "execute_batch");
  assert.deepEqual(cmd.params, { steps, stopOnError: true });
  assert.ok(cmd.batchId.startsWith("batch_"));
  assert.equal(cmd.risk, "medium");
  const proof = JSON.parse(fs.readFileSync(path.join(plansDir, `${plan.planId}.proof.json`), "utf8"));
  assert.equal(proof.mode, "batch");
  assert.deepEqual(proof.changes, { modified: [5] });
  await broker.stop();
});

test("execute_batch with planId must carry exactly the plan's steps", async () => {
  const { broker, port } = await bootBroker();
  const plugin = await pluginClient(port, {
    tools: [
      { name: "execute_batch", kind: "write", risk: "low", batchable: false },
      { name: "set_parameters", kind: "write", risk: "medium" },
    ],
  });
  const mcp = await mcpClient(port);
  const steps = [
    { tool: "set_parameters", args: { elementIds: [1] } },
    { tool: "set_parameters", args: { elementIds: [2] } },
  ];
  const plan = await createPlan(mcp, "doc-A", steps);
  await decide(plugin, plan.planId);
  const base = { type: "command", docId: "doc-A", command: "execute_batch", planId: plan.planId };
  let r = await mcp.request({ ...base, correlationId: cid(), params: { steps: [steps[0]] } });
  assert.equal(r.error.code, "PLAN_STEP_MISMATCH");
  r = await mcp.request({ ...base, correlationId: cid(), params: { steps } });
  assert.equal(r.result.ok, true);
  // Without a plan, the medium batch is gated.
  r = await mcp.request({ ...base, planId: undefined, correlationId: cid(), params: { steps } });
  assert.equal(r.error.code, "APPROVAL_REQUIRED");
  await broker.stop();
});

test("multi-step legacy plan runs step by step and stops at the first failure", async () => {
  const { broker, port } = await bootBroker();
  const plugin = await pluginClient(port, {
    handler: (msg) =>
      msg.params.elementIds?.[0] === 2
        ? { error: { code: "INTERNAL_ERROR", message: "boom" } }
        : { result: { ok: true } },
  });
  const mcp = await mcpClient(port);
  const plan = await createPlan(mcp, "doc-A", [
    { tool: "delete_element", args: { elementIds: [1] } },
    { tool: "delete_element", args: { elementIds: [2] } },
    { tool: "delete_element", args: { elementIds: [3] } },
  ]);
  await decide(plugin, plan.planId);
  const r = await mcp.request({ type: "plan_execute", correlationId: cid(), planId: plan.planId });
  assert.equal(r.error.code, "INTERNAL_ERROR");
  assert.match(r.error.hint, /one by one/);
  assert.equal(plugin.commands.length, 2);
  const g = await mcp.request({ type: "plan_get", correlationId: cid(), planId: plan.planId });
  assert.equal(g.result.status, "failed");
  assert.deepEqual(g.result.steps.map((s) => s.status), ["executed", "failed", "skipped"]);
  await broker.stop();
});

test("plan_create validates its input", async () => {
  const { broker, port } = await bootBroker({ maxBatchSteps: 3 });
  await pluginClient(port);
  const mcp = await mcpClient(port);
  const bad = async (extra, code) => {
    const r = await mcp.request({ type: "plan_create", correlationId: cid(), docId: "doc-A", summary: "s", ...extra });
    assert.equal(r.error?.code, code, JSON.stringify(extra));
  };
  await bad({ steps: [] }, "BAD_REQUEST");
  await bad({ steps: [{ args: {} }] }, "BAD_REQUEST");
  await bad({ steps: [{ tool: "x", args: [1] }] }, "BAD_REQUEST");
  await bad({ steps: [{ tool: "execute_batch", args: {} }] }, "BAD_REQUEST");
  await bad({ steps: [{ tool: "x" }], summary: " " }, "BAD_REQUEST");
  await bad({ steps: [{ tool: "x" }, { tool: "x" }, { tool: "x" }, { tool: "x" }] }, "BATCH_TOO_LARGE");
  await bad({ steps: [{ tool: "x" }], docId: "ghost" }, "DOCUMENT_NOT_FOUND");
  let r = await mcp.request({ type: "plan_get", correlationId: cid(), planId: "plan_000000000000" });
  assert.equal(r.error.code, "PLAN_NOT_FOUND");
  r = await mcp.request({ type: "plan_wait", correlationId: cid(), planId: "nope" });
  assert.equal(r.error.code, "PLAN_NOT_FOUND");
  await broker.stop();
});

// Silence unused-import lint in some editors.
void connect;
void TOKEN;

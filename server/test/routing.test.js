import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import os from "os";
import path from "path";
import fs from "fs";
import { WebSocket } from "ws";

// Isolate broker state (token) to a temp dir BEFORE importing anything that
// reads it. Both the broker and the server derive the token from APPDATA.
const TMP = fs.mkdtempSync(path.join(os.tmpdir(), "revit-mcp-routing-"));
process.env.APPDATA = TMP;

// Broker source is a sibling of server/.
const { Broker } = await import("../../broker/src/broker.js");
const { AuditLog } = await import("../../broker/src/audit.js");
// Server build output (routing/targeting under test).
const { broker: brokerClient } = await import("../build/utils/BrokerConnection.js");
const targeting = await import("../build/utils/targeting.js");
const { withRevitConnection } = await import("../build/utils/ConnectionManager.js");
const { registerListRevitLinksTool } = await import("../build/tools/list_revit_links.js");

class NullAudit extends AuditLog {
  constructor() {
    super(path.join(TMP, "audit"));
  }
  write() {}
}

let broker;
let plugin;

/** Fake plugin: registers a document set and echoes every command as success. */
function fakePlugin(port, token, documents) {
  const ws = new WebSocket(`ws://127.0.0.1:${port}`);
  ws.on("open", () => {
    ws.send(JSON.stringify({ type: "hello", role: "plugin", token, sessionId: "test-sess" }));
  });
  ws.on("message", (d) => {
    const msg = JSON.parse(d.toString());
    if (msg.type === "welcome") {
      ws.send(JSON.stringify({ type: "register", sessionId: "test-sess", revitVersion: "2026", documents }));
    } else if (msg.type === "command") {
      ws.send(JSON.stringify({
        type: "response",
        correlationId: msg.correlationId,
        result: { routedTo: msg.docId, command: msg.command },
      }));
    }
  });
  return ws;
}

before(async () => {
  // Port 8090 is what BrokerConnection dials.
  // ask_first is the production default; a short soft timeout exercises jobs.
  broker = new Broker({ port: 8090, audit: new NullAudit(), approvalMode: "ask_first", softTimeoutMs: 300 });
  await broker.start();
});

after(async () => {
  try { plugin && plugin.close(); } catch {}
  try { await brokerClient; } catch {}
  await broker.stop();
});

test("single open document resolves with no explicit target (retrocompat)", async () => {
  plugin = fakePlugin(8090, broker.token, [
    { docId: "solo", title: "Only Project", isActive: true },
  ]);
  await waitFor(() => brokerClient.listDocuments().then((d) => d.length === 1));

  const res = await withRevitConnection((c) => c.sendCommand("create_level", { data: [] }));
  assert.equal(res.routedTo, "solo");
  plugin.close();
  await delay(50);
});

test("explicit document argument wins", async () => {
  plugin = fakePlugin(8090, broker.token, [
    { docId: "A", title: "Torre A", isActive: true },
    { docId: "B", title: "Torre B", isActive: false },
  ]);
  await waitFor(() => brokerClient.listDocuments().then((d) => d.length === 2));

  const res = await targeting.runWithExplicitDocument("Torre B", () =>
    withRevitConnection((c) => c.sendCommand("get_current_view_info", {}))
  );
  assert.equal(res.routedTo, "B");
  plugin.close();
  await delay(50);
});

test("two open documents with no target throws AMBIGUOUS_TARGET", async () => {
  plugin = fakePlugin(8090, broker.token, [
    { docId: "A", title: "Torre A", isActive: true },
    { docId: "B", title: "Torre B", isActive: false },
  ]);
  await waitFor(() => brokerClient.listDocuments().then((d) => d.length === 2));

  await assert.rejects(
    () => withRevitConnection((c) => c.sendCommand("create_level", {})),
    (err) => err.code === "AMBIGUOUS_TARGET"
  );
  plugin.close();
  await delay(50);
});

test("fixed target applies when no explicit document is given", async () => {
  plugin = fakePlugin(8090, broker.token, [
    { docId: "A", title: "Torre A", isActive: true },
    { docId: "B", title: "Torre B", isActive: false },
  ]);
  await waitFor(() => brokerClient.listDocuments().then((d) => d.length === 2));

  targeting.setFixedTarget("A");
  const res = await withRevitConnection((c) => c.sendCommand("create_grid", {}));
  assert.equal(res.routedTo, "A");
  targeting.setFixedTarget(undefined);
  plugin.close();
  await delay(50);
});

test("list_revit_links routes a read-only query to its selected host document", async () => {
  plugin = fakePlugin(8090, broker.token, [
    { docId: "host", title: "Condominio", isActive: true },
    { docId: "other", title: "Outro Projeto", isActive: false },
  ]);
  await waitFor(() => brokerClient.listDocuments().then((d) => d.length === 2));

  let handler;
  registerListRevitLinksTool({
    tool(name, ...args) {
      assert.equal(name, "list_revit_links");
      handler = args[args.length - 1];
    },
  });

  const response = await targeting.runWithExplicitDocument("host", () => handler());
  const payload = JSON.parse(response.content[0].text);
  assert.equal(payload.routedTo, "host");
  assert.equal(payload.command, "list_revit_links");
  plugin.close();
  await delay(50);
});

// --- approval gate and jobs (Contract E) ----------------------------------

const { legacyCallScope } = await import("../build/utils/ConnectionManager.js");
const { registerDeleteElementTool } = await import("../build/tools/delete_element.js");
const approval = await import("../build/framework/tools/approval.js");
const { toToolResult, envelopeFromError } = await import("../build/framework/index.js");

/** Fake plugin that also answers plan_pending with the given decision. */
function approvingPlugin(port, token, documents, { decision = "approved", slow = new Set() } = {}) {
  const ws = new WebSocket(`ws://127.0.0.1:${port}`);
  ws.commands = [];
  ws.on("open", () => {
    ws.send(JSON.stringify({ type: "hello", role: "plugin", token, sessionId: "appr-sess" }));
  });
  ws.on("message", (d) => {
    const msg = JSON.parse(d.toString());
    if (msg.type === "welcome") {
      ws.send(JSON.stringify({ type: "register", sessionId: "appr-sess", revitVersion: "2026", documents }));
    } else if (msg.type === "plan_pending") {
      setTimeout(() => {
        ws.send(JSON.stringify({
          type: "plan_decision",
          planId: msg.plan.planId,
          planHash: msg.planHash,
          decision,
          approver: "tester",
        }));
      }, 50);
    } else if (msg.type === "command") {
      ws.commands.push(msg);
      const reply = () =>
        ws.send(JSON.stringify({
          type: "response",
          correlationId: msg.correlationId,
          result: { ok: true, data: { ran: msg.command, planId: msg.planId ?? null }, warnings: [], dryRun: false },
        }));
      if (slow.has(msg.command)) setTimeout(reply, 700);
      else reply();
    }
  });
  return ws;
}

const textOf = (r) => r.content.find((c) => c.type === "text").text;
const noCtx = { dryRun: false, warn() {}, send: async () => ({ ok: true }) };

test("legacy tool: medium risk is gated and the message carries the hint and the exact step", async () => {
  plugin = approvingPlugin(8090, broker.token, [{ docId: "solo", title: "Only", isActive: true }]);
  await waitFor(() => brokerClient.listDocuments().then((d) => d.length === 1));

  let handler;
  registerDeleteElementTool({ tool: (...args) => { handler = args[args.length - 1]; } });
  const res = await legacyCallScope.run(true, () => handler({ elementIds: [7] }));
  assert.equal(res.isError, true);
  const text = res.content[0].text;
  assert.match(text, /APPROVAL_REQUIRED/);
  assert.match(text, /propose_change_plan/);
  assert.match(text, /"tool":"delete_element","args":\{"elementIds":\[7\]\}/);
  assert.equal(plugin.commands.length, 0);

  // Outside the legacy scope the structured fields survive for the framework envelope.
  const err = await withRevitConnection((c) => c.sendCommand("delete_element", { elementIds: [7] })).catch((e) => e);
  assert.equal(err.code, "APPROVAL_REQUIRED");
  assert.equal(err.recommendedTool, "propose_change_plan");
  assert.match(err.hint, /propose_change_plan/);
  const env = envelopeFromError(err);
  assert.equal(env.error.recommendedTool, "propose_change_plan");
  assert.match(textOf(toToolResult(env, "delete_element")), /Hint: Call propose_change_plan/);
  plugin.close();
  await delay(50);
});

test("plan tools: propose -> wait (approved in Revit) -> execute -> get_plan", async () => {
  plugin = approvingPlugin(8090, broker.token, [{ docId: "solo", title: "Only", isActive: true }]);
  await waitFor(() => brokerClient.listDocuments().then((d) => d.length === 1));

  const proposed = await approval.proposeChangePlan.run(
    { steps: [{ tool: "delete_element", args: { elementIds: [7] } }], summary: "Delete wall 7" },
    noCtx
  );
  assert.equal(proposed.status, "pending");
  assert.match(proposed.next, /wait_for_plan_decision/);

  const decided = await approval.waitForPlanDecision.run({ planId: proposed.planId, waitSeconds: 5 }, noCtx);
  assert.equal(decided.status, "approved");
  assert.equal(decided.decision.approver, "tester");
  assert.match(decided.next, /execute_plan/);

  const executed = await approval.executePlan.run({ planId: proposed.planId }, noCtx);
  assert.equal(executed.ok, true);
  assert.equal(executed.data.planId, proposed.planId);
  assert.deepEqual(plugin.commands.map((c) => [c.command, c.planId]), [["delete_element", proposed.planId]]);

  const plan = await approval.getPlan.run({ planId: proposed.planId }, noCtx);
  assert.equal(plan.status, "executed");
  assert.ok(plan.proofPath && fs.existsSync(plan.proofPath));

  await assert.rejects(
    () => approval.executePlan.run({ planId: proposed.planId }, noCtx),
    (e) => e.code === "PLAN_ALREADY_EXECUTED"
  );
  await assert.rejects(
    () => approval.executePlan.run({ planId: proposed.planId }, { ...noCtx, dryRun: true }),
    (e) => e.code === "DRY_RUN_UNSUPPORTED"
  );
  plugin.close();
  await delay(50);
});

test("a rejected plan cannot run", async () => {
  plugin = approvingPlugin(8090, broker.token, [{ docId: "solo", title: "Only", isActive: true }], { decision: "rejected" });
  await waitFor(() => brokerClient.listDocuments().then((d) => d.length === 1));
  const p = await approval.proposeChangePlan.run(
    { steps: [{ tool: "delete_element", args: { elementIds: [8] } }], summary: "Delete 8" },
    noCtx
  );
  const d = await approval.waitForPlanDecision.run({ planId: p.planId, waitSeconds: 5 }, noCtx);
  assert.equal(d.status, "rejected");
  assert.match(d.next, /rejected/);
  await assert.rejects(() => approval.executePlan.run({ planId: p.planId }, noCtx), (e) => e.code === "PLAN_REJECTED");
  plugin.close();
  await delay(50);
});

test("slow command: running result tells the AI to poll; get_job_result collects it", async () => {
  plugin = approvingPlugin(8090, broker.token, [{ docId: "solo", title: "Only", isActive: true }], {
    slow: new Set(["analyze_model_statistics"]),
  });
  await waitFor(() => brokerClient.listDocuments().then((d) => d.length === 1));

  const running = await withRevitConnection((c) => c.sendCommand("analyze_model_statistics", {}));
  assert.equal(running.status, "running");
  assert.ok(running.jobId);
  assert.deepEqual(running.nextStep, { tool: "get_job_result", args: { jobId: running.jobId } });
  assert.match(running.message, /get_job_result/);

  // Survives the framework envelope (manifest tools return it as data).
  const { normalizeEnvelope } = await import("../build/framework/index.js");
  const r = toToolResult(normalizeEnvelope(running), "analyze_model_statistics");
  assert.equal(r.isError, false);
  const payload = JSON.parse(textOf(r));
  assert.equal(payload.data.status, "running");
  assert.equal(payload.data.jobId, running.jobId);

  const job = await approval.getJobResult.run({ jobId: running.jobId, waitSeconds: 5 }, noCtx);
  assert.equal(job.ok, true);
  assert.equal(job.data.status, "succeeded");
  assert.deepEqual(job.data.result, { ran: "analyze_model_statistics", planId: null });
  plugin.close();
  await delay(50);
});

test("jobEnvelope: running is ok, failed carries the error", () => {
  const base = { jobId: "j", command: "x", docId: "d", startedAt: "", finishedAt: null, elapsedMs: 1 };
  const running = toToolResult(approval.jobEnvelope({ ...base, status: "running" }), "get_job_result");
  assert.equal(running.isError, false);
  assert.match(textOf(running), /get_job_result again/);
  const failed = toToolResult(
    approval.jobEnvelope({ ...base, status: "failed", error: { code: "TIMEOUT", message: "too slow" } }),
    "get_job_result"
  );
  assert.equal(failed.isError, true);
  assert.match(textOf(failed), /^Error TIMEOUT: too slow/);
});

// --- helpers ---------------------------------------------------------------
function delay(ms) { return new Promise((r) => setTimeout(r, ms)); }
async function waitFor(pred, timeoutMs = 3000) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    try { if (await pred()) return; } catch {}
    await delay(30);
  }
  throw new Error("waitFor timed out");
}

import { test } from "node:test";
import assert from "node:assert/strict";
import { bootBroker, mcpClient, pluginClient, connect, delay, cid, TOKEN } from "./helpers.js";

/** Plugin that answers each command only when release(correlationId) is called. */
async function slowPlugin(port) {
  const gates = new Map();
  const plugin = await pluginClient(port, {
    handler: (msg) =>
      new Promise((resolve) => gates.set(msg.correlationId, () => resolve({ result: { ok: true, done: msg.command } }))),
  });
  plugin.release = async (correlationId) => {
    for (let i = 0; i < 100 && !gates.has(correlationId); i++) await delay(10);
    gates.get(correlationId)();
  };
  return plugin;
}

test("past the soft timeout a command becomes a job; get_job collects it", async () => {
  const { broker, port, audit } = await bootBroker({ softTimeoutMs: 50 });
  const plugin = await slowPlugin(port);
  const mcp = await mcpClient(port);

  const c = cid("slow");
  const r = await mcp.request({ type: "command", correlationId: c, docId: "doc-A", command: "analyze_model_statistics", params: {} });
  assert.equal(r.result.status, "running");
  assert.equal(r.result.jobId, c);
  assert.match(r.result.message, /get_job_result/);

  // Not finished yet: a zero-wait poll says running.
  let g = await mcp.request({ type: "get_job", correlationId: cid(), jobId: c });
  assert.equal(g.result.status, "running");
  assert.equal(g.result.result, undefined);

  // A long poll returns as soon as the plugin answers.
  const pollCid = cid("poll");
  mcp.send$({ type: "get_job", correlationId: pollCid, jobId: c, waitSeconds: 10 });
  await delay(20);
  await plugin.release(c);
  g = await mcp.next((m) => m.correlationId === pollCid);
  assert.equal(g.result.status, "succeeded");
  assert.deepEqual(g.result.result, { ok: true, done: "analyze_model_statistics" });

  // No second "response" for the original correlationId after detaching.
  await delay(30);
  assert.ok(audit.entries.some((e) => e.event === "job_running" && e.jobId === c));
  await broker.stop();
});

test("fast commands answer directly, with no job indirection", async () => {
  const { broker, port } = await bootBroker({ softTimeoutMs: 500 });
  await pluginClient(port);
  const mcp = await mcpClient(port);
  const c = cid();
  const r = await mcp.request({ type: "command", correlationId: c, docId: "doc-A", command: "say_hello", params: {} });
  assert.equal(r.result.ok, true);
  const g = await mcp.request({ type: "get_job", correlationId: cid(), jobId: c });
  assert.equal(g.result.status, "succeeded");
  await broker.stop();
});

test("an identical in-flight command is not resent and shares the jobId", async () => {
  const { broker, port } = await bootBroker({ softTimeoutMs: 40 });
  const plugin = await slowPlugin(port);
  const a = await mcpClient(port);
  const b = await mcpClient(port);
  const params = { data: { x: 1, y: 2 } };

  const c1 = cid("a");
  a.send$({ type: "command", correlationId: c1, docId: "doc-A", command: "create_level", params });
  await delay(10);
  const dup = await b.request({
    type: "command",
    correlationId: cid("b"),
    docId: "doc-A",
    command: "create_level",
    params: { data: { y: 2, x: 1 } },
  });
  assert.equal(dup.result.status, "running");
  assert.equal(dup.result.jobId, c1);
  assert.equal(dup.result.duplicate, true);
  assert.equal(plugin.commands.length, 1);

  await plugin.release(c1);
  const g = await b.request({ type: "get_job", correlationId: cid(), jobId: c1, waitSeconds: 5 });
  assert.equal(g.result.status, "succeeded");

  // Once finished, the same command runs again normally.
  const c2 = cid("again");
  a.send$({ type: "command", correlationId: c2, docId: "doc-A", command: "create_level", params });
  await plugin.release(c2);
  await delay(20);
  assert.equal(plugin.commands.length, 2);
  await broker.stop();
});

test("a job survives its MCP client disconnecting", async () => {
  const { broker, port } = await bootBroker({ softTimeoutMs: 5000 });
  const plugin = await slowPlugin(port);
  const a = await mcpClient(port);
  const c = cid("orphan");
  a.send$({ type: "command", correlationId: c, docId: "doc-A", command: "get_material_quantities", params: {} });
  await delay(20);
  a.close();
  await delay(20);
  await plugin.release(c);
  const b = await mcpClient(port);
  const g = await b.request({ type: "get_job", correlationId: cid(), jobId: c, waitSeconds: 2 });
  assert.equal(g.result.status, "succeeded");
  await broker.stop();
});

test("plugin death fails a detached job", async () => {
  const { broker, port } = await bootBroker({ softTimeoutMs: 30 });
  const plugin = await pluginClient(port, { handler: () => null });
  const mcp = await mcpClient(port);
  const c = cid();
  const r = await mcp.request({ type: "command", correlationId: c, docId: "doc-A", command: "say_hello", params: {} });
  assert.equal(r.result.status, "running");
  plugin.close();
  const g = await mcp.request({ type: "get_job", correlationId: cid(), jobId: c, waitSeconds: 2 });
  assert.equal(g.result.status, "failed");
  assert.equal(g.result.error.code, "PLUGIN_DISCONNECTED");
  await broker.stop();
});

test("the hard timeout fails a job", async () => {
  const { broker, port } = await bootBroker({ softTimeoutMs: 20, commandTimeoutMs: 80 });
  await pluginClient(port, { handler: () => null });
  const mcp = await mcpClient(port);
  const c = cid();
  await mcp.request({ type: "command", correlationId: c, docId: "doc-A", command: "say_hello", params: {} });
  const g = await mcp.request({ type: "get_job", correlationId: cid(), jobId: c, waitSeconds: 2 });
  assert.equal(g.result.status, "failed");
  assert.equal(g.result.error.code, "TIMEOUT");
  await broker.stop();
});

test("get_job on an unknown id", async () => {
  const { broker, port } = await bootBroker();
  const mcp = await mcpClient(port);
  const g = await mcp.request({ type: "get_job", correlationId: cid(), jobId: "nope" });
  assert.equal(g.error.code, "JOB_NOT_FOUND");
  await broker.stop();
});

test("limits: in-flight per MCP client, batch size, message size", async () => {
  const { broker, port } = await bootBroker({ maxInflightPerMcp: 2, maxBatchSteps: 2, maxMessageBytes: 2048, softTimeoutMs: 5000 });
  await pluginClient(port, { handler: () => null });
  const mcp = await mcpClient(port);
  for (let i = 0; i < 2; i++) {
    mcp.send$({ type: "command", correlationId: cid("busy"), docId: "doc-A", command: "say_hello", params: { i } });
  }
  await delay(30);
  let r = await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "say_hello", params: { i: 3 } });
  assert.equal(r.error.code, "OVERLOADED");

  // Another client has its own budget.
  const other = await mcpClient(port);
  r = await other.request({
    type: "command",
    correlationId: cid(),
    docId: "doc-A",
    command: "execute_batch",
    params: { steps: [{ tool: "a" }, { tool: "b" }, { tool: "c" }] },
  });
  assert.equal(r.error.code, "BATCH_TOO_LARGE");
  r = await other.request({
    type: "command",
    correlationId: cid(),
    docId: "doc-A",
    command: "execute_batch",
    params: { steps: [{ tool: "execute_batch" }] },
  });
  assert.equal(r.error.code, "BAD_REQUEST");

  const big = cid("big");
  r = await other.request({ type: "command", correlationId: big, docId: "doc-A", command: "say_hello", params: { blob: "x".repeat(4000) } });
  assert.equal(r.error.code, "MESSAGE_TOO_LARGE");
  await broker.stop();
});

test("audit records risk/jobId/planId/batchId and hashes params by default", async () => {
  const { broker, port, audit } = await bootBroker();
  await pluginClient(port);
  const mcp = await mcpClient(port);
  await mcp.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "create_level", params: { name: "L1" } });
  const entry = audit.entries.find((e) => e.event === "command");
  assert.equal(entry.risk, "low");
  assert.equal(entry.kind, "write");
  assert.ok(entry.jobId);
  assert.ok("planId" in entry && "batchId" in entry);
  assert.match(entry.paramsHash, /^[0-9a-f]{64}$/);
  assert.equal(entry.params, undefined);
  const resp = audit.entries.find((e) => e.event === "response");
  assert.equal(resp.ok, true);
  assert.equal(typeof resp.durationMs, "number");
  await broker.stop();

  const full = await bootBroker({ auditParamsMode: "full" });
  await pluginClient(full.port);
  const m2 = await mcpClient(full.port);
  await m2.request({ type: "command", correlationId: cid(), docId: "doc-A", command: "create_level", params: { name: "L1" } });
  assert.deepEqual(full.audit.entries.find((e) => e.event === "command").params, { name: "L1" });
  await full.broker.stop();
});

void connect;
void TOKEN;

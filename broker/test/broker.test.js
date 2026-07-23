import { test } from "node:test";
import assert from "node:assert/strict";
import { WebSocket } from "ws";
import { Broker } from "../src/broker.js";
import { AuditLog } from "../src/audit.js";

const TOKEN = "test-token";

/** Silent audit log so tests don't write to the user's real state dir. */
class NullAudit extends AuditLog {
  constructor() {
    super("/tmp/revit-mcp-broker-test-audit");
  }
  write() {}
}

/** Boot a broker on an ephemeral port. */
async function bootBroker(opts = {}) {
  const broker = new Broker({ port: 0, token: TOKEN, audit: new NullAudit(), ...opts });
  await broker.start();
  const port = broker._wss.address().port;
  return { broker, port };
}

/** Minimal promise-based WS client. */
function connect(port) {
  const ws = new WebSocket(`ws://127.0.0.1:${port}`);
  const inbox = [];
  const waiters = [];
  ws.on("message", (d) => {
    const msg = JSON.parse(d.toString());
    const w = waiters.shift();
    if (w) w(msg);
    else inbox.push(msg);
  });
  ws.next = () =>
    new Promise((resolve) => {
      const queued = inbox.shift();
      if (queued) resolve(queued);
      else waiters.push(resolve);
    });
  ws.send$ = (obj) => ws.send(JSON.stringify(obj));
  return new Promise((resolve) => ws.on("open", () => resolve(ws)));
}

test("routes a command to the plugin session that owns the docId", async () => {
  const { broker, port } = await bootBroker();

  // Plugin connects, authenticates, and registers two documents.
  const plugin = await connect(port);
  plugin.send$({ type: "hello", role: "plugin", token: TOKEN, sessionId: "1234-2026" });
  assert.equal((await plugin.next()).type, "welcome");
  plugin.send$({
    type: "register",
    sessionId: "1234-2026",
    revitVersion: "2026",
    documents: [
      { docId: "doc-A", title: "Torre A", isActive: true },
      { docId: "doc-B", title: "Torre B", isActive: false },
    ],
  });

  // Plugin echoes commands back as successful responses.
  plugin.on("message", (d) => {
    const msg = JSON.parse(d.toString());
    if (msg.type === "command") {
      plugin.send$({
        type: "response",
        correlationId: msg.correlationId,
        result: { echoedDoc: msg.docId, command: msg.command },
      });
    }
  });

  const mcp = await connect(port);
  mcp.send$({ type: "hello", role: "mcp", token: TOKEN });
  assert.equal((await mcp.next()).type, "welcome");

  // list_documents surfaces both registered docs.
  mcp.send$({ type: "list_documents", correlationId: "c0" });
  const docs = await mcp.next();
  assert.equal(docs.type, "documents");
  assert.deepEqual(docs.documents.map((d) => d.docId).sort(), ["doc-A", "doc-B"]);

  // Command to doc-B is routed and answered.
  mcp.send$({ type: "command", correlationId: "c1", docId: "doc-B", command: "create_level", params: {} });
  const resp = await mcp.next();
  assert.equal(resp.type, "response");
  assert.equal(resp.correlationId, "c1");
  assert.equal(resp.result.echoedDoc, "doc-B");

  await broker.stop();
});

test("rejects a bad token and closes", async () => {
  const { broker, port } = await bootBroker();
  const ws = await connect(port);
  ws.send$({ type: "hello", role: "mcp", token: "wrong" });
  const msg = await ws.next();
  assert.equal(msg.type, "error");
  assert.equal(msg.error.code, "BAD_TOKEN");
  await broker.stop();
});

test("unknown docId yields DOCUMENT_NOT_FOUND", async () => {
  const { broker, port } = await bootBroker();
  const mcp = await connect(port);
  mcp.send$({ type: "hello", role: "mcp", token: TOKEN });
  await mcp.next(); // welcome
  mcp.send$({ type: "command", correlationId: "x1", docId: "ghost", command: "create_level", params: {} });
  const resp = await mcp.next();
  assert.equal(resp.error.code, "DOCUMENT_NOT_FOUND");
  await broker.stop();
});

test("two MCP clients are routed independently, no crosstalk", async () => {
  const { broker, port } = await bootBroker();

  const plugin = await connect(port);
  plugin.send$({ type: "hello", role: "plugin", token: TOKEN, sessionId: "s1" });
  await plugin.next();
  plugin.send$({
    type: "register",
    sessionId: "s1",
    revitVersion: "2026",
    documents: [
      { docId: "P-A", title: "A", isActive: true },
      { docId: "P-B", title: "B", isActive: false },
    ],
  });
  plugin.on("message", (d) => {
    const msg = JSON.parse(d.toString());
    if (msg.type === "command") {
      plugin.send$({ type: "response", correlationId: msg.correlationId, result: { doc: msg.docId } });
    }
  });

  const a = await connect(port);
  a.send$({ type: "hello", role: "mcp", token: TOKEN });
  await a.next();
  const b = await connect(port);
  b.send$({ type: "hello", role: "mcp", token: TOKEN });
  await b.next();

  a.send$({ type: "command", correlationId: "a1", docId: "P-A", command: "cmd", params: {} });
  b.send$({ type: "command", correlationId: "b1", docId: "P-B", command: "cmd", params: {} });

  const ra = await a.next();
  const rb = await b.next();
  assert.equal(ra.correlationId, "a1");
  assert.equal(ra.result.doc, "P-A");
  assert.equal(rb.correlationId, "b1");
  assert.equal(rb.result.doc, "P-B");

  await broker.stop();
});

test("sweeps a session that stops heartbeating and drops its documents", async () => {
  const { broker, port } = await bootBroker({ sessionTtlMs: 150, sweepIntervalMs: 50 });

  const plugin = await connect(port);
  plugin.send$({ type: "hello", role: "plugin", token: TOKEN, sessionId: "ttl-sess" });
  await plugin.next();
  plugin.send$({
    type: "register",
    sessionId: "ttl-sess",
    revitVersion: "2026",
    documents: [{ docId: "TTL", title: "Doc", isActive: true }],
  });

  const mcp = await connect(port);
  mcp.send$({ type: "hello", role: "mcp", token: TOKEN });
  await mcp.next();

  // Present at first.
  mcp.send$({ type: "list_documents", correlationId: "d1" });
  assert.equal((await mcp.next()).documents.length, 1);

  // Stop heartbeating; wait past the TTL, then the doc is gone.
  await new Promise((r) => setTimeout(r, 300));
  mcp.send$({ type: "list_documents", correlationId: "d2" });
  assert.equal((await mcp.next()).documents.length, 0);

  await broker.stop();
});

test("evicts a session whose in-flight command outlives its socket", async () => {
  const { broker, port } = await bootBroker();

  const plugin = await connect(port);
  plugin.send$({ type: "hello", role: "plugin", token: TOKEN, sessionId: "s9" });
  await plugin.next();
  plugin.send$({
    type: "register",
    sessionId: "s9",
    revitVersion: "2026",
    documents: [{ docId: "D9", title: "Nine", isActive: true }],
  });
  // Note: plugin never replies to commands.

  const mcp = await connect(port);
  mcp.send$({ type: "hello", role: "mcp", token: TOKEN });
  await mcp.next();

  mcp.send$({ type: "command", correlationId: "k1", docId: "D9", command: "cmd", params: {} });
  // Kill the plugin socket before it can answer.
  plugin.close();

  const resp = await mcp.next();
  assert.equal(resp.correlationId, "k1");
  assert.equal(resp.error.code, "PLUGIN_DISCONNECTED");

  await broker.stop();
});

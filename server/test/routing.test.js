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
  broker = new Broker({ port: 8090, audit: new NullAudit() });
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

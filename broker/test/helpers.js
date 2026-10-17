import fs from "fs";
import os from "os";
import path from "path";
import { WebSocket } from "ws";
import { Broker } from "../src/broker.js";
import { AuditLog } from "../src/audit.js";

export const TOKEN = "test-token";

/** Audit log that records entries in memory instead of writing to disk. */
export class MemoryAudit extends AuditLog {
  constructor() {
    super(path.join(os.tmpdir(), "revit-mcp-broker-test-audit"));
    this.entries = [];
  }
  write(entry) {
    this.entries.push(entry);
  }
}

export function tempDir(prefix = "revit-mcp-broker-test-") {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

/** Boot a broker on an ephemeral port with isolated plan storage. */
export async function bootBroker(opts = {}) {
  const audit = new MemoryAudit();
  const plansDir = opts.plansDir ?? tempDir();
  const broker = new Broker({
    port: 0,
    token: TOKEN,
    audit,
    plansDir,
    approvalMode: "ask_first",
    ...opts,
  });
  await broker.start();
  const port = broker._wss.address().port;
  return { broker, port, audit, plansDir };
}

/** Minimal promise-based WS client with an inbox. */
export function connect(port) {
  const ws = new WebSocket(`ws://127.0.0.1:${port}`);
  const inbox = [];
  const waiters = [];
  ws.on("message", (d) => {
    const msg = JSON.parse(d.toString());
    const i = waiters.findIndex((w) => w.pred(msg));
    if (i >= 0) waiters.splice(i, 1)[0].resolve(msg);
    else inbox.push(msg);
  });
  /** Next message (optionally the next one matching pred). */
  ws.next = (pred = () => true, timeoutMs = 3000) =>
    new Promise((resolve, reject) => {
      const qi = inbox.findIndex(pred);
      if (qi >= 0) return resolve(inbox.splice(qi, 1)[0]);
      const timer = setTimeout(() => reject(new Error("timed out waiting for message")), timeoutMs);
      waiters.push({
        pred,
        resolve: (m) => {
          clearTimeout(timer);
          resolve(m);
        },
      });
    });
  ws.send$ = (obj) => ws.send(JSON.stringify(obj));
  /** Send and wait for the response with the same correlationId. */
  ws.request = (obj, timeoutMs) => {
    ws.send$(obj);
    return ws.next((m) => m.correlationId === obj.correlationId, timeoutMs);
  };
  return new Promise((resolve) => ws.on("open", () => resolve(ws)));
}

export async function mcpClient(port) {
  const mcp = await connect(port);
  mcp.send$({ type: "hello", role: "mcp", token: TOKEN });
  await mcp.next((m) => m.type === "welcome");
  return mcp;
}

/**
 * Fake plugin. `handler(msg)` decides the reply to each command: return
 * {result} / {error}, a Promise of one, or `null` to never answer.
 * Records every command and plan_pending it sees.
 */
export async function pluginClient(port, { sessionId = "sess-1", documents, tools, handler } = {}) {
  const plugin = await connect(port);
  plugin.commands = [];
  plugin.plansPending = [];
  plugin.send$({ type: "hello", role: "plugin", token: TOKEN, sessionId });
  await plugin.next((m) => m.type === "welcome");
  plugin.on("message", async (d) => {
    const msg = JSON.parse(d.toString());
    if (msg.type === "plan_pending") plugin.plansPending.push(msg);
    if (msg.type !== "command") return;
    plugin.commands.push(msg);
    const reply = handler ? await handler(msg) : { result: { ok: true, echoed: msg.command } };
    if (reply == null) return;
    plugin.send$({ type: "response", correlationId: msg.correlationId, ...reply });
  });
  plugin.send$({
    type: "register",
    sessionId,
    revitVersion: "2026",
    documents: documents ?? [{ docId: "doc-A", title: "Torre A", isActive: true }],
    ...(tools !== undefined ? { tools } : {}),
  });
  return plugin;
}

/** Poll until `fn()` is truthy. */
export async function waitFor(fn, timeoutMs = 2000) {
  const start = Date.now();
  for (;;) {
    if (await fn()) return;
    if (Date.now() - start > timeoutMs) throw new Error("waitFor timed out");
    await delay(10);
  }
}

export function delay(ms) {
  return new Promise((r) => setTimeout(r, ms));
}

let seq = 0;
export function cid(prefix = "c") {
  return `${prefix}-${++seq}`;
}

import crypto from "crypto";
import { WebSocketServer } from "ws";
import {
  HOST,
  PORT,
  HEARTBEAT_INTERVAL_MS,
  COMMAND_TIMEOUT_MS,
  loadOrCreateToken,
} from "./config.js";
import { Registry } from "./registry.js";
import { AuditLog, summarizeParams } from "./audit.js";

/**
 * The broker is the single loopback listener. Two kinds of client connect to
 * it and are told apart at handshake:
 *
 *   - `plugin`: a Revit add-in (one per Revit process). Connects OUT to the
 *     broker, registers its open documents, heartbeats, and executes commands.
 *   - `mcp`: a Claude MCP server (one per chat). Sends command envelopes and
 *     control requests; the broker fans replies back by correlationId.
 *
 * Nothing here holds a notion of a global "current connection": N mcp clients
 * and N plugin sessions coexist, and every command carries its own docId.
 */
export class Broker {
  /**
   * @param {object} [opts]
   * @param {string} [opts.host]
   * @param {number} [opts.port]
   * @param {string} [opts.token]
   * @param {AuditLog} [opts.audit]
   */
  constructor(opts = {}) {
    this.host = opts.host ?? HOST;
    this.port = opts.port ?? PORT;
    this.token = opts.token ?? loadOrCreateToken();
    this.audit = opts.audit ?? new AuditLog();
    this.sweepIntervalMs = opts.sweepIntervalMs ?? HEARTBEAT_INTERVAL_MS;
    this.registry = new Registry(opts.sessionTtlMs);

    /** @type {Set<import("ws").WebSocket>} MCP clients, for lifecycle only. */
    this.mcpClients = new Set();

    /**
     * In-flight commands: correlationId -> { mcpWs, docId, command, timer }.
     * Lets us route a plugin's reply back to the exact MCP client that asked,
     * and fail cleanly if the owning plugin dies mid-flight.
     * @type {Map<string, { mcpWs: import("ws").WebSocket, docId: string, command: string, timer: NodeJS.Timeout }>}
     */
    this.pending = new Map();

    this._wss = null;
    this._sweepTimer = null;
  }

  /** @returns {Promise<void>} resolves once listening (or rejects on EADDRINUSE). */
  start() {
    return new Promise((resolve, reject) => {
      this._wss = new WebSocketServer({ host: this.host, port: this.port });
      this._wss.on("connection", (ws) => this._onConnection(ws));
      this._wss.on("listening", () => {
        this._sweepTimer = setInterval(() => this._sweep(), this.sweepIntervalMs);
        this.audit.write({ event: "broker_start", host: this.host, port: this.port });
        resolve();
      });
      this._wss.on("error", (err) => reject(err));
    });
  }

  async stop() {
    if (this._sweepTimer) clearInterval(this._sweepTimer);
    for (const { timer } of this.pending.values()) clearTimeout(timer);
    this.pending.clear();
    if (!this._wss) return;
    // server.close() only calls back once every client socket is gone, so
    // terminate them first — otherwise a lingering connection hangs shutdown.
    for (const client of this._wss.clients) client.terminate();
    await new Promise((r) => this._wss.close(() => r()));
  }

  // --- connection lifecycle -------------------------------------------------

  _onConnection(ws) {
    ws._role = null; // 'plugin' | 'mcp', set at handshake
    ws._sessionId = null;
    ws.on("message", (data) => this._onMessage(ws, data));
    ws.on("close", () => this._onClose(ws));
    ws.on("error", () => {}); // errors surface as 'close'
  }

  _onClose(ws) {
    if (ws._role === "plugin") {
      const sessionId = this.registry.removeBySocket(ws);
      if (sessionId) {
        this._failPendingForSession(sessionId, "PLUGIN_DISCONNECTED");
        this.audit.write({ event: "session_closed", sessionId });
      }
    } else if (ws._role === "mcp") {
      this.mcpClients.delete(ws);
      this._failPendingForMcp(ws);
    }
  }

  _onMessage(ws, data) {
    let msg;
    try {
      msg = JSON.parse(data.toString());
    } catch {
      return this._send(ws, { type: "error", error: { code: "BAD_JSON", message: "Invalid JSON" } });
    }

    // Handshake must come first and carry the shared token.
    if (ws._role == null) {
      if (msg.type !== "hello") {
        return this._send(ws, { type: "error", error: { code: "EXPECTED_HELLO", message: "First message must be a hello" } });
      }
      if (msg.token !== this.token) {
        this.audit.write({ event: "auth_rejected", role: msg.role });
        this._send(ws, { type: "error", error: { code: "BAD_TOKEN", message: "Invalid token" } });
        return ws.close();
      }
      if (msg.role === "plugin") {
        ws._role = "plugin";
        ws._sessionId = msg.sessionId;
      } else if (msg.role === "mcp") {
        ws._role = "mcp";
        this.mcpClients.add(ws);
      } else {
        return ws.close();
      }
      return this._send(ws, { type: "welcome", role: ws._role });
    }

    if (ws._role === "plugin") return this._onPluginMessage(ws, msg);
    if (ws._role === "mcp") return this._onMcpMessage(ws, msg);
  }

  // --- plugin side ----------------------------------------------------------

  _onPluginMessage(ws, msg) {
    switch (msg.type) {
      case "register":
      case "heartbeat": {
        // Both carry the full document list; treat them the same.
        this.registry.upsertSession(
          msg.sessionId,
          ws,
          { revitVersion: msg.revitVersion, pid: msg.pid },
          msg.documents || []
        );
        ws._sessionId = msg.sessionId;
        return;
      }
      case "response": {
        // A reply to a routed command. Hand it back to the waiting MCP client.
        return this._resolvePending(msg);
      }
      default:
        return;
    }
  }

  // --- mcp side -------------------------------------------------------------

  _onMcpMessage(ws, msg) {
    switch (msg.type) {
      case "list_documents":
        return this._send(ws, {
          type: "documents",
          correlationId: msg.correlationId,
          documents: this.registry.listDocuments(),
        });

      case "command":
        return this._routeCommand(ws, msg);

      default:
        return this._send(ws, {
          type: "error",
          correlationId: msg.correlationId,
          error: { code: "UNKNOWN_TYPE", message: `Unknown message type: ${msg.type}` },
        });
    }
  }

  /**
   * Route one command envelope to the plugin session that owns its docId.
   * Envelope: { type:"command", correlationId, docId, command, params }.
   */
  _routeCommand(mcpWs, msg) {
    const correlationId = msg.correlationId || crypto.randomUUID();
    const { docId, command, params } = msg;

    const target = docId ? this.registry.sessionForDoc(docId) : null;
    if (!target) {
      this.audit.write({
        event: "command_rejected",
        correlationId,
        docId,
        command,
        reason: "DOCUMENT_NOT_FOUND",
      });
      return this._send(mcpWs, {
        type: "response",
        correlationId,
        error: {
          code: "DOCUMENT_NOT_FOUND",
          message: docId
            ? `No open document with id ${docId}`
            : "No docId supplied and it could not be resolved",
        },
      });
    }

    const timer = setTimeout(() => {
      this.pending.delete(correlationId);
      this._send(mcpWs, {
        type: "response",
        correlationId,
        error: { code: "TIMEOUT", message: `Command timed out after ${COMMAND_TIMEOUT_MS}ms: ${command}` },
      });
    }, COMMAND_TIMEOUT_MS);

    this.pending.set(correlationId, { mcpWs, docId, command, timer });

    this.audit.write({
      event: "command",
      correlationId,
      docId,
      command,
      params: summarizeParams(params),
    });

    this._send(target.ws, {
      type: "command",
      correlationId,
      docId,
      command,
      params: params ?? {},
    });
  }

  _resolvePending(msg) {
    const entry = this.pending.get(msg.correlationId);
    if (!entry) return; // already timed out or unknown
    clearTimeout(entry.timer);
    this.pending.delete(msg.correlationId);
    this.audit.write({
      event: "response",
      correlationId: msg.correlationId,
      docId: entry.docId,
      command: entry.command,
      ok: !msg.error,
    });
    this._send(entry.mcpWs, {
      type: "response",
      correlationId: msg.correlationId,
      result: msg.result,
      error: msg.error,
    });
  }

  // --- failure fan-out ------------------------------------------------------

  _failPendingForSession(sessionId, code) {
    for (const [cid, entry] of this.pending) {
      const doc = this.registry.documents.get(entry.docId);
      const stillOwned = doc && doc.sessionId === sessionId;
      // doc already gone (session removed) → also fail anything routed to it.
      if (stillOwned || !doc) {
        clearTimeout(entry.timer);
        this.pending.delete(cid);
        this._send(entry.mcpWs, {
          type: "response",
          correlationId: cid,
          error: { code, message: "Owning Revit session disconnected mid-command" },
        });
      }
    }
  }

  _failPendingForMcp(mcpWs) {
    for (const [cid, entry] of this.pending) {
      if (entry.mcpWs === mcpWs) {
        clearTimeout(entry.timer);
        this.pending.delete(cid);
      }
    }
  }

  _sweep() {
    const dead = this.registry.sweep();
    for (const sessionId of dead) {
      this._failPendingForSession(sessionId, "SESSION_TIMEOUT");
      this.audit.write({ event: "session_evicted", sessionId });
    }
  }

  _send(ws, obj) {
    if (ws.readyState === ws.OPEN) ws.send(JSON.stringify(obj));
  }
}

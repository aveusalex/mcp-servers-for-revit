import WebSocket from "ws";
import { spawn } from "child_process";
import { randomUUID } from "crypto";
import path from "path";
import fs from "fs";
import { fileURLToPath } from "url";
import {
  BROKER_HOST,
  BROKER_PORT,
  loadOrCreateToken,
} from "./brokerConfig.js";

export interface DocumentMeta {
  docId: string;
  title: string;
  pathName?: string;
  isActive: boolean;
  isWorkshared?: boolean;
  revitVersion?: string;
  sessionId?: string;
}

/**
 * Client-side safety net only. The broker answers a slow command with
 * {status:"running", jobId} after the tool's soft timeout (default 50 s) and
 * gives up on its own after 15 min, so this just has to be longer than that.
 */
const COMMAND_TIMEOUT_MS = 16 * 60 * 1000;

/** Error raised for a broker/plugin error reply; keeps the structured fields. */
export interface BrokerError extends Error {
  code?: string;
  hint?: string;
  recommendedTool?: string;
  details?: unknown;
}

/** What the broker answers when a command outlives its soft timeout. */
export interface RunningResult {
  status: "running";
  jobId: string;
  command?: string;
  duplicate?: boolean;
  message?: string;
}

export function isRunningResult(value: unknown): value is RunningResult {
  return (
    !!value &&
    typeof value === "object" &&
    (value as any).status === "running" &&
    typeof (value as any).jobId === "string"
  );
}

export interface PlanStep {
  tool: string;
  args?: Record<string, unknown>;
}

export interface JobView {
  jobId: string;
  status: "running" | "succeeded" | "failed";
  command: string;
  docId: string;
  risk?: string;
  planId?: string | null;
  batchId?: string | null;
  startedAt: string;
  finishedAt: string | null;
  elapsedMs: number;
  result?: unknown;
  error?: { code: string; message: string; hint?: string; recommendedTool?: string } | null;
}

export interface PlanView {
  planId: string;
  planHash: string;
  docId: string;
  docTitle?: string | null;
  summary: string;
  reasoning?: string | null;
  risk: string;
  status: "pending" | "approved" | "rejected" | "expired" | "executing" | "executed" | "failed";
  createdAt: string;
  expiresAt: string;
  steps: Array<{ index: number; tool: string; args: unknown; kind: string; risk: string; status: string }>;
  decision: { decision: string; approver: string; note?: string | null; decidedAt: string } | null;
  proofPath?: string | null;
}

/**
 * The MCP server's single, persistent link to the broker daemon. One of these
 * exists per MCP server process (i.e. per Claude chat). It:
 *   - connects to ws://127.0.0.1:8090 and authenticates with the shared token,
 *   - spawns the broker detached if nothing is listening yet (idempotent),
 *   - correlates replies back to callers by correlationId.
 */
class BrokerConnection {
  private ws: WebSocket | null = null;
  private connecting: Promise<void> | null = null;
  private token = loadOrCreateToken();
  private pending = new Map<
    string,
    { resolve: (v: any) => void; reject: (e: Error) => void; timer: NodeJS.Timeout }
  >();

  private isOpen(): boolean {
    return this.ws != null && this.ws.readyState === WebSocket.OPEN;
  }

  /** Ensure a live, authenticated connection, spawning the broker if needed. */
  async ensureConnected(): Promise<void> {
    if (this.isOpen()) return;
    if (this.connecting) return this.connecting;
    this.connecting = this._connectWithAutostart().finally(() => {
      this.connecting = null;
    });
    return this.connecting;
  }

  private async _connectWithAutostart(): Promise<void> {
    try {
      await this._connectOnce();
      return;
    } catch {
      // Nothing listening (or handshake failed). Spawn the broker and retry.
      this._spawnBroker();
    }
    // Retry a handful of times while the freshly-spawned daemon comes up.
    let lastErr: unknown;
    for (let i = 0; i < 10; i++) {
      await delay(300);
      try {
        await this._connectOnce();
        return;
      } catch (e) {
        lastErr = e;
      }
    }
    throw new Error(
      `Could not connect to the Revit MCP broker at ws://${BROKER_HOST}:${BROKER_PORT}: ${
        lastErr instanceof Error ? lastErr.message : String(lastErr)
      }`
    );
  }

  private _connectOnce(): Promise<void> {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(`ws://${BROKER_HOST}:${BROKER_PORT}`);
      let settled = false;

      const fail = (err: Error) => {
        if (settled) return;
        settled = true;
        ws.removeAllListeners();
        try {
          ws.close();
        } catch {
          /* ignore */
        }
        reject(err);
      };

      ws.on("error", (e) => fail(e instanceof Error ? e : new Error(String(e))));

      ws.on("open", () => {
        ws.send(JSON.stringify({ type: "hello", role: "mcp", token: this.token }));
      });

      ws.once("message", (data) => {
        let msg: any;
        try {
          msg = JSON.parse(data.toString());
        } catch {
          return fail(new Error("Malformed handshake reply from broker"));
        }
        if (msg.type === "welcome") {
          settled = true;
          this.ws = ws;
          this._wireOpenSocket(ws);
          resolve();
        } else {
          fail(new Error(`Broker rejected handshake: ${msg?.error?.code ?? "unknown"}`));
        }
      });

      setTimeout(() => fail(new Error("Broker handshake timed out")), 4000);
    });
  }

  private _wireOpenSocket(ws: WebSocket): void {
    ws.on("message", (data) => this._onMessage(data));
    ws.on("close", () => {
      this.ws = null;
      // Fail everything still waiting; callers can retry, which reconnects.
      for (const [, p] of this.pending) {
        clearTimeout(p.timer);
        p.reject(new Error("Broker connection closed"));
      }
      this.pending.clear();
    });
  }

  private _onMessage(data: WebSocket.RawData): void {
    let msg: any;
    try {
      msg = JSON.parse(data.toString());
    } catch {
      return;
    }
    const cid = msg.correlationId;
    if (!cid) return;
    const p = this.pending.get(cid);
    if (!p) return;
    clearTimeout(p.timer);
    this.pending.delete(cid);
    if (msg.error) {
      const err = new Error(msg.error.message || "Broker error") as BrokerError;
      err.code = msg.error.code;
      // Keep the guidance for the AI (F3's envelope renders hint/recommendedTool).
      if (msg.error.hint) err.hint = msg.error.hint;
      if (msg.error.recommendedTool) err.recommendedTool = msg.error.recommendedTool;
      if (msg.error.details !== undefined) err.details = msg.error.details;
      p.reject(err);
    } else if (msg.type === "documents") {
      p.resolve(msg.documents ?? []);
    } else {
      p.resolve(msg.result);
    }
  }

  private _request<T>(payload: object): Promise<T> {
    const correlationId = randomUUID();
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(correlationId);
        reject(new Error("Broker request timed out"));
      }, COMMAND_TIMEOUT_MS);
      this.pending.set(correlationId, { resolve, reject, timer });
      this.ws!.send(JSON.stringify({ ...payload, correlationId }));
    });
  }

  /** Ask the broker for every document registered across all Revit sessions. */
  async listDocuments(): Promise<DocumentMeta[]> {
    await this.ensureConnected();
    return this._request<DocumentMeta[]>({ type: "list_documents" });
  }

  /**
   * Route a command to the plugin session that owns `docId`. Resolves with the
   * plugin's result, or with a {@link RunningResult} when the command is still
   * running past its soft timeout (collect it with getJob).
   */
  async sendCommand(docId: string, command: string, params: any, planId?: string): Promise<any> {
    await this.ensureConnected();
    return this._request({ type: "command", docId, command, params, ...(planId ? { planId } : {}) });
  }

  /** Status/result of a job; long-polls up to `waitSeconds` (broker caps it at 50). */
  async getJob(jobId: string, waitSeconds = 0): Promise<JobView> {
    await this.ensureConnected();
    return this._request<JobView>({ type: "get_job", jobId, waitSeconds });
  }

  /** Propose a change plan; the owning Revit shows it to the user for approval. */
  async planCreate(
    docId: string,
    steps: PlanStep[],
    summary: string,
    reasoning?: string
  ): Promise<{ planId: string; planHash: string; status: string; expiresAt: string; risk: string; docId: string; steps: unknown[] }> {
    await this.ensureConnected();
    return this._request({ type: "plan_create", docId, steps, summary, reasoning });
  }

  async planGet(planId: string): Promise<PlanView> {
    await this.ensureConnected();
    return this._request<PlanView>({ type: "plan_get", planId });
  }

  /** Resolves when the plan leaves `pending` or after `waitSeconds` (≤ 50). */
  async planWait(planId: string, waitSeconds = 50): Promise<PlanView> {
    await this.ensureConnected();
    return this._request<PlanView>({ type: "plan_wait", planId, waitSeconds });
  }

  /** Run the remaining steps of an approved plan (one execute_batch when possible). */
  async planExecute(planId: string): Promise<any> {
    await this.ensureConnected();
    return this._request({ type: "plan_execute", planId });
  }

  private _spawnBroker(): void {
    const cli = resolveBrokerCli();
    if (!cli) return; // best effort; the retry loop will surface a clear error
    try {
      const child = spawn(process.execPath, [cli], {
        detached: true,
        stdio: "ignore",
        windowsHide: true,
      });
      child.unref();
    } catch {
      /* best effort */
    }
  }
}

/**
 * Find broker/src/cli.js. In this repo the broker sits next to server/.
 * An explicit REVIT_MCP_BROKER_CMD wins, for packaged/relocated installs.
 */
function resolveBrokerCli(): string | null {
  if (process.env.REVIT_MCP_BROKER_CMD) return process.env.REVIT_MCP_BROKER_CMD;
  const here = path.dirname(fileURLToPath(import.meta.url)); // server/build/utils
  const candidates = [
    path.resolve(here, "../../../broker/src/cli.js"), // repo layout (built)
    path.resolve(here, "../../broker/src/cli.js"),
    path.resolve(here, "../../../../broker/src/cli.js"),
  ];
  return candidates.find((p) => fs.existsSync(p)) ?? null;
}

function delay(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

/** Process-wide singleton: one broker link per MCP server. */
export const broker = new BrokerConnection();

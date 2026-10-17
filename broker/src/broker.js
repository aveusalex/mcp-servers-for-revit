import crypto from "crypto";
import { WebSocketServer } from "ws";
import {
  HOST,
  PORT,
  HEARTBEAT_INTERVAL_MS,
  COMMAND_TIMEOUT_MS,
  MAX_WAIT_S,
  JOB_RETENTION_MS,
  MAX_MESSAGE_BYTES,
  MAX_BATCH_STEPS,
  MAX_INFLIGHT_PER_MCP,
  approvalMode,
  auditParamsMode,
  loadOrCreateToken,
} from "./config.js";
import { Registry } from "./registry.js";
import { AuditLog, auditParams } from "./audit.js";
import { canonicalHash, canonicalJson, stepArgs } from "./canonical.js";
import { classify, classifyBatch, extractElementIds, maxRisk, needsApproval, parseManifest } from "./classify.js";
import { PlanStore, publicPlan } from "./plans.js";

/** Hard limit on any frame; the 2 MB contract limit is enforced per message below. */
const WS_MAX_PAYLOAD = 64 * 1024 * 1024;

/**
 * The broker is the single loopback listener. Two kinds of client connect to
 * it and are told apart at handshake:
 *
 *   - `plugin`: a Revit add-in (one per Revit process). Connects OUT to the
 *     broker, registers its open documents (and its tool manifest), heartbeats,
 *     executes commands, and relays the human's plan decisions.
 *   - `mcp`: a Claude MCP server (one per chat). Sends command envelopes and
 *     control requests; the broker fans replies back by correlationId.
 *
 * Nothing here holds a notion of a global "current connection": N mcp clients
 * and N plugin sessions coexist, and every command carries its own docId.
 *
 * On top of routing the broker owns the safety rails (Contract E):
 *   - classification of every command (kind/risk) from the owning session's
 *     manifest, a static legacy table, or fail-closed write/medium;
 *   - the human-approval gate (REVIT_MCP_APPROVAL) backed by the plan store;
 *   - jobs: a command slower than its tool's soft timeout is answered with
 *     {status:"running", jobId} and can be collected later with get_job;
 *   - limits (message size, batch size, in-flight per MCP client) and audit.
 */
export class Broker {
  /**
   * @param {object} [opts]
   * @param {string} [opts.host]
   * @param {number} [opts.port]
   * @param {string} [opts.token]
   * @param {AuditLog} [opts.audit]
   * @param {number} [opts.sweepIntervalMs]
   * @param {number} [opts.sessionTtlMs]
   * @param {"ask_first"|"auto"|"look_only"} [opts.approvalMode]
   * @param {"hash"|"full"} [opts.auditParamsMode]
   * @param {number} [opts.commandTimeoutMs] hard ceiling per command
   * @param {number} [opts.softTimeoutMs] overrides every tool's soft timeout (tests)
   * @param {PlanStore} [opts.plans]
   * @param {string} [opts.plansDir]
   * @param {number} [opts.planTtlMs]
   * @param {number} [opts.maxMessageBytes]
   * @param {number} [opts.maxBatchSteps]
   * @param {number} [opts.maxInflightPerMcp]
   * @param {number} [opts.jobRetentionMs]
   */
  constructor(opts = {}) {
    this.host = opts.host ?? HOST;
    this.port = opts.port ?? PORT;
    this.token = opts.token ?? loadOrCreateToken();
    this.audit = opts.audit ?? new AuditLog();
    this.sweepIntervalMs = opts.sweepIntervalMs ?? HEARTBEAT_INTERVAL_MS;
    this.registry = new Registry(opts.sessionTtlMs);

    // An undefined option falls through to REVIT_MCP_APPROVAL (default param).
    this.approvalMode = approvalMode(opts.approvalMode);
    this.auditParamsMode = opts.auditParamsMode ?? auditParamsMode();
    this.commandTimeoutMs = opts.commandTimeoutMs ?? COMMAND_TIMEOUT_MS;
    this.softTimeoutMs = opts.softTimeoutMs ?? null;
    this.maxMessageBytes = opts.maxMessageBytes ?? MAX_MESSAGE_BYTES;
    this.maxBatchSteps = opts.maxBatchSteps ?? MAX_BATCH_STEPS;
    this.maxInflightPerMcp = opts.maxInflightPerMcp ?? MAX_INFLIGHT_PER_MCP;
    this.jobRetentionMs = opts.jobRetentionMs ?? JOB_RETENTION_MS;
    this.plans = opts.plans ?? new PlanStore({ dir: opts.plansDir, ttlMs: opts.planTtlMs });

    /** @type {Set<import("ws").WebSocket>} MCP clients, for lifecycle only. */
    this.mcpClients = new Set();

    /** sessionId -> Map(toolName -> manifest entry), from register.tools. */
    this.sessionTools = new Map();

    /**
     * Requests sent to a plugin and not yet answered:
     * pluginCorrelationId -> { ws, sessionId, docId, command, resolve, timer }.
     */
    this.pluginPending = new Map();

    /**
     * Jobs: one per routed command or plan execution, keyed by jobId (= the
     * MCP correlationId). Kept after completion for get_job.
     * @type {Map<string, object>}
     */
    this.jobs = new Map();

    /** canonical {docId, command, params} hash -> jobId, for running jobs. */
    this.inflightKeys = new Map();

    this._wss = null;
    this._sweepTimer = null;
  }

  /** @returns {Promise<void>} resolves once listening (or rejects on EADDRINUSE). */
  start() {
    return new Promise((resolve, reject) => {
      this._wss = new WebSocketServer({ host: this.host, port: this.port, maxPayload: WS_MAX_PAYLOAD });
      this._wss.on("connection", (ws) => this._onConnection(ws));
      this._wss.on("listening", () => {
        this._sweepTimer = setInterval(() => this._sweep(), this.sweepIntervalMs);
        this.audit.write({
          event: "broker_start",
          host: this.host,
          port: this.port,
          approvalMode: this.approvalMode,
        });
        resolve();
      });
      this._wss.on("error", (err) => reject(err));
    });
  }

  async stop() {
    if (this._sweepTimer) clearInterval(this._sweepTimer);
    for (const { timer } of this.pluginPending.values()) clearTimeout(timer);
    this.pluginPending.clear();
    for (const job of this.jobs.values()) {
      clearTimeout(job.softTimer);
      for (const w of job.waiters) w();
    }
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
    ws._clientId = null;
    ws.on("message", (data) => this._onMessage(ws, data));
    ws.on("close", () => this._onClose(ws));
    ws.on("error", () => {}); // errors surface as 'close'
  }

  _onClose(ws) {
    if (ws._role === "plugin") {
      this._failPluginRequests((e) => e.ws === ws, "PLUGIN_DISCONNECTED");
      const sessionId = this.registry.removeBySocket(ws);
      if (sessionId) {
        this.sessionTools.delete(sessionId);
        this._failPluginRequests((e) => e.sessionId === sessionId, "PLUGIN_DISCONNECTED");
        this.audit.write({ event: "session_closed", sessionId });
      }
    } else if (ws._role === "mcp") {
      this.mcpClients.delete(ws);
      // The commands keep running in Revit; their jobs stay collectable.
      for (const job of this.jobs.values()) {
        if (job.ownerWs === ws) {
          job.ownerWs = null;
          job.detached = true;
        }
      }
    }
  }

  _onMessage(ws, data) {
    const size = typeof data === "string" ? Buffer.byteLength(data) : data.length;
    if (ws._role !== "plugin" && size > this.maxMessageBytes) {
      // Don't parse it; fish the correlationId out so the caller isn't left hanging.
      const head = data.toString("utf8", 0, Math.min(size, 4096));
      const m = /"correlationId"\s*:\s*"([^"]{1,200})"/.exec(head);
      this.audit.write({ event: "message_rejected", reason: "MESSAGE_TOO_LARGE", bytes: size });
      return this._send(ws, {
        type: "response",
        correlationId: m ? m[1] : undefined,
        error: {
          code: "MESSAGE_TOO_LARGE",
          message: `Message of ${size} bytes exceeds the ${this.maxMessageBytes}-byte limit`,
          hint: "Split the request (fewer elements or steps per call).",
        },
      });
    }

    let msg;
    try {
      msg = JSON.parse(data.toString());
    } catch {
      return this._send(ws, { type: "error", error: { code: "BAD_JSON", message: "Invalid JSON" } });
    }
    if (!msg || typeof msg !== "object") {
      return this._send(ws, { type: "error", error: { code: "BAD_JSON", message: "Expected a JSON object" } });
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
        ws._clientId = "mcp_" + crypto.randomBytes(4).toString("hex");
        this.mcpClients.add(ws);
      } else {
        return ws.close();
      }
      return this._send(ws, { type: "welcome", role: ws._role, approvalMode: this.approvalMode });
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
        // The manifest only rides on register; heartbeats keep the last one.
        if (msg.tools !== undefined && msg.tools !== null) {
          this.sessionTools.set(msg.sessionId, parseManifest(msg.tools));
        }
        if (msg.type === "register") this._resendPendingPlans(ws, msg.documents || []);
        return;
      }
      case "response":
        return this._onPluginResponse(msg);
      case "plan_decision":
        return this._onPlanDecision(ws, msg);
      default:
        return;
    }
  }

  _onPluginResponse(msg) {
    const entry = this.pluginPending.get(msg.correlationId);
    if (!entry) return; // already timed out or unknown
    clearTimeout(entry.timer);
    this.pluginPending.delete(msg.correlationId);
    entry.resolve({ result: msg.result, error: msg.error });
  }

  /** A (re)connecting plugin gets the still-pending plans for its documents. */
  _resendPendingPlans(ws, documents) {
    const docIds = documents.map((d) => d && d.docId).filter(Boolean);
    for (const plan of this.plans.pendingForDocs(docIds)) {
      this._send(ws, { type: "plan_pending", plan: publicPlan(plan), planHash: plan.planHash });
    }
  }

  /**
   * The human's verdict, relayed by the plugin. Only the plugin connection
   * that currently owns the plan's document may decide: an MCP client can
   * never approve its own plan, and one Revit cannot approve for another.
   */
  _onPlanDecision(ws, msg) {
    const ack = (extra) =>
      this._send(ws, { type: "plan_decision_ack", correlationId: msg.correlationId, planId: msg.planId, ...extra });

    const plan = this.plans.get(msg.planId);
    if (!plan) return ack({ ok: false, error: { code: "PLAN_NOT_FOUND", message: `No plan ${msg.planId}` } });

    const doc = this.registry.documents.get(plan.docId);
    const session = doc ? this.registry.sessions.get(doc.sessionId) : null;
    if (!doc || !session || session.ws !== ws) {
      this.audit.write({
        event: "plan_decision_rejected",
        planId: plan.planId,
        docId: plan.docId,
        sessionId: ws._sessionId,
        reason: "NOT_DOCUMENT_OWNER",
      });
      return ack({
        ok: false,
        error: { code: "NOT_DOCUMENT_OWNER", message: "Only the Revit session that owns the document can decide this plan" },
      });
    }

    const r = this.plans.decide(plan.planId, {
      planHash: msg.planHash,
      decision: msg.decision,
      approver: msg.approver,
      note: msg.note,
      sessionId: ws._sessionId,
    });
    this.audit.write({
      event: r.ok ? "plan_decision" : "plan_decision_rejected",
      planId: plan.planId,
      planHash: plan.planHash,
      docId: plan.docId,
      sessionId: ws._sessionId,
      decision: msg.decision,
      approver: r.ok ? r.plan.decision.approver : msg.approver,
      reason: r.ok ? undefined : r.code,
    });
    return r.ok ? ack({ ok: true, status: r.plan.status }) : ack({ ok: false, error: { code: r.code, message: r.message } });
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
      case "get_job":
        return this._getJob(ws, msg);
      case "plan_create":
        return this._planCreate(ws, msg);
      case "plan_get":
        return this._planGet(ws, msg);
      case "plan_wait":
        return this._planWait(ws, msg);
      case "plan_execute":
        return this._planExecute(ws, msg);
      default:
        return this._send(ws, {
          type: "error",
          correlationId: msg.correlationId,
          error: { code: "UNKNOWN_TYPE", message: `Unknown message type: ${msg.type}` },
        });
    }
  }

  _reply(ws, correlationId, payload) {
    this._send(ws, { type: "response", correlationId, ...payload });
  }

  _replyError(ws, correlationId, code, message, extra = {}) {
    this._reply(ws, correlationId, { error: { code, message, ...extra } });
  }

  /**
   * Route one command envelope to the plugin session that owns its docId.
   * Envelope: { type:"command", correlationId, docId, command, params, planId? }.
   */
  _routeCommand(mcpWs, msg) {
    const correlationId = msg.correlationId || crypto.randomUUID();
    const { docId, command, planId } = msg;
    const params = msg.params ?? {};

    const doc = docId ? this.registry.documents.get(docId) : null;
    const target = docId ? this.registry.sessionForDoc(docId) : null;
    if (!doc || !target) {
      this.audit.write({ event: "command_rejected", correlationId, docId, command, reason: "DOCUMENT_NOT_FOUND" });
      return this._replyError(
        mcpWs,
        correlationId,
        "DOCUMENT_NOT_FOUND",
        docId ? `No open document with id ${docId}` : "No docId supplied and it could not be resolved"
      );
    }
    if (typeof command !== "string" || !command) {
      return this._replyError(mcpWs, correlationId, "BAD_REQUEST", "command must be a non-empty string");
    }

    const reject = (code, message, extra = {}) => {
      this.audit.write({ event: "command_rejected", correlationId, docId, command, planId, reason: code });
      return this._replyError(mcpWs, correlationId, code, message, extra);
    };

    if (command === "execute_batch") {
      const steps = params.steps;
      if (!Array.isArray(steps) || steps.length === 0) {
        return reject("BAD_REQUEST", "execute_batch needs a non-empty steps array");
      }
      if (steps.length > this.maxBatchSteps) {
        return reject("BATCH_TOO_LARGE", `A batch may have at most ${this.maxBatchSteps} steps (got ${steps.length})`);
      }
      if (steps.some((s) => !s || typeof s.tool !== "string" || s.tool === "execute_batch")) {
        return reject("BAD_REQUEST", "Each step needs a tool name; batches cannot be nested");
      }
    }

    const manifest = this.sessionTools.get(doc.sessionId);
    const cls = classify(command, params, manifest);

    // Idempotency: an identical command already running is not sent again.
    const key = canonicalHash({ docId, command, params, planId: planId ?? null });
    const runningId = this.inflightKeys.get(key);
    const running = runningId ? this.jobs.get(runningId) : null;
    if (running && running.status === "running") {
      this.audit.write({ event: "command_deduplicated", correlationId, jobId: running.jobId, docId, command });
      return this._reply(mcpWs, correlationId, { result: this._runningPayload(running, true) });
    }

    // A dry run is rolled back by the framework dispatcher, so it changes
    // nothing and needs no approval. Only trusted for framework tools: legacy
    // commands ignore dryRun and would really write.
    const trustedDryRun =
      params.dryRun === true &&
      (command === "execute_batch"
        ? manifest?.has("execute_batch") === true && params.steps.every((s) => manifest.has(s.tool))
        : manifest?.has(command) === true);

    if (this.approvalMode === "look_only" && cls.kind === "write" && !trustedDryRun) {
      return reject("LOOK_ONLY", `'${command}' changes the model and the broker is in look_only mode`, {
        hint: "Only read tools are allowed. Ask the user to change REVIT_MCP_APPROVAL if edits are intended.",
      });
    }

    if (this._inflightFor(mcpWs) >= this.maxInflightPerMcp) {
      return reject("OVERLOADED", `Too many commands in flight (limit ${this.maxInflightPerMcp})`, {
        hint: "Wait for running jobs to finish (get_job_result) before sending more.",
      });
    }

    // Approval gate.
    let planCtx = null;
    if (planId) {
      const plan = this.plans.get(planId);
      const check = this.plans.checkExecutable(plan, docId);
      if (!check.ok) return reject(check.code, check.message);
      const indexes = this._matchPlanSteps(plan, command, params);
      if (!indexes) {
        return reject("PLAN_STEP_MISMATCH", `No unexecuted step of ${planId} matches '${command}' with these exact args`, {
          hint: "Use execute_plan, or resend the step exactly as it appears in the approved plan.",
        });
      }
      if (!this.plans.claim(plan, indexes, correlationId)) {
        return reject("PLAN_STEP_ALREADY_EXECUTED", `That step of ${planId} has already been claimed`);
      }
      planCtx = { plan, indexes, mode: command === "execute_batch" ? "batch" : "single" };
    } else if (this.approvalMode === "ask_first" && needsApproval(cls.risk) && !trustedDryRun) {
      const step = { tool: command, args: stepArgs(params) };
      return reject(
        "APPROVAL_REQUIRED",
        `'${command}' is a ${cls.risk}-risk ${cls.kind} and needs the user's approval in Revit before it runs.`,
        {
          hint:
            "Call propose_change_plan with steps=[" + truncate(JSON.stringify(step), 4000) + "] (exactly these " +
            "args), tell the user to review it in the Revit approval window, call wait_for_plan_decision, then " +
            "execute_plan." +
            (cls.source === "manifest" ? " To preview the effect first, call the tool with dryRun=true (no approval needed)." : ""),
          recommendedTool: "propose_change_plan",
          details: { docId, risk: cls.risk, kind: cls.kind, step },
        }
      );
    }

    const batchId = command === "execute_batch" ? "batch_" + correlationId : null;
    const sessionId = doc.sessionId;
    this._startJob({
      mcpWs,
      jobId: correlationId,
      docId,
      command,
      params,
      cls,
      key,
      planCtx,
      batchId,
      run: () =>
        this._sendToPlugin(sessionId, target.ws, {
          type: "command",
          correlationId,
          docId,
          command,
          params,
          kind: cls.kind,
          risk: cls.risk,
          ...(batchId ? { batchId } : {}),
          ...(planId ? { planId } : {}),
        }),
    });
  }

  /** Step indexes of `plan` that this command would execute, or null. */
  _matchPlanSteps(plan, command, params) {
    if (command === "execute_batch") {
      const pending = plan.steps.filter((s) => s.status === "pending");
      const want = canonicalJson(pending.map((s) => ({ tool: s.tool, args: stepArgs(s.args) })));
      const got = canonicalJson((params.steps ?? []).map((s) => ({ tool: s?.tool, args: stepArgs(s?.args ?? {}) })));
      return pending.length && want === got ? pending.map((s) => s.index) : null;
    }
    const i = this.plans.findStep(plan, command, params);
    return i < 0 ? null : [i];
  }

  _inflightFor(mcpWs) {
    let n = 0;
    for (const job of this.jobs.values()) if (job.status === "running" && job.ownerWs === mcpWs) n++;
    return n;
  }

  // --- jobs -----------------------------------------------------------------

  /**
   * Start a job and answer its MCP caller either with the final result (fast
   * path) or, once the soft timeout passes, with {status:"running", jobId}.
   *
   * `run` resolves to {result, error, stepResults?, mode?}; it never rejects
   * for plugin-level failures.
   */
  _startJob({ mcpWs, jobId, docId, command, params, cls, key, planCtx, batchId, run }) {
    const job = {
      jobId,
      docId,
      command,
      kind: cls.kind,
      risk: cls.risk,
      planId: planCtx?.plan.planId ?? null,
      planCtx,
      batchId: batchId ?? null,
      key: key ?? null,
      ownerWs: mcpWs,
      status: "running",
      detached: false,
      startedAt: Date.now(),
      finishedAt: null,
      result: undefined,
      error: undefined,
      waiters: new Set(),
      softTimer: null,
    };
    this.jobs.set(jobId, job);
    if (job.key) this.inflightKeys.set(job.key, jobId);

    this.audit.write({
      event: "command",
      correlationId: jobId,
      jobId,
      docId,
      command,
      kind: cls.kind,
      risk: cls.risk,
      classifiedBy: cls.source,
      planId: job.planId,
      batchId: job.batchId,
      ...auditParams(params, this.auditParamsMode),
    });

    const softMs = this.softTimeoutMs ?? Math.max(1, cls.timeoutSeconds) * 1000;
    job.softTimer = setTimeout(() => {
      if (job.status !== "running" || job.detached) return;
      job.detached = true;
      this.audit.write({ event: "job_running", jobId, docId, command, afterMs: softMs });
      if (job.ownerWs) this._reply(job.ownerWs, jobId, { result: this._runningPayload(job, false) });
    }, softMs);

    Promise.resolve()
      .then(run)
      .then(
        (out) => this._finishJob(job, out ?? {}),
        (err) => this._finishJob(job, { error: { code: "INTERNAL_ERROR", message: String(err?.message ?? err) } })
      );
    return job;
  }

  _runningPayload(job, duplicate) {
    return {
      status: "running",
      jobId: job.jobId,
      command: job.command,
      ...(duplicate ? { duplicate: true } : {}),
      message:
        `'${job.command}' is still running in Revit` +
        (duplicate ? " (an identical command was already in progress; it was not sent again)" : "") +
        `. Call get_job_result with jobId "${job.jobId}" to collect the result.`,
    };
  }

  _finishJob(job, { result, error, stepResults, mode }) {
    if (job.status !== "running") return;
    clearTimeout(job.softTimer);
    const failed = isFailure(result, error);
    job.status = failed ? "failed" : "succeeded";
    job.result = result;
    job.error = error;
    job.finishedAt = Date.now();
    if (job.key && this.inflightKeys.get(job.key) === job.jobId) this.inflightKeys.delete(job.key);

    if (job.planCtx) {
      const { plan, indexes } = job.planCtx;
      this.plans.complete(plan, indexes, {
        ok: !failed,
        result,
        error,
        stepResults,
        mode: mode ?? job.planCtx.mode,
        jobId: job.jobId,
        batchId: job.batchId,
      });
      this.audit.write({ event: "plan_executed", planId: plan.planId, docId: plan.docId, jobId: job.jobId, status: plan.status });
    }

    this.audit.write({
      event: "response",
      correlationId: job.jobId,
      jobId: job.jobId,
      docId: job.docId,
      command: job.command,
      risk: job.risk,
      planId: job.planId,
      batchId: job.batchId,
      ok: !failed,
      errorCode: error?.code,
      durationMs: job.finishedAt - job.startedAt,
      detached: job.detached,
    });

    if (!job.detached && job.ownerWs) this._reply(job.ownerWs, job.jobId, { result, error });
    for (const w of [...job.waiters]) w();
  }

  _jobView(job) {
    return {
      jobId: job.jobId,
      status: job.status,
      command: job.command,
      docId: job.docId,
      risk: job.risk,
      planId: job.planId,
      batchId: job.batchId,
      startedAt: new Date(job.startedAt).toISOString(),
      finishedAt: job.finishedAt ? new Date(job.finishedAt).toISOString() : null,
      elapsedMs: (job.finishedAt ?? Date.now()) - job.startedAt,
      ...(job.status === "running" ? {} : { result: job.result ?? null, error: job.error ?? null }),
    };
  }

  async _getJob(ws, msg) {
    const job = typeof msg.jobId === "string" ? this.jobs.get(msg.jobId) : null;
    if (!job) {
      return this._replyError(ws, msg.correlationId, "JOB_NOT_FOUND", `No job ${msg.jobId} (unknown or expired)`);
    }
    const waitMs = clampWait(msg.waitSeconds);
    if (job.status === "running" && waitMs > 0) {
      await new Promise((resolve) => {
        const done = () => {
          clearTimeout(timer);
          job.waiters.delete(done);
          resolve();
        };
        const timer = setTimeout(done, waitMs);
        job.waiters.add(done);
      });
    }
    this._reply(ws, msg.correlationId, { result: this._jobView(job) });
  }

  /**
   * Send one request to a plugin; resolves with {result, error} when it
   * answers, when the hard timeout passes, or when the plugin goes away.
   */
  _sendToPlugin(sessionId, ws, envelope) {
    return new Promise((resolve) => {
      const cid = envelope.correlationId;
      const timer = setTimeout(() => {
        this.pluginPending.delete(cid);
        resolve({
          error: { code: "TIMEOUT", message: `Command timed out after ${this.commandTimeoutMs}ms: ${envelope.command}` },
        });
      }, this.commandTimeoutMs);
      this.pluginPending.set(cid, { ws, sessionId, docId: envelope.docId, command: envelope.command, resolve, timer });
      this._send(ws, envelope);
    });
  }

  // --- plans ----------------------------------------------------------------

  _planCreate(ws, msg) {
    const cid = msg.correlationId;
    const { docId, steps, summary, reasoning } = msg;
    const doc = docId ? this.registry.documents.get(docId) : null;
    const target = docId ? this.registry.sessionForDoc(docId) : null;
    if (!doc || !target) return this._replyError(ws, cid, "DOCUMENT_NOT_FOUND", `No open document with id ${docId}`);

    if (!Array.isArray(steps) || steps.length === 0) {
      return this._replyError(ws, cid, "BAD_REQUEST", "A plan needs a non-empty steps array");
    }
    if (steps.length > this.maxBatchSteps) {
      return this._replyError(ws, cid, "BATCH_TOO_LARGE", `A plan may have at most ${this.maxBatchSteps} steps`);
    }
    for (const s of steps) {
      if (!s || typeof s.tool !== "string" || !s.tool) {
        return this._replyError(ws, cid, "BAD_REQUEST", "Each step needs {tool, args}");
      }
      if (s.args != null && (typeof s.args !== "object" || Array.isArray(s.args))) {
        return this._replyError(ws, cid, "BAD_REQUEST", `args of step '${s.tool}' must be an object`);
      }
      if (s.tool === "execute_batch") {
        return this._replyError(ws, cid, "BAD_REQUEST", "List the individual steps; the broker batches them itself");
      }
    }
    if (typeof summary !== "string" || !summary.trim()) {
      return this._replyError(ws, cid, "BAD_REQUEST", "A plan needs a human-readable summary");
    }

    const manifest = this.sessionTools.get(doc.sessionId);
    const classified = steps.map((s) => {
      const args = stepArgs(s.args ?? {});
      const c = classify(s.tool, args, manifest);
      return { tool: s.tool, args, kind: c.kind, risk: c.risk, affectedElementIds: extractElementIds(args) };
    });
    if (this.approvalMode === "look_only" && classified.some((s) => s.kind === "write")) {
      return this._replyError(ws, cid, "LOOK_ONLY", "The broker is in look_only mode; plans with writes are refused");
    }

    const plan = this.plans.create({
      docId,
      docTitle: doc.title,
      steps: classified,
      summary: summary.slice(0, 4000),
      reasoning: typeof reasoning === "string" ? reasoning.slice(0, 8000) : undefined,
      risk: maxRisk(classified.map((s) => s.risk)),
      createdBy: ws._clientId,
    });
    this.audit.write({
      event: "plan_created",
      planId: plan.planId,
      planHash: plan.planHash,
      docId,
      risk: plan.risk,
      steps: plan.steps.map((s) => s.tool),
      createdBy: ws._clientId,
    });

    if (this.approvalMode === "auto") {
      this.plans.autoApprove(plan.planId);
      this.audit.write({ event: "plan_decision", planId: plan.planId, decision: "approved", approver: "auto-mode" });
    } else {
      this._send(target.ws, { type: "plan_pending", plan: publicPlan(plan), planHash: plan.planHash });
    }

    this._reply(ws, cid, {
      result: {
        planId: plan.planId,
        planHash: plan.planHash,
        status: plan.status,
        expiresAt: plan.expiresAt,
        risk: plan.risk,
        docId,
        steps: plan.steps.map((s) => ({ index: s.index, tool: s.tool, kind: s.kind, risk: s.risk })),
      },
    });
  }

  _planGet(ws, msg) {
    const plan = this.plans.get(msg.planId);
    if (!plan) return this._replyError(ws, msg.correlationId, "PLAN_NOT_FOUND", `No plan ${msg.planId}`);
    this._reply(ws, msg.correlationId, { result: publicPlan(plan) });
  }

  async _planWait(ws, msg) {
    if (!this.plans.get(msg.planId)) {
      return this._replyError(ws, msg.correlationId, "PLAN_NOT_FOUND", `No plan ${msg.planId}`);
    }
    const plan = await this.plans.waitForDecision(msg.planId, clampWait(msg.waitSeconds));
    this._reply(ws, msg.correlationId, { result: publicPlan(plan) });
  }

  /**
   * Execute every remaining step of an approved plan. Several steps go to the
   * plugin as ONE execute_batch (one TransactionGroup, one Ctrl+Z) when the
   * owning session exposes execute_batch and every step is batchable; legacy
   * commands, which cannot be batched, run one after another, stopping at the
   * first failure.
   */
  _planExecute(ws, msg) {
    const cid = msg.correlationId || crypto.randomUUID();
    const plan = this.plans.get(msg.planId);
    const fail = (code, message, extra) => {
      this.audit.write({ event: "plan_execute_rejected", planId: msg.planId, reason: code });
      return this._replyError(ws, cid, code, message, extra);
    };
    if (!plan) return fail("PLAN_NOT_FOUND", `No plan ${msg.planId}`);
    const check = this.plans.checkExecutable(plan, plan.docId);
    if (!check.ok) return fail(check.code, check.message);

    const doc = this.registry.documents.get(plan.docId);
    if (!doc || !this.registry.sessionForDoc(plan.docId)) {
      return fail("DOCUMENT_NOT_FOUND", `The plan's document ${plan.docId} is not open`);
    }
    if (this.approvalMode === "look_only" && plan.steps.some((s) => s.kind === "write")) {
      return fail("LOOK_ONLY", "The broker is in look_only mode");
    }
    if (this._inflightFor(ws) >= this.maxInflightPerMcp) {
      return fail("OVERLOADED", `Too many commands in flight (limit ${this.maxInflightPerMcp})`);
    }

    const pending = plan.steps.filter((s) => s.status === "pending");
    if (pending.length === 0) return fail("PLAN_ALREADY_EXECUTED", `Plan ${plan.planId} has nothing left to run`);

    const manifest = this.sessionTools.get(doc.sessionId);
    const canBatch =
      manifest?.has("execute_batch") &&
      pending.every((s) => {
        const m = manifest.get(s.tool);
        return m && m.batchable && m.kind !== "ui";
      });
    const mode = pending.length === 1 ? "single" : canBatch ? "batch" : "sequential";
    const indexes = pending.map((s) => s.index);

    if (!this.plans.claim(plan, indexes, cid)) {
      return fail("PLAN_STEP_ALREADY_EXECUTED", `Plan ${plan.planId} is already being executed`);
    }

    const docId = plan.docId;
    const stepsWire = pending.map((s) => ({ tool: s.tool, args: s.args }));
    const command = mode === "single" ? pending[0].tool : mode === "batch" ? "execute_batch" : "plan_sequence";
    const params =
      mode === "single" ? pending[0].args : mode === "batch" ? { steps: stepsWire, stopOnError: true } : { steps: stepsWire };
    const cls =
      mode === "single" ? classify(command, params, manifest) : classifyBatch({ steps: stepsWire }, manifest);
    const batchId = mode === "batch" ? "batch_" + cid : null;

    const sendOne = (correlationId, tool, args, extra = {}) => {
      const session = this.registry.sessionForDoc(docId);
      if (!session) {
        return Promise.resolve({ error: { code: "DOCUMENT_NOT_FOUND", message: `Document ${docId} closed mid-plan` } });
      }
      const c = classify(tool, args, manifest);
      // An approved plan really runs: say dryRun:false explicitly, because tools with DryRunDefault
      // (bulk edits) would otherwise only preview. Plans store steps without dryRun:false.
      const wireArgs =
        tool !== "execute_batch" && manifest?.get(tool)?.kind === "write" && !("dryRun" in (args ?? {}))
          ? { ...args, dryRun: false }
          : args;
      return this._sendToPlugin(this.registry.documents.get(docId)?.sessionId, session.ws, {
        type: "command",
        correlationId,
        docId,
        command: tool,
        params: wireArgs,
        kind: c.kind,
        risk: c.risk,
        planId: plan.planId,
        ...extra,
      });
    };

    const run = async () => {
      if (mode === "single") return { ...(await sendOne(cid, command, params)), mode };
      if (mode === "batch") {
        const out = await sendOne(cid, "execute_batch", params, { batchId, kind: cls.kind, risk: cls.risk });
        return { ...out, mode };
      }
      const stepResults = [];
      let firstError = null;
      for (let n = 0; n < pending.length; n++) {
        if (firstError) {
          stepResults.push({ index: pending[n].index, tool: pending[n].tool, ok: false, skipped: true });
          continue;
        }
        const r = await sendOne(`${cid}:${n}`, pending[n].tool, pending[n].args);
        const ok = !isFailure(r.result, r.error);
        stepResults.push({ index: pending[n].index, tool: pending[n].tool, ok, result: r.result, error: r.error });
        if (!ok) firstError = r.error ?? { code: "STEP_FAILED", message: `Step ${pending[n].index} (${pending[n].tool}) failed` };
      }
      return {
        mode,
        stepResults,
        result: { mode, atomic: false, steps: stepResults },
        error: firstError
          ? {
              ...firstError,
              hint:
                "Steps run one by one (legacy commands cannot be batched); earlier steps were applied. " +
                "Check the result per step.",
            }
          : undefined,
      };
    };

    this._startJob({
      mcpWs: ws,
      jobId: cid,
      docId,
      command,
      params,
      cls,
      key: null,
      planCtx: { plan, indexes, mode },
      batchId,
      run,
    });
  }

  // --- failure fan-out ------------------------------------------------------

  /** Resolve (as failed) every outstanding plugin request matching `pred`. */
  _failPluginRequests(pred, code) {
    for (const [cid, entry] of this.pluginPending) {
      if (!pred(entry)) continue;
      clearTimeout(entry.timer);
      this.pluginPending.delete(cid);
      entry.resolve({ error: { code, message: "Owning Revit session disconnected mid-command" } });
    }
  }

  _sweep() {
    const dead = this.registry.sweep();
    for (const sessionId of dead) {
      this.sessionTools.delete(sessionId);
      this._failPluginRequests((e) => e.sessionId === sessionId, "SESSION_TIMEOUT");
      this.audit.write({ event: "session_evicted", sessionId });
    }
    const cutoff = Date.now() - this.jobRetentionMs;
    for (const [id, job] of this.jobs) {
      if (job.status !== "running" && job.finishedAt < cutoff) this.jobs.delete(id);
    }
  }

  _send(ws, obj) {
    if (ws && ws.readyState === ws.OPEN) ws.send(JSON.stringify(obj));
  }
}

/** A command failed if the plugin said so, or its envelope/legacy result says so. */
function isFailure(result, error) {
  if (error) return true;
  if (result && typeof result === "object") {
    if (result.ok === false || result.Success === false || result.success === false) return true;
  }
  return false;
}

function truncate(text, max) {
  return text.length > max ? text.slice(0, max) + "...(truncated)" : text;
}

function clampWait(waitSeconds) {
  const s = Number(waitSeconds);
  if (!Number.isFinite(s) || s <= 0) return 0;
  return Math.min(s, MAX_WAIT_S) * 1000;
}

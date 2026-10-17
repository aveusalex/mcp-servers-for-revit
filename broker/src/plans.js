import fs from "fs";
import path from "path";
import crypto from "crypto";
import { EventEmitter } from "events";
import { plansDir, PLAN_TTL_MS } from "./config.js";
import { canonicalHash, canonicalJson } from "./canonical.js";

/**
 * Change plans awaiting (or holding) a human decision.
 *
 * A plan is bound to ONE document: `planHash` is SHA-256 of the canonical JSON
 * of `{docId, steps, summary}`, so an approval shown for one model can never
 * execute against another, and any edit to the steps after display changes
 * the hash the approver signed.
 *
 * Lifecycle: pending -> approved | rejected | expired; approved -> executing
 * -> executed | failed (or expired, if never executed within the TTL).
 *
 * Each step can be claimed exactly once (at-most-once): the claim is an
 * O_EXCL marker file under plans/.claims, created BEFORE the step is sent to
 * Revit, so a crash or a duplicate call can never run a step twice.
 *
 * Files: plans/<planId>.json, plans/<planId>.proof.json, plans/.claims/*.
 */
export class PlanStore extends EventEmitter {
  /**
   * @param {object} [opts]
   * @param {string} [opts.dir]
   * @param {number} [opts.ttlMs]
   * @param {() => number} [opts.now]
   */
  constructor(opts = {}) {
    super();
    this.setMaxListeners(0);
    this.dir = opts.dir ?? plansDir();
    this.ttlMs = opts.ttlMs ?? PLAN_TTL_MS;
    this.now = opts.now ?? (() => Date.now());
    /** @type {Map<string, object>} */
    this.plans = new Map();
    try {
      fs.mkdirSync(path.join(this.dir, ".claims"), { recursive: true });
    } catch {
      /* surfaced on first write */
    }
  }

  static hashOf(docId, steps, summary) {
    return canonicalHash({
      docId,
      steps: steps.map((s) => ({ tool: s.tool, args: s.args ?? {} })),
      summary: summary ?? "",
    });
  }

  /**
   * @param {object} p
   * @param {string} p.docId
   * @param {string} [p.docTitle]
   * @param {{tool:string,args:object,kind:string,risk:string,affectedElementIds?:number[]}[]} p.steps
   * @param {string} p.summary
   * @param {string} [p.reasoning]
   * @param {string} p.risk
   * @param {string} [p.createdBy]
   */
  create(p) {
    const now = this.now();
    const planId = "plan_" + crypto.randomBytes(6).toString("hex");
    const steps = p.steps.map((s, index) => ({
      index,
      tool: s.tool,
      args: s.args ?? {},
      kind: s.kind,
      risk: s.risk,
      affectedElementIds: s.affectedElementIds ?? [],
      status: "pending",
    }));
    const plan = {
      planId,
      planHash: PlanStore.hashOf(p.docId, steps, p.summary),
      docId: p.docId,
      docTitle: p.docTitle ?? null,
      summary: p.summary ?? "",
      reasoning: p.reasoning ?? null,
      risk: p.risk,
      steps,
      status: "pending",
      createdAt: new Date(now).toISOString(),
      expiresAt: new Date(now + this.ttlMs).toISOString(),
      createdBy: p.createdBy ?? null,
      decision: null,
      execution: null,
    };
    this.plans.set(planId, plan);
    this._persist(plan);
    return plan;
  }

  /** @returns {object|null} the plan, with status refreshed for expiry. */
  get(planId) {
    if (typeof planId !== "string" || !/^plan_[0-9a-f]{12}$/.test(planId)) return null;
    let plan = this.plans.get(planId);
    if (!plan) {
      try {
        plan = JSON.parse(fs.readFileSync(this._file(planId), "utf8"));
        this.plans.set(planId, plan);
      } catch {
        return null;
      }
    }
    this._refreshExpiry(plan);
    return plan;
  }

  _refreshExpiry(plan) {
    if ((plan.status === "pending" || plan.status === "approved") && this.now() > Date.parse(plan.expiresAt)) {
      plan.status = "expired";
      this._persist(plan);
      this.emit("changed", plan);
    }
  }

  /**
   * Record a human decision. The caller (broker) has already checked that the
   * decision came from the plugin session owning plan.docId.
   *
   * @returns {{ok:true, plan:object} | {ok:false, code:string, message:string}}
   */
  decide(planId, { planHash, decision, approver, note, sessionId }) {
    const plan = this.get(planId);
    if (!plan) return fail("PLAN_NOT_FOUND", `No plan ${planId}`);
    if (plan.status !== "pending") return fail("PLAN_NOT_PENDING", `Plan ${planId} is ${plan.status}`);
    if (planHash !== plan.planHash) {
      return fail("PLAN_HASH_MISMATCH", "The decision was made on a different version of the plan");
    }
    if (decision !== "approved" && decision !== "rejected") {
      return fail("BAD_DECISION", "decision must be 'approved' or 'rejected'");
    }
    const now = this.now();
    plan.status = decision;
    plan.decision = {
      decision,
      approver: typeof approver === "string" && approver ? approver.slice(0, 200) : "unknown",
      note: typeof note === "string" ? note.slice(0, 2000) : null,
      sessionId: sessionId ?? null,
      decidedAt: new Date(now).toISOString(),
      approvedHash: plan.planHash,
    };
    // The approval window starts at the decision, not at creation.
    if (decision === "approved") plan.expiresAt = new Date(now + this.ttlMs).toISOString();
    this._persist(plan);
    this.emit("changed", plan);
    return { ok: true, plan };
  }

  /** Auto-approve (REVIT_MCP_APPROVAL=auto). */
  autoApprove(planId) {
    const plan = this.get(planId);
    if (!plan) return;
    plan.status = "approved";
    plan.decision = {
      decision: "approved",
      approver: "auto-mode",
      note: "REVIT_MCP_APPROVAL=auto",
      sessionId: null,
      decidedAt: new Date(this.now()).toISOString(),
      approvedHash: plan.planHash,
    };
    this._persist(plan);
    this.emit("changed", plan);
  }

  /**
   * Check that `plan` may run `steps` (indexes) for `docId` right now.
   * @returns {{ok:true} | {ok:false, code:string, message:string}}
   */
  checkExecutable(plan, docId) {
    if (!plan) return fail("PLAN_NOT_FOUND", "Unknown planId");
    this._refreshExpiry(plan);
    if (plan.docId !== docId) {
      return fail("PLAN_DOCUMENT_MISMATCH", `Plan ${plan.planId} was approved for document ${plan.docId}, not ${docId}`);
    }
    if (plan.status === "pending") return fail("PLAN_NOT_APPROVED", `Plan ${plan.planId} is still waiting for a decision`);
    if (plan.status === "rejected") return fail("PLAN_REJECTED", `Plan ${plan.planId} was rejected`);
    if (plan.status === "expired") return fail("PLAN_EXPIRED", `Plan ${plan.planId} expired; propose it again`);
    if (plan.status !== "approved" && plan.status !== "executing") {
      return fail("PLAN_ALREADY_EXECUTED", `Plan ${plan.planId} is ${plan.status}`);
    }
    if (plan.decision?.approvedHash !== plan.planHash) {
      return fail("PLAN_HASH_MISMATCH", "Plan content differs from what was approved");
    }
    return { ok: true };
  }

  /**
   * Find the first unclaimed step matching `{tool, args}` canonically.
   * @returns {number} step index or -1
   */
  findStep(plan, tool, args) {
    const want = canonicalJson(args ?? {});
    for (const s of plan.steps) {
      if (s.status === "pending" && s.tool === tool && canonicalJson(s.args) === want) return s.index;
    }
    return -1;
  }

  /**
   * Atomically claim steps (all or nothing). Creates one O_EXCL marker per
   * step; if any already exists, the ones created here are rolled back.
   *
   * @param {object} plan
   * @param {number[]} indexes
   * @param {string} jobId
   * @returns {boolean}
   */
  claim(plan, indexes, jobId) {
    const created = [];
    for (const i of indexes) {
      const step = plan.steps[i];
      if (!step || step.status !== "pending") return this._unclaim(created, false);
      const marker = path.join(this.dir, ".claims", `${plan.planId}.${i}`);
      try {
        fs.writeFileSync(marker, JSON.stringify({ jobId, at: new Date(this.now()).toISOString() }), { flag: "wx" });
        created.push(marker);
      } catch {
        return this._unclaim(created, false);
      }
    }
    for (const i of indexes) {
      plan.steps[i].status = "running";
      plan.steps[i].jobId = jobId;
    }
    plan.status = "executing";
    this._persist(plan);
    return true;
  }

  _unclaim(markers, result) {
    for (const m of markers) {
      try {
        fs.unlinkSync(m);
      } catch {
        /* ignore */
      }
    }
    return result;
  }

  /**
   * Record the outcome of claimed steps and, when nothing is left to run (or
   * something failed), close the plan and write the proof file.
   *
   * @param {object} plan
   * @param {number[]} indexes
   * @param {{ok:boolean, result?:any, error?:any, mode:string, jobId:string, batchId?:string, stepResults?:any[]}} outcome
   */
  complete(plan, indexes, outcome) {
    const now = new Date(this.now()).toISOString();
    indexes.forEach((i, n) => {
      const step = plan.steps[i];
      if (!step) return;
      const sr = outcome.stepResults?.[n];
      const stepOk = sr ? sr.ok : outcome.ok;
      step.status = stepOk ? "executed" : sr?.skipped ? "skipped" : "failed";
      step.finishedAt = now;
    });
    const anyFailed = plan.steps.some((s) => s.status === "failed");
    const allDone = plan.steps.every((s) => s.status !== "pending" && s.status !== "running");
    if (anyFailed) plan.status = "failed";
    else if (allDone) plan.status = "executed";
    else plan.status = "approved";

    plan.execution = plan.execution ?? { runs: [] };
    plan.execution.runs.push({
      jobId: outcome.jobId,
      batchId: outcome.batchId ?? null,
      mode: outcome.mode,
      steps: indexes,
      ok: outcome.ok,
      finishedAt: now,
    });
    this._persist(plan);

    // Written after every run; for step-by-step execution it is an interim
    // proof until the last step closes the plan.
    this._writeProof(plan, outcome);
    this.emit("changed", plan);
  }

  _writeProof(plan, outcome) {
    const proof = {
      schema: "revit-mcp.proof/1",
      planId: plan.planId,
      planHash: plan.planHash,
      docId: plan.docId,
      docTitle: plan.docTitle,
      summary: plan.summary,
      status: plan.status,
      approver: plan.decision?.approver ?? null,
      decidedAt: plan.decision?.decidedAt ?? null,
      executedAt: new Date(this.now()).toISOString(),
      mode: outcome.mode,
      jobId: outcome.jobId,
      batchId: outcome.batchId ?? null,
      steps: plan.steps.map((s) => ({ index: s.index, tool: s.tool, risk: s.risk, status: s.status })),
      changes: outcome.result?.changes ?? collectChanges(outcome.stepResults),
      result: outcome.result ?? null,
      error: outcome.error ?? null,
    };
    try {
      fs.writeFileSync(this.proofFile(plan.planId), JSON.stringify(proof, null, 2));
    } catch {
      /* proof is best-effort; the plan file still records the run */
    }
    plan.proofPath = this.proofFile(plan.planId);
  }

  proofFile(planId) {
    return path.join(this.dir, `${planId}.proof.json`);
  }

  _file(planId) {
    return path.join(this.dir, `${planId}.json`);
  }

  _persist(plan) {
    try {
      fs.mkdirSync(this.dir, { recursive: true });
      fs.writeFileSync(this._file(plan.planId), JSON.stringify(plan, null, 2));
    } catch {
      /* in-memory copy stays authoritative for this broker run */
    }
  }

  /**
   * Resolve once the plan leaves `pending` (or after timeoutMs).
   * @returns {Promise<object|null>}
   */
  waitForDecision(planId, timeoutMs) {
    const plan = this.get(planId);
    if (!plan || plan.status !== "pending" || timeoutMs <= 0) return Promise.resolve(plan);
    return new Promise((resolve) => {
      const onChange = (p) => {
        if (p.planId === planId && p.status !== "pending") done();
      };
      const done = () => {
        clearTimeout(timer);
        this.off("changed", onChange);
        resolve(this.get(planId));
      };
      const timer = setTimeout(done, timeoutMs);
      this.on("changed", onChange);
    });
  }

  /** Plans to show a plugin that (re)connects: pending ones for its docs. */
  pendingForDocs(docIds) {
    const set = new Set(docIds);
    const out = [];
    for (const plan of this.plans.values()) {
      this._refreshExpiry(plan);
      if (plan.status === "pending" && set.has(plan.docId)) out.push(plan);
    }
    return out;
  }
}

function collectChanges(stepResults) {
  if (!Array.isArray(stepResults)) return null;
  return stepResults.map((r) => r?.result?.changes ?? null);
}

function fail(code, message) {
  return { ok: false, code, message };
}

/** The view of a plan sent over the wire (no internal fields). */
export function publicPlan(plan) {
  if (!plan) return null;
  return {
    planId: plan.planId,
    planHash: plan.planHash,
    docId: plan.docId,
    docTitle: plan.docTitle,
    summary: plan.summary,
    reasoning: plan.reasoning,
    risk: plan.risk,
    status: plan.status,
    createdAt: plan.createdAt,
    expiresAt: plan.expiresAt,
    steps: plan.steps.map((s) => ({
      index: s.index,
      tool: s.tool,
      args: s.args,
      kind: s.kind,
      risk: s.risk,
      status: s.status,
      affectedElementIds: s.affectedElementIds,
    })),
    decision: plan.decision
      ? {
          decision: plan.decision.decision,
          approver: plan.decision.approver,
          note: plan.decision.note,
          decidedAt: plan.decision.decidedAt,
        }
      : null,
    proofPath: plan.proofPath ?? null,
  };
}

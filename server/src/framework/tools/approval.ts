import { broker, isRunningResult, type JobView, type PlanStep, type PlanView } from "../../utils/BrokerConnection.js";
import { withJobGuidance } from "../../utils/ConnectionManager.js";
import { resolveTargetDocId } from "../../utils/targeting.js";
import { asEnvelope, defineServerTool } from "../registry.js";
import { errorEnvelope, normalizeEnvelope, okEnvelope, ToolFailure } from "../envelope.js";

/**
 * Human approval and long-running jobs (FASE0_SPEC, Contract E). The broker
 * owns the gate; these tools only let the AI propose a plan, wait for the
 * person's decision in Revit, run the approved plan and collect slow results.
 * There is deliberately no tool to approve or reject: only the Revit add-in
 * (the person at the keyboard) can send that decision to the broker.
 */

const PLAN_ID = {
  type: "string",
  pattern: "^plan_[0-9a-f]{12}$",
  description: "Plan id returned by propose_change_plan (plan_ + 12 hex characters).",
};

const WAIT_SECONDS = (dflt: number) => ({
  type: "number",
  minimum: 0,
  maximum: 50,
  default: dflt,
  description: `How long to wait for a change, in seconds (0-50, default ${dflt}). Call again to keep waiting.`,
});

/** What the AI should do next for a plan in this state. */
export function nextStepFor(plan: Pick<PlanView, "status" | "planId">): string {
  switch (plan.status) {
    case "pending":
      return `Still waiting for the user to approve or reject in the Revit window. Call wait_for_plan_decision again (planId "${plan.planId}").`;
    case "approved":
      return `Approved. Call execute_plan with planId "${plan.planId}".`;
    case "rejected":
      return "The user rejected the plan. Do not retry it; ask the user what to change.";
    case "expired":
      return "The plan expired before it ran. Propose it again if it is still wanted.";
    case "executing":
      return "The plan is running. Use get_job_result with the jobId execute_plan returned.";
    case "executed":
      return "The plan has been executed. One Ctrl+Z in Revit undoes a batched plan.";
    case "failed":
      return "The plan failed while executing; see the steps and the proof file.";
    default:
      return "";
  }
}

export const proposeChangePlan = defineServerTool<{ steps: PlanStep[]; summary: string; reasoning?: string }>({
  name: "propose_change_plan",
  description:
    "Propose a change to the Revit model for the user's approval. Required before any medium- or high-risk " +
    "change (bulk edits, type changes, deletions, code execution): those tools answer APPROVAL_REQUIRED and their " +
    "hint gives the exact {tool, args} step to put here. Each step is a Revit command name and its exact " +
    "arguments (same names and units as the tool: mm, m2, m3, degrees; omit document and dryRun). The plan is " +
    "shown to the user in a window inside Revit with the affected elements; nothing changes until they approve. " +
    "Then call wait_for_plan_decision, and execute_plan once approved. A plan is bound to one document and " +
    "expires 30 minutes after creation or approval. Low-risk edits do not need a plan. When get_session_context " +
    "reports approval.mode = auto (the person chose not to be asked), medium/high tools run directly and plans are " +
    "approved at once.",
  kind: "read",
  risk: "none",
  toolset: "core",
  targetsDocument: true,
  inputSchema: {
    type: "object",
    properties: {
      steps: {
        type: "array",
        minItems: 1,
        maxItems: 200,
        description: "Ordered steps. Several steps run as one undoable batch when the add-in supports it.",
        items: {
          type: "object",
          properties: {
            tool: { type: "string", description: "Revit command name, e.g. delete_element or set_parameters." },
            args: { type: "object", description: "Exact arguments of that command.", additionalProperties: true },
          },
          required: ["tool"],
          additionalProperties: false,
        },
      },
      summary: {
        type: "string",
        minLength: 1,
        maxLength: 4000,
        description: "Plain-language summary for the person approving: what changes, where, and why.",
      },
      reasoning: { type: "string", maxLength: 8000, description: "Optional: assumptions, what was excluded, risks." },
    },
    required: ["steps", "summary"],
    additionalProperties: false,
  },
  async run(args) {
    const docId = await resolveTargetDocId();
    const plan = await broker.planCreate(
      docId,
      args.steps.map((s) => ({ tool: s.tool, args: s.args ?? {} })),
      args.summary,
      args.reasoning
    );
    return {
      ...plan,
      next:
        plan.status === "approved"
          ? `Auto-approved (REVIT_MCP_APPROVAL=auto). Call execute_plan with planId "${plan.planId}".`
          : "Tell the user a plan is waiting for their approval in the Revit window, then call " +
            `wait_for_plan_decision with planId "${plan.planId}".`,
    };
  },
});

export const waitForPlanDecision = defineServerTool<{ planId: string; waitSeconds?: number }>({
  name: "wait_for_plan_decision",
  description:
    "Wait (up to 50 s per call) for the user to approve or reject a plan in Revit, then return the plan with its " +
    "status (pending, approved, rejected, expired, executed...). Call again while it is still pending. Use this " +
    "instead of asking the user to tell you they approved.",
  kind: "read",
  risk: "none",
  toolset: "core",
  inputSchema: {
    type: "object",
    properties: { planId: PLAN_ID, waitSeconds: WAIT_SECONDS(50) },
    required: ["planId"],
    additionalProperties: false,
  },
  async run(args) {
    const plan = await broker.planWait(args.planId, args.waitSeconds ?? 50);
    // While nothing changed, don't resend the full steps (they can hold kilobytes of code) on every poll.
    if (plan?.status === "pending") {
      return {
        planId: plan.planId,
        status: plan.status,
        risk: plan.risk,
        expiresAt: plan.expiresAt,
        steps: Array.isArray(plan.steps)
          ? plan.steps.map((s: any) => ({ index: s.index, tool: s.tool, risk: s.risk }))
          : undefined,
        next: nextStepFor(plan),
      };
    }
    return { ...plan, next: nextStepFor(plan) };
  },
});

export const getPlan = defineServerTool<{ planId: string }>({
  name: "get_plan",
  description:
    "Return a change plan: steps, risk, status, the approver's decision and, once executed, the path of its " +
    "proof file (approver, time, changes, result). Does not wait; see wait_for_plan_decision.",
  kind: "read",
  risk: "none",
  toolset: "core",
  idempotent: true,
  inputSchema: {
    type: "object",
    properties: { planId: PLAN_ID },
    required: ["planId"],
    additionalProperties: false,
  },
  async run(args) {
    const plan = await broker.planGet(args.planId);
    return { ...plan, next: nextStepFor(plan) };
  },
});

export const executePlan = defineServerTool<{ planId: string }>({
  name: "execute_plan",
  description:
    "Execute an approved change plan in Revit. Each step runs at most once; several steps run as one batch " +
    "(a single Ctrl+Z undoes them) when the add-in supports it, otherwise one by one stopping at the first error. " +
    "Fails with PLAN_NOT_APPROVED / PLAN_REJECTED / PLAN_EXPIRED / PLAN_ALREADY_EXECUTED as appropriate. If it " +
    "takes long the result is {status:'running', jobId}: poll get_job_result. dryRun is not supported here: " +
    "preview each step with its own tool's dryRun before proposing the plan.",
  kind: "write",
  risk: "medium",
  toolset: "core",
  inputSchema: {
    type: "object",
    properties: { planId: PLAN_ID },
    required: ["planId"],
    additionalProperties: false,
  },
  async run(args, ctx) {
    if (ctx.dryRun) {
      throw new ToolFailure(
        "DRY_RUN_UNSUPPORTED",
        "execute_plan always runs the approved plan for real.",
        "Preview a step by calling its own tool with dryRun=true; then run the plan with dryRun=false."
      );
    }
    const result = await broker.planExecute(args.planId);
    if (isRunningResult(result)) return withJobGuidance(result);
    const envelope = normalizeEnvelope(result);
    return asEnvelope({ ...envelope, data: { planId: args.planId, result: envelope.data } });
  },
});

export const getJobResult = defineServerTool<{ jobId: string; waitSeconds?: number }>({
  name: "get_job_result",
  description:
    "Collect the result of a Revit command that answered {status:'running', jobId} because it took longer than " +
    "its timeout. Waits up to waitSeconds (max 50) for it to finish; while it is still running, returns " +
    "status 'running' again — call again. Results are kept for 30 minutes.",
  kind: "read",
  risk: "none",
  toolset: "core",
  inputSchema: {
    type: "object",
    properties: {
      jobId: { type: "string", minLength: 1, description: "The jobId from the 'running' answer." },
      waitSeconds: WAIT_SECONDS(30),
    },
    required: ["jobId"],
    additionalProperties: false,
  },
  async run(args) {
    const job = await broker.getJob(args.jobId, args.waitSeconds ?? 30);
    return asEnvelope(jobEnvelope(job));
  },
});

/** Job view -> envelope: running stays ok with guidance; finished jobs carry the command's own envelope. */
export function jobEnvelope(job: JobView) {
  const meta = { jobId: job.jobId, status: job.status, command: job.command, elapsedMs: job.elapsedMs };
  if (job.status === "running") {
    return okEnvelope({
      ...meta,
      message: `Still running in Revit. Call get_job_result again with jobId "${job.jobId}".`,
    });
  }
  if (job.status === "failed" && job.error) {
    return {
      ...errorEnvelope(job.error.code, job.error.message, job.error.hint, job.error.recommendedTool),
      data: meta,
    };
  }
  const inner = normalizeEnvelope(job.result);
  return { ...inner, data: { ...meta, result: inner.data } };
}

export const approvalTools = [proposeChangePlan, waitForPlanDecision, getPlan, executePlan, getJobResult];

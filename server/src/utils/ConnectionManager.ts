import { AsyncLocalStorage } from "async_hooks";
import { broker, isRunningResult, type BrokerError } from "./BrokerConnection.js";
import { resolveTargetDocId } from "./targeting.js";

/**
 * Marks a call made by a legacy (hand-written) tool. Those tools only print
 * `error.message` and JSON.stringify their result, so for them the guidance
 * the broker sends separately (hint, recommended tool, job polling) is folded
 * into what they do print. Framework tools render those fields themselves.
 */
export const legacyCallScope = new AsyncLocalStorage<boolean>();

/** A {status:"running", jobId} result, made self-explanatory for the AI. */
export function withJobGuidance(result: any): any {
  if (!isRunningResult(result)) return result;
  return {
    ...result,
    message:
      result.message ??
      `The command is still running in Revit. Call get_job_result with jobId "${result.jobId}" to collect the result.`,
    nextStep: { tool: "get_job_result", args: { jobId: result.jobId } },
  };
}

/** Fold hint/recommendedTool into the message of a broker error (legacy tools print only the message). */
export function withErrorGuidance(err: unknown): unknown {
  if (!(err instanceof Error)) return err;
  const e = err as BrokerError;
  if (!e.hint && !e.recommendedTool) return err;
  let message = e.code ? `${e.code}: ${e.message}` : e.message;
  if (e.hint) message += ` Hint: ${e.hint}`;
  if (e.recommendedTool) message += ` Recommended tool: ${e.recommendedTool}.`;
  const out = new Error(message) as BrokerError;
  out.code = e.code;
  out.hint = e.hint;
  out.recommendedTool = e.recommendedTool;
  out.details = e.details;
  return out;
}

/**
 * The object handed to each tool's operation callback. It keeps the exact
 * `sendCommand(command, params)` shape the 23 existing tools already call, so
 * none of them need to change. Under the hood every command is now tagged with
 * a resolved docId and routed through the broker instead of a raw TCP socket.
 */
export interface RevitClient {
  sendCommand(command: string, params?: any): Promise<any>;
}

/**
 * Execute an operation against Revit. Retained name + signature for backward
 * compatibility: `withRevitConnection(async (client) => client.sendCommand(...))`.
 *
 * The target document is resolved once per command from (in order) the explicit
 * `document` argument, the fixed target, or the single open document — see
 * targeting.ts. With exactly one project open this is invisible, preserving the
 * original single-document behaviour.
 */
export async function withRevitConnection<T>(
  operation: (client: RevitClient) => Promise<T>
): Promise<T> {
  const client: RevitClient = {
    async sendCommand(command: string, params: any = {}) {
      const docId = await resolveTargetDocId();
      if (!legacyCallScope.getStore()) {
        return withJobGuidance(await broker.sendCommand(docId, command, params));
      }
      try {
        return withJobGuidance(await broker.sendCommand(docId, command, params));
      } catch (err) {
        throw withErrorGuidance(err);
      }
    },
  };

  await broker.ensureConnected();
  return operation(client);
}

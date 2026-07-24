import { broker } from "./BrokerConnection.js";
import { resolveTargetDocId } from "./targeting.js";

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
      return broker.sendCommand(docId, command, params);
    },
  };

  await broker.ensureConnected();
  return operation(client);
}

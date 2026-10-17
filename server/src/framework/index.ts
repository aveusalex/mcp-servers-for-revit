import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { GuardOptions } from "./envelope.js";
import { loadManifest, type ToolManifest } from "./manifest.js";
import { policyFromEnv, type ExposurePolicy } from "./policy.js";
import {
  installFaithfulToolListing,
  registerManifestTools,
  registerServerTool,
  type RevitSender,
  type ServerToolDefinition,
} from "./registry.js";
import { serverTools as defaultServerTools } from "./serverTools.js";

export * from "./envelope.js";
export * from "./manifest.js";
export * from "./policy.js";
export * from "./registry.js";

let activeManifest: ToolManifest | undefined;

/** The manifest this server registered from (for get_session_context's ADDIN_OUTDATED check). */
export function getActiveManifest(): ToolManifest | undefined {
  return activeManifest;
}

export interface FrameworkOptions {
  manifest?: ToolManifest;
  policy?: ExposurePolicy;
  send?: RevitSender;
  serverTools?: ServerToolDefinition<any>[];
  guard?: GuardOptions;
}

/**
 * Register manifest tools and server-only tools. Returns every name registered so the legacy registration can
 * skip a legacy tool that a framework tool now replaces.
 */
export function registerFrameworkTools(server: McpServer, options: FrameworkOptions = {}): Set<string> {
  installFaithfulToolListing(server);
  const policy = options.policy ?? policyFromEnv();
  const registered = new Set<string>();

  let manifest = options.manifest;
  if (!manifest) {
    try {
      manifest = loadManifest();
    } catch (err) {
      console.error(`Framework tools disabled: cannot read tools.manifest.json (${err instanceof Error ? err.message : err}).`);
    }
  }
  if (manifest) {
    activeManifest = manifest;
    for (const name of registerManifestTools(server, manifest, { policy, send: options.send, guard: options.guard })) {
      registered.add(name);
    }
  }

  for (const def of options.serverTools ?? defaultServerTools) {
    if (registerServerTool(server, def, { policy, send: options.send, guard: options.guard })) registered.add(def.name);
  }
  return registered;
}

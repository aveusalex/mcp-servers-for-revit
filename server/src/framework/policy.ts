import type { ToolKind } from "./manifest.js";

/**
 * Which tools this server exposes, from the environment:
 *   REVIT_MCP_TOOLSETS=core,parameters,...   only these toolsets ("all" or unset = everything)
 *   REVIT_MCP_READ_ONLY=1                    only kind=read tools
 *
 * Idea (rewritten, not copied) from bimwright's ToolsetFilter and KenLP's tool profiles.
 */
export interface ExposurePolicy {
  /** null = every toolset. */
  toolsets: Set<string> | null;
  readOnly: boolean;
}

/** Toolset name for the 23+ hand-written legacy tools in src/tools. */
export const LEGACY_TOOLSET = "legacy";

/** Session plumbing that stays available whatever the filter says (they never touch the model). */
export const ALWAYS_EXPOSED = new Set(["list_open_documents", "set_target_document"]);

export function policyFromEnv(env: NodeJS.ProcessEnv = process.env): ExposurePolicy {
  const rawSets = (env.REVIT_MCP_TOOLSETS ?? "").trim();
  const names = rawSets
    .split(/[,;\s]+/)
    .map((s) => s.trim().toLowerCase())
    .filter(Boolean);
  const toolsets = names.length === 0 || names.includes("all") ? null : new Set(names);
  const ro = (env.REVIT_MCP_READ_ONLY ?? "").trim().toLowerCase();
  return { toolsets, readOnly: ro === "1" || ro === "true" || ro === "yes" };
}

export function isExposed(
  tool: { name: string; toolset: string; kind: ToolKind },
  policy: ExposurePolicy
): boolean {
  if (ALWAYS_EXPOSED.has(tool.name)) return true;
  if (policy.readOnly && tool.kind !== "read") return false;
  if (policy.toolsets && !policy.toolsets.has(tool.toolset.toLowerCase())) return false;
  return true;
}

/**
 * Kind of each legacy tool, so REVIT_MCP_READ_ONLY can hide the ones that change the model (or local data).
 * Anything not listed here is treated as write (fail-closed).
 */
const LEGACY_READ_TOOLS = new Set([
  "ai_element_filter",
  "analyze_model_statistics",
  "export_room_data",
  "get_available_family_types",
  "get_current_view_elements",
  "get_current_view_info",
  "get_material_quantities",
  "get_selected_elements",
  "list_open_documents",
  "list_revit_links",
  "query_stored_data",
  "say_hello",
  "set_target_document",
]);

export function legacyKind(name: string): ToolKind {
  return LEGACY_READ_TOOLS.has(name) ? "read" : "write";
}

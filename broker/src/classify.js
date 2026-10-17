import { DEFAULT_TOOL_TIMEOUT_S } from "./config.js";

/**
 * Tool classification: what a command does to the model (`kind`) and how bad
 * it is if it goes wrong (`risk`). The broker gates on this, so it must never
 * be guessable from the command name alone.
 *
 * Sources, in order:
 *   1. the manifest the owning plugin session sent in `register.tools`
 *      (framework tools, Contract D), then
 *   2. the static table below for legacy IRevitCommand commands, then
 *   3. fail-closed: anything unknown is write/medium (needs approval).
 *
 * Keep LEGACY_TOOLS in sync with command.json (plugin side mirrors it in
 * plugin/Core/ToolClassification.cs).
 */

export const RISK_ORDER = ["none", "low", "medium", "high"];

/** @param {string} risk */
export function riskRank(risk) {
  const i = RISK_ORDER.indexOf(String(risk ?? "").toLowerCase());
  return i < 0 ? RISK_ORDER.indexOf("medium") : i;
}

/** @param {string[]} risks */
export function maxRisk(risks) {
  let best = 0;
  for (const r of risks) best = Math.max(best, riskRank(r));
  return RISK_ORDER[best];
}

/** True when this risk needs a human-approved plan in ask_first mode. */
export function needsApproval(risk) {
  return riskRank(risk) >= riskRank("medium");
}

const read = (extra = {}) => ({ kind: "read", risk: "none", ...extra });
const write = (risk, extra = {}) => ({ kind: "write", risk, ...extra });

/**
 * Legacy commands (command.json / server/src/tools). Legacy commands are not
 * framework tools, so they cannot be steps of execute_batch (batchable=false).
 * `riskFor(params)` refines risk for commands whose danger depends on the args.
 */
export const LEGACY_TOOLS = Object.freeze({
  say_hello: read(),
  list_revit_links: read(),
  get_available_family_types: read(),
  get_current_view_elements: read(),
  get_current_view_info: read(),
  get_selected_elements: read(),
  ai_element_filter: read(),
  export_room_data: read(),
  get_material_quantities: read(),
  analyze_model_statistics: read(),

  // Additive edits: one element (or a small explicit set) at a time.
  create_point_based_element: write("low"),
  create_line_based_element: write("low"),
  create_surface_based_element: write("low"),
  create_grid: write("low"),
  create_level: write("low"),
  create_room: write("low"),
  create_structural_framing_system: write("low"),
  create_dimensions: write("low"),
  // View graphic overrides only; trivially reversible.
  color_splash: write("low"),

  // Bulk: tags every wall/room in the view.
  tag_walls: write("medium"),
  tag_rooms: write("medium"),
  // Deletion cascades to dependent elements.
  delete_element: write("medium"),
  // Selection/visibility/colour; its "Delete" action deletes elements.
  operate_element: write("low", {
    riskFor: (params) => {
      const action = String(params?.data?.action ?? params?.action ?? "").toLowerCase();
      return action === "delete" ? "medium" : "low";
    },
  }),
  // Arbitrary C# in the Revit process: always ask.
  send_code_to_revit: write("high"),
});

/**
 * Normalise whatever the plugin sent in `register.tools` into a Map of
 * name -> entry. Accepts the full manifest object `{version, tools:[...]}`, a
 * bare array of tools, or either of those as a JSON string (what
 * ToolRegistry.GetManifestJson() returns). Malformed input yields an empty map.
 *
 * @param {any} raw
 * @returns {Map<string, object>}
 */
export function parseManifest(raw) {
  const out = new Map();
  let value = raw;
  if (typeof value === "string") {
    try {
      value = JSON.parse(value);
    } catch {
      return out;
    }
  }
  const list = Array.isArray(value) ? value : Array.isArray(value?.tools) ? value.tools : [];
  for (const t of list) {
    if (!t || typeof t.name !== "string" || !t.name) continue;
    const kind = String(t.kind ?? "write").toLowerCase();
    out.set(t.name, {
      kind: ["read", "write", "ui"].includes(kind) ? kind : "write",
      risk: RISK_ORDER.includes(String(t.risk ?? "").toLowerCase()) ? String(t.risk).toLowerCase() : "medium",
      batchable: t.batchable !== false,
      idempotent: t.idempotent === true,
      timeoutSeconds:
        Number.isFinite(t.timeoutSeconds) && t.timeoutSeconds > 0 ? t.timeoutSeconds : DEFAULT_TOOL_TIMEOUT_S,
      toolset: t.toolset ?? "core",
    });
  }
  return out;
}

/**
 * Classify one command.
 *
 * @param {string} command
 * @param {any} params
 * @param {Map<string, object>} [manifest] - the owning session's tools
 * @returns {{kind:string, risk:string, batchable:boolean, timeoutSeconds:number, source:string}}
 */
export function classify(command, params, manifest) {
  if (command === "execute_batch") return classifyBatch(params, manifest);

  const m = manifest?.get(command);
  if (m) {
    return {
      kind: m.kind,
      risk: m.risk,
      batchable: m.batchable,
      timeoutSeconds: m.timeoutSeconds,
      source: "manifest",
    };
  }

  const legacy = LEGACY_TOOLS[command];
  if (legacy) {
    let risk = legacy.risk;
    if (legacy.riskFor) {
      try {
        risk = legacy.riskFor(params) ?? risk;
      } catch {
        /* keep table risk */
      }
    }
    return {
      kind: legacy.kind,
      risk,
      batchable: false,
      timeoutSeconds: DEFAULT_TOOL_TIMEOUT_S,
      source: "legacy",
    };
  }

  // Fail-closed: an unknown command is treated as a write that needs approval.
  return {
    kind: "write",
    risk: "medium",
    batchable: false,
    timeoutSeconds: DEFAULT_TOOL_TIMEOUT_S,
    source: "unknown",
  };
}

/**
 * A batch is as risky as its riskiest step, a write if any step writes, and
 * may take as long as all of its steps together (capped by the hard timeout).
 */
export function classifyBatch(params, manifest) {
  const steps = Array.isArray(params?.steps) ? params.steps : [];
  const parts = steps.map((s) =>
    s && s.tool !== "execute_batch" ? classify(s.tool, s.args ?? {}, manifest) : null
  );
  const risks = parts.map((p) => (p ? p.risk : "high"));
  const anyWrite = parts.some((p) => !p || p.kind !== "read");
  const anyUi = parts.some((p) => p && p.kind === "ui");
  const timeout = parts.reduce((acc, p) => acc + (p ? p.timeoutSeconds : DEFAULT_TOOL_TIMEOUT_S), 0);
  return {
    kind: anyWrite ? "write" : anyUi ? "ui" : "read",
    risk: steps.length ? maxRisk(risks) : "medium",
    batchable: false,
    timeoutSeconds: Math.max(DEFAULT_TOOL_TIMEOUT_S, timeout),
    source: "batch",
  };
}

/**
 * Collect numeric element ids mentioned in a step's args (keys like
 * elementId, elementIds, ids, hostId...), so the plugin can highlight them.
 * Bounded to keep plan_pending small.
 *
 * @param {any} args
 * @param {number} [limit]
 * @returns {number[]}
 */
export function extractElementIds(args, limit = 500) {
  const found = new Set();
  const visit = (value, key) => {
    if (found.size >= limit || value == null) return;
    const idKey = typeof key === "string" && /(^ids?$|elementids?$|elementid$|^hostid$|ids$)/i.test(key);
    if (typeof value === "number" && idKey) {
      if (Number.isInteger(value) && value > 0) found.add(value);
      return;
    }
    // Framework tools take ids as strings ("220128"); UniqueIds and "link:element" refs are not plain ids.
    if (typeof value === "string" && idKey) {
      if (/^\d{1,18}$/.test(value)) {
        const n = Number(value);
        if (Number.isSafeInteger(n) && n > 0) found.add(n);
      }
      return;
    }
    if (Array.isArray(value)) {
      for (const v of value) visit(v, idKey ? key : undefined);
      return;
    }
    if (typeof value === "object") {
      for (const [k, v] of Object.entries(value)) visit(v, k);
    }
  };
  visit(args, undefined);
  return [...found];
}

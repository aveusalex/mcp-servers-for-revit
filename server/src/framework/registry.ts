import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { ListToolsRequestSchema } from "@modelcontextprotocol/sdk/types.js";
import { z } from "zod";
import { Ajv, type ErrorObject, type ValidateFunction } from "ajv";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { runWithExplicitDocument } from "../utils/targeting.js";
import {
  envelopeFromError,
  errorEnvelope,
  normalizeEnvelope,
  okEnvelope,
  toToolResult,
  type GuardOptions,
  type ToolEnvelope,
  type ToolWarning,
} from "./envelope.js";
import {
  computeCapabilityHash,
  riskAtLeast,
  type JsonSchema,
  type ManifestTool,
  type ToolKind,
  type ToolManifest,
  type ToolRisk,
} from "./manifest.js";
import { isExposed, type ExposurePolicy } from "./policy.js";

// ---- injected parameters ------------------------------------------------------------------------------------

export const DOCUMENT_PARAM: JsonSchema = {
  type: "string",
  description:
    "Optional target document: a docId (from list_open_documents) or an exact title. " +
    "Omit when only one project is open, or when a default has been fixed with set_target_document.",
};

export const DRY_RUN_PARAM: JsonSchema = {
  type: "boolean",
  default: false,
  description:
    "Simulate the change: Revit runs it in a transaction that is rolled back, and the result lists the " +
    "elements that would be created, modified or deleted. Nothing is kept in the model.",
};

/**
 * The schema the client sees: the tool's own schema plus `document` (every tool that reaches Revit) and
 * `dryRun` (write tools only). Tools must not declare these themselves.
 */
export function withFrameworkParams(
  schema: JsonSchema | undefined,
  kind: ToolKind,
  opts: { document?: boolean; name?: string; dryRunDefault?: boolean } = {}
): JsonSchema {
  const base: JsonSchema = schema && typeof schema === "object" ? structuredClone(schema) : {};
  if (base.type !== undefined && base.type !== "object") {
    throw new Error(`${opts.name ?? "tool"}: inputSchema must be an object schema.`);
  }
  base.type = "object";
  base.properties = { ...(base.properties ?? {}) };
  for (const reserved of ["document", "dryRun"]) {
    if (reserved in base.properties) {
      throw new Error(`${opts.name ?? "tool"}: '${reserved}' is injected by the framework; do not declare it.`);
    }
  }
  if (opts.document !== false) base.properties.document = { ...DOCUMENT_PARAM };
  if (kind === "write") {
    base.properties.dryRun = opts.dryRunDefault
      ? {
          ...DRY_RUN_PARAM,
          default: true,
          description:
            DRY_RUN_PARAM.description +
            " Defaults to true for this tool: pass dryRun:false to apply (after reviewing the preview).",
        }
      : { ...DRY_RUN_PARAM };
  }
  return base;
}

/** MCP tool annotations from the manifest flags. */
export function annotationsFor(kind: ToolKind, risk: ToolRisk, idempotent: boolean) {
  return {
    readOnlyHint: kind === "read",
    destructiveHint: riskAtLeast(risk, "medium"),
    idempotentHint: idempotent,
    openWorldHint: false,
  };
}

// ---- validation ---------------------------------------------------------------------------------------------

const ajv = new Ajv({ strict: false, allErrors: true, useDefaults: true });

function describeAjvError(e: ErrorObject): string {
  const where = e.instancePath ? e.instancePath.replace(/^\//, "").replace(/\//g, ".") : "arguments";
  switch (e.keyword) {
    case "additionalProperties":
      return `${where}: unknown property '${(e.params as any).additionalProperty}'`;
    case "required":
      return `${where}: missing required property '${(e.params as any).missingProperty}'`;
    case "enum":
      return `${where}: must be one of ${JSON.stringify((e.params as any).allowedValues)}`;
    default:
      return `${where}: ${e.message}`;
  }
}

export function validationEnvelope(validate: ValidateFunction): ToolEnvelope {
  const details = (validate.errors ?? []).map(describeAjvError);
  return errorEnvelope(
    "INVALID_ARGUMENTS",
    `Invalid arguments: ${details.join("; ")}.`,
    "Check the tool's input schema (property names are case-sensitive; units are mm, m2, m3 and degrees)."
  );
}

// ---- faithful tools/list ------------------------------------------------------------------------------------

const faithfulSchemas = new WeakMap<McpServer, Map<string, JsonSchema>>();

function schemaMap(server: McpServer): Map<string, JsonSchema> {
  let map = faithfulSchemas.get(server);
  if (!map) {
    map = new Map();
    faithfulSchemas.set(server, map);
  }
  return map;
}

function rewriteList(server: McpServer, result: any): any {
  const map = schemaMap(server);
  if (!result || !Array.isArray(result.tools) || map.size === 0) return result;
  return {
    ...result,
    tools: result.tools.map((t: any) => (map.has(t.name) ? { ...t, inputSchema: map.get(t.name) } : t)),
  };
}

/**
 * McpServer only advertises schemas it derives from zod. Framework tools carry hand-off JSON Schemas from the
 * manifest (descriptions, units, enums, defaults), so we wrap the tools/list handler McpServer installs and swap
 * in the original JSON Schema for those tools; legacy zod tools are listed exactly as before. Call this before
 * the first tool is registered (registerTools does).
 */
export function installFaithfulToolListing(server: McpServer): void {
  const inner = server.server as any;
  if (inner.__revitFaithfulListing) return;
  inner.__revitFaithfulListing = true;

  const original = inner.setRequestHandler.bind(inner);
  inner.setRequestHandler = (schema: unknown, handler: (req: any, extra: any) => any) => {
    if (schema === ListToolsRequestSchema) {
      return original(schema, async (req: any, extra: any) => rewriteList(server, await handler(req, extra)));
    }
    return original(schema, handler);
  };

  // Late install: McpServer already set its handler; wrap the stored one.
  const handlers: Map<string, any> | undefined = inner._requestHandlers;
  const existing = handlers?.get("tools/list");
  if (existing) {
    handlers!.set("tools/list", async (req: any, extra: any) => rewriteList(server, await existing(req, extra)));
  }
}

// ---- registration -------------------------------------------------------------------------------------------

/** Sends a command to Revit for the given document selector. Injected so tests can run without a broker. */
export type RevitSender = (command: string, params: Record<string, unknown>, document?: string) => Promise<unknown>;

export const brokerSender: RevitSender = (command, params, document) =>
  runWithExplicitDocument(document, () => withRevitConnection((client) => client.sendCommand(command, params)));

interface FrameworkToolSpec {
  name: string;
  description: string;
  kind: ToolKind;
  risk: ToolRisk;
  toolset: string;
  idempotent: boolean;
  /** Final schema (framework params already injected). */
  inputSchema: JsonSchema;
  meta?: Record<string, unknown>;
}

const enumKey = (s: string): string => s.toLowerCase().replace(/[\s_\-]/g, "");

/**
 * Tool enums mix styles ("floor_plan", "finishFaceInterior", "all_circuits"). Map a string that matches an enum
 * value except for case, spaces, '_' or '-' ("floorPlan", "Floor Plan", "finish_face_interior") to that value,
 * in place, so the model's near-misses don't cost a round trip. Exact values are left alone; anything that
 * still doesn't match is left for Ajv to reject with the list of valid values.
 */
export function normalizeEnums(value: any, schema: JsonSchema | undefined): any {
  if (!schema || typeof schema !== "object" || value == null) return value;
  // Element ids are strings in some tools (they also accept UniqueIds) and integers in others: accept either
  // form where the other is expected, as long as it is a plain whole number.
  if (schema.type === "string" && typeof value === "number" && Number.isSafeInteger(value)) return String(value);
  if (schema.type === "integer" && typeof value === "string" && /^-?\d{1,15}$/.test(value.trim())) {
    return Number(value.trim());
  }
  if (typeof value === "string" && Array.isArray(schema.enum)) {
    if (schema.enum.includes(value)) return value;
    const hit = schema.enum.filter((e: unknown) => typeof e === "string" && enumKey(e) === enumKey(value));
    return hit.length === 1 ? hit[0] : value;
  }
  if (Array.isArray(value) && schema.items && typeof schema.items === "object") {
    for (let i = 0; i < value.length; i++) value[i] = normalizeEnums(value[i], schema.items as JsonSchema);
    return value;
  }
  if (typeof value === "object" && schema.properties && typeof schema.properties === "object") {
    for (const [k, sub] of Object.entries(schema.properties as Record<string, JsonSchema>)) {
      if (k in value) value[k] = normalizeEnums(value[k], sub);
    }
  }
  return value;
}

/** Permissive zod schema so McpServer hands us the raw arguments; real validation is Ajv on the JSON Schema. */
const PASSTHROUGH_ARGS = z.object({}).passthrough().optional();

function registerFrameworkTool(
  server: McpServer,
  spec: FrameworkToolSpec,
  execute: (args: Record<string, any>) => Promise<ToolEnvelope>,
  guard: GuardOptions
): void {
  installFaithfulToolListing(server);
  const validate = ajv.compile(spec.inputSchema);

  (server as any).registerTool(
    spec.name,
    {
      description: spec.description,
      inputSchema: PASSTHROUGH_ARGS,
      annotations: annotationsFor(spec.kind, spec.risk, spec.idempotent),
      _meta: { "revit-mcp": { kind: spec.kind, risk: spec.risk, toolset: spec.toolset, ...(spec.meta ?? {}) } },
    },
    async (rawArgs: unknown) => {
      const args: Record<string, any> =
        rawArgs && typeof rawArgs === "object" ? structuredClone(rawArgs as Record<string, any>) : {};
      normalizeEnums(args, spec.inputSchema);
      if (!validate(args)) return toToolResult(validationEnvelope(validate), spec.name, guard);
      let envelope: ToolEnvelope;
      try {
        envelope = await execute(args);
      } catch (err) {
        envelope = envelopeFromError(err);
      }
      return toToolResult(envelope, spec.name, guard);
    }
  );
  schemaMap(server).set(spec.name, spec.inputSchema);
}

export interface RegisterOptions {
  policy: ExposurePolicy;
  send?: RevitSender;
  guard?: GuardOptions;
}

/**
 * Register every manifest tool allowed by the policy. The Revit command name is the tool name; `document`
 * routes the call and is not forwarded; `dryRun` is forwarded (write tools only) for the dispatcher.
 * Returns the names registered.
 */
export function registerManifestTools(server: McpServer, manifest: ToolManifest, opts: RegisterOptions): string[] {
  const send = opts.send ?? brokerSender;
  const registered: string[] = [];

  const expected = computeCapabilityHash(manifest.tools);
  if (manifest.capabilityHash !== expected) {
    console.error(
      `tools.manifest.json: capabilityHash does not match its content (edited by hand?). ` +
        `Regenerate it with \`npm run manifest\`.`
    );
  }

  for (const tool of manifest.tools) {
    if (!isExposed(tool, opts.policy)) continue;
    const inputSchema = withFrameworkParams(tool.inputSchema, tool.kind, {
      name: tool.name,
      dryRunDefault: tool.dryRunDefault === true,
    });
    registerFrameworkTool(
      server,
      { ...tool, inputSchema, meta: { batchable: tool.batchable, timeoutSeconds: tool.timeoutSeconds } },
      async (args) => {
        const { document, dryRun, ...rest } = args;
        // Always forward an explicit boolean: the broker gates on it and the add-in applies the same default.
        const effectiveDryRun = dryRun === undefined ? tool.dryRunDefault === true : dryRun === true;
        const params: Record<string, unknown> = tool.kind === "write" ? { ...rest, dryRun: effectiveDryRun } : rest;
        return normalizeEnvelope(await send(tool.name, params, document));
      },
      opts.guard ?? {}
    );
    registered.push(tool.name);
  }
  return registered;
}

// ---- server-only tools --------------------------------------------------------------------------------------

const ENVELOPE = Symbol.for("revit-mcp.envelope");

/** Return this from a server tool's `run` to hand back a full envelope (e.g. a job result) instead of data. */
export function asEnvelope(envelope: ToolEnvelope): ToolEnvelope {
  return Object.defineProperty({ ...envelope }, ENVELOPE, { value: true, enumerable: false });
}

export interface ServerToolContext {
  /** The `document` argument, when the tool targets a document. */
  document?: string;
  /** The `dryRun` argument (write tools only). */
  dryRun: boolean;
  /** Add a warning to the envelope. */
  warn(message: string, elementIds?: number[]): void;
  /** Send a command to Revit (routed to `document`) and get its envelope. */
  send(command: string, params?: Record<string, unknown>): Promise<ToolEnvelope>;
}

export interface ServerToolDefinition<A = Record<string, any>> {
  name: string;
  /** What it does, when to use it, and how to undo it. */
  description: string;
  kind?: ToolKind;
  risk?: ToolRisk;
  toolset?: string;
  idempotent?: boolean;
  /** JSON Schema of the arguments (object). Do not declare `document`/`dryRun`. */
  inputSchema?: JsonSchema;
  /** Inject the `document` argument (tools that act on a Revit document). Default false. */
  targetsDocument?: boolean;
  /**
   * Return the data (wrapped as ok), or `asEnvelope(...)` for a full envelope. Throw `ToolFailure(code, message,
   * hint, recommendedTool)` for expected errors.
   */
  run(args: A, ctx: ServerToolContext): unknown | Promise<unknown>;
}

/** Declare a tool that lives only in the MCP server (plans, jobs, context, knowledge...), same envelope. */
export function defineServerTool<A = Record<string, any>>(def: ServerToolDefinition<A>): ServerToolDefinition<A> {
  if (!/^[a-z][a-z0-9_]*$/.test(def.name)) throw new Error(`Server tool name '${def.name}' must be snake_case.`);
  if (!def.description || def.description.trim().length < 20) {
    throw new Error(`Server tool '${def.name}' needs a real description.`);
  }
  return def;
}

export function registerServerTool(
  server: McpServer,
  def: ServerToolDefinition<any>,
  opts: RegisterOptions
): boolean {
  const kind = def.kind ?? "read";
  const risk = def.risk ?? "none";
  const toolset = def.toolset ?? "core";
  if (!isExposed({ name: def.name, toolset, kind }, opts.policy)) return false;
  const send = opts.send ?? brokerSender;
  const inputSchema = withFrameworkParams(def.inputSchema ?? { type: "object", properties: {} }, kind, {
    document: def.targetsDocument === true,
    name: def.name,
  });

  registerFrameworkTool(
    server,
    { name: def.name, description: def.description, kind, risk, toolset, idempotent: def.idempotent ?? false, inputSchema, meta: { serverOnly: true } },
    async (args) => {
      const { document, dryRun, ...rest } = args;
      const warnings: ToolWarning[] = [];
      const ctx: ServerToolContext = {
        document,
        dryRun: dryRun === true,
        warn: (message, elementIds) => warnings.push(elementIds ? { message, elementIds } : { message }),
        send: async (command, params = {}) => normalizeEnvelope(await send(command, params, document)),
      };
      const run = () => Promise.resolve(def.run(rest, ctx));
      const result = def.targetsDocument ? await runWithExplicitDocument(document, run) : await run();
      const envelope =
        result && typeof result === "object" && (result as any)[ENVELOPE]
          ? (result as ToolEnvelope)
          : okEnvelope(result, { dryRun: dryRun === true });
      if (warnings.length) envelope.warnings = [...(envelope.warnings ?? []), ...warnings];
      return envelope;
    },
    opts.guard ?? {}
  );
  return true;
}

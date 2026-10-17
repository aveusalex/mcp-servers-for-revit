import fs from "fs";
import os from "os";
import path from "path";
import { reportsFailure } from "../utils/toolResults.js";

/**
 * Result envelope every framework tool returns (FASE0_SPEC, Contract B/F):
 *   { ok, data, changes?, warnings[], dryRun, error?{code, message, hint?, recommendedTool?} }
 */
export interface ToolErrorInfo {
  code: string;
  message: string;
  hint?: string;
  recommendedTool?: string;
}

export interface ChangeSet {
  created: number[];
  modified: number[];
  deleted: number[];
  truncated?: boolean;
}

export interface ToolWarning {
  message: string;
  elementIds?: number[];
  severity?: string;
}

export interface ToolEnvelope {
  ok: boolean;
  data?: unknown;
  changes?: ChangeSet;
  warnings?: Array<ToolWarning | string>;
  dryRun?: boolean;
  error?: ToolErrorInfo;
}

/** MCP CallToolResult (structural; avoids coupling to one SDK version's types). */
export interface ToolCallResult {
  [key: string]: unknown;
  content: Array<
    | { type: "text"; text: string }
    | { type: "image"; data: string; mimeType: string }
  >;
  isError?: boolean;
}

export interface GuardOptions {
  /** Inline budget for the JSON text; above it the result is spilled to a file. Default 100 KB. */
  maxInlineBytes?: number;
  /** Arrays longer than this are cut (with a `_truncated` marker). Default 200. */
  maxArrayItems?: number;
  /** Where spilled results go. Default %LOCALAPPDATA%\revit-mcp\out (REVIT_MCP_OUT_DIR overrides). */
  outDir?: string;
  /** Characters of the result kept as `preview` when spilled. Default 2000. */
  previewChars?: number;
}

export const DEFAULT_MAX_INLINE_BYTES = 100 * 1024;
export const DEFAULT_MAX_ARRAY_ITEMS = 200;

/** Thrown by server-side tools (and usable anywhere) to fail with a stable code and a hint for the AI. */
export class ToolFailure extends Error {
  constructor(
    public code: string,
    message: string,
    public hint?: string,
    public recommendedTool?: string
  ) {
    super(message);
    this.name = "ToolFailure";
  }
}

export function okEnvelope(data: unknown, extra: Partial<ToolEnvelope> = {}): ToolEnvelope {
  return { ok: true, data, warnings: [], dryRun: false, ...extra };
}

export function errorEnvelope(
  code: string,
  message: string,
  hint?: string,
  recommendedTool?: string
): ToolEnvelope {
  const error: ToolErrorInfo = { code, message };
  if (hint) error.hint = hint;
  if (recommendedTool) error.recommendedTool = recommendedTool;
  return { ok: false, warnings: [], dryRun: false, error };
}

/** Any thrown value -> error envelope. Keeps `code`/`hint`/`recommendedTool` when the error carries them. */
export function envelopeFromError(err: unknown, fallbackCode = "INTERNAL_ERROR"): ToolEnvelope {
  if (err && typeof err === "object") {
    const e = err as any;
    const code = typeof e.code === "string" && e.code ? e.code : fallbackCode;
    const message = typeof e.message === "string" && e.message ? e.message : String(err);
    return errorEnvelope(code, message, e.hint, e.recommendedTool);
  }
  return errorEnvelope(fallbackCode, String(err));
}

function pick(obj: any, camel: string): any {
  if (camel in obj) return obj[camel];
  const pascal = camel[0].toUpperCase() + camel.slice(1);
  return obj[pascal];
}

/**
 * Turn whatever came back from Revit into an envelope. Framework tools already answer with one (camelCase or,
 * defensively, PascalCase keys); legacy commands answer with raw data, which is wrapped as-is.
 */
export function normalizeEnvelope(raw: unknown): ToolEnvelope {
  if (raw && typeof raw === "object" && !Array.isArray(raw) && typeof pick(raw, "ok") === "boolean") {
    const r = raw as any;
    const env: ToolEnvelope = {
      ok: pick(r, "ok"),
      data: pick(r, "data"),
      warnings: pick(r, "warnings") ?? [],
      dryRun: pick(r, "dryRun") ?? false,
    };
    const changes = pick(r, "changes");
    if (changes) {
      env.changes = {
        created: pick(changes, "created") ?? [],
        modified: pick(changes, "modified") ?? [],
        deleted: pick(changes, "deleted") ?? [],
      };
      const truncated = pick(changes, "truncated");
      if (truncated) env.changes.truncated = true;
    }
    const error = pick(r, "error");
    if (error) {
      env.error = {
        code: pick(error, "code") ?? "INTERNAL_ERROR",
        message: pick(error, "message") ?? "Unknown error",
      };
      const hint = pick(error, "hint");
      const rec = pick(error, "recommendedTool");
      if (hint) env.error.hint = hint;
      if (rec) env.error.recommendedTool = rec;
    }
    return env;
  }
  if (reportsFailure(raw)) {
    const r = raw as any;
    return { ...errorEnvelope("COMMAND_FAILED", String(r.Message ?? r.message ?? "Command failed")), data: raw };
  }
  return okEnvelope(raw);
}

/**
 * Cut every array longer than `max` to its first `max` items followed by one marker object
 * `{ _truncated: true, _total: <original length> }`. Returns whether anything was cut.
 */
export function truncateArrays(value: unknown, max = DEFAULT_MAX_ARRAY_ITEMS): { value: unknown; truncated: boolean } {
  let truncated = false;
  const walk = (v: unknown, depth: number): unknown => {
    if (depth > 64 || v === null || typeof v !== "object") return v;
    if (Array.isArray(v)) {
      const items = v.length > max ? v.slice(0, max) : v;
      const out = items.map((x) => walk(x, depth + 1));
      if (v.length > max) {
        truncated = true;
        out.push({ _truncated: true, _total: v.length });
      }
      return out;
    }
    const out: Record<string, unknown> = {};
    for (const [k, x] of Object.entries(v as object)) out[k] = walk(x, depth + 1);
    return out;
  };
  return { value: walk(value, 0), truncated };
}

export function defaultOutDir(): string {
  if (process.env.REVIT_MCP_OUT_DIR) return process.env.REVIT_MCP_OUT_DIR;
  const local = process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local");
  return path.join(local, "revit-mcp", "out");
}

let spillCounter = 0;

/** Write the full JSON to `<outDir>/<tool>-<timestamp>.json`; returns the path. */
export function spillToFile(tool: string, json: string, outDir = defaultOutDir()): string {
  fs.mkdirSync(outDir, { recursive: true });
  const safeTool = tool.replace(/[^A-Za-z0-9_-]/g, "_") || "tool";
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  let file = path.join(outDir, `${safeTool}-${stamp}.json`);
  if (fs.existsSync(file)) file = path.join(outDir, `${safeTool}-${stamp}-${++spillCounter}.json`);
  fs.writeFileSync(file, json, "utf8");
  return file;
}

function extractImage(data: unknown): { data: unknown; image?: { base64: string; mimeType: string } } {
  if (!data || typeof data !== "object" || Array.isArray(data)) return { data };
  const img = (data as any).image;
  if (!img || typeof img !== "object" || typeof img.base64 !== "string" || typeof img.mimeType !== "string") {
    return { data };
  }
  const rest = { ...(data as any) };
  const { base64, mimeType, ...meta } = img;
  rest.image = { ...meta, mimeType, bytes: Buffer.byteLength(base64, "base64"), sentAs: "image content block" };
  return { data: rest, image: { base64, mimeType } };
}

function errorText(error: ToolErrorInfo): string {
  let text = `Error ${error.code}: ${error.message}`;
  if (error.hint) text += `\nHint: ${error.hint}`;
  if (error.recommendedTool) text += `\nRecommended tool: ${error.recommendedTool}`;
  return text;
}

/**
 * Envelope -> MCP tool result (Contract F): compact JSON text, isError = !ok, readable error/hint lines,
 * images as image blocks, arrays over 200 items cut, and results over 100 KB spilled to a file with a preview.
 * Inspiration: bimwright ResponseSizeGuard/spill and LuDattilo responseCompactor (ideas only, rewritten).
 */
export function toToolResult(envelope: ToolEnvelope, tool: string, options: GuardOptions = {}): ToolCallResult {
  const maxInline = options.maxInlineBytes ?? DEFAULT_MAX_INLINE_BYTES;
  const maxItems = options.maxArrayItems ?? DEFAULT_MAX_ARRAY_ITEMS;
  const previewChars = options.previewChars ?? 2000;

  const { data, image } = extractImage(envelope.data);
  const payload: Record<string, unknown> = { ok: envelope.ok };
  if (data !== undefined) payload.data = data;
  if (envelope.changes) payload.changes = envelope.changes;
  payload.warnings = envelope.warnings ?? [];
  payload.dryRun = envelope.dryRun ?? false;
  if (envelope.error) payload.error = envelope.error;

  const fullJson = JSON.stringify(payload);
  const fullBytes = Buffer.byteLength(fullJson, "utf8");
  const cut = truncateArrays(payload.data, maxItems);

  if (fullBytes > maxInline || cut.truncated) {
    let spilledPath: string | undefined;
    let spillError: string | undefined;
    try {
      spilledPath = spillToFile(tool, fullJson, options.outDir ?? defaultOutDir());
    } catch (e) {
      spillError = e instanceof Error ? e.message : String(e);
    }

    if (fullBytes > maxInline) {
      const preview = JSON.stringify(cut.value ?? null).slice(0, previewChars);
      payload.data = spilledPath
        ? { spilled: true, path: spilledPath, bytes: fullBytes, preview }
        : { spilled: false, bytes: fullBytes, preview, spillError };
    } else {
      payload.data = cut.value;
      if (spilledPath) payload.fullResult = spilledPath;
    }
  }

  let text = JSON.stringify(payload);
  if (envelope.error) text = `${errorText(envelope.error)}\n${text}`;

  const content: ToolCallResult["content"] = [];
  if (image) content.push({ type: "image", data: image.base64, mimeType: image.mimeType });
  content.push({ type: "text", text });
  return { content, isError: !envelope.ok };
}

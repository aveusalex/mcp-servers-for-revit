import crypto from "crypto";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

/** JSON Schema as produced by tools/ManifestGen (draft-07 subset plus `x-unit`). */
export type JsonSchema = { [key: string]: any };

export type ToolKind = "read" | "write" | "ui";
export type ToolRisk = "none" | "low" | "medium" | "high";

/** One entry of tools.manifest.json (FASE0_SPEC, Contract D). */
export interface ManifestTool {
  name: string;
  description: string;
  kind: ToolKind;
  risk: ToolRisk;
  toolset: string;
  batchable: boolean;
  idempotent: boolean;
  timeoutSeconds: number;
  inputSchema: JsonSchema;
}

export interface ToolManifest {
  version: number;
  capabilityHash: string;
  tools: ManifestTool[];
}

const RISK_ORDER: ToolRisk[] = ["none", "low", "medium", "high"];

export function riskAtLeast(risk: ToolRisk, floor: ToolRisk): boolean {
  return RISK_ORDER.indexOf(risk) >= RISK_ORDER.indexOf(floor);
}

/** The manifest committed next to this module (copied to build/framework by the build). */
export function defaultManifestPath(): string {
  if (process.env.REVIT_MCP_MANIFEST) return path.resolve(process.env.REVIT_MCP_MANIFEST);
  const here = path.dirname(fileURLToPath(import.meta.url));
  return path.join(here, "tools.manifest.json");
}

export function loadManifest(file: string = defaultManifestPath()): ToolManifest {
  const raw = JSON.parse(fs.readFileSync(file, "utf8"));
  return parseManifest(raw, file);
}

export function parseManifest(raw: any, source = "manifest"): ToolManifest {
  if (!raw || typeof raw !== "object" || !Array.isArray(raw.tools)) {
    throw new Error(`${source}: not a tools manifest (missing "tools" array).`);
  }
  if (raw.version !== 1) {
    throw new Error(`${source}: unsupported manifest version ${raw.version}.`);
  }
  for (const t of raw.tools) {
    if (typeof t?.name !== "string" || typeof t.inputSchema !== "object") {
      throw new Error(`${source}: malformed tool entry ${JSON.stringify(t?.name)}.`);
    }
  }
  return raw as ToolManifest;
}

/**
 * Same algorithm as ToolManifestBuilder.ComputeCapabilityHash (C#): SHA-256 hex of the canonical JSON of the
 * tools sorted by name, object keys sorted ordinally, no whitespace. Lets the server notice a hand-edited
 * manifest and lets tests verify it without dotnet.
 */
export function computeCapabilityHash(tools: ManifestTool[]): string {
  const sorted = [...tools].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
  return crypto.createHash("sha256").update(canonicalJson(sorted), "utf8").digest("hex");
}

export function canonicalJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (value && typeof value === "object") {
    const keys = Object.keys(value as object).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
    return `{${keys.map((k) => `${JSON.stringify(k)}:${canonicalJson((value as any)[k])}`).join(",")}}`;
  }
  return JSON.stringify(value);
}

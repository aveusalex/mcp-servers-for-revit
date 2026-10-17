import crypto from "crypto";

/**
 * Canonical JSON: object keys sorted, no whitespace, numbers normalised
 * (5 and 5.0 are the same number in JS; -0 becomes 0; non-finite becomes null),
 * `undefined` members dropped. Two semantically identical argument objects
 * always produce the same string, so they hash and compare the same.
 *
 * @param {any} value
 * @returns {string}
 */
export function canonicalJson(value) {
  return JSON.stringify(normalize(value));
}

function normalize(value) {
  if (value === null || value === undefined) return null;
  if (typeof value === "number") {
    if (!Number.isFinite(value)) return null;
    return Object.is(value, -0) ? 0 : value;
  }
  if (typeof value !== "object") return value;
  if (Array.isArray(value)) return value.map((v) => (v === undefined ? null : normalize(v)));
  const out = {};
  for (const key of Object.keys(value).sort()) {
    if (value[key] === undefined) continue;
    out[key] = normalize(value[key]);
  }
  return out;
}

/**
 * The args of a step as a plan records and matches them: `dryRun: false` is
 * the framework default the MCP server injects on every write call, so it is
 * dropped to keep a plan step and the later real call identical.
 *
 * @param {any} args
 */
export function stepArgs(args) {
  if (!args || typeof args !== "object" || Array.isArray(args)) return args ?? {};
  if (!("dryRun" in args) || args.dryRun === true) return args;
  const { dryRun, ...rest } = args;
  return rest;
}

/** SHA-256 hex of the canonical JSON of `value`. */
export function canonicalHash(value) {
  return crypto.createHash("sha256").update(canonicalJson(value)).digest("hex");
}

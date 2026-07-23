import fs from "fs";
import path from "path";
import { auditDir } from "./config.js";

/**
 * Append-only audit log, one JSON object per line (JSONL), one file per day.
 * Every command that crosses the broker is recorded with enough context to
 * reconstruct what an AI did to which document. Best-effort: a logging failure
 * must never take down routing.
 */
export class AuditLog {
  constructor(dir = auditDir()) {
    this._dir = dir;
    try {
      fs.mkdirSync(dir, { recursive: true });
    } catch {
      /* ignore */
    }
  }

  _file() {
    const day = new Date().toISOString().slice(0, 10); // YYYY-MM-DD
    return path.join(this._dir, `${day}.jsonl`);
  }

  /**
   * @param {object} entry - already-summarized fields; do not pass raw
   *   geometry or large arrays here, only a short summary.
   */
  write(entry) {
    const line =
      JSON.stringify({ ts: new Date().toISOString(), ...entry }) + "\n";
    try {
      fs.appendFile(this._file(), line, () => {});
    } catch {
      /* never throw from the audit path */
    }
  }
}

/**
 * Reduce a params object to something safe and small to log: primitives kept,
 * arrays reduced to their length, nested objects to their key list.
 * @param {any} params
 */
export function summarizeParams(params) {
  if (params == null || typeof params !== "object") return params ?? null;
  const out = {};
  for (const [k, v] of Object.entries(params)) {
    if (Array.isArray(v)) out[k] = `[array:${v.length}]`;
    else if (v && typeof v === "object") out[k] = `{${Object.keys(v).join(",")}}`;
    else out[k] = v;
  }
  return out;
}

import os from "os";
import path from "path";
import fs from "fs";
import crypto from "crypto";

/**
 * Broker wire configuration.
 *
 * The broker binds ONLY to loopback. The port is fixed; a second Revit process
 * (e.g. Revit 2024 alongside Revit 2026) does not need a second port because
 * every plugin connects OUT to this single broker and is told apart by its
 * sessionId. Port separation would only matter if two brokers ran at once,
 * which the autostart logic explicitly prevents.
 */
export const HOST = "127.0.0.1";
// REVIT_MCP_BROKER_PORT exists for tests (so they never collide with a running broker). The plugin always
// dials 8090, so do not change it in production.
export const PORT = Number(process.env.REVIT_MCP_BROKER_PORT) || 8090;

/** Heartbeat cadence expected from plugins, in milliseconds. */
export const HEARTBEAT_INTERVAL_MS = 5000;

/**
 * A plugin session is dropped (together with all of its documents) if no
 * heartbeat is seen for this long. 3 missed beats.
 */
export const SESSION_TTL_MS = 15000;

/**
 * Hard ceiling for one command. Past the per-tool soft timeout the MCP client
 * is told the command is still running (a job), and the broker keeps waiting
 * for the plugin up to this limit. Revit operations can be very slow.
 */
export const COMMAND_TIMEOUT_MS = 15 * 60 * 1000;

/** Default soft timeout (seconds) after which a command becomes a job. */
export const DEFAULT_TOOL_TIMEOUT_S = 50;

/** Longest a get_job / plan_wait long-poll may block, in seconds. */
export const MAX_WAIT_S = 50;

/** Finished jobs are kept this long so get_job can still return them. */
export const JOB_RETENTION_MS = 30 * 60 * 1000;

/** Time a plan stays valid (pending, or approved but not executed). */
export const PLAN_TTL_MS = 30 * 60 * 1000;

/** Limits (Contract E). */
export const MAX_MESSAGE_BYTES = 2 * 1024 * 1024;
export const MAX_BATCH_STEPS = 200;
export const MAX_INFLIGHT_PER_MCP = 8;

/**
 * Human-approval mode, from REVIT_MCP_APPROVAL:
 *   ask_first (default) - risk >= medium needs an approved plan
 *   auto                - no gate (CI / headless); plans are auto-approved
 *   look_only           - every write is refused
 * Anything unrecognised falls back to ask_first (fail-closed).
 */
export function approvalMode(value = process.env.REVIT_MCP_APPROVAL) {
  const v = String(value ?? "").trim().toLowerCase();
  if (v === "auto" || v === "look_only") return v;
  return "ask_first";
}

/** File where the person's choice of approval mode is kept (written by the Revit add-in, never by a tool). */
export const APPROVAL_FILE = () => path.join(stateDir(), "approval-mode.json");

/**
 * The approval mode chosen in Revit, or null when none was chosen (or the file is unreadable: then the
 * environment / ask_first default applies, which is the fail-closed side). Re-read only when the file changes.
 */
let approvalCache = { mtimeMs: -1, value: null };
export function readApprovalSetting(file = APPROVAL_FILE()) {
  let stat;
  try {
    stat = fs.statSync(file);
  } catch {
    approvalCache = { mtimeMs: -1, value: null };
    return null;
  }
  if (stat.mtimeMs === approvalCache.mtimeMs) return approvalCache.value;
  let value = null;
  try {
    const raw = JSON.parse(fs.readFileSync(file, "utf8").replace(/^﻿/, ""));
    const mode = String(raw?.mode ?? "").trim().toLowerCase();
    if (mode === "ask_first" || mode === "auto" || mode === "look_only") {
      value = { mode, changedBy: raw.changedBy ?? null, changedAt: raw.changedAt ?? null };
    }
  } catch {
    value = null;
  }
  approvalCache = { mtimeMs: stat.mtimeMs, value };
  return value;
}

/** How command params are written to the audit log: hash (default) | full. */
export function auditParamsMode(value = process.env.REVIT_MCP_AUDIT_PARAMS) {
  return String(value ?? "").trim().toLowerCase() === "full" ? "full" : "hash";
}

/**
 * Root of all broker state on disk. Windows: %APPDATA%\revit-mcp.
 * Falls back to ~/.revit-mcp on non-Windows dev machines so the broker and its
 * tests run anywhere.
 */
export function stateDir() {
  const appData =
    process.env.APPDATA ||
    (process.platform === "darwin"
      ? path.join(os.homedir(), "Library", "Application Support")
      : path.join(os.homedir(), ".config"));
  return path.join(appData, "revit-mcp");
}

export function auditDir() {
  return path.join(stateDir(), "audit");
}

export function plansDir() {
  return path.join(stateDir(), "plans");
}

const TOKEN_FILE = () => path.join(stateDir(), "broker-token");

/**
 * Read the shared handshake token, generating and persisting one on first run.
 * Because the plugin now connects on its own (no manual click), the token is
 * not optional: it is the only thing standing between a random local process
 * and the Revit API.
 *
 * @returns {string}
 */
export function loadOrCreateToken() {
  const dir = stateDir();
  fs.mkdirSync(dir, { recursive: true });
  const file = TOKEN_FILE();
  try {
    const existing = fs.readFileSync(file, "utf8").trim();
    if (existing) return existing;
  } catch {
    /* first run */
  }
  const token = crypto.randomBytes(32).toString("hex");
  fs.writeFileSync(file, token, { mode: 0o600 });
  return token;
}

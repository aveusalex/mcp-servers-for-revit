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
export const PORT = 8090;

/** Heartbeat cadence expected from plugins, in milliseconds. */
export const HEARTBEAT_INTERVAL_MS = 5000;

/**
 * A plugin session is dropped (together with all of its documents) if no
 * heartbeat is seen for this long. 3 missed beats.
 */
export const SESSION_TTL_MS = 15000;

/** Default per-command timeout. Revit operations can be slow. */
export const COMMAND_TIMEOUT_MS = 120000;

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

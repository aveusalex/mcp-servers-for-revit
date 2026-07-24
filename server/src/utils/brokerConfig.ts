import os from "os";
import path from "path";
import fs from "fs";

/**
 * Wire + on-disk config that the MCP server shares with the broker daemon.
 * These MUST stay in lockstep with broker/src/config.js — same host, same
 * port, same token file — otherwise the handshake fails.
 */
export const BROKER_HOST = "127.0.0.1";
export const BROKER_PORT = 8090;

export function stateDir(): string {
  const appData =
    process.env.APPDATA ||
    (process.platform === "darwin"
      ? path.join(os.homedir(), "Library", "Application Support")
      : path.join(os.homedir(), ".config"));
  return path.join(appData, "revit-mcp");
}

/**
 * Read the shared handshake token, generating it if the broker has not run yet.
 * Kept identical to the broker's generator so whichever process starts first
 * establishes the token and the other reads it back.
 */
export function loadOrCreateToken(): string {
  const dir = stateDir();
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, "broker-token");
  try {
    const existing = fs.readFileSync(file, "utf8").trim();
    if (existing) return existing;
  } catch {
    /* first run */
  }
  const token = [...Array(32)]
    .map(() => Math.floor(Math.random() * 256).toString(16).padStart(2, "0"))
    .join("");
  fs.writeFileSync(file, token, { mode: 0o600 });
  return token;
}

#!/usr/bin/env node
import { Broker } from "./broker.js";
import { PORT, HOST } from "./config.js";

/**
 * Daemon entrypoint. Designed to be spawned detached by the MCP server, and to
 * be idempotent: if the loopback port is already held (by another broker that
 * started first), we exit 0 without complaint instead of crashing on
 * EADDRINUSE. That makes "spawn the broker if it isn't there" safe to call from
 * every MCP server process without coordination.
 */
async function main() {
  const broker = new Broker();

  const shutdown = async () => {
    await broker.stop();
    process.exit(0);
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);

  try {
    await broker.start();
    // eslint-disable-next-line no-console
    console.error(`revit-mcp-broker listening on ws://${HOST}:${PORT}`);
  } catch (err) {
    if (err && err.code === "EADDRINUSE") {
      // Someone else already owns the port — assume a healthy broker and bow out.
      console.error("revit-mcp-broker: port already in use, another broker is running; exiting 0");
      process.exit(0);
    }
    console.error("revit-mcp-broker failed to start:", err);
    process.exit(1);
  }
}

main();

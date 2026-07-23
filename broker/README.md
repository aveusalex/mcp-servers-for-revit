# revit-mcp-broker

The loopback WebSocket broker that routes MCP commands to one or more Revit
documents across one or more Revit processes.

## Why a broker

Revit is single-process / multi-document: three open projects are usually one
`Revit.exe`, one add-in, three `Document` objects. Routing is therefore keyed by
the **document**, not by a port. The plugin connects OUT to this broker, so the
broker is the only process that binds a port — no port scanning, no bind races,
no discovery file.

## Run

```bash
npm install
npm start        # listens on ws://127.0.0.1:8090
npm test         # node --test
```

The MCP server spawns this automatically (detached, idempotent: it exits 0 if a
broker already holds the port), so you rarely need to run it by hand.

## Protocol

All messages are JSON. The first message on any connection must be a `hello`
carrying the shared token from `%APPDATA%\revit-mcp\broker-token`.

### Plugin role

```jsonc
{ "type": "hello", "role": "plugin", "token": "...", "sessionId": "<pid>-<version>" }
{ "type": "register",  "sessionId": "...", "revitVersion": "2026", "documents": [ /* DocumentMeta[] */ ] }
{ "type": "heartbeat", "sessionId": "...", "documents": [ /* full set, every 5s */ ] }
{ "type": "response",  "correlationId": "...", "result": { }, "error": { } }
```

`DocumentMeta`: `{ docId, title, pathName, isActive, isWorkshared, revitVersion }`
where `docId` is `Document.ProjectInformation.UniqueId` (stable across "save as").

### MCP role

```jsonc
{ "type": "hello", "role": "mcp", "token": "..." }
{ "type": "list_documents", "correlationId": "..." }
{ "type": "command", "correlationId": "...", "docId": "...", "command": "create_level", "params": { } }
```

The broker replies with `{ type: "welcome" }`, `{ type: "documents", documents }`,
or `{ type: "response", correlationId, result | error }`.

## Guarantees

- Binds **only** to `127.0.0.1`.
- Token required for both roles; a bad token is rejected and the socket closed.
- A session with no heartbeat for 15s is swept, dropping all of its documents and
  failing any in-flight command with `SESSION_TIMEOUT`.
- `correlationId` routing supports N MCP clients with no shared "current
  connection" state; default command timeout is 120s.
- One JSONL audit line per command under `%APPDATA%\revit-mcp\audit\<date>.jsonl`.

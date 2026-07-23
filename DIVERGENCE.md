# Divergence from upstream

This fork changes the transport and document model of
[`mcp-servers-for-revit`](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit).
This note exists so future upstream changes can be merged with the deltas in
mind, and so a reviewer knows exactly what changed and what still needs
validation inside Revit.

## Summary of the change

| Area | Upstream | This fork |
|---|---|---|
| Transport | Plugin binds a TCP port (`8080`); MCP server dials in | Plugin dials OUT to a **broker** on `ws://127.0.0.1:8090`; broker is the only listener |
| Activation | Manual click on the ribbon per session | Automatic on `OnStartup`; ribbon is a kill switch |
| Documents | One active document | All open documents, routed by `ProjectInformation.UniqueId` |
| MCP clients | One at a time | N simultaneous, no shared "current connection" state |
| Auth | None | Shared token (`%APPDATA%\revit-mcp\broker-token`) required for both roles |
| Audit | None | Per-command JSONL under `%APPDATA%\revit-mcp\audit\` |

## New components

- **`broker/`** — Node daemon. Loopback WS server, token handshake, in-memory
  registry (`sessionId → connection`, `docId → meta`) with a 15s heartbeat TTL,
  `correlationId` routing with a 120s command timeout, idempotent autostart.
  Covered by `broker/test/broker.test.js` (6 tests).
- **`server/src/utils/BrokerConnection.ts`** — persistent authenticated link to
  the broker, with detached autostart.
- **`server/src/utils/targeting.ts`** — target resolution order (explicit
  `document` → fixed target → single open document → `AMBIGUOUS_TARGET`).
- **`server/src/tools/list_open_documents.ts`, `set_target_document.ts`** — new tools.
- **`plugin/Core/BrokerClient.cs`** — replaces `SocketService`; outbound
  `ClientWebSocket`, backoff/reconnect, register + heartbeat, command dispatch.
- **`plugin/Core/DocumentSessionManager.cs`** — document lifecycle tracking and
  `docId → Document` resolution.
- **`commandset/Utils/RevitDocumentContext.cs`** — resolves the target
  `Document` from the AppDomain slot the dispatcher sets.

## Backward compatibility

With exactly one project open, `resolveTargetDocId()` returns that document with
no `document` argument, so every existing tool works unchanged. Tool schemas gain
only an **optional** `document` argument.

## What is verified here vs. what needs Revit

The Node/TypeScript pieces are covered by automated tests that run in CI without
Revit:

- `broker/`: routing, auth rejection, `DOCUMENT_NOT_FOUND`, two-client isolation,
  heartbeat-TTL eviction, mid-command plugin death.
- `server/`: single-document retrocompat, explicit target, `AMBIGUOUS_TARGET`,
  fixed target — exercised end-to-end against a live broker with a fake plugin.

The C# plugin and command-set changes **cannot be compiled or run without Revit
and the `RevitMCPSDK` / `Nice3point.Revit.Api.*` packages**, so they are written
to the existing patterns but still need manual validation in Revit:

1. Auto-connect on startup with no click; three projects register within ~5s and
   deregister within ~15s of closing.
2. `doc-agnostic` commands run correctly against a non-active document.
3. `ui-bound` commands return `REQUIRES_ACTIVE_DOCUMENT` for a non-active target.
4. Kill switch drops every document of the session immediately; reconnect works.
5. Broker restarted with Revit open → plugin reconnects on its own (backoff).
6. `allowAutoActivate` (Opção B) is experimental: `OpenAndActivateDocument` must
   run on the UI thread; validate before enabling.

See Phase 4/5 acceptance criteria in the original spec.

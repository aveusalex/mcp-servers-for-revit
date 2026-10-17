import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";

const { workflowPrompts, registerWorkflowPrompts } = await import("../build/framework/prompts.js");
const { serverTools } = await import("../build/framework/serverTools.js");

const here = path.dirname(fileURLToPath(import.meta.url));
const manifest = JSON.parse(fs.readFileSync(path.join(here, "..", "src", "framework", "tools.manifest.json"), "utf8"));
const legacyDir = path.join(here, "..", "src", "tools");

/** Every tool name the server can expose: manifest tools, server-only tools and legacy tools. */
function knownToolNames() {
  const names = new Set(manifest.tools.map((t) => t.name));
  for (const t of serverTools) names.add(t.name);
  for (const f of fs.readdirSync(legacyDir)) {
    if (f.endsWith(".ts") && f !== "register.ts" && f !== "index.ts") names.add(f.replace(/\.ts$/, ""));
  }
  return names;
}

/** Backticked snake_case identifiers = tool names by the prompts' convention. */
function referencedTools(text) {
  return [...text.matchAll(/`([a-z][a-z0-9]*(?:_[a-z0-9]+)+)`/g)].map((m) => m[1]);
}

async function connect() {
  const server = new McpServer({ name: "prompts-test", version: "0.0.0" });
  const registered = registerWorkflowPrompts(server);
  const client = new Client({ name: "prompts-test-client", version: "0.0.0" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);
  return { server, client, registered };
}

test("workflow prompts are listed over MCP with their arguments", async () => {
  const { client, server, registered } = await connect();
  try {
    const { prompts } = await client.listPrompts();
    const names = prompts.map((p) => p.name).sort();
    assert.deepEqual(names, ["roteiro_ambiente", "roteiro_conversao", "roteiro_documentacao", "roteiro_familia", "roteiro_marcenaria", "roteiro_qa"]);
    assert.deepEqual([...registered].sort(), names);
    for (const p of prompts) {
      assert.ok(p.description && p.description.length > 20, `${p.name} needs a description`);
      const def = workflowPrompts.find((w) => w.name === p.name);
      assert.deepEqual((p.arguments ?? []).map((a) => a.name).sort(), def.args.map((a) => a.name).sort());
    }
  } finally {
    await client.close();
    await server.close();
  }
});

test("every prompt mentions only existing tool names and the approval/dryRun conventions", async () => {
  const known = knownToolNames();
  const { client, server } = await connect();
  try {
    for (const def of workflowPrompts) {
      for (const args of [{}, Object.fromEntries(def.args.map((a) => [a.name, "teste"]))]) {
        const res = await client.getPrompt({ name: def.name, arguments: args });
        assert.equal(res.messages.length, 1);
        const text = res.messages[0].content.text;
        const tools = referencedTools(text);
        assert.ok(tools.length >= 5, `${def.name} references too few tools`);
        const unknown = [...new Set(tools.filter((t) => !known.has(t)))];
        assert.deepEqual(unknown, [], `${def.name} mentions tools that do not exist: ${unknown.join(", ")}`);
        for (const must of ["get_session_context", "propose_change_plan", "wait_for_plan_decision", "execute_plan"]) {
          assert.ok(tools.includes(must), `${def.name} should mention ${must}`);
        }
        assert.match(text, /dryRun:true/);
      }
    }
  } finally {
    await client.close();
    await server.close();
  }
});

test("roteiro_qa adds the deep audits only for profundidade=completa", () => {
  const qa = workflowPrompts.find((p) => p.name === "roteiro_qa");
  assert.ok(!referencedTools(qa.build({})).includes("audit_families"));
  assert.ok(referencedTools(qa.build({ profundidade: "completa" })).includes("audit_families"));
});

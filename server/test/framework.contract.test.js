import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "fs";
import os from "os";
import path from "path";
import { fileURLToPath } from "url";

process.env.REVIT_MCP_OUT_DIR = fs.mkdtempSync(path.join(os.tmpdir(), "revit-mcp-out-"));

const { McpServer } = await import("@modelcontextprotocol/sdk/server/mcp.js");
const { Client } = await import("@modelcontextprotocol/sdk/client/index.js");
const { InMemoryTransport } = await import("@modelcontextprotocol/sdk/inMemory.js");
const { registerTools } = await import("../build/tools/register.js");
const fw = await import("../build/framework/index.js");

const here = path.dirname(fileURLToPath(import.meta.url));
const SAMPLE = fw.loadManifest(path.join(here, "fixtures", "sample.manifest.json"));
const COMMITTED = fw.loadManifest(path.join(here, "..", "src", "framework", "tools.manifest.json"));
const ALL = { toolsets: null, readOnly: false };

const LEGACY_TOOLS = fs
  .readdirSync(path.join(here, "..", "src", "tools"))
  .filter((f) => f.endsWith(".ts") && f !== "register.ts")
  .map((f) => fs.readFileSync(path.join(here, "..", "src", "tools", f), "utf8"))
  .flatMap((src) => [...src.matchAll(/server\.tool\(\s*"([a-z_]+)"/g)].map((m) => m[1]));

/** Spin up a real McpServer + client over an in-memory transport. */
async function connect(options) {
  const server = new McpServer({ name: "test", version: "0.0.0" });
  const calls = [];
  const send = async (command, params, document) => {
    calls.push({ command, params, document });
    return { ok: true, data: { echoed: command }, warnings: [], dryRun: params.dryRun === true };
  };
  await registerTools(server, { send, ...options });
  const [a, b] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: "test-client", version: "0.0.0" });
  await Promise.all([server.connect(a), client.connect(b)]);
  const { tools } = await client.listTools();
  return { server, client, tools, calls, byName: new Map(tools.map((t) => [t.name, t])) };
}

/** Same name heuristic as ToolManifestBuilder.LooksLikeMeasure (C#). */
const MEASURE_WORDS = ["length", "width", "height", "depth", "thickness", "offset", "elevation", "distance", "radius",
  "diameter", "area", "volume", "angle", "rotation", "spacing", "tolerance", "gap", "size", "x", "y", "z"];
function looksLikeMeasure(name) {
  const n = name.toLowerCase();
  return MEASURE_WORDS.some((w) => n === w || (w.length > 1 && (n.endsWith(w) || n.startsWith(w))));
}

function* walkProperties(schema, prefix = "") {
  for (const [name, prop] of Object.entries(schema?.properties ?? {})) {
    yield [prefix + name, name, prop];
    const inner = prop.type === "array" ? prop.items : prop;
    if (inner?.properties) yield* walkProperties(inner, `${prefix}${name}.`);
  }
}

for (const [label, manifest] of [["committed manifest", COMMITTED], ["sample manifest", SAMPLE]]) {
  test(`${label}: capabilityHash matches its content`, () => {
    assert.equal(manifest.capabilityHash, fw.computeCapabilityHash(manifest.tools));
  });

  test(`${label}: tool contract (descriptions, units, reserved params)`, () => {
    for (const tool of manifest.tools) {
      assert.match(tool.name, /^[a-z][a-z0-9_]*$/);
      assert.ok(["read", "write", "ui"].includes(tool.kind), `${tool.name}: kind`);
      assert.ok(["none", "low", "medium", "high"].includes(tool.risk), `${tool.name}: risk`);
      assert.ok(tool.description.trim().length >= 40, `${tool.name}: description is too short`);
      if (tool.kind === "write") assert.notEqual(tool.risk, "none", `${tool.name}: write tool needs an explicit risk`);
      for (const [where, name, prop] of walkProperties(tool.inputSchema)) {
        assert.ok(!["document", "dryRun"].includes(name) || where.includes("."), `${tool.name}: declares reserved '${name}'`);
        assert.ok((prop.description ?? "").trim().length >= 8, `${tool.name}.${where}: missing description`);
        const t = prop.type === "array" ? prop.items?.type : prop.type;
        if (t === "number" && looksLikeMeasure(name)) {
          assert.ok(["mm", "m2", "m3", "deg"].includes(prop["x-unit"]), `${tool.name}.${where}: numeric measure without [Unit]`);
        }
      }
    }
  });

  test(`${label}: every tool is registered with its faithful schema and annotations`, async () => {
    const { tools, byName, client } = await connect({ manifest, policy: ALL, serverTools: [] });
    for (const tool of manifest.tools) {
      const listed = byName.get(tool.name);
      assert.ok(listed, `${tool.name} not registered`);
      assert.equal(listed.description, tool.description);
      // Faithful: every declared property comes through untouched (descriptions, units, enums, defaults).
      for (const [prop, schema] of Object.entries(tool.inputSchema.properties)) {
        assert.deepEqual(listed.inputSchema.properties[prop], schema, `${tool.name}.${prop}`);
      }
      assert.deepEqual(listed.inputSchema.required, tool.inputSchema.required);
      assert.equal(listed.inputSchema.properties.document.type, "string");
      if (tool.kind === "write") assert.equal(listed.inputSchema.properties.dryRun.type, "boolean");
      else assert.equal(listed.inputSchema.properties.dryRun, undefined);
      assert.equal(listed.annotations.readOnlyHint, tool.kind === "read");
      assert.equal(listed.annotations.destructiveHint, tool.risk === "medium" || tool.risk === "high");
      assert.equal(listed.annotations.idempotentHint, tool.idempotent);
    }
    // Legacy tools are still there, unchanged apart from `document`.
    for (const name of LEGACY_TOOLS) {
      assert.ok(byName.get(name), `legacy ${name} missing`);
      assert.equal(byName.get(name).inputSchema.properties.document.type, "string");
    }
    assert.equal(tools.length, manifest.tools.length + LEGACY_TOOLS.length);
    await client.close();
  });
}

test("legacy tool count is stable (keep the hand-written tools working)", () => {
  assert.ok(LEGACY_TOOLS.length >= 23, `found ${LEGACY_TOOLS.length}`);
  assert.ok(LEGACY_TOOLS.includes("delete_element") && LEGACY_TOOLS.includes("list_open_documents"));
});

test("legacy schemas still come from zod (e.g. delete_element.elementIds)", async () => {
  const { byName, client } = await connect({ manifest: SAMPLE, policy: ALL, serverTools: [] });
  const del = byName.get("delete_element");
  assert.equal(del.inputSchema.properties.elementIds.type, "array");
  assert.deepEqual(del.inputSchema.required, ["elementIds"]);
  await client.close();
});

test("call: document routes and is stripped, dryRun forwarded for write tools", async () => {
  const { client, calls } = await connect({ manifest: SAMPLE, policy: ALL, serverTools: [] });
  const r = await client.callTool({
    name: "sample_move_elements",
    arguments: { elementIds: [1, 2], offset: { x: 1000, y: 0 }, document: "Torre A", dryRun: true },
  });
  assert.equal(r.isError, false);
  assert.deepEqual(calls[0], {
    command: "sample_move_elements",
    params: { elementIds: [1, 2], offset: { x: 1000, y: 0 }, dryRun: true },
    document: "Torre A",
  });
  assert.equal(JSON.parse(r.content[0].text).dryRun, true);

  await client.callTool({ name: "sample_find_elements", arguments: {} });
  assert.deepEqual(calls[1].params, { limit: 100, includeTypes: false }, "defaults applied, no dryRun on read tools");
  await client.close();
});

test("call: invalid arguments are rejected before reaching Revit", async () => {
  const { client, calls } = await connect({ manifest: SAMPLE, policy: ALL, serverTools: [] });
  const r = await client.callTool({ name: "sample_move_elements", arguments: { offset: { x: "far" }, bogus: 1 } });
  assert.equal(r.isError, true);
  const text = r.content[0].text;
  assert.match(text, /^Error INVALID_ARGUMENTS/);
  assert.match(text, /missing required property 'elementIds'/);
  assert.match(text, /unknown property 'bogus'/);
  assert.match(text, /offset\.x: must be number/);
  assert.equal(calls.length, 0);
  await client.close();
});

test("REVIT_MCP_TOOLSETS filters manifest and legacy tools", async () => {
  const policy = fw.policyFromEnv({ REVIT_MCP_TOOLSETS: "query" });
  const { tools, client } = await connect({ manifest: SAMPLE, policy, serverTools: [] });
  const names = tools.map((t) => t.name).sort();
  assert.deepEqual(names, ["list_open_documents", "set_target_document", "sample_find_elements"].sort());
  await client.close();

  const withLegacy = await connect({ manifest: SAMPLE, policy: fw.policyFromEnv({ REVIT_MCP_TOOLSETS: "editing,legacy" }), serverTools: [] });
  assert.ok(withLegacy.byName.has("sample_move_elements"));
  assert.ok(withLegacy.byName.has("delete_element"));
  assert.ok(!withLegacy.byName.has("sample_find_elements"));
  await withLegacy.client.close();
});

test("REVIT_MCP_READ_ONLY=1 keeps only read tools", async () => {
  const policy = fw.policyFromEnv({ REVIT_MCP_READ_ONLY: "1" });
  assert.equal(policy.readOnly, true);
  const { tools, byName, client } = await connect({ manifest: SAMPLE, policy, serverTools: [] });
  assert.ok(byName.has("sample_find_elements"));
  assert.ok(!byName.has("sample_move_elements"));
  assert.ok(!byName.has("sample_select_elements"));
  for (const legacyWrite of ["delete_element", "create_level", "send_code_to_revit", "operate_element"]) {
    assert.ok(!byName.has(legacyWrite), legacyWrite);
  }
  assert.ok(byName.has("get_current_view_info"));
  assert.ok(tools.every((t) => !t.inputSchema.properties?.dryRun));
  await client.close();
});

test("a framework tool replaces a legacy tool of the same name", async () => {
  const manifest = {
    version: 1,
    capabilityHash: "",
    tools: [{ ...SAMPLE.tools[0], name: "get_current_view_info" }],
  };
  manifest.capabilityHash = fw.computeCapabilityHash(manifest.tools);
  const { tools, byName, client } = await connect({ manifest, policy: ALL, serverTools: [] });
  assert.equal(byName.get("get_current_view_info").description, SAMPLE.tools[0].description);
  assert.equal(tools.filter((t) => t.name === "get_current_view_info").length, 1);
  await client.close();
});

test("defineServerTool: same envelope, document scope, warnings and ToolFailure", async () => {
  const echo = fw.defineServerTool({
    name: "echo_context",
    description: "Echo the arguments back; used by the framework tests only.",
    targetsDocument: true,
    idempotent: true,
    inputSchema: {
      type: "object",
      properties: { say: { type: "string", description: "Text to echo." } },
      required: ["say"],
      additionalProperties: false,
    },
    async run(args, ctx) {
      if (args.say === "fail") throw new fw.ToolFailure("NOPE", "Asked to fail.", "Say something else.");
      ctx.warn("just a warning");
      const sent = await ctx.send("say_hello", {});
      return { said: args.say, document: ctx.document, sent: sent.data };
    },
  });
  const { client, byName, calls } = await connect({ manifest: SAMPLE, policy: ALL, serverTools: [echo] });
  const listed = byName.get("echo_context");
  assert.equal(listed.inputSchema.properties.say.description, "Text to echo.");
  assert.equal(listed.inputSchema.properties.document.type, "string");
  assert.equal(listed.annotations.readOnlyHint, true);

  const r = await client.callTool({ name: "echo_context", arguments: { say: "hi", document: "B" } });
  const payload = JSON.parse(r.content[0].text);
  assert.deepEqual(payload.data, { said: "hi", document: "B", sent: { echoed: "say_hello" } });
  assert.deepEqual(payload.warnings, [{ message: "just a warning" }]);
  assert.equal(calls[0].document, "B");

  const bad = await client.callTool({ name: "echo_context", arguments: { say: "fail" } });
  assert.equal(bad.isError, true);
  assert.match(bad.content[0].text, /^Error NOPE: Asked to fail\.\nHint: Say something else\./);
  await client.close();

  assert.throws(() => fw.defineServerTool({ name: "BadName", description: "x".repeat(30), run() {} }));
});

test("framework tools reject schemas that declare reserved params", () => {
  assert.throws(
    () => fw.withFrameworkParams({ type: "object", properties: { dryRun: { type: "boolean" } } }, "write", { name: "t" }),
    /dryRun/
  );
});

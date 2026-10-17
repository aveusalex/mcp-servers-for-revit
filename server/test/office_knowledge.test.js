import { test } from "node:test";
import assert from "node:assert/strict";
import os from "os";
import path from "path";
import fs from "fs";
import { fileURLToPath } from "url";

const { listTopics, readTopic, normalizeTopic, resolveKnowledgeDir, registerGetOfficeKnowledgeTool } =
  await import("../build/tools/get_office_knowledge.js");

const here = path.dirname(fileURLToPath(import.meta.url));
const repoKnowledge = path.resolve(here, "..", "..", "knowledge");

function makeDir() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "revit-mcp-knowledge-"));
  fs.writeFileSync(path.join(dir, "README.md"), "# Readme\n");
  fs.writeFileSync(path.join(dir, "folhas.md"), "# Folhas\nA1\n");
  fs.mkdirSync(path.join(dir, "workflows"));
  fs.writeFileSync(path.join(dir, "workflows", "familia.md"), "# Família\npassos\n");
  fs.writeFileSync(path.join(dir, "notes.txt"), "ignored");
  return dir;
}

/** Minimal McpServer stand-in that captures the registered handler. */
function captureTool() {
  const captured = {};
  registerGetOfficeKnowledgeTool({
    tool: (name, _description, _shape, cb) => {
      captured.name = name;
      captured.cb = cb;
    },
  });
  return captured;
}

test("default knowledge dir is the repo knowledge/ folder and env overrides it", () => {
  assert.equal(resolveKnowledgeDir({}), repoKnowledge);
  assert.equal(resolveKnowledgeDir({ REVIT_MCP_KNOWLEDGE_DIR: "C:\\x\\kb" }), path.resolve("C:\\x\\kb"));
});

test("repo ships the three example topics and hides README", () => {
  const topics = listTopics(repoKnowledge).map((t) => t.topic);
  for (const t of ["nomenclatura", "folhas", "parametros"]) assert.ok(topics.includes(t), t);
  assert.ok(!topics.map((t) => t.toLowerCase()).includes("readme"));
});

test("lists markdown topics recursively with titles", () => {
  const dir = makeDir();
  const topics = listTopics(dir);
  assert.deepEqual(topics.map((t) => t.topic), ["folhas", "workflows/familia"]);
  assert.equal(topics[1].title, "Família");
});

test("reads a topic and re-reads it after an edit", () => {
  const dir = makeDir();
  let r = readTopic(dir, "folhas");
  assert.equal(r.ok, true);
  assert.match(r.content, /A1/);
  fs.writeFileSync(path.join(dir, "folhas.md"), "# Folhas\nA3\n");
  r = readTopic(dir, "folhas.md");
  assert.equal(r.ok, true);
  assert.match(r.content, /A3/);
  assert.equal(readTopic(dir, "workflows/familia").ok, true);
  assert.equal(readTopic(dir, "workflows\\familia").ok, true);
});

test("rejects path traversal and absolute paths", () => {
  const dir = makeDir();
  const outside = path.join(path.dirname(dir), "secret-outside.md");
  fs.writeFileSync(outside, "secret");
  try {
    for (const bad of ["../secret-outside", "..\\secret-outside", "/etc/passwd", "C:\\Windows\\win", "workflows/../../x", ".", "", "a/b/c/d/e"]) {
      const r = readTopic(dir, bad);
      assert.equal(r.ok, false, bad);
      assert.equal(r.code, "INVALID_TOPIC", bad);
    }
    assert.equal(normalizeTopic("../x"), null);
    assert.equal(readTopic(dir, "missing").code, "NOT_FOUND");
  } finally {
    fs.rmSync(outside, { force: true });
  }
});

test("refuses a symlink that points outside the folder", (t) => {
  const dir = makeDir();
  const outsideDir = fs.mkdtempSync(path.join(os.tmpdir(), "revit-mcp-outside-"));
  fs.writeFileSync(path.join(outsideDir, "secret.md"), "secret");
  try {
    fs.symlinkSync(path.join(outsideDir, "secret.md"), path.join(dir, "link.md"), "file");
  } catch {
    t.skip("symlinks not permitted on this machine");
    return;
  }
  const r = readTopic(dir, "link");
  assert.equal(r.ok, false);
  assert.equal(r.code, "NOT_FOUND");
});

test("tool handler lists topics, reads one, and reports unknown topics as errors", async () => {
  const dir = makeDir();
  const previous = process.env.REVIT_MCP_KNOWLEDGE_DIR;
  process.env.REVIT_MCP_KNOWLEDGE_DIR = dir;
  try {
    const { name, cb } = captureTool();
    assert.equal(name, "get_office_knowledge");

    const list = await cb({});
    const listed = JSON.parse(list.content[0].text);
    assert.equal(listed.count, 2);

    const one = await cb({ topic: "folhas" });
    assert.ok(!one.isError);
    assert.match(one.content[0].text, /# Folhas/);

    const bad = await cb({ topic: "../../x" });
    assert.equal(bad.isError, true);
    const err = JSON.parse(bad.content[0].text);
    assert.equal(err.error.code, "INVALID_TOPIC");
    assert.deepEqual(err.availableTopics, ["folhas", "workflows/familia"]);
  } finally {
    if (previous === undefined) delete process.env.REVIT_MCP_KNOWLEDGE_DIR;
    else process.env.REVIT_MCP_KNOWLEDGE_DIR = previous;
  }
});

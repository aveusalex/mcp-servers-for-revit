import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "child_process";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

// Staleness checks: they need the dotnet SDK (and, for the real manifest, a built RevitMCPCommandSet.dll).
// Without them the tests are skipped; `npm run manifest:check` is the strict variant for CI / pre-merge.
const here = path.dirname(fileURLToPath(import.meta.url));
const script = path.join(here, "..", "scripts", "manifest.mjs");

function runScript(...args) {
  return spawnSync(process.execPath, [script, ...args], { encoding: "utf8" });
}

const hasDotnet = spawnSync("dotnet", ["--version"], { encoding: "utf8" }).status === 0;

function commandSetBuilt() {
  if (process.env.REVIT_MCP_COMMANDSET_DLL) return fs.existsSync(process.env.REVIT_MCP_COMMANDSET_DLL);
  const bin = path.join(here, "..", "..", "commandset", "bin");
  return fs.existsSync(bin) && fs.readdirSync(bin).some((c) => fs.existsSync(path.join(bin, c, "RevitMCPCommandSet.dll")));
}

test(
  "ManifestGen output for the fixture assembly matches test/fixtures/sample.manifest.json",
  { skip: !hasDotnet && "dotnet SDK not available", timeout: 300_000 },
  () => {
    const r = runScript("--sample", "--check");
    assert.equal(r.status, 0, r.stdout + r.stderr);
  }
);

test(
  "committed src/framework/tools.manifest.json is up to date with RevitMCPCommandSet.dll",
  { skip: (!hasDotnet && "dotnet SDK not available") || (!commandSetBuilt() && "command set not built"), timeout: 300_000 },
  () => {
    const r = runScript("--check");
    assert.equal(r.status, 0, `${r.stdout}${r.stderr}\nRun \`npm run manifest\` and commit the result.`);
  }
);

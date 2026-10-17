// Regenerates (or checks) the tools manifest with tools/ManifestGen.
//
//   npm run manifest            -> write src/framework/tools.manifest.json from RevitMCPCommandSet.dll
//   npm run manifest:check      -> exit 1 if the committed manifest is stale
//   npm run manifest:sample     -> regenerate test/fixtures/sample.manifest.json from the fixture assembly
//   node scripts/manifest.mjs --sample --check
//
// The command set must be built first (e.g. `dotnet build commandset -c "Release R26"`), or point
// REVIT_MCP_COMMANDSET_DLL at a RevitMCPCommandSet.dll. Pass --build to build "Release R26" here.
// Exit codes: 0 ok, 1 stale/failed, 3 prerequisites missing (dotnet or the command set build).
import { spawnSync } from "child_process";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const server = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const repo = path.resolve(server, "..");
const args = new Set(process.argv.slice(2));
const check = args.has("--check");
const sample = args.has("--sample");

function run(cmd, cmdArgs, opts = {}) {
  const r = spawnSync(cmd, cmdArgs, { stdio: opts.quiet ? "pipe" : "inherit", encoding: "utf8", ...opts });
  if (r.error) throw r.error;
  return r;
}

function dotnetAvailable() {
  try {
    return run("dotnet", ["--version"], { quiet: true }).status === 0;
  } catch {
    return false;
  }
}

function build(project, configuration) {
  const r = run("dotnet", ["build", project, "-c", configuration, "-nologo", "-v", "q"], { quiet: true });
  if (r.status !== 0) {
    process.stderr.write(r.stdout + r.stderr);
    process.exit(1);
  }
}

/** Newest RevitMCPCommandSet.dll under commandset/bin (any configuration), or the env override. */
function findCommandSetDll() {
  if (process.env.REVIT_MCP_COMMANDSET_DLL) return process.env.REVIT_MCP_COMMANDSET_DLL;
  const bin = path.join(repo, "commandset", "bin");
  if (!fs.existsSync(bin)) return null;
  const found = [];
  for (const config of fs.readdirSync(bin)) {
    for (const candidate of [path.join(bin, config, "RevitMCPCommandSet.dll")]) {
      if (fs.existsSync(candidate)) found.push({ file: candidate, mtime: fs.statSync(candidate).mtimeMs });
    }
  }
  found.sort((a, b) => b.mtime - a.mtime);
  return found[0]?.file ?? null;
}

if (!dotnetAvailable()) {
  console.error("manifest: dotnet SDK not found on PATH.");
  process.exit(3);
}

let assembly;
let out;
if (sample) {
  build(path.join(repo, "tools", "ManifestGen.Fixture"), "Release");
  assembly = path.join(repo, "tools", "ManifestGen.Fixture", "bin", "Release", "net8.0", "ManifestGen.Fixture.dll");
  out = path.join(server, "test", "fixtures", "sample.manifest.json");
} else {
  if (args.has("--build")) build(path.join(repo, "commandset", "RevitMCPCommandSet.csproj"), "Release R26");
  assembly = findCommandSetDll();
  if (!assembly) {
    console.error(
      'manifest: RevitMCPCommandSet.dll not found. Build it (dotnet build commandset -c "Release R26"), ' +
        "pass --build, or set REVIT_MCP_COMMANDSET_DLL."
    );
    process.exit(3);
  }
  out = path.join(server, "src", "framework", "tools.manifest.json");
}

build(path.join(repo, "tools", "ManifestGen"), "Release");
const gen = path.join(repo, "tools", "ManifestGen", "bin", "Release", "net8.0", "ManifestGen.dll");
const genArgs = [gen, "--assembly", assembly, "--out", out];
if (check) genArgs.push("--check");
if (sample) genArgs.push("--no-auto-refs");
const r = run("dotnet", genArgs);
process.exit(r.status ?? 1);

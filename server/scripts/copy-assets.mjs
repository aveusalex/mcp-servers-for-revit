// Copies non-TS runtime assets (the tools manifest) next to the compiled framework code.
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const assets = [["src/framework/tools.manifest.json", "build/framework/tools.manifest.json"]];

for (const [from, to] of assets) {
  fs.mkdirSync(path.dirname(path.join(root, to)), { recursive: true });
  fs.copyFileSync(path.join(root, from), path.join(root, to));
}

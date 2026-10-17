import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { runWithExplicitDocument } from "../utils/targeting.js";
import { legacyCallScope } from "../utils/ConnectionManager.js";
import {
  installFaithfulToolListing,
  isExposed,
  legacyKind,
  LEGACY_TOOLSET,
  policyFromEnv,
  registerFrameworkTools,
  type ExposurePolicy,
  type FrameworkOptions,
} from "../framework/index.js";

/**
 * Optional per-call document selector added to every tool. Accepts a docId
 * (Document.ProjectInformation.UniqueId) or an exact title. Left unset, the
 * command targets the fixed document (set_target_document) or, if only one
 * project is open, that one — so single-project use is unchanged.
 */
const documentParam = z
  .string()
  .optional()
  .describe(
    "Optional target document: a docId (from list_open_documents) or an exact title. " +
      "Omit when only one project is open, or when a default has been fixed with set_target_document."
  );

/**
 * Wrap McpServer.tool so that, whatever a tool registers, every call:
 *   1. advertises an extra optional `document` argument, and
 *   2. runs its handler inside an AsyncLocalStorage scope carrying that value,
 *      which targeting.ts reads to route the command.
 *
 * This gives multi-document targeting to all 23 existing tools without editing
 * any of them. Tools that never touch Revit simply ignore the value.
 */
function installDocumentParam(
  server: McpServer,
  policy: ExposurePolicy,
  frameworkNames: Set<string>
): void {
  const original = server.tool.bind(server) as (...args: any[]) => any;

  (server as any).tool = (...args: any[]) => {
    const name = args[0];
    if (typeof name === "string") {
      // A framework (manifest) tool with the same name replaces the legacy one.
      if (frameworkNames.has(name)) {
        console.error(`Legacy tool ${name} skipped: replaced by the framework tool of the same name.`);
        return undefined;
      }
      // REVIT_MCP_TOOLSETS / REVIT_MCP_READ_ONLY apply to legacy tools too (toolset "legacy").
      if (!isExposed({ name, toolset: LEGACY_TOOLSET, kind: legacyKind(name) }, policy)) {
        return undefined;
      }
    }

    // Supported shapes:
    //   tool(name, description, shape, cb)
    //   tool(name, description, cb)
    //   tool(name, shape, cb)  /  tool(name, cb)
    const cbIndex = args.length - 1;
    const cb = args[cbIndex];
    if (typeof cb !== "function") return original(...args);

    const maybeShape = args[cbIndex - 1];
    const hasShape =
      maybeShape &&
      typeof maybeShape === "object" &&
      !Array.isArray(maybeShape);

    const shape = hasShape ? maybeShape : {};
    const mergedShape = { ...shape, document: documentParam };

    const wrappedCb = (toolArgs: any, extra: any) => {
      const document =
        toolArgs && typeof toolArgs === "object" ? toolArgs.document : undefined;
      // Hand the original handler its own args without our injected key.
      let forwarded = toolArgs;
      if (toolArgs && typeof toolArgs === "object" && "document" in toolArgs) {
        forwarded = { ...toolArgs };
        delete forwarded.document;
      }
      // legacyCallScope: broker guidance (approval hint, job polling) is folded
      // into what these tools print — see ConnectionManager.
      return legacyCallScope.run(true, () =>
        runWithExplicitDocument(document, () => cb(forwarded, extra))
      );
    };

    const rebuilt = hasShape
      ? [...args.slice(0, cbIndex - 1), mergedShape, wrappedCb]
      : [...args.slice(0, cbIndex), mergedShape, wrappedCb];
    return original(...rebuilt);
  };
}

export async function registerTools(server: McpServer, options: FrameworkOptions = {}) {
  // Framework tools advertise their manifest JSON Schema verbatim; must be in place before any registration.
  installFaithfulToolListing(server);
  const policy = options.policy ?? policyFromEnv();

  // Manifest tools (generated from [McpTool] classes) and server-only tools go first, so a framework tool can
  // take over a legacy name.
  const frameworkNames = registerFrameworkTools(server, { ...options, policy });

  // Every legacy tool registered after this call gains the optional `document` arg.
  installDocumentParam(server, policy, frameworkNames);

  // 获取当前文件的目录路径
  const __filename = fileURLToPath(import.meta.url);
  const __dirname = path.dirname(__filename);

  // 读取tools目录下的所有文件
  const files = fs.readdirSync(__dirname);

  // 过滤出.ts或.js文件，但排除index文件和register文件
  const toolFiles = files.filter(
    (file) =>
      (file.endsWith(".ts") || file.endsWith(".js")) &&
      file !== "index.ts" &&
      file !== "index.js" &&
      file !== "register.ts" &&
      file !== "register.js"
  );

  // 动态导入并注册每个工具
  for (const file of toolFiles) {
    try {
      const importPath = `./${file.replace(/\.(ts|js)$/, ".js")}`;
      const module = await import(importPath);

      const registerFunctionName = Object.keys(module).find(
        (key) => key.startsWith("register") && typeof module[key] === "function"
      );

      if (registerFunctionName) {
        module[registerFunctionName](server);
        console.error(`Registered tool: ${file}`);
      } else {
        console.warn(`Warning: no register function found in ${file}`);
      }
    } catch (error) {
      console.error(`Error registering tool ${file}:`, error);
    }
  }
}

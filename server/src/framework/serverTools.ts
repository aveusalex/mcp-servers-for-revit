import type { ServerToolDefinition } from "./registry.js";


/**
 * Server-only tools (plans, jobs, session context, knowledge, ...), registered with the same envelope, size
 * guard, toolset filter and read-only mode as the manifest tools. Add yours here:
 *
 *   // src/framework/tools/get_office_knowledge.ts
 *   export const getOfficeKnowledge = defineServerTool({ name: "get_office_knowledge", ... });
 *
 *   // here
 *   import { getOfficeKnowledge } from "./tools/get_office_knowledge.js";
 *   export const serverTools = [getOfficeKnowledge];
 */
import { approvalTools } from "./tools/approval.js";

export const serverTools: ServerToolDefinition<any>[] = [...approvalTools];

import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { broker } from "../utils/BrokerConnection.js";
import { setFixedTarget } from "../utils/targeting.js";

/**
 * Fixes the default target document for this MCP session (one MCP server == one
 * Claude chat). Once set, tools that omit their own `document` argument route
 * to this document. Clear it by passing an empty target.
 */
export function registerSetTargetDocumentTool(server: McpServer) {
  server.tool(
    "set_target_document",
    "Fix the default Revit document for this session by docId or exact title. Subsequent tool calls that omit `document` " +
      "target it. Pass an empty string to clear the fixed target. Use list_open_documents to see valid values.",
    {
      target: z
        .string()
        .describe(
          "docId or exact title of the document to make the default. Empty string clears the fixed target."
        ),
    },
    async (args: { target: string }) => {
      try {
        const target = (args.target ?? "").trim();

        if (!target) {
          setFixedTarget(undefined);
          return {
            content: [{ type: "text", text: "Cleared the fixed target document." }],
          };
        }

        // Validate against the live set so we fail early with a helpful message.
        const docs = await broker.listDocuments();
        const byId = docs.find((d) => d.docId === target);
        const byTitle = docs.filter((d) => d.title === target);
        const resolved = byId ?? (byTitle.length === 1 ? byTitle[0] : undefined);

        if (!resolved) {
          const reason =
            byTitle.length > 1
              ? `More than one open document is titled "${target}"; pass the docId instead.`
              : `No open document matches "${target}".`;
          return {
            content: [
              {
                type: "text",
                text:
                  `Could not fix target: ${reason} Open documents: ` +
                  docs.map((d) => `${d.title} (${d.docId})`).join(", "),
              },
            ],
          };
        }

        // Store what the caller gave us (docId or title); resolution re-runs per call.
        setFixedTarget(target);
        return {
          content: [
            {
              type: "text",
              text: `Fixed target document set to "${resolved.title}" (${resolved.docId}).`,
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Failed to set target document: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}

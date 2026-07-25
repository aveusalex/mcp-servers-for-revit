import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

/**
 * Lists the selected host document's Revit link-instance tree. Linked models
 * remain read-only: a source RVT must be opened as a normal document before an
 * MCP client can modify it.
 */
export function registerListRevitLinksTool(server: McpServer) {
  server.tool(
    "list_revit_links",
    "List the normalized Revit-link tree in the selected host document, including loaded/unloaded status, source path, attachment type, nested links, instance identity, and each instance's transform into host coordinates. The result returns both top-level and total instance counts, and prevents a nested link from being duplicated as a root. Read-only: linked models cannot be modified through this tool; open the source RVT as a normal document to edit it.",
    {},
    async () => {
      try {
        const response = await withRevitConnection((revitClient) =>
          revitClient.sendCommand("list_revit_links", {})
        );
        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Failed to list Revit links: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}

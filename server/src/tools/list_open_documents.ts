import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { broker } from "../utils/BrokerConnection.js";
import { getFixedTarget } from "../utils/targeting.js";

/**
 * Surfaces every Revit document currently registered with the broker, across
 * all open projects and all Revit processes. This is the tool Claude uses to
 * orient itself before acting in a multi-document session.
 */
export function registerListOpenDocumentsTool(server: McpServer) {
  server.tool(
    "list_open_documents",
    "List all Revit documents currently open and registered with the broker, across every open project and Revit version. " +
      "Returns each document's docId, title, path, whether it is the active window, its Revit session and version. " +
      "Use the docId (or exact title) as the `document` argument on other tools, or fix a default with set_target_document.",
    {},
    async () => {
      try {
        const docs = await broker.listDocuments();
        const fixed = getFixedTarget();
        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(
                {
                  count: docs.length,
                  fixedTarget: fixed ?? null,
                  documents: docs.map((d) => ({
                    docId: d.docId,
                    title: d.title,
                    pathName: d.pathName ?? null,
                    isActive: d.isActive,
                    isWorkshared: d.isWorkshared ?? null,
                    revitVersion: d.revitVersion ?? null,
                    sessionId: d.sessionId ?? null,
                  })),
                  note:
                    "Commands to different documents are serialized onto Revit's single UI thread, so they run one after another, not in parallel.",
                },
                null,
                2
              ),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Failed to list open documents: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}

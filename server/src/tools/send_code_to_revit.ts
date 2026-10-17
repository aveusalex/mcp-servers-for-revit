import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const transactionModeSchema = z
  .enum(["auto", "rollback", "none"])
  .default("auto")
  .describe(
    "How the snippet runs with respect to Revit transactions. " +
      "'auto' (default): wrapped in a transaction that is committed on success and rolled back on error. " +
      "'rollback': read mode - wrapped in a transaction that is ALWAYS rolled back, so the model never changes; " +
      "use it to inspect the model or to try an edit safely before running it with 'auto'. " +
      "'none': no transaction - for read-only code, or code that opens its own Transaction."
  );

/** Shape of the result the add-in returns (see ExecuteCodeEventHandler.ExecutionResultInfo). */
interface CodeExecutionResult {
  success?: boolean;
  result?: unknown;
  errorMessage?: string;
  errorType?: string;
  exceptionType?: string;
  line?: number;
  errors?: { code: string; line?: number; column?: number; message: string }[];
  hints?: string[];
  transactionMode?: string;
  rolledBack?: boolean;
}

/** Formats a failed execution as readable text followed by the structured details. */
export function formatCodeFailure(response: CodeExecutionResult): string {
  const lines = [`Code execution failed: ${response.errorMessage ?? "unknown error"}`];
  if (response.hints?.length) {
    lines.push("", "Hints:", ...response.hints.map((h) => `- ${h}`));
  }
  const details = {
    errorType: response.errorType,
    exceptionType: response.exceptionType,
    line: response.line,
    errors: response.errors,
    transactionMode: response.transactionMode,
    rolledBack: response.rolledBack,
  };
  lines.push("", JSON.stringify(details, null, 2));
  return lines.join("\n");
}

export function registerSendCodeToRevitTool(server: McpServer) {
  server.tool(
    "send_code_to_revit",
    "Send C# code to Revit for execution. The code is the body of " +
      "'static object Execute(Document document, object[] parameters)': use 'document' (the target document, not " +
      "necessarily the active one), read 'parameters', and end with a return statement (e.g. 'return null;'). " +
      "Imported namespaces: System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB, Autodesk.Revit.UI. " +
      "Revit internal units are feet and radians. Use transactionMode 'rollback' for read-only inspection or to " +
      "dry-run an edit. Failures return the snippet line number and hints.",
    {
      code: z
        .string()
        .describe(
          "The C# statements to execute in Revit (a method body). The target Document is available as 'document' and the arguments as 'parameters'."
        ),
      parameters: z
        .array(z.string())
        .optional()
        .describe(
          "Optional execution parameters that will be passed to your code"
        ),
      transactionMode: transactionModeSchema,
    },
    async (args, extra) => {
      const params = {
        code: args.code,
        parameters: args.parameters || [],
        transactionMode: args.transactionMode ?? "auto",
      };

      try {
        const response = (await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("send_code_to_revit", params);
        })) as CodeExecutionResult;

        // Compile errors and exceptions thrown by the snippet come back as a normal response.
        if (response?.success === false) {
          return {
            content: [{ type: "text", text: formatCodeFailure(response) }],
            isError: true,
          };
        }

        const note =
          response?.transactionMode === "rollback"
            ? "\n(rollback mode: every change was undone, the model is unchanged)"
            : "";
        return {
          content: [
            {
              type: "text",
              text: `Code execution successful!${note}\nResult: ${JSON.stringify(
                response,
                null,
                2
              )}`,
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Code execution failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}

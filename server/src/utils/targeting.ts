import { AsyncLocalStorage } from "async_hooks";
import { broker, DocumentMeta } from "./BrokerConnection.js";

/**
 * Per-call slot carrying the explicit `document` argument (a docId or a title)
 * that the registration wrapper reads off every tool call. AsyncLocalStorage
 * keeps it scoped to the one tool invocation without threading a parameter
 * through 23 tool files.
 */
const explicitDocument = new AsyncLocalStorage<string | undefined>();

/** Session-fixed default target set via set_target_document (process-global:
 *  one MCP server == one Claude chat). */
let fixedTarget: string | undefined;

export function setFixedTarget(docIdOrTitle: string | undefined): void {
  fixedTarget = docIdOrTitle;
}

export function getFixedTarget(): string | undefined {
  return fixedTarget;
}

export function runWithExplicitDocument<T>(
  document: string | undefined,
  fn: () => Promise<T>
): Promise<T> {
  return explicitDocument.run(document, fn);
}

export class TargetError extends Error {
  code: string;
  constructor(code: string, message: string) {
    super(message);
    this.code = code;
  }
}

/** Match a user-supplied string against the live document set: first by exact
 *  docId, then by exact title (ambiguous title -> ask for the docId). */
function matchDocument(ref: string, docs: DocumentMeta[]): DocumentMeta {
  const byId = docs.find((d) => d.docId === ref);
  if (byId) return byId;
  const byTitle = docs.filter((d) => d.title === ref);
  if (byTitle.length === 1) return byTitle[0];
  if (byTitle.length > 1) {
    throw new TargetError(
      "AMBIGUOUS_TARGET",
      `More than one open document is titled "${ref}". Pass the docId instead. Candidates: ` +
        byTitle.map((d) => `${d.title} (${d.docId})`).join(", ")
    );
  }
  throw new TargetError(
    "DOCUMENT_NOT_FOUND",
    `No open document matches "${ref}". Use list_open_documents to see what is open.`
  );
}

/**
 * Resolve the target docId for the current tool call, in the order the spec
 * mandates:
 *   1. explicit `document` argument on the call
 *   2. the fixed target from set_target_document
 *   3. if exactly one document is open, use it (this is what keeps the
 *      single-project workflow working with zero changes)
 *   4. otherwise AMBIGUOUS_TARGET, listing the options
 *
 * @returns the resolved docId
 */
export async function resolveTargetDocId(): Promise<string> {
  const docs = await broker.listDocuments();

  const explicit = explicitDocument.getStore();
  if (explicit) return matchDocument(explicit, docs).docId;

  if (fixedTarget) return matchDocument(fixedTarget, docs).docId;

  if (docs.length === 1) return docs[0].docId;

  if (docs.length === 0) {
    throw new TargetError(
      "DOCUMENT_NOT_FOUND",
      "No Revit documents are registered. Open a project in Revit (the plugin registers it automatically)."
    );
  }

  throw new TargetError(
    "AMBIGUOUS_TARGET",
    "Several documents are open and no target was specified. Pass `document` (docId or title) " +
      "or call set_target_document first. Open documents: " +
      docs.map((d) => `${d.title}${d.isActive ? " [active]" : ""} (${d.docId})`).join(", ")
  );
}

import { z } from "zod";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";

/**
 * Office knowledge: markdown files (one per topic) owned by the office, read
 * from REVIT_MCP_KNOWLEDGE_DIR or, by default, the repo's `knowledge/` folder.
 * Files are re-read on every call, so edits take effect without a restart.
 * Server-only tool: it never talks to Revit.
 */

const TOPIC_SEGMENT = /^[A-Za-z0-9][A-Za-z0-9_-]*$/;
const MAX_TOPIC_BYTES = 256 * 1024;
const EXCLUDED = new Set(["readme"]);

export interface KnowledgeTopic {
  topic: string;
  title: string;
  bytes: number;
  modified: string;
}

/** Knowledge folder: REVIT_MCP_KNOWLEDGE_DIR, else <repo>/knowledge (server/build/tools -> ../../../knowledge). */
export function resolveKnowledgeDir(env: NodeJS.ProcessEnv = process.env): string {
  const fromEnv = env.REVIT_MCP_KNOWLEDGE_DIR?.trim();
  if (fromEnv) return path.resolve(fromEnv);
  const here = path.dirname(fileURLToPath(import.meta.url));
  return path.resolve(here, "..", "..", "..", "knowledge");
}

/** Validates a topic name ("folhas", "workflows/familia") and returns null when it is not acceptable. */
export function normalizeTopic(topic: string): string | null {
  let t = topic.trim().replace(/\\/g, "/");
  if (t.toLowerCase().endsWith(".md")) t = t.slice(0, -3);
  const segments = t.split("/");
  if (segments.length === 0 || segments.length > 4) return null;
  if (!segments.every((s) => TOPIC_SEGMENT.test(s))) return null;
  return segments.join("/");
}

function isInside(base: string, candidate: string): boolean {
  const rel = path.relative(base, candidate);
  return rel !== "" && !rel.startsWith("..") && !path.isAbsolute(rel);
}

function firstHeading(content: string): string | null {
  const match = content.match(/^#\s+(.+?)\s*$/m);
  return match ? match[1] : null;
}

/** Lists every *.md topic under the folder (recursively), excluding README. */
export function listTopics(dir: string): KnowledgeTopic[] {
  if (!fs.existsSync(dir)) return [];
  const base = fs.realpathSync(dir);
  const topics: KnowledgeTopic[] = [];

  const walk = (current: string, prefix: string, depth: number) => {
    if (depth > 4) return;
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      if (entry.name.startsWith(".")) continue;
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) {
        if (TOPIC_SEGMENT.test(entry.name)) walk(full, `${prefix}${entry.name}/`, depth + 1);
        continue;
      }
      if (!entry.isFile() || !entry.name.toLowerCase().endsWith(".md")) continue;
      const name = entry.name.slice(0, -3);
      if (!TOPIC_SEGMENT.test(name)) continue;
      if (prefix === "" && EXCLUDED.has(name.toLowerCase())) continue;
      const stat = fs.statSync(full);
      let head = "";
      try {
        head = fs.readFileSync(full, "utf8").slice(0, 4096);
      } catch {
        // unreadable file: still list it
      }
      topics.push({
        topic: `${prefix}${name}`,
        title: firstHeading(head) ?? name,
        bytes: stat.size,
        modified: stat.mtime.toISOString(),
      });
    }
  };

  walk(base, "", 1);
  return topics.sort((a, b) => a.topic.localeCompare(b.topic));
}

export type ReadTopicResult =
  | { ok: true; topic: string; content: string; modified: string }
  | { ok: false; code: "INVALID_TOPIC" | "NOT_FOUND" | "TOO_LARGE"; message: string };

/** Reads one topic, refusing anything that would resolve outside the knowledge folder. */
export function readTopic(dir: string, topic: string): ReadTopicResult {
  const normalized = normalizeTopic(topic);
  if (!normalized) {
    return {
      ok: false,
      code: "INVALID_TOPIC",
      message: `Invalid topic '${topic}'. Use a topic name from the list, e.g. 'folhas' (letters, digits, '-' and '_', optionally 'folder/name').`,
    };
  }
  if (!fs.existsSync(dir)) {
    return { ok: false, code: "NOT_FOUND", message: `Knowledge folder not found: ${dir}` };
  }

  const base = fs.realpathSync(dir);
  const candidate = path.resolve(base, ...normalized.split("/")) + ".md";
  if (!isInside(base, candidate) || !fs.existsSync(candidate)) {
    return { ok: false, code: "NOT_FOUND", message: `Topic '${normalized}' not found.` };
  }
  // Re-check after resolving symlinks so a link cannot point outside the folder.
  const real = fs.realpathSync(candidate);
  if (!isInside(base, real) || !fs.statSync(real).isFile()) {
    return { ok: false, code: "NOT_FOUND", message: `Topic '${normalized}' not found.` };
  }
  const stat = fs.statSync(real);
  if (stat.size > MAX_TOPIC_BYTES) {
    return { ok: false, code: "TOO_LARGE", message: `Topic '${normalized}' is larger than ${MAX_TOPIC_BYTES} bytes; split it into smaller topics.` };
  }
  return { ok: true, topic: normalized, content: fs.readFileSync(real, "utf8"), modified: stat.mtime.toISOString() };
}

export function registerGetOfficeKnowledgeTool(server: McpServer) {
  server.tool(
    "get_office_knowledge",
    "Read the office's own conventions (naming, sheets, parameters, standards...) kept as markdown topics. " +
      "Call without 'topic' to list the available topics, then call with a topic to read it. " +
      "Follow these conventions when creating or renaming anything in Revit. Content is office-owned and may be in Portuguese.",
    {
      topic: z
        .string()
        .optional()
        .describe("Topic to read, e.g. 'nomenclatura'. Omit to list all topics."),
    },
    async (args) => {
      const dir = resolveKnowledgeDir();
      try {
        if (!args.topic) {
          const topics = listTopics(dir);
          return {
            content: [
              {
                type: "text" as const,
                text: JSON.stringify(
                  {
                    count: topics.length,
                    topics,
                    note:
                      topics.length === 0
                        ? `No topics found in ${dir}. Set REVIT_MCP_KNOWLEDGE_DIR or add markdown files to the knowledge folder.`
                        : "Call get_office_knowledge with one of these topics to read it.",
                  },
                  null,
                  2
                ),
              },
            ],
          };
        }

        const result = readTopic(dir, args.topic);
        if (!result.ok) {
          const available = listTopics(dir).map((t) => t.topic);
          return {
            content: [
              {
                type: "text" as const,
                text: JSON.stringify({ error: { code: result.code, message: result.message }, availableTopics: available }, null, 2),
              },
            ],
            isError: true,
          };
        }

        return {
          content: [
            {
              type: "text" as const,
              text: `<!-- topic: ${result.topic} | modified: ${result.modified} -->\n${result.content}`,
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `Failed to read office knowledge: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}

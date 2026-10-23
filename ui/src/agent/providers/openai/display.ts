import type { StoredMessage } from "../../../lib/types";
import type { DisplayPart } from "../types";

/** A web_search_call as a step card: what it did and on what (docs/providers/design.md 6.9). */
function webPart(item: any): DisplayPart {
  const a = item.action ?? {};
  const isError = item.status !== "completed";
  if (a.type === "open_page") return { kind: "web", title: "Opened a page", detail: a.url, isError, call: { name: "web_fetch", input: { url: a.url } } };
  if (a.type === "find_in_page") {
    const detail = a.pattern ? `"${a.pattern}"${a.url ? ` in ${a.url}` : ""}` : a.url;
    return { kind: "web", title: "Searched in a page", detail, isError, call: { name: "web_search", input: { query: detail } } };
  }
  const query = a.query ?? (Array.isArray(a.queries) ? a.queries.join("; ") : undefined);
  const sources = Array.isArray(a.sources) ? a.sources.map((s: any) => s?.url).filter(Boolean) : [];
  return {
    kind: "web",
    title: "Searched the web",
    detail: query,
    isError,
    call: { name: "web_search", input: { query } },
    result: sources.length ? sources.slice(0, 8).join("\n") : undefined,
  };
}

/** A summary part opens with a bold heading ("**Finding the standard** Looking it up."): shown as a plain sentence. */
function plainHeading(text: string): string {
  return text.replace(/^\*\*(.+?)\*\*\s*/, (_, h: string) => (/[.:!?]$/.test(h) ? `${h} ` : `${h}. `)).trim();
}

/**
 * How an OpenAI assistant message shows: its output in order - reasoning summaries as progress notes, web searches as
 * step cards, and the canonical blocks (text, tool calls) where they came from. Falls back to the plain blocks when
 * the replay no longer matches the content.
 */
export function openaiDisplay(m: StoredMessage): DisplayPart[] {
  const content = typeof m.content === "string" ? [] : m.content;
  const plain = content.map((_, index): DisplayPart => ({ kind: "block", index }));
  const output = (m.replay?.data as { output?: unknown } | undefined)?.output;
  if (!Array.isArray(output)) return plain;
  const parts: DisplayPart[] = [];
  let index = 0;
  for (const item of output as any[]) {
    if (item?.type === "reasoning") {
      const text = (item.summary ?? [])
        .map((s: any) => plainHeading(s?.text ?? ""))
        .filter((t: string) => t.trim())
        .join("\n\n")
        .trim();
      if (text) parts.push({ kind: "progress", text });
    } else if (item?.type === "message") {
      for (const p of item.content ?? []) if (p.type === "output_text") parts.push({ kind: "block", index: index++ });
    } else if (item?.type === "function_call") {
      parts.push({ kind: "block", index: index++ });
    } else if (item?.type === "web_search_call") {
      parts.push(webPart(item));
    }
  }
  return index === content.length ? parts : plain;
}

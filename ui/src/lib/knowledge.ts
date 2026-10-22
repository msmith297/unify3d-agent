import type { KnowledgeIndex } from "./types";

/**
 * The agent's helpers and API notes as the end of the system prompt (knowledge.index from the add-in, which already capped
 * them at about 1,500 tokens). Pure, so the panel checks can run it; changes only when a helper or note is saved.
 */
export function knowledgeSection(index: KnowledgeIndex | null | undefined): string {
  const helpers = index?.helpers ?? [];
  const notes = index?.notes ?? [];
  const disabled = index?.disabled ?? [];
  const omitted = index?.omitted ?? { helpers: 0, notes: 0 };
  const one = (s: string) => s.replace(/\s+/g, " ").trim();
  let text = `\n\n# Helpers (call them in code cells, before writing raw API calls)\n${helpers.length ? helpers.map((h) => `- ${one(h.line)}${h.source?.startsWith("plugin:") ? ` [${h.source}]` : ""}`).join("\n") : "(none yet)"}`;
  if (omitted.helpers > 0) text += `\n(${omitted.helpers} more: list_helpers)`;
  if (disabled.length) text += `\nDisabled, don't call: ${disabled.map((d) => `${d.name}${d.reason ? ` (${one(d.reason)})` : ""}`).join("; ")}`;
  if (notes.length || omitted.notes > 0) {
    text += `\n\n# API notes (lessons you saved: apply them)\n${notes.map((n) => `- ${one(n.line)}`).join("\n")}`;
    if (omitted.notes > 0) text += `\n(${omitted.notes} more: list_api_notes with a query)`;
  }
  return text;
}

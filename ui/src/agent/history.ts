import type { ContentBlockParam, MessageParam } from "../lib/types";

/** What an agent step that never reported back is recorded as. */
export const INTERRUPTED =
  "Interrupted: this step never reported back (the run stopped, or SolidWorks or the document was closed or reset while it ran). " +
  "Its effect on the model is unknown - check the model before relying on it.";

const blocksOf = (m: MessageParam): ContentBlockParam[] => (typeof m.content === "string" ? [{ type: "text", text: m.content }] : m.content);

/**
 * Make a conversation valid for the API before it is sent. Every tool_use must be answered by a tool_result at the
 * start of the very next message - a run cut off mid-step (Stop, a crash, the document closed under it) leaves one
 * unanswered, and from then on every request in that chat fails with a 400. Unanswered steps get an INTERRUPTED
 * result; results that answer nothing are dropped; neighbouring messages of the same role are merged. Pure: the stored
 * chat isn't changed, and the same history always repairs the same way (so the prompt cache still holds).
 */
export function repairHistory(messages: MessageParam[]): MessageParam[] {
  const out: MessageParam[] = [];
  const push = (m: MessageParam) => {
    const blocks = blocksOf(m);
    if (blocks.length === 0) return;
    const last = out[out.length - 1];
    if (last && last.role === m.role) {
      // Same role twice: merge, keeping tool results first in a user message.
      const all = [...blocksOf(last), ...blocks];
      const merged =
        m.role === "user" ? [...all.filter((b) => b.type === "tool_result"), ...all.filter((b) => b.type !== "tool_result")] : all;
      out[out.length - 1] = { role: m.role, content: merged };
    } else {
      out.push({ role: m.role, content: blocks });
    }
  };

  for (let i = 0; i < messages.length; i++) {
    const m = messages[i];
    if (m.role === "assistant") {
      push(i === messages.length - 1 ? m : withoutOpenServerCalls(m));
      const ids = blocksOf(m).filter((b) => b.type === "tool_use").map((b) => (b as { id: string }).id);
      if (ids.length === 0) continue;
      const next = messages[i + 1];
      const nextBlocks = next && next.role === "user" ? blocksOf(next) : [];
      const results: ContentBlockParam[] = ids.map(
        (id) =>
          nextBlocks.find((b) => b.type === "tool_result" && (b as { tool_use_id: string }).tool_use_id === id) ??
          ({ type: "tool_result", tool_use_id: id, is_error: true, content: INTERRUPTED } as ContentBlockParam),
      );
      // Results for these steps first, then whatever else the next message said (dropping results for other steps).
      const rest = nextBlocks.filter((b) => b.type !== "tool_result");
      out.push({ role: "user", content: [...results, ...rest] });
      if (next && next.role === "user") i++; // consumed
    } else {
      // A user message not right after a tool_use can't carry tool results.
      push({ role: "user", content: blocksOf(m).filter((b) => b.type !== "tool_result") });
    }
  }
  // The API wants the conversation to start with the user.
  while (out.length && out[0].role !== "user") out.shift();
  return out;
}

/**
 * A web search or fetch (server_tool_use) whose result isn't in the same message was cut off (a paused turn the user
 * stopped): the API only resumes one when it is the last message, so anywhere else it is dropped.
 */
function withoutOpenServerCalls(m: MessageParam): MessageParam {
  if (typeof m.content === "string" || !m.content.some((b) => b.type === "server_tool_use")) return m;
  const answered = new Set(m.content.filter((b) => b.type !== "tool_result" && "tool_use_id" in b).map((b) => (b as { tool_use_id: string }).tool_use_id));
  return { ...m, content: m.content.filter((b) => b.type !== "server_tool_use" || answered.has(b.id)) };
}

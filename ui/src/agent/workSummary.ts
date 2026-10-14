import type { ToolCall } from "../components/ToolCard";

/**
 * Which tool calls are worth showing once a turn is finished. Reads, searches, captures, rebuild checks and failed
 * attempts are how the agent got there; what changed the model (or saved something) is what happened.
 */

/** Tools that never change anything the user cares about seeing afterwards. */
const MINOR_TOOLS = new Set([
  "get_model_context",
  "get_feature_tree",
  "get_selection",
  "list_open_documents",
  "search_sw_api",
  "get_sw_api_type",
  "capture_views",
  "describe_model_history",
  "list_user_tools",
  "read_user_tool",
  "design_library",
  "select_refs",
  "rebuild",
  "undo",
]);

/** Titles of C# cells that only look at the model (for calls made before the `kind` field existed). */
const CHECK_TITLE = /^(check|verify|validate|inspect|list|find|get|read|measure|look|query|probe|debug|diagnose|examine|confirm|test|count|show|print|dump|report|search|compare|re-?check|investigate|locate|identify|explore|review|analy[sz]e|evaluate|determine)\b/i;

export function isHighlight(call: ToolCall): boolean {
  if (call.name === "ask_user" || call.name === "ask_form") return true;
  if (!call.result || call.result.is_error) return false;
  if (MINOR_TOOLS.has(call.name)) return false;
  if (call.name === "execute_csharp") {
    const kind = String(call.input?.kind ?? "").toLowerCase();
    if (kind === "check") return false;
    if (kind === "change") return true;
    return !CHECK_TITLE.test(String(call.input?.title ?? "").trim());
  }
  return true; // save_user_tool, edit_user_tool, add_to_design_library, and anything new
}

/**
 * The highlights of a finished turn, in order. A step retried under the same title is shown once (the last
 * successful attempt).
 */
export function highlights(calls: ToolCall[]): ToolCall[] {
  const keep = calls.filter(isHighlight);
  const lastByTitle = new Map<string, number>();
  keep.forEach((c, i) => {
    if (c.name === "execute_csharp" && c.input?.title) lastByTitle.set(String(c.input.title).trim().toLowerCase(), i);
  });
  return keep.filter((c, i) => c.name !== "execute_csharp" || !c.input?.title || lastByTitle.get(String(c.input.title).trim().toLowerCase()) === i);
}

import type { DocInfo, MessageParam, ToolRunSticky } from "./types";

/**
 * Long tool runs (front matter long_run, e.g. the compression-mold designer) take minutes to hours, with questions in
 * between, and the user often continues them with a typed follow-up ("continue", "use 4 dowels"). Such a follow-up
 * keeps the tool run's model, effort, step limit and auto-approval - but only while the run is really still open:
 *
 * - its document is the active one (other documents get the chat's own settings, and the chip says it is paused);
 * - the tool hasn't reported that it is done: a result of one of its required agent tools (or mold_record) whose
 *   last `status` is `done` ends it - the mold tool's record turns `done` at its final phase;
 * - it has been active within the last 2 hours (a run started or ended in the chat);
 * - the user hasn't closed the banner chip and no other tool has run in the chat since.
 *
 * Everything else uses the chat's own model and approval setting. Pure, so it is easy to check.
 */

/** API steps per turn: default, a long tool's default and the hard cap (UserTools.cs has the same numbers). */
export const DEFAULT_STEPS = 200;
export const LONG_RUN_STEPS = 600;
export const STEPS_CAP = 1000;

/** A long tool run's follow-up settings lapse after this long without activity in the chat. */
export const STICKY_IDLE_MS = 2 * 60 * 60 * 1000;

/** The step limit for a run: the tool's own (clamped to 1..1000), else the default. */
export function clampSteps(n: number | null | undefined, fallback = DEFAULT_STEPS): number {
  if (typeof n !== "number" || !Number.isFinite(n)) return fallback;
  return Math.min(STEPS_CAP, Math.max(1, Math.round(n)));
}

/** Whether a document is the one the tool ran on: same key, else the same saved path, else (unsaved) the same title. */
export function docMatches(doc: ToolRunSticky["doc"], active: DocInfo | null | undefined): boolean {
  if (!doc) return true; // started with no document: nothing to compare
  if (!active) return false;
  if (active.key === doc.key) return true;
  if (doc.path && active.path) return doc.path.toLowerCase() === active.path.toLowerCase();
  return !doc.path && !active.path && doc.title === active.title;
}

export type StickyState = "active" | "other_doc" | "expired";

export function stickyState(tr: ToolRunSticky, now: number, active: DocInfo | null | undefined): StickyState {
  if (now - (tr.lastActive || tr.startedAt) > STICKY_IDLE_MS) return "expired";
  return docMatches(tr.doc, active) ? "active" : "other_doc";
}

/**
 * A top-level status line: `status: done`, `Record status: done`, `  "status": "done",` (pretty JSON, top level) or a
 * compact JSON object that starts with it. Nested statuses (per stage, per check) are indented deeper or named
 * otherwise ("stage status") and don't count.
 */
const STATUS = /^ {0,2}\{?\s*(?:(?:record|mold|design|run)\s+)?["']?status["']?\s*[:=]\s*["']?([A-Za-z][\w-]*)/gim;

function resultText(content: unknown): string {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content
    .filter((b) => b && typeof b === "object" && (b as { type?: string }).type === "text")
    .map((b) => (b as { text: string }).text)
    .join("\n");
}

/**
 * The last `status` reported by the tool's own agent tools (its `requires`, plus mold_record) in messages[from..], or
 * null if none reported one.
 */
export function lastReportedStatus(messages: MessageParam[], from: number, tools: string[] | undefined): string | null {
  const names = new Set([...(tools ?? []), "mold_record"]);
  const ids = new Set<string>();
  let status: string | null = null;
  for (const m of messages.slice(Math.max(0, from))) {
    if (typeof m.content === "string") continue;
    for (const b of m.content) {
      if (b.type === "tool_use" && names.has(b.name)) ids.add(b.id);
      else if (b.type === "tool_result" && ids.has(b.tool_use_id) && !b.is_error) {
        for (const match of resultText(b.content).matchAll(STATUS)) status = match[1].toLowerCase();
      }
    }
  }
  return status;
}

/** The tool reported its run finished (its record's status is done). */
export function runReportsDone(messages: MessageParam[], from: number, tools: string[] | undefined): boolean {
  return lastReportedStatus(messages, from, tools) === "done";
}

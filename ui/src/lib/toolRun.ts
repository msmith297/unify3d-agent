import type { DocInfo, MessageParam, ToolRunDoc, ToolRunSticky } from "./types";

/**
 * Long tool runs (front matter long_run, e.g. the compression-mold designer) take minutes to hours, with questions in
 * between, and the user often continues them with a typed follow-up ("continue", "use 4 dowels"). Such a follow-up
 * keeps the tool run's model, effort, step limit and (for a ribbon tool) auto-approval - but only while the run is
 * really still open:
 *
 * - one of its documents is the active one: the one it started on, or one the run itself activated since (other
 *   documents get the chat's own settings, and the chip says it is paused);
 * - the tool hasn't reported that it is done: a write of its record (mold_record, or another of its `requires`
 *   tools) whose result carries `record_status: done` - the mold tool's record turns `done` at its final phase;
 * - it has been active within the last 2 hours (a run started or ended in the chat);
 * - the user hasn't closed the banner chip or changed the chat's model or effort, and no other tool or workflow has
 *   run in the chat since.
 *
 * Everything else uses the chat's own model and approval setting. Pure, so it is easy to check
 * (checks/lib.check.mjs).
 */

/** API steps per turn: default, a long tool's default, and the range (UserTools.cs has the same numbers). */
export const DEFAULT_STEPS = 200;
export const LONG_RUN_STEPS = 600;
export const STEPS_MIN = 50;
export const STEPS_CAP = 1000;

/** A long tool run's follow-up settings lapse after this long without activity in the chat. */
export const STICKY_IDLE_MS = 2 * 60 * 60 * 1000;

/** The step limit for a run: the tool's own (clamped to 50..1000, as ToolSpec and UserTools.MaxStepsOf), else the default. */
export function clampSteps(n: number | null | undefined, fallback = DEFAULT_STEPS): number {
  if (typeof n !== "number" || !Number.isFinite(n)) return fallback;
  return Math.min(STEPS_CAP, Math.max(STEPS_MIN, Math.round(n)));
}

/** Whether a document is one the tool ran on: same key, else the same saved path, else (unsaved) the same title. */
export function docMatches(doc: ToolRunDoc | null | undefined, active: DocInfo | null | undefined): boolean {
  if (!doc || !active) return false; // nothing to compare: never assume it is the run's document
  if (active.key === doc.key) return true;
  if (doc.path && active.path) return doc.path.toLowerCase() === active.path.toLowerCase();
  return !doc.path && !active.path && doc.title === active.title;
}

/** Every document of the run: the one it started on, then the ones it worked in since. */
export function runDocs(tr: ToolRunSticky): ToolRunDoc[] {
  return [...(tr.doc ? [tr.doc] : []), ...(tr.docs ?? [])];
}

/** The run with this document added to the ones it worked in (unchanged if it is already one of them). */
export function withRunDoc(tr: ToolRunSticky, doc: DocInfo): ToolRunSticky {
  if (runDocs(tr).some((d) => docMatches(d, doc))) return tr;
  return { ...tr, docs: [...(tr.docs ?? []), { key: doc.key, title: doc.title, path: doc.path || null }] };
}

export type StickyState = "active" | "other_doc" | "expired";

export function stickyState(tr: ToolRunSticky, now: number, active: DocInfo | null | undefined): StickyState {
  if (now - (tr.lastActive || tr.startedAt) > STICKY_IDLE_MS) return "expired";
  return runDocs(tr).some((d) => docMatches(d, active)) ? "active" : "other_doc";
}

/**
 * The status a write of the record reports. Agreed with Track D (INTEGRATION-NOTES-C.md): every mold_record action
 * that writes (init, merge, ...) prints the record's status on its own line, at the start of the line:
 * `record_status: building`. A line in a result, not a key in JSON, so nested statuses (stages, checks, simulations)
 * can never be mistaken for it, and reads are ignored altogether: a re-run that reads a finished record isn't "done".
 */
const RECORD_STATUS = /^record_status:[ \t]*([A-Za-z][\w-]*)/gim;
/** MoldRecord.Merge's own line ("merged; status done; valid"), in case the tool passes it through unchanged. */
const MERGED_STATUS = /^merged; status ([A-Za-z][\w-]*)/im;
/** Actions that only read: their results never change the status, whatever they print. */
const READ_ACTIONS = new Set(["read", "path", "validate", "get", "list", "summary", "show"]);

function resultText(content: unknown): string {
  if (typeof content === "string") return content;
  if (!Array.isArray(content)) return "";
  return content
    .filter((b) => b && typeof b === "object" && (b as { type?: string }).type === "text")
    .map((b) => (b as { text: string }).text)
    .join("\n");
}

function inputObject(input: unknown): Record<string, unknown> {
  return input && typeof input === "object" && !Array.isArray(input) ? (input as Record<string, unknown>) : {};
}

/** The record status a merge's input sets ({action: "merge", data: {status: "done"}}, data possibly a JSON string). */
function mergedStatusInput(input: Record<string, unknown>): string | null {
  let data = input.data ?? input.patch;
  if (typeof data === "string") {
    try {
      data = JSON.parse(data);
    } catch {
      return null;
    }
  }
  const st = inputObject(data).status;
  return typeof st === "string" && /^[A-Za-z][\w-]*$/.test(st) ? st : null;
}

/**
 * The last record status the tool's own writes reported in messages[from..] (its `requires` tools and mold_record), or
 * null if none did. Reads, error results and every other tool's output are ignored.
 */
export function lastReportedStatus(messages: MessageParam[], from: number, tools: string[] | undefined): string | null {
  const names = new Set([...(tools ?? []), "mold_record"]);
  const writes = new Map<string, { name: string; input: Record<string, unknown> }>();
  let status: string | null = null;
  for (const m of messages.slice(Math.max(0, from))) {
    if (typeof m.content === "string") continue;
    for (const b of m.content) {
      if (b.type === "tool_use" && names.has(b.name)) {
        const input = inputObject(b.input);
        const action = typeof input.action === "string" ? input.action.trim().toLowerCase() : "";
        // No action: the record tools' default is a read.
        if (action && !READ_ACTIONS.has(action)) writes.set(b.id, { name: b.name, input: { ...input, action } });
      } else if (b.type === "tool_result" && !b.is_error) {
        const w = writes.get(b.tool_use_id);
        if (!w) continue;
        const text = resultText(b.content);
        let found: string | null = null;
        for (const match of text.matchAll(RECORD_STATUS)) found = match[1];
        if (!found && w.name === "mold_record") {
          found = MERGED_STATUS.exec(text)?.[1] ?? (w.input.action === "merge" ? mergedStatusInput(w.input) : null);
        }
        if (found) status = found.toLowerCase();
      }
    }
  }
  return status;
}

/** The tool reported its run finished (its record's status is done). */
export function runReportsDone(messages: MessageParam[], from: number, tools: string[] | undefined): boolean {
  return lastReportedStatus(messages, from, tools) === "done";
}

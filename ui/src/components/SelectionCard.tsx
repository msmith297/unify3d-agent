import { useState } from "react";
import { IconSelect, Spinner } from "./SwIcons";
import { Answered } from "./Answered";
import { EMPTY, useStore, type RunState } from "../store";
import { isAllowed, kindsText, selectionStatus } from "../lib/selection";
import type { SelectionAsk } from "../lib/types";
import type { ToolCall } from "./ToolCard";

/**
 * The agent asks the user to pick entities in SolidWorks (ask_selection): the request, what is selected right now,
 * an optional note, Done and Skip. Done snapshots the selection and hands it back as [S#] references. Typing in the
 * chat box or speaking while the card is open also counts as Done, with the words as the note.
 */
export function SelectionCard({ call, threadId, run }: { call: ToolCall; threadId: string; run: RunState | undefined }) {
  const input = (call.input ?? { prompt: "" }) as SelectionAsk;
  const pending = run?.question?.toolUseId === call.id && !!run.question.selection;
  const ask = pending ? { ...input, ...run!.question!.selection } : input;
  const answered = typeof call.result?.content === "string" ? call.result.content : undefined;

  if (!pending) {
    const ids = answered?.match(/\[S\d+\]/g) ?? [];
    const summary =
      answered === undefined ? null
      : /pressed Skip/.test(answered) ? "Skipped"
      : /pressed Stop/.test(answered) ? "Stopped"
      : ids.length ? `${ids.length} selected: ${ids.slice(0, 4).join(" ")}${ids.length > 4 ? " …" : ""}`
      : "Nothing selected";
    return (
      <Answered question={input.prompt ?? ""} answer={summary}>
        <div className="selectable text-[12px] text-text-2">{input.prompt}</div>
        {answered && <pre className="selectable mt-1 whitespace-pre-wrap font-[inherit] text-[11.5px] text-text-3">{answered}</pre>}
      </Answered>
    );
  }
  return <Pending ask={ask} threadId={threadId} />;
}

function Pending({ ask, threadId }: { ask: SelectionAsk; threadId: string }) {
  const answerSelection = useStore((s) => s.answerSelection);
  const note = useStore((s) => s.answerDrafts[threadId] ?? "");
  const setNote = (v: string) => useStore.getState().setAnswerDraft(threadId, v);
  // The live selection of the active document (the panel's chips): what Done will hand back, roughly.
  const live = useStore((s) => s.selection[s.activeDocKey ?? ""] ?? EMPTY);
  // The chips stop at 30; the add-in also reports how many are selected in all.
  const total = useStore((s) => s.selectionTotal[s.activeDocKey ?? ""] ?? 0);
  const hasDoc = useStore((s) => !!s.docs[s.activeDocKey ?? ""]);
  const [busy, setBusy] = useState(false);
  const matching = live.filter((it) => isAllowed(it.type, ask));
  const status = selectionStatus(matching.length, ask, { more: total > live.length });

  const finish = async (done: boolean) => {
    setBusy(true);
    try {
      await answerSelection(threadId, done, note);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="sw-rollout !border-accent px-2 py-1.5">
      <div className="mb-1 flex items-start gap-2">
        <span className="mt-px shrink-0">
          <IconSelect size={15} />
        </span>
        <div className="selectable text-[12px] font-semibold text-text">{ask.prompt}</div>
      </div>
      <div className="flex flex-col gap-1.5 pl-[22px]">
        <div className="text-[11.5px] text-text-2">
          {hasDoc ? `Select ${kindsText(ask.types)} in SolidWorks (Ctrl+click for more), then press Done.` : "Open the document in SolidWorks and select there, then press Done."}
        </div>
        <div className={`text-[11.5px] ${status.ok ? "text-text-3" : "text-warn"}`} aria-live="polite">
          {status.text}
        </div>
        {ask.unknownTypes?.length ? (
          <div className="text-[11px] text-text-3">The agent asked for {ask.unknownTypes.join(", ")}, which isn't a selection type here - {ask.types?.length ? "ignored" : "any entity counts"}.</div>
        ) : null}
        {matching.length > 0 && (
          <div className="flex flex-wrap gap-1">
            {matching.slice(0, 8).map((it, i) => (
              <span key={i} className="max-w-[160px] truncate rounded-[3px] border border-line bg-card-2 px-1.5 py-px text-[11px] text-text-2" title={`${it.type}${it.label ? ` of ${it.label}` : ""}`}>
                {it.type}
                {it.label ? ` · ${it.label}` : ""}
              </span>
            ))}
            {matching.length > 8 && <span className="self-center text-[11px] text-text-3">+{matching.length - 8} more</span>}
          </div>
        )}
        <form
          className="flex gap-1.5"
          onSubmit={(e) => {
            e.preventDefault();
            if (status.ok && !busy) void finish(true);
          }}
        >
          <input
            value={note}
            onChange={(e) => setNote(e.target.value)}
            placeholder="Add a note (optional) - or just talk"
            className="sw-input h-[22px] min-w-0 flex-1 px-1.5 text-[12px]"
          />
          <button type="button" className="sw-btn" disabled={busy} onClick={() => void finish(false)} title="Continue without picking anything">
            Skip
          </button>
          <button type="submit" className="sw-btn sw-btn-primary" disabled={busy || !status.ok} title="Hand the selection to the agent">
            {busy ? <Spinner size={11} /> : null}
            Done
          </button>
        </form>
      </div>
    </div>
  );
}

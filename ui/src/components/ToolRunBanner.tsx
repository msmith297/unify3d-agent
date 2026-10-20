import { useEffect, useState } from "react";
import { X } from "lucide-react";
import { IconError, IconOk, IconToolbox, Spinner } from "./SwIcons";
import { currentThread, useStore, type RunState } from "../store";
import { stickyState } from "../lib/toolRun";
import { EFFORTS, modelOption } from "../lib/models";

/**
 * A long tool run that is still open in this chat (lib/toolRun.ts): follow-ups typed here keep the tool's model,
 * effort, step limit and auto-approval. Shows that - or that it is paused while another document is active - with an
 * X to end it, so auto-approval never carries on unseen.
 */
function LongRunChip() {
  const thread = useStore(currentThread);
  const tr = thread?.toolRun ?? null;
  const active = useStore((s) => s.docs[s.activeDocKey ?? ""]);
  const endToolRun = useStore((s) => s.endToolRun);
  if (!thread || !tr) return null;
  const state = stickyState(tr, Date.now(), active);
  if (state === "expired") return null;
  const effort = EFFORTS.find((e) => e.id === tr.effort)?.label.toLowerCase() ?? tr.effort;
  return (
    <div className="flex shrink-0 items-center gap-2 border-b border-line bg-card px-2 py-1 text-[11.5px] shadow-[inset_0_1px_0_var(--bevel-hi)]">
      <IconToolbox size={13} />
      <span
        className="min-w-0 flex-1 truncate text-text-2"
        title={
          state === "active"
            ? "Messages you send in this chat continue the tool run with its own settings and without asking before running code. " +
              "This ends when the tool reports it is done, after 2 hours without activity, when another tool runs here, or when you close it."
            : "Follow-ups use this chat's own model and approval while another document is active. Switch back to continue the tool run."
        }
      >
        <span className="font-semibold text-text">{tr.label}</span>
        {state === "active" ? (
          <> - follow-ups continue it: {modelOption(tr.model).label} · {effort} effort · auto-approve · {tr.maxSteps} steps</>
        ) : (
          <> - paused: {tr.doc?.title ?? "its document"} isn't the active document</>
        )}
      </span>
      <button className="text-text-3 hover:text-text" onClick={() => endToolRun(thread.id)} title="End the tool run: follow-ups use this chat's own model and approval">
        <X size={12} />
      </button>
    </div>
  );
}

/**
 * While a ribbon tool runs with the agent, the chat panel shows one quiet line - "Smart fillet executing..." with
 * elapsed time and steps - instead of streaming the work. Afterwards it shows the one-line outcome for a moment.
 * Click it to see the details in the chat the tool ran in.
 */
export function ToolRunBanner() {
  const run = useStore((s) => Object.values(s.runs).find((r) => r.label));
  const outcome = useStore((s) => s.toolOutcome);
  const [, tick] = useState(0);

  useEffect(() => {
    if (!run && !outcome) return;
    const t = setInterval(() => tick((n) => n + 1), 500);
    return () => clearInterval(t);
  }, [run, outcome]);

  useEffect(() => {
    if (!outcome || run) return;
    const t = setTimeout(() => useStore.setState({ toolOutcome: null }), 9000);
    return () => clearTimeout(t);
  }, [outcome, run]);

  const open = (docKey: string, threadId: string) => {
    const s = useStore.getState();
    s.showDoc(docKey);
    s.selectThread(threadId);
    useStore.setState({ panel: "chat" });
  };

  if (run) return <Running run={run} onOpen={() => open(run.docKey, run.threadId)} />;
  if (!outcome) return <LongRunChip />;
  return (
    <div className="flex shrink-0 items-center gap-2 border-b border-line bg-card px-2 py-1.5 text-[11.5px] shadow-[inset_0_1px_0_var(--bevel-hi)]">
      {outcome.ok ? <IconOk size={12} /> : <IconError size={13} />}
      <button className="min-w-0 flex-1 truncate text-left text-text hover:underline" onClick={() => open(outcome.docKey, outcome.threadId)} title="Show the details">
        <span className="font-semibold">{outcome.label}</span>
        {outcome.text && <span className="text-text-2"> - {outcome.text}</span>}
      </button>
      <button className="text-text-3 hover:text-text" onClick={() => useStore.setState({ toolOutcome: null })} title="Dismiss">
        <X size={12} />
      </button>
    </div>
  );
}

function Running({ run, onOpen }: { run: RunState; onOpen: () => void }) {
  const stop = useStore((s) => s.stop);
  const steps = useStore((s) => {
    const t = s.chats[run.docKey]?.data.threads.find((x) => x.id === run.threadId);
    if (!t) return 0;
    let n = 0;
    for (let i = t.turns[t.turns.length - 1]?.msgIndex ?? 0; i < t.messages.length; i++) {
      const m = t.messages[i];
      if (m.role === "assistant" && typeof m.content !== "string") n += m.content.filter((b) => b.type === "tool_use").length;
    }
    return n;
  });
  const seconds = Math.max(0, Math.round((Date.now() - (run.startedAt ?? Date.now())) / 1000));
  return (
    <div className="flex shrink-0 items-center gap-2 border-b border-line bg-accent-soft px-2 py-1.5 text-[11.5px] shadow-[inset_0_1px_0_var(--bevel-hi)]">
      <IconToolbox size={14} />
      <button className="min-w-0 flex-1 truncate text-left text-text" onClick={onOpen} title="Show what the tool is doing">
        <span className="font-semibold">{run.label}</span>
        <span className="shimmer"> executing</span>
        <span className="working-dots">
          <span>.</span>
          <span>.</span>
          <span>.</span>
        </span>
        <span className="text-text-3">
          {" "}
          {seconds}s{steps > 0 ? ` · step ${steps}` : ""}
        </span>
      </button>
      <Spinner size={12} />
      <button className="sw-btn !h-[20px] !px-1.5" onClick={() => stop(run.threadId)} title="Stop the tool">
        Stop
      </button>
    </div>
  );
}

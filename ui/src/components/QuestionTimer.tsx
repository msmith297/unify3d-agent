import { useEffect, useState } from "react";
import { useStore, type RunState } from "../store";

/**
 * Under a pending question: how long until the agent goes on without an answer (Settings > questionTimeoutSec), and a
 * "Keep waiting" button that turns the timeout off for this question. Paused while the user is typing an answer.
 */
export function QuestionTimer({ threadId, run }: { threadId: string; run: RunState | undefined }) {
  const deadline = run?.question?.deadline;
  const typing = useStore((s) => !!(s.answerDrafts[threadId] ?? "").trim());
  const keepWaiting = useStore((s) => s.keepWaiting);
  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    if (deadline === undefined) return;
    const t = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(t);
  }, [deadline]);
  if (deadline === undefined) return null;
  const left = Math.max(0, Math.ceil((deadline - nowMs) / 1000));
  const clock = `${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")}`;
  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[11px] text-text-3">
      <span>{typing ? "Waiting while you type." : `No answer in ${clock}: the agent decides on its own.`}</span>
      <button type="button" onClick={() => keepWaiting(threadId)} className="underline hover:text-text-2">
        Keep waiting
      </button>
    </div>
  );
}

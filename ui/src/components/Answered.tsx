import { useState, type ReactNode } from "react";
import { Caret, IconOk } from "./SwIcons";

/**
 * A question or questionnaire the user has already dealt with: one muted line with what they chose, so it never looks
 * like it's still waiting for them. Click to see the full question again.
 */
export function Answered({ question, answer, children }: { question: string; answer: string | null; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="rounded-[3px] border border-line/60">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center gap-1.5 px-2 py-1 text-left text-[11.5px] text-text-3 hover:text-text-2"
        title={open ? "Hide the question" : question}
      >
        <Caret open={open} className="shrink-0" />
        {answer === null ? <span className="shrink-0">Not answered:</span> : <IconOk size={11} />}
        <span className="min-w-0 flex-1 truncate">
          {answer === null ? question : <><span className="text-text-2">{answer}</span><span> - {question}</span></>}
        </span>
      </button>
      {open && <div className="border-t border-line/60 px-2 py-1.5">{children}</div>}
    </div>
  );
}

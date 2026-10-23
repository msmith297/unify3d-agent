import { useState } from "react";
import { IconBook, IconUndo } from "./SwIcons";
import { useStore } from "../store";
import type { RememberedChip } from "../lib/memory";

/**
 * "Remembered" under the agent's reply when it saved a standing instruction (remember): where it went and what, with Undo.
 * Stays visible when the turn's steps fold.
 */
export function MemoryChip({ chip, threadId, undone }: { chip: RememberedChip; threadId: string; undone: boolean }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <div className={`flex items-start gap-1.5 self-start rounded-[3px] border border-line px-2 py-1 text-[11.5px] ${undone ? "opacity-60" : ""}`}>
      <span className="mt-px shrink-0 text-text-3">
        <IconBook size={12} />
      </span>
      <span className="min-w-0 flex-1">
        <span className="font-semibold text-text">{undone ? "Undone" : "Remembered"}</span>
        <span className="text-text-3"> · {chip.where}</span>
        <span className={`block text-text-2 ${undone ? "line-through" : ""}`}>{chip.text}</span>
        {error && <span className="block text-err">{error}</span>}
      </span>
      {!undone && (
        <button
          type="button"
          className="sw-btn !h-[20px] shrink-0 !px-1.5"
          disabled={busy}
          title="Forget this instruction"
          onClick={async () => {
            setBusy(true);
            setError(null);
            try {
              await useStore.getState().undoMemory(threadId, chip.id);
            } catch (e) {
              setError(`Couldn't undo: ${e instanceof Error ? e.message : String(e)}`);
            } finally {
              setBusy(false);
            }
          }}
        >
          <IconUndo size={11} /> Undo
        </button>
      )}
    </div>
  );
}

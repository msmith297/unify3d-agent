import { useEffect, useMemo, useState, type ReactElement, type ReactNode } from "react";
import { IconBook } from "./SwIcons";
import { useStore } from "../store";
import { on, rpc } from "../lib/bridge";
import type { MemoryIndex, MemoryNote, MemoryScopeInfo } from "../lib/memory";

type RolloutProps = { title: string; icon?: ReactNode; children: ReactNode };
type ToggleProps = { checked: boolean; onChange: (v: boolean) => void; label: string; note?: string };

/**
 * Options > "What the agent remembers" (docs/agent-memory.md section 3): the general memory and the notes on tools and
 * skills, each editable and deletable, the switch to stop saving, and the built-in or plugin tools hidden in favour of
 * the user's own copies.
 */
export function MemorySettings({ Rollout, Toggle }: { Rollout: (p: RolloutProps) => ReactElement; Toggle: (p: ToggleProps) => ReactElement }) {
  const saving = useStore((s) => s.settings.saveMemory !== false);
  const tools = useStore((s) => s.userTools);
  const hidden = useMemo(() => (tools ?? []).filter((t) => t.hidden), [tools]);
  const [scopes, setScopes] = useState<MemoryScopeInfo[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const load = () =>
      rpc<MemoryScopeInfo[]>("memory.list")
        .then((list) => setScopes(list ?? []))
        .catch((e) => setError(e instanceof Error ? e.message : String(e)));
    void load();
    return on("memory.changed", () => void load());
  }, []);

  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };
  const notes = (scopes ?? []).filter((s) => s.notes.length > 0);

  return (
    <Rollout title="What the agent remembers" icon={<IconBook size={14} />}>
      <Toggle
        checked={saving}
        onChange={async (v) => {
          await useStore.getState().saveSettings({ saveMemory: v });
          // the system prompt says whether saving is on
          const memory = await rpc<MemoryIndex | null>("memory.index").catch(() => null);
          if (memory) useStore.setState({ memory });
        }}
        label="Save what I tell it to keep doing"
        note={'When you say "always", "never" or "from now on", the agent saves it to the right tool, skill or its general memory.'}
      />
      {scopes === null && !error && <p className="py-1 text-[11px] text-text-3">Loading…</p>}
      {scopes !== null && notes.length === 0 && <p className="py-1 text-[11px] text-text-3">Nothing saved yet.</p>}
      {notes.map((s) => (
        <div key={s.scope} className="mt-1.5">
          <div className="flex items-baseline gap-1 text-[11.5px]">
            <span className="flex-1 font-semibold text-text">{s.label.charAt(0).toUpperCase() + s.label.slice(1)}</span>
            {s.kind === "general" && (
              <span className="text-[10.5px] text-text-3" title="General memory goes into every request">
                {Math.round((100 * s.used) / s.cap)}% full
              </span>
            )}
          </div>
          {s.notes.map((n) => (
            <NoteRow key={n.id} note={n} onSave={(text) => act(() => rpc("memory.edit", { id: n.id, text }))} onDelete={() => act(() => rpc("memory.forget", { id: n.id }))} />
          ))}
        </div>
      ))}
      {hidden.length > 0 && (
        <div className="mt-2">
          <div className="text-[11.5px] font-semibold text-text">Hidden tools</div>
          <p className="text-[11px] text-text-3">Kept off the ribbon and the Tools menu because you use your own copy. The ribbon changes after a restart.</p>
          {hidden.map((t) => (
            <div key={t.name} className="flex items-center gap-1.5 py-0.5 text-[12px]">
              <span className="min-w-0 flex-1 truncate text-text">{t.title}</span>
              <button type="button" className="sw-btn !h-[20px] !px-1.5" onClick={() => act(() => rpc("userTools.setHidden", { name: t.name, hidden: false }))}>
                Show
              </button>
            </div>
          ))}
        </div>
      )}
      {error && <p className="mt-1 text-[11px] text-err">{error}</p>}
    </Rollout>
  );
}

function NoteRow({ note, onSave, onDelete }: { note: MemoryNote; onSave: (text: string) => Promise<void>; onDelete: () => Promise<void> }) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(note.text);
  if (editing)
    return (
      <form
        className="flex flex-col gap-1 py-0.5"
        onSubmit={async (e) => {
          e.preventDefault();
          if (text.trim() && text.trim() !== note.text) await onSave(text.trim());
          setEditing(false);
        }}
      >
        <textarea value={text} onChange={(e) => setText(e.target.value)} rows={2} maxLength={500} className="sw-input min-w-0 px-1.5 py-1 text-[12px]" autoFocus />
        <div className="flex justify-end gap-1">
          <button type="button" className="sw-btn !h-[20px] !px-1.5" onClick={() => (setText(note.text), setEditing(false))}>
            Cancel
          </button>
          <button type="submit" className="sw-btn sw-btn-primary !h-[20px] !px-1.5" disabled={!text.trim()}>
            Save
          </button>
        </div>
      </form>
    );
  return (
    <div className="group flex items-start gap-1.5 py-0.5 text-[12px]">
      <span className="min-w-0 flex-1 text-text-2">
        {note.text}
        {note.date && <span className="text-[10.5px] text-text-3"> · {note.date}</span>}
      </span>
      <button type="button" className="sw-btn !h-[20px] shrink-0 !px-1.5" title="Change this instruction" onClick={() => (setText(note.text), setEditing(true))}>
        Edit
      </button>
      <button type="button" className="sw-btn !h-[20px] shrink-0 !px-1.5" title="Forget this instruction" onClick={() => void onDelete()}>
        Delete
      </button>
    </div>
  );
}

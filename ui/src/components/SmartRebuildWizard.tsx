import { useEffect, useRef, useState, type ReactNode } from "react";
import { X } from "lucide-react";
import { Caret, IconAttach, IconCancel, IconModelFile, IconRebuild, IconSend } from "./SwIcons";
import { useStore, type SmartRebuildSpec } from "../store";
import { rpc } from "../lib/bridge";
import type { FileEntry } from "../lib/types";

/**
 * Smart Rebuild (a built-in Unify3D tool): rebuild the whole design around a change far back in its history. This page
 * collects what changed, whether to work on a copy, how big decisions are made and any references, then hands the
 * agent a brief; the procedure itself is in the system prompt.
 */
const CHANGES: { id: SmartRebuildSpec["change"]; label: string; hint: string }[] = [
  { id: "replace", label: "New customer part", hint: "Swap the inserted part for a revised file" },
  { id: "describe", label: "Describe the change", hint: "The agent makes it, then rebuilds" },
  { id: "manual", label: "I'll make it", hint: "The agent records the design, you change it, it rebuilds" },
  { id: "done", label: "Already made", hint: "You changed it but haven't saved since" },
];

const DECISIONS: { id: SmartRebuildSpec["decisions"]; label: string; hint: string }[] = [
  { id: "balanced", label: "Ask on big ones", hint: "Decides routine fixes, asks before anything that changes function, cost or other parts" },
  { id: "ask", label: "Ask about all", hint: "Asks before every judgement call" },
  { id: "auto", label: "Decide everything", hint: "Makes the engineering calls itself and reports them" },
];

export function SmartRebuildWizard() {
  const doc = useStore((s) => s.docs[s.viewKey]);
  const [change, setChange] = useState<SmartRebuildSpec["change"]>("replace");
  const [newPart, setNewPart] = useState<FileEntry | null>(null);
  const [notes, setNotes] = useState("");
  const [files, setFiles] = useState<FileEntry[]>([]);
  const [copy, setCopy] = useState(true);
  const [copyName, setCopyName] = useState(() => `${(doc?.title ?? "Design").replace(/\.[^.]+$/, "")} - rebuilt`);
  const [decisions, setDecisions] = useState<SmartRebuildSpec["decisions"]>("balanced");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const ref = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    if (change === "describe") ref.current?.focus();
  }, [change]);

  const close = () => useStore.setState({ panel: "chat" });
  const isPart = doc?.type === "part";
  const ready = !busy && isPart && (change !== "replace" || !!newPart) && (change !== "describe" || notes.trim().length > 0) && (!copy || copyName.trim().length > 0);

  const pick = async (one: boolean) => {
    const picked = await rpc<FileEntry[]>("files.pick").catch(() => [] as FileEntry[]);
    if (!picked?.length) return;
    if (one) setNewPart(picked[0]);
    else setFiles((f) => [...f, ...picked.filter((p) => !f.some((x) => x.path === p.path))]);
  };

  const start = async () => {
    if (!ready) return;
    setBusy(true);
    setError(null);
    const problem = await useStore.getState().startSmartRebuild({ change, newPart, notes, files, copy, copyName: copyName.trim(), decisions });
    if (problem) {
      setError(problem);
      setBusy(false);
    }
  };

  return (
    <div
      className="min-h-0 flex-1 overflow-y-auto bg-bg px-1.5 py-1.5"
      onKeyDown={(e) => {
        if (e.key === "Escape") close();
        if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) void start();
      }}
    >
      <div className="zoom-content mx-auto flex max-w-[520px] flex-col gap-1.5">
        <div className="flex items-center gap-1.5 px-1 pb-0.5">
          <IconRebuild size={18} />
          <span className="flex-1 text-[13px] font-semibold text-text">Smart Rebuild</span>
          <button onClick={close} className="text-text-3 hover:text-text" title="Cancel (Esc)">
            <X size={15} />
          </button>
        </div>
        <p className="px-1 text-[11.5px] leading-snug text-text-2">
          Rebuilds the whole design around a change far back in its history - as if every feature had updated perfectly. The agent records
          what each feature means first, then repairs everything the change broke, in order.
        </p>
        {!isPart && <p className="px-1 text-[11.5px] text-err">Open the part (a multi-body part such as a mold) to rebuild.</p>}

        <Rollout title="What changed?">
          <div className="grid grid-cols-2 gap-1">
            {CHANGES.map((c) => (
              <button
                key={c.id}
                onClick={() => setChange(c.id)}
                className={`flex flex-col items-start rounded-[3px] border px-1.5 py-1 text-left ${
                  change === c.id ? "border-accent bg-accent-soft" : "border-line bg-card-2 hover:border-line-strong"
                }`}
              >
                <span className="text-[11.5px] font-semibold text-text">{c.label}</span>
                <span className="text-[10.5px] leading-snug text-text-3">{c.hint}</span>
              </button>
            ))}
          </div>
          {change === "replace" && (
            <div className="mt-1.5 flex items-center gap-1.5">
              {newPart ? (
                <span className="flex min-w-0 flex-1 items-center gap-1 text-[12px] text-text" title={newPart.path}>
                  <IconModelFile size={13} /> <span className="truncate">{newPart.name}</span>
                </span>
              ) : (
                <span className="flex-1 text-[11.5px] text-text-3">Choose the revised customer part (SLDPRT, STEP, ...)</span>
              )}
              <button className="sw-btn" onClick={() => pick(true)}>
                {newPart ? "Change…" : "Choose file…"}
              </button>
            </div>
          )}
          {change === "manual" && (
            <p className="mt-1.5 text-[11px] text-text-2">The agent records the design first, then asks you to make the change (roll back, edit, roll forward) and press Done.</p>
          )}
          {change === "done" && (
            <p className="mt-1.5 text-[11px] text-text-2">The agent reads the last saved version of the file as the "before". Don't save before starting.</p>
          )}
          <textarea
            ref={ref}
            rows={3}
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            placeholder={
              change === "describe"
                ? "e.g. Make the cavity 2 mm deeper and move the gate to the other end…"
                : "Anything the agent should know: what changed in the new part, what must stay, what can move… (optional)"
            }
            className="sw-input mt-1.5 block w-full resize-none px-2 py-1 text-[12px] leading-[1.42]"
          />
        </Rollout>

        <Rollout title="Where">
          <label className="flex cursor-pointer items-center gap-1.5 text-[12px] text-text">
            <input type="checkbox" checked={copy} onChange={(e) => setCopy(e.target.checked)} />
            Work on a copy (Save As first - the original file stays as it is)
          </label>
          {copy && (
            <input value={copyName} onChange={(e) => setCopyName(e.target.value)} className="sw-input mt-1 h-[22px] w-full px-1.5 text-[12px]" placeholder="Name of the copy" />
          )}
        </Rollout>

        <Rollout title="Big decisions">
          <div className="flex flex-col gap-0.5">
            {DECISIONS.map((d) => (
              <label key={d.id} className="flex cursor-pointer items-start gap-1.5 rounded-[2px] px-0.5 py-0.5 hover:bg-card-2">
                <input type="radio" className="mt-[3px]" checked={decisions === d.id} onChange={() => setDecisions(d.id)} />
                <span className="text-[12px] text-text">
                  {d.label} <span className="text-[11px] text-text-3">- {d.hint}</span>
                </span>
              </label>
            ))}
          </div>
        </Rollout>

        <Rollout title="References (optional)" defaultOpen={false}>
          <div className="flex flex-col gap-1">
            {files.map((f) => (
              <div key={f.path} className="flex items-center gap-1.5 text-[11.5px] text-text-2" title={f.path}>
                <IconAttach size={12} /> <span className="min-w-0 flex-1 truncate">{f.name}</span>
                <button className="sw-tool !h-5 !min-w-5" onClick={() => setFiles(files.filter((x) => x.path !== f.path))} title="Remove">
                  <IconCancel size={10} />
                </button>
              </div>
            ))}
            <button className="sw-btn self-start" onClick={() => pick(false)}>
              <IconAttach size={12} /> Add drawings, images, PDFs, models…
            </button>
          </div>
        </Rollout>

        <div className="flex items-center gap-1.5 px-1 pt-0.5">
          <span className="flex-1 text-[11px] text-text-3">{error ? <span className="text-err">{error}</span> : "Runs on Opus at high effort. Large designs take a while; you can keep watching."}</span>
          <button className="sw-btn" onClick={close}>
            Cancel
          </button>
          <button className="sw-btn sw-btn-primary" disabled={!ready} onClick={start} title="Start (Ctrl+Enter)">
            <IconSend size={13} /> {busy ? "Starting…" : "Start rebuild"}
          </button>
        </div>
      </div>
    </div>
  );
}

function Rollout({ title, children, defaultOpen = true }: { title: string; children: ReactNode; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <section className="sw-rollout">
      <button type="button" className="sw-rollout-head" onClick={() => setOpen((o) => !o)} aria-expanded={open}>
        <span className="flex-1 text-[12px]">{title}</span>
        <Caret open={open} className="text-text-3" />
      </button>
      {open && <div className="px-2 py-1.5">{children}</div>}
    </section>
  );
}

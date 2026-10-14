import { useEffect, useRef } from "react";
import { X } from "lucide-react";
import { Dropdown, IconButton } from "./Menu";
import { ToolsPalette } from "./ToolsPalette";
import {
  IconAgent,
  IconCamera,
  IconCancel,
  IconMarkup,
  IconOk,
  IconSelect,
  IconSend,
  IconStop,
  IconToolArrow,
  IconToolCircle,
  IconToolHighlighter,
  IconToolPen,
  IconUndo,
} from "./SwIcons";
import { EMPTY, MARKUP_COLORS, MARKUP_NAME, currentThread, draftKey, isAgentSelection, useStore, type MarkupTool } from "../store";
import { EFFORTS, MODELS } from "../lib/models";
import type { Attachment, Effort } from "../lib/types";

export function Composer() {
  const thread = useStore(currentThread);
  const key = useStore(draftKey);
  const draft = useStore((s) => s.drafts[key] ?? "");
  const attachments = useStore((s) => s.attachments[key] ?? EMPTY);
  const running = useStore((s) => (thread ? !!s.runs[thread.id] : false));
  const settings = useStore((s) => s.settings);
  const hasDoc = useStore((s) => !!s.docs[s.viewKey]);
  const markupActive = useStore((s) => s.markup.active);
  const focusTick = useStore((s) => s.focusTick);
  const { setDraft, send, stop, attachView, addAttachment, removeAttachment, setThreadModel, setThreadEffort, startMarkup } = useStore.getState();
  const ref = useRef<HTMLTextAreaElement>(null);

  const model = thread?.model ?? settings.defaultModel;
  const effort = thread?.effort ?? settings.defaultEffort;
  const canSend = !running && !markupActive && (draft.trim().length > 0 || attachments.length > 0);

  useEffect(() => {
    ref.current?.focus();
    const el = ref.current;
    if (el) el.setSelectionRange(el.value.length, el.value.length);
  }, [focusTick, key]);

  // Auto-grow up to ~8 lines.
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = Math.min(el.scrollHeight, 160) + "px";
  }, [draft]);

  const submit = () => {
    if (canSend) send(draft);
  };

  const onPaste = (e: React.ClipboardEvent) => {
    for (const item of Array.from(e.clipboardData.items)) {
      if (!item.type.startsWith("image/")) continue;
      const file = item.getAsFile();
      if (!file) continue;
      e.preventDefault();
      readImage(file).then((a) => a && addAttachment(a));
    }
  };

  return (
    <div className="sw-etch relative shrink-0 bg-panel px-1.5 pb-1.5 pt-1.5">
      {markupActive ? <MarkupBar /> : hasDoc && <SelectionChips />}

      <div className="sw-input">
        {attachments.length > 0 && (
          <div className="flex flex-wrap gap-1.5 px-1.5 pt-1.5">
            {attachments.map((a) => (
              <div key={a.id} className="group relative overflow-hidden rounded-[2px] border border-line shadow-[var(--shadow)]" title={a.name}>
                <img src={`data:${a.mediaType};base64,${a.data}`} alt={a.name} className="h-12 w-auto" />
                {a.name === MARKUP_NAME && (
                  <span className="absolute bottom-0 left-0 right-0 bg-black/55 px-1 text-[9.5px] text-white">Markup</span>
                )}
                <button
                  onClick={() => removeAttachment(a.id)}
                  title="Remove"
                  className="absolute right-0.5 top-0.5 hidden h-4 w-4 items-center justify-center rounded-full bg-black/60 text-white group-hover:flex"
                >
                  <X size={10} />
                </button>
              </div>
            ))}
          </div>
        )}
        <textarea
          ref={ref}
          rows={2}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onPaste={onPaste}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault();
              submit();
            }
          }}
          placeholder={hasDoc ? "Ask a question or describe a change…" : "Ask anything, or open a document first…"}
          className="block max-h-[160px] min-h-[36px] w-full resize-none bg-transparent px-2 pb-1 pt-1.5 text-[12px] leading-[1.42] text-text outline-none placeholder:text-text-3"
        />
      </div>

      <div className="mt-1 flex items-center gap-0.5">
        <Dropdown title="Model" icon={<IconAgent size={14} />} value={model} items={MODELS.map((m) => ({ id: m.id, label: m.label, note: m.note }))} onChange={setThreadModel} disabled={running} />
        <Dropdown<Effort> title="Effort" value={effort} items={EFFORTS.map((e) => ({ id: e.id, label: e.label, note: e.note }))} onChange={setThreadEffort} disabled={running} />
        <ToolsPalette disabled={running || markupActive} />
        <div className="flex-1" />
        {hasDoc && (
          <>
            <IconButton title="Draw on the view (markup)" active={markupActive} onClick={() => (markupActive ? undefined : startMarkup())}>
              <IconMarkup />
            </IconButton>
            <IconButton title="Attach a screenshot of the current view" onClick={attachView} disabled={markupActive}>
              <IconCamera />
            </IconButton>
          </>
        )}
        {running ? (
          <button type="button" onClick={() => stop()} title="Stop" className="sw-btn !h-6 !px-2">
            <IconStop size={13} /> Stop
          </button>
        ) : (
          <button type="button" onClick={submit} disabled={!canSend} title="Send (Enter)" className="sw-btn sw-btn-primary !h-6 !px-2.5">
            <IconSend size={13} /> Send
          </button>
        )}
      </div>
      {settings.approval !== "ask" && (
        <div className="mt-0.5 px-1 text-[10.5px] text-text-3">
          {settings.approval === "auto" ? "Code runs without asking. Each change is one undo step." : "Read-only: the agent can't change the model."}
        </div>
      )}
    </div>
  );
}

/** One chip per selected entity; click a chip to leave it out of the next message. */
function SelectionChips() {
  const selection = useStore((s) => s.selection[s.viewKey] ?? EMPTY);
  const includeSelection = useStore((s) => s.includeSelection);
  const excluded = useStore((s) => s.excludedSel);
  const agentMade = useStore((s) => isAgentSelection(s, s.viewKey));
  const agentWorking = useStore((s) => Object.values(s.runs).some((r) => r.docKey === s.viewKey));
  const { setIncludeSelection, toggleSelItem } = useStore.getState();
  // Selections the agent makes while working (or leaves behind) aren't the user's - don't offer them.
  if (selection.length === 0 || agentMade || agentWorking) return null;

  const shown = selection.slice(0, 8);
  return (
    <div className="mb-1 flex flex-wrap items-center gap-1 px-0.5">
      <button
        type="button"
        onClick={() => setIncludeSelection(!includeSelection)}
        aria-pressed={includeSelection}
        title={includeSelection ? "The agent sees what you selected. Click to stop sharing the selection." : "Click to share your selection with the agent"}
        className={`sw-tool !h-[20px] !px-1 text-[10.5px] ${includeSelection ? "" : "opacity-60"}`}
      >
        <IconSelect size={13} />
        {includeSelection ? "Selection" : "Selection off"}
      </button>
      {includeSelection &&
        shown.map((item, i) => {
          const off = excluded.includes(i);
          return (
            <button
              key={i}
              type="button"
              onClick={() => toggleSelItem(i)}
              title={off ? "Click to include this in your message" : "Included in your message. Click to leave it out."}
              className={`flex h-[20px] max-w-[170px] items-center gap-1 rounded-[2px] border px-1.5 text-[10.5px] ${
                off ? "border-line text-text-3 line-through opacity-70" : "border-accent/50 bg-accent-soft text-text"
              }`}
            >
              <span className="truncate">
                {pretty(item.type)}
                {item.label ? ` · ${item.label}` : ""}
              </span>
              {!off && <X size={9} className="shrink-0 opacity-60" />}
            </button>
          );
        })}
      {includeSelection && selection.length > shown.length && <span className="text-[10.5px] text-text-3">+{selection.length - shown.length} more</span>}
    </div>
  );
}

const TOOLS: { id: MarkupTool; label: string; icon: React.ReactNode }[] = [
  { id: "pen", label: "Pen", icon: <IconToolPen size={14} /> },
  { id: "arrow", label: "Arrow", icon: <IconToolArrow size={14} /> },
  { id: "circle", label: "Circle", icon: <IconToolCircle size={14} /> },
  { id: "highlighter", label: "Highlighter", icon: <IconToolHighlighter size={14} /> },
];

/** Shown while the markup overlay is active over the SolidWorks view. */
function MarkupBar() {
  const markup = useStore((s) => s.markup);
  const { setMarkup, markupUndo, markupClear, cancelMarkup, finishMarkup } = useStore.getState();
  return (
    <div className="sw-rollout mb-1.5">
      <div className="sw-rollout-head !font-normal">
        <IconMarkup size={14} />
        <span className="flex-1 text-[11.5px] font-semibold">Drawing on the view</span>
        <span className="text-[10.5px] text-text-3">{markup.strokes} mark{markup.strokes === 1 ? "" : "s"}</span>
      </div>
      <div className="flex items-center gap-0.5 px-1 pt-1">
        {TOOLS.map((t) => (
          <button key={t.id} type="button" title={t.label} aria-pressed={markup.tool === t.id} onClick={() => setMarkup({ tool: t.id })} className="sw-tool" style={{ color: markup.color }}>
            {t.icon}
          </button>
        ))}
        <span className="mx-1 h-4 w-px bg-line" />
        {MARKUP_COLORS.map((c) => (
          <button
            key={c}
            type="button"
            title="Colour"
            aria-pressed={markup.color === c}
            onClick={() => setMarkup({ color: c })}
            className="sw-tool !min-w-[20px] !p-0"
          >
            <span className="h-3 w-3 rounded-full border border-black/40" style={{ background: c }} />
          </button>
        ))}
      </div>
      <div className="flex items-center gap-0.5 px-1 pb-1 pt-0.5">
        <button type="button" title="Undo last mark (Ctrl+Z)" onClick={markupUndo} className="sw-tool">
          <IconUndo size={14} />
        </button>
        <button type="button" title="Clear all marks" onClick={markupClear} className="sw-tool text-[11px]">
          Clear
        </button>
        <div className="flex-1" />
        <button type="button" onClick={cancelMarkup} className="sw-btn" title="Discard (Esc)">
          <IconCancel size={10} /> Cancel
        </button>
        <button type="button" onClick={finishMarkup} className="sw-btn" title="Attach the marked-up view to your message (Enter)">
          <IconOk size={11} /> Attach
        </button>
      </div>
    </div>
  );
}

function pretty(type: string) {
  const t = type.toLowerCase().replace(/_/g, " ");
  return t.charAt(0).toUpperCase() + t.slice(1);
}

async function readImage(file: File): Promise<Attachment | null> {
  const mediaType = file.type as Attachment["mediaType"];
  if (!["image/jpeg", "image/png", "image/webp", "image/gif"].includes(mediaType)) return null;
  const data = await new Promise<string>((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result).split(",")[1] ?? "");
    r.onerror = () => reject(r.error);
    r.readAsDataURL(file);
  });
  return { id: crypto.randomUUID(), kind: "image", name: file.name || "Pasted image", mediaType, data };
}

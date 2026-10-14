import { useEffect, useRef, useState } from "react";
import { X } from "lucide-react";
import { Dropdown, IconButton } from "./Menu";
import { ToolsPalette } from "./ToolsPalette";
import {
  IconAgent,
  IconAttach,
  IconCamera,
  IconDocs,
  IconModelFile,
  IconWarning,
  IconWatch,
  Spinner,
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
import { isHosted, rpc } from "../lib/bridge";
import type { Attachment, Effort, FileEntry, ImageMediaType } from "../lib/types";

export function Composer() {
  const thread = useStore(currentThread);
  const key = useStore(draftKey);
  const draft = useStore((s) => s.drafts[key] ?? "");
  const attachments = useStore((s) => s.attachments[key] ?? EMPTY);
  const running = useStore((s) => (thread ? !!s.runs[thread.id] : false));
  const settings = useStore((s) => s.settings);
  const hasDoc = useStore((s) => !!s.docs[s.viewKey]);
  const markupActive = useStore((s) => s.markup.active);
  const watch = useStore((s) => s.watch);
  const canWatch = useStore((s) => s.docs[s.viewKey]?.type === "part" || s.docs[s.viewKey]?.type === "assembly");
  const focusTick = useStore((s) => s.focusTick);
  const { setDraft, send, stop, attachView, addAttachment, removeAttachment, setThreadModel, setThreadEffort, startMarkup, pickFiles, startWatch } =
    useStore.getState();
  const ref = useRef<HTMLTextAreaElement>(null);
  const [dragging, setDragging] = useState(false);

  const model = thread?.model ?? settings.defaultModel;
  const effort = thread?.effort ?? settings.defaultEffort;
  const reading = attachments.some((a) => a.kind === "model" && a.status === "reading");
  const canSend = !running && !markupActive && !reading && (draft.trim().length > 0 || attachments.length > 0);

  // Files dropped anywhere on the panel must not make the WebView navigate to them.
  useEffect(() => {
    const block = (e: DragEvent) => {
      if (e.dataTransfer?.types.includes("Files")) e.preventDefault();
    };
    window.addEventListener("dragover", block);
    window.addEventListener("drop", block);
    return () => {
      window.removeEventListener("dragover", block);
      window.removeEventListener("drop", block);
    };
  }, []);

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

  const onDrop = (e: React.DragEvent) => {
    setDragging(false);
    const files = Array.from(e.dataTransfer.files);
    if (files.length === 0) return;
    e.preventDefault();
    const webview = (window as any).chrome?.webview;
    if (isHosted && webview?.postMessageWithAdditionalObjects) {
      // The page can't see file paths; WebView2 passes the File objects to the add-in, which reads them.
      webview.postMessageWithAdditionalObjects({ kind: "files" }, e.dataTransfer.files);
      return;
    }
    // Outside SolidWorks (UI development): read images here, let the mock host stand in for the rest.
    for (const f of files) {
      if (f.type.startsWith("image/")) readImage(f).then((a) => a && addAttachment(a));
    }
    const others = files.filter((f) => !f.type.startsWith("image/")).map((f) => f.name);
    if (others.length) rpc<FileEntry[]>("files.entries", { paths: others }).then((entries) => useStore.getState().addFileEntries(entries));
  };

  return (
    <div
      className="sw-etch relative shrink-0 bg-panel px-1.5 pb-1.5 pt-1.5"
      onDragOver={(e) => {
        if (!e.dataTransfer.types.includes("Files")) return;
        e.preventDefault();
        setDragging(true);
      }}
      onDragLeave={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node)) setDragging(false);
      }}
      onDrop={onDrop}
    >
      {dragging && (
        <div className="pointer-events-none absolute inset-1 z-10 flex items-center justify-center rounded-[3px] border-2 border-dashed border-accent bg-panel/85 text-[11.5px] font-semibold text-text">
          Drop images, PDFs or 3D models (STL, STEP, 3MF, IGES, SLDPRT...)
        </div>
      )}
      {watch.active || watch.busy ? <WatchBar /> : markupActive ? <MarkupBar /> : hasDoc && <SelectionChips />}

      <div className="sw-input">
        {attachments.length > 0 && (
          <div className="flex flex-wrap gap-1.5 px-1.5 pt-1.5">
            {attachments.map((a) => (
              <AttachmentChip key={a.id} a={a} onRemove={() => removeAttachment(a.id)} />
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
        <IconButton title="Attach images, PDFs or 3D models (or drop them here)" onClick={pickFiles} disabled={markupActive}>
          <IconAttach />
        </IconButton>
        {hasDoc && (
          <>
            {canWatch && (
              <IconButton
                title={watch.active ? "Watching what you do - stop from the bar above" : "Watch me: record what you do in this document, then turn it into a tool or workflow"}
                active={watch.active}
                onClick={() => (watch.active ? undefined : startWatch())}
                disabled={markupActive || watch.busy}
              >
                <IconWatch recording={watch.active} />
              </IconButton>
            )}
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
          <button type="button" onClick={submit} disabled={!canSend} title={reading ? "Waiting for the 3D model to be read" : "Send (Enter)"} className="sw-btn sw-btn-primary !h-6 !px-2.5">
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

/** One attached item above the text box: an image, a PDF, a 3D model (its six views once read) or a Watch me log. */
function AttachmentChip({ a, onRemove }: { a: Attachment; onRemove: () => void }) {
  let body: React.ReactNode;
  let title = a.name;
  switch (a.kind) {
    case "image":
      body = (
        <>
          <img src={`data:${a.mediaType};base64,${a.data}`} alt={a.name} className="h-12 w-auto" />
          {a.name === MARKUP_NAME && <span className="absolute bottom-0 left-0 right-0 bg-black/55 px-1 text-[9.5px] text-white">Markup</span>}
        </>
      );
      break;
    case "pdf":
      body = <ChipLabel icon={<IconDocs size={18} />} name={a.name} note="PDF" />;
      break;
    case "model":
      title = a.entry?.summary ?? (a.status === "reading" ? `Reading ${a.name}: rendering six views and measuring it...` : a.entry?.error ?? a.name);
      body =
        a.status === "ready" && a.entry?.views ? (
          <>
            <img src={`data:${a.entry.viewsMediaType ?? "image/jpeg"};base64,${a.entry.views}`} alt={a.name} className="h-12 w-auto" />
            <span className="absolute bottom-0 left-0 right-0 truncate bg-black/55 px-1 text-[9.5px] text-white">{a.name}</span>
          </>
        ) : (
          <ChipLabel
            icon={a.status === "reading" ? <Spinner size={16} /> : a.status === "error" ? <IconWarning size={16} /> : <IconModelFile size={18} />}
            name={a.name}
            note={a.status === "reading" ? "Reading model..." : a.status === "error" ? "Couldn't read - path only" : (a.entry?.format ?? "3D model")}
          />
        );
      break;
    case "watch":
      title = a.watch.text;
      body = (
        <>
          {a.watch.after || a.watch.before ? (
            <img src={`data:image/jpeg;base64,${a.watch.after ?? a.watch.before}`} alt="" className="h-12 w-auto opacity-90" />
          ) : (
            <div className="h-12 w-[72px] bg-card-2" />
          )}
          <span className="absolute bottom-0 left-0 right-0 flex items-center gap-1 bg-black/60 px-1 text-[9.5px] text-white">
            <IconWatch size={11} /> {a.watch.steps} step{a.watch.steps === 1 ? "" : "s"}
          </span>
        </>
      );
      break;
  }
  return (
    <div className="group relative overflow-hidden rounded-[2px] border border-line bg-card shadow-[var(--shadow)]" title={title}>
      {body}
      <button
        onClick={onRemove}
        title="Remove"
        className="absolute right-0.5 top-0.5 hidden h-4 w-4 items-center justify-center rounded-full bg-black/60 text-white group-hover:flex"
      >
        <X size={10} />
      </button>
    </div>
  );
}

function ChipLabel({ icon, name, note }: { icon: React.ReactNode; name: string; note: string }) {
  return (
    <div className="flex h-12 max-w-[190px] items-center gap-1.5 px-2">
      <span className="shrink-0">{icon}</span>
      <span className="min-w-0">
        <span className="block truncate text-[11px] text-text">{name}</span>
        <span className="block truncate text-[10px] text-text-3">{note}</span>
      </span>
    </div>
  );
}

/** Shown while "Watch me" records: the steps so far, live, and Stop / Discard. */
function WatchBar() {
  const watch = useStore((s) => s.watch);
  const { stopWatch, cancelWatch } = useStore.getState();
  const [, tick] = useState(0);
  useEffect(() => {
    const t = setInterval(() => tick((n) => n + 1), 1000);
    return () => clearInterval(t);
  }, []);
  const list = useRef<HTMLDivElement>(null);
  useEffect(() => {
    list.current?.scrollTo({ top: list.current.scrollHeight });
  }, [watch.entries.length]);
  const seconds = Math.max(0, Math.round((Date.now() - watch.startedAt) / 1000));
  const clock = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
  return (
    <div className="sw-rollout mb-1.5">
      <div className="sw-rollout-head !font-normal">
        <span className="relative flex h-2.5 w-2.5 shrink-0">
          {!watch.busy && <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-[#E5302A] opacity-60" />}
          <span className="relative inline-flex h-2.5 w-2.5 rounded-full bg-[#E5302A]" />
        </span>
        <span className="min-w-0 flex-1 truncate text-[11.5px] font-semibold">
          {watch.busy ? "Finishing the recording..." : `Watching what you do${watch.doc ? ` in ${watch.doc}` : ""}`}
        </span>
        <span className="text-[10.5px] tabular-nums text-text-3">{clock}</span>
      </div>
      <div ref={list} className="max-h-[92px] overflow-y-auto px-2 py-1 text-[10.5px] leading-[1.45] text-text-2">
        {watch.entries.length === 0 ? (
          <div className="text-text-3">Model as usual - features you add, edit, rename or delete, and what you select, are recorded here.</div>
        ) : (
          watch.entries.map((e, i) => (
            <div key={e.id} className="flex gap-1.5">
              <span className="w-4 shrink-0 text-right text-text-3">{i + 1}.</span>
              <span className={`min-w-0 ${e.kind === "select" ? "text-text-3" : ""}`}>
                {e.text}
                {e.byAgent ? <span className="text-text-3"> (agent)</span> : null}
              </span>
            </div>
          ))
        )}
      </div>
      <div className="flex items-center gap-0.5 px-1 pb-1 pt-0.5">
        <span className="px-1 text-[10.5px] text-text-3">
          {watch.entries.length} step{watch.entries.length === 1 ? "" : "s"}
        </span>
        <div className="flex-1" />
        <button type="button" onClick={cancelWatch} disabled={watch.busy} className="sw-btn" title="Stop and throw the recording away">
          <IconCancel size={10} /> Discard
        </button>
        <button
          type="button"
          onClick={stopWatch}
          disabled={watch.busy}
          className="sw-btn sw-btn-primary"
          title="Stop and attach the log, with the view before and after, to your next message"
        >
          <IconOk size={11} /> Stop &amp; attach
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
  const mediaType = file.type as ImageMediaType;
  if (!["image/jpeg", "image/png", "image/webp", "image/gif"].includes(mediaType)) return null;
  const data = await new Promise<string>((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result).split(",")[1] ?? "");
    r.onerror = () => reject(r.error);
    r.readAsDataURL(file);
  });
  return { id: crypto.randomUUID(), kind: "image", name: file.name || "Pasted image", mediaType, data };
}

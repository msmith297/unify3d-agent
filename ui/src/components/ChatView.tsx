import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { ArrowDown, X } from "lucide-react";
import { IconCancel, IconCopy, IconEdit, IconError, IconEye, IconOk, IconQuestion, IconResend, IconShield, ToolIcon } from "./SwIcons";
import { Markdown } from "./Markdown";
import { BrandLogo } from "./Brand";
import { FormCard } from "./FormCard";
import { svgDataUrl } from "../lib/toolIcons";
import { Caret } from "./SwIcons";
import { ToolCard, statusFor, type ToolCall } from "./ToolCard";
import { highlights as pickHighlights } from "../agent/workSummary";
import { currentThread, isPrompt, promptText, useStore, writesAfter, type RunState } from "../store";
import { modelOption } from "../lib/models";
import type { ContentBlockParam, ImageBlockParam, MessageParam, Thread, ToolResultBlockParam, TurnMeta } from "../lib/types";
import type { LiveBlock } from "../agent/runAgent";

type Item =
  | { kind: "prompt"; key: string; index: number; text: string; images: string[]; refs: { id: string; text: string }[]; view: string | null }
  | { kind: "progress"; key: string; text: string }
  | { kind: "text"; key: string; text: string; streaming?: boolean }
  | { kind: "tool"; key: string; call: ToolCall }
  | { kind: "question"; key: string; call: ToolCall }
  | { kind: "footer"; key: string; turn: TurnMeta; isLast: boolean }
  | { kind: "work"; key: string; items: Item[]; highlights: Item[]; steps: number; durationMs?: number }
  | { kind: "thinking"; key: string };

export function ChatView() {
  const thread = useStore(currentThread);
  const run = useStore((s) => (thread ? s.runs[thread.id] : undefined));
  const showProgress = useStore((s) => s.settings.showProgress);
  const [lightbox, setLightbox] = useState<string | null>(null);
  const scroller = useRef<HTMLDivElement>(null);
  const [pinned, setPinned] = useState(true);

  const items = useMemo(() => (thread ? buildItems(thread, run, showProgress) : []), [thread, run, showProgress]);

  // Stick to the bottom while new content streams in, unless the user scrolled up.
  useLayoutEffect(() => {
    const el = scroller.current;
    if (el && pinned) el.scrollTop = el.scrollHeight;
  }, [items, pinned]);

  useEffect(() => setPinned(true), [thread?.id]);

  if (!thread) return <EmptyState />;

  return (
    <div className="relative min-h-0 flex-1">
      <div
        ref={scroller}
        className="h-full overflow-y-auto px-3 pb-4 pt-3"
        onScroll={(e) => {
          const el = e.currentTarget;
          setPinned(el.scrollHeight - el.scrollTop - el.clientHeight < 40);
        }}
      >
        <div className="zoom-content mx-auto flex max-w-[780px] flex-col gap-2">
          {items.map((item) => (
            <Fragment key={item.key}>{renderItem(item, thread, run, setLightbox)}</Fragment>
          ))}
        </div>
      </div>
      {!pinned && (
        <button
          type="button"
          onClick={() => setPinned(true)}
          className="sw-btn absolute bottom-3 left-1/2 !h-6 !w-6 -translate-x-1/2 !rounded-full !p-0"
          title="Scroll to latest"
        >
          <ArrowDown size={14} />
        </button>
      )}
      {lightbox && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4" onClick={() => setLightbox(null)}>
          <img src={lightbox} alt="" className="max-h-full max-w-full rounded shadow-2xl" />
          <button className="absolute right-3 top-3 text-white/80 hover:text-white" title="Close">
            <X size={20} />
          </button>
        </div>
      )}
    </div>
  );
}

function renderItem(item: Item, thread: Thread, run: RunState | undefined, onImage: (s: string) => void) {
  switch (item.kind) {
    case "prompt":
      return <PromptBubble item={item} thread={thread} running={!!run} onImage={onImage} />;
    case "progress":
      return (
        <div className="flex gap-2 pl-1 text-[12px] italic leading-snug text-text-3">
          <span className="mt-[7px] h-1 w-1 shrink-0 rounded-full bg-text-3" />
          <span className="selectable">{item.text}</span>
        </div>
      );
    case "thinking":
      return <div className="pl-1 text-[12px] shimmer">Thinking…</div>;
    case "text":
      return (
        <div className="selectable px-0.5 text-text">
          <Markdown text={item.text} streaming={item.streaming} />
        </div>
      );
    case "tool":
      return (
        <ToolCard call={item.call} status={statusFor(item.call, run)} onImage={onImage}>
          {run?.approval?.toolUseId === item.call.id && <ApprovalCard threadId={thread.id} />}
        </ToolCard>
      );
    case "question":
      return item.call.name === "ask_form" ? <FormCard call={item.call} threadId={thread.id} run={run} /> : <QuestionCard call={item.call} threadId={thread.id} run={run} />;
    case "footer":
      return <TurnFooter turn={item.turn} isLast={item.isLast} running={!!run} />;
    case "work":
      return <WorkGroup item={item} thread={thread} run={run} onImage={onImage} />;
  }
}

/**
 * A finished turn's working steps, folded: a header ("18 steps · 2m 53s") over just the steps that changed
 * something. Expanding shows everything - every check, search, failed attempt and the narration between them.
 */
function WorkGroup({
  item,
  thread,
  run,
  onImage,
}: {
  item: Extract<Item, { kind: "work" }>;
  thread: Thread;
  run: RunState | undefined;
  onImage: (s: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const hidden = item.steps - item.highlights.filter((h) => h.kind === "tool").length;
  return (
    <div className="flex flex-col gap-1.5">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="flex items-center gap-1.5 self-start rounded-[3px] px-1 py-0.5 text-[11.5px] text-text-3 hover:bg-accent-soft hover:text-text"
        title={open ? "Show only the main changes" : "Show every step, including checks, searches and retries"}
      >
        <Caret open={open} />
        <span>
          {item.steps} step{item.steps === 1 ? "" : "s"}
          {item.durationMs !== undefined && ` · ${formatDuration(item.durationMs)}`}
          {!open && hidden > 0 && ` · ${hidden} hidden`}
        </span>
      </button>
      {(open ? item.items : item.highlights).map((sub) => (
        <Fragment key={sub.key}>{renderItem(sub, thread, run, onImage)}</Fragment>
      ))}
    </div>
  );
}

/**
 * Fold a finished turn: everything up to the last tool call is "work"; the text after it is the answer and stays.
 * Turns without tool calls are left as they are.
 */
function foldTurn(segment: Item[], turn: TurnMeta | undefined, key: string): Item[] {
  let last = -1;
  segment.forEach((it, i) => {
    if (it.kind === "tool" || it.kind === "question") last = i;
  });
  if (last < 0) return segment;
  const work = segment.slice(0, last + 1);
  const calls = work.filter((it): it is Extract<Item, { kind: "tool" | "question" }> => it.kind === "tool" || it.kind === "question");
  const keep = new Set(pickHighlights(calls.map((c) => c.call)).map((c) => c.id));
  const group: Item = {
    kind: "work",
    key,
    items: work,
    highlights: calls.filter((c) => keep.has(c.call.id)),
    steps: calls.filter((c) => c.kind === "tool").length,
    durationMs: turn?.durationMs,
  };
  return [group, ...segment.slice(last + 1)];
}

/** Flatten API messages (+ the live stream) into display items. */
function buildItems(thread: Thread, run: RunState | undefined, showProgress: boolean): Item[] {
  const items: Item[] = [];
  const results = new Map<string, ToolResultBlockParam>();
  thread.messages.forEach((m) => {
    if (m.role === "user" && typeof m.content !== "string") {
      for (const b of m.content) if (b.type === "tool_result") results.set(b.tool_use_id, b);
    }
  });

  const footerAfter = new Map<number, TurnMeta>();
  thread.turns.forEach((t) => footerAfter.set(t.msgIndex, t)); // last turn per prompt wins
  const promptIndexes = thread.messages.map((m, i) => (isPrompt(m) ? i : -1)).filter((i) => i >= 0);
  const lastPrompt = promptIndexes[promptIndexes.length - 1] ?? -1;

  // Items of the current turn are collected in `segment` and folded when the turn is finished.
  let segment: Item[] = [];
  const flushSegment = (promptIdx: number, finished: boolean) => {
    const turn = footerAfter.get(promptIdx);
    items.push(...(finished && promptIdx >= 0 ? foldTurn(segment, turn, `w${promptIdx}`) : segment));
    segment = [];
  };
  const flushFooter = (promptIdx: number) => {
    flushSegment(promptIdx, true);
    const turn = footerAfter.get(promptIdx);
    if (turn) items.push({ kind: "footer", key: `f${promptIdx}`, turn, isLast: promptIdx === lastPrompt });
  };

  let currentPrompt = -1;
  thread.messages.forEach((m, i) => {
    if (isPrompt(m)) {
      if (currentPrompt >= 0) flushFooter(currentPrompt);
      else flushSegment(-1, false);
      currentPrompt = i;
      items.push(promptItem(m, i));
      return;
    }
    if (m.role !== "assistant" || typeof m.content === "string") return;
    m.content.forEach((b, j) => {
      const key = `m${i}b${j}`;
      if (b.type === "thinking" && b.thinking.trim() && showProgress) segment.push({ kind: "progress", key, text: b.thinking.trim() });
      else if (b.type === "text" && b.text.trim()) segment.push({ kind: "text", key, text: b.text });
      else if (b.type === "tool_use") {
        const call: ToolCall = { id: b.id, name: b.name, input: b.input, result: results.get(b.id) };
        segment.push(call.name === "ask_user" || call.name === "ask_form" ? { kind: "question", key, call } : { kind: "tool", key, call });
      }
    });
  });

  if (run) {
    flushSegment(currentPrompt, false); // the running turn stays unfolded
    const waitingOnTool = !run.live && (run.runningToolId || run.approval || run.question);
    if (!waitingOnTool) items.push(...liveItems(run.live, showProgress));
  }
  else if (currentPrompt >= 0) flushFooter(currentPrompt);
  else flushSegment(-1, false);
  return items;
}

function liveItems(live: LiveBlock[] | null, showProgress: boolean): Item[] {
  const out: Item[] = [];
  if (!live || live.length === 0) return [{ kind: "thinking", key: "live-thinking" }];
  live.forEach((b, i) => {
    const key = `live${i}`;
    if (b.type === "thinking" && b.text.trim() && showProgress) out.push({ kind: "progress", key, text: b.text.trim() });
    else if (b.type === "text" && b.text) out.push({ kind: "text", key, text: b.text, streaming: i === live.length - 1 });
    else if (b.type === "tool_use" && b.name !== "ask_user") {
      out.push({ kind: "tool", key, call: { id: b.id ?? key, name: b.name ?? "tool", input: undefined, partial: b.json ?? "" } });
    }
  });
  const last = live[live.length - 1];
  if (!last || last.type === "thinking" || last.type === "other") out.push({ kind: "thinking", key: "live-thinking" });
  return out;
}

function promptItem(m: MessageParam, index: number): Item {
  const blocks: ContentBlockParam[] = typeof m.content === "string" ? [{ type: "text", text: m.content }] : m.content;
  const texts: string[] = [];
  const images: string[] = [];
  let refs: { id: string; text: string }[] = [];
  let view: string | null = null;
  let afterContext = false;
  for (const b of blocks) {
    if (b.type === "text") {
      if (b.text.startsWith("<sw_context>")) {
        refs = [...refs, ...parseRefs(b.text)];
        afterContext = true;
      } else texts.push(b.text);
    } else if (b.type === "image") {
      const src = (b as ImageBlockParam).source;
      if (src.type !== "base64") continue;
      const url = `data:${src.media_type};base64,${src.data}`;
      // Images after the context block are the automatic "what the user sees" screenshot.
      if (afterContext) view = url;
      else images.push(url);
    }
  }
  return { kind: "prompt", key: `p${index}`, index, text: texts.join("\n\n"), images, refs, view };
}

/** "[S1] Face of Boss-Extrude1 - planar, ..." lines from the context block, shortened for chips. */
function parseRefs(raw: string): { id: string; text: string }[] {
  const out: { id: string; text: string }[] = [];
  for (const line of raw.split("\n")) {
    const m = /^\[(S\d+)\] (.+?)(?: - |$)/.exec(line);
    if (m) out.push({ id: m[1], text: m[2] });
  }
  return out;
}

function PromptBubble({
  item,
  thread,
  running,
  onImage,
}: {
  item: Extract<Item, { kind: "prompt" }>;
  thread: Thread;
  running: boolean;
  onImage: (s: string) => void;
}) {
  const resendFrom = useStore((s) => s.resendFrom);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const [copied, setCopied] = useState(false);
  const { text, images, refs, view, index } = item;

  /** Resending drops later replies; changes already made to the model stay (Ctrl+Z undoes them). */
  const confirmRewind = () => {
    const writes = writesAfter(thread, index);
    if (writes === 0) return true;
    return confirm(
      `The replies after this message will be replaced. The agent made ${writes} change${writes > 1 ? "s" : ""} to the model ` +
        `since then - ${writes > 1 ? "they stay" : "it stays"} in the model (use Ctrl+Z in SolidWorks to undo ${writes > 1 ? "them" : "it"}). Continue?`,
    );
  };

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(promptText(thread.messages[index]));
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard unavailable */
    }
  };

  const submitEdit = () => {
    if (!draft.trim() && images.length === 0) return;
    if (!confirmRewind()) return;
    setEditing(false);
    resendFrom(index, draft);
  };

  if (editing) {
    return (
      <div className="sw-rollout mt-2 flex flex-col gap-1.5 !border-accent p-1.5 first:mt-0">
        <textarea
          autoFocus
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onFocus={(e) => e.currentTarget.setSelectionRange(e.currentTarget.value.length, e.currentTarget.value.length)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault();
              submitEdit();
            } else if (e.key === "Escape") {
              setEditing(false);
            }
          }}
          rows={Math.min(8, Math.max(2, draft.split("\n").length))}
          className="sw-input w-full resize-none px-2 py-1 text-[12px] leading-[1.42]"
        />
        <div className="flex items-center justify-end gap-1.5">
          <span className="mr-auto text-[10.5px] text-text-3">Replies after this message will be replaced.</span>
          <button onClick={() => setEditing(false)} className="sw-btn">
            <IconCancel size={11} />
            Cancel
          </button>
          <button
            onClick={submitEdit}
            disabled={!draft.trim() && images.length === 0}
            className="sw-btn sw-btn-primary"
          >
            Send
          </button>
        </div>
      </div>
    );
  }

  if (/^\[Run tool: /.test(text)) return <ToolRunPrompt text={text} images={images} refs={refs} view={view} onImage={onImage} />;

  return (
    <div className="group mt-2 flex flex-col items-end gap-1 first:mt-0">
      {images.length > 0 && (
        <div className="flex flex-wrap justify-end gap-1.5">
          {images.map((src, i) => (
            <button key={i} onClick={() => onImage(src)} className="overflow-hidden rounded-[2px] border border-input-border shadow-[var(--shadow)]">
              <img src={src} alt="" className="h-[64px] w-auto" />
            </button>
          ))}
        </div>
      )}
      {text && <PromptText text={text} />}
      {(refs.length > 0 || view) && (
        <div className="flex max-w-[92%] flex-wrap items-center justify-end gap-1">
          {refs.map((r) => (
            <span key={r.id} title={r.text} className="flex max-w-[180px] items-center gap-1 rounded-[2px] border border-line bg-card-2 px-1 text-[10.5px] text-text-2">
              <b className="text-accent">{r.id}</b>
              <span className="truncate">{r.text}</span>
            </span>
          ))}
          {view && (
            <button onClick={() => onImage(view)} title="The view the agent saw with this message" className="flex items-center gap-1 rounded-[2px] border border-line bg-card-2 py-px pl-0.5 pr-1 text-[10.5px] text-text-3 hover:text-text">
              <img src={view} alt="" className="h-4 w-auto rounded-[1px]" />
              <IconEye size={11} /> View
            </button>
          )}
        </div>
      )}
      <div className="flex h-5 items-center gap-0.5">
        <div className="flex items-center gap-0.5 opacity-0 transition-opacity group-hover:opacity-100 focus-within:opacity-100">
          <BubbleAction title={copied ? "Copied" : "Copy"} onClick={copy}>
            {copied ? <IconOk size={12} /> : <IconCopy size={13} />}
          </BubbleAction>
          {!running && (
            <>
              <BubbleAction
                title="Edit"
                onClick={() => {
                  setDraft(text);
                  setEditing(true);
                }}
              >
                <IconEdit size={13} />
              </BubbleAction>
              <BubbleAction title="Resend" onClick={() => confirmRewind() && resendFrom(index)}>
                <IconResend size={13} />
              </BubbleAction>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

/**
 * A ribbon tool run: just "Tool: Smart fillet" - the inputs, attachments, selections and view the tool captured
 * stay folded away unless the user opens them.
 */
function ToolRunPrompt({
  text,
  images,
  refs,
  view,
  onImage,
}: {
  text: string;
  images: string[];
  refs: { id: string; text: string }[];
  view: string | null;
  onImage: (s: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const tools = useStore((s) => s.userTools);
  const m = /^\[Run tool: ([a-z0-9-]+)\]\s*([\s\S]*)$/.exec(text);
  const tool = m ? tools.find((t) => t.name === m[1]) : undefined;
  const body = (m?.[2] ?? "").replace(/<\/?tool_inputs>/g, "").trim();
  const hasDetails = !!body || images.length > 0 || refs.length > 0 || !!view;
  return (
    <div className="mt-2 flex flex-col items-end gap-1 first:mt-0">
      <button
        type="button"
        onClick={() => hasDetails && setOpen((o) => !o)}
        className="flex max-w-[88%] items-center gap-1.5 rounded-[3px] border border-line bg-user px-2.5 py-1 text-[12px] font-semibold text-text shadow-[inset_0_1px_0_var(--bevel-hi),var(--shadow)]"
        title={hasDetails ? (open ? "Hide the inputs" : "Show the inputs and context the tool captured") : undefined}
      >
        {tool?.iconSvg ? <img src={svgDataUrl(tool.iconSvg)} alt="" width={14} height={14} /> : <ToolIcon name={tool?.icon ?? "macro"} size={14} />}
        <span>Tool: {tool?.title ?? m?.[1]}</span>
        {hasDetails && <Caret open={open} className="text-text-3" />}
      </button>
      {open && (
        <div className="flex max-w-[92%] flex-col items-end gap-1">
          {body && <div className="selectable whitespace-pre-wrap rounded-[3px] border border-line bg-card-2 px-2 py-1 text-[11.5px] text-text-2">{body}</div>}
          {(images.length > 0 || view) && (
            <div className="flex flex-wrap justify-end gap-1.5">
              {[...images, ...(view ? [view] : [])].map((src, i) => (
                <button key={i} onClick={() => onImage(src)} className="overflow-hidden rounded-[2px] border border-input-border shadow-[var(--shadow)]">
                  <img src={src} alt="" className="h-[48px] w-auto" />
                </button>
              ))}
            </div>
          )}
          {refs.length > 0 && (
            <div className="flex flex-wrap justify-end gap-1">
              {refs.map((r) => (
                <span key={r.id} title={r.text} className="flex max-w-[180px] items-center gap-1 rounded-[2px] border border-line bg-card-2 px-1 text-[10.5px] text-text-2">
                  <b className="text-accent">{r.id}</b>
                  <span className="truncate">{r.text}</span>
                </span>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/** The user's words; a leading "[Run workflow: name]" / "[Run tool: name]" is shown as a chip instead of raw text. */
function PromptText({ text }: { text: string }) {
  const tools = useStore((s) => s.userTools);
  const m = /^\[Run (tool|workflow): ([a-z0-9-]+)\]\s*([\s\S]*)$/.exec(text);
  const tool = m ? tools.find((t) => t.name === m[2]) : undefined;
  const body = m ? m[3] : text;
  const label = tool?.kind === "tool" ? "Tool" : tool?.kind === "workflow" ? "Workflow" : m?.[1] === "tool" ? "Tool" : "Workflow";
  return (
    <div className="selectable max-w-[88%] rounded-[3px] border border-line bg-user px-2.5 py-1.5 text-[12px] text-text shadow-[inset_0_1px_0_var(--bevel-hi),var(--shadow)]">
      {m && (
        <div className={`flex items-center gap-1.5 font-semibold ${body ? "mb-1" : ""}`}>
          {tool?.iconSvg ? <img src={svgDataUrl(tool.iconSvg)} alt="" width={14} height={14} /> : <ToolIcon name={tool?.icon ?? "macro"} size={14} />}
          <span>
            {label}: {tool?.title ?? m[2]}
          </span>
        </div>
      )}
      {body && <div className="whitespace-pre-wrap">{body}</div>}
    </div>
  );
}

function BubbleAction({ title, onClick, children }: { title: string; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      title={title}
      aria-label={title}
      onClick={onClick}
      className="sw-tool !h-[22px] !min-w-[22px] !p-0"
    >
      {children}
    </button>
  );
}

function TurnFooter({ turn, isLast, running }: { turn: TurnMeta; isLast: boolean; running: boolean }) {
  const retry = useStore((s) => s.retry);
  const u = turn.usage;
  const tokens = u ? u.input + u.cacheRead + u.cacheWrite : 0;
  const cachedPct = tokens > 0 ? Math.round((u!.cacheRead / tokens) * 100) : 0;
  return (
    <div className="flex flex-col gap-1.5">
      {turn.error && (
        <div className="flex items-start gap-2 rounded-[3px] border border-err/50 bg-err-soft px-2 py-1.5 text-[12px] text-text shadow-[var(--shadow)]">
          <span className="mt-px shrink-0"><IconError size={14} /></span>
          <span className="selectable flex-1">{turn.error}</span>
          {isLast && !running && turn.stop !== "aborted" && (
            <button onClick={retry} className="sw-btn shrink-0">
              <IconResend size={12} /> Retry
            </button>
          )}
        </div>
      )}
      {turn.stop === "aborted" && !turn.error && <div className="pl-1 text-[11px] text-text-3">Stopped.</div>}
      <div className="flex items-center gap-1.5 pl-1 text-[10.5px] text-text-3">
        <span>{modelOption(turn.model).label}</span>
        <span>·</span>
        <span>{turn.effort}</span>
        {turn.durationMs !== undefined && (
          <>
            <span>·</span>
            <span>{formatDuration(turn.durationMs)}</span>
          </>
        )}
        {tokens > 0 && (
          <>
            <span>·</span>
            <span
              title={
                `Largest prompt: ${(u.context ?? 0).toLocaleString()} tokens\n` +
                `Sent over all steps: ${tokens.toLocaleString()} (${cachedPct}% from cache, billed at 10%)\n` +
                `uncached ${u.input.toLocaleString()} · cache write ${u.cacheWrite.toLocaleString()} · output ${u.output.toLocaleString()}`
              }
            >
              {u.context ? `${formatTokens(u.context)} context` : `${formatTokens(tokens)} in`} / {formatTokens(u.output)} out
            </span>
          </>
        )}
      </div>
    </div>
  );
}

function ApprovalCard({ threadId }: { threadId: string }) {
  const answer = useStore((s) => s.answerApproval);
  const [rejecting, setRejecting] = useState(false);
  const [note, setNote] = useState("");
  return (
    <div className="rounded-[3px] border border-accent/60 bg-accent-soft px-2 py-1.5 shadow-[inset_0_1px_0_var(--bevel-hi)]">
      <div className="mb-1.5 flex items-center gap-1.5 text-[12px] font-semibold text-text">
        <IconShield size={14} /> Run this code in SolidWorks?
      </div>
      {!rejecting ? (
        <div className="flex flex-wrap gap-1.5">
          <button autoFocus onClick={() => answer(threadId, { decision: "run" })} className="sw-btn">
            <IconOk size={12} />
            Run
          </button>
          <button onClick={() => answer(threadId, { decision: "always" })} className="sw-btn" title="Don't ask again in this chat">
            Always allow in this chat
          </button>
          <button onClick={() => setRejecting(true)} className="sw-btn">
            <IconCancel size={11} />
            Reject…
          </button>
        </div>
      ) : (
        <form
          className="flex gap-1.5"
          onSubmit={(e) => {
            e.preventDefault();
            answer(threadId, { decision: "reject", note: note.trim() || undefined });
          }}
        >
          <input
            autoFocus
            value={note}
            onChange={(e) => setNote(e.target.value)}
            placeholder="Optional: tell the agent what to do instead"
            className="sw-input h-[22px] min-w-0 flex-1 px-1.5 text-[12px]"
          />
          <button type="submit" className="sw-btn">
            <IconCancel size={11} />
            Reject
          </button>
        </form>
      )}
    </div>
  );
}

function QuestionCard({ call, threadId, run }: { call: ToolCall; threadId: string; run: RunState | undefined }) {
  const answer = useStore((s) => s.answerQuestion);
  const [text, setText] = useState("");
  const pending = run?.question?.toolUseId === call.id;
  const answered = typeof call.result?.content === "string" ? call.result.content : undefined;
  const options: string[] = call.input?.options ?? [];
  return (
    <div className={`sw-rollout px-2 py-1.5 ${pending ? "!border-accent" : ""}`}>
      <div className="mb-1.5 flex items-start gap-2">
        <span className="mt-px shrink-0"><IconQuestion size={15} /></span>
        <div className="selectable text-[12px] font-semibold text-text">{call.input?.question}</div>
      </div>
      {pending ? (
        <div className="flex flex-col gap-1.5 pl-[22px]">
          {options.length > 0 && (
            <div className="flex flex-wrap gap-1.5">
              {options.map((o) => (
                <button key={o} onClick={() => answer(threadId, o)} className="sw-btn !h-auto min-h-[22px] whitespace-normal py-0.5 text-left">
                  {o}
                </button>
              ))}
            </div>
          )}
          <form
            className="flex gap-1.5"
            onSubmit={(e) => {
              e.preventDefault();
              if (text.trim()) answer(threadId, text.trim());
            }}
          >
            <input
              autoFocus
              value={text}
              onChange={(e) => setText(e.target.value)}
              placeholder={options.length ? "Or type your own answer" : "Type your answer"}
              className="sw-input h-[22px] min-w-0 flex-1 px-1.5 text-[12px]"
            />
            <button type="submit" disabled={!text.trim()} className="sw-btn sw-btn-primary">
              Answer
            </button>
          </form>
        </div>
      ) : answered ? (
        <div className="pl-[22px] text-[12px] text-text-2">
          <span className="text-text-3">Answer: </span>
          {answered}
        </div>
      ) : (
        <div className="pl-[22px] text-[11.5px] text-text-3">Not answered</div>
      )}
    </div>
  );
}

const SUGGESTIONS = [
  "Summarize this model and check it for rebuild errors",
  "Add a 2 mm fillet to the selected edges",
  "Create a 120 × 80 × 10 mm plate with a Ø8 hole 15 mm in from each corner",
  "Set the material to 6061 aluminium and tell me the mass",
];

function EmptyState() {
  const doc = useStore((s) => s.docs[s.viewKey]);
  const send = useStore((s) => s.send);
  const setDraft = useStore((s) => s.setDraft);
  return (
    <div className="flex min-h-0 flex-1 flex-col items-center justify-center overflow-y-auto px-5 py-6 text-center">
      <div className="mb-4">
        <BrandLogo height={30} />
      </div>
      <div className="text-[14px] font-semibold text-text">{doc ? `Ask about ${doc.title}` : "Ask Unify3D anything"}</div>
      <p className="mt-1 max-w-[300px] text-[12px] text-text-2">
        {doc
          ? "It can read and change this document through the SolidWorks API. The conversation is saved inside the file."
          : "Open or create a part, assembly or drawing to keep the conversation with that file."}
      </p>
      <div className="mt-4 flex w-full max-w-[340px] flex-col gap-1.5">
        {SUGGESTIONS.map((s) => (
          <button
            key={s}
            onClick={() => (s.includes("selected") ? setDraft(s) : send(s))}
            className="sw-btn !h-auto !justify-start whitespace-normal py-1.5 text-left"
          >
            {s}
          </button>
        ))}
      </div>
    </div>
  );
}

function formatDuration(ms: number) {
  const s = Math.round(ms / 1000);
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`;
}

function formatTokens(n: number) {
  return n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n);
}

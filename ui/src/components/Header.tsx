import { useCallback, useRef, useState } from "react";
import { IconButton, useOutside } from "./Menu";
import { Caret, IconAssembly, IconCancel, IconDrawing, IconGear, IconHistory, IconNewChat, IconPart, Spinner } from "./SwIcons";
import { EMPTY, currentThread, useStore } from "../store";
import { UpdateButton } from "./Update";
import { confirmAction } from "./Dialog";
import { BrandMark } from "./Brand";
import type { DocInfo } from "../lib/types";

export function DocIcon({ type, size = 16 }: { type?: DocInfo["type"]; size?: number }) {
  if (type === "assembly") return <IconAssembly size={size} />;
  if (type === "drawing") return <IconDrawing size={size} />;
  if (type === "part") return <IconPart size={size} />;
  return <BrandMark size={size} />;
}

/** Title bar styled like the PropertyManager header: document identity left, commands right. */
export function Header() {
  const doc = useStore((s) => s.docs[s.activeDocKey ?? ""]);
  const thread = useStore(currentThread);
  // A chat the agent is working in that isn't the one on screen (e.g. after New chat): click to go back to it.
  const elsewhereId = useStore((s) => {
    const shown = s.chats[s.viewKey]?.data.activeThreadId;
    return Object.keys(s.runs).find((t) => t !== shown) ?? null;
  });
  const elsewhereTitle = useStore((s) => (elsewhereId ? (s.chats[s.viewKey]?.data.threads.find((t) => t.id === elsewhereId)?.title ?? "a chat") : null));
  const elsewhere = elsewhereId ? { id: elsewhereId, title: elsewhereTitle ?? "a chat" } : null;
  const runningHere = useStore((s) => {
    const shown = s.chats[s.viewKey]?.data.activeThreadId;
    return !!shown && !!s.runs[shown];
  });
  const panel = useStore((s) => s.panel);
  const setPanel = useStore((s) => s.setPanel);
  const newChat = useStore((s) => s.newChat);

  return (
    <header className="flex h-[30px] shrink-0 items-center gap-0.5 border-b border-line bg-[linear-gradient(var(--head-top),var(--head-bot))] px-1 shadow-[inset_0_1px_0_var(--bevel-hi)]">
      <div className="flex min-w-0 flex-1 items-center gap-1.5 pl-1" title={doc ? `Active document: ${doc.path || doc.title}. The agent works in the active document.` : "No document open"}>
        <DocIcon type={doc?.type} />
        <span className="truncate text-[12px] font-semibold text-text">{doc?.title ?? "No document"}</span>
        {runningHere && <Spinner size={12} />}
        {elsewhere && (
          <button
            type="button"
            className="flex min-w-0 shrink items-center gap-1 rounded-[3px] border border-accent/60 bg-accent-soft px-1.5 py-[1px] text-[10.5px] text-text hover:border-accent"
            title={`The agent is working in "${elsewhere.title}" - show it`}
            onClick={() => {
              setPanel("chat");
              useStore.getState().selectThread(elsewhere.id);
            }}
          >
            <Spinner size={10} />
            <span className="truncate">Working: {elsewhere.title}</span>
          </button>
        )}
      </div>
      <UpdateButton />
      <ThreadMenu currentTitle={thread?.title} />
      <IconButton
        title="New chat"
        onClick={() => {
          setPanel("chat");
          newChat();
        }}
      >
        <IconNewChat />
      </IconButton>
      <IconButton tour="options" title="Options" active={panel === "settings"} onClick={() => setPanel(panel === "settings" ? "chat" : "settings")}>
        <IconGear />
      </IconButton>
    </header>
  );
}

function ThreadMenu({ currentTitle }: { currentTitle?: string }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useOutside(ref, useCallback(() => setOpen(false), []));
  const threads = useStore((s) => s.chats[s.viewKey]?.data.threads ?? EMPTY);
  const activeId = useStore((s) => s.chats[s.viewKey]?.data.activeThreadId);
  const runs = useStore((s) => s.runs);
  const selectThread = useStore((s) => s.selectThread);
  const deleteThread = useStore((s) => s.deleteThread);
  const setPanel = useStore((s) => s.setPanel);

  if (threads.length === 0) return null;
  return (
    <div ref={ref} className="relative min-w-0">
      <button type="button" aria-pressed={open} onClick={() => setOpen((o) => !o)} title="Your chats (all documents)" className="sw-tool max-w-[160px] text-[11.5px]">
        <IconHistory size={14} />
        <span className="truncate">{currentTitle ?? `${threads.length} chat${threads.length > 1 ? "s" : ""}`}</span>
        <Caret open className="opacity-70" />
      </button>
      {open && (
        <div className="absolute right-0 top-full z-30 mt-1 max-h-[60vh] w-[270px] overflow-y-auto rounded-[3px] border border-line-strong bg-card shadow-[0_4px_12px_rgba(0,0,0,0.35)]">
          <div className="sw-rollout-head !min-h-[20px] text-[11px]">Your chats</div>
          {threads.map((t) => (
            <div key={t.id} className={`group flex items-center gap-1 pr-1 hover:bg-accent-soft ${t.id === activeId ? "bg-card-2" : ""}`}>
              <button
                className="min-w-0 flex-1 px-2 py-1 text-left"
                onClick={() => {
                  selectThread(t.id);
                  setPanel("chat");
                  setOpen(false);
                }}
              >
                <div className="flex items-center gap-1.5">
                  {runs[t.id] && <Spinner size={10} />}
                  <span className="truncate text-[12px] text-text">{t.title}</span>
                </div>
                <div className="truncate text-[10.5px] text-text-3">
                  {new Date(t.updatedAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })}
                  {t.files?.length ? ` · ${t.files.map((f) => f.title).join(", ")}` : ""}
                </div>
              </button>
              <button
                title="Delete chat"
                onClick={() => {
                  confirmAction("Delete this chat?", <p>"{t.title}" is deleted from this PC.</p>, "Delete", true).then((ok) => { if (ok) deleteThread(t.id); });
                }}
                className="sw-tool invisible !h-5 !min-w-5 group-hover:visible"
              >
                <IconCancel size={10} />
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

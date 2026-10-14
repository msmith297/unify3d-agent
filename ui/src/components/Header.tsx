import { useCallback, useRef, useState } from "react";
import { IconButton, useOutside } from "./Menu";
import { Caret, IconAssembly, IconCancel, IconDrawing, IconGear, IconHistory, IconNewChat, IconPart, Spinner } from "./SwIcons";
import { EMPTY, currentThread, useStore } from "../store";
import { UpdateButton } from "./Update";
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
  const doc = useStore((s) => s.docs[s.viewKey]);
  const thread = useStore(currentThread);
  const running = useStore((s) => Object.keys(s.runs).length > 0);
  const panel = useStore((s) => s.panel);
  const setPanel = useStore((s) => s.setPanel);
  const newChat = useStore((s) => s.newChat);

  return (
    <header className="flex h-[30px] shrink-0 items-center gap-0.5 border-b border-line bg-[linear-gradient(var(--head-top),var(--head-bot))] px-1 shadow-[inset_0_1px_0_var(--bevel-hi)]">
      <div className="flex min-w-0 flex-1 items-center gap-1.5 pl-1" title={doc?.path || doc?.title || "No document"}>
        <DocIcon type={doc?.type} />
        <span className="truncate text-[12px] font-semibold text-text">{doc?.title ?? "No document"}</span>
        {running && <Spinner size={12} />}
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
      <IconButton title="Options" active={panel === "settings"} onClick={() => setPanel(panel === "settings" ? "chat" : "settings")}>
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
      <button type="button" aria-pressed={open} onClick={() => setOpen((o) => !o)} title="Chats saved in this document" className="sw-tool max-w-[160px] text-[11.5px]">
        <IconHistory size={14} />
        <span className="truncate">{currentTitle ?? `${threads.length} chat${threads.length > 1 ? "s" : ""}`}</span>
        <Caret open className="opacity-70" />
      </button>
      {open && (
        <div className="absolute right-0 top-full z-30 mt-1 max-h-[60vh] w-[270px] overflow-y-auto rounded-[3px] border border-line-strong bg-card shadow-[0_4px_12px_rgba(0,0,0,0.35)]">
          <div className="sw-rollout-head !min-h-[20px] text-[11px]">Saved in this document</div>
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
                <div className="text-[10.5px] text-text-3">{new Date(t.updatedAt).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })}</div>
              </button>
              <button
                title="Delete chat"
                onClick={() => {
                  if (confirm(`Delete "${t.title}"? This removes it from the document when you next save.`)) deleteThread(t.id);
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

import { useCallback, useRef, useState } from "react";
import { useOutside } from "./Menu";
import { Caret, IconCancel, IconEdit, IconNewChat, IconPart, IconToolbox, ToolIcon } from "./SwIcons";
import { EMPTY, draftKey, useStore } from "../store";
import { rpc } from "../lib/bridge";
import { svgDataUrl } from "../lib/toolIcons";

/**
 * The user's own tools: saved procedures the agent runs. Run uses the current selection and whatever is typed in
 * the composer as inputs. New tools are written with the agent, from a description or from the current model.
 */
export function ToolsPalette({ disabled }: { disabled?: boolean }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useOutside(ref, useCallback(() => setOpen(false), []));
  const tools = useStore((s) => s.userTools ?? EMPTY);
  const hasDoc = useStore((s) => !!s.docs[s.viewKey]);
  const { runUserTool, deleteUserTool, send, setDraft } = useStore.getState();

  const prefill = (text: string) => {
    setDraft(text);
    useStore.setState((s) => ({ focusTick: s.focusTick + 1, drafts: { ...s.drafts, [draftKey(s)]: text } }));
    setOpen(false);
  };

  return (
    <div ref={ref}>
      <button type="button" title="Your tools" aria-pressed={open} disabled={disabled} onClick={() => setOpen((o) => !o)} className="sw-tool text-[11.5px]">
        <IconToolbox size={14} />
        Tools
        <Caret open className="opacity-70" />
      </button>
      {open && (
        <div className="absolute bottom-full left-1.5 right-1.5 z-30 mb-1 overflow-hidden rounded-[3px] border border-line-strong bg-card shadow-[0_4px_14px_rgba(0,0,0,0.4)]">
          <div className="sw-rollout-head !min-h-[22px] text-[11.5px]">
            <IconToolbox size={14} />
            <span className="flex-1">My tools</span>
            <button className="text-[10.5px] font-normal text-text-3 hover:text-text" onClick={() => rpc("userTools.openFolder")} title="Open the folder where tools are saved">
              Folder
            </button>
          </div>

          <div className="max-h-[50vh] overflow-y-auto">
            {tools.length === 0 && (
              <p className="px-2.5 py-2 text-[11.5px] text-text-3">
                No tools yet. Describe a task you repeat, or turn the current model into a tool, and the agent will write it with you.
              </p>
            )}
            {tools.map((t) => (
              <div key={t.name} className="group flex items-start gap-2 border-b border-line/60 px-2 py-1.5 last:border-b-0 hover:bg-accent-soft">
                <span className="mt-0.5 shrink-0">
                  {t.iconSvg ? <img src={svgDataUrl(t.iconSvg)} alt="" width={16} height={16} /> : <ToolIcon name={t.icon} />}
                </span>
                <div className="min-w-0 flex-1">
                  <div className="truncate text-[12px] font-semibold text-text">{t.title}</div>
                  <div className="line-clamp-2 text-[11px] text-text-3">{t.description}</div>
                </div>
                <div className="flex shrink-0 gap-0.5 opacity-0 transition-opacity group-hover:opacity-100 focus-within:opacity-100">
                  <button className="sw-tool !h-[22px] !min-w-[22px] !p-0" title="Change this tool with the agent" onClick={() => prefill(`Update my tool "${t.name}": `)}>
                    <IconEdit size={12} />
                  </button>
                  <button
                    className="sw-tool !h-[22px] !min-w-[22px] !p-0"
                    title="Delete this tool"
                    onClick={() => {
                      if (confirm(`Delete the tool "${t.title}"? This removes its folder.`)) deleteUserTool(t.name);
                    }}
                  >
                    <IconCancel size={9} />
                  </button>
                </div>
                <button
                  className="sw-btn sw-btn-primary shrink-0 !h-[22px] !px-2"
                  disabled={disabled}
                  title="Run with the current selection. Text in the message box is passed as notes."
                  onClick={() => {
                    setOpen(false);
                    runUserTool(t.name);
                  }}
                >
                  Run
                </button>
              </div>
            ))}
          </div>

          <div className="sw-etch flex flex-wrap gap-1 bg-card-2 px-1.5 py-1.5">
            <button className="sw-btn" onClick={() => prefill("Help me create a new tool that ")}>
              <IconNewChat size={13} /> New tool…
            </button>
            {hasDoc && (
              <button
                className="sw-btn"
                title="The agent studies this model's full feature history and turns it into a reusable, parametric tool"
                onClick={() => {
                  setOpen(false);
                  send(
                    "Turn this model into a reusable tool. Study its complete feature history (every sketch, dimension and feature), " +
                      "then ask me which dimensions and choices should become inputs before saving it.",
                  );
                }}
              >
                <IconPart size={13} /> From this model
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

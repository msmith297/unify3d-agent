import { useCallback, useRef, useState } from "react";
import { useOutside } from "./Menu";
import { Caret, IconCancel, IconEdit, IconNewChat, IconPart, IconToolbox, ToolIcon } from "./SwIcons";
import { EMPTY, draftKey, useStore } from "../store";
import { rpc } from "../lib/bridge";
import { confirmAction } from "./Dialog";
import { svgDataUrl } from "../lib/toolIcons";
import type { Tier, UserToolInfo } from "../lib/types";
import { TIERS } from "../lib/models";

function Icon({ t }: { t: UserToolInfo }) {
  return t.iconSvg ? <img src={svgDataUrl(t.iconSvg)} alt="" width={16} height={16} /> : <ToolIcon name={t.icon} />;
}

/**
 * The Workflows menu: the user's larger saved procedures, run here with the current selection and whatever is
 * typed in the composer as inputs. The quick Tools live on the Unify3D ribbon tab; they're listed below the
 * workflows only to edit or delete them.
 */
export function ToolsPalette({ disabled }: { disabled?: boolean }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useOutside(ref, useCallback(() => setOpen(false), []));
  const all = useStore((s) => s.userTools ?? EMPTY);
  const workflows = all.filter((t) => t.kind !== "tool");
  const tools = all.filter((t) => t.kind === "tool");
  const hasDoc = useStore((s) => !!s.docs[s.viewKey]);
  const { runUserTool, deleteUserTool, send, setDraft } = useStore.getState();

  const prefill = (text: string) => {
    setDraft(text);
    useStore.setState((s) => ({ focusTick: s.focusTick + 1, drafts: { ...s.drafts, [draftKey(s)]: text } }));
    setOpen(false);
  };

  const row = (t: UserToolInfo, run: boolean) => (
    <div key={t.name} className="group flex items-start gap-2 border-b border-line/60 px-2 py-1.5 last:border-b-0 hover:bg-accent-soft">
      <span className="mt-0.5 shrink-0">
        <Icon t={t} />
      </span>
      <div className="min-w-0 flex-1">
        <div className="truncate text-[12px] font-semibold text-text">{t.title}</div>
        <div className="line-clamp-2 text-[11px] text-text-3">{t.description}</div>
      </div>
      <div className="flex shrink-0 gap-0.5 opacity-0 transition-opacity group-hover:opacity-100 focus-within:opacity-100">
        <button
          className="sw-tool !h-[22px] !min-w-[22px] !p-0"
          title={`Change this ${t.kind === "tool" ? "tool" : "workflow"} with the agent`}
          onClick={() => prefill(`Update my ${t.kind === "tool" ? "tool" : "workflow"} "${t.name}": `)}
        >
          <IconEdit size={12} />
        </button>
        <button
          className="sw-tool !h-[22px] !min-w-[22px] !p-0"
          title="Delete"
          onClick={() => {
            confirmAction(`Delete this ${t.kind === "tool" ? "tool" : "workflow"}?`, <p>"{t.title}" and its folder are deleted.</p>, "Delete", true).then((ok) => { if (ok) deleteUserTool(t.name); });
          }}
        >
          <IconCancel size={9} />
        </button>
      </div>
      {run || t.kind === "tool" ? (
        <button
          className="sw-btn sw-btn-primary shrink-0 !h-[22px] !px-2"
          disabled={disabled}
          title="Run with the current selection. Text in the message box is passed as notes."
          onClick={() => {
            setOpen(false);
            if (t.kind === "tool") rpc("userTools.run", { name: t.name });
            else runUserTool(t.name);
          }}
        >
          Run
        </button>
      ) : (
        t.agent === false ? (
          <span className="shrink-0 self-center text-[10.5px] text-text-3" title="Runs its macro directly - no agent">
            hard coded
          </span>
        ) : (
          <select
            className="sw-input shrink-0 self-center !h-[20px] px-0.5 text-[10.5px]"
            value={t.intelligence ?? "balanced"}
            title="How much intelligence the agent uses for this tool"
            onChange={(e) => rpc("userTools.setValue", { name: t.name, key: "intelligence", value: e.target.value })}
          >
            {(Object.keys(TIERS) as Tier[]).map((k) => (
              <option key={k} value={k}>
                {TIERS[k].label}
              </option>
            ))}
          </select>
        )
      )}
    </div>
  );

  return (
    <div ref={ref}>
      <button type="button" title="Your workflows" aria-pressed={open} disabled={disabled} onClick={() => setOpen((o) => !o)} className="sw-tool text-[11.5px]">
        <IconToolbox size={14} />
        Workflows
        <Caret open className="opacity-70" />
      </button>
      {open && (
        <div className="absolute bottom-full left-1.5 right-1.5 z-30 mb-1 overflow-hidden rounded-[3px] border border-line-strong bg-card shadow-[0_4px_14px_rgba(0,0,0,0.4)]">
          <div className="sw-rollout-head !min-h-[22px] text-[11.5px]">
            <IconToolbox size={14} />
            <span className="flex-1">Workflows</span>
            <button className="text-[10.5px] font-normal text-text-3 hover:text-text" onClick={() => rpc("userTools.openFolder")} title="Open the folder where workflows and tools are saved">
              Folder
            </button>
          </div>

          <div className="max-h-[50vh] overflow-y-auto">
            {workflows.length === 0 && (
              <p className="px-2.5 py-2 text-[11.5px] text-text-3">
                No workflows yet. Describe a job you repeat, or turn the current model into a workflow, and the agent will write it with you.
              </p>
            )}
            {workflows.map((t) => row(t, true))}

            {tools.length > 0 && (
              <>
                <div className="sw-rollout-head !min-h-[20px] border-t border-line text-[11px]">
                  <span className="flex-1">Tools - on the Unify3D ribbon tab</span>
                </div>
                {tools.map((t) => row(t, false))}
              </>
            )}
          </div>

          <div className="sw-etch flex flex-wrap gap-1 bg-card-2 px-1.5 py-1.5">
            <button className="sw-btn" onClick={() => prefill("Help me create a new workflow that ")}>
              <IconNewChat size={13} /> New workflow…
            </button>
            {hasDoc && (
              <button
                className="sw-btn"
                title="The agent studies this model's full feature history and turns it into a reusable, parametric workflow"
                onClick={() => {
                  setOpen(false);
                  send(
                    "Turn this model into a reusable workflow. Study its complete feature history (every sketch, dimension and feature), " +
                      "then ask me which dimensions and choices should become inputs before saving it.",
                  );
                }}
              >
                <IconPart size={13} /> From this model
              </button>
            )}
            <button className="sw-btn" title="A quick command on the Unify3D ribbon tab that you can put on a hotkey" onClick={() => { setOpen(false); useStore.setState({ panel: "newTool" }); }}>
              <IconNewChat size={13} /> New tool…
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

import { useState, type ReactNode } from "react";
import { BrandMark } from "./Brand";
import { UpdateSettings } from "./Update";
import { stepZoom } from "../lib/zoom";
import { useStore } from "../store";
import { rpc } from "../lib/bridge";
import { EFFORTS, MODELS } from "../lib/models";
import { SW_THEME_NAMES } from "../lib/theme";
import { Caret, IconAgent, IconGear, IconHistory, IconKey, IconOk, IconPart, IconRebuild, IconShield, IconUpdate } from "./SwIcons";
import type { ApprovalMode, Effort, Theme } from "../lib/types";

/** Options page laid out like a SolidWorks PropertyManager: stacked rollouts. */
export function SettingsPanel() {
  const settings = useStore((s) => s.settings);
  const apiKeySource = useStore((s) => s.apiKeySource);
  const host = useStore((s) => s.host);
  const swTheme = useStore((s) => s.swTheme);
  const { saveSettings, setApiKey, setPanel } = useStore.getState();
  const [key, setKey] = useState("");
  const [saved, setSaved] = useState(false);
  const swName = swTheme ? SW_THEME_NAMES[swTheme.theme] : null;

  return (
    <div className="min-h-0 flex-1 overflow-y-auto bg-bg px-1.5 py-1.5">
      <div className="zoom-content mx-auto flex max-w-[520px] flex-col gap-1.5">
        <div className="flex items-center gap-1.5 px-1 pb-0.5">
          <BrandMark size={18} />
          <span className="flex-1 text-[13px] font-semibold text-text">Unify3D Agent Options</span>
          <button onClick={() => setPanel("chat")} className="sw-btn" title="Back to chat">
            <IconOk size={12} /> Done
          </button>
        </div>

        <Rollout title="Anthropic API key" icon={<IconKey size={14} />}>
          <p className="text-[11.5px] text-text-2">
            {apiKeySource === "stored" && "A key is saved for your Windows account, encrypted with DPAPI."}
            {apiKeySource === "environment" && "Using the ANTHROPIC_API_KEY environment variable. Saving a key here overrides it."}
            {apiKeySource === "none" && "Paste an API key from the Claude Console to start chatting. It's encrypted for your Windows account and never stored in your files."}
          </p>
          <form
            className="mt-1.5 flex gap-1"
            onSubmit={async (e) => {
              e.preventDefault();
              if (!key.trim()) return;
              await setApiKey(key.trim());
              setKey("");
              setSaved(true);
              setTimeout(() => setSaved(false), 2000);
            }}
          >
            <input
              type="password"
              value={key}
              onChange={(e) => setKey(e.target.value)}
              placeholder={apiKeySource === "none" ? "sk-ant-…" : "Replace the saved key"}
              autoComplete="off"
              spellCheck={false}
              className="sw-input h-[22px] min-w-0 flex-1 px-1.5 font-mono text-[11.5px]"
            />
            <button type="submit" disabled={!key.trim()} className="sw-btn sw-btn-primary">
              {saved ? "Saved" : "Save"}
            </button>
          </form>
          <div className="mt-1.5 flex gap-3 text-[11px]">
            <button className="text-accent hover:underline" onClick={() => rpc("app.openExternal", { url: "https://platform.claude.com/settings/keys" })}>
              Get a key…
            </button>
            {apiKeySource === "stored" && (
              <button className="text-text-3 hover:text-err" onClick={() => setApiKey("")}>
                Remove saved key
              </button>
            )}
          </div>
        </Rollout>

        <Rollout title="Defaults for new chats" icon={<IconAgent size={14} />}>
          <Field label="Model">
            <Select value={settings.defaultModel} onChange={(v) => saveSettings({ defaultModel: v })} options={MODELS.map((m) => [m.id, `${m.label} - ${m.note}`])} />
          </Field>
          <Field label="Effort">
            <Select<Effort> value={settings.defaultEffort} onChange={(v) => saveSettings({ defaultEffort: v })} options={EFFORTS.map((e) => [e.id, `${e.label} - ${e.note}`])} />
          </Field>
        </Rollout>

        <Rollout title="Modelling" icon={<IconPart size={14} />}>
          <Toggle
            checked={settings.realisticAppearance}
            onChange={(v) => saveSettings({ realisticAppearance: v })}
            label="Give parts real materials and realistic appearances"
            note="The agent assigns the actual engineering material (so mass is right) and a matching finish - colour, texture, coating - to the parts it creates."
          />
          <Toggle
            checked={settings.shareView}
            onChange={(v) => saveSettings({ shareView: v })}
            label="Send a screenshot of my view with each message"
            note="The agent sees exactly what you see, with your selection highlighted. Adds roughly 1,000 tokens per message."
          />
        </Rollout>

        <Rollout title="Running code" icon={<IconShield size={14} />}>
          <div className="flex flex-col">
            {(
              [
                ["ask", "Ask before changing the model", "You approve each code run. Reading the model never asks."],
                ["auto", "Run automatically", "Fastest. Every change is still one Ctrl+Z undo step."],
                ["read_only", "Read-only", "The agent can inspect and explain, but not modify anything."],
              ] as [ApprovalMode, string, string][]
            ).map(([id, label, note]) => (
              <label key={id} className="flex cursor-default items-start gap-2 rounded-[2px] px-1 py-1 hover:bg-card-2">
                <input type="radio" name="approval" checked={settings.approval === id} onChange={() => saveSettings({ approval: id })} className="mt-0.5 accent-[var(--accent)]" />
                <span>
                  <span className="block text-[12px] text-text">{label}</span>
                  <span className="block text-[11px] text-text-3">{note}</span>
                </span>
              </label>
            ))}
          </div>
        </Rollout>

        <Rollout title="While the agent works" icon={<IconRebuild size={14} />}>
          <Toggle
            checked={settings.muteErrorSounds}
            onChange={(v) => saveSettings({ muteErrorSounds: v })}
            label="Silence SolidWorks' rebuild-error sound"
            note="The agent often hits and fixes rebuild errors along the way. The sound comes back as soon as it finishes."
          />
        </Rollout>

        <Rollout title="Chat history" icon={<IconHistory size={14} />}>
          <Toggle
            checked={settings.markDocumentDirty}
            onChange={(v) => saveSettings({ markDocumentDirty: v })}
            label="Mark the document as modified when the chat changes"
            note="SolidWorks then prompts you to save, so the conversation is stored in the file. Unsaved messages are also kept in a local backup."
          />
          <Toggle checked={settings.showProgress} onChange={(v) => saveSettings({ showProgress: v })} label="Show the agent's progress notes" note="Short status lines the model writes between tool calls." />
        </Rollout>

        <Rollout title="Updates" icon={<IconUpdate size={14} />}>
          <UpdateSettings Toggle={Toggle} />
        </Rollout>

        <Rollout title="Appearance" icon={<IconGear size={14} />}>
          <Field label="Theme">
            <Select<Theme>
              value={settings.theme}
              onChange={(v) => saveSettings({ theme: v })}
              options={[
                ["solidworks", `Match SolidWorks${swName ? ` (${swName})` : ""}`],
                ["light", "Light"],
                ["dark", "Dark"],
                ["system", "Match Windows"],
              ]}
            />
          </Field>
          <p className="mt-1 text-[11px] text-text-3">Match SolidWorks follows Tools &gt; Options &gt; Colors &gt; Background and updates when you change it.</p>
          <Field label="Text size">
            <div className="flex items-center gap-1">
              <button type="button" className="sw-btn !h-[22px] !w-[24px] !p-0" title="Smaller (Ctrl -)" onClick={() => saveSettings({ zoom: stepZoom(settings.zoom ?? 1, -1) })}>
                −
              </button>
              <span className="w-[44px] text-center text-[12px] tabular-nums text-text">{Math.round((settings.zoom ?? 1) * 100)}%</span>
              <button type="button" className="sw-btn !h-[22px] !w-[24px] !p-0" title="Larger (Ctrl +)" onClick={() => saveSettings({ zoom: stepZoom(settings.zoom ?? 1, 1) })}>
                +
              </button>
              {(settings.zoom ?? 1) !== 1 && (
                <button type="button" className="sw-btn !h-[22px] !px-2" title="Reset (Ctrl 0)" onClick={() => saveSettings({ zoom: 1 })}>
                  Reset
                </button>
              )}
            </div>
          </Field>
          <p className="mt-1 text-[11px] text-text-3">Or press Ctrl + / Ctrl − (or Ctrl + mouse wheel) in the panel; Ctrl 0 resets.</p>
        </Rollout>

        <div className="flex items-center justify-between px-1 pt-0.5 text-[11px] text-text-3">
          <span>
            Agent {host?.version}
            {host?.solidworksVersion ? ` · SolidWorks ${host.solidworksVersion}` : ""}
          </span>
          <span className="flex gap-3">
            <button className="hover:text-text" onClick={() => location.reload()} title="Reload the panel (F5)">
              Reload panel
            </button>
            <button className="hover:text-text" onClick={() => rpc("app.openLogs")}>
              Open logs
            </button>
          </span>
        </div>
      </div>
    </div>
  );
}

function Rollout({ title, icon, children }: { title: string; icon?: ReactNode; children: ReactNode }) {
  const [open, setOpen] = useState(true);
  return (
    <section className="sw-rollout">
      <button type="button" className={`sw-rollout-head ${open ? "" : "collapsed"}`} onClick={() => setOpen((o) => !o)} aria-expanded={open}>
        {icon}
        <span className="flex-1 text-[12px]">{title}</span>
        <Caret open={open} className="text-text-3" />
      </button>
      {open && <div className="px-2 py-1.5">{children}</div>}
    </section>
  );
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="mt-1 flex items-center gap-2 first:mt-0">
      <span className="w-12 shrink-0 text-[11.5px] text-text-2">{label}</span>
      {children}
    </label>
  );
}

function Select<T extends string>({ value, onChange, options }: { value: T; onChange: (v: T) => void; options: [T, string][] }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value as T)} className="sw-input h-[22px] min-w-0 flex-1 px-1 text-[12px]">
      {options.map(([id, label]) => (
        <option key={id} value={id}>
          {label}
        </option>
      ))}
    </select>
  );
}

function Toggle({ checked, onChange, label, note }: { checked: boolean; onChange: (v: boolean) => void; label: string; note?: string }) {
  return (
    <label className="flex cursor-default items-start gap-2 py-1">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} className="mt-0.5 accent-[var(--accent)]" />
      <span>
        <span className="block text-[12px] text-text">{label}</span>
        {note && <span className="block text-[11px] text-text-3">{note}</span>}
      </span>
    </label>
  );
}

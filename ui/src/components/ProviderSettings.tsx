import { useEffect, useRef, useState } from "react";
import { useStore } from "../store";
import { rpc } from "../lib/bridge";
import { SPEEDS } from "../lib/models";
import { PROVIDERS } from "../agent/providers";
import { errorText } from "../agent/providers/errors";
import type { Provider } from "../agent/providers/types";
import type { Settings } from "../lib/types";
import { IconKey } from "./SwIcons";
import { Rollout } from "./SettingsPanel";

/** "Claude (Anthropic)", "OpenAI": the service, and the account when it has another name. */
export function providerTitle(p: Provider): string {
  return p.label === p.vendor ? p.label : `${p.label} (${p.vendor})`;
}

/** Options > AI providers: one row per provider (docs/providers/design.md 7.7). */
export function AiProviderSettings() {
  return (
    <Rollout title="AI providers" icon={<IconKey size={14} />}>
      <div className="flex flex-col gap-2.5">
        {PROVIDERS.map((p) => (
          <ProviderKeyRow key={p.id} provider={p} />
        ))}
      </div>
    </Rollout>
  );
}

function ProviderKeyRow({ provider: p }: { provider: Provider }) {
  const source = useStore((s) => s.apiKeySources[p.id] ?? "none");
  const focused = useStore((s) => s.keyFocus === p.id);
  const speed = useStore((s) => s.settings.openaiSpeed ?? "standard");
  const { setApiKey, saveSettings } = useStore.getState();
  const [key, setKey] = useState("");
  const [saved, setSaved] = useState(false);
  const [test, setTest] = useState<{ busy: boolean; text?: string; ok?: boolean }>({ busy: false });
  const row = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!focused) return;
    row.current?.scrollIntoView({ block: "nearest" });
    input.current?.focus();
  }, [focused]);

  const runTest = async () => {
    setTest({ busy: true });
    const typed = key.trim();
    const k = typed || (await rpc<{ key: string | null }>("apiKey.get", { provider: p.id }).catch(() => null))?.key;
    if (!k) return setTest({ busy: false, ok: false, text: "No key to test." });
    const r = await p.testKey(k);
    setTest(r.ok ? { busy: false, ok: true, text: "Key works." } : { busy: false, ok: false, text: errorText(r.error) });
  };

  return (
    <div ref={row} className={`rounded-[2px] ${focused ? "outline outline-1 outline-[var(--accent)]" : ""}`} data-provider={p.id}>
      <div className="flex items-baseline gap-2">
        <span className="flex-1 text-[12px] font-semibold text-text">{providerTitle(p)}</span>
        <span className={`text-[11px] ${source === "none" ? "text-text-3" : "text-text-2"}`}>
          {source === "stored" ? "Saved" : source === "environment" ? `Using the ${p.envKey} environment variable` : "Not set"}
        </span>
      </div>
      {source === "none" && <p className="mt-0.5 text-[11px] text-text-3">Paste an API key from {p.vendor}. It's encrypted for your Windows account and never stored in your files.</p>}
      <form
        className="mt-1 flex gap-1"
        onSubmit={async (e) => {
          e.preventDefault();
          if (!key.trim()) return;
          await setApiKey(p.id, key.trim());
          setKey("");
          setTest({ busy: false });
          setSaved(true);
          setTimeout(() => setSaved(false), 2000);
        }}
      >
        <input
          ref={input}
          type="password"
          value={key}
          onChange={(e) => setKey(e.target.value)}
          placeholder={source === "none" ? p.keyPlaceholder : "Replace the saved key"}
          autoComplete="off"
          spellCheck={false}
          aria-label={`${providerTitle(p)} API key`}
          className="sw-input h-[22px] min-w-0 flex-1 px-1.5 font-mono text-[11.5px]"
        />
        <button type="submit" disabled={!key.trim()} className="sw-btn sw-btn-primary">
          {saved ? "Saved" : "Save"}
        </button>
        <button type="button" disabled={test.busy || (!key.trim() && source === "none")} className="sw-btn" onClick={runTest} title="Check the key with a free call">
          {test.busy ? "Testing…" : "Test"}
        </button>
      </form>
      {test.text && <p className={`mt-1 text-[11px] ${test.ok ? "text-text-2" : "text-err"}`}>{test.text}</p>}
      <div className="mt-1 flex flex-wrap gap-x-3 text-[11px]">
        <button type="button" className="text-accent hover:underline" onClick={() => rpc("app.openExternal", { url: p.keysUrl })}>
          Get a key…
        </button>
        {source === "stored" && (
          <button type="button" className="text-text-3 hover:text-err" onClick={() => setApiKey(p.id, "").then(() => setTest({ busy: false }))}>
            Remove saved key
          </button>
        )}
      </div>
      {p.id === "openai" && (
        <label className="mt-1.5 flex items-center gap-2">
          <span className="w-12 shrink-0 text-[11.5px] text-text-2">Speed</span>
          <select
            value={speed}
            onChange={(e) => saveSettings({ openaiSpeed: e.target.value as Settings["openaiSpeed"] })}
            className="sw-input h-[22px] min-w-0 flex-1 px-1 text-[12px]"
          >
            {SPEEDS.map((s) => (
              <option key={s.id} value={s.id}>
                {s.note ? `${s.label}, ${s.note}` : s.label}
              </option>
            ))}
          </select>
        </label>
      )}
      {p.id === "openai" && <p className="mt-0.5 text-[11px] text-text-3">Fast and Ultrafast apply to models that support them.</p>}
    </div>
  );
}

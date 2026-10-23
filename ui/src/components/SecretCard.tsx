import { useState } from "react";
import { IconKey, Spinner } from "./SwIcons";
import { Answered } from "./Answered";
import { useStore, type RunState } from "../store";
import { rpc } from "../lib/bridge";
import { SECRET_SAVED, SECRET_SKIPPED, safeHelpUrl, type SecretAsk } from "../lib/creator";
import type { ToolCall } from "./ToolCard";

/**
 * ask_secret: a masked field for a key or token a plugin needs. Save sends the value straight to the plugin's encrypted
 * options (plugins.setConfig); the agent and the chat only ever see "saved" or "skipped". The value lives in this card
 * until it is saved, and nothing typed in the chat box or spoken answers it.
 */
export function SecretCard({ call, threadId, run }: { call: ToolCall; threadId: string; run: RunState | undefined }) {
  const input = (call.input ?? {}) as Partial<SecretAsk>;
  const secret = run?.question?.toolUseId === call.id ? run.question.secret : undefined;
  if (secret) return <Pending ask={secret} threadId={threadId} />;
  const answer = typeof call.result?.content === "string" ? call.result.content : undefined;
  const summary = answer === undefined ? null : answer === SECRET_SAVED ? "Saved" : answer === SECRET_SKIPPED ? "Skipped" : "Not asked";
  return (
    <Answered question={input.label ?? "A key for a plugin"} answer={summary}>
      <div className="selectable text-[12px] text-text-2">
        {input.label} ({input.plugin}). {call.result?.is_error ? answer : "The value is stored encrypted on this PC and isn't shown in the chat."}
      </div>
    </Answered>
  );
}

function Pending({ ask, threadId }: { ask: SecretAsk & { set: boolean }; threadId: string }) {
  const answerSecret = useStore((s) => s.answerSecret);
  const [value, setValue] = useState("");
  const [show, setShow] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const help = safeHelpUrl(ask.help_url);

  const finish = async (v: string | null) => {
    setBusy(true);
    setError(null);
    try {
      await answerSecret(threadId, v);
      setValue("");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="sw-rollout !border-accent px-2 py-1.5">
      <div className="mb-1 flex items-start gap-2">
        <span className="mt-px shrink-0">
          <IconKey size={15} />
        </span>
        <div className="selectable text-[12px] font-semibold text-text">{ask.label}</div>
      </div>
      <div className="flex flex-col gap-1.5 pl-[22px]">
        <div className="text-[11.5px] text-text-2">
          For the plugin {ask.plugin}. It's stored encrypted on this PC. The agent never sees it.
          {ask.set ? " One is already saved; a new one replaces it." : ""}
        </div>
        {help && (
          <button type="button" className="self-start text-[11.5px] text-accent hover:underline" onClick={() => rpc("app.openExternal", { url: help })} title={help}>
            Where do I get one?
          </button>
        )}
        <form
          className="flex gap-1.5"
          onSubmit={(e) => {
            e.preventDefault();
            if (value.trim() && !busy) void finish(value.trim());
          }}
        >
          <input
            type={show ? "text" : "password"}
            value={value}
            onChange={(e) => setValue(e.target.value)}
            autoComplete="off"
            spellCheck={false}
            autoFocus
            placeholder="Paste it here"
            aria-label={ask.label}
            className="sw-input h-[22px] min-w-0 flex-1 px-1.5 text-[12px]"
          />
          <button type="button" className="sw-btn" onClick={() => setShow((s) => !s)} title={show ? "Hide what you typed" : "Show what you typed"}>
            {show ? "Hide" : "Show"}
          </button>
          <button type="button" className="sw-btn" disabled={busy} onClick={() => void finish(null)} title="Continue without it">
            Skip
          </button>
          <button type="submit" className="sw-btn sw-btn-primary" disabled={busy || !value.trim()} title="Save it for the plugin">
            {busy ? <Spinner size={11} /> : null}
            Save
          </button>
        </form>
        {error && <div className="text-[11.5px] text-warn">Not saved: {error}</div>}
      </div>
    </div>
  );
}

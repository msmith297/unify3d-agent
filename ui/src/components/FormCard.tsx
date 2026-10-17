import { useState } from "react";
import { IconQuestion } from "./SwIcons";
import { Answered } from "./Answered";
import { useStore, type RunState } from "../store";
import type { FormField, FormSpec } from "../lib/types";
import type { ToolCall } from "./ToolCard";

type Value = string | number | boolean | string[];

function initial(f: FormField): Value {
  if (f.default !== undefined && f.default !== null) return f.default as Value;
  if (f.type === "multi") return [];
  if (f.type === "yesno") return false;
  if (f.type === "choice") return f.options?.[0] ?? "";
  return "";
}

/**
 * The agent's questionnaire (ask_form): a compact form in the chat, pre-filled with its proposals so the user can
 * mostly just confirm. Answers go back as JSON keyed by field id.
 */
export function FormCard({ call, threadId, run }: { call: ToolCall; threadId: string; run: RunState | undefined }) {
  const answer = useStore((s) => s.answerQuestion);
  const spec = (call.input ?? { title: "", fields: [] }) as FormSpec;
  const fields = spec.fields ?? [];
  const pending = run?.question?.toolUseId === call.id;
  const [values, setValues] = useState<Record<string, Value>>(() => Object.fromEntries(fields.map((f) => [f.id, initial(f)])));
  const set = (id: string, v: Value) => setValues((s) => ({ ...s, [id]: v }));
  const missing = fields.filter((f) => f.required && (values[f.id] === "" || (Array.isArray(values[f.id]) && (values[f.id] as string[]).length === 0)));

  let answered: Record<string, unknown> | null = null;
  if (typeof call.result?.content === "string") {
    try {
      answered = JSON.parse(call.result.content);
    } catch {
      answered = null;
    }
  }

  if (!pending) {
    const show = (v: unknown) => (v === undefined ? "-" : Array.isArray(v) ? v.join(", ") || "-" : typeof v === "boolean" ? (v ? "Yes" : "No") : String(v) || "-");
    const summary = !answered ? null : answered._skipped ? "Skipped - the agent used its proposals" : fields.slice(0, 3).map((f) => show(answered?.[f.id])).join(" · ") + (fields.length > 3 ? " …" : "");
    return (
      <Answered question={spec.title} answer={summary}>
        <div className="flex flex-col gap-1">
          {fields.map((f) => (
            <div key={f.id} className="text-[11.5px]">
              <span className="text-text-3">{f.label}: </span>
              <span className="selectable text-text-2">{show(answered?.[f.id])}</span>
            </div>
          ))}
        </div>
      </Answered>
    );
  }

  return (
    <div className={`sw-rollout ${pending ? "!border-accent" : ""}`}>
      <div className="sw-rollout-head !font-semibold">
        <IconQuestion size={15} />
        <span className="flex-1 text-[12px]">{spec.title}</span>
      </div>
      <div className="flex flex-col gap-2 px-2.5 py-2">
        {spec.intro && <p className="selectable text-[12px] text-text-2">{spec.intro}</p>}
        {fields.map((f) => {
          const v = pending ? values[f.id] : (answered?.[f.id] as Value | undefined);
          return (
            <div key={f.id} className="flex flex-col gap-1">
              <div className="text-[11.5px] font-semibold text-text">
                {f.label}
                {f.required && <span className="text-err"> *</span>}
              </div>
              {f.help && <div className="-mt-0.5 text-[11px] text-text-3">{f.help}</div>}
              {!pending ? (
                <div className="selectable text-[12px] text-text-2">{v === undefined ? "-" : Array.isArray(v) ? v.join(", ") || "-" : typeof v === "boolean" ? (v ? "Yes" : "No") : String(v) || "-"}</div>
              ) : f.type === "choice" ? (
                <div className="flex flex-wrap gap-1">
                  {(f.options ?? []).map((o) => (
                    <button key={o} type="button" aria-pressed={v === o} onClick={() => set(f.id, o)} className={`sw-btn !h-auto min-h-[22px] whitespace-normal py-0.5 text-left ${v === o ? "sw-btn-primary" : ""}`}>
                      {o}
                    </button>
                  ))}
                </div>
              ) : f.type === "multi" ? (
                <div className="flex flex-col gap-0.5">
                  {(f.options ?? []).map((o) => {
                    const list = (v as string[]) ?? [];
                    return (
                      <label key={o} className="flex items-start gap-1.5 text-[12px] text-text">
                        <input type="checkbox" className="mt-0.5 accent-[var(--accent)]" checked={list.includes(o)} onChange={(e) => set(f.id, e.target.checked ? [...list, o] : list.filter((x) => x !== o))} />
                        {o}
                      </label>
                    );
                  })}
                </div>
              ) : f.type === "yesno" ? (
                <div className="flex gap-1">
                  {[true, false].map((b) => (
                    <button key={String(b)} type="button" onClick={() => set(f.id, b)} className={`sw-btn ${v === b ? "sw-btn-primary" : ""}`}>
                      {b ? "Yes" : "No"}
                    </button>
                  ))}
                </div>
              ) : f.type === "number" ? (
                <input type="number" value={String(v ?? "")} onChange={(e) => set(f.id, e.target.value === "" ? "" : Number(e.target.value))} className="sw-input h-[22px] w-[120px] px-1.5 text-[12px]" />
              ) : (
                <textarea value={String(v ?? "")} onChange={(e) => set(f.id, e.target.value)} rows={2} className="sw-input w-full resize-y px-1.5 py-1 text-[12px]" placeholder="Type, or press Win+H to dictate" />
              )}
            </div>
          );
        })}
      </div>
      {pending ? (
        <div className="sw-etch flex items-center justify-end gap-1.5 bg-card-2 px-2 py-1.5">
          {missing.length > 0 && <span className="mr-auto text-[11px] text-text-3">Fill in the required fields</span>}
          <button className="sw-btn" onClick={() => answer(threadId, JSON.stringify({ _skipped: true, note: "The user skipped the form - use your proposed defaults." }))}>
            Skip
          </button>
          <button className="sw-btn sw-btn-primary" disabled={missing.length > 0} onClick={() => answer(threadId, JSON.stringify(values))}>
            Submit
          </button>
        </div>
      ) : (
        !answered && <div className="px-2.5 pb-2 text-[11.5px] text-text-3">Not answered</div>
      )}
    </div>
  );
}

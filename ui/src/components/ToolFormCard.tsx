import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { X } from "lucide-react";
import { Caret, IconAttach, IconCancel, IconEye, IconSelect, IconToolbox, IconWarning, Spinner, ToolIcon } from "./SwIcons";
import { useStore } from "../store";
import { useToolForm, type ToolFormInput, type ToolFormState } from "../lib/toolForm";
import { TIER_LABELS, acceptText, chipText, formatNumber, parseNumber, stepNumber, tableHint, takesText, visibleGroups } from "../lib/toolFormLogic";
import { svgDataUrl } from "../lib/toolIcons";

/**
 * A tool's inputs in the chat (Tools/ToolFormBridge.cs), in place of its PropertyManager page: the same groups, inputs,
 * units, data choices and checks, drawn as rollouts in one card above the composer. OK runs the tool; its output then
 * flows into the chat as before. Esc cancels, Ctrl+Enter is OK.
 */
export function ToolFormCard() {
  const form = useToolForm((s) => s.form);
  const ref = useRef<HTMLDivElement>(null);
  const formId = form?.formId;

  // A new form scrolls into view (the chat may be scrolled up).
  useEffect(() => {
    if (formId) ref.current?.scrollIntoView({ block: "nearest", behavior: "smooth" });
  }, [formId]);

  // The first preview, once the add-in has compiled preview.cs (previews only run inside an RPC, never on a timer there).
  const previewReady = !!form?.preview?.available && !!form.preview.live;
  const asked = useRef<string | null>(null);
  useEffect(() => {
    if (formId && previewReady && asked.current !== formId) {
      asked.current = formId;
      void useToolForm.getState().change({ preview: true });
    }
  }, [formId, previewReady]);

  if (!form) return null;
  return (
    <div ref={ref}>
      <Card form={form} />
    </div>
  );
}

/** Without a chat yet, the form takes the chat's place (with the composer still below it). */
export function ToolFormPane() {
  return (
    <div className="min-h-0 flex-1 overflow-y-auto px-3 pb-4 pt-3">
      <div className="zoom-content mx-auto max-w-[780px]">
        <ToolFormCard />
      </div>
    </div>
  );
}

function Card({ form }: { form: ToolFormState }) {
  const { ok, cancel, change, closing, error } = useToolForm();
  const info = useStore((s) => s.userTools.find((t) => t.name === form.tool));
  const [more, setMore] = useState(false);
  const groups = visibleGroups(form);

  const onKey = (e: KeyboardEvent) => {
    if (e.key === "Escape") {
      e.preventDefault();
      void cancel();
    } else if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      // Leaving the box commits what was typed in it (number and text boxes wait for a pause); ok() waits for that change.
      const focused = document.activeElement;
      if (focused instanceof HTMLElement && e.currentTarget.contains(focused)) focused.blur();
      void ok(); // checks okEnabled once that change is in
    }
  };

  return (
    <section className="sw-rollout !border-accent" onKeyDown={onKey} aria-label={`${form.title} inputs`}>
      <div className="sw-rollout-head">
        {info?.iconSvg ? <img src={svgDataUrl(info.iconSvg)} alt="" width={16} height={16} /> : info?.icon ? <ToolIcon name={info.icon} /> : <IconToolbox size={16} />}
        <span className="min-w-0 flex-1 truncate text-[12px]" title={form.title}>
          {form.title}
        </span>
        <button type="button" onClick={() => void cancel()} className="text-text-3 hover:text-text" title="Cancel (Esc)" disabled={!!closing}>
          <X size={15} />
        </button>
      </div>

      <div className="flex flex-col gap-1.5 px-2 py-1.5">
        {form.description && (
          <p className={`selectable text-[11.5px] leading-snug text-text-2 ${more ? "" : "line-clamp-3"}`} onClick={() => setMore((m) => !m)} title={more ? undefined : "Show all"}>
            {form.description}
          </p>
        )}
        {(form.message || error) && (
          <div className="flex items-start gap-1.5 text-[11.5px] text-warn" aria-live="polite">
            <span className="mt-px shrink-0">
              <IconWarning size={13} />
            </span>
            <span className="selectable">{error ?? form.message}</span>
          </div>
        )}
        {groups.map((g) => (
          <Group key={g.id} id={g.id} title={g.title} expanded={g.expanded} hasError={g.inputs.some((i) => i.visible && !!i.error)}>
            {g.inputs.filter((i) => i.visible).map((i) => (
              <Field key={i.id} input={i} blocking={form.okField === i.id} />
            ))}
          </Group>
        ))}
        {form.agent && (
          <Group id="__agent" title="Unify3D Agent" expanded>
            <label className="flex items-center gap-2 text-[11.5px] text-text-2" title="How much intelligence the agent uses for this tool: Fast (quickest), Balanced, High (careful), or Max (hardest problems).">
              <span className="shrink-0">Intelligence</span>
              <select value={form.agent.tier} onChange={(e) => void change({ tier: e.target.value })} className="sw-input h-[22px] min-w-0 flex-1 px-1 text-[12px]">
                {form.agent.tiers.map((t) => (
                  <option key={t} value={t}>
                    {TIER_LABELS[t] ?? t}
                  </option>
                ))}
              </select>
            </label>
          </Group>
        )}
        {form.preview?.message && <div className="text-[11px] text-text-3">{form.preview.message}</div>}
      </div>

      <div className="sw-etch flex flex-wrap items-center justify-end gap-1.5 bg-card-2 px-2 py-1.5">
        <span className={`mr-auto min-w-0 flex-1 text-[11px] ${form.okEnabled ? "text-text-3" : "text-warn"}`}>
          {form.okEnabled ? (form.agent ? "OK hands the inputs to the agent in this chat." : "OK runs the tool (Ctrl+Z undoes it).") : form.okReason}
        </span>
        {form.preview?.available && !form.preview.live && (
          <button type="button" className="sw-btn" onClick={() => void change({ preview: true })} title="Draw the preview for these values">
            <IconEye size={13} /> Preview
          </button>
        )}
        <button type="button" className="sw-btn" onClick={() => void cancel()} disabled={!!closing}>
          Cancel
        </button>
        <button type="button" className="sw-btn sw-btn-primary" onClick={() => void ok()} disabled={!form.okEnabled || !!closing} title="Run the tool (Ctrl+Enter)">
          {closing === "ok" ? <Spinner size={11} /> : null}
          OK
        </button>
      </div>
    </section>
  );
}

function Group({ id, title, expanded, hasError, children }: { id: string; title: string; expanded: boolean; hasError?: boolean; children: ReactNode }) {
  const stored = useToolForm((s) => s.groupOpen[id]);
  const toggle = useToolForm((s) => s.toggleGroup);
  const open = stored ?? expanded;
  return (
    <section className="sw-rollout">
      <button type="button" className={`sw-rollout-head ${open ? "" : "collapsed"}`} onClick={() => toggle(id, !open)} aria-expanded={open}>
        <span className="flex-1 text-[12px]">{title}</span>
        {hasError && !open && <IconWarning size={12} />}
        <Caret open={open} className="text-text-3" />
      </button>
      {open && <div className="flex flex-col gap-2 px-2 py-1.5">{children}</div>}
    </section>
  );
}

// ---- one input ----

function Field({ input, blocking }: { input: ToolFormInput; blocking: boolean }) {
  const change = useToolForm((s) => s.change);
  if (input.type === "info") return <div className="selectable text-[11.5px] leading-snug text-text-3" title={input.tip ?? undefined}>{input.text}</div>;
  if (input.type === "checkbox") {
    return (
      <div>
        <label className="flex cursor-default items-start gap-1.5 text-[12px] text-text" title={input.tip ?? undefined}>
          <input type="checkbox" className="mt-0.5 accent-[var(--accent)]" checked={input.value === true} onChange={(e) => void change({ id: input.id, value: e.target.checked })} />
          {input.label}
        </label>
        <Notes input={input} blocking={blocking} />
      </div>
    );
  }
  return (
    <div className="flex flex-col gap-1">
      <Label input={input} />
      <div className={input.enabled ? "" : "pointer-events-none opacity-50"}>
        <Control input={input} />
      </div>
      <Notes input={input} blocking={blocking} />
    </div>
  );
}

/** The caption; an Optional input's caption is the switch that turns it on. */
function Label({ input }: { input: ToolFormInput }) {
  const change = useToolForm((s) => s.change);
  const text = (
    <>
      {input.label}
      {input.required && input.type !== "selection" && <span className="text-err"> *</span>}
    </>
  );
  if (input.optional) {
    return (
      <label className="flex cursor-default items-center gap-1.5 text-[11.5px] font-semibold text-text" title={input.tip ?? undefined}>
        <input type="checkbox" className="accent-[var(--accent)]" checked={!!input.on} onChange={(e) => void change({ id: input.id, on: e.target.checked })} />
        {text}
      </label>
    );
  }
  return (
    <div className="text-[11.5px] font-semibold text-text" title={input.tip ?? undefined}>
      {text}
    </div>
  );
}

function Notes({ input, blocking }: { input: ToolFormInput; blocking: boolean }) {
  const change = useToolForm((s) => s.change);
  const hint = tableHint(input);
  return (
    <>
      {hint && (
        <div className="flex items-center gap-1.5 text-[11px] text-text-3">
          <span>{hint} - edited</span>
          <button type="button" className="text-accent hover:underline" onClick={() => void change({ id: input.id, value: input.table })}>
            Use table value
          </button>
        </div>
      )}
      {input.error && <div className={`text-[11px] ${blocking ? "text-warn" : "text-err"}`}>{input.error}</div>}
    </>
  );
}

function Control({ input }: { input: ToolFormInput }) {
  const change = useToolForm((s) => s.change);
  switch (input.type) {
    case "number":
    case "integer":
      return (
        <div className="flex flex-wrap items-center gap-1.5">
          <NumberBox input={input} value={typeof input.value === "number" ? input.value : null} onCommit={(v) => void change({ id: input.id, value: v })} />
          <Unit input={input} />
        </div>
      );
    case "size": {
      const values = Array.isArray(input.value) ? (input.value as number[]) : [];
      const names = ["Width", "Depth", "Thickness"];
      return (
        <div className="flex flex-wrap items-center gap-1">
          {values.map((v, k) => (
            <span key={k} className="flex items-center gap-1">
              {k > 0 && <span className="text-[11px] text-text-3">×</span>}
              <NumberBox input={input} value={v} narrow title={names[k]} onCommit={(n) => void change({ id: input.id, index: k, value: n })} />
            </span>
          ))}
          <Unit input={input} />
        </div>
      );
    }
    case "choice":
      return (input.choices ?? []).length === 0 ? (
        <div className="text-[11.5px] text-text-3">Nothing to pick for these settings.</div>
      ) : (
        <select
          value={typeof input.value === "string" ? input.value : ""}
          onChange={(e) => void change({ id: input.id, value: e.target.value })}
          className="sw-input h-[22px] w-full min-w-0 px-1 text-[12px]"
          title={input.tip ?? undefined}
        >
          {input.value === null && <option value="">(none)</option>}
          {(input.choices ?? []).map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </select>
      );
    case "options":
      return (
        <div className="flex flex-col">
          {(input.choices ?? []).map((c) => (
            <label key={c} className="flex cursor-default items-start gap-1.5 rounded-[2px] px-0.5 py-0.5 hover:bg-card-2">
              <input type="radio" className="mt-[3px] accent-[var(--accent)]" checked={input.value === c} onChange={() => void change({ id: input.id, value: c })} />
              <span className="text-[12px] text-text">{c}</span>
            </label>
          ))}
        </div>
      );
    case "text":
      return <TextBox input={input} />;
    case "files":
      return <Files input={input} />;
    case "selection":
      return <Selection input={input} />;
    default:
      return null;
  }
}

/** The unit after a number: a dropdown when the input offers a choice of units. */
function Unit({ input }: { input: ToolFormInput }) {
  const change = useToolForm((s) => s.change);
  if (input.unitChoices && input.unitChoices.length > 1) {
    return (
      <select value={input.unit ?? ""} onChange={(e) => void change({ id: input.id, unit: e.target.value })} className="sw-input h-[22px] min-w-0 max-w-full px-1 text-[12px]" title={`Unit of ${input.label}`}>
        {input.unitChoices.map((u) => (
          <option key={u} value={u}>
            {u}
          </option>
        ))}
      </select>
    );
  }
  return input.unit ? <span className="text-[11.5px] text-text-2">{input.unit}</span> : null;
}

/**
 * A number box: what the user types is kept as typed and sent after a short pause, on Enter or when the box is left (the
 * add-in answers with the value it took: kept in range, rounded for integers). Arrow keys step.
 */
function NumberBox({ input, value, onCommit, narrow, title }: { input: ToolFormInput; value: number | null; onCommit: (v: number) => void; narrow?: boolean; title?: string }) {
  const [draft, setDraft] = useState<string | null>(null);
  const timer = useRef<number>(0);
  useEffect(() => () => clearTimeout(timer.current), []);
  const commit = (text: string) => {
    clearTimeout(timer.current);
    const n = parseNumber(text);
    if (n !== null && n !== value) onCommit(n);
  };
  const bad = draft !== null && parseNumber(draft) === null;
  return (
    <input
      type="text"
      inputMode="decimal"
      value={draft ?? formatNumber(value)}
      title={title ?? input.tip ?? undefined}
      aria-label={title ? `${input.label} - ${title}` : input.label}
      aria-invalid={bad || !!input.error}
      onChange={(e) => {
        const text = e.target.value;
        setDraft(text);
        clearTimeout(timer.current);
        timer.current = window.setTimeout(() => commit(text), 450);
      }}
      onBlur={() => {
        if (draft !== null) commit(draft);
        setDraft(null);
      }}
      onKeyDown={(e) => {
        if (e.key === "Enter" && !e.ctrlKey && !e.metaKey) {
          e.preventDefault();
          if (draft !== null) commit(draft);
          setDraft(null);
        } else if ((e.key === "ArrowUp" || e.key === "ArrowDown") && value !== null) {
          e.preventDefault();
          const next = stepNumber(parseNumber(draft ?? "") ?? value, input.step, e.key === "ArrowUp" ? 1 : -1, input.min, input.max);
          setDraft(null);
          onCommit(next);
        }
      }}
      className={`sw-input h-[22px] px-1.5 text-right text-[12px] tabular-nums ${narrow ? "w-[64px]" : "w-[96px] min-w-0 max-w-full"} ${bad || input.error ? "!border-err" : ""}`}
    />
  );
}

function TextBox({ input }: { input: ToolFormInput }) {
  const change = useToolForm((s) => s.change);
  const [draft, setDraft] = useState<string | null>(null);
  const timer = useRef<number>(0);
  useEffect(() => () => clearTimeout(timer.current), []);
  const value = typeof input.value === "string" ? input.value : "";
  const commit = (text: string) => {
    clearTimeout(timer.current);
    if (text !== value) void change({ id: input.id, value: text });
  };
  return (
    <textarea
      value={draft ?? value}
      rows={2}
      title={input.tip ?? undefined}
      placeholder="Type, or press Win+H to dictate"
      onChange={(e) => {
        const text = e.target.value;
        setDraft(text);
        clearTimeout(timer.current);
        timer.current = window.setTimeout(() => commit(text), 600);
      }}
      onBlur={() => {
        if (draft !== null) commit(draft);
        setDraft(null);
      }}
      className="sw-input w-full resize-y px-1.5 py-1 text-[12px]"
    />
  );
}

function Files({ input }: { input: ToolFormInput }) {
  const { change, pick } = useToolForm();
  const files = input.files ?? [];
  return (
    <div className="flex flex-col gap-1">
      {files.map((f, i) => (
        <div key={f.path} className="flex items-center gap-1.5 text-[11.5px] text-text-2" title={f.path}>
          <IconAttach size={12} /> <span className="min-w-0 flex-1 truncate">{f.name}</span>
          <button type="button" className="sw-tool !h-5 !min-w-5" onClick={() => void change({ id: input.id, remove: i })} title="Remove">
            <IconCancel size={10} />
          </button>
        </div>
      ))}
      <div className="flex gap-1">
        <button type="button" className="sw-btn" onClick={() => void pick(input.id)} title={`Add ${acceptText(input.accept)}`}>
          <IconAttach size={12} /> {files.length && input.multiple === false ? "Change…" : "Add file…"}
        </button>
        {files.length > 1 && (
          <button type="button" className="sw-btn" onClick={() => void change({ id: input.id, clear: true })}>
            Clear
          </button>
        )}
      </div>
    </div>
  );
}

/**
 * A selection input as a list box of chips. "Use selection" takes what is selected in SolidWorks now; while the box is
 * active (clicked, outlined) it follows the selection as the user picks, like a PropertyManager selection box.
 */
function Selection({ input }: { input: ToolFormInput }) {
  const { change, pick, setFocused } = useToolForm();
  const active = useToolForm((s) => s.focused === input.id);
  const items = input.items ?? [];
  const multiple = input.multiple !== false;
  return (
    <div className="flex flex-col gap-1">
      <div
        role="listbox"
        tabIndex={0}
        aria-label={input.label}
        onFocus={() => setFocused(input.id)}
        onClick={() => setFocused(input.id)}
        className={`flex min-h-[26px] flex-wrap items-center gap-1 rounded-[2px] border px-1 py-0.5 ${active ? "border-accent bg-accent-soft" : "border-input-border bg-[var(--input-bg)]"}`}
        title={input.tip ?? undefined}
      >
        {items.length === 0 ? (
          <span className="px-0.5 text-[11px] text-text-3">{active ? `Select ${takesText(input.filter, multiple)} in SolidWorks` : `Click here, then select ${takesText(input.filter, multiple)}`}</span>
        ) : (
          items.map((it, i) => (
            <span key={i} className="flex max-w-[180px] items-center gap-1 rounded-[3px] border border-line bg-card-2 py-px pl-1.5 pr-0.5 text-[11px] text-text-2" title={it.detail ?? undefined}>
              <span className="truncate">{chipText(it)}</span>
              <button
                type="button"
                className="shrink-0 text-text-3 hover:text-text"
                onClick={(e) => {
                  e.stopPropagation();
                  void change({ id: input.id, remove: i });
                }}
                title="Remove"
              >
                <X size={11} />
              </button>
            </span>
          ))
        )}
      </div>
      <div className="flex items-center gap-1">
        <button type="button" className="sw-btn" onClick={() => void pick(input.id)} title="Take what is selected in SolidWorks now">
          <IconSelect size={12} /> Use selection
        </button>
        {items.length > 1 && (
          <button type="button" className="sw-btn" onClick={() => void change({ id: input.id, clear: true })}>
            Clear
          </button>
        )}
        {active && (
          <button type="button" className="ml-auto text-[11px] text-text-3 hover:text-text" onClick={() => setFocused(null)}>
            Done selecting
          </button>
        )}
      </div>
    </div>
  );
}

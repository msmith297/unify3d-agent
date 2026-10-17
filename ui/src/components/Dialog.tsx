import { useEffect, useRef, useState, type ReactNode } from "react";
import { X } from "lucide-react";
import { create } from "zustand";
import { IconWarning, Spinner } from "./SwIcons";

/**
 * The panel's own confirmation dialog, laid out like the update dialog (window.confirm shows a browser box titled
 * "swagent.app says"). `ask()` resolves to the chosen action's value, or null when cancelled (Esc, backdrop, Cancel).
 * An action with `run` keeps the dialog open with a spinner while it works; if it returns a message, the dialog
 * shows it and stays open.
 */
export interface DialogAction<T extends string = string> {
  value: T;
  label: string;
  primary?: boolean;
  danger?: boolean;
  run?: () => Promise<string | void>;
}

export interface DialogSpec<T extends string = string> {
  title: string;
  message: ReactNode;
  actions: DialogAction<T>[];
  cancelLabel?: string;
  icon?: ReactNode;
}

interface DialogState {
  open: (DialogSpec & { resolve: (v: string | null) => void }) | null;
}

const useDialog = create<DialogState>(() => ({ open: null }));

export function ask<T extends string>(spec: DialogSpec<T>): Promise<T | null> {
  return new Promise((resolve) => {
    useDialog.getState().open?.resolve(null);
    useDialog.setState({ open: { ...(spec as DialogSpec), resolve: resolve as (v: string | null) => void } });
  });
}

/** Confirmation with a single action. */
export async function confirmAction(title: string, message: ReactNode, label: string, danger = false): Promise<boolean> {
  return (await ask({ title, message, actions: [{ value: "ok", label, primary: !danger, danger }] })) === "ok";
}

export function DialogHost() {
  const open = useDialog((s) => s.open);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const defaultRef = useRef<HTMLButtonElement>(null);

  const close = (v: string | null) => {
    if (!open || (busy && v === null)) return;
    useDialog.setState({ open: null });
    open.resolve(v);
  };

  useEffect(() => {
    setBusy(null);
    setError(null);
    if (!open) return;
    defaultRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        e.preventDefault();
        e.stopPropagation();
        close(null);
      }
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  if (!open) return null;

  const choose = async (a: DialogAction) => {
    if (!a.run) return close(a.value);
    setBusy(a.value);
    setError(null);
    try {
      const problem = await a.run();
      if (problem) {
        setError(problem);
        setBusy(null);
        return;
      }
    } catch (e: any) {
      setError(String(e?.message ?? e));
      setBusy(null);
      return;
    }
    setBusy(null);
    useDialog.setState({ open: null });
    open.resolve(a.value);
  };

  const defaultIndex = open.actions.findIndex((a) => a.primary); // none for a destructive confirmation
  return (
    <div className="absolute inset-0 z-40 flex items-start justify-center bg-black/40 p-3 pt-10" onClick={() => close(null)}>
      <div
        role="dialog"
        aria-modal="true"
        aria-label={open.title}
        className="sw-rollout w-full max-w-[420px] !bg-card shadow-[0_8px_24px_rgba(0,0,0,0.45)]"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="sw-rollout-head">
          {open.icon ?? <IconWarning size={15} />}
          <span className="flex-1 text-[12px]">{open.title}</span>
          <button className="text-text-3 hover:text-text" onClick={() => close(null)} title="Close" disabled={!!busy}>
            <X size={13} />
          </button>
        </div>
        <div className="space-y-2 px-2.5 py-2 text-[12px] text-text-2">
          {open.message}
          {error && <p className="text-err">{error}</p>}
        </div>
        <div className="sw-etch flex flex-wrap justify-end gap-1.5 bg-card-2 px-2 py-1.5">
          <button className="sw-btn" disabled={!!busy} onClick={() => close(null)}>
            {open.cancelLabel ?? "Cancel"}
          </button>
          {open.actions.map((a, i) => (
            <button
              key={a.value}
              ref={i === defaultIndex ? defaultRef : undefined}
              disabled={!!busy}
              onClick={() => choose(a)}
              className={`sw-btn ${a.primary ? "sw-btn-primary" : ""} ${a.danger ? "!text-err" : ""}`}
            >
              {busy === a.value && <Spinner size={12} />}
              {a.label}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

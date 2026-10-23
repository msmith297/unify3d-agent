/**
 * The tool form open in the panel (Tools/ToolFormBridge.cs), in its own small store: a ribbon tool, /run or the Workflows
 * palette starts a tool in the add-in (ToolRunner), which shows its inputs here (event toolForm.show) unless the user chose
 * the PropertyManager page (setting toolInputs: "page"). Every change goes to the add-in, which answers with the whole new
 * state; OK hands the inputs back to ToolRunner, and the run continues there exactly as after the page's OK.
 */
import { create } from "zustand";
import { on, rpc } from "./bridge";
import { useStore } from "../store";
import type { ToolFormChange, ToolFormProfileAction, ToolFormState } from "./toolFormLogic";

export type { ToolFormChange, ToolFormInput, ToolFormGroup, ToolFormProfile, ToolFormProfileAction, ToolFormState } from "./toolFormLogic";

interface ToolFormStore {
  form: ToolFormState | null;
  /** OK or Cancel is on its way to the add-in. */
  closing: false | "ok" | "cancel";
  /** An RPC failed (the form was closed in the add-in, ...). */
  error: string | null;
  /** The selection input that follows the SolidWorks selection while it is focused, like a PropertyManager selection box. */
  focused: string | null;
  /** Rollouts the user opened or closed, by group id (per form; the add-in's "expanded" otherwise). */
  groupOpen: Record<string, boolean>;
  change(change: ToolFormChange): Promise<void>;
  /** Selection inputs: "Use selection" replaces the list; follow (the input is focused and the selection changed) adds to it. */
  pick(id: string, follow?: boolean): Promise<void>;
  /** The Profile row: load, save, update and the menu's actions; the add-in answers with the new state. */
  profile(action: ToolFormProfileAction): Promise<void>;
  ok(): Promise<void>;
  cancel(): Promise<void>;
  setFocused(id: string | null): void;
  toggleGroup(id: string, open: boolean): void;
}

/** Only the newest answer is shown: a slow reply to an earlier change must not undo a later one. */
let seq = 0;
/** Changes on their way to the add-in: OK waits for them, so the tool runs with what the user typed last. */
const inflight = new Set<Promise<void>>();

export const useToolForm = create<ToolFormStore>((set, get) => {
  /** Send to the add-in and show the state it answers with (when it is still this form and the newest request). */
  const send = (method: string, params: Record<string, unknown>): Promise<void> => {
    const form = get().form;
    if (!form) return Promise.resolve();
    const mine = ++seq;
    const p = (async () => {
      try {
        const state = await rpc<ToolFormState | null>(method, { formId: form.formId, ...params });
        if (mine === seq && state && get().form?.formId === state.formId) set({ form: state, error: null });
      } catch (err: any) {
        if (mine === seq) set({ error: err?.message ?? String(err) });
      }
    })();
    inflight.add(p);
    void p.finally(() => inflight.delete(p));
    return p;
  };

  return {
    form: null,
    closing: false,
    error: null,
    focused: null,
    groupOpen: {},
    change: (change) => send("toolForm.change", change),
    pick: (id, follow) => send("toolForm.pick", follow ? { id, follow: true } : { id }),
    profile: (action) => send("toolForm.profile", action),
    async ok() {
      const form = get().form;
      if (!form || get().closing) return;
      set({ closing: "ok" });
      // A value just committed (a box left by clicking OK, or Ctrl+Enter) must reach the add-in before OK does.
      await Promise.all([...inflight]);
      if (get().form?.formId !== form.formId) return;
      if (!get().form?.okEnabled) {
        set({ closing: false }); // the value just sent left something to fix: the form says what
        return;
      }
      seq++;
      try {
        const r = await rpc<{ closed: boolean; form?: ToolFormState }>("toolForm.ok", { formId: form.formId });
        if (r?.closed) close(form.formId);
        else set({ form: r?.form ?? form, closing: false });
      } catch (err: any) {
        set({ error: err?.message ?? String(err), closing: false });
      }
    },
    async cancel() {
      const form = get().form;
      if (!form) return;
      seq++;
      set({ closing: "cancel" });
      // Closed here whatever the add-in says: a form it no longer has is closed anyway.
      await rpc("toolForm.cancel", { formId: form.formId }).catch(() => undefined);
      close(form.formId);
    },
    setFocused: (id) => set({ focused: id }),
    toggleGroup: (id, open) => set((s) => ({ groupOpen: { ...s.groupOpen, [id]: open } })),
  };

  function close(formId: string) {
    if (get().form?.formId === formId) set({ form: null, closing: false, focused: null, groupOpen: {}, error: null });
  }
});

/** A form (new, or the same one again with its preview compiled) to show. */
function show(state: ToolFormState) {
  const s = useToolForm.getState();
  if (s.form?.formId === state.formId) {
    useToolForm.setState({ form: state });
    return;
  }
  seq++;
  useToolForm.setState({ form: state, closing: false, error: null, focused: null, groupOpen: {} });
  // The form is in the chat: bring the chat back from Options or the new-tool screen.
  if (useStore.getState().panel !== "chat") useStore.setState({ panel: "chat" });
}

on("toolForm.show", (state: ToolFormState) => {
  if (state?.formId) show(state);
});

on("toolForm.closed", ({ formId, reason }: { formId: string; reason?: string }) => {
  const form = useToolForm.getState().form;
  if (form?.formId !== formId) return;
  useToolForm.setState({ form: null, closing: false, focused: null, groupOpen: {}, error: null });
  // Another tool taking its place shows its own form; anything else is worth a word.
  if (reason && !/^Another tool/.test(reason)) useStore.setState({ notice: `${form.title}: ${reason}` });
});

// A focused selection input follows what the user selects in SolidWorks (the add-in reads the selection in the RPC): new
// entities are added, as in a PropertyManager selection box; clicking empty space or an entity it doesn't take keeps the list.
on("selection.changed", () => {
  const { form, focused, pick, closing } = useToolForm.getState();
  if (!form || !focused || closing) return;
  const input = form.groups.flatMap((g) => g.inputs).find((i) => i.id === focused);
  if (input?.type === "selection" && input.visible) void pick(focused, true);
});

// After a panel reload the add-in may still be waiting on a form (ToolRunner): show it again.
void rpc<ToolFormState[] | null>("toolForm.list")
  .then((list) => {
    if (list?.[0]?.formId && !useToolForm.getState().form) show(list[0]);
  })
  .catch(() => undefined);

import { create } from "zustand";
import { on, rpc } from "./lib/bridge";
import { syncRibbonIcons } from "./lib/toolIcons";
import { DEFAULT_SETTINGS, SETTINGS_VERSION } from "./lib/models";
import type {
  Attachment,
  ChatData,
  ContentBlockParam,
  DocInfo,
  Effort,
  HostState,
  MessageParam,
  SelectionItem,
  Settings,
  SwThemeInfo,
  UpdateInfo,
  UserToolInfo,
  Thread,
  ToolDef,
  ToolRunResult,
  TurnMeta,
  Usage,
} from "./lib/types";
import { WRITE_TOOLS, runAgent, type ApprovalDecision, type ApprovalRequest, type LiveBlock } from "./agent/runAgent";

export interface ChatSlot {
  docKey: string;
  docId: string;
  data: ChatData;
  recovered: boolean;
}

export interface RunState {
  docKey: string;
  threadId: string;
  live: LiveBlock[] | null;
  runningToolId: string | null;
  approval: (ApprovalRequest & { resolve: (d: ApprovalDecision) => void }) | null;
  question: { toolUseId: string; question: string; options: string[]; resolve: (answer: string) => void } | null;
  controller: AbortController;
}

interface State {
  booted: boolean;
  bootError: string | null;
  host: HostState | null;
  settings: Settings;
  apiKeySource: HostState["apiKeySource"];
  swTheme: SwThemeInfo | null;
  tools: ToolDef[];
  userTools: UserToolInfo[];
  /** Update feed state; the header shows an update button while a newer version exists. */
  update: UpdateInfo | null;
  updateDialog: boolean;
  docs: Record<string, DocInfo>;
  activeDocKey: string | null;
  viewKey: string;
  chats: Record<string, ChatSlot>;
  selection: Record<string, SelectionItem[]>;
  includeSelection: boolean;
  /** Indexes (in SolidWorks selection order) the user removed from the selection chips. Reset when the selection changes. */
  excludedSel: number[];
  /** Per document: signature of a selection the agent made (it selects entities to build features). Hidden, never sent. */
  agentSel: Record<string, string>;
  markup: { active: boolean; tool: MarkupTool; color: string; strokes: number };
  runs: Record<string, RunState>;
  drafts: Record<string, string>;
  attachments: Record<string, Attachment[]>;
  panel: "chat" | "settings";
  focusTick: number;
  notice: string | null;

  boot(): Promise<void>;
  showDoc(key: string): Promise<void>;
  newChat(): Promise<void>;
  selectThread(threadId: string): Promise<void>;
  deleteThread(threadId: string): Promise<void>;
  setThreadModel(model: string): void;
  setThreadEffort(effort: Effort): void;
  setDraft(text: string): void;
  addAttachment(a: Attachment): void;
  removeAttachment(id: string): void;
  attachView(): Promise<void>;
  send(text: string): Promise<void>;
  retry(): Promise<void>;
  /** Drop everything after the prompt at msgIndex and run again, optionally with edited text. */
  resendFrom(msgIndex: number, editedText?: string): Promise<void>;
  stop(): void;
  answerApproval(threadId: string, d: ApprovalDecision): void;
  answerQuestion(threadId: string, answer: string): void;
  saveSettings(patch: Partial<Settings>): Promise<void>;
  setApiKey(key: string): Promise<void>;
  setPanel(p: State["panel"]): void;
  setIncludeSelection(v: boolean): void;
  toggleSelItem(index: number): void;
  runUserTool(name: string): Promise<void>;
  deleteUserTool(name: string): Promise<void>;
  startMarkup(): Promise<void>;
  setMarkup(patch: { tool?: MarkupTool; color?: string }): void;
  markupUndo(): void;
  markupClear(): void;
  finishMarkup(): Promise<void>;
  cancelMarkup(): void;
  dismissNotice(): void;
}

export type MarkupTool = "pen" | "arrow" | "circle" | "highlighter";
export const MARKUP_COLORS = ["#E5302A", "#F2B11F", "#2F84D6", "#2E9E3E"];
export const MARKUP_NAME = "Markup";

interface SelectionSnapshot {
  items: { type: string; label: string; detail: string; ref: string | null }[];
  view: { text: string } | null;
}

/** Identity of a selection as the panel sees it (type + owner of each entity, in order). */
export function selSig(items: SelectionItem[]): string {
  return items.map((i) => `${i.type}|${i.label}`).join(";");
}

/** True when what's selected in the document right now was selected by the agent, not the user. */
export function isAgentSelection(s: { selection: Record<string, SelectionItem[]>; agentSel: Record<string, string> }, docKey: string): boolean {
  const items = s.selection[docKey] ?? [];
  return items.length > 0 && s.agentSel[docKey] === selSig(items);
}

/** Stable empty array for selectors (a fresh [] each call would re-render forever). */
export const EMPTY: never[] = [];

const uid = () => crypto.randomUUID();
const now = () => new Date().toISOString();
const ZERO_USAGE: Usage = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 };

/** Key for drafts/attachments: the active thread, or the doc's "new chat" slot. */
export function draftKey(s: Pick<State, "chats" | "viewKey">): string {
  const slot = s.chats[s.viewKey];
  return slot?.data.activeThreadId ?? `new:${s.viewKey}`;
}

export function currentThread(s: Pick<State, "chats" | "viewKey">): Thread | null {
  const slot = s.chats[s.viewKey];
  if (!slot?.data.activeThreadId) return null;
  return slot.data.threads.find((t) => t.id === slot.data.activeThreadId) ?? null;
}

// Saves are serialized per document so an older snapshot never overwrites a newer one.
const saveChains = new Map<string, Promise<unknown>>();

export const useStore = create<State>((set, get) => {
  const persist = (docKey: string) => {
    const slot = get().chats[docKey];
    if (!slot) return Promise.resolve();
    const snapshot = slot.data;
    const next = (saveChains.get(docKey) ?? Promise.resolve())
      .then(() => rpc("chat.save", { docKey, data: snapshot }))
      .catch((err) => set({ notice: `Couldn't save the chat: ${err.message}` }));
    saveChains.set(docKey, next);
    return next;
  };

  const updateThread = (docKey: string, threadId: string, fn: (t: Thread) => Thread) => {
    set((s) => {
      const slot = s.chats[docKey];
      if (!slot) return {};
      const threads = slot.data.threads.map((t) => (t.id === threadId ? { ...fn(t), updatedAt: now() } : t));
      return { chats: { ...s.chats, [docKey]: { ...slot, data: { ...slot.data, threads } } } };
    });
  };

  const updateSlot = (docKey: string, fn: (d: ChatData) => ChatData) => {
    set((s) => {
      const slot = s.chats[docKey];
      return slot ? { chats: { ...s.chats, [docKey]: { ...slot, data: fn(slot.data) } } } : {};
    });
  };

  const updateRun = (threadId: string, patch: Partial<RunState>) => {
    set((s) => (s.runs[threadId] ? { runs: { ...s.runs, [threadId]: { ...s.runs[threadId], ...patch } } } : {}));
  };

  const loadChat = async (docKey: string) => {
    if (get().chats[docKey]) return;
    const res = await rpc<{ docKey: string; docId: string; recovered: boolean; data: ChatData }>("chat.load", { docKey });
    const raw: Partial<ChatData> = res.data ?? {};
    const data: ChatData = {
      schema: 1,
      docId: res.docId,
      revision: raw.revision ?? 0,
      activeThreadId: raw.activeThreadId ?? null,
      threads: raw.threads ?? [],
    };
    data.threads = (data.threads ?? []).map((t) => ({ ...t, turns: t.turns ?? [], messages: t.messages ?? [] }));
    set((s) => ({
      chats: { ...s.chats, [docKey]: { docKey, docId: res.docId, data, recovered: res.recovered } },
      notice: res.recovered ? "Recovered chat messages that hadn't been saved into the file yet." : s.notice,
    }));
  };

  /**
   * The add-in's view of the world at the moment of sending: document, camera, the selection as numbered
   * references ([S1]...) and, optionally, a screenshot of exactly what the user sees. Appended after the
   * user's text so the history stays append-only.
   */
  const buildContext = async (
    docKey: string,
    threadId: string,
    hasMarkup: boolean,
  ): Promise<{ blocks: ContentBlockParam[]; refs: Record<string, string> }> => {
    const s = get();
    const doc = s.docs[docKey];
    const lines: string[] = [];
    const refs: Record<string, string> = {};
    const extra: ContentBlockParam[] = [];
    if (!doc) {
      lines.push("No document is linked to this chat. Tools act on whichever document is active in SolidWorks (if any).");
      return { blocks: [{ type: "text", text: `<sw_context>\n${lines.join("\n")}\n</sw_context>` }], refs };
    }

    lines.push(`Document: ${doc.title} (${doc.type ?? "document"})${doc.path ? ` - ${doc.path}` : " - not saved yet"}`);
    const snap = await rpc<SelectionSnapshot | null>("selection.snapshot", { docKey }).catch(() => null);
    if (snap?.view?.text) lines.push(`Camera: ${snap.view.text}`);

    const items = isAgentSelection(s, docKey) ? [] : snap?.items ?? [];
    const thread = s.chats[docKey]?.data.threads.find((t) => t.id === threadId);
    let n = Object.keys(thread?.selRefs ?? {}).length;
    if (items.length > 0 && !s.includeSelection) {
      lines.push("Selection: (the user chose not to share it)");
    } else {
      const included = items.filter((_, i) => !s.excludedSel.includes(i));
      if (included.length === 0) {
        lines.push("Selection: nothing selected");
      } else {
        lines.push(`Selected when this message was sent (${included.length}) - may or may not relate to the request:`);
        for (const it of included) {
          const id = `S${++n}`;
          if (it.ref) refs[id] = it.ref;
          lines.push(`[${id}] ${it.type}${it.label ? ` of ${it.label}` : ""}${it.detail ? ` - ${it.detail}` : ""}${it.ref ? "" : " (no persistent id: not addressable with Ref)"}`);
        }
      }
    }

    if (hasMarkup) {
      lines.push("The image attached above is the user's markup: strokes, arrows and circles drawn over their current view.");
    } else if (s.settings.shareView && doc.type !== "other") {
      const shot = await rpc<ToolRunResult>("tools.run", {
        docKey,
        threadId,
        name: "capture_views",
        input: { views: ["current"], max_size: 1024 },
      }).catch(() => null);
      const img = shot && !shot.isError ? shot.content.find((c) => c.type === "image") : undefined;
      if (img) {
        extra.push(img);
        lines.push("Attached after this block: a screenshot of exactly what the user sees right now (selected entities are highlighted).");
      }
    }
    return { blocks: [{ type: "text", text: `<sw_context>\n${lines.join("\n")}\n</sw_context>` }, ...extra], refs };
  };

  const startRun = async (docKey: string, threadId: string) => {
    const keyRes = await rpc<{ key: string | null }>("apiKey.get");
    if (!keyRes.key) {
      set({ panel: "settings", notice: "Add your Anthropic API key to start chatting." });
      return;
    }
    const thread = get().chats[docKey]?.data.threads.find((t) => t.id === threadId);
    if (!thread) return;

    const controller = new AbortController();
    set((s) => ({
      runs: { ...s.runs, [threadId]: { docKey, threadId, live: null, runningToolId: null, approval: null, question: null, controller } },
    }));
    const started = performance.now();
    const selectionAtStart = selSig(get().selection[docKey] ?? []);
    rpc("agent.activity", { running: true }).catch(() => {});
    const turnIndex = thread.turns.length - 1;
    const patchTurn = (patch: Partial<TurnMeta>) =>
      updateThread(docKey, threadId, (t) => ({
        ...t,
        turns: t.turns.map((m, i) => (i === turnIndex ? { ...m, ...patch } : m)),
      }));

    try {
      const result = await runAgent({
        apiKey: keyRes.key,
        model: thread.model,
        effort: thread.effort,
        tools: get().tools,
        approval: get().settings.approval,
        realisticAppearance: get().settings.realisticAppearance,
        userTools: get().userTools,
        signal: controller.signal,
        cb: {
          getMessages: () => get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.messages ?? [],
          append: async (message: MessageParam) => {
            updateThread(docKey, threadId, (t) => ({ ...t, messages: [...t.messages, message] }));
            await persist(docKey);
          },
          setLive: (live) => updateRun(threadId, { live }),
          setRunningTool: (id) => updateRun(threadId, { runningToolId: id }),
          addUsage: (u) =>
            updateThread(docKey, threadId, (t) => ({
              ...t,
              turns: t.turns.map((m, i) => {
                if (i !== turnIndex) return m;
                const prev = m.usage ?? ZERO_USAGE;
                return {
                  ...m,
                  usage: {
                    input: prev.input + u.input,
                    output: prev.output + u.output,
                    cacheRead: prev.cacheRead + u.cacheRead,
                    cacheWrite: prev.cacheWrite + u.cacheWrite,
                    context: Math.max(prev.context ?? 0, u.context ?? 0),
                  },
                };
              }),
            })),
          approve: (req) => new Promise<ApprovalDecision>((resolve) => updateRun(threadId, { approval: { ...req, resolve } })),
          ask: (toolUseId, question, options) =>
            new Promise<string>((resolve) => updateRun(threadId, { question: { toolUseId, question, options, resolve } })),
          runTool: (name, input) =>
            rpc<ToolRunResult>("tools.run", {
              docKey,
              threadId,
              name,
              input,
              refs: get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.selRefs ?? {},
            }),
          isAutoApproved: () => !!get().chats[docKey]?.data.threads.find((t) => t.id === threadId)?.autoApprove,
          setAutoApproved: () => updateThread(docKey, threadId, (t) => ({ ...t, autoApprove: true })),
        },
      });
      patchTurn({ stop: result.stop, error: result.error, durationMs: Math.round(performance.now() - started) });
    } catch (err: any) {
      const status = err?.status;
      const message =
        status === 401 ? "The API key was rejected. Check it in Settings."
        : status === 429 ? "Rate limited by the API. Wait a moment and press Retry."
        : status === 529 || status === 503 ? "The API is overloaded right now. Press Retry in a moment."
        : err?.message ?? String(err);
      patchTurn({ stop: "error", error: message, durationMs: Math.round(performance.now() - started) });
    } finally {
      set((s) => {
        const runs = { ...s.runs };
        delete runs[threadId];
        return { runs };
      });
      rpc("agent.activity", { running: false }).catch(() => {});
      // Selection events can lag the last API call; read the final state once the run is over.
      const after = await rpc<{ docKey: string; items: SelectionItem[] } | null>("selection.get", { docKey }).catch(() => null);
      if (after) {
        const sig = selSig(after.items);
        set((s) => ({
          selection: { ...s.selection, [docKey]: after.items },
          agentSel: sig !== selectionAtStart && after.items.length > 0 ? { ...s.agentSel, [docKey]: sig } : s.agentSel,
        }));
      }
      await persist(docKey);
    }
  };

  return {
    booted: false,
    bootError: null,
    host: null,
    settings: DEFAULT_SETTINGS,
    apiKeySource: "none",
    swTheme: null,
    tools: [],
    userTools: [],
    update: null,
    updateDialog: false,
    docs: {},
    activeDocKey: null,
    viewKey: "scratch",
    chats: {},
    selection: {},
    includeSelection: true,
    excludedSel: [],
    agentSel: {},
    markup: { active: false, tool: "pen", color: MARKUP_COLORS[0], strokes: 0 },
    runs: {},
    drafts: {},
    attachments: {},
    panel: "chat",
    focusTick: 0,
    notice: null,

    async boot() {
      try {
        const [host, tools] = await Promise.all([rpc<HostState>("app.getState"), rpc<ToolDef[]>("tools.list")]);
        const docs: Record<string, DocInfo> = {};
        for (const d of host.docs) docs[d.key] = d;
        if (host.activeDoc) docs[host.activeDoc.key] = host.activeDoc;
        const viewKey = host.activeDoc?.key ?? host.scratchKey;
        let settings: Settings = { ...DEFAULT_SETTINGS, ...host.settings };
        if ((host.settings.settingsVersion ?? 1) < SETTINGS_VERSION) {
          // v2: the panel now follows the SolidWorks interface theme by default.
          settings = { ...settings, theme: "solidworks", settingsVersion: SETTINGS_VERSION };
          rpc("settings.save", settings);
        }
        if (host.update) set({ update: host.update });
        set({
          host,
          tools,
          docs,
          activeDocKey: host.activeDoc?.key ?? null,
          viewKey,
          apiKeySource: host.apiKeySource,
          swTheme: host.swTheme ?? null,
          settings,
        });
        await loadChat(viewKey);
        if (host.activeDoc) {
          rpc<{ docKey: string; items: SelectionItem[] } | null>("selection.get", { docKey: host.activeDoc.key }).then((r) => {
            if (r) set((s) => ({ selection: { ...s.selection, [r.docKey]: r.items } }));
          });
        }
        set({ booted: true, panel: host.apiKeySource === "none" ? "settings" : "chat" });
      } catch (err: any) {
        set({ bootError: err?.message ?? String(err) });
      }

      on("doc.activated", (doc: DocInfo) => {
        set((s) => ({ docs: { ...s.docs, [doc.key]: doc }, activeDocKey: doc.key }));
        get().showDoc(doc.key);
      });
      on("theme.changed", (t: SwThemeInfo) => set({ swTheme: t }));
      on("update.changed", (u: UpdateInfo) => set({ update: u }));
      on("userTools.changed", (list: UserToolInfo[]) => {
        set({ userTools: list ?? [] });
        syncRibbonIcons(list ?? []);
      });
      rpc<UserToolInfo[]>("userTools.list")
        .then((list) => {
          set({ userTools: list ?? [] });
          syncRibbonIcons(list ?? []);
        })
        .catch(() => {});
      on("doc.updated", (doc: DocInfo) => set((s) => ({ docs: { ...s.docs, [doc.key]: doc } })));
      on("doc.closed", ({ key }: { key: string }) => {
        set((s) => {
          const docs = { ...s.docs };
          delete docs[key];
          const chats = { ...s.chats };
          if (!Object.values(s.runs).some((r) => r.docKey === key)) delete chats[key];
          const activeDocKey = s.activeDocKey === key ? null : s.activeDocKey;
          const viewKey = s.viewKey === key ? activeDocKey ?? s.host?.scratchKey ?? "scratch" : s.viewKey;
          return { docs, chats, activeDocKey, viewKey };
        });
        loadChat(get().viewKey);
      });
      on("selection.changed", ({ docKey, items }: { docKey: string; items: SelectionItem[] }) =>
        set((s) => {
          const agentWorking = Object.values(s.runs).some((r) => r.docKey === docKey);
          return {
            selection: { ...s.selection, [docKey]: items },
            excludedSel: [],
            agentSel: agentWorking ? { ...s.agentSel, [docKey]: selSig(items) } : s.agentSel,
          };
        }),
      );
      on("markup.strokes", ({ count }: { count: number }) => set((s) => ({ markup: { ...s.markup, strokes: count } })));
      on("markup.key", ({ command }: { command: string }) => {
        if (command === "finish") get().finishMarkup();
        else get().cancelMarkup();
      });
      rpc("markup.cancel").catch(() => {}); // a reloaded panel must not leave an orphaned overlay

      // SolidWorks doesn't raise an activation event for File > New, and events can be missed while the panel
      // loads, so also follow the active document by polling (cheap: one RPC every 1.5 s).
      setInterval(async () => {
        const st = await rpc<HostState>("app.getState").catch(() => null);
        const active = st?.activeDoc;
        if (!active || active.key === get().activeDocKey) return;
        set((s) => ({ docs: { ...s.docs, [active.key]: active }, activeDocKey: active.key }));
        get().showDoc(active.key);
      }, 1500);
      on("command", ({ name, args }: { name: string; args?: unknown }) => {
        const s = get();
        switch (name) {
          case "focus":
            set({ panel: "chat", focusTick: s.focusTick + 1 });
            break;
          case "newChat":
            set({ panel: "chat" });
            s.newChat();
            break;
          case "openSettings":
            set({ panel: "settings" });
            break;
          case "runTool": {
            // A tool button on the SolidWorks ribbon.
            const tool = (args as { tool?: string } | undefined)?.tool;
            set({ panel: "chat" });
            if (tool && !s.runs[currentThread(s)?.id ?? ""]) void s.runUserTool(tool);
            break;
          }
          case "newTool": {
            const key = draftKey(s);
            set({ panel: "chat", drafts: { ...s.drafts, [key]: "Help me create a new tool that " }, focusTick: s.focusTick + 1 });
            break;
          }
          case "attachView":
            set({ panel: "chat" });
            s.attachView();
            break;
          case "askAboutSelection": {
            const sel = s.selection[s.viewKey] ?? [];
            const what = sel.length === 0 ? "the selection" : sel.length === 1 ? `the selected ${sel[0].type.toLowerCase()}` : `the ${sel.length} selected items`;
            const key = draftKey(s);
            set({
              panel: "chat",
              includeSelection: true,
              drafts: { ...s.drafts, [key]: (s.drafts[key] ? s.drafts[key] + " " : "") + `About ${what}: ` },
              focusTick: s.focusTick + 1,
            });
            break;
          }
        }
      });
    },

    async showDoc(key) {
      set({ viewKey: key });
      await loadChat(key);
      const r = await rpc<{ docKey: string; items: SelectionItem[] } | null>("selection.get", { docKey: key });
      if (r) set((s) => ({ selection: { ...s.selection, [r.docKey]: r.items } }));
    },

    async newChat() {
      const key = get().viewKey;
      await loadChat(key);
      updateSlot(key, (d) => ({ ...d, activeThreadId: null }));
      set((s) => ({ focusTick: s.focusTick + 1 }));
    },

    async selectThread(threadId) {
      const key = get().viewKey;
      updateSlot(key, (d) => ({ ...d, activeThreadId: threadId }));
      await persist(key);
    },

    async deleteThread(threadId) {
      const key = get().viewKey;
      get().runs[threadId]?.controller.abort();
      updateSlot(key, (d) => ({
        ...d,
        threads: d.threads.filter((t) => t.id !== threadId),
        activeThreadId: d.activeThreadId === threadId ? null : d.activeThreadId,
      }));
      rpc("session.reset", { threadId });
      await persist(key);
    },

    setThreadModel(model) {
      const s = get();
      const t = currentThread(s);
      if (t) {
        updateThread(s.viewKey, t.id, (th) => ({ ...th, model }));
        persist(s.viewKey);
      }
      get().saveSettings({ defaultModel: model });
    },

    setThreadEffort(effort) {
      const s = get();
      const t = currentThread(s);
      if (t) {
        updateThread(s.viewKey, t.id, (th) => ({ ...th, effort }));
        persist(s.viewKey);
      }
      get().saveSettings({ defaultEffort: effort });
    },

    setDraft(text) {
      const key = draftKey(get());
      set((s) => ({ drafts: { ...s.drafts, [key]: text } }));
    },

    addAttachment(a) {
      const key = draftKey(get());
      set((s) => ({ attachments: { ...s.attachments, [key]: [...(s.attachments[key] ?? []), a] } }));
    },

    removeAttachment(id) {
      const key = draftKey(get());
      set((s) => ({ attachments: { ...s.attachments, [key]: (s.attachments[key] ?? []).filter((a) => a.id !== id) } }));
    },

    async attachView() {
      const s = get();
      try {
        const res = await rpc<ToolRunResult>("tools.run", {
          docKey: s.viewKey,
          threadId: currentThread(s)?.id ?? "attach",
          name: "capture_views",
          input: { views: ["current"], max_size: 1280 },
        });
        const img = res.content.find((c) => c.type === "image");
        if (res.isError || !img || img.type !== "image" || img.source.type !== "base64") {
          const text = res.content.find((c) => c.type === "text");
          set({ notice: `Couldn't capture the view: ${text && text.type === "text" ? text.text : "no image"}` });
          return;
        }
        get().addAttachment({ id: uid(), kind: "image", name: "View", mediaType: img.source.media_type as Attachment["mediaType"], data: img.source.data });
        set((st) => ({ focusTick: st.focusTick + 1 }));
      } catch (err: any) {
        set({ notice: `Couldn't capture the view: ${err.message}` });
      }
    },

    async send(text) {
      const s0 = get();
      const docKey = s0.viewKey;
      await loadChat(docKey);
      const dKey = draftKey(get());
      const attachments = get().attachments[dKey] ?? [];
      const trimmed = text.trim();
      if (!trimmed && attachments.length === 0) return;

      let thread = currentThread(get());
      if (thread && get().runs[thread.id]) return; // already running
      const settings = get().settings;
      if (!thread) {
        thread = {
          id: uid(),
          title: (trimmed || "Image").replace(/\s+/g, " ").slice(0, 70),
          createdAt: now(),
          updatedAt: now(),
          model: settings.defaultModel,
          effort: settings.defaultEffort,
          messages: [],
          turns: [],
        };
        const created = thread;
        updateSlot(docKey, (d) => ({ ...d, threads: [created, ...d.threads], activeThreadId: created.id }));
      }

      const threadId = thread.id;
      // Clear the composer right away; capturing the selection and view can take a moment.
      set((s) => ({
        drafts: { ...s.drafts, [dKey]: "", [threadId]: "" },
        attachments: { ...s.attachments, [dKey]: [], [threadId]: [] },
      }));
      const blocks: ContentBlockParam[] = [];
      if (trimmed) blocks.push({ type: "text", text: trimmed });
      for (const a of attachments) blocks.push({ type: "image", source: { type: "base64", media_type: a.mediaType, data: a.data } });
      const ctx = await buildContext(docKey, threadId, attachments.some((a) => a.name === MARKUP_NAME));
      blocks.push(...ctx.blocks);

      updateThread(docKey, threadId, (t) => {
        const last = t.messages[t.messages.length - 1];
        // After a stopped or failed turn the history can end on a user message; extend it rather than
        // sending two user messages in a row (no assistant output follows it, so nothing is invalidated).
        const messages: MessageParam[] =
          last && last.role === "user"
            ? [...t.messages.slice(0, -1), { role: "user", content: [...asBlocks(last.content), ...blocks] }]
            : [...t.messages, { role: "user", content: blocks }];
        const msgIndex = messages.length - 1;
        return {
          ...t,
          messages,
          selRefs: { ...(t.selRefs ?? {}), ...ctx.refs },
          turns: [...t.turns, { at: now(), msgIndex, model: t.model, effort: t.effort, usage: { ...ZERO_USAGE } }],
        };
      });
      await persist(docKey);
      await startRun(docKey, threadId);
    },

    async resendFrom(msgIndex, editedText) {
      const s = get();
      const docKey = s.viewKey;
      const t = currentThread(s);
      if (!t || s.runs[t.id]) return;
      const msg = t.messages[msgIndex];
      if (!msg || !isPrompt(msg)) return;

      const blocks = asBlocks(msg.content);
      let content: ContentBlockParam[] = blocks;
      let newRefs: Record<string, string> = {};
      if (editedText !== undefined) {
        const text = editedText.trim();
        // Tool results answer the previous assistant turn and must stay; the prompt itself is replaced,
        // keeping the user's own images (those before the context block) and taking a fresh snapshot of
        // the document, selection and view.
        const ctxAt = blocks.findIndex((b) => b.type === "text" && b.text.startsWith("<sw_context>"));
        const ownImages = blocks.filter((b, i) => b.type === "image" && (ctxAt < 0 || i < ctxAt));
        const ctx = await buildContext(docKey, t.id, false);
        newRefs = ctx.refs;
        content = [
          ...blocks.filter((b) => b.type === "tool_result"),
          ...(text ? [{ type: "text" as const, text }] : []),
          ...ownImages,
          ...ctx.blocks,
        ];
        if (!content.some((b) => b.type === "text" && !b.text.startsWith("<sw_context>")) && !content.some((b) => b.type === "image")) return;
      }

      // Truncating the tail and replacing the last message keeps every earlier thinking block valid.
      const isFirstPrompt = !t.messages.slice(0, msgIndex).some(isPrompt);
      updateThread(docKey, t.id, (th) => ({
        ...th,
        title: editedText !== undefined && isFirstPrompt && editedText.trim() ? editedText.trim().replace(/\s+/g, " ").slice(0, 70) : th.title,
        messages: [...th.messages.slice(0, msgIndex), { role: "user", content }],
        selRefs: { ...(th.selRefs ?? {}), ...newRefs },
        turns: [
          ...th.turns.filter((m) => m.msgIndex < msgIndex),
          { at: now(), msgIndex, model: th.model, effort: th.effort, usage: { ...ZERO_USAGE } },
        ],
      }));
      await persist(docKey);
      await startRun(docKey, t.id);
    },

    async retry() {
      const s = get();
      const t = currentThread(s);
      if (!t || s.runs[t.id]) return;
      const last = t.messages[t.messages.length - 1];
      if (!last || last.role !== "user") return;
      updateThread(s.viewKey, t.id, (th) => ({
        ...th,
        turns: [...th.turns, { at: now(), msgIndex: lastPromptIndex(th.messages), model: th.model, effort: th.effort, usage: { ...ZERO_USAGE } }],
      }));
      await startRun(s.viewKey, t.id);
    },

    stop() {
      const s = get();
      const t = currentThread(s);
      if (!t) return;
      const run = s.runs[t.id];
      if (!run) return;
      run.controller.abort();
      run.approval?.resolve({ decision: "reject", note: "The user pressed Stop." });
      run.question?.resolve("(The user pressed Stop instead of answering.)");
      rpc("tools.cancel", { threadId: t.id });
    },

    answerApproval(threadId, d) {
      const run = get().runs[threadId];
      run?.approval?.resolve(d);
      updateRun(threadId, { approval: null });
    },

    answerQuestion(threadId, answer) {
      const run = get().runs[threadId];
      run?.question?.resolve(answer);
      updateRun(threadId, { question: null });
    },

    async saveSettings(patch) {
      const settings = { ...get().settings, ...patch };
      set({ settings });
      await rpc("settings.save", settings);
    },

    async setApiKey(key) {
      const res = await rpc<{ source: HostState["apiKeySource"] }>("apiKey.set", { key });
      set({ apiKeySource: res.source, notice: null });
    },

    setPanel(panel) {
      set({ panel });
    },

    setIncludeSelection(v) {
      set({ includeSelection: v });
    },

    async runUserTool(name) {
      // Whatever is typed in the composer becomes the tool's notes/inputs.
      const notes = (get().drafts[draftKey(get())] ?? "").trim();
      await get().send(`[Run tool: ${name}]${notes ? `\n${notes}` : ""}`);
    },

    async deleteUserTool(name) {
      await rpc("userTools.delete", { name });
    },

    toggleSelItem(index) {
      set((s) => ({
        excludedSel: s.excludedSel.includes(index) ? s.excludedSel.filter((i) => i !== index) : [...s.excludedSel, index],
      }));
    },

    async startMarkup() {
      const s = get();
      try {
        await rpc("markup.start", { docKey: s.viewKey, tool: s.markup.tool, color: s.markup.color });
        set((st) => ({ markup: { ...st.markup, active: true, strokes: 0 } }));
      } catch (err: any) {
        set({ notice: `Can't draw on the view: ${err.message}` });
      }
    },

    setMarkup(patch) {
      set((s) => ({ markup: { ...s.markup, ...patch } }));
      if (get().markup.active) rpc("markup.set", patch);
    },

    markupUndo() {
      rpc("markup.undo");
    },

    markupClear() {
      rpc("markup.clear");
    },

    async finishMarkup() {
      if (!get().markup.active) return;
      set((s) => ({ markup: { ...s.markup, active: false } }));
      try {
        const res = await rpc<{ mediaType: Attachment["mediaType"]; data: string; strokes: number } | null>("markup.finish");
        if (res?.data) {
          // One markup per message: replace an earlier one that wasn't sent yet.
          const key = draftKey(get());
          set((s) => ({
            attachments: { ...s.attachments, [key]: (s.attachments[key] ?? []).filter((a) => a.name !== MARKUP_NAME) },
          }));
          get().addAttachment({ id: uid(), kind: "image", name: MARKUP_NAME, mediaType: res.mediaType, data: res.data });
          set((s) => ({ focusTick: s.focusTick + 1 }));
        }
      } catch (err: any) {
        set({ notice: `Couldn't attach the markup: ${err.message}` });
      }
    },

    cancelMarkup() {
      set((s) => ({ markup: { ...s.markup, active: false, strokes: 0 } }));
      rpc("markup.cancel");
    },

    dismissNotice() {
      set({ notice: null });
    },
  };
});

/** Model-changing tool calls that ran after the message at msgIndex (they are not undone by resending). */
export function writesAfter(thread: Thread, msgIndex: number): number {
  let n = 0;
  for (const m of thread.messages.slice(msgIndex + 1)) {
    if (m.role !== "assistant" || typeof m.content === "string") continue;
    for (const b of m.content) if (b.type === "tool_use" && WRITE_TOOLS.has(b.name)) n++;
  }
  return n;
}

/** Plain text of a prompt, without the add-in's context block. */
export function promptText(m: MessageParam): string {
  if (typeof m.content === "string") return m.content;
  return m.content
    .filter((b): b is Extract<ContentBlockParam, { type: "text" }> => b.type === "text" && !b.text.startsWith("<sw_context>"))
    .map((b) => b.text)
    .join("\n\n");
}

/** Index of the last user message that has something other than tool results (i.e. a prompt). */
export function lastPromptIndex(messages: MessageParam[]): number {
  for (let i = messages.length - 1; i >= 0; i--) {
    if (isPrompt(messages[i])) return i;
  }
  return 0;
}

export function isPrompt(m: MessageParam): boolean {
  return m.role === "user" && (typeof m.content === "string" || m.content.some((b) => b.type !== "tool_result"));
}

function asBlocks(content: MessageParam["content"]): ContentBlockParam[] {
  return typeof content === "string" ? [{ type: "text", text: content }] : [...content];
}

// Dev-only handle for poking at state from the browser console.
if (import.meta.env.DEV) (window as any).__agentStore = useStore;

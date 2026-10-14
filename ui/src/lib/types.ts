import type Anthropic from "@anthropic-ai/sdk";

export type MessageParam = Anthropic.Beta.Messages.BetaMessageParam;
export type ContentBlockParam = Anthropic.Beta.Messages.BetaContentBlockParam;
export type ToolResultBlockParam = Anthropic.Beta.Messages.BetaToolResultBlockParam;
export type ImageBlockParam = Anthropic.Beta.Messages.BetaImageBlockParam;
export type TextBlockParam = Anthropic.Beta.Messages.BetaTextBlockParam;
export type ToolUseBlockParam = Anthropic.Beta.Messages.BetaToolUseBlockParam;

export type Effort = "low" | "medium" | "high" | "xhigh" | "max";
export type ApprovalMode = "ask" | "auto" | "read_only";
export type Theme = "solidworks" | "system" | "light" | "dark";

/** SolidWorks interface theme as reported by the add-in (swInterfaceBrightnessTheme_e + panel colours). */
export interface SwThemeInfo {
  theme: number;
  treeBg?: string;
  pmBg?: string;
}

export interface Usage {
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  /** Largest single prompt in the turn (tokens the model actually saw at once, after context editing). */
  context?: number;
}

/** Bookkeeping for one user turn (one press of Send), stored alongside the API messages. */
export interface TurnMeta {
  at: string;
  /** Index of the user message this turn answers. */
  msgIndex: number;
  model: string;
  effort: Effort;
  usage: Usage;
  durationMs?: number;
  stop?: string;
  error?: string;
}

/** A conversation. `messages` is the exact Claude API history so it can be replayed verbatim. */
export interface Thread {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  model: string;
  effort: Effort;
  messages: MessageParam[];
  turns: TurnMeta[];
  /** "Always allow" was chosen for code execution in this chat. */
  autoApprove?: boolean;
  /** Selection references [S1]... captured with the user's messages: id -> SolidWorks persistent reference (base64). */
  selRefs?: Record<string, string>;
  /** "tools": the per-document chat where ribbon tools run when the agent has to step in. */
  kind?: "tools";
}

/** What the add-in stores inside the SolidWorks file. */
export interface ChatData {
  schema: 1;
  docId: string;
  revision: number;
  activeThreadId: string | null;
  threads: Thread[];
}

export interface DocInfo {
  key: string;
  title: string;
  path?: string;
  type?: "part" | "assembly" | "drawing" | "other";
}

export interface Settings {
  /** Bumped when a default changes in a way existing installs should pick up. */
  settingsVersion: number;
  defaultModel: string;
  defaultEffort: Effort;
  approval: ApprovalMode;
  theme: Theme;
  markDocumentDirty: boolean;
  showProgress: boolean;
  /** Ask the agent to give parts real materials and realistic colours/appearances. */
  realisticAppearance: boolean;
  /** Attach a screenshot of the user's current view to every message. */
  shareView: boolean;
  /** Silence SolidWorks' rebuild-error sound while the agent works. */
  muteErrorSounds: boolean;
  /** Text size of the panel (1 = 100%). Ctrl+/Ctrl-/Ctrl+0. */
  zoom: number;
  /** Download updates in the background and install them when SolidWorks closes. Unset until the user is asked. */
  autoUpdate?: boolean | null;
}

/** Update feed state from the add-in (Infrastructure/Updater.cs). */
export interface UpdateInfo {
  current: string;
  latest: string | null;
  status: "idle" | "checking" | "uptodate" | "available" | "downloading" | "ready" | "error";
  notes: string | null;
  releaseUrl: string | null;
  error: string | null;
  progress: number;
  lastChecked: string | null;
}

export interface HostState {
  version: string;
  solidworksVersion: string | null;
  apiKeySource: "stored" | "environment" | "none";
  settings: Partial<Settings>;
  scratchKey: string;
  activeDoc: DocInfo | null;
  docs: DocInfo[];
  logFile: string;
  swTheme?: SwThemeInfo | null;
  update?: UpdateInfo;
}

export interface ToolDef {
  name: string;
  description: string;
  input_schema: { type: "object"; properties?: Record<string, any>; required?: string[] };
}

export interface ToolRunResult {
  content: Array<TextBlockParam | ImageBlockParam>;
  isError: boolean;
}

/** A user-created tool (a saved, reusable procedure the agent runs). */
export interface UserToolInfo {
  name: string;
  title: string;
  description: string;
  icon: string;
  updated: string;
  /** The tool's own icon (SVG markup), if the agent designed one. */
  iconSvg?: string | null;
  /** Whether the ribbon PNG is up to date (false: the panel renders it). */
  hasPng?: boolean;
  /** "workflow": big procedure, Workflows menu. "tool": quick ribbon command (agentic macro). */
  kind?: "workflow" | "tool";
  /** A tool's verified macro exists (runs directly, without the agent). */
  hasMacro?: boolean;
  /** Model for agent-run steps, if the tool chose one. */
  model?: string | null;
}

export interface SelectionItem {
  type: string;
  label: string;
}

export interface Attachment {
  id: string;
  kind: "image";
  name: string;
  mediaType: "image/jpeg" | "image/png" | "image/webp" | "image/gif";
  data: string; // base64
}

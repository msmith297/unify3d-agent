import type { Effort, Settings } from "./types";
import type { Tier } from "./types";

export interface ModelOption {
  id: string;
  label: string;
  note: string;
  /** Server-side refusal fallback (`fallbacks: "default"`). */
  fallbacks: boolean;
  /** Supports `thinking.display: "updates"` (progress notes between tool calls). */
  progressUpdates: boolean;
}

export const MODELS: ModelOption[] = [
  { id: "claude-opus-5-5", label: "Opus 5.5", note: "Default · best balance for CAD work", fallbacks: true, progressUpdates: true },
  { id: "claude-fable-5-1", label: "Fable 5.1", note: "Most capable · about 2.5× the cost", fallbacks: true, progressUpdates: true },
  { id: "claude-sonnet-5-5", label: "Sonnet 5.5", note: "Faster · half the cost", fallbacks: true, progressUpdates: true },
  { id: "claude-haiku-5-5", label: "Haiku 5.5", note: "Fastest · for quick questions", fallbacks: false, progressUpdates: false },
];

export const EFFORTS: { id: Effort; label: string; note: string }[] = [
  { id: "low", label: "Low", note: "Quick answers" },
  { id: "medium", label: "Medium", note: "Routine edits" },
  { id: "high", label: "High", note: "Default · multi-step modelling" },
  { id: "xhigh", label: "Extra high", note: "Hard modelling problems" },
  { id: "max", label: "Max", note: "When correctness matters most" },
];

export const SETTINGS_VERSION = 2;

export const DEFAULT_SETTINGS: Settings = {
  settingsVersion: SETTINGS_VERSION,
  defaultModel: "claude-opus-5-5",
  defaultEffort: "high",
  approval: "ask",
  theme: "solidworks",
  markDocumentDirty: true,
  showProgress: true,
  realisticAppearance: true,
  verbosity: "medium",
  shareView: true,
  muteErrorSounds: true,
  zoom: 1,
  voiceEnabled: false,
  voiceKey: "vk:192",
  voiceModel: "base.en",
  voiceSend: "release",
  voiceViews: true,
  speakReplies: "off",
  speakRate: 0,
  // The add-in reads these from settings.json with the same defaults when a key is missing (plugins, MCP, tool forms).
  pluginAutoUpdate: true,
  pluginHooks: true,
  pluginMcp: true,
  pluginWakesPerHour: 20,
  pluginEventsInChat: true,
  toolInputs: "panel",
};

/** A command's or agent's `model` front matter (Claude Code aliases or a model id) as a model id; null: the chat's own. */
export function modelFromAlias(alias: string | null | undefined): string | null {
  const a = (alias ?? "").trim().toLowerCase();
  if (!a || a === "inherit") return null;
  const byFamily = MODELS.find((m) => m.id.startsWith(`claude-${a}-`));
  if (byFamily) return byFamily.id;
  return MODELS.find((m) => m.id === a || m.label.toLowerCase() === a)?.id ?? (a.startsWith("claude-") ? a : null);
}

export function modelOption(id: string): ModelOption {
  return MODELS.find((m) => m.id === id) ?? { id, label: id, note: "", fallbacks: false, progressUpdates: false };
}

/**
 * Intelligence tiers for tools, per provider. A tool stores only the tier, so it keeps working when the user
 * switches provider (only Anthropic is wired up today).
 */
export const TIERS: Record<Tier, { label: string; note: string; anthropic: { model: string; effort: "low" | "medium" | "high" } }> = {
  fast: { label: "Fast", note: "Quickest - simple, mechanical jobs", anthropic: { model: "claude-haiku-5-5", effort: "low" } },
  balanced: { label: "Balanced", note: "Default - most tools", anthropic: { model: "claude-sonnet-5-5", effort: "medium" } },
  high: { label: "High", note: "Careful geometry work", anthropic: { model: "claude-opus-5-5", effort: "medium" } },
  max: { label: "Max", note: "Hardest geometry reasoning", anthropic: { model: "claude-opus-5-5", effort: "high" } },
};

export function tierModel(tier: Tier | undefined): { model: string; effort: "low" | "medium" | "high" } {
  return (TIERS[tier ?? "balanced"] ?? TIERS.balanced).anthropic;
}

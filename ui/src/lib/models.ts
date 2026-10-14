import type { Effort, Settings } from "./types";

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
  shareView: true,
  muteErrorSounds: true,
  zoom: 1,
};

export function modelOption(id: string): ModelOption {
  return MODELS.find((m) => m.id === id) ?? { id, label: id, note: "", fallbacks: false, progressUpdates: false };
}

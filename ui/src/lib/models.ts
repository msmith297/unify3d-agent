import type { Effort, ProviderId, Settings } from "./types";
import type { Tier } from "./types";

/** What a model can do (docs/providers/design.md 7.1). The app asks these instead of testing model names. */
export interface ModelCaps {
  /** Offered effort levels, low..max order. */
  efforts: Effort[];
  /** Images in messages and in tool results. */
  vision: boolean;
  /** PDF documents (document blocks / input_file). */
  pdf: boolean;
  /** Whose web research tools the model gets, if any. */
  web: "anthropic" | "openai" | null;
  /** Claude: supports `thinking.display: "updates"` (progress notes between tool calls). */
  progressUpdates: boolean;
  /** Claude: server-side refusal fallback (`fallbacks: "default"`). */
  fallbacks: boolean;
  /** Price of a cached input token against an uncached one (the footer's "billed at"). */
  cacheReadRatio: number;
  /** The longer prompt cache a long tool run asks for. */
  cacheTtlLong: "1h" | "12h" | null;
  /** OpenAI speed tiers (`service_tier`) the model accepts. */
  serviceTiers: ("fast" | "ultrafast")[];
  /** Informational; nothing reads it in v1. */
  contextWindow?: number;
  maxOutput: number;
}

export interface ModelInfo {
  id: string;
  provider: ProviderId;
  label: string;
  note: string;
  /** Reasoning compatibility and alias mapping: models of one family share reasoning. */
  family: string;
  /** Which tier this model stands for when a plugin names it but its provider has no key. */
  tier: Tier;
  caps: ModelCaps;
  /** USD per 1M tokens (prompts up to 272K for OpenAI); for a later cost display, not shown in v1. */
  price?: { input: number; cachedInput: number; cacheWrite: number; output: number };
}

/** @deprecated The registry row; use ModelInfo. Kept while callers move over. */
export type ModelOption = ModelInfo;

const ALL_EFFORTS: Effort[] = ["low", "medium", "high", "xhigh", "max"];

const claudeCaps = (progressUpdates: boolean, fallbacks: boolean): ModelCaps => ({
  efforts: ALL_EFFORTS,
  vision: true,
  pdf: true,
  web: "anthropic",
  progressUpdates,
  fallbacks,
  cacheReadRatio: 0.1,
  cacheTtlLong: "1h",
  serviceTiers: [],
  maxOutput: 64000,
});

const gptCaps = (cacheReadRatio: number, serviceTiers: ("fast" | "ultrafast")[]): ModelCaps => ({
  efforts: ALL_EFFORTS,
  vision: true,
  pdf: true,
  web: "openai",
  progressUpdates: false,
  fallbacks: false,
  cacheReadRatio,
  cacheTtlLong: "12h",
  serviceTiers,
  contextWindow: 1_050_000,
  maxOutput: 128000,
});

/** Every model the app offers. The order is the picker order: Claude first, then OpenAI. */
export const MODELS: ModelInfo[] = [
  { id: "claude-opus-5-5", provider: "anthropic", label: "Opus 5.5", note: "Default · best balance for CAD work", family: "claude", tier: "high", caps: claudeCaps(true, true) },
  { id: "claude-fable-5-1", provider: "anthropic", label: "Fable 5.1", note: "Most capable · about 2.5× the cost", family: "claude", tier: "max", caps: claudeCaps(true, true) },
  { id: "claude-sonnet-5-5", provider: "anthropic", label: "Sonnet 5.5", note: "Faster · half the cost", family: "claude", tier: "balanced", caps: claudeCaps(true, true) },
  { id: "claude-haiku-5-5", provider: "anthropic", label: "Haiku 5.5", note: "Fastest · for quick questions", family: "claude", tier: "fast", caps: claudeCaps(false, false) },
  {
    id: "gpt-6.1-sol",
    provider: "openai",
    label: "GPT-6.1 Sol",
    note: "OpenAI default · best balance",
    family: "gpt-6",
    tier: "balanced",
    caps: gptCaps(0.05, ["fast", "ultrafast"]),
    price: { input: 2, cachedInput: 0.1, cacheWrite: 2.5, output: 10 },
  },
  { id: "gpt-6-astra", provider: "openai", label: "GPT-6 Astra", note: "Most capable · about 5× the cost of Sol", family: "gpt-6", tier: "high", caps: gptCaps(0.1, ["fast", "ultrafast"]) },
  { id: "gpt-6-luna", provider: "openai", label: "GPT-6 Luna", note: "Fastest · for quick questions", family: "gpt-6", tier: "fast", caps: gptCaps(0.1, []) },
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
  questionTimeoutSec: 60,
  webResearch: true,
  openaiSpeed: "standard",
};

/** Ids whose provider is known from the name alone. */
const PREFIXES: [RegExp, ProviderId][] = [
  [/^claude-/, "anthropic"],
  [/^(gpt-|o\d|chatgpt-)/, "openai"],
];

/** The provider of a model id: the registry, then the id's prefix; anything else is taken as Anthropic. */
export function inferProvider(id: string): ProviderId {
  const known = MODELS.find((m) => m.id === id);
  if (known) return known.provider;
  return PREFIXES.find(([re]) => re.test(id))?.[1] ?? "anthropic";
}

/** A model's registry row; an unknown id gets its inferred provider and conservative capabilities. */
export function modelOption(id: string): ModelInfo {
  const known = MODELS.find((m) => m.id === id);
  if (known) return known;
  const provider = inferProvider(id);
  if (provider === "openai") {
    return { id, provider, label: id, note: "", family: id, tier: "balanced", caps: { ...gptCaps(0.1, []), cacheTtlLong: null, contextWindow: undefined } };
  }
  return { id, provider, label: id, note: "", family: "claude", tier: "balanced", caps: claudeCaps(false, false) };
}

/** The effort a model is sent: the one asked for if it offers it, else the nearest offered one (ties go down). */
export function effortFor(model: ModelInfo, effort: Effort): Effort {
  const offered = model.caps.efforts;
  if (offered.includes(effort) || offered.length === 0) return effort;
  const at = ALL_EFFORTS.indexOf(effort);
  let best = offered[0];
  for (const e of offered) {
    const d = Math.abs(ALL_EFFORTS.indexOf(e) - at);
    const bd = Math.abs(ALL_EFFORTS.indexOf(best) - at);
    if (d < bd || (d === bd && ALL_EFFORTS.indexOf(e) < ALL_EFFORTS.indexOf(best))) best = e;
  }
  return best;
}

export interface TierModel {
  model: string;
  effort: Effort;
}

/**
 * Intelligence tiers for tools, per provider. A tool stores only the tier, so it keeps working when the user
 * switches provider.
 */
export const TIERS: Record<Tier, { label: string; note: string } & Record<ProviderId, TierModel>> = {
  fast: { label: "Fast", note: "Quickest - simple, mechanical jobs", anthropic: { model: "claude-haiku-5-5", effort: "low" }, openai: { model: "gpt-6-luna", effort: "low" } },
  balanced: { label: "Balanced", note: "Default - most tools", anthropic: { model: "claude-sonnet-5-5", effort: "medium" }, openai: { model: "gpt-6.1-sol", effort: "medium" } },
  high: { label: "High", note: "Careful geometry work", anthropic: { model: "claude-opus-5-5", effort: "medium" }, openai: { model: "gpt-6-astra", effort: "medium" } },
  max: { label: "Max", note: "Hardest geometry reasoning", anthropic: { model: "claude-opus-5-5", effort: "high" }, openai: { model: "gpt-6-astra", effort: "high" } },
};

export function tierModel(tier: Tier | undefined, provider: ProviderId = "anthropic"): TierModel {
  return (TIERS[tier ?? "balanced"] ?? TIERS.balanced)[provider];
}

/** Claude Code's model aliases, and the tier each stands for on another provider. */
const ALIAS_TIERS: Record<string, Tier> = { haiku: "fast", sonnet: "balanced", opus: "high", fable: "max" };

/**
 * A command's, agent's or workflow's `model` (a Claude Code alias, a model id or label) as a model id for a chat on
 * `ctx.chatModel`; null means the chat's own model. Aliases resolve inside the chat's provider; a model whose provider
 * has no key falls back to its tier on the chat's provider (docs/providers/design.md 7.4).
 */
export function resolveModel(name: string | null | undefined, ctx: { chatModel: string; hasKey(p: ProviderId): boolean }): string | null {
  const a = (name ?? "").trim().toLowerCase();
  if (!a || a === "inherit") return null;
  const chatProvider = inferProvider(ctx.chatModel);
  const aliasTier = ALIAS_TIERS[a];
  if (aliasTier) {
    if (chatProvider === "anthropic") return MODELS.find((m) => m.id.startsWith(`claude-${a}-`))?.id ?? null;
    return tierModel(aliasTier, chatProvider).model;
  }
  const known = MODELS.find((m) => m.id === a || m.label.toLowerCase() === a);
  if (known) return ctx.hasKey(known.provider) ? known.id : tierModel(known.tier, chatProvider).model;
  const prefixed = PREFIXES.find(([re]) => re.test(a));
  if (prefixed) return ctx.hasKey(prefixed[1]) ? a : null;
  return null;
}

/** @deprecated Use resolveModel. A model name on a Claude chat with every key present (today's meaning). */
export function modelFromAlias(alias: string | null | undefined): string | null {
  return resolveModel(alias, { chatModel: "claude-opus-5-5", hasKey: () => true });
}

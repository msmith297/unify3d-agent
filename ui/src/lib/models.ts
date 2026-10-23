import type { Effort, ProviderId, Settings } from "./types";
import type { Tier } from "./types";

// ---- the registry (CORE-STUB: C0 of docs/providers/design.md 7.1-7.4 writes this part; integration keeps C0's copy) ----

export interface ModelCaps {
  /** Offered levels, low..max order. */
  efforts: Effort[];
  /** Images in messages and in tool results. */
  vision: boolean;
  /** PDF documents (Anthropic `document`, OpenAI `input_file`). */
  pdf: boolean;
  web: "anthropic" | "openai" | null;
  /** Claude: thinking.display "updates" (progress notes between tool calls). */
  progressUpdates: boolean;
  /** Claude: server-side refusal fallback (`fallbacks: "default"`). */
  fallbacks: boolean;
  /** Price of a cached input token vs an uncached one (the footer's "billed at" text). */
  cacheReadRatio: number;
  cacheTtlLong: "1h" | "12h" | null;
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
  /** USD per 1M tokens (prompts up to 272K for OpenAI); for later cost display, not shown in v1. */
  price?: { input: number; cachedInput: number; cacheWrite: number; output: number };
}

/** Until every caller says ModelInfo. */
export type ModelOption = ModelInfo;

const ALL_EFFORTS: Effort[] = ["low", "medium", "high", "xhigh", "max"];

const claude = (id: string, label: string, note: string, tier: Tier, rich: boolean): ModelInfo => ({
  id,
  provider: "anthropic",
  label,
  note,
  family: "claude",
  tier,
  caps: {
    efforts: ALL_EFFORTS,
    vision: true,
    pdf: true,
    web: "anthropic",
    progressUpdates: rich,
    fallbacks: rich,
    cacheReadRatio: 0.1,
    cacheTtlLong: "1h",
    serviceTiers: [],
    maxOutput: 64000,
  },
});

const gpt = (id: string, label: string, note: string, tier: Tier, cacheReadRatio: number, serviceTiers: ("fast" | "ultrafast")[], price?: ModelInfo["price"]): ModelInfo => ({
  id,
  provider: "openai",
  label,
  note,
  family: "gpt-6",
  tier,
  caps: {
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
  },
  ...(price ? { price } : {}),
});

/** The picker order: Claude first, then OpenAI. */
export const MODELS: ModelInfo[] = [
  claude("claude-opus-5-5", "Opus 5.5", "Default · best balance for CAD work", "high", true),
  claude("claude-fable-5-1", "Fable 5.1", "Most capable · about 2.5× the cost", "max", true),
  claude("claude-sonnet-5-5", "Sonnet 5.5", "Faster · half the cost", "balanced", true),
  claude("claude-haiku-5-5", "Haiku 5.5", "Fastest · for quick questions", "fast", false),
  gpt("gpt-6.1-sol", "GPT-6.1 Sol", "OpenAI default · best balance", "balanced", 0.05, ["fast", "ultrafast"], { input: 2, cachedInput: 0.1, cacheWrite: 2.5, output: 10 }),
  gpt("gpt-6-astra", "GPT-6 Astra", "Most capable · about 5× the cost of Sol", "high", 0.1, ["fast", "ultrafast"]),
  gpt("gpt-6-luna", "GPT-6 Luna", "Fastest · for quick questions", "fast", 0.1, []),
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

/** The provider of a known prefix, or null: `claude-...` is Anthropic, `gpt-...`, `o3...`, `chatgpt-...` OpenAI. */
function prefixProvider(id: string): ProviderId | null {
  if (/^claude-/i.test(id)) return "anthropic";
  if (/^(gpt-|o\d|chatgpt-)/i.test(id)) return "openai";
  return null;
}

/** The registry's provider for a model id, else by its prefix, else Anthropic. */
export function inferProvider(id: string): ProviderId {
  return MODELS.find((m) => m.id === id)?.provider ?? prefixProvider(id) ?? "anthropic";
}

/** A model's registry row; an unknown id gets its inferred provider and conservative capabilities. */
export function modelOption(id: string): ModelInfo {
  const known = MODELS.find((m) => m.id === id);
  if (known) return known;
  const provider = inferProvider(id);
  return {
    id,
    provider,
    label: id,
    note: "",
    family: id,
    tier: "balanced",
    caps: {
      efforts: ALL_EFFORTS,
      vision: true,
      pdf: true,
      web: provider,
      progressUpdates: false,
      fallbacks: false,
      cacheReadRatio: 0.1,
      cacheTtlLong: provider === "anthropic" ? "1h" : null,
      serviceTiers: [],
      maxOutput: provider === "anthropic" ? 64000 : 128000,
    },
  };
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

/** Claude Code's aliases and the tier each stands for on another provider. */
const ALIAS_TIERS = new Map<string, Tier>([
  ["haiku", "fast"],
  ["sonnet", "balanced"],
  ["opus", "high"],
  ["fable", "max"],
]);

export interface ResolveContext {
  /** The model of the chat (or run) the name is resolved for: its provider is the one aliases map to. */
  chatModel: string;
  hasKey(p: ProviderId): boolean;
}

/**
 * A command's, agent's or /model's model name as a model id; null: use the chat's own (docs/providers/design.md 7.4).
 * Aliases resolve inside the chat's provider; a registry model whose provider has no key becomes its tier's model on
 * the chat's provider; an unknown id with a known prefix is kept only when its provider has a key.
 */
export function resolveModel(name: string | null | undefined, ctx: ResolveContext): string | null {
  const a = (name ?? "").trim().toLowerCase();
  if (!a || a === "inherit") return null;
  const chatProvider = inferProvider(ctx.chatModel);
  const aliasTier = ALIAS_TIERS.get(a);
  if (aliasTier) {
    if (chatProvider === "anthropic") return MODELS.find((m) => m.id.startsWith(`claude-${a}-`))?.id ?? null;
    return tierModel(aliasTier, chatProvider).model;
  }
  const known = MODELS.find((m) => m.id === a || m.label.toLowerCase() === a);
  if (known) return ctx.hasKey(known.provider) ? known.id : tierModel(known.tier, chatProvider).model;
  const p = prefixProvider(a);
  return p && ctx.hasKey(p) ? a : null;
}

/** resolveModel on a Claude chat with every key present: the meaning model names had before providers. */
export function modelFromAlias(alias: string | null | undefined): string | null {
  return resolveModel(alias, { chatModel: "claude-opus-5-5", hasKey: () => true });
}

// ---- helpers for the panel (EDGES: store, pickers, Options) ----

/**
 * The provider ribbon tool runs use: the default model's when it has a key, else the first provider (in the adapters'
 * order) that has one, else Anthropic (and the no-key notice follows).
 */
export function pickToolProvider(defaultModel: string, hasKey: (p: ProviderId) => boolean, order: ProviderId[]): ProviderId {
  const own = inferProvider(defaultModel);
  if (hasKey(own)) return own;
  return order.find(hasKey) ?? "anthropic";
}

/** The efforts a model offers, as picker rows. */
export function effortsFor(model: string) {
  const offered = modelOption(model).caps.efforts;
  return EFFORTS.filter((e) => offered.includes(e.id));
}

/** The nearest effort a model offers (the list runs low to max; a tie goes down). */
export function clampEffort(effort: Effort, model: string): Effort {
  const offered = modelOption(model).caps.efforts;
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

/**
 * What a model picker lists: the models of providers that have a key, plus the current model (whatever it is). With no
 * key at all, the current model's provider (the first run looks as before).
 */
export function pickerModels(current: string, hasKey: (p: ProviderId) => boolean): ModelInfo[] {
  const keyed = [...new Set(MODELS.map((m) => m.provider))].filter(hasKey);
  const shown = keyed.length ? keyed : [inferProvider(current)];
  const list = MODELS.filter((m) => shown.includes(m.provider) || m.id === current);
  if (current && !list.some((m) => m.id === current)) list.push(modelOption(current));
  return list;
}

/** The picker's groups, in provider order; `header` is false when only one provider is listed (it looks as before). */
export function groupModels(models: ModelInfo[], order: ProviderId[]): { provider: ProviderId; header: boolean; models: ModelInfo[] }[] {
  const providers = order.filter((p) => models.some((m) => m.provider === p));
  for (const m of models) if (!providers.includes(m.provider)) providers.push(m.provider);
  const header = providers.length > 1;
  return providers.map((provider) => ({ provider, header, models: models.filter((m) => m.provider === provider) }));
}

/** OpenAI's speed setting (service_tier): Standard sends nothing. */
export const SPEEDS: { id: NonNullable<Settings["openaiSpeed"]>; label: string; note: string }[] = [
  { id: "standard", label: "Standard", note: "" },
  { id: "fast", label: "Fast", note: "about 2× the cost" },
  { id: "ultrafast", label: "Ultrafast", note: "about 6× the cost" },
];

export function speedLabel(speed: Settings["openaiSpeed"]): string {
  return SPEEDS.find((s) => s.id === speed)?.label ?? "Standard";
}

/** /model: a name per resolveModel, else a label or id containing it. Only registry models. */
export function findModel(query: string, ctx: ResolveContext): ModelInfo | null {
  const a = query.trim().toLowerCase();
  if (!a) return null;
  const id = resolveModel(a, ctx) ?? MODELS.find((x) => x.label.toLowerCase().includes(a) || x.id.includes(a))?.id;
  return MODELS.find((m) => m.id === id) ?? null;
}

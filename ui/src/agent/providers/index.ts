import { inferProvider } from "../../lib/models";
import type { ProviderId, StoredMessage } from "../../lib/types";
import { anthropicProvider } from "./anthropic";
import { errorText, type ProviderError } from "./errors";
import { openaiProvider } from "./openai/client";
import type { DisplayPart, Provider } from "./types";

/**
 * Every provider, in the order Options lists them and the fallback order for tool runs. A third provider is one
 * adapter added here plus its rows in lib/models.ts (docs/providers/design.md 10).
 */
export const PROVIDERS: Provider[] = [anthropicProvider, openaiProvider];

export function getProvider(id: ProviderId): Provider {
  const p = PROVIDERS.find((x) => x.id === id);
  if (!p) throw new Error(`Unknown model provider '${id}'.`);
  return p;
}

/** The provider a model id runs on. */
export function providerOf(modelId: string): Provider {
  return getProvider(inferProvider(modelId));
}

/**
 * How a stored message shows in the chat: its provider's display (OpenAI: progress notes and web steps in output
 * order) or one part per content block, in order (Claude, and every user message).
 */
export function displayParts(m: StoredMessage): DisplayPart[] {
  const own = m.replay ? PROVIDERS.find((p) => p.id === m.replay?.provider)?.display?.(m) : undefined;
  if (own) return own;
  const content = typeof m.content === "string" ? [] : m.content;
  return content.map((_, index) => ({ kind: "block", index }));
}

/** The user text for a provider error, with the provider's account name. */
export function providerErrorText(err: ProviderError): string {
  return errorText(err, { vendor: getProvider(err.provider).vendor });
}

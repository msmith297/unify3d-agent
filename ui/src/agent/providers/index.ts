// CORE-STUB: the CORE track (docs/providers/design.md 4.1, 9.2) owns this file and the adapters behind it; integration
// keeps CORE's copy. Only what EDGES reads is here: the user-facing facts of each provider and a plain testKey.
import type { ProviderId } from "../../lib/types";
import { inferProvider } from "../../lib/models";
import { ProviderError, type ErrorKind } from "./errors";
import type { Provider } from "./types";

const notWired = (): never => {
  throw new Error("This provider's adapter isn't in this build yet."); // CORE-STUB
};

/** GET /v1/models with the key: 200 means it works (a free call). */
async function checkKey(id: ProviderId, url: string, headers: Record<string, string>) {
  try {
    const res = await fetch(url, { method: "GET", headers });
    if (res.ok) return { ok: true as const };
    const kind: ErrorKind = res.status === 401 || res.status === 403 ? "auth" : res.status === 429 ? "rate" : res.status >= 500 ? "overloaded" : "other";
    return { ok: false as const, error: new ProviderError(id, kind, res.status, `HTTP ${res.status}`) };
  } catch (err: any) {
    return { ok: false as const, error: new ProviderError(id, "network", undefined, err?.message ?? String(err)) };
  }
}

/** The adapters, in the order Options, pickers and the tour list them (a third provider is added here). */
export const PROVIDERS: Provider[] = [
  {
    id: "anthropic",
    label: "Claude",
    vendor: "Anthropic",
    origin: "https://api.anthropic.com",
    envKey: "ANTHROPIC_API_KEY",
    keyPlaceholder: "sk-ant-…",
    keysUrl: "https://platform.claude.com/settings/keys",
    stallMs: 90_000,
    open: notWired,
    testKey: (key) =>
      checkKey("anthropic", "https://api.anthropic.com/v1/models", {
        "x-api-key": key,
        "anthropic-version": "2023-06-01",
        "anthropic-dangerous-direct-browser-access": "true",
      }),
  },
  {
    id: "openai",
    label: "OpenAI",
    vendor: "OpenAI",
    origin: "https://api.openai.com",
    envKey: "OPENAI_API_KEY",
    keyPlaceholder: "sk-proj-…",
    keysUrl: "https://platform.openai.com/api-keys",
    stallMs: 180_000,
    open: notWired,
    testKey: (key) => checkKey("openai", "https://api.openai.com/v1/models", { authorization: `Bearer ${key}` }),
  },
];

export function getProvider(id: ProviderId): Provider {
  return PROVIDERS.find((p) => p.id === id) ?? PROVIDERS[0];
}

export function providerOf(modelId: string): Provider {
  return getProvider(inferProvider(modelId));
}

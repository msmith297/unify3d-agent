import type { ProviderId } from "../../lib/types";

/** What went wrong with a provider request; store.ts shows the text for the kind (errorText). */
export type ErrorKind = "auth" | "access" | "rate" | "quota" | "overloaded" | "context" | "policy" | "tier" | "network" | "other";

/** A failed provider request. The message is already redacted (no key fragments). */
export class ProviderError extends Error {
  provider: ProviderId;
  kind: ErrorKind;
  /** HTTP status, kept for the existing `err.status` checks. */
  status: number | undefined;
  /** x-request-id / request-id: may be logged, never the key. */
  requestId?: string;
  /** The speed tier a `tier` error is about. */
  tier?: string;

  constructor(provider: ProviderId, kind: ErrorKind, status: number | undefined, message: string, requestId?: string) {
    super(message);
    this.name = "ProviderError";
    this.provider = provider;
    this.kind = kind;
    this.status = status;
    this.requestId = requestId;
  }
}

/** The reply couldn't be read (unparseable tool arguments, a stream with no end): runAgent re-sends up to 2 times. */
export class MalformedOutputError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "MalformedOutputError";
  }
}

/** The request was aborted (by the user or the stall watchdog). */
export class AbortedError extends Error {
  constructor(message = "Request was aborted.") {
    super(message);
    this.name = "AbortedError";
  }
}

const TIER_LABELS: Record<string, string> = { fast: "Fast", ultrafast: "Ultrafast" };

/**
 * The text the user sees for a provider error (docs/providers/design.md 4.4). Anthropic's texts are the ones the panel
 * showed before the provider layer; `vendor` is the provider's account name ("Anthropic", "OpenAI").
 */
export function errorText(err: ProviderError, ctx: { vendor: string }): string {
  switch (err.kind) {
    case "auth":
      return err.provider === "anthropic" ? "The API key was rejected. Check it in Settings." : `The ${ctx.vendor} API key was rejected. Check it in Settings.`;
    case "access":
      return `${ctx.vendor} refused this request from your location or network. Check your ${ctx.vendor} account's access.`;
    case "rate":
      return "Rate limited by the API. Wait a moment and press Retry.";
    case "quota":
      return err.provider === "openai"
        ? "Your OpenAI account is out of credit or over its spend limit. Check billing on platform.openai.com, then press Retry."
        : `Your ${ctx.vendor} account is out of credit or over its spend limit. Check its billing, then press Retry.`;
    case "overloaded":
      return "The API is overloaded right now. Press Retry in a moment.";
    case "context":
      return "This chat is too long for the model. Start a new chat, or ask for a summary in a new one.";
    case "policy":
      return `${ctx.vendor} stopped this run after a safety check. Review the agent's changes before you continue.`;
    case "tier":
      return `Your ${ctx.vendor} project doesn't allow the ${TIER_LABELS[err.tier ?? ""] ?? err.tier ?? "chosen"} speed. Set Speed to Standard in Options.`;
    default:
      return err.message;
  }
}

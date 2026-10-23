// CORE-STUB: C0 (docs/providers/design.md 4.2, 4.4) writes this file; integration keeps CORE's copy. Fields are declared
// rather than parameter properties so node's type stripping can load it (the .check.mjs files).
import type { ProviderId } from "../../lib/types";

export type ErrorKind = "auth" | "rate" | "quota" | "overloaded" | "context" | "policy" | "tier" | "network" | "other";

export class ProviderError extends Error {
  provider: ProviderId;
  kind: ErrorKind;
  /** HTTP status, kept for the existing `err.status` checks. */
  status: number | undefined;
  /** x-request-id / request-id: may be logged, never the key. */
  requestId?: string;
  constructor(provider: ProviderId, kind: ErrorKind, status: number | undefined, message: string, requestId?: string) {
    super(message); // already redacted (redactKeys)
    this.provider = provider;
    this.kind = kind;
    this.status = status;
    this.requestId = requestId;
  }
}
export class MalformedOutputError extends Error {}
export class AbortedError extends Error {}

const VENDOR: Record<ProviderId, string> = { anthropic: "Anthropic", openai: "OpenAI" };

/** The user's text for an error (design 4.4). Anthropic's texts are exactly today's. */
export function errorText(err: ProviderError, ctx: { tier?: string } = {}): string {
  const vendor = VENDOR[err.provider] ?? err.provider;
  switch (err.kind) {
    case "auth":
      return err.provider === "anthropic" ? "The API key was rejected. Check it in Settings." : `The ${vendor} API key was rejected. Check it in Settings.`;
    case "rate":
      return "Rate limited by the API. Wait a moment and press Retry.";
    case "quota":
      return `Your ${vendor} account is out of credit or over its spend limit. Check billing on platform.openai.com, then press Retry.`;
    case "overloaded":
      return "The API is overloaded right now. Press Retry in a moment.";
    case "context":
      return "This chat is too long for the model. Start a new chat, or ask for a summary in a new one.";
    case "policy":
      return `${vendor} stopped this run after a safety check. Review the agent's changes before you continue.`;
    case "tier":
      return `Your ${vendor} project doesn't allow the ${ctx.tier ?? "chosen"} speed. Set Speed to Standard in Options.`;
    default:
      return err.message;
  }
}

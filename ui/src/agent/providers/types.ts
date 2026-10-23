import type { Effort, ProviderId, StoredMessage, ToolDef, Usage } from "../../lib/types";
import type { ModelInfo } from "../../lib/models";
import type { LiveBlock } from "../runAgent";
import type { ProviderError } from "./errors";

/**
 * The provider layer (docs/providers/design.md 4): runAgent's loop is provider-neutral and asks a Provider for one
 * streamed model request at a time. The stored history stays in the Anthropic block format; each adapter translates it.
 */

/** Anthropic's stop vocabulary is the canonical one; every adapter maps to it. */
export type StopReason = "end_turn" | "tool_use" | "max_tokens" | "refusal" | "pause_turn";

/** Today's CLEAR_OLD_TOOL_USES, with exclude_tools already filtered to this run's tools. */
export interface ClearingRules {
  triggerTokens: number; // 40000
  keepToolUses: number; // 6
  clearAtLeastTokens: number; // 12000
  clearInputs: boolean; // true
  excludeTools: string[];
}

export interface TurnRequest {
  model: ModelInfo;
  effort: Effort;
  system: string;
  /** Add-in tools then local tools, canonical shape (input_schema), in today's order. */
  tools: ToolDef[];
  /** The thread as stored (unrepaired): adapters repair and translate it. */
  messages: StoredMessage[];
  /** Web research for this run: main agent only and the setting on. */
  web: boolean;
  longRun: boolean;
  clearing: ClearingRules;
  /** OpenAI speed setting; adapters that have no such thing ignore it. */
  serviceTier?: "fast" | "ultrafast";
}

export interface TurnResult {
  /** Canonical assistant message to append (with `replay` when the provider uses it). */
  message: StoredMessage;
  stop: StopReason;
  /** A refusal's category, when the provider gives one. */
  stopDetail?: string;
  usage: Usage;
  /** The speed tier actually served (OpenAI), when not the standard one. */
  servedTier?: string;
}

export interface TurnIO {
  /** Aborts this one request (the watchdog or the user). */
  signal: AbortSignal;
  /** The live blocks so far; the adapter calls it as events arrive (runAgent batches with requestAnimationFrame). */
  onLive(blocks: LiveBlock[]): void;
}

export interface ProviderSession {
  /**
   * One model request, streamed. Throws AbortedError (aborted by the signal), MalformedOutputError (the reply could
   * not be read: unparseable tool JSON and the like; runAgent re-sends up to 2 times), or ProviderError.
   */
  turn(req: TurnRequest, io: TurnIO): Promise<TurnResult>;
}

export interface Provider {
  id: ProviderId;
  /** Names in user-facing text: "Claude" / "OpenAI" for the service... */
  label: string;
  /** ...and "Anthropic" / "OpenAI" for the account. */
  vendor: string;
  /** Exact origin for the CSP check, e.g. "https://api.anthropic.com". */
  origin: string;
  /** The environment variable the add-in falls back to, e.g. "ANTHROPIC_API_KEY". */
  envKey: string;
  keyPlaceholder: string;
  /** Where to get a key. */
  keysUrl: string;
  /** No bytes for this long means the connection is dead (Claude 90 s, as before the provider layer). */
  stallMs: number;
  open(apiKey: string, liveness: { last: number }): ProviderSession;
  /** A free call that checks a key (GET /v1/models). Network: only from the Options "Test" button. */
  testKey(apiKey: string): Promise<{ ok: true } | { ok: false; error: ProviderError }>;
  /** How a stored assistant message shows; omitted = one part per block (Claude). */
  display?(m: StoredMessage): DisplayPart[];
}

export type DisplayPart =
  | { kind: "block"; index: number } // render content[index] as before
  | { kind: "progress"; text: string } // a progress note (like Claude thinking with showProgress)
  | { kind: "web"; title: string; detail?: string; isError?: boolean };

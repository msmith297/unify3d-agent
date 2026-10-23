import Anthropic from "@anthropic-ai/sdk";
import { repairHistory } from "../history";
import { effortFor } from "../../lib/models";
import { webBetas, webTools } from "../../lib/creator";
import type { ContentBlockParam } from "../../lib/types";
import type { LiveBlock } from "../runAgent";
import { AbortedError, MalformedOutputError, ProviderError, type ErrorKind } from "./errors";
import { livenessFetch, redactKeys } from "./transport";
import type { ClearingRules, Provider, ProviderSession, StopReason, TurnIO, TurnRequest, TurnResult } from "./types";

/**
 * The Claude adapter: the request, stream handling and usage of the agent loop as they were before the provider layer,
 * moved behind the Provider interface (docs/providers/design.md 5.1). agent.check.mjs proves the requests are
 * byte-identical: keep the parameter order, the betas order and every value as they are.
 */

/**
 * Server-side context editing: once a request passes the trigger, the oldest tool calls and their results (inputs
 * included - execute_csharp cells are the bulk) are replaced by a placeholder, keeping the last few. clear_at_least
 * makes each clearing large enough to be worth the prompt-cache rewrite it causes.
 */
function clearToolUses(r: ClearingRules) {
  return {
    type: "clear_tool_uses_20250919" as const,
    trigger: { type: "input_tokens" as const, value: r.triggerTokens },
    keep: { type: "tool_uses" as const, value: r.keepToolUses },
    clear_at_least: { type: "input_tokens" as const, value: r.clearAtLeastTokens },
    clear_tool_inputs: r.clearInputs,
    exclude_tools: r.excludeTools,
  };
}

/** An Anthropic API error as a ProviderError (status and message kept: store.ts shows the same texts as before). */
function providerError(err: InstanceType<typeof Anthropic.APIError>): ProviderError {
  const status = err.status;
  const kind: ErrorKind =
    status === 401 ? "auth"
    : status === 429 ? "rate"
    : status === 529 || status === 503 ? "overloaded"
    : status === 400 && /prompt is too long/i.test(err.message) ? "context"
    : status === undefined ? "network"
    : "other";
  return new ProviderError("anthropic", kind, status, redactKeys(err.message), err.requestID ?? undefined);
}

function session(apiKey: string, liveness: { last: number }): ProviderSession {
  const client = new Anthropic({ apiKey, dangerouslyAllowBrowser: true, maxRetries: 3, fetch: livenessFetch(liveness) });
  return {
    async turn(req: TurnRequest, io: TurnIO): Promise<TurnResult> {
      const m = req.model;
      const betas = ["thinking-binding-controls-2026-08-01", "context-management-2025-06-27"];
      if (m.caps.progressUpdates) betas.push("thinking-display-updates-2026-08-18");
      if (m.caps.fallbacks) betas.push("server-side-fallback-2026-07-01");
      // Web research runs on Anthropic's side (server tools).
      betas.push(...webBetas(m.id, req.web));

      const tools: Anthropic.Beta.Messages.BetaToolUnion[] = [
        ...req.tools.map((t) => ({
          name: t.name,
          description: t.description,
          input_schema: t.input_schema,
          eager_input_streaming: true,
        })),
        ...(webTools(m.id, req.web) as unknown as Anthropic.Beta.Messages.BetaToolUnion[]),
      ];

      const live: LiveBlock[] = [];
      try {
        const stream = client.beta.messages.stream(
          {
            model: m.id,
            max_tokens: 64000,
            system: [{ type: "text", text: req.system }],
            tools,
            messages: repairHistory(req.messages),
            // Thinking is always on for these models; effort controls depth. "drop_block" keeps chats that were
            // reloaded from a file (or continued after an add-in update) valid instead of failing with a 400.
            thinking: {
              type: "adaptive",
              display: m.caps.progressUpdates ? "updates" : "summarized",
              block_binding: { prefix_mismatch_behavior: "drop_block" },
            },
            output_config: { effort: effortFor(m, req.effort) },
            // Long tool runs wait 5-60 min on gate questions: a 1-hour cache (writes cost 2x instead of 1.25x) keeps the
            // whole prompt cached across them instead of rewriting it after every answer.
            cache_control: req.longRun ? { type: "ephemeral", ttl: "1h" } : { type: "ephemeral" },
            // Old tool calls (C# cells, screenshots, model dumps) are cleared server-side once the prompt grows, keeping
            // only the most recent ones. The stored history stays complete and append-only, so thinking stays valid.
            context_management: { edits: [clearToolUses(req.clearing)] },
            ...(m.caps.fallbacks ? { fallbacks: "default" as const } : {}),
            betas,
          },
          { signal: io.signal },
        );

        for await (const ev of stream) {
          if (ev.type === "content_block_start") {
            const b = ev.content_block;
            live[ev.index] =
              b.type === "text" ? { type: "text", text: "" }
              : b.type === "thinking" ? { type: "thinking", text: "" }
              : b.type === "tool_use" ? { type: "tool_use", text: "", name: b.name, id: b.id, json: "" }
              : { type: "other", text: "" };
            io.onLive(live);
          } else if (ev.type === "content_block_delta") {
            const b = live[ev.index];
            if (!b) continue;
            if (ev.delta.type === "text_delta") b.text += ev.delta.text;
            else if (ev.delta.type === "thinking_delta") b.text += ev.delta.thinking;
            else if (ev.delta.type === "input_json_delta") b.json = (b.json ?? "") + ev.delta.partial_json;
            io.onLive(live);
          }
        }
        const message = await stream.finalMessage();

        const u = message.usage;
        const input = u.input_tokens ?? 0;
        const cacheRead = u.cache_read_input_tokens ?? 0;
        const cacheWrite = u.cache_creation_input_tokens ?? 0;
        return {
          message: { role: "assistant", content: message.content as unknown as ContentBlockParam[] },
          // Passed through as the API gives it (stop_sequence and others included).
          stop: (message.stop_reason ?? "end_turn") as StopReason,
          stopDetail: message.stop_details?.category ?? undefined,
          usage: { input, output: u.output_tokens ?? 0, cacheRead, cacheWrite, context: input + cacheRead + cacheWrite },
        };
      } catch (err) {
        if (err instanceof Anthropic.APIUserAbortError) throw new AbortedError();
        if (err instanceof Anthropic.APIError) throw providerError(err);
        // With eager input streaming, unparseable tool JSON rejects finalMessage: runAgent re-issues the turn.
        throw new MalformedOutputError(err instanceof Error ? err.message : String(err));
      }
    },
  };
}

export const anthropicProvider: Provider = {
  id: "anthropic",
  label: "Claude",
  vendor: "Anthropic",
  origin: "https://api.anthropic.com",
  envKey: "ANTHROPIC_API_KEY",
  keyPlaceholder: "sk-ant-…",
  keysUrl: "https://platform.claude.com/settings/keys",
  stallMs: 90_000,
  open: session,
  async testKey(apiKey) {
    const client = new Anthropic({ apiKey, dangerouslyAllowBrowser: true, maxRetries: 1 });
    try {
      await client.models.list({ limit: 1 });
      return { ok: true };
    } catch (err) {
      if (err instanceof Anthropic.APIError) return { ok: false, error: providerError(err) };
      return { ok: false, error: new ProviderError("anthropic", "network", undefined, redactKeys(err instanceof Error ? err.message : String(err))) };
    }
  },
};

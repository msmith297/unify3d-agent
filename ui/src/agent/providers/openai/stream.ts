import type { ModelInfo } from "../../../lib/models";
import type { ContentBlockParam } from "../../../lib/types";
import type { LiveBlock } from "../../runAgent";
import { MalformedOutputError } from "../errors";
import type { StopReason, TurnResult } from "../types";

/**
 * Responses API stream events as live blocks, and the final `response` as a canonical message, stop and usage
 * (docs/providers/design.md 6.5, 6.10).
 */

/**
 * The canonical blocks of a response's output: one text block per output_text part, one tool_use per function_call.
 * Reasoning and web_search_call items give no blocks (they live in `replay`). Throws MalformedOutputError when a
 * complete call's arguments aren't JSON.
 */
export function canonicalBlocks(output: any[]): { blocks: ContentBlockParam[]; refused: boolean; calls: number } {
  const blocks: ContentBlockParam[] = [];
  let refused = false;
  let calls = 0;
  for (const item of output) {
    if (item?.type === "message") {
      for (const part of item.content ?? []) {
        if (part.type === "output_text") blocks.push({ type: "text", text: part.text ?? "" });
        else if (part.type === "refusal") refused = true;
      }
    } else if (item?.type === "function_call") {
      calls++;
      let input: unknown = {};
      // A call cut off by the output limit is never parsed: runAgent reports the cut-off instead.
      if (item.status !== "incomplete" && item.arguments) {
        try {
          input = JSON.parse(item.arguments);
        } catch (err) {
          throw new MalformedOutputError(`Unable to parse the arguments of ${item.name} from the model: ${err instanceof Error ? err.message : String(err)}`);
        }
      }
      blocks.push({ type: "tool_use", id: item.call_id, name: item.name, input });
    }
  }
  return { blocks, refused, calls };
}

/** Collects the live blocks (by output_index) while events arrive, and the final response from the terminal event. */
export class Assembler {
  live: LiveBlock[] = [];
  final: any = null;

  /** Applies one event; true when the live blocks changed. Throws for response.failed (the caller maps the error). */
  handle(ev: any): boolean {
    const b = typeof ev.output_index === "number" ? this.live[ev.output_index] : undefined;
    switch (ev.type) {
      case "response.output_item.added": {
        const item = ev.item ?? {};
        this.live[ev.output_index] =
          item.type === "message" ? { type: "text", text: "" }
          : item.type === "reasoning" ? { type: "thinking", text: "" }
          : item.type === "function_call" ? { type: "tool_use", text: "", name: item.name, id: item.call_id, json: "" }
          : { type: "other", text: "" };
        return true;
      }
      case "response.output_text.delta":
      case "response.refusal.delta":
      case "response.reasoning_summary_text.delta":
        if (!b) return false;
        b.text += ev.delta ?? "";
        return true;
      case "response.reasoning_summary_part.added":
        if (!b || !b.text) return false;
        b.text += "\n\n";
        return true;
      case "response.function_call_arguments.delta":
        if (!b) return false;
        b.json = (b.json ?? "") + (ev.delta ?? "");
        return true;
      case "response.completed":
      case "response.incomplete":
        this.final = ev.response;
        return false;
      case "response.failed":
        throw new FailedResponse(ev.response?.error ?? null);
      default:
        return false;
    }
  }
}

/** response.failed: the response's error object (code, message, misalignment). */
export class FailedResponse extends Error {
  error: any;
  constructor(error: any) {
    super(error?.message ?? "The response failed.");
    this.error = error;
  }
}

/** The final response as the turn's result: the canonical message with `replay`, the stop, usage and served tier. */
export function finishTurn(response: any, model: ModelInfo): TurnResult {
  const output: any[] = Array.isArray(response?.output) ? response.output : [];
  const { blocks, refused, calls } = canonicalBlocks(output);
  let stop: StopReason;
  let stopDetail: string | undefined;
  if (response?.status === "incomplete") {
    const reason = response.incomplete_details?.reason;
    if (reason === "content_filter") {
      stop = "refusal";
      stopDetail = "content filter";
    } else {
      stop = "max_tokens";
      if (reason && reason !== "max_output_tokens") stopDetail = reason;
    }
  } else {
    stop = calls > 0 ? "tool_use" : refused ? "refusal" : "end_turn";
  }

  const u = response?.usage ?? {};
  const inputTokens = u.input_tokens ?? 0;
  const cacheRead = u.input_tokens_details?.cached_tokens ?? 0;
  const cacheWrite = u.input_tokens_details?.cache_write_tokens ?? 0;
  const tier = response?.service_tier;
  return {
    message: { role: "assistant", content: blocks, replay: { provider: "openai", model: model.id, data: { output } } },
    stop,
    ...(stopDetail ? { stopDetail } : {}),
    usage: { input: Math.max(0, inputTokens - cacheRead - cacheWrite), output: u.output_tokens ?? 0, cacheRead, cacheWrite, context: inputTokens },
    ...(tier && tier !== "default" && tier !== "auto" ? { servedTier: tier } : {}),
  };
}

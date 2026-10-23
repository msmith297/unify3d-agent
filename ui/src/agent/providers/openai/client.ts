import OpenAI from "openai";
import { effortFor } from "../../../lib/models";
import { AbortedError, MalformedOutputError, ProviderError, type ErrorKind } from "../errors";
import { livenessFetch, redactKeys } from "../transport";
import type { Provider, ProviderSession, TurnIO, TurnRequest, TurnResult } from "../types";
import { openaiDisplay } from "./display";
import { clearOldCalls, toInput, toolDefs } from "./history";
import { Assembler, FailedResponse, finishTurn } from "./stream";

/**
 * The OpenAI adapter (docs/providers/design.md 6): the Responses API, streamed, stateless (store: false, the whole
 * history every time), with the canonical history translated at request time.
 */

/** 429s that waiting won't fix: the account is out of credit or over a spend limit. */
const QUOTA_CODES = new Set([
  "insufficient_quota",
  "credit_balance_exhausted",
  "organization_spend_limit_exceeded",
  "project_spend_limit_exceeded",
  "organization_usage_limit_exceeded",
]);

/**
 * livenessFetch plus the 429 rule: the SDK retries every 429, so a quota 429 is marked `x-should-retry: false`
 * (honoured by the SDK) and fails at once instead of after the retries' backoff.
 */
export function openaiFetch(liveness: { last: number }) {
  const inner = livenessFetch(liveness);
  return async (input: RequestInfo | URL, init?: RequestInit) => {
    const res = await inner(input, init);
    if (res.status !== 429) return res;
    const text = await res.text();
    let code: unknown;
    try {
      const err = JSON.parse(text)?.error;
      code = err?.code ?? err?.type;
    } catch {
      /* not JSON: an ordinary rate limit */
    }
    const headers = new Headers(res.headers);
    if (typeof code === "string" && QUOTA_CODES.has(code)) headers.set("x-should-retry", "false");
    return new Response(text, { status: res.status, statusText: res.statusText, headers });
  };
}

/** The kind of an OpenAI error, from its HTTP status and error code (section 4.4). */
function kindOf(status: number | undefined, code: string | undefined, err: any): ErrorKind {
  if (code === "misalignment_policy_violation" || err?.misalignment) return "policy";
  if (status === 401) return "auth";
  if (status === 403) return "access"; // a country, region or IP block: the key itself is fine
  if (status === 429 || code === "rate_limit_exceeded" || code === "slow_down") return code && QUOTA_CODES.has(code) ? "quota" : "rate";
  if (code && QUOTA_CODES.has(code)) return "quota";
  if (code === "context_length_exceeded") return "context";
  if (status === 400 && (err?.param === "service_tier" || /service[_ ]tier/i.test(String(err?.message ?? "")))) return "tier";
  if ((status !== undefined && status >= 500) || code === "server_is_overloaded" || code === "server_error") return "overloaded";
  return "other";
}

/** Any error out of the SDK or the stream as AbortedError, MalformedOutputError or ProviderError. */
export function mapError(err: unknown, tier?: string): Error {
  if (err instanceof AbortedError || err instanceof MalformedOutputError || err instanceof ProviderError) return err;
  if (err instanceof OpenAI.APIUserAbortError) return new AbortedError();
  if (err instanceof FailedResponse) {
    const e = err.error ?? {};
    return new ProviderError("openai", kindOf(undefined, e.code, e), undefined, redactKeys(String(e.message ?? err.message)));
  }
  if (err instanceof OpenAI.APIConnectionError) return new ProviderError("openai", "network", undefined, redactKeys(err.message));
  if (err instanceof OpenAI.APIError) {
    const e = (err.error ?? {}) as any;
    const kind = kindOf(err.status, err.code ?? e.code ?? undefined, e);
    const pe = new ProviderError("openai", kind, err.status, redactKeys(err.message), err.requestID ?? undefined);
    if (kind === "tier") pe.tier = tier;
    return pe;
  }
  // Anything else (a stream that couldn't be read): the turn is re-sent.
  return new MalformedOutputError(err instanceof Error ? err.message : String(err));
}

/** The request body; the key order is fixed so the body is byte-stable between requests (section 6.2). */
export function requestBody(req: TurnRequest): Record<string, unknown> {
  const m = req.model;
  const web = req.web && m.caps.web === "openai";
  const tools = toolDefs(req.tools, web);
  const { items } = clearOldCalls(toInput(req), req.clearing, tools);
  const body: Record<string, unknown> = {
    model: m.id,
    input: items,
    tools,
    // Constant: changing it breaks the cache. The loop still runs the calls one after another.
    parallel_tool_calls: true,
    max_output_tokens: 64000,
    reasoning: { effort: effortFor(m, req.effort), summary: "auto" },
    store: false,
    stream: true,
  };
  if (web) {
    body.include = ["web_search_call.action.sources"];
    body.max_tool_calls = 18; // Claude's 8 searches + 10 fetches, as one cap
  }
  // Long tool runs wait 5-60 min on gate questions: keep the prompt cached across them. No GPT model has "12h" yet
  // (models.ts gptCaps): it waits for hand check H6.
  if (req.longRun && m.caps.cacheTtlLong === "12h") body.prompt_cache_options = { ttl: "12h" };
  if (req.serviceTier && m.caps.serviceTiers.includes(req.serviceTier)) body.service_tier = req.serviceTier;
  return body;
}

function session(apiKey: string, liveness: { last: number }): ProviderSession {
  const client = new OpenAI({ apiKey, dangerouslyAllowBrowser: true, maxRetries: 3, fetch: openaiFetch(liveness) });
  return {
    async turn(req: TurnRequest, io: TurnIO): Promise<TurnResult> {
      const asm = new Assembler();
      try {
        // The SDK's request types lag the API (prompt_cache_options, service tiers): the body is a plain object.
        const stream = (await client.responses.create(requestBody(req) as unknown as OpenAI.Responses.ResponseCreateParamsStreaming, {
          signal: io.signal,
        })) as AsyncIterable<unknown>;
        for await (const ev of stream) if (asm.handle(ev)) io.onLive(asm.live);
      } catch (err) {
        if (io.signal.aborted) throw new AbortedError();
        throw mapError(err, req.serviceTier);
      }
      if (io.signal.aborted) throw new AbortedError();
      // No response.completed / response.incomplete: the stream was cut; the turn is re-sent.
      if (!asm.final) throw new MalformedOutputError("The reply from OpenAI ended before it was complete.");
      return finishTurn(asm.final, req.model);
    },
  };
}

export const openaiProvider: Provider = {
  id: "openai",
  label: "OpenAI",
  vendor: "OpenAI",
  origin: "https://api.openai.com",
  envKey: "OPENAI_API_KEY",
  keyPlaceholder: "sk-proj-…",
  keysUrl: "https://platform.openai.com/api-keys",
  stallMs: 180_000,
  open: session,
  async testKey(apiKey) {
    const client = new OpenAI({ apiKey, dangerouslyAllowBrowser: true, maxRetries: 1 });
    try {
      await client.models.list();
      return { ok: true };
    } catch (err) {
      const e = mapError(err);
      return { ok: false, error: e instanceof ProviderError ? e : new ProviderError("openai", "network", undefined, redactKeys(e.message)) };
    }
  },
  display: openaiDisplay,
};


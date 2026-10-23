/**
 * Fake model APIs for the checks and the mock host (`?fake`): a fetch that answers Anthropic and OpenAI requests from a
 * script of SSE streams, JSON errors or a connection that never sends anything. No network, no keys.
 */

export type FakeStep =
  | { sse: string; headers?: Record<string, string> }
  | { status: number; json: unknown; headers?: Record<string, string> }
  /** Headers arrive, then nothing: the stall watchdog's case. */
  | { hang: true };

export interface FakeRequest {
  url: string;
  host: "anthropic" | "openai" | "other";
  method: string;
  headers: Headers;
  /** The exact body string, and the parsed body (null when it isn't JSON). */
  text: string;
  body: any;
}

/** Steps served in order, or a function that answers each request. */
export type FakeScript = FakeStep[] | ((req: FakeRequest) => FakeStep);

const hostOf = (url: string): FakeRequest["host"] =>
  url.startsWith("https://api.anthropic.com/") ? "anthropic" : url.startsWith("https://api.openai.com/") ? "openai" : "other";

/**
 * A fetch serving the script for the model APIs; every such request is pushed to `log`. Other hosts go to the fetch that
 * was in place when this one was made (the mock host's own requests). Error answers carry retry-after-ms 1 (instant
 * retries).
 */
export function fakeFetch(script: FakeScript, log: FakeRequest[] = []): typeof fetch {
  let next = 0;
  const passthrough = typeof fetch === "function" ? fetch : null;
  return (async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    if (hostOf(url) === "other" && passthrough) return passthrough(input, init);
    const text = typeof init.body === "string" ? init.body : "";
    let body: any = null;
    try {
      body = text ? JSON.parse(text) : null;
    } catch {
      /* not JSON */
    }
    const req: FakeRequest = { url, host: hostOf(url), method: init.method ?? "GET", headers: new Headers(init.headers), text, body };
    log.push(req);
    const signal = init.signal;
    if (signal?.aborted) throw new DOMException("The operation was aborted.", "AbortError");
    const step = typeof script === "function" ? script(req) : script[next++];
    if (!step) throw new Error(`fakeFetch: no answer scripted for request ${log.length} (${url})`);
    const id = { "request-id": `req_fake_${log.length}`, "x-request-id": `req_fake_${log.length}` };
    if ("hang" in step) {
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          signal?.addEventListener("abort", () => controller.error(new DOMException("The operation was aborted.", "AbortError")));
        },
      });
      return new Response(stream, { status: 200, headers: { "content-type": "text/event-stream", ...id } });
    }
    if ("sse" in step) return new Response(step.sse, { status: 200, headers: { "content-type": "text/event-stream", ...id, ...step.headers } });
    return new Response(JSON.stringify(step.json), { status: step.status, headers: { "content-type": "application/json", "retry-after-ms": "1", ...id, ...step.headers } });
  }) as typeof fetch;
}

// ---- SSE builders (for scripted replies that aren't worth a fixture file) ----

/** Server-sent events: `event:` from each data's `type` unless given. */
export function sseText(events: { event?: string; data: any }[]): string {
  return events.map((e) => `event: ${e.event ?? e.data.type}\ndata: ${JSON.stringify(e.data)}\n\n`).join("");
}

/** A Claude streamed message with these blocks (text, thinking, tool_use). */
export function anthropicStream(blocks: any[], stop = "end_turn", model = "claude-opus-5-5"): string {
  const ev: { data: any }[] = [
    {
      data: {
        type: "message_start",
        message: { id: "msg_fake", type: "message", role: "assistant", model, content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 1000, cache_creation_input_tokens: 0, cache_read_input_tokens: 0, output_tokens: 1 } },
      },
    },
  ];
  blocks.forEach((b, index) => {
    if (b.type === "text") {
      ev.push({ data: { type: "content_block_start", index, content_block: { type: "text", text: "" } } });
      ev.push({ data: { type: "content_block_delta", index, delta: { type: "text_delta", text: b.text } } });
    } else if (b.type === "thinking") {
      ev.push({ data: { type: "content_block_start", index, content_block: { type: "thinking", thinking: "", signature: "" } } });
      ev.push({ data: { type: "content_block_delta", index, delta: { type: "thinking_delta", thinking: b.thinking } } });
      ev.push({ data: { type: "content_block_delta", index, delta: { type: "signature_delta", signature: "fake-signature" } } });
    } else if (b.type === "tool_use") {
      ev.push({ data: { type: "content_block_start", index, content_block: { type: "tool_use", id: b.id, name: b.name, input: {} } } });
      ev.push({ data: { type: "content_block_delta", index, delta: { type: "input_json_delta", partial_json: JSON.stringify(b.input ?? {}) } } });
    }
    ev.push({ data: { type: "content_block_stop", index } });
  });
  ev.push({ data: { type: "message_delta", delta: { stop_reason: stop, stop_sequence: null }, usage: { output_tokens: 50 } } });
  ev.push({ data: { type: "message_stop" } });
  return sseText(ev);
}

/**
 * An OpenAI Responses stream for a final response: created, each output item added / streamed / done, then the
 * terminal event for its status (completed, incomplete or failed).
 */
export function openaiStream(response: any): string {
  let seq = 0;
  const ev: { data: any }[] = [];
  const add = (data: any) => ev.push({ data: { ...data, sequence_number: seq++ } });
  const pending = { ...response, status: "in_progress", output: [], usage: null };
  add({ type: "response.created", response: pending });
  (response.output ?? []).forEach((item: any, output_index: number) => {
    if (item.type === "message") {
      add({ type: "response.output_item.added", output_index, item: { ...item, status: "in_progress", content: [] } });
      (item.content ?? []).forEach((part: any, content_index: number) => {
        add({ type: "response.content_part.added", output_index, item_id: item.id, content_index, part: { ...part, text: part.type === "output_text" ? "" : part.text } });
        if (part.type === "output_text") add({ type: "response.output_text.delta", output_index, item_id: item.id, content_index, delta: part.text });
        if (part.type === "refusal") add({ type: "response.refusal.delta", output_index, item_id: item.id, content_index, delta: part.refusal });
        add({ type: "response.content_part.done", output_index, item_id: item.id, content_index, part });
      });
    } else if (item.type === "reasoning") {
      add({ type: "response.output_item.added", output_index, item: { ...item, summary: [], encrypted_content: undefined } });
      (item.summary ?? []).forEach((s: any, summary_index: number) => {
        add({ type: "response.reasoning_summary_part.added", output_index, item_id: item.id, summary_index, part: { type: "summary_text", text: "" } });
        add({ type: "response.reasoning_summary_text.delta", output_index, item_id: item.id, summary_index, delta: s.text });
        add({ type: "response.reasoning_summary_part.done", output_index, item_id: item.id, summary_index, part: s });
      });
    } else if (item.type === "function_call") {
      add({ type: "response.output_item.added", output_index, item: { ...item, status: "in_progress", arguments: "" } });
      add({ type: "response.function_call_arguments.delta", output_index, item_id: item.id, delta: item.arguments });
      add({ type: "response.function_call_arguments.done", output_index, item_id: item.id, arguments: item.arguments });
    } else {
      add({ type: "response.output_item.added", output_index, item: { ...item, status: "in_progress" } });
    }
    add({ type: "response.output_item.done", output_index, item });
  });
  const terminal = response.status === "incomplete" ? "response.incomplete" : response.status === "failed" ? "response.failed" : "response.completed";
  add({ type: terminal, response });
  return sseText(ev);
}

/** A finished Responses API response object with sensible usage. */
export function openaiResponse(output: any[], over: Record<string, unknown> = {}): any {
  return {
    id: "resp_fake",
    object: "response",
    created_at: 1761200000,
    status: "completed",
    model: "gpt-6.1-sol",
    output,
    usage: { input_tokens: 1000, input_tokens_details: { cached_tokens: 0, cache_write_tokens: 0 }, output_tokens: 50, output_tokens_details: { reasoning_tokens: 20 }, total_tokens: 1050 },
    service_tier: "default",
    incomplete_details: null,
    error: null,
    ...over,
  };
}

const FAKE_NOTE = "(A fake reply from the mock host: no API call was made.)";

/**
 * The mock host's answers: a new prompt gets a short tool loop (get_model_context when the run has it), a tool result
 * gets a final reply, on both providers. Key tests (GET /v1/models) succeed.
 */
export const defaultFakeScript = (req: FakeRequest): FakeStep => {
  if (req.method === "GET") return { status: 200, json: { data: [], object: "list" } };
  const b = req.body ?? {};
  const hasTool = (b.tools ?? []).some((t: any) => t.name === "get_model_context");
  if (req.host === "openai") {
    const last = (b.input ?? [])[(b.input ?? []).length - 1];
    const reasoning = { id: "rs_fake", type: "reasoning", summary: [{ type: "summary_text", text: "Reading the request." }], encrypted_content: "fake-encrypted" };
    if (last?.type === "function_call_output" || !hasTool) {
      return { sse: openaiStream(openaiResponse([reasoning, { id: "msg_fake2", type: "message", status: "completed", role: "assistant", phase: "final_answer", content: [{ type: "output_text", text: `Done. ${FAKE_NOTE}`, annotations: [] }] }])) };
    }
    return {
      sse: openaiStream(
        openaiResponse([
          reasoning,
          { id: "msg_fake1", type: "message", status: "completed", role: "assistant", phase: "commentary", content: [{ type: "output_text", text: "Looking at the model first.", annotations: [] }] },
          { id: "fc_fake", type: "function_call", status: "completed", call_id: `call_fake_${Date.now()}`, name: "get_model_context", arguments: "{}" },
        ]),
      ),
    };
  }
  const messages = b.messages ?? [];
  const last = messages[messages.length - 1];
  const answered = Array.isArray(last?.content) && last.content.some((c: any) => c.type === "tool_result");
  if (answered || !hasTool) return { sse: anthropicStream([{ type: "text", text: `Done. ${FAKE_NOTE}` }], "end_turn", b.model) };
  return {
    sse: anthropicStream(
      [
        { type: "text", text: "Looking at the model first." },
        { type: "tool_use", id: `toolu_fake_${Date.now()}`, name: "get_model_context", input: {} },
      ],
      "tool_use",
      b.model,
    ),
  };
};

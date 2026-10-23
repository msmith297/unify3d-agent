// CORE-STUB: the CORE track (docs/providers/design.md 4.1) writes the real fakeFetch with its SSE fixtures; integration
// keeps CORE's copy. This one answers just enough for the mock host's ?fake: key tests and a one-line Claude reply.

export interface FakeScript {
  reply: string;
}

export const defaultFakeScript: FakeScript = { reply: "This is a faked reply (?fake): no request left this browser." };

const sse = (events: [string, unknown][]) =>
  new Response(events.map(([event, data]) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`).join(""), {
    status: 200,
    headers: { "content-type": "text/event-stream", "request-id": "req_fake" },
  });

/** A fetch for api.anthropic.com and api.openai.com that never leaves the browser; other urls go to `real`. */
export function fakeFetch(script: FakeScript, real: typeof fetch = fetch): typeof fetch {
  return async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const host = /^https:\/\/api\.(anthropic|openai)\.com\//.exec(url)?.[1];
    if (!host) return real(input, init);
    const headers = new Headers(init?.headers);
    const key = headers.get("x-api-key") ?? headers.get("authorization")?.replace(/^Bearer /, "") ?? "";
    if (/bad|wrong/i.test(key)) {
      return new Response(JSON.stringify({ error: { type: "authentication_error", message: "invalid x-api-key" } }), { status: 401, headers: { "content-type": "application/json" } });
    }
    if (url.endsWith("/v1/models")) return new Response(JSON.stringify({ data: [] }), { status: 200, headers: { "content-type": "application/json" } });
    if (host === "anthropic") {
      return sse([
        ["message_start", { type: "message_start", message: { id: "msg_fake", type: "message", role: "assistant", model: "fake", content: [], stop_reason: null, stop_sequence: null, usage: { input_tokens: 1200, output_tokens: 1 } } }],
        ["content_block_start", { type: "content_block_start", index: 0, content_block: { type: "text", text: "" } }],
        ["content_block_delta", { type: "content_block_delta", index: 0, delta: { type: "text_delta", text: script.reply } }],
        ["content_block_stop", { type: "content_block_stop", index: 0 }],
        ["message_delta", { type: "message_delta", delta: { stop_reason: "end_turn", stop_sequence: null }, usage: { output_tokens: 12 } }],
        ["message_stop", { type: "message_stop" }],
      ]);
    }
    return new Response(JSON.stringify({ error: { message: "The OpenAI adapter isn't in this build yet." } }), { status: 501, headers: { "content-type": "application/json" } });
  };
}

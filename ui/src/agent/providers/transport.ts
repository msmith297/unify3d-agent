/**
 * The SDKs' fetch: liveness is every byte the API sends - text, thinking, reasoning summaries or keep-alive pings - so a
 * long think keeps the connection busy and a silent connection is stalled (runAgent's watchdog reads `liveness.last`).
 */
export function livenessFetch(liveness: { last: number }) {
  return async (input: RequestInfo | URL, init?: RequestInit) => {
    const res = await fetch(input, init);
    liveness.last = Date.now();
    if (!res.body) return res;
    const reader = res.body.getReader();
    const body = new ReadableStream<Uint8Array>({
      async pull(controller) {
        const { done, value } = await reader.read();
        liveness.last = Date.now();
        if (done) controller.close();
        else controller.enqueue(value);
      },
      cancel(reason) {
        return reader.cancel(reason);
      },
    });
    return new Response(body, { status: res.status, statusText: res.statusText, headers: res.headers });
  };
}

/**
 * Removes anything that looks like an API key from a provider's message before it reaches the chat, a notice, the
 * console or a log (OpenAI's 401 echoes part of the key: "Incorrect API key provided: sk-proj-...abcd").
 */
export function redactKeys(text: string): string {
  return text.replace(/\b(sk|rk|sess)-[A-Za-z0-9_\-*.]{6,}/g, "[key]");
}

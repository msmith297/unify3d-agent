// Golden check of the agent loop's Claude requests (docs/providers/design.md 5.2): runs runAgent under node with a stubbed
// fetch that answers from hand-written SSE fixtures (agent/providers/fixtures/anthropic-*.sse), and compares every request
// body (as a string), the headers that matter and what the loop reported back with the goldens in fixtures/golden/.
// The goldens were captured from the code before the provider refactor and are never regenerated: a difference is a
// regression in what Claude gets, whatever the reason.
// Run from the ui folder:  node src/lib/checks/agent.check.mjs   (--write only creates golden files that are missing)
// No network: fetch is replaced for the whole process, and the key is a fake.
import assert from "node:assert/strict";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { BASE, FIXTURES, KEY, PDF, PNG, prompt, recorder, runAgent, runner } from "./harness.mjs";

const WRITE = process.argv.includes("--write");

const t = runner();

/**
 * Replaces fetch for one run: each request takes the next scripted answer ({ sse: "file" } or { status, json }) and is
 * logged with its url, method, exact body string and the anthropic-beta / anthropic-version / content-type headers.
 * The key must be the fake one; it isn't logged.
 */
function stubFetch(script, log) {
  let i = 0;
  globalThis.fetch = async (input, init = {}) => {
    const url = typeof input === "string" ? input : (input.url ?? String(input));
    const h = new Headers(init.headers);
    assert.equal(h.get("x-api-key"), KEY, "the request carries the fake key");
    assert.equal(typeof init.body, "string", "the body is a string");
    const headers = {};
    for (const name of ["anthropic-beta", "anthropic-version", "content-type"]) if (h.has(name)) headers[name] = h.get(name);
    log.push({ url, method: init.method, headers, body: init.body });
    const step = script[i++];
    if (!step) throw new Error(`unexpected request ${i}`);
    if (step.sse) {
      const text = readFileSync(new URL(step.sse, FIXTURES), "utf8");
      return new Response(text, { status: 200, headers: { "content-type": "text/event-stream", "request-id": `req_${i}` } });
    }
    // retry-after-ms 1 (0 means "use the default backoff" to the SDK) makes its retries instant.
    return new Response(JSON.stringify(step.json), { status: step.status, headers: { "content-type": "application/json", "request-id": `req_${i}`, "retry-after-ms": "1" } });
  };
}

/** One runAgent call: its requests, its events and its result (or the error it threw: status and message). */
async function run(name, script, opts, messages, o) {
  const requests = [];
  stubFetch(script, requests);
  const rec = recorder(messages, o);
  let result;
  let thrown;
  try {
    result = await runAgent({ ...BASE, ...opts, signal: new AbortController().signal, cb: rec.cb });
  } catch (e) {
    thrown = { status: e?.status ?? null, message: String(e?.message ?? e) };
  }
  assert.equal(requests.length, script.length, `${name}: every scripted answer was asked for`);
  return JSON.parse(JSON.stringify({ name, requests, events: rec.events, ...(thrown ? { thrown } : { result }) }));
}

/** Compare with the golden file (or create it with --write when it doesn't exist). */
function golden(id, runs) {
  const file = new URL(`golden/claude-${id}.json`, FIXTURES);
  if (!existsSync(file)) {
    if (!WRITE) assert.fail(`${id}: golden file missing (run once with --write before the refactor)`);
    writeFileSync(file, JSON.stringify({ scenario: id, runs }, null, 2) + "\n");
    console.log(`   wrote golden/claude-${id}.json`);
    return;
  }
  const want = JSON.parse(readFileSync(file, "utf8")).runs;
  assert.equal(runs.length, want.length, `${id}: runs`);
  runs.forEach((r, i) => {
    const w = want[i];
    assert.equal(r.requests.length, w.requests.length, `${id}/${r.name}: request count`);
    r.requests.forEach((q, j) => {
      assert.equal(q.url, w.requests[j].url, `${id}/${r.name} request ${j + 1}: url`);
      assert.equal(q.method, w.requests[j].method, `${id}/${r.name} request ${j + 1}: method`);
      assert.deepEqual(q.headers, w.requests[j].headers, `${id}/${r.name} request ${j + 1}: headers`);
      assert.equal(q.body, w.requests[j].body, `${id}/${r.name} request ${j + 1}: body`);
    });
    assert.deepEqual(r.events, w.events, `${id}/${r.name}: events`);
    assert.deepEqual(r.result, w.result, `${id}/${r.name}: result`);
    assert.deepEqual(r.thrown, w.thrown, `${id}/${r.name}: thrown`);
  });
}

// ---- scenarios ----

await t("C1 Opus 5.5, high effort, web on: a reply with thinking", async () => {
  golden("C1", [await run("text", [{ sse: "anthropic-text.sse" }], {}, [prompt("How thick is the shell?")])]);
});

await t("C2 a tool loop: tool result with an image, a PostToolUse hook's context, Send now steering", async () => {
  const hook = (event) =>
    event === "PostToolUse" ? { block: false, additionalContext: ["Peer agent: the customer wants R2."], systemMessages: [], continue: true } : null;
  const toolResult = { content: [{ type: "text", text: "+ Fillet1 [Fillet]" }, { type: "image", source: { type: "base64", media_type: "image/png", data: PNG } }], isError: false };
  const loop = await run("loop", [{ sse: "anthropic-tool.sse" }, { sse: "anthropic-done.sse" }], {}, [prompt("Fillet the selected edges 2 mm.")], {
    hook,
    toolResult,
    steer: [{ type: "text", text: "Also check the draft." }],
  });
  golden("C2", [loop]);
  // The cached prefix: the second request repeats the first one's system, tools and messages before its new turn.
  const [a, b] = loop.requests.map((q) => q.body);
  const upToMessages = (s) => s.slice(0, s.indexOf('"messages":[') + '"messages":['.length);
  assert.equal(upToMessages(a), upToMessages(b));
  const msgs = (s) => JSON.stringify(JSON.parse(s).messages);
  assert.ok(msgs(b).startsWith(msgs(a).slice(0, -1)));
});

await t("C3 Sonnet 5.5, long run (1 h cache), web on (dynamic web tools, no web beta)", async () => {
  golden("C3", [await run("long", [{ sse: "anthropic-done.sse" }], { model: "claude-sonnet-5-5", effort: "medium", longRun: true, maxSteps: 400 }, [prompt("[Run tool: flash-grooves]")])]);
});

await t("C4 Haiku 5.5: no fallbacks, summarized thinking, basic web tools and the web-fetch beta", async () => {
  golden("C4", [await run("haiku", [{ sse: "anthropic-done.sse" }], { model: "claude-haiku-5-5", effort: "low" }, [prompt("What is selected?")])]);
});

await t("C5 run_agent: a plugin agent on model sonnet (own prompt, no web, no local tools)", async () => {
  const agents = [
    { id: "shop:reviewer", description: "Reviews compression molds.", tools: ["get_model_context", "execute_csharp"], model: "sonnet", prompt: "You review compression molds for the shop." },
  ];
  golden("C5", [
    await run("sub", [{ sse: "anthropic-run-agent.sse" }, { sse: "anthropic-sub-reply.sse" }, { sse: "anthropic-done.sse" }], { agents }, [prompt("Have the reviewer look at the mold.")]),
  ]);
});

await t("C6 history repair: unanswered tool_use, stray tool_result, users in a row, open server call, PDF and images", async () => {
  const messages = [
    {
      role: "user",
      content: [
        { type: "text", text: "Here is the drawing and a photo." },
        { type: "document", source: { type: "base64", media_type: "application/pdf", data: PDF }, title: "drawing.pdf" },
        { type: "image", source: { type: "base64", media_type: "image/png", data: PNG } },
      ],
    },
    {
      role: "assistant",
      content: [
        { type: "thinking", thinking: "Look up the standard first.", signature: "sigA" },
        { type: "text", text: "Checking the standard." },
        { type: "server_tool_use", id: "srvtoolu_01Open", name: "web_search", input: { query: "ISO 2768" } },
        { type: "tool_use", id: "toolu_01Lost", name: "get_model_context", input: {} },
      ],
    },
    prompt("Never mind, just model it."),
    { role: "user", content: [{ type: "tool_result", tool_use_id: "toolu_01Stray", content: "stray" }, { type: "text", text: "And keep it simple." }] },
    { role: "assistant", content: [{ type: "text", text: "Modelled." }] },
    prompt("Now add a 1 mm chamfer."),
  ];
  golden("C6", [await run("repair", [{ sse: "anthropic-done.sse" }], {}, messages)]);
});

await t("C7 refusal with a category; max_tokens inside a tool call; unparseable tool JSON re-sent", async () => {
  golden("C7", [
    await run("refusal", [{ sse: "anthropic-refusal.sse" }], {}, [prompt("Do something you won't.")]),
    await run("max_tokens", [{ sse: "anthropic-max-tokens-tool.sse" }], {}, [prompt("Write a very long cell.")]),
    await run("bad json, twice", [{ sse: "anthropic-bad-json.sse" }, { sse: "anthropic-bad-json.sse" }, { sse: "anthropic-done.sse" }], {}, [prompt("Try again.")]),
    await run("bad json, three times", [{ sse: "anthropic-bad-json.sse" }, { sse: "anthropic-bad-json.sse" }, { sse: "anthropic-bad-json.sse" }], {}, [prompt("Try again.")]),
  ]);
});

await t("C8 API errors: 429 then 200 (SDK retry), 401 thrown, 529 retried then thrown", async () => {
  const err = (type, message) => ({ type: "error", error: { type, message } });
  golden("C8", [
    await run("429 then ok", [{ status: 429, json: err("rate_limit_error", "Number of request tokens has exceeded your per-minute rate limit") }, { sse: "anthropic-done.sse" }], {}, [prompt("Hi")]),
    await run("401", [{ status: 401, json: err("authentication_error", "invalid x-api-key") }], {}, [prompt("Hi")]),
    await run("529", [529, 529, 529, 529].map((status) => ({ status, json: err("overloaded_error", "Overloaded") })), {}, [prompt("Hi")]),
  ]);
});

console.log(`${t.count} agent checks passed`);

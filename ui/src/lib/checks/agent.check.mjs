// Golden check of the agent loop's Claude requests (docs/providers/design.md 5.2): runs runAgent under node with a stubbed
// fetch that answers from hand-written SSE fixtures (agent/providers/fixtures/anthropic-*.sse), and compares every request
// body (as a string), the headers that matter and what the loop reported back with the goldens in fixtures/golden/.
// The goldens were captured from the code before the provider refactor and are never regenerated: a difference is a
// regression in what Claude gets, whatever the reason.
// Run from the ui folder:  node src/lib/checks/agent.check.mjs   (--write only creates golden files that are missing)
// No network: fetch is replaced for the whole process, and the key is a fake.
import assert from "node:assert/strict";
import { registerHooks } from "node:module";
import { existsSync, readFileSync, writeFileSync } from "node:fs";

// The panel's sources import without extensions (vite resolves them); node needs the .ts.
registerHooks({
  resolve(specifier, context, nextResolve) {
    if (/^\.\.?\//.test(specifier) && !/\.[cm]?[jt]sx?$/.test(specifier)) {
      for (const ext of [".ts", ".tsx", "/index.ts"]) {
        try {
          return nextResolve(specifier + ext, context);
        } catch {
          /* next */
        }
      }
    }
    return nextResolve(specifier, context);
  },
});

// Nothing from this PC's environment may change the requests (base URL, keys).
for (const k of Object.keys(process.env)) if (k.startsWith("ANTHROPIC_") || k.startsWith("OPENAI_")) delete process.env[k];
// Synchronous frames: every live update is flushed at once, so the last live state before a request ends is complete.
globalThis.requestAnimationFrame = (f) => {
  f(0);
  return 0;
};
globalThis.cancelAnimationFrame = () => {};

const { runAgent } = await import("../../agent/runAgent.ts");

const WRITE = process.argv.includes("--write");
const FIXTURES = new URL("../../agent/providers/fixtures/", import.meta.url);
export const KEY = "test-key-not-real";

let n = 0;
const t = async (name, fn) => {
  await fn();
  n++;
  console.log("ok -", name);
};

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

/** Callbacks that record, in order, what the loop reports; `live` is the last live state before each clear. */
function recorder(messages, o = {}) {
  const events = [];
  let live = null;
  let steer = o.steer ?? null;
  const cb = {
    getMessages: () => messages,
    takeSteering: o.steer
      ? () => {
          const s = steer;
          steer = null;
          events.push({ takeSteering: s });
          return s;
        }
      : undefined,
    append: async (m) => {
      messages.push(m);
      events.push({ append: m });
    },
    setLive: (b) => {
      if (b) live = b;
      else if (live) {
        events.push({ live });
        live = null;
      }
    },
    setRunningTool: (id) => events.push({ runningTool: id }),
    addUsage: (u) => events.push({ usage: u }),
    approve: async (req) => {
      events.push({ approve: req });
      return { decision: "run" };
    },
    ask: async (id, question) => {
      events.push({ ask: { id, question } });
      return "Yes";
    },
    askSelection: async (id) => {
      events.push({ askSelection: id });
      return "[S1] Face";
    },
    askSecret: async (id) => {
      events.push({ askSecret: id });
      return { text: "skipped" };
    },
    onRequest: () => events.push({ request: true }),
    onStatus: (s) => events.push({ status: s }),
    runTool: async (name, input, id) => {
      events.push({ runTool: { name, input, id } });
      return o.toolResult ?? { content: [{ type: "text", text: "ok" }], isError: false };
    },
    isAutoApproved: () => false,
    setAutoApproved: () => events.push({ setAutoApproved: true }),
    setSubAgent: (p) => events.push({ subAgent: p }),
    hook: o.hook
      ? async (event, call) => {
          events.push({ hook: { event, call } });
          return o.hook(event, call);
        }
      : undefined,
  };
  return { cb, events };
}

// ---- the fixed inputs (a realistic main-agent run; the system prompt is built from these) ----

export const TOOLS = [
  {
    name: "execute_csharp",
    description: "Run a C# cell inside SolidWorks. Each call is one undo step named after its title.",
    input_schema: {
      type: "object",
      properties: { title: { type: "string", description: "Short undo-step title." }, code: { type: "string", description: "The cell." } },
      required: ["title", "code"],
    },
  },
  { name: "get_model_context", description: "The active document, its feature tree and the selection.", input_schema: { type: "object", properties: {} } },
  {
    name: "capture_views",
    description: "Render views of the model.",
    input_schema: { type: "object", properties: { views: { type: "array", items: { type: "string" } }, max_size: { type: "integer" } } },
  },
  { name: "read_user_tool", description: "Read a tool's TOOL.md.", input_schema: { type: "object", properties: { name: { type: "string" } }, required: ["name"] } },
];

export const BASE = {
  apiKey: KEY,
  keys: { get: async (p) => (p === "anthropic" ? KEY : "test-openai-key-not-real"), has: () => true },
  model: "claude-opus-5-5",
  effort: "high",
  tools: TOOLS,
  approval: "ask",
  realisticAppearance: true,
  verbosity: "medium",
  userTools: [{ name: "flash-grooves", title: "Flash grooves", description: "Cuts flash grooves around the cavities.", kind: "tool", category: "Molding" }],
  skills: [],
  agents: [],
  mcpInstructions: [],
  sessionContext: [],
  knowledge: null,
  webResearch: true,
  memory: null,
};

const PNG = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
const PDF = "JVBERi0xLjQKJcfsj6IKMSAwIG9iago8PD4+CmVuZG9iagp0cmFpbGVyCjw8Pj4KJSVFT0YK";
const prompt = (text) => ({ role: "user", content: [{ type: "text", text: `${text}\n\n<sw_context>\nActive document: Part1.SLDPRT\n</sw_context>` }] });

/** One runAgent call: its requests, its events and its result (or the error it threw: status and message). */
export async function run(name, script, opts, messages, o) {
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
  golden("C2", [
    await run("loop", [{ sse: "anthropic-tool.sse" }, { sse: "anthropic-done.sse" }], {}, [prompt("Fillet the selected edges 2 mm.")], {
      hook,
      toolResult,
      steer: [{ type: "text", text: "Also check the draft." }],
    }),
  ]);
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

console.log(`${n} agent checks passed`);

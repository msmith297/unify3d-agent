// End-to-end checks of the provider seams (docs/providers/design.md 9.1 step 4): full agent turns with tool calls on
// each provider through the mock host's fake streams, a chat that switches provider both ways, keys that reach only
// their own provider, sub-agent aliases on the chat's provider, speed for sub-agents and capability flags.
// Run from the ui folder:  node src/lib/checks/integration.check.mjs
// No network: fetch is the fake one (agent/providers/fake.ts), and the keys are fakes.
import assert from "node:assert/strict";
import { BASE, KEY, OPENAI_KEY, PNG, prompt, recorder, runAgent, runner } from "./harness.mjs";

const { fakeFetch, defaultFakeScript, anthropicStream, openaiStream, openaiResponse } = await import("../../agent/providers/fake.ts");
const { readableHistory, unreadableNote } = await import("../../agent/providers/media.ts");
const { repairHistory } = await import("../../agent/history.ts");
const { MODELS } = await import("../models.ts");

const t = runner();

/** Keys that record which provider asked; `only` limits which providers have one. */
function keys(only = ["anthropic", "openai"]) {
  const asked = [];
  return {
    asked,
    get: async (p) => {
      asked.push(p);
      return only.includes(p) ? (p === "anthropic" ? KEY : OPENAI_KEY) : null;
    },
    has: (p) => only.includes(p),
  };
}

/** One runAgent call against the fake fetch; every request's key is checked against its host. */
async function run(script, opts, messages) {
  const log = [];
  globalThis.fetch = fakeFetch(script, log);
  const rec = recorder(messages, { toolResult: { content: [{ type: "text", text: "Part1: 3 bodies, 41 features." }], isError: false } });
  const k = opts.keys ?? keys();
  const served = [];
  const result = await runAgent({ ...BASE, onServedTier: (s) => served.push(s), ...opts, keys: k, signal: new AbortController().signal, cb: rec.cb });
  for (const r of log) {
    if (r.host === "openai") {
      assert.equal(r.headers.get("authorization"), `Bearer ${OPENAI_KEY}`, "OpenAI gets only its own key");
      assert.equal(r.headers.get("x-api-key"), null, "no Anthropic key header on an OpenAI request");
      assert.ok(!r.text.includes(KEY), "the Anthropic key is in no OpenAI body");
    }
    if (r.host === "anthropic") {
      assert.equal(r.headers.get("x-api-key"), KEY, "Anthropic gets only its own key");
      assert.equal(r.headers.get("authorization"), null, "no bearer token on an Anthropic request");
      assert.ok(!r.text.includes(OPENAI_KEY), "the OpenAI key is in no Anthropic body");
    }
  }
  return { log, result, events: rec.events, messages, asked: k.asked, served };
}

const of = (events, key) => events.filter((e) => key in e).map((e) => e[key]);
const toolUses = (m) => (typeof m.content === "string" ? [] : m.content.filter((b) => b.type === "tool_use"));

// ---- a full turn with a tool call on each provider (the mock host's ?fake script) ----

const chat = [prompt("What is in this part?")];

await t("Claude: a full turn with a tool call through the fake streams; only the Anthropic key is asked for", async () => {
  const r = await run(defaultFakeScript, { model: "claude-opus-5-5" }, chat);
  assert.equal(r.result.stop, "end_turn");
  assert.deepEqual(r.log.map((x) => x.host), ["anthropic", "anthropic"]);
  assert.deepEqual([...new Set(r.asked)], ["anthropic"]);
  assert.deepEqual(of(r.events, "runTool").map((x) => x.name), ["get_model_context"]);
  const [, first, results, last] = chat;
  assert.equal(toolUses(first)[0].name, "get_model_context");
  assert.equal(results.content[0].type, "tool_result");
  assert.equal(results.content[0].tool_use_id, toolUses(first)[0].id);
  assert.match(last.content[0].text, /^Done\./);
  assert.ok(chat.every((m) => !("replay" in m)), "Claude never writes replay");
  // The second request carries the first one's messages (the cached prefix) and the tool result.
  assert.equal(r.log[1].body.messages.length, 3);
  assert.deepEqual(r.log[1].body.messages[0], r.log[0].body.messages[0]);
  assert.equal(r.log[1].body.messages[2].content[0].type, "tool_result");
});

await t("OpenAI: the same chat continues on GPT-6.1 Sol with a tool call; Claude's ids carry over, replay is written", async () => {
  chat.push(prompt("Now look again on the other model."));
  const before = chat.length;
  const r = await run(defaultFakeScript, { model: "gpt-6.1-sol" }, chat);
  assert.equal(r.result.stop, "end_turn");
  assert.deepEqual(r.log.map((x) => x.host), ["openai", "openai"]);
  assert.deepEqual([...new Set(r.asked)], ["openai"]);
  assert.equal(r.log[0].url, "https://api.openai.com/v1/responses");
  const input = r.log[0].body.input;
  assert.equal(input[0].role, "developer");
  // The Claude turn translated: its call and result keep their toolu_ id.
  const claudeCall = toolUses(chat[1])[0].id;
  assert.ok(input.some((i) => i.type === "function_call" && i.call_id === claudeCall));
  assert.ok(input.some((i) => i.type === "function_call_output" && i.call_id === claudeCall));
  assert.ok(!JSON.stringify(input).includes('"thinking"'));
  // The new turn: a call, its output, the final answer; every assistant message carries OpenAI's output items.
  const added = chat.slice(before);
  assert.equal(added.length, 3);
  assert.equal(added[0].replay.provider, "openai");
  assert.equal(added[0].replay.model, "gpt-6.1-sol");
  assert.equal(added[2].replay.provider, "openai");
  const second = r.log[1].body.input;
  const call = toolUses(added[0])[0];
  assert.ok(second.some((i) => i.type === "function_call_output" && i.call_id === call.id && i.output === "Part1: 3 bodies, 41 features."));
  // Reasoning items are replayed verbatim (same model family).
  assert.ok(second.some((i) => i.type === "reasoning" && i.encrypted_content === "fake-encrypted"));
});

await t("Back to Claude: OpenAI turns go as plain blocks, replay never reaches Anthropic, the old prefix is unchanged", async () => {
  chat.push(prompt("And once more on Claude."));
  const r = await run(defaultFakeScript, { model: "claude-sonnet-5-5" }, chat);
  assert.equal(r.result.stop, "end_turn");
  assert.deepEqual([...new Set(r.asked)], ["anthropic"]);
  const body = r.log[0].body;
  assert.ok(!r.log[0].text.includes('"replay"'), "no replay in a Claude request");
  assert.ok(!r.log[0].text.includes("fake-encrypted"), "no OpenAI reasoning in a Claude request");
  // The Claude request's messages are exactly the repaired canonical history.
  assert.deepEqual(body.messages, JSON.parse(JSON.stringify(repairHistory(chat.slice(0, body.messages.length)))));
  const openaiCall = body.messages.flatMap((m) => (typeof m.content === "string" ? [] : m.content)).find((b) => b.type === "tool_use" && b.id.startsWith("call_"));
  assert.ok(openaiCall, "the OpenAI call is a tool_use with its call_ id");
});

// ---- keys and sub-agents ----

await t("A model whose provider has no key: no request, the short notice", async () => {
  const r = await run([], { model: "gpt-6-astra", keys: keys(["anthropic"]) }, [prompt("Hi.")]);
  assert.equal(r.log.length, 0);
  assert.equal(r.result.stop, "error");
  assert.equal(r.result.error, "GPT-6 Astra needs an OpenAI API key. Add one in Options, or pick another model.");
});

const reviewer = (model) => [{ id: "shop:reviewer", description: "Reviews molds.", tools: ["get_model_context"], model, prompt: "You review molds." }];
/** A parent turn that calls run_agent once, then the sub-agent's reply, then the parent's last words. */
const subScript = (parent) => (req) => {
  if (req.method === "GET") return { status: 200, json: { data: [] } };
  const b = req.body;
  const isSub = (b.system?.[0]?.text ?? b.input?.[0]?.content?.[0]?.text ?? "").includes("You review molds.");
  if (isSub) {
    return req.host === "openai"
      ? { sse: openaiStream(openaiResponse([{ id: "msg_s", type: "message", status: "completed", role: "assistant", content: [{ type: "output_text", text: "Looks fine.", annotations: [] }] }], { model: b.model, service_tier: b.service_tier ?? "default" })) }
      : { sse: anthropicStream([{ type: "text", text: "Looks fine." }], "end_turn", b.model) };
  }
  const answered = parent === "openai"
    ? (b.input ?? []).some((i) => i.type === "function_call_output")
    : (b.messages ?? []).some((m) => Array.isArray(m.content) && m.content.some((c) => c.type === "tool_result"));
  if (answered) {
    return parent === "openai"
      ? { sse: openaiStream(openaiResponse([{ id: "msg_d", type: "message", status: "completed", role: "assistant", content: [{ type: "output_text", text: "Done.", annotations: [] }] }])) }
      : { sse: anthropicStream([{ type: "text", text: "Done." }], "end_turn", b.model) };
  }
  const input = { agent: "shop:reviewer", prompt: "Review the mold." };
  return parent === "openai"
    ? { sse: openaiStream(openaiResponse([{ id: "fc_1", type: "function_call", status: "completed", call_id: "call_sub1", name: "run_agent", arguments: JSON.stringify(input) }])) }
    : { sse: anthropicStream([{ type: "tool_use", id: "toolu_sub1", name: "run_agent", input }], "tool_use", b.model) };
};
const subModel = (r) => r.log.find((x) => JSON.stringify(x.body).includes("You review molds.")).body.model;

await t("Sub-agent aliases resolve on the chat's provider; a model without a key falls back to its tier there", async () => {
  let r = await run(subScript("openai"), { model: "gpt-6.1-sol", agents: reviewer("sonnet") }, [prompt("Review it.")]);
  assert.equal(r.result.stop, "end_turn");
  assert.equal(subModel(r), "gpt-6.1-sol");
  r = await run(subScript("anthropic"), { model: "claude-opus-5-5", agents: reviewer("haiku") }, [prompt("Review it.")]);
  assert.equal(subModel(r), "claude-haiku-5-5");
  // An explicit Claude id on an OpenAI chat with no Anthropic key: Haiku's tier (fast) on OpenAI; Anthropic is never asked.
  r = await run(subScript("openai"), { model: "gpt-6.1-sol", agents: reviewer("claude-haiku-5-5"), keys: keys(["openai"]) }, [prompt("Review it.")]);
  assert.equal(subModel(r), "gpt-6-luna");
  assert.ok(!r.asked.includes("anthropic"));
  // With both keys, the sub-agent runs on the other provider with that provider's key.
  r = await run(subScript("openai"), { model: "gpt-6.1-sol", agents: reviewer("claude-haiku-5-5") }, [prompt("Review it.")]);
  assert.equal(subModel(r), "claude-haiku-5-5");
  assert.deepEqual([...new Set(r.asked)].sort(), ["anthropic", "openai"]);
});

await t("Speed: a Claude chat's OpenAI sub-agent gets the speed setting; Claude requests never carry it", async () => {
  const r = await run(subScript("anthropic"), { model: "claude-opus-5-5", agents: reviewer("gpt-6.1-sol"), serviceTier: "fast" }, [prompt("Review it.")]);
  const sub = r.log.find((x) => x.host === "openai");
  assert.equal(sub.body.service_tier, "fast");
  for (const x of r.log.filter((x) => x.host === "anthropic")) assert.ok(!x.text.includes("service_tier"));
  assert.deepEqual(r.served, [], "the footer shows the turn's own model: a sub-agent's tier isn't reported");
});

// ---- capability flags ----

/** Runs fn with one registry row's caps changed, then puts them back. */
async function withCaps(id, change, fn) {
  const row = MODELS.find((m) => m.id === id);
  const caps = row.caps;
  row.caps = { ...caps, ...change };
  try {
    await fn();
  } finally {
    row.caps = caps;
  }
}

await t("A model without vision or PDF support: images and PDFs become a note, and the composer says so", async () => {
  const messages = [{ role: "user", content: [{ type: "text", text: "Look." }, { type: "image", source: { type: "base64", media_type: "image/png", data: PNG } }] }];
  assert.equal(readableHistory(messages, { vision: true, pdf: true }), messages, "unchanged (same array) for every registry model");
  await withCaps("gpt-6-luna", { vision: false }, async () => {
    const r = await run(defaultFakeScript, { model: "gpt-6-luna", tools: [] }, messages.slice());
    const user = r.log[0].body.input[1];
    assert.ok(!JSON.stringify(user).includes("input_image"));
    assert.ok(JSON.stringify(user).includes("can't read images"));
    assert.equal(messages[0].content[1].type, "image", "the stored history keeps the image");
  });
  assert.equal(unreadableNote("image", "GPT-6 Luna", { vision: false, pdf: true }), "GPT-6 Luna can't read images. Pick another model, or the image is left out.");
  assert.equal(unreadableNote("pdf", "Opus 5.5", { vision: true, pdf: true }), null);
});

await t("A model without web tools: no web tool and no web wording in the prompt, even with the setting on", async () => {
  await withCaps("gpt-6-luna", { web: null }, async () => {
    const r = await run(defaultFakeScript, { model: "gpt-6-luna", tools: [] }, [prompt("Find the standard.")]);
    const b = r.log[0].body;
    assert.ok(!(b.tools ?? []).some((x) => x.type === "web_search"));
    assert.equal(b.include, undefined);
    assert.ok(!b.input[0].content[0].text.includes("web_search"));
  });
  const r = await run(defaultFakeScript, { model: "gpt-6-luna", tools: [] }, [prompt("Find the standard.")]);
  assert.ok(r.log[0].body.tools.some((x) => x.type === "web_search"), "with web caps it is there");
  assert.ok(r.log[0].body.input[0].content[0].text.includes("web_search"), "and the prompt names it");
});

console.log(`integration: ${t.count} checks passed`);

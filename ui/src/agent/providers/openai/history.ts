import { repairHistory } from "../../history";
import { modelOption, type ModelInfo } from "../../../lib/models";
import type { ContentBlockParam, ProviderReplay, StoredMessage, ToolDef } from "../../../lib/types";
import type { ClearingRules } from "../types";
import { canonicalBlocks } from "./stream";

/**
 * The canonical history (Anthropic blocks) as Responses API `input` items (docs/providers/design.md 6.3), the tool
 * definitions (6.4) and client-side context clearing (6.8). Everything here is pure: the same history always gives the
 * same items, which keeps OpenAI's prompt cache valid.
 */

// The SDK's input item types are a large union; the items are built as plain objects in a fixed key order.
export type Item = Record<string, any>;

export interface Translated {
  items: Item[];
  /** For each item, the index of the assistant message (in the repaired history) it came from, or -1. */
  owner: number[];
}

const CALL_ID = /^[A-Za-z0-9_-]{1,64}$/;

/** 64-bit FNV-1a of a string, as 16 hex digits. */
function fnv1a64(text: string): string {
  let h = 0xcbf29ce484222325n;
  for (let i = 0; i < text.length; i++) {
    h ^= BigInt(text.charCodeAt(i));
    h = (h * 0x100000001b3n) & 0xffffffffffffffffn;
  }
  return h.toString(16).padStart(16, "0");
}

/** A call id OpenAI accepts: Claude's toolu_... ids pass through; anything else gets a stable hashed id. */
export function callId(id: string): string {
  return CALL_ID.test(id) ? id : `c_${fnv1a64(id)}`;
}

const blocksOf = (m: StoredMessage): ContentBlockParam[] => (typeof m.content === "string" ? [{ type: "text", text: m.content }] : m.content);

/** A user-side block (message content or a tool result's content) as an input content part; null = not sendable. */
function inputPart(b: any): Item | null {
  if (b.type === "text") return { type: "input_text", text: b.text };
  if (b.type === "image") {
    const s = b.source;
    if (s?.type === "base64") return { type: "input_image", image_url: `data:${s.media_type};base64,${s.data}`, detail: "auto" };
    if (s?.type === "url") return { type: "input_image", image_url: s.url, detail: "auto" };
    return null;
  }
  if (b.type === "document") {
    const s = b.source;
    if (s?.type === "base64") return { type: "input_file", filename: b.title ?? "document.pdf", file_data: `data:${s.media_type ?? "application/pdf"};base64,${s.data}` };
    if (s?.type === "text") return { type: "input_text", text: s.data };
    if (s?.type === "url") return { type: "input_file", file_url: s.url };
    return null;
  }
  return null;
}

/** A tool_result as a function_call_output: plain text as one string; images and files as a content array. */
function callOutput(b: any): Item {
  const content: any[] = typeof b.content === "string" ? [{ type: "text", text: b.content }] : (b.content ?? []);
  let output: string | Item[];
  if (content.every((c) => c.type === "text")) {
    const text = content.map((c) => c.text).join("\n\n");
    output = b.is_error ? `Error: ${text}` : text;
  } else {
    const parts = content.map(inputPart).filter((p): p is Item => p !== null);
    if (b.is_error) {
      if (parts[0]?.type === "input_text") parts[0] = { ...parts[0], text: `Error: ${parts[0].text}` };
      else parts.unshift({ type: "input_text", text: "Error:" });
    }
    output = parts;
  }
  return { type: "function_call_output", call_id: callId(b.tool_use_id), output };
}

/** Assistant text as an input message (the plain form: no id, status or output_text parts, which only OpenAI's own items have). */
const assistantText = (text: string, phase: unknown): Item => ({ type: "message", role: "assistant", content: text, ...(phase ? { phase } : {}) });

/** A function call without OpenAI's item id and status: what a translated call looks like. */
const plainCall = (item: Item): Item => ({ type: "function_call", call_id: item.call_id, name: item.name, arguments: item.arguments });

/** An assistant message from blocks: text as plain assistant messages, tool_use as function_call; Claude-only blocks dropped. */
function translatedAssistant(blocks: ContentBlockParam[]): Item[] {
  const phase = blocks.some((b) => b.type === "tool_use") ? "commentary" : "final_answer";
  const items: Item[] = [];
  for (const b of blocks as any[]) {
    if (b.type === "text") {
      if (b.text) items.push(assistantText(b.text, phase));
    } else if (b.type === "tool_use") {
      items.push(plainCall({ call_id: callId(b.id), name: b.name, arguments: JSON.stringify(b.input ?? {}) }));
    }
    // thinking, redacted_thinking, server_tool_use and web_*_tool_result are Claude's own.
  }
  return items;
}

/** The replay of an assistant message, if all its blocks came from one OpenAI reply that still matches them. */
function usableReplay(blocks: ContentBlockParam[], replayOf: Map<object, ProviderReplay>, model: ModelInfo): ProviderReplay | null {
  if (blocks.length === 0) return null;
  const r = replayOf.get(blocks[0]);
  if (!r || r.provider !== "openai" || blocks.some((b) => replayOf.get(b) !== r)) return null;
  if (modelOption(r.model).family !== model.family) return null;
  const output = (r.data as { output?: unknown })?.output;
  if (!Array.isArray(output)) return null;
  let derived: ContentBlockParam[];
  try {
    derived = canonicalBlocks(output).blocks;
  } catch {
    return null;
  }
  if (derived.length !== blocks.length) return null;
  const same = derived.every((d: any, i) => {
    const b = blocks[i] as any;
    if (d.type !== b.type) return false;
    return d.type === "tool_use" ? d.id === b.id : d.text === b.text;
  });
  return same ? r : null;
}

/**
 * The request's `input`: the system prompt as the first developer message (a cache boundary), then the repaired history.
 * Messages with a matching OpenAI replay are sent as OpenAI returned them; everything else is translated from blocks.
 */
export function toInput(req: { system: string; messages: StoredMessage[]; model: ModelInfo }): Translated {
  const replayOf = new Map<object, ProviderReplay>();
  for (const m of req.messages) {
    if (m.replay && typeof m.content !== "string") for (const b of m.content) replayOf.set(b, m.replay);
  }
  const items: Item[] = [{ type: "message", role: "developer", content: [{ type: "input_text", text: req.system }] }];
  const owner: number[] = [-1];
  const push = (item: Item, o: number) => {
    items.push(item);
    owner.push(o);
  };

  repairHistory(req.messages).forEach((m, k) => {
    const blocks = blocksOf(m);
    if (m.role === "user") {
      // Results first (as function_call_output items), then what else the message says: hook text, steering, a prompt.
      for (const b of blocks) if (b.type === "tool_result") push(callOutput(b), -1);
      const parts = blocks.filter((b) => b.type !== "tool_result").map(inputPart).filter((p): p is Item => p !== null);
      if (parts.length) push({ type: "message", role: "user", content: parts }, -1);
      return;
    }
    const replay = usableReplay(blocks, replayOf, req.model);
    if (replay) {
      for (const item of (replay.data as { output: Item[] }).output) {
        // Reasoning goes back only with its encrypted content (store: false keeps nothing on OpenAI's side).
        if (item.type === "reasoning" && !item.encrypted_content) continue;
        push(item, k);
      }
    } else {
      for (const item of translatedAssistant(blocks)) push(item, k);
    }
  });
  return { items, owner };
}

/** Keywords whose values are data, not schemas. */
const NOT_SCHEMA = new Set(["default", "enum", "const", "examples", "example"]);
/** Keywords whose values map names to schemas: every value is a schema, whatever its name ("default" too). */
const SCHEMA_MAPS = new Set(["properties", "patternProperties", "$defs", "definitions", "dependentSchemas"]);
const fixed = new WeakMap<object, unknown>();

function fixSchema(node: any): any {
  if (Array.isArray(node)) return node.map(fixSchema);
  if (!node || typeof node !== "object") return node;
  const out: any = {};
  for (const [k, v] of Object.entries(node)) out[k] = NOT_SCHEMA.has(k) ? v : SCHEMA_MAPS.has(k) ? fixSchemaMap(v) : fixSchema(v);
  const isArray = out.type === "array" || (Array.isArray(out.type) && out.type.includes("array"));
  if (isArray && out.items === undefined && out.prefixItems === undefined) out.items = {};
  return out;
}

function fixSchemaMap(map: any): any {
  if (!map || typeof map !== "object" || Array.isArray(map)) return fixSchema(map);
  const out: any = {};
  for (const [name, schema] of Object.entries(map)) out[name] = fixSchema(schema);
  return out;
}

/**
 * A tool's input schema as OpenAI takes it (with strict: false): arrays without `items` get `items: {}` and the root is
 * an object. Nothing else changes. Memoised per schema object, so the tools stay byte-stable between requests.
 */
export function forOpenAI(schema: ToolDef["input_schema"]): Record<string, unknown> {
  const hit = fixed.get(schema);
  if (hit) return hit as Record<string, unknown>;
  const out = fixSchema(schema);
  out.type = "object";
  fixed.set(schema, out);
  return out;
}

/** OpenAI's hosted web search; approximate location avoids the US default. */
export const WEB_SEARCH_TOOL = { type: "web_search", user_location: { type: "approximate" }, search_context_size: "medium" };

/** Function tools in the canonical order (add-in tools, then local tools), then web_search. */
export function toolDefs(tools: ToolDef[], web: boolean): Item[] {
  return [
    ...tools.map((t) => ({ type: "function", name: t.name, description: t.description, parameters: forOpenAI(t.input_schema), strict: false })),
    ...(web ? [WEB_SEARCH_TOOL] : []),
  ];
}

// ---- client-side context clearing (the counterpart of Claude's clear_tool_uses_20250919) ----

export const CLEARED_OUTPUT = "[Cleared to save context: an older tool result. Run the tool again if you need it.]";
const CLEARED_ARGS = '{"cleared":true}';

const quarter = (text: unknown) => (typeof text === "string" ? Math.ceil(text.length / 4) : 0);

function partTokens(p: Item): number {
  if (p.type === "input_image") return 1600;
  if (p.type === "input_file") return 3000;
  return quarter(p.text ?? p.refusal);
}

/** A deterministic token estimate of one input item (no tokenizer). */
export function itemTokens(item: Item): number {
  switch (item.type) {
    case "message":
      return Array.isArray(item.content) ? item.content.reduce((n: number, p: Item) => n + partTokens(p), 0) : quarter(item.content);
    case "function_call":
      return quarter(item.arguments);
    case "function_call_output":
      return Array.isArray(item.output) ? item.output.reduce((n: number, p: Item) => n + partTokens(p), 0) : quarter(item.output);
    case "reasoning":
      return typeof item.encrypted_content === "string" ? Math.ceil(item.encrypted_content.length / 8) : 0;
    default:
      return quarter(JSON.stringify(item));
  }
}

/**
 * Once the estimate passes the trigger, the oldest tool calls (arguments and outputs) are replaced by a placeholder,
 * keeping the last `keepToolUses` and the excluded tools. The amount cleared moves in `clearAtLeastTokens` steps, so the
 * cleared set only grows when the estimate crosses another step and requests in between are byte-identical (the cache
 * holds). Reasoning items of an assistant message whose calls were cleared are dropped (they might not match any more),
 * and the rest of that message goes as if translated: calls and text without their item ids, other items (web_search_call)
 * left out, because OpenAI may refuse an output item replayed without the reasoning it came with.
 * Pure: returns new items; the stored history never changes.
 */
export function clearOldCalls(t: Translated, rules: ClearingRules, tools: Item[]): { items: Item[]; cleared: string[] } {
  const total = t.items.reduce((n, item) => n + itemTokens(item), 0) + quarter(JSON.stringify(tools));
  if (total <= rules.triggerTokens) return { items: t.items, cleared: [] };

  const outputs = new Map<string, number>();
  t.items.forEach((item, i) => {
    if (item.type === "function_call_output") outputs.set(item.call_id, i);
  });
  const calls = t.items.map((item, i) => ({ item, i })).filter((c) => c.item.type === "function_call");
  const kept = new Set(calls.slice(Math.max(0, calls.length - rules.keepToolUses)).map((c) => c.i));
  const clearable = calls.filter((c) => !kept.has(c.i) && !rules.excludeTools.includes(c.item.name));

  const target = Math.ceil((total - rules.triggerTokens) / rules.clearAtLeastTokens) * rules.clearAtLeastTokens;
  const chosen: typeof clearable = [];
  let sum = 0;
  for (const c of clearable) {
    if (sum >= target) break;
    const out = outputs.get(c.item.call_id);
    sum += itemTokens(c.item) + (out !== undefined ? itemTokens(t.items[out]) : 0);
    chosen.push(c);
  }
  if (!chosen.length) return { items: t.items, cleared: [] };

  const clearCalls = new Set(chosen.map((c) => c.i));
  const clearOutputs = new Set(chosen.map((c) => outputs.get(c.item.call_id)).filter((i): i is number => i !== undefined));
  const clearedTurns = new Set(chosen.map((c) => t.owner[c.i]));
  const items: Item[] = [];
  t.items.forEach((item, i) => {
    if (clearOutputs.has(i)) return void items.push({ ...item, output: CLEARED_OUTPUT });
    if (t.owner[i] < 0 || !clearedTurns.has(t.owner[i])) return void items.push(item);
    // An item of a cleared turn: no reasoning, and nothing with an OpenAI item id.
    if (item.type === "function_call") {
      const call = plainCall(item);
      items.push(clearCalls.has(i) && rules.clearInputs ? { ...call, arguments: CLEARED_ARGS } : call);
    } else if (item.type === "message") {
      if (typeof item.content === "string") items.push(assistantText(item.content, item.phase));
      else for (const p of item.content ?? []) if (p.type === "output_text" && p.text) items.push(assistantText(p.text, item.phase));
    }
  });
  return { items, cleared: chosen.map((c) => c.item.call_id) };
}

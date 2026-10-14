import Anthropic from "@anthropic-ai/sdk";
import { buildSystemPrompt } from "./systemPrompt";
import { modelOption } from "../lib/models";
import type {
  ApprovalMode,
  ContentBlockParam,
  Effort,
  MessageParam,
  ToolDef,
  ToolResultBlockParam,
  ToolRunResult,
  Usage,
} from "../lib/types";

/** Tools that change the model; gated by the approval setting. */
export const WRITE_TOOLS = new Set(["execute_csharp", "undo", "rebuild"]);

/** Handled entirely in the UI: shows a question card and waits for the user. */
export const ASK_USER_TOOL: ToolDef = {
  name: "ask_user",
  description:
    "Ask the user a question and wait for the answer. Use it when a decision changes the result (a dimension, which " +
    "face or component, design intent) and you can't infer it. Offer 2-5 concrete options when possible; the user can " +
    "also type a free answer.",
  input_schema: {
    type: "object",
    properties: {
      question: { type: "string", description: "The question, one or two sentences." },
      options: { type: "array", items: { type: "string" }, description: "Suggested answers." },
    },
    required: ["question"],
  },
};

/** A content block while it streams. */
export interface LiveBlock {
  type: "text" | "thinking" | "tool_use" | "other";
  text: string;
  name?: string;
  id?: string;
  json?: string;
}

export interface ApprovalRequest {
  toolUseId: string;
  name: string;
  input: any;
}

export type ApprovalDecision = { decision: "run" | "always" | "reject"; note?: string };

export interface AgentCallbacks {
  getMessages(): MessageParam[];
  /** Append to the thread and persist. History is append-only - never edit earlier messages. */
  append(message: MessageParam): Promise<void>;
  setLive(blocks: LiveBlock[] | null): void;
  setRunningTool(toolUseId: string | null): void;
  addUsage(usage: Usage): void;
  approve(req: ApprovalRequest): Promise<ApprovalDecision>;
  ask(toolUseId: string, question: string, options: string[]): Promise<string>;
  runTool(name: string, input: unknown): Promise<ToolRunResult>;
  isAutoApproved(): boolean;
  setAutoApproved(): void;
}

export interface AgentRunOptions {
  apiKey: string;
  model: string;
  effort: Effort;
  tools: ToolDef[];
  approval: ApprovalMode;
  realisticAppearance: boolean;
  userTools: { name: string; title: string; description: string; kind?: string }[];
  signal: AbortSignal;
  cb: AgentCallbacks;
}

export interface AgentRunResult {
  stop: string;
  error?: string;
}

/**
 * Server-side context editing: once a request passes TRIGGER tokens, the oldest tool calls and their results
 * (inputs included - execute_csharp cells are the bulk) are replaced by a placeholder, keeping the last KEEP.
 * clear_at_least makes each clearing large enough to be worth the prompt-cache rewrite it causes.
 * ask_user (the user's answers) and read_user_tool (instructions being followed) are never cleared.
 */
const CLEAR_OLD_TOOL_USES = {
  type: "clear_tool_uses_20250919" as const,
  trigger: { type: "input_tokens" as const, value: 40000 },
  keep: { type: "tool_uses" as const, value: 6 },
  clear_at_least: { type: "input_tokens" as const, value: 12000 },
  clear_tool_inputs: true,
  exclude_tools: ["ask_user", "read_user_tool"],
};

/** Runs the agent until it ends its turn, is stopped, or fails. */
export async function runAgent(opts: AgentRunOptions): Promise<AgentRunResult> {
  const { cb, signal } = opts;
  const client = new Anthropic({ apiKey: opts.apiKey, dangerouslyAllowBrowser: true, maxRetries: 3 });
  const m = modelOption(opts.model);

  const betas = ["thinking-binding-controls-2026-08-01", "context-management-2025-06-27"];
  if (m.progressUpdates) betas.push("thinking-display-updates-2026-08-18");
  if (m.fallbacks) betas.push("server-side-fallback-2026-07-01");

  const tools: Anthropic.Beta.Messages.BetaToolUnion[] = [...opts.tools, ASK_USER_TOOL].map((t) => ({
    name: t.name,
    description: t.description,
    input_schema: t.input_schema,
    eager_input_streaming: true,
  }));

  const system = buildSystemPrompt({ realisticAppearance: opts.realisticAppearance, userTools: opts.userTools });
  let malformedRetries = 0;
  try {
    for (let iteration = 0; iteration < 200; iteration++) {
      if (signal.aborted) return { stop: "aborted" };

      const stream = client.beta.messages.stream(
        {
          model: m.id,
          max_tokens: 64000,
          system: [{ type: "text", text: system }],
          tools,
          messages: cb.getMessages(),
          // Thinking is always on for these models; effort controls depth. "drop_block" keeps chats that were
          // reloaded from a file (or continued after an add-in update) valid instead of failing with a 400.
          thinking: {
            type: "adaptive",
            display: m.progressUpdates ? "updates" : "summarized",
            block_binding: { prefix_mismatch_behavior: "drop_block" },
          },
          output_config: { effort: opts.effort },
          cache_control: { type: "ephemeral" },
          // Old tool calls (C# cells, screenshots, model dumps) are cleared server-side once the prompt grows, keeping
          // only the most recent ones. The stored history stays complete and append-only, so thinking stays valid.
          context_management: { edits: [CLEAR_OLD_TOOL_USES] },
          ...(m.fallbacks ? { fallbacks: "default" as const } : {}),
          betas,
        },
        { signal },
      );

      const live: LiveBlock[] = [];
      let frame = 0;
      const flush = () => {
        frame = 0;
        cb.setLive(live.map((b) => ({ ...b })));
      };
      const schedule = () => {
        if (!frame) frame = requestAnimationFrame(flush);
      };

      let message: Anthropic.Beta.Messages.BetaMessage;
      try {
        for await (const ev of stream) {
          if (ev.type === "content_block_start") {
            const b = ev.content_block;
            live[ev.index] =
              b.type === "text" ? { type: "text", text: "" }
              : b.type === "thinking" ? { type: "thinking", text: "" }
              : b.type === "tool_use" ? { type: "tool_use", text: "", name: b.name, id: b.id, json: "" }
              : { type: "other", text: "" };
            schedule();
          } else if (ev.type === "content_block_delta") {
            const b = live[ev.index];
            if (!b) continue;
            if (ev.delta.type === "text_delta") b.text += ev.delta.text;
            else if (ev.delta.type === "thinking_delta") b.text += ev.delta.thinking;
            else if (ev.delta.type === "input_json_delta") b.json = (b.json ?? "") + ev.delta.partial_json;
            schedule();
          }
        }
        message = await stream.finalMessage();
        malformedRetries = 0;
      } catch (err) {
        if (frame) cancelAnimationFrame(frame);
        if (signal.aborted || err instanceof Anthropic.APIUserAbortError) return { stop: "aborted" };
        // With eager input streaming, unparseable tool JSON rejects finalMessage. Re-issue that turn (nothing was appended).
        if (!(err instanceof Anthropic.APIError) && malformedRetries++ < 2) continue;
        throw err;
      }
      if (frame) cancelAnimationFrame(frame);
      cb.setLive(null);

      const u = message.usage;
      const input = u.input_tokens ?? 0;
      const cacheRead = u.cache_read_input_tokens ?? 0;
      const cacheWrite = u.cache_creation_input_tokens ?? 0;
      cb.addUsage({ input, output: u.output_tokens ?? 0, cacheRead, cacheWrite, context: input + cacheRead + cacheWrite });

      if (message.stop_reason === "refusal") {
        const category = message.stop_details?.category;
        return { stop: "refusal", error: `The model declined this request${category ? ` (${category})` : ""}. Try rephrasing it.` };
      }

      const content = message.content as unknown as ContentBlockParam[];
      const toolUses = message.content.filter((b): b is Anthropic.Beta.Messages.BetaToolUseBlock => b.type === "tool_use");

      if (message.stop_reason === "max_tokens" && toolUses.length > 0) {
        return { stop: "max_tokens", error: "The response was cut off in the middle of a tool call. Ask the agent to continue in smaller steps." };
      }

      await cb.append({ role: "assistant", content });

      if (message.stop_reason === "pause_turn") continue;
      if (toolUses.length === 0) return { stop: message.stop_reason ?? "end_turn" };

      const results: ToolResultBlockParam[] = [];
      for (const tu of toolUses) {
        if (signal.aborted) {
          results.push({ type: "tool_result", tool_use_id: tu.id, is_error: true, content: "Stopped by the user before this ran." });
          continue;
        }
        results.push(await executeTool(tu, opts));
      }
      await cb.append({ role: "user", content: results });
      if (signal.aborted) return { stop: "aborted" };
    }
    return { stop: "iteration_limit", error: "Stopped after 200 steps." };
  } finally {
    cb.setLive(null);
    cb.setRunningTool(null);
  }
}

async function executeTool(tu: Anthropic.Beta.Messages.BetaToolUseBlock, opts: AgentRunOptions): Promise<ToolResultBlockParam> {
  const { cb } = opts;
  const err = (text: string): ToolResultBlockParam => ({ type: "tool_result", tool_use_id: tu.id, is_error: true, content: text });

  const def = tu.name === ASK_USER_TOOL.name ? ASK_USER_TOOL : opts.tools.find((t) => t.name === tu.name);
  if (!def) return err(`Unknown tool '${tu.name}'.`);
  const problem = validateInput(def, tu.input);
  if (problem) return err(`INVALID_INPUT: ${problem}. Re-issue the call with complete, valid JSON input.`);

  if (tu.name === ASK_USER_TOOL.name) {
    const input = tu.input as { question: string; options?: string[] };
    const answer = await cb.ask(tu.id, input.question, input.options ?? []);
    return { type: "tool_result", tool_use_id: tu.id, content: answer };
  }

  if (WRITE_TOOLS.has(tu.name)) {
    if (opts.approval === "read_only") {
      return err("Read-only mode is on: tools that change the model are disabled. Explain what you would do instead.");
    }
    if (opts.approval === "ask" && !cb.isAutoApproved()) {
      const d = await cb.approve({ toolUseId: tu.id, name: tu.name, input: tu.input });
      if (d.decision === "reject") {
        return err(`The user declined to run this.${d.note ? ` Their note: ${d.note}` : ""} Don't retry it unchanged.`);
      }
      if (d.decision === "always") cb.setAutoApproved();
    }
  }

  cb.setRunningTool(tu.id);
  try {
    const res = await cb.runTool(tu.name, tu.input);
    return { type: "tool_result", tool_use_id: tu.id, content: res.content, ...(res.isError ? { is_error: true } : {}) };
  } catch (e) {
    return err(`The add-in failed to run ${tu.name}: ${e instanceof Error ? e.message : String(e)}`);
  } finally {
    cb.setRunningTool(null);
  }
}

/** Minimal JSON-schema check: object, required keys present, primitive types match. */
function validateInput(def: ToolDef, input: unknown): string | null {
  if (typeof input !== "object" || input === null || Array.isArray(input)) return "input must be an object";
  const obj = input as Record<string, unknown>;
  for (const key of def.input_schema.required ?? []) {
    if (obj[key] === undefined || obj[key] === null || obj[key] === "") return `missing required '${key}'`;
  }
  for (const [key, schema] of Object.entries(def.input_schema.properties ?? {})) {
    const v = obj[key];
    if (v === undefined || v === null) continue;
    const t = schema?.type;
    if (t === "string" && typeof v !== "string") return `'${key}' must be a string`;
    if ((t === "number" || t === "integer") && typeof v !== "number") return `'${key}' must be a number`;
    if (t === "boolean" && typeof v !== "boolean") return `'${key}' must be true or false`;
    if (t === "array" && !Array.isArray(v)) return `'${key}' must be an array`;
  }
  return null;
}

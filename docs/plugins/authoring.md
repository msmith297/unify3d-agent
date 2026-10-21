# Writing a Unify3D plugin

For people who write plugins for Unify3D Agent by hand, and for checking what the built-in plugin creator
(`tools/builtin/create-plugin`, also `/new-plugin`) produces. The runtime behind it is specified in
[design.md](design.md); where this page and the design differ, this page describes the corrected contract the add-in
implements.

A Unify3D plugin is a **Claude Code plugin** plus, optionally, **Unify3D tools**. A folder that works in Claude Code
installs in Unify3D unchanged, as far as its parts apply (section 2), and a Unify3D plugin's commands, skills, agents,
hooks and MCP servers work in Claude Code.

## 1. The folder

```
my-plugin/
  .claude-plugin/plugin.json      required: the manifest
  README.md                       shown in the store's detail view
  commands/<name>.md              slash commands
  skills/<skill>/SKILL.md         skills (plus any extra files in the skill folder)
  agents/<name>.md                sub-agents
  hooks/hooks.json                hooks
  .mcp.json                       MCP servers
  tools/<tool>/TOOL.md ...        Unify3D tools and workflows (Unify3D only)
```

Everything except `.claude-plugin/plugin.json` is optional.

### plugin.json

```json
{
  "name": "pu-mold-shop",
  "version": "1.0.0",
  "displayName": "PU Mold Shop",
  "description": "Flash grooves, the mold-base workflow and drawing checks for a PU compression-mold shop.",
  "author": { "name": "Jane Toolmaker", "email": "jane@example.com" },
  "homepage": "https://example.com/pu-mold-shop",
  "repository": "https://github.com/example/pu-mold-shop",
  "license": "MIT",
  "keywords": ["molding", "polyurethane"],
  "userConfig": {
    "erp_url": { "type": "string", "title": "ERP address", "description": "Base URL of the shop's ERP", "default": "" },
    "erp_key": { "type": "string", "title": "ERP API key", "sensitive": true }
  },
  "unify3d": {
    "category": "Molding & Toolmaking",
    "icon": "./icon.png",
    "tools": "./tools",
    "minAddInVersion": "0.2.0"
  }
}
```

- `name`: `^[a-z0-9][a-z0-9-]{1,47}$` (kebab-case, 2-48 characters). It prefixes everything the plugin adds, so
  keep it short.
- `version`: semantic version. Raise it with every change you share. A plugin without one (agent-bridge has none) is
  versioned by its source commit.
- `commands`, `agents`, `skills` (a path or an array of paths), `hooks` (a path or an inline object) and `mcpServers`
  (a path or an inline object) work as in Claude Code: extra locations next to the default folders.
- `userConfig`: settings the user fills in under Plugins > Installed. A `sensitive` value is stored encrypted (DPAPI)
  and never shown again.
- `unify3d`: Unify3D-only fields. Claude Code does not use them.

  | Field | Meaning |
  |---|---|
  | `category` | The store group. Each tool keeps its own `category` for the ribbon and the Workflows menu. |
  | `icon` | A tool icon name (`macro`, `part`, `assembly`, `drawing`, `tree`, `select`, `camera`, `rebuild`, `gear`, `book`, `markup`, `shield`) or a path in the plugin, e.g. `./icon.png` (128 x 128, transparent). |
  | `tools` | Where the Unify3D tools are, default `./tools`. |
  | `minAddInVersion` | The oldest add-in that can run the plugin; older add-ins refuse to enable it. |
  | `env` | Extra environment variables for this plugin's own hooks and MCP servers (section 5). |

## 2. Claude Code compatibility

| Part | Claude Code | Unify3D |
|---|---|---|
| `plugin.json` fields | all | all; `unify3d` added |
| Commands | yes | yes: `$ARGUMENTS`, `$1`-`$9`, `@path`, `description`, `argument-hint`, `model`; `allowed-tools` is advisory; `` !`cmd` `` is **not run** (left as text, with a note) |
| Skills | yes | yes: listed in the agent's skills index, loaded with `use_skill`, extra files read with `read_plugin_file` |
| Agents | yes | yes, through `run_agent`; tool names Unify3D doesn't have (`Read`, `Bash`, `Edit` ...) are dropped, and an agent left with none gets read-only tools |
| Hooks | yes | `type: "command"` only, six events (section 6) |
| MCP servers | stdio, HTTP, SSE | stdio only (section 7) |
| Output styles, LSP servers, `settings.json`, status line | yes | ignored |
| `tools/` (Unify3D tools) | ignored | yes (section 3) |
| `unify3d` in `plugin.json` and marketplace entries | ignored | yes |

### Commands

```markdown
---
description: Check a mold drawing against the shop rules
argument-hint: "<drawing or 'active'>"
model: claude-sonnet-5-5
---
Check $ARGUMENTS against the drawing rules in @reference/drawing-rules.md and list every problem with its sheet and view.
```

- Invoked as `/<plugin>:<command>`, or `/<command>` when no other command has that name.
- The expanded text is sent as the user's message, with a chip that shows the command.
- `@relative/path` reads a file of the plugin (never outside it).

### Skills

```markdown
---
name: drawing-rules
description: The shop's rules for mold drawings (tolerances, title block, views). Use when checking or making a mold drawing.
---
```

The `description` is all the agent sees until it loads the skill: say what is inside and when to use it. The user
can also run a skill as `/<skill>` and point the agent at it with `@`.

### Agents

```markdown
---
name: drawing-checker
description: Reviews a drawing against the shop rules. Use for a second opinion before release.
tools: get_model_context, get_feature_tree, capture_views, list_open_documents
model: inherit
---
You are a careful drawing checker ...
```

`tools` lists Unify3D agent tools; `execute_csharp`, `undo` and `rebuild` change the model, everything else reads.
`model` is `inherit`, `haiku`, `sonnet`, `opus` or a model id. The body is the whole system prompt of the sub-agent,
which does not see the chat. Sub-agents can't use the panel's `ask_*` tools.

## 3. Unify3D tools in a plugin

`tools/<tool>/` holds a tool or workflow folder exactly like the user's own (`%APPDATA%\SwAgent\tools\<tool>`) and the
built-in ones (`tools/builtin/<tool>` in this repository): `TOOL.md` with front matter, optional `inputs.json` (the
tool's inputs page), `macro.cs`, `preview.cs`, `icon.svg` / `icon.png`, and any data files.

- In Unify3D a plugin tool is named `<plugin>:<tool>` (e.g. `pu-mold-shop:flash-grooves`): on the ribbon, in the
  Workflows menu, in `[Run tool: ...]` / `[Run workflow: ...]` and for `read_user_tool`. Inside the plugin, refer to
  sibling tools by that full name.
- Installed plugin tools are read-only like the built-in ones; the user's settings for them (intelligence, category,
  remembered inputs) are kept outside the plugin, under the full name. Tools in a **local** plugin
  (`plugins\local\<name>`) stay editable with `edit_user_tool`.
- Tools reach the ribbon after a SolidWorks restart; workflows appear at once.
- Disabling the plugin removes its tools.
- `requires:` in `TOOL.md` names agent tools the tool needs. Use it for tools that need a newer add-in, or MCP tools
  of the plugin's own servers.

## 4. Variables

Expanded in hook commands, MCP `command` / `args` / `env`, `unify3d.env` values and command bodies:

| Variable | Value |
|---|---|
| `${CLAUDE_PLUGIN_ROOT}`, `${UNIFY3D_PLUGIN_ROOT}` | The installed plugin folder (read-only; replaced on update) |
| `${CLAUDE_PLUGIN_DATA}` | `%APPDATA%\SwAgent\plugins\data\<plugin>`: writable, survives updates |
| `${UNIFY3D_DEVICE}` | This PC's name |
| `${UNIFY3D_PID}` | The process id of SolidWorks (`SLDWORKS.exe`) |
| `${user_config.<key>}` | The user's value for a `userConfig` key |
| `${NAME}` | Any environment variable |

Unify3D expands them itself before starting a process (`cmd.exe` does not expand `${...}`). Keep `sensitive`
`userConfig` values off command lines and out of `args`: read them from the environment (section 5).

## 5. The environment of hooks and MCP servers

Every hook and MCP server of a plugin gets the add-in's environment plus:

| Variable | Value |
|---|---|
| `CLAUDE_PLUGIN_ROOT` | The plugin folder |
| `CLAUDE_PLUGIN_DATA` | The plugin's data folder |
| `CLAUDE_PROJECT_DIR` | `%USERPROFILE%` (Unify3D has no project folder) |
| `CLAUDE_CODE_SESSION_ID` | The Unify3D session id (the same as `session_id` on hook stdin) |
| `UNIFY3D` | `1`, so a script can tell it runs under Unify3D |
| `CLAUDE_PLUGIN_OPTION_<KEY>` | Each `userConfig` value, key in capitals (sensitive ones too) |
| the plugin's `unify3d.env` | After the marketplace entry's `unify3d.env` (section 8); `userConfig` wins over both |

`PATH` is refreshed from the registry, so a Node.js installed after SolidWorks started is found. The Anthropic API key
is never in a plugin's environment.

**The session.** Unify3D has one session id per PC, kept in `%APPDATA%\SwAgent\plugins\session.json` and reused after
every restart (a second SolidWorks running at the same time gets a temporary one). `/clear` and new chats keep the
session. Several chats can run under one session.

**Working folder.** Hooks and MCP servers start in `%USERPROFILE%`, and `cwd` on hook stdin is the same folder. Use
absolute paths. A hook that needs the active document gets it as `unify3d_document` on stdin (when the panel knows
one).

## 6. Hooks

Claude Code's hook format and protocol (`hooks/hooks.json`, or `hooks` in `plugin.json`):

```json
{
  "hooks": {
    "PostToolUse": [
      { "matcher": "execute_csharp|rebuild",
        "hooks": [ { "type": "command", "command": "node \"${CLAUDE_PLUGIN_ROOT}/scripts/log.mjs\"", "timeout": 15 } ] }
    ]
  }
}
```

### Support matrix

| Event | Fired in Unify3D | When | stdin extras |
|---|---|---|---|
| `SessionStart` | yes | Once per session, when the panel first loads, after the enabled MCP servers have started (or 30 s). The matcher is matched against `source`. | `source: "startup"` |
| `UserPromptSubmit` | yes | Before a prompt the user typed, a queued user message or a ribbon tool run is sent. Not for agent-bridge style wake-ups or Stop continuations. Not in sub-agent runs. | `prompt` |
| `PreToolUse` | yes | Before any agent tool, before validation and the approval card (also MCP tools, panel tools and in sub-agents) | `tool_name`, `tool_input` |
| `PostToolUse` | yes | After the tool returned | `tool_name`, `tool_input`, `tool_response` |
| `Stop` | yes | The agent's turn ended normally (not on abort, error, refusal or step limit). Not in sub-agent runs. | `stop_hook_active` |
| `SessionEnd` | yes | SolidWorks closes or the add-in disconnects; best effort, 3 s | `reason` |
| `PermissionRequest` | **no** | Unify3D's approval cards can't be answered by a hook (so agent-bridge's remote approvals don't work) | |
| `Notification`, `SubagentStart`, `SubagentStop`, `PreCompact`, others | **no** | | |

| Hook field | Unify3D |
|---|---|
| `type: "command"` | yes |
| `type: "prompt"`, `type: "agent"`, HTTP hooks | no |
| `matcher` | regex on the tool name for `PreToolUse` / `PostToolUse` (empty or `*` = all); on `source` for `SessionStart` |
| `timeout` (seconds) | yes, default 60; long-running async hooks may use up to 604800 (a week) |
| `async: true` | yes: the turn doesn't wait |
| `asyncRewake: true` + `rewakeMessage` | yes (below) |

### Running

- Commands run as `cmd.exe /d /s /c "<command>"`, with no window, in the plugin's environment (section 5), outside
  SolidWorks and off the UI thread. A hook never touches SolidWorks; to change the model it would have to go through
  the agent.
- stdin: one JSON object, UTF-8 without BOM, then stdin is closed. Common fields: `session_id`, `transcript_path`,
  `cwd`, `hook_event_name`, `permission_mode: "default"`. There is no `turn_id` (scripts that support Codex treat
  `turn_id` as Codex). `transcript_path` is a JSONL export of the chat the event belongs to, under
  `%LOCALAPPDATA%\SwAgent\plugins\transcripts`.
- `tool_response` is `{ content: [text blocks; images as "[image]"], isError }`, up to about 100 KB.
- Several hooks for one event run in parallel.
- Timeouts, disabling the plugin and SessionEnd kill the hook's whole process tree.
- Hook stdin is never logged.

### Outputs

| Exit code | Meaning |
|---|---|
| 0 | Success. stdout may be JSON (below). For `UserPromptSubmit` and `SessionStart`, plain text on stdout is added as context. |
| 2 | Block: stderr is the reason. `PreToolUse`: the tool is denied. `UserPromptSubmit`: the prompt is not sent and goes back to the composer. `Stop`: the turn continues with the reason. |
| other | A non-blocking error, logged |

JSON on stdout (exit 0):

- `hookSpecificOutput.additionalContext`: `UserPromptSubmit`, `SessionStart` and `PostToolUse` add it to the model's
  context. After a tool it is a separate text block after the tool results (so clearing old tool results doesn't
  remove it); after a prompt it shows in the chat as a context card, not as the user's words.
- `decision: "block"` with `reason`: `Stop` continues the turn with the reason; `PostToolUse` adds the reason after
  the tool result.
- `PreToolUse`: `hookSpecificOutput.permissionDecision` `allow`, `deny` or `ask`, with `permissionDecisionReason`.
  `ask` shows Unify3D's approval card even for a tool that normally needs none.
- `continue: false` with `stopReason`: the run stops.
- `systemMessage`: shown to the user as a notice.

When several hooks answer: any block wins; deny beats ask beats allow; contexts are joined; any `continue: false`
wins.

### Async hooks and wake-ups

With `async: true` the hook runs in the background. With `asyncRewake: true` as well, when it exits with code 2 its
stderr (stdout if stderr is empty), prefixed with `rewakeMessage`, wakes the agent:

- it starts a new turn in the session's wake thread (the chat used last, else a new workspace chat named after the
  plugin), shown with a system-event chip; when that chat is busy (a run, a question, an approval or a form waiting),
  the wake waits until it is done;
- wakes and `Stop` continuations together are limited per hour (Options > Plugins > Automatic wakes per hour,
  default 20); over the limit they wait for the user's next prompt, with a notice;
- a wake is kept until it is shown in a chat, also across a panel reload.

This is how agent-bridge's background listener (`Stop` / `SessionStart` with `--listen`) works.

## 7. MCP servers

`.mcp.json` (or `mcpServers` in `plugin.json`), the Claude Code shape:

```json
{
  "mcpServers": {
    "erp": {
      "command": "node",
      "args": ["${CLAUDE_PLUGIN_ROOT}/mcp/erp.mjs"],
      "env": { "ERP_URL": "${user_config.erp_url}" }
    }
  }
}
```

| MCP feature | Unify3D v1 |
|---|---|
| stdio transport (newline-delimited JSON-RPC 2.0) | yes |
| Streamable HTTP, SSE | no |
| `initialize` (protocol `2025-06-18`, client `unify3d-agent`), then `notifications/initialized` | yes; `initialize` may take up to 30 s |
| Server `instructions` | yes: added to the agent's system prompt |
| `tools/list`, `tools/call`, `notifications/tools/list_changed` | yes |
| Tool results: text, images, `isError` | yes |
| Cancelling a call (`notifications/cancelled`) | yes, when the user stops the run |
| `ping`, `roots/list` from the server | answered |
| `sampling/*`, `elicitation/*` from the server | refused (`-32601`) |
| Resources, prompts | no |

- **Tool names.** A plugin server's tools are `mcp__plugin_<plugin>_<server>__<tool>` (as in Claude Code); a server
  from the user's `%APPDATA%\SwAgent\mcp.json` gets `mcp__<server>__<tool>`. Names longer than the API's 64
  characters are shortened with a hash.
- **Schemas.** `inputSchema` must be an object schema; `$schema` is dropped, and a top-level `anyOf` / `oneOf` is not
  accepted.
- **Life cycle.** A server starts when its plugin is enabled (at SolidWorks start or on enable), restarts with
  backoff when it exits, and stops on disable and at SessionEnd (stdin is closed first, so it can clean up; then its
  process tree is ended).
- **Process.** A `command` that is an `.exe` (`node`, `python`) is started directly, not through `cmd.exe`, so the
  server's parent process is SolidWorks. The working folder is `%USERPROFILE%`. stderr is drained and logged.
- **Calls** don't wait for SolidWorks: MCP tools run outside the model, with a 10-minute timeout.
- State and errors show in Plugins > Installed, with a restart button.

## 8. Trust and safety

Hooks and MCP servers run programs on the user's PC. Unify3D runs them only for plugins whose current hooks and MCP
configuration the user approved:

- Installing or enabling a plugin with hooks or MCP servers shows every command line it will run, and asks once.
- The approval is for exactly those commands. An update, or an edit of a local plugin, that changes them stops them
  until the user approves again.
- The agent cannot approve: its plugin tools (`plugin_create`, `plugin_write_file`, `plugin_enable` ...) never do.
- The built-in "unify3d" marketplace is trusted. Third-party marketplaces don't auto-update by default.
- Options > Plugins can turn all plugin hooks or all MCP servers off.

When you write a plugin:

- never put secrets in files; use `userConfig` with `sensitive: true` and read `CLAUDE_PLUGIN_OPTION_<KEY>`;
- don't download and run code, and don't send the user's files or chats anywhere the user didn't choose;
- keep data in `${CLAUDE_PLUGIN_DATA}`, not in the plugin folder (it is replaced on update);
- treat everything your hook or server receives (prompts, tool inputs, model data) as the user's private data.

## 9. Packaging and sharing: marketplaces

A **marketplace** is a folder or repository with `.claude-plugin/marketplace.json`, the Claude Code format:

```json
{
  "name": "pu-tools",
  "owner": { "name": "Jane Toolmaker" },
  "plugins": [
    { "name": "pu-mold-shop", "source": "./pu-mold-shop", "description": "...", "version": "1.0.0",
      "category": "Molding & Toolmaking", "keywords": ["molding"] }
  ]
}
```

Plugin `source` kinds:

| Source | Example |
|---|---|
| A folder in the marketplace | `"./pu-mold-shop"` |
| GitHub | `{ "source": "github", "repo": "owner/repo", "ref": "v1.2" }` |
| Git | `{ "source": "git", "url": "https://example.com/repo.git", "ref": "main" }` |
| A folder in a git repository | `{ "source": "git-subdir", "url": "https://github.com/owner/repo.git", "path": "plugin" }` |
| A zip | `{ "source": "url", "url": "https://example.com/plugin.zip" }` |

- Unify3D uses `git.exe` when it is installed. Without git, any `github.com` source (also `git` and `git-subdir`
  URLs on github.com) is downloaded as a zip from codeload.github.com, which only works for **public** repositories.
- A plugin without a `version` is installed under its commit.

### Unify3D fields on a marketplace entry

A marketplace entry may carry a `unify3d` object for plugins whose own files you can't change:

| Field | Meaning |
|---|---|
| `env` | Environment for that plugin's hooks and MCP servers, applied before the plugin's own `unify3d.env` and its `userConfig` |
| `installWith` | Names of plugins in the same marketplace that are installed together with this one (companions) |
| `hideSkills` | The plugin's skills Unify3D should not offer (e.g. ones written for Claude Code only) |

### Packaging

From the agent: "package my plugin" (`plugin_package`), or Plugins > Create > Package. It writes
`Documents\Unify3D Plugins\<name>-<version>.zip` and a `marketplace.json` next to it that lists the plugin, so that
folder is a ready marketplace.

### Sharing

- **One colleague:** send the zip. They unzip it and add the folder in Plugins > Marketplaces > Add, then install
  from Discover.
- **A team:** put the marketplace on a shared drive or in a git repository. Everyone adds it once (a folder, a git
  URL, or GitHub `owner/repo`); Update brings new versions.
- **Claude Code users** can add the same marketplace (`/plugin marketplace add ...`). Everything but `tools/` works
  there.

Before you share, check the folder for private data: customer names and part numbers, prices, shop rules you don't
want to give away, file paths of your PC, sample models.

## 10. Example: Agent Bridge

The built-in marketplace (`plugins/.claude-plugin/marketplace.json` in this repository) installs the real
[agent-bridge](https://github.com/msmith297/agent-bridge) plugin unchanged, by reference:

```json
{
  "name": "agent-bridge",
  "source": { "source": "git-subdir", "url": "https://github.com/msmith297/agent-bridge.git", "path": "plugin" },
  "unify3d": {
    "env": { "AGENT_BRIDGE_NAME": "Unify3D SolidWorks - ${UNIFY3D_DEVICE}", "AGENT_BRIDGE_OWNER_PID": "${UNIFY3D_PID}" },
    "installWith": ["agent-bridge-unify3d"],
    "hideSkills": ["revive-session", "agent-bridge-plugin-update", "server-setup"]
  }
}
```

- Its MCP server (`client/mcp.mjs`) reads `AGENT_BRIDGE_NAME` for the seat's name; `AGENT_BRIDGE_OWNER_PID` tells its
  background listener which process to watch (SolidWorks), so a listener whose SolidWorks is gone stops.
- Its hooks (`client/hook.mjs`) use `UserPromptSubmit` and `PostToolUse` to deliver mail, `Stop` to continue a turn
  when mail is waiting, and async `Stop` / `SessionStart` hooks with `--listen` and `asyncRewake` to wake an idle
  agent. `PermissionRequest` (remote approvals) is never fired in Unify3D.
- The companion `plugins/agent-bridge-unify3d` adds a skill for acting on bridge messages inside SolidWorks and the
  `/bridge` command; it has no hooks or MCP servers, so it needs no trust prompt.

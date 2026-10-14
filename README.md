<p><img src="assets/brand/logo-full.png" alt="Unify3D" height="72"></p>

# Unify3D Agent

An AI design agent for SOLIDWORKS. A chat panel in the Task Pane, a **Unify3D** ribbon tab and right-click
commands let you talk to Claude about the open model. The agent reads and changes the model through the full
SolidWorks API. Each document keeps its own conversations, stored **inside the .sldprt / .sldasm / .slddrw file**.

```
SLDWORKS.exe
 └─ SwAgent.dll  (C# .NET Framework 4.8 add-in, xCAD.NET)
     ├─ Ribbon tab "Unify3D" (panel commands + your Tools), right-click "Ask Unify3D" on faces/edges/features
     ├─ Task Pane ─ WebView2 (separate Chromium processes)
     │               └─ React + TypeScript UI  ── agent loop ──▶ Claude API (@anthropic-ai/sdk)
     │                        ▲ JSON-RPC over postMessage │ tool calls
     ├─ ToolRegistry ◀────────┘───────────────────────────┘
     │   execute_csharp · capture_views · get_model_context · get_feature_tree · get_selection
     │   search_sw_api · get_sw_api_type · list_open_documents · undo · rebuild   (+ ask_user, UI-side)
     └─ Chat storage: third-party stream "SwAgent.Chat.v1" inside each document
```

## Workflows and tools

- **Workflows** are larger saved procedures (e.g. building a compression mold). Run them from the **Workflows** menu in
  the chat panel; the agent follows the saved instructions.
- **Tools** are quick, AI-powered commands on the **Unify3D** ribbon tab, assignable to hotkeys in
  Tools > Customize > Keyboard (category *Unify3D*). A tool is a skill with optional hard-coded parts:
  - a native **PropertyManager page** built from its declared inputs (selection boxes, numbers with units, checkboxes,
    dropdowns, option buttons, a note, image/PDF/3D-model files), skipped when everything it needs is already selected;
  - a live **holographic preview** (temporary bodies, rebuilt on every change; switches to on-demand if it gets slow);
  - a **macro** (verified C#, one undo step) - for mechanical tools it is the whole job, no AI involved;
  - for tools with an **agent in the loop**: context captured silently (view, selection, model, feature history) and
    staged with the inputs for the agent at the tool's **intelligence** tier (Fast / Balanced / Max), shown in the panel
    as a single "executing" line.
- The agent creates both with an in-chat questionnaire, from a description, from "what you just did", or from the model.

Both are created with the agent and stored in `%APPDATA%\SwAgent\tools\<name>\` (`TOOL.md`, `inputs.json`, `macro.cs`, `preview.cs`, `icon.svg`).

## How it works

- **Full API access.** `execute_csharp` runs agent-written C# with Roslyn, in-process on the SolidWorks UI thread.
  It has `swApp`, `Model`, unit helpers `mm()`/`deg()` and `Agent.CallToolAsync(...)` in scope. Variables persist
  between calls in a chat. Each call is recorded as **one undo step** named `Agent: <title>`.
- **Same tools for UI and agent.** Every plugin capability is an `[AgentTool]` method in `src/SwAgent/Tools`. The
  registry generates the JSON schemas the model sees. The UI calls the same tools, for example the camera button
  calls `capture_views`. To add a capability, add a method; the agent picks it up automatically.
- **Seeing the model.** `capture_views` grabs the graphics area. It can also cycle iso/front/top/... views, tile them
  into one labelled image, and then restore the user's view.
- **Exact API signatures.** `search_sw_api` and `get_sw_api_type` reflect over the installed interop assemblies, so
  the agent can check signatures for your SolidWorks version instead of guessing them.
- **Model selector.** Opus 5.5 at *high* effort is the default. Fable 5.1, Sonnet 5.5 and Haiku 5.5 are also
  offered, at efforts from low to max. Requests use adaptive thinking with progress updates, server-side refusal
  fallbacks and prompt caching.
- **Approvals.** *Ask* (the default) shows the code and waits for **Run / Always allow in this chat / Reject**.
  *Auto* runs without asking. *Read-only* disables tools that change the model.

### Chat history is saved with the file

- Conversations are written into the document as a gzip'd JSON third-party storage stream on **every save**.
  Renaming, moving, copying, Pack and Go and PDM check-in all keep the history. Nothing is keyed by file path; each
  file carries its own `docId`.
- Each chat change marks the document as modified, so SolidWorks prompts you to save. You can turn this off in
  Settings.
- Unsaved chat is also backed up to `%LOCALAPPDATA%\SwAgent\chat-backup\{docId}.json.gz`. If SolidWorks crashes,
  or you close without saving, the next open restores the newer messages and tells you so.
- To keep files small, only the newest two screenshots per conversation are stored in the file. Older ones become a
  placeholder.
- Saved history is replayed exactly. Requests set `thinking.block_binding.prefix_mismatch_behavior: "drop_block"`,
  so a chat reopened later, or after an add-in update, never fails on stale thinking blocks.
- The API key is never stored in documents. It is encrypted with Windows DPAPI in `%APPDATA%\SwAgent\apikey.bin`;
  the `ANTHROPIC_API_KEY` environment variable also works.

## Build and install

## Install

Download `Unify3D-Agent-Setup-x.y.z.exe` from [Releases](https://github.com/msmith297/unify3d-agent/releases/latest)
and run it (administrator rights are needed to register a SOLIDWORKS add-in). Requires Windows 10/11 x64 and
SOLIDWORKS 2024 or newer. Restart SOLIDWORKS, open the **Unify3D Agent** Task Pane tab and paste your Anthropic API key
in Options. The add-in checks for updates itself (Options > Updates).

## Build from source

Requirements: SolidWorks 2024+ (developed on 2026), Visual Studio 2022 with the *.NET desktop
development* workload, Node 20+, and the WebView2 runtime (included with Windows 11).

```powershell
.\scripts\build.ps1                     # builds ui/ and the add-in (Debug)
```

Then, in an **Administrator** PowerShell:

```powershell
.\scripts\register.ps1                  # RegAsm /codebase + SolidWorks add-in keys
```

Restart SolidWorks. The add-in loads at startup and also appears under **Tools > Add-Ins** as *Unify3D Agent*.
Open the **Unify3D Agent** tab in the Task Pane, paste your API key in Settings, and start chatting.

To uninstall, run `.\scripts\unregister.ps1` as Administrator. Chats already saved in your files stay there,
harmless to SolidWorks.

## Development

- **UI with hot reload, inside SolidWorks:**
  1. Run `npm --prefix ui run dev`.
  2. Set the user environment variable `SWAGENT_DEV_URL=http://localhost:5173`.
  3. Restart SolidWorks.

  The panel then loads the Vite dev server. Right-click > Inspect opens DevTools.
- **UI without SolidWorks:** run `npm --prefix ui run dev` and open `http://localhost:5173/?demo`. A mock host
  (`ui/src/lib/mockHost.ts`) stands in for the add-in and seeds a sample conversation.
- **Logs:** `%LOCALAPPDATA%\SwAgent\logs\swagent-YYYYMMDD.log`. Settings > *Open logs* opens the folder.
- **Rebuilding the add-in:** close SolidWorks first, because it locks `SwAgent.dll`. You only need to re-register
  if the output path changes.

## Layout

| Path | What |
|---|---|
| `src/SwAgent/AgentAddIn.cs` | Add-in entry point: ribbon commands, context menus, task pane, document handlers |
| `src/SwAgent/AgentController.cs` | RPC surface for the UI; document, selection and chat events |
| `src/SwAgent/Chat/` | In-file chat storage and crash backups |
| `src/SwAgent/Tools/` | Agent tools: model inspection, view capture, API reference, registry |
| `src/SwAgent/Scripting/` | Roslyn C# REPL that `execute_csharp` runs on |
| `src/SwAgent/UI/` | WebView2 task pane and JSON-RPC bridge |
| `ui/src/agent/` | System prompt and the agent loop (streaming, tools, approvals) |
| `ui/src/components/` | Chat UI |
| `src/SwAgent/Infrastructure/Updater.cs`, `src/SwAgent/update.ps1` | Update check (GitHub Releases), download, install after SOLIDWORKS exits |
| `installer/Unify3DAgent.iss` | Windows installer (Inno Setup 6) |
| `assets/brand/` | Logo and icons |

## Releasing

1. Put the "What's new" text for users in `RELEASE_NOTES.md` (shown in the in-app update dialog).
2. Tag and push: `git tag v0.2.0; git push origin v0.2.0`.

GitHub Actions (`.github/workflows/release.yml`) builds the UI, the add-in and the installer, and publishes a release with
`Unify3D-Agent-Setup-0.2.0.exe` and its `.sha256`. Installed copies find it within a few hours and offer the update.
To build an installer locally: `.scriptsmake-installer.ps1 -Version 0.2.0` (needs Inno Setup 6).

# Integration notes - Track C (categories, long runs, ask_selection, panel)

Files changed: `src/SwAgent/Tools/UserTools.cs`, `src/SwAgent/UI/UnifyRibbon.cs`, `src/SwAgent/AgentController.cs`,
`tests/SwAgent.Tests/BuiltInToolsTests.cs`, `ui/src/**`. No file of another track was touched.

## Front matter keys read by Track C (Track E's TOOL.md must use exactly these)

| Key | Parsed by | Meaning |
|---|---|---|
| `category: Molding & Toolmaking` | `UserTools.CategoryOf` | one line, max 48 chars; blank = none. Built-ins: the user's override in builtin-settings.json wins, and an empty override means "no category". |
| `ribbon_title: Compression Mold` | `UserTools.ListTools` (`ribbonTitle`) | ribbon button title (`RibbonLayout.Title`); the menu and the agent index keep `title`. |
| `long_run: true` | `UserTools.LongRunOf` | true / yes / 1 (any case). |
| `max_steps: 600` | `UserTools.MaxStepsOf` | clamped to 1..1000; a long_run tool without it gets 600; otherwise null (panel default 200). |
| `requires: mold_calc, mold_part, mold_bodies, mold_record` | `UserTools.RequiresOf` | comma/space separated; each must match `^[a-z][a-z0-9_]*$` (BuiltInToolsTests checks every built-in). |

`ListTools()` (and so `userTools.list` / `userTools.changed`) now emits `category`, `ribbonTitle`, `longRun`, `maxSteps`, `requires`.

## Track B (ToolSpec / ToolRunner)

- The panel takes `payload.maxSteps ?? tool.maxSteps ?? (longRun ? 600 : 200)` and `payload.longRun ?? tool.longRun`,
  so it works with or without Track B's payload fields. Please make ToolSpec's `LongRun` / `MaxSteps` / `Requires` /
  `Category` / `RibbonTitle` agree with the table above - ideally by calling `UserTools.LongRunOf`, `MaxStepsOf`,
  `RequiresOf`, `CategoryOf` (internal static, same assembly) and the constants `UserTools.DefaultMaxSteps` (200),
  `LongRunMaxSteps` (600), `MaxStepsCap` (1000). If B parses on its own, the lead should replace one copy at merge.
- `userTools.setValue` now also allows `category` (values are cleaned: one line, 48 chars). Moving a tool between
  categories marks the ribbon stale (`Signature()` includes category and ribbon title).

## Track D (mold_record) - how a long run's follow-up settings end

The panel keeps a long tool run's model, effort, step limit and auto-approval for follow-ups typed in the same chat
(critique G4) only while ALL hold (ui/src/lib/toolRun.ts):
1. the document the tool ran on is active (same add-in key; else same saved path; else, unsaved, same title);
2. no result of the tool's `requires` tools or of `mold_record` has reported `status: done` (the last reported status
   wins);
3. the chat was active (a run ended) within the last 2 hours;
4. the user hasn't closed the banner chip, and no other tool has run in that chat since.

For rule 2, **mold_record's text result must carry a top-level status line** at the start of a line with at most 2
spaces of indentation, e.g. `status: building` / `Record status: done`, or pretty JSON with `  "status": "done",` at
the top level (or a compact JSON object whose first key is status). Nested statuses (per stage, per check) must be
indented deeper or named otherwise (`stage_status`, "stage status: ...") - they are ignored. Error results are
ignored. Set the record's status to `done` only when Phase 6 is complete.

## Track E (TOOL.md) - what the agent can rely on

- `ask_selection {prompt, types?: ["face"|"edge"|"vertex"|"body"|"feature"|"component"], min?, max?}` - pre-select
  suggestions in code first. The result is text:
  `The user pressed Done with 3 faces selected - use the [S#] references with Ref("S#") and select_refs:` then one line
  per entity `[S7] Face of <feature> - <detail>` (entities without a persistent id: `- Face of ... (no persistent id:
  not addressable with Ref)`), then optional `Not counted (not faces): 1 Edge.`, `That is fewer than the N you asked
  for.`, and `Their note: "..."` (typed in the card, typed in the chat box, or spoken - all count as Done). Skip gives
  `The user skipped this (pressed Skip) ...`. The refs are merged into the chat's selection refs.
- The system prompt now says long tools follow their TOOL.md reporting rules instead of the one-line finish and the
  reply-length preference, so TOOL.md should state its phase/gate/report rules explicitly.
- Phase 6 should end with a `mold_record` call that sets status `done` (ends the sticky follow-up settings, see D).

## Things for the lead to check in the browser preview (`npm run dev` in ui, mock host)

Set `localStorage["swagent-mock:settings"] = {"tourDone":true,"voiceAsked":true,"settingsVersion":2}` and any dummy
`swagent-mock:apiKey` to get past the tour/settings. I checked all of this in the built-in browser on this branch:
1. Workflows menu: section "Molding & Toolmaking" (built-in Design compression mold, then the user's Flash grooves
   tool - different capitalisation, same section - and the Create compression mold workflow), then "Workflows",
   "Built in - Unify3D tools", "Tools - on the Unify3D ribbon tab".
2. Hover a row -> the tree icon opens "Category of ..." (input focused, Enter saves, datalist of existing categories);
   the row moves to its new section (mock keeps it in localStorage `swagent-mock:toolSettings`).
3. ask_selection card: in the console with `__agentStore`, add a thread whose last assistant message has a
   `tool_use` `ask_selection` and a run whose `question` has `selection: {types:["face"], min:1}`. The card shows the
   prompt, the live count ("1 face selected" from the selection chips), chips, a note box, Skip / Done. Done answers
   with `[S3] Face of Boss-Extrude1 - ...` and `Not counted (not faces): 1 Edge.` and merges S3 into `selRefs`
   (numbering continues after the highest existing id). The composer placeholder says a typed note counts as Done.
4. Banner chip: "Compression Mold - follow-ups continue it: Opus 5.5 · high effort · auto-approve · 600 steps" with X;
   switching `activeDocKey` to another doc shows "paused: <doc> isn't the active document" and a follow-up then uses
   the chat's model and the 5-minute cache; `lastActive` older than 2 h clears it on the next send.
5. Running Design compression mold from the menu (mock hands the run over) sends Opus/high with
   `cache_control {type:"ephemeral", ttl:"1h"}`; a non-long tool run afterwards clears the chip.
In SolidWorks: the Unify3D tab shows a box per category (e.g. Compression Mold + the user's categorised tools), the
uncategorised built-ins, then the user's tools + New Tool; button hints start with "[Molding & Toolmaking]".

## Not done / left open

- Workflows (kind workflow) with `long_run` run through `[Run workflow: ...]` and get no step limit or sticky settings;
  only ribbon tools (kind tool) do. The mold tool is kind tool.
- No TS test runner exists in ui; the pure modules (lib/selection.ts, lib/toolRun.ts, lib/toolGroups.ts) were checked
  with a node script outside the repo (11 checks, all passing). Consider adding vitest later.

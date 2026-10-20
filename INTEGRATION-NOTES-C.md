# Integration notes - Track C (categories, long runs, ask_selection, panel)

Files changed: `src/SwAgent/Tools/UserTools.cs`, `src/SwAgent/UI/UnifyRibbon.cs`, `src/SwAgent/AgentController.cs`,
`tests/SwAgent.Tests/BuiltInToolsTests.cs`, `ui/src/**`. No file of another track was touched.

## Front matter keys read by Track C (Track E's TOOL.md must use exactly these)

| Key | Parsed by | Meaning |
|---|---|---|
| `category: Molding & Toolmaking` | `UserTools.CategoryOf` | one line, max 48 chars; blank = none. Built-ins: the user's override in builtin-settings.json wins, and an empty override means "no category". |
| `ribbon_title: Compression Mold` | `UserTools.ListTools` (`ribbonTitle`) | ribbon button title (`RibbonLayout.Title`); the menu and the agent index keep `title`. |
| `long_run: true` | `UserTools.LongRunOf` | true / yes / 1 (any case). |
| `max_steps: 600` | `UserTools.MaxStepsOf` | clamped to 50..1000 (design 2.1.1, `UserTools.MaxStepsMin`/`MaxStepsCap`; the panel's `clampSteps` uses the same range); a long_run tool without it gets 600; otherwise null (panel default 200). |
| `requires: mold_calc, mold_part, mold_bodies, mold_record` | `UserTools.RequiresOf` | comma/space separated; each must match `^[a-z][a-z0-9_]*$` (BuiltInToolsTests checks every built-in). |

`ListTools()` (and so `userTools.list` / `userTools.changed`) now emits `category`, `ribbonTitle`, `longRun`, `maxSteps`, `requires`,
and for built-ins `categoryDefault` (the shipped category; the category dialog offers "Reset to ..." when the user changed it).

`userTools.setValue` for a built-in stores the user's override in builtin-settings.json; a `null` value, or the value the
tool ships with, removes the override (so later changes to the shipped value apply again).

## Track B (ToolSpec / ToolRunner)

- The panel takes `payload.maxSteps ?? tool.maxSteps ?? (longRun ? 600 : 200)` and `payload.longRun ?? tool.longRun`,
  so it works with or without Track B's payload fields. Please make ToolSpec's `LongRun` / `MaxSteps` / `Requires` /
  `Category` / `RibbonTitle` agree with the table above - ideally by calling `UserTools.LongRunOf`, `MaxStepsOf`,
  `RequiresOf`, `CategoryOf` (internal static, same assembly) and the constants `UserTools.DefaultMaxSteps` (200),
  `LongRunMaxSteps` (600), `MaxStepsMin` (50), `MaxStepsCap` (1000). If B parses on its own, the lead should replace
  one copy at merge (keep one parser, range 50..1000).
- `userTools.setValue` now also allows `category` (values are cleaned: one line, 48 chars). Moving a tool between
  categories marks the ribbon stale (`Signature()` includes category and ribbon title).

## Track D (mold_record) - how a long run's follow-up settings end

The panel keeps a long tool run's model, effort, step limit and (ribbon tools only) auto-approval for follow-ups typed
in the same chat (critique G4) only while ALL hold (ui/src/lib/toolRun.ts):
1. one of the run's documents is active: the one it started on (same add-in key; else same saved path; else, unsaved,
   same title), or a document that became active while a run of the tool was working (e.g. the Phase 1a part it built
   from a drawing). No document known = not matching;
2. the record hasn't reported `done` (below);
3. the chat was active (a run ended) within the last 2 hours;
4. the user hasn't closed the banner chip or changed the chat's model or effort, no other tool or workflow has run
   in that chat, and no resend went back to before the run started.
The user's read-only approval mode always wins over the tool's auto-approval.

**Request for Track D - one explicit line.** Every `mold_record` action that WRITES the record (`init`, `merge`, and
any other write) must print the record's status on its own line, starting at column 0:

```
record_status: building
```

(values: planning | sizing | building | verifying | done). Print it after A1's `MoldRecord.Merge` text, e.g.
`merged; status building; valid` + newline + `record_status: building`. Reads (`read`, `path`, `validate`) must NOT
print it - the panel ignores read results anyway (input `action` read/path/validate/get/list/summary/show, or no action),
so re-running the tool on a finished mold (Phase 0 reads `status done`) doesn't end the new run. JSON in a result is
never parsed for a status, so nested `stages[]/checks[]/simulations[]` statuses can't be mistaken for it.
Fallbacks while D doesn't print the line: a `mold_record` merge result starting `merged; status <x>` (A1's
`MoldRecord.Merge` text passed through), else the merge input's `data.status` (object or JSON string). The last write
in the turn wins. Error results are ignored. Other tools listed in the tool's `requires` may use the same
`record_status:` line (only that line counts for them). Set `done` only when Phase 6 is complete (Track E's TOOL.md
merges `status: "done"` last). The mock host (ui/src/lib/mockHost.ts) has a `mold_record` that follows this.

## Track E (TOOL.md) - what the agent can rely on

- `ask_selection {prompt, types?: ["face"|"edge"|"vertex"|"body"|"feature"|"component"], min?, max?}` - pre-select
  suggestions in code first. The result is text:
  `The user pressed Done with 3 faces selected - use the [S#] references with Ref("S#") and select_refs:` then one line
  per entity `[S7] Face of <feature> - <detail>` (entities without a persistent id: `- Face of ... (no persistent id:
  not addressable with Ref)`), then optional `Not counted (not faces): 1 Edge.`, `That is fewer than the N you asked
  for.`, and `Their note: "..."` (typed in the card, typed in the chat box, or spoken - all count as Done). Skip gives
  `The user skipped this (pressed Skip) ...`. The refs are merged into the chat's selection refs. Up to 500 entities
  are read; a larger selection adds `The list is cut: only the first 500 of N selected entities were read. ...`.
  Unknown `types` values are reported (`(Unknown types ignored: ... )`) instead of silently widening the request.
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
   `cache_control {type:"ephemeral", ttl:"1h"}`; a non-long tool run or a workflow run afterwards clears the chip.
6. Done detection with the mock `mold_record`: a fake API reply calling `mold_record {action:"merge", data:{status:"done"}}`
   ends the run (notice "... is done"); `{action:"read"}` or a merge with status building keeps it open.
   (I checked items 4-6 plus: a workflow queued behind a running long tool waits without "Send now" and runs with the
   chat's model; resending the tool's own prompt keeps the run, resending an earlier prompt ends it; changing the
   chat's effort ends it; read-only approval shows "read-only" in the chip; an ask_selection with
   `types:["face","surface"], min:900, max:800` normalises to min=max=500 and reports the unknown type.)
In SolidWorks: the Unify3D tab shows a box per category (e.g. Compression Mold + the user's categorised tools), the
uncategorised built-ins, then the user's tools + New Tool; button hints start with "[Molding & Toolmaking]".

## Tests of the panel's pure modules

`ui/src/lib/checks/lib.check.mjs` (16 checks: selection answers incl. cut lists and unknown types, step range, document
matching incl. documents the run activated, done detection incl. the pretty-printed mold.json of design 4.3, reads,
nested statuses, the Workflows-menu sections). Run from `ui`: `node src/lib/checks/lib.check.mjs` (Node 22.18+ runs the
.ts imports directly; nothing imports the file, tsc and vite ignore it). **Request for the lead:** add vitest to
`ui/package.json` (not Track C's file) and move these checks into `*.test.ts`; the store-level behaviour (workflow runs
closing a long run, resend, queue) is then worth a test with a mocked bridge too.

## Not done / left open

- Workflows (kind workflow) with `long_run` now get their step limit, the 1-hour cache and follow-up settings, but keep
  the chat's model and approval (only ribbon tool runs are auto-approved; the `model:` front matter of workflows is still
  not applied, as before).
- A document the user activates by hand while a long tool run is working also counts as the run's document (the panel
  can't tell the agent's activations from the user's); it only matters until the run reports done or goes idle.

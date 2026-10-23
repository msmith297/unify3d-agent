# ApiCheatSheet

Dev-only console (not shipped) behind the agent's SolidWorks API cheat sheet, `ui/src/agent/cad/swApiCheatsheet.ts`. The
SolidWorks CAD profile (`ui/src/agent/cad/solidworks.ts`, API section) puts the sheet in the cached system prompt, so the agent
has the exact signatures of the members it uses most without calling `search_sw_api`, and the gotchas it kept hitting
(wrong interface: CS1061; `ISelectData` for `SelectData`: CS1503; array casts; selection marks; rotation sign).

| File | What |
|---|---|
| `members.json` | The curated members, grouped by task (`"IFeatureManager.InsertRefPlane"`, or `{ "member", "hint" }` for a short note on the line; `"use": "Features.Cut"` for a member a built-in helper covers: the line gives only its parameter count and return type, and names the helper). An entry without a dot is a swconst enum: `{ "member": "swEndConditions_e", "values": [...] }` lists those values (all of them without `values`). `budgetTokens` is the sheet's limit (characters / 4). |
| `notes.md` | The gotchas, appended to the sheet as written (the `#` title and `<!-- -->` comments are left out). |
| `Program.cs` | Reflects every member from the interop and writes the `.ts`. A member that isn't on the named interface is an error that names the interfaces that do have it. |

The signatures come from `SolidWorks.Interop.sldworks.dll` / `swconst.dll` of the `Xarial.XCad.SolidWorks` package, the same
interop the add-in compiles code cells against, in the format `search_sw_api` prints.

## Use

From the repository root:

```
dotnet build tools/dev/ApiCheatSheet
tools/dev/ApiCheatSheet/bin/Debug/net48/ApiCheatSheet.exe            # write the .ts
tools/dev/ApiCheatSheet/bin/Debug/net48/ApiCheatSheet.exe check      # exit 1 if the shipped .ts is stale, a member is missing or over budget
tools/dev/ApiCheatSheet/bin/Debug/net48/ApiCheatSheet.exe find FeatureByName,GetBodies2   # which interfaces have a member (for curating)
tools/dev/ApiCheatSheet/bin/Debug/net48/ApiCheatSheet.exe type IFeatureManager Split     # one interface's members
```

Options: `root=<repo>` (default: found from the current folder), `interop=<path to SolidWorks.Interop.sldworks.dll>` to
reflect another interop, e.g. the installed SolidWorks' `api\redist` (swconst is read from the same folder).

Also checked without the tool:
- `node ui/src/lib/checks/lib.check.mjs` (runs `cheatsheet.check.mjs`): the sheet is under its budget and names every member
  in `members.json`.
- `dotnet test tests/SwAgent.Tests` (`ApiCheatSheetTests`): every member in `members.json` exists on its interface in the
  interop the add-in loads, and its line in the sheet is the exact reflected signature.

## Changing the sheet

Add the member to `members.json` (or a line to `notes.md`), run the tool, check the budget it prints, rebuild the UI. Only
put a selection mark or sign in the sheet as fact when a measured helper (`src/SwAgent/Mold/Geometry`) or a run in SolidWorks
has verified it; otherwise say it is unverified and how to check.

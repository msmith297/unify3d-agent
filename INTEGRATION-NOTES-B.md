# Integration notes - Track B (PropertyManager page extensions)

Track B owns these files:
- `src/SwAgent/Tools/ToolSpec.cs`
- `src/SwAgent/Tools/Units.cs` (new)
- `src/SwAgent/Tools/ToolData.cs` (new): `ToolData`, `ToolForm`, `FormField`, `RememberStore`
- `src/SwAgent/UI/ToolPage.cs` (with `PageQuirks`)
- `src/SwAgent/Scripting/ToolInputs.cs`
- `src/SwAgent/Tools/ToolRunner.cs`
- `tools/dev/page-test/**`
- `tests/SwAgent.Tests/{UnitsTests,ToolDataTests,ToolSpecTests}.cs`

Below: the changes other tracks need to make, and the facts they code against.

## Requests to other tracks

### Track D - `src/SwAgent/Tools/ToolRegistry.cs` (needed, small)

`SchemaFor` treats every generic `IEnumerable` as an array. That includes `Dictionary<string, T>`, so the agent sees `row_filter`, `value_map`, `fills` (`Dictionary<string,string>`) and `show_when` (`Dictionary<string,string[]>`) as arrays of strings. If the agent follows that schema, `save_user_tool` fails to deserialize. Add a dictionary case before the `IEnumerable` branch, next to the B1 fix:

```csharp
var dict = t.GetInterfaces().Concat(new[] { t }).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
if (dict != null) return new JObject { ["type"] = "object", ["additionalProperties"] = SchemaFor(dict.GetGenericArguments()[1]) };
```

Then remove the `Skip` from `ToolSpecTests.Dictionary_options_are_objects_in_the_agents_schema`. Until then, nothing crashes: `The_agents_tool_schema_still_builds_with_the_new_options` passes, and hand-written inputs.json files load correctly.

### Track C - UI, UserTools, systemPrompt

**1. Payload (`ui/src/lib/types.ts` `ToolRunPayload`).** The panel needs no change to work. `value` is still a pre-formatted string, which `stageToolPrompt` prints as it comes. New optional fields:
- Top level: `maxSteps: number` (default 200, 600 for `long_run`, clamped 50-1000) and `longRun: boolean`.
- Per input:
  - `display?: string`: same as `value` when set;
  - `si?: number | number[] | null`: the SI value; an array in m for `size`; null when not set or n/a;
  - `table?: number`: the SI value a data row filled in;
  - `edited?: true`;
  - `unit?: string`: the unit the user saw, ASCII, e.g. `"psi"`;
  - `notApplicable?: true`;
  - `row?: object`: the selected data row of a `ChoicesFrom` choice.
- Inputs hidden by `ShowWhen`, and `info` lines, are not in `inputs`. Hidden inputs are not sent.

**2. ListTools / ribbon / palette.** The front-matter keys are `category`, `ribbon_title`, `long_run` (true/yes/1), `max_steps` and `requires` (comma- or space-separated, or `[a, b]`). Parse them the same way as `ToolSpec`: `ToolSpec.ApplyRunSettings(spec, frontMatter)` is `internal static`, so you can reuse it.
- `ToolSpec` fields: `Category` (null when unset), `RibbonTitle` (null when unset), `LongRun`, `MaxSteps`, `Requires` (`string[]`), `Dir`.
- `ToolRunner` already refuses a tool whose `requires` names an agent tool that isn't registered. The status reads "Unify3D ▸ {title}: needs a newer add-in (missing x, y)".

**3. Reserved names.** Track B stores files under `UserTools.Root`, which is `%APPDATA%\SwAgent\tools`:
- `last-inputs\<tool>.json`: remembered values;
- `page-quirks.json`: page fallbacks;
- `builtin-data\<tool>\<file>`: shop overrides of built-in data (also read by A1).

`ListTools` already skips folders without TOOL.md. Please also make `Save` refuse the tool names `last-inputs` and `builtin-data`, so a user tool can't share a folder with them.

**4. systemPrompt sentence** (design 2.1.5 / contract C). This is where the inputs are described (`systemPrompt.ts:69`):

> Inputs can also use: `unit` pressure | temperature | mass | force | tonnage | viscosity | time (spec units MPa, degC, kg, kN, kN, cP, min; the page shows the document's system, macros get SI), `unit_choices` (units the user picks per field, remembered), `optional` + `enabled` (a checkbox switches the value on; off = Inputs.Limit is null), type `size` (`dims` 2 or 3 length boxes; Inputs.Size in m), type `info` (`text` with {id}, {id.field} of a choice's row and {...|unit}), `choices_from` "file.json#key" + `choice_key` + `depends_on` + `row_filter` / `value_map` (cascading dropdowns from a data file in the tool folder), `fills` {target: "path.in.row[|unit]"} (picking an entry fills editable fields; null hides them), `show_when` {input: [values]}, `remember` (last OK value becomes the default) and `group_expanded`.

## Facts for Track E (`tools/builtin/design-compression-mold/`)

**Property names.** These are exactly as in design 2.1.1 plus `UnitChoices`, in PascalCase in inputs.json: `ChoicesFrom`, `ChoiceKey`, `DependsOn`, `RowFilter`, `ValueMap`, `Fills`, `Optional`, `Enabled`, `ShowWhen`, `Remember`, `Dims`, `Text`, `GroupExpanded`, `UnitChoices`. New types: `size`, `info`. New units: `pressure`, `temperature`, `mass`, `force`, `tonnage`, `viscosity`, `time`. `tools/dev/page-test/inputs.json` is a working example of every one of them; it is shaped like design 1.4.

**`Fills`.** The value after `|` is a unit symbol, for data stored in another unit: `"moldingPressurePsi.typ|psi"`. Known symbols include:
- pressure: psi, bar, MPa, kPa, Pa, kgf/cm2, ksi;
- temperature: degC, degF, K;
- mass: kg, lb, g;
- force: N, kN, lbf, kgf;
- tonnage: t, US ton;
- viscosity: cP, Pa*s, P;
- time: s, min, h;
- length: mm, in, cm, m, ft;
- angle: deg, rad.

Without `|`, the value is in the target's spec unit: mm, deg, %, MPa, degC, kg, kN (also for tonnage), cP, min.

**A null row value.** A whole field that is null (`"mooney": null`) and a null leaf (`"viscosityCp": {"typ": null}`) both hide the target. The payload line reads `n/a for this <label of the filling choice, lower case>`. With part_grade's Label "Grade" it reads "n/a for this grade" (design 2.1.5 wrote "for this material").

**`UnitChoices`.**
- Entries are symbols of the field's quantity: pressure `["psi","bar","MPa","kgf/cm2"]`, tonnage `["US ton","t","kN"]`.
- The chosen unit is remembered for every field that offers choices, whether or not `Remember` is set.
- With no remembered unit, the page uses the document's unit if it is in the list (psi for inch documents, MPa for metric), else the first entry.
- A one-entry list fixes the unit: e.g. `["bar"]` shows bar with no dropdown.
- Put `"Max": 100` on gauge fields and `"Max": 150` on-part pressure (C1). Ranges are in spec units.

**Percent.** The page draws percent exactly as before: a plain box with no "(%)" added, so existing tools look the same. Put the unit in the label ("Shrinkage (%)"). The payload adds " %".

**Defaults in another system** are rounded for display: 3 significant digits when that is within 0.05 %, otherwise 4. 13.79 MPa shows 2000 psi, 20.68 MPa shows 3000 psi, 444.8 kN shows 50 US ton, 23 kg shows 50.7 lb.

**ChoicesFrom data choices.**
- The default entry is, in order: the remembered value, `Default`, the row with `isDefault: true` among the filtered rows, the first entry.
- `RowFilter` matching is case-insensitive. An array field matches when it contains the pattern. `"*"` matches any non-null value.
- `ValueMap` maps the parent's displayed value before substitution.

**Payload lines** (what the agent reads; tested in `ToolDataTests`):
- Converted numbers: `2000 psi (13.79 MPa)`, `212 degF (100 degC)`, `50 US ton (444.8 kN)`, `1200 cP (1.2 Pa*s)`, `51 lb (23.13 kg)`, `6 min`.
- Edited table values: `1.8 % (table 1.5 % - edited by the user)`. A table value left as it is reads as a plain value.
- `not set` (Optional off); `n/a for this grade`.
- Sizes: `600 x 400 x 150 mm`, or `23.62 x 15.75 x 5.906 in (600 x 400 x 150 mm)` in an inch document.
- Data choices: `6061-T651 [al_6061_t651]`, `TDI-polyether / MBOCA 90A [pu-tdi-ether-90a; data confidence medium; sources S19, S20, K-PU]`. Confidence and sources are shown only when the row's sources are short keys (no spaces, 12 characters or fewer), as in materials.json; the long citations in mold-materials.json stay in `row`. A choice whose entry names several rows (a family) shows just the entry.
- Not built, compared with design 2.1.5: the integer "tier" note (`5000 (tier T2: ...)`), "; compare sorted" on sizes and "(check skipped)". These are tool-specific, so TOOL.md should say them.

**Macro accessors** (`ToolInputs`): `Number` (SI), `Limit(id)` (`double?`, null when off, n/a or hidden), `Size(id)` (`double[]` in m), `Row(id)` (JObject), `Display(id)`, `Table(id)` (SI), `Edited(id)`, `Hidden(id)`. SI means m, rad, Pa, degC, kg, N, Pa*s and s; percent stays 0-100.

**Info templates.**
- `{id}` is the input's value as shown, e.g. "2000 psi".
- `{id.path}` is a field of that choice's selected row.
- `{...|temperature}` gives a value in that quantity's spec unit (degC). `{...|psi}` gives a value stored in psi. Both are shown in the document's system.
- Unknown ids are left as written. Null values read "n/a".

**Remember.** Values are stored in spec units in `%APPDATA%\SwAgent\tools\last-inputs\design-compression-mold.json` on OK. Cancel stores nothing; files, selections and text are never stored. At start a remembered value isn't overwritten by a fill; a grade the user picks does overwrite. `press_temp` without `Remember` follows the material, as designed.

**Required.**
- A `Required` choice needs an entry.
- A `Required` + `Optional` input must be switched on.
- Converted numbers outside Min/Max disable OK, and a bubble says why.
- Hidden inputs are never required.

## Facts for Track A1 (MoldData)

- `ToolData` (the page) merges shop overrides from `UserTools.Root\builtin-data\<tool>\<file name>`. That is the file name only: `data/materials.json` is overridden by `builtin-data\design-compression-mold\materials.json`.
- Merge rules:
  - arrays of rows with `id`: same id replaces the row, new ids are appended;
  - objects: key by key, recursively;
  - top-level scalars and arrays without ids: kept from the shipped file.
- `ToolData.MergeOverride(JToken shipped, JToken over)` is `internal static` and pure. `MoldData.Load(toolDir, overrideDir)` should use it, or the same rules, so the page and the calculator see the same rows.

## For the lead (integration)

- `BuiltInToolsTests` for the new tool can use:
  - `ToolSpec.CheckInputs(spec.Dir, spec.Inputs)`, which must be empty (DependsOn order, ChoicesFrom files, Fills/ShowWhen/RowFilter ids, units, UnitChoices);
  - `new ToolForm(spec, ToolData.For(spec), imperial, null)`, to check that every data choice has entries, `Blocking()` is null, and Fills paths resolve (`ToolForm.SplitFill`, `ToolData.ValueAt`).
  - `ToolSpecTests.Page_test_data_resolves_every_choice_and_fill_path` shows the pattern.
- The fixes for spikes S1-S6 that can't run here are coded defensively. The checklist is in `tools/dev/page-test/README.md`. `%APPDATA%\SwAgent\tools\page-quirks.json` switches the fallbacks:
  - `comboRefill: variants`: one dropdown per entry list;
  - `rangeAfterShow: false`: wide ranges for unit-choice boxes;
  - `hidden: disable`: hidden inputs are greyed out instead of hidden.
  The page sets the first two itself when it detects the failure.
- Existing tools take the original code paths. Their controls, captions and defaults are unchanged. Their payload text is unchanged too, except that a `percent` number gains " %". Group ids now come from the control-id counter (C4).

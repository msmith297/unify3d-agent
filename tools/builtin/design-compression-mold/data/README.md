# design-compression-mold: data files

This folder holds the tables, templates and schema of the built-in tool "Design compression mold for plastics and resins". The whole tool folder is copied next to the add-in on every build and install (`builtin-tools\design-compression-mold\`), so **don't edit these files in place on a user's PC: they are replaced by the next install.** Shop changes go in an override folder (below).

## What each file is

| File | What it holds | Read by |
|---|---|---|
| `materials.json` | Part materials: 96 subtypes in 38 families (`rows`), with process data: shrink and its basis, SG, viscosity, Mooney, pot life, mold temperature, demold time, post-cure, molding pressure, draft, flash/land/vent/overflow values, charge excess, hazards, confidence and sources. | The page (family and grade lists, the fields they fill), `mold_calc` |
| `mold-materials.json` | Mold grades (aluminium and steels) with strength at temperature (10,000 h basis), fatigue, CTE, density, service limits, `short` page labels and `use` (plate, insert, backing, stop); fasteners (inch and metric), thread inserts, dowels, stock thicknesses. | The page (grade lists), `mold_calc` |
| `shop-standards.json` | The shop's own rules: flash grooves (lip, moat, runners), plate rounding, blank allowance, dowels, pry slots, chamfers, thermocouple wells, handles, pour rates, custom properties. | `mold_calc`, the agent |
| `sizing-defaults.json` | Every judgement factor of the structural method in one place: safety factors, flash-gap classes and the gap budget, bearing divisors, bolt preload, tightening scatter and hot proof factors, minimum thicknesses, lifting defaults. | `mold_calc` |
| `validation/` | Worked examples (`examples.json`, read by the unit tests) and the results of the validation runs: `sizing-table.csv`, `montecarlo-summary.json`, `solid3d-summary.json`. | Tests, the agent (quotes them in reports) |
| `mold.schema.json` | JSON Schema (draft 2020-12) of the design record `mold.json`, schema id `unify3d.mold/1`. | Tests, simulators |
| `setup-sheet.html` | Template of the operator's setup sheet. It reads the record plus `sheet`, the operator numbers `SetupSheet.Build` derives (pieces, press setting, partial-cavity table, bolts, lid lifting, preheat, maintenance). | `mold_record setup_sheet` |
| `machining-sheet.html` | Template of the machine shop's sheet. It reads the record plus `machining`, which `MachiningSheet.Build` derives: per machined body the stock and blank, holes with fits and limits, flatness, finish and notes; general rules; the purchased-hardware list (also written as `hardware-bom.csv`); the screw table. | `mold_record machining_sheet` |
| `examples/mold.sample.json` | A complete example record: the 2-cavity PU pour mold of worked example E2 as the calculator writes it, plus the keys the agent and `mold_bodies` write (including a PART-REF and a Garbage body). It validates against the schema. Use it as the reference for record shapes. | Tests, the agent |

Units inside the data and the record are SI-like: mm, MPa, degC, kg, kN, cP, min. A key that names another unit says so (`moldingPressurePsi`, `perCavity_g`, `torqueDry_Nm`). The page and the reports show the document's units, with SI in brackets.

## Shop overrides

Put a file with the **same name** in:

```
%APPDATA%\SwAgent\tools\builtin-data\design-compression-mold\
```

That folder survives reinstalls. The page, `mold_calc` and `mold_record save_default` all read it. Merge rules:

- `materials.json` `rows` and `mold-materials.json` `mold_materials`: a row with the same `id` replaces the shipped row (give the whole row); a new `id` is added at the end.
- Other objects are merged key by key. An override only needs the keys it changes, for example `sizing-defaults.json` with `{ "sfStatic": 2.5 }`, or `shop-standards.json` with `{ "flash": { "grooveWidth": { "in": 0.375 } } }`.
- Everything else comes from the shipped file.
- Lengths in `shop-standards.json` are `{"in": x}` or `{"mm": x}`.

The easy way: ask the agent, for example "save 2.1 % as my EPDM 70A shrink" or "our dowels are 3/8 in". It writes the override with `mold_record save_default` after checking the keys against the shipped table.

## Template syntax (setup and machining sheets)

The templates are plain HTML with placeholders, filled by `SwAgent.Mold.Template` without any library. The context is the whole record plus one derived object: `sheet` for the setup sheet, `machining` for the machining sheet.

| Placeholder | Meaning |
|---|---|
| `{{a.b.c}}` | The value at that dotted path, HTML-escaped. Index an array with brackets: `{{shrink.scale[0]}}`. |
| `{{this}}` | Inside a block: the current item itself (for lists of strings). |
| `{{this.a}}` | Inside a block: a key of the current item. Use it for every item key, so a key the item lacks is reported instead of being taken from an enclosing item or the root. |
| `{{@index}}`, `{{@number}}` | Inside a block: the item's position from 0, or from 1. |
| `{{#each a.b}} ... {{/each}}` | Repeats its content for every element of the array at that path. Blocks nest (`{{#each this.holes}}` inside `{{#each machining.bodies}}`). An empty array prints nothing. |
| `{{#if a.b}} ... {{else}} ... {{/if}}` | False for a missing path, null, false, "", 0 and []. For parts that only some jobs have (the class A pour time, a post-cure). |
| `{{path\|unit}}` | A number stored in `unit`, printed in the mold document's unit system first, the other in brackets: `{{sheet.charge.perCavity_g\|g}}` gives "454.3 g (1.001 lb)". |

What prints, and what counts as unresolved:

- A **missing key** prints `-` and is reported as unresolved. `mold_record setup_sheet` / `machining_sheet` list the unresolved paths, and acceptance check A15 wants none.
- A key whose value is **null** is deliberately empty: it prints `-` and is not reported. Write null for "not known" or "does not apply" (`partNumber`, an unset `daylight`, a not-run simulator's `summary`), never a made-up string.
- An `{{#each}}` over a missing key is reported; write `[]` for "none". An `{{#if}}` over a missing key is just false.

Unit filters (the unit the value is **stored** in), and what an imperial document prints first:

| Filter | Prints | Filter | Prints |
|---|---|---|---|
| `mm` | in | `degC` | degF |
| `mm2` | in² | `Nm` | lbf·ft |
| `mm3` | in³ (cm³ first in metric) | `min` | min |
| `MPa` | psi | `pct` | % |
| `kN` | US ton (t in brackets in metric) | `g` | g (lb in brackets) |
| `kg` | lb | | |

No other filter exists: an unknown one is a template error, never a silent pass-through. Seconds, kelvin, cP and degrees have no filter; write the unit after the placeholder (`{{sheet.pour.secondsTotal}} s`). Numbers print with 4 significant digits (a 0.01 mm flatness prints as 0.0003937 in (0.01 mm)). The record's `units.display` ("imperial" or "metric") picks the system. Pressures print in psi and MPa; the gauge's own unit (bar, kgf/cm²) is not on the sheet yet.

Keep placeholders out of HTML comments: the filler replaces them there too.

## The design record `mold.json`

- It lives in `<mold file name>.mold\mold.json` next to the mold part. The part's custom property `Unify3D.MoldRecord` names the folder, and `Unify3D.MoldId` holds the record id.
- Consumers ignore unknown keys. Additive changes keep the schema id `unify3d.mold/1`; a breaking change makes it `/2`.
- The arrays `mold_record merge` merges by id (`checks`, `stages`, `bodies`, `hardware.bolts` and the others listed in the schema's description) carry a string `id`.
- The schema's description says which tool writes which key. The sheets read mostly what the calculator and `mold_bodies` write; the agent adds only `identity`, `setup`, `views`, `report`, `part.drawing`, `bodies[].machining.notes` and `hardware.bom` (purchased items the calculator doesn't size).
- Geometry a simulator returns is located by `{bodyId, point, normal, stepFaceId?}` (body ids match `exportsodies\<bodyId>.step`), never by SolidWorks persistent ids.
- `materials.part.rheology`, `.kinetics`, `.thermal` and `.mechanical` are reserved for simulator data, with `status: "unknown"` until someone fills them.
- A pressure field for the structural check (`structural.loadCases.LC1.pressureField`) is either `(u, v, w, p)` samples in the mold frame or per-face samples `{bodyId, point, normal, p_MPa}`.

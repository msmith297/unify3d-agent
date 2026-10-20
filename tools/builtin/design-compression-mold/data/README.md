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
| `setup-sheet.html` | Template of the operator's setup sheet. | `mold_record setup_sheet` |
| `machining-sheet.html` | Template of the machine shop's sheet: per body stock, holes, fits with limits, flatness, finish, corner reliefs; purchased hardware; screws and torques. | `mold_record machining_sheet` |
| `examples/mold.sample.json` | A complete example record (a 2-cavity PU pour mold). It validates against the schema and fills every placeholder of both templates. Use it as the reference for record shapes. | Tests, the agent |

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

The templates are plain HTML with placeholders, filled from `mold.json` without any library:

| Placeholder | Meaning |
|---|---|
| `{{a.b.c}}` | The value at that dotted path. A numeric segment indexes an array: `{{box_mm.0}}`. The value is HTML-escaped. A missing or null value prints `-` and is reported as unresolved (except inside an empty block). |
| `{{this}}` | Inside a block: the current item itself (for lists of strings). |
| `{{#each a.b}} ... {{/each}}` | Repeats its content for every element of the array at that path. Blocks nest. Inside a block, a path is looked up on the current item first, then on the enclosing items, then on the record root. A missing or empty array prints nothing. |
| `{{path\|unit}}` | A number stored in `unit`, printed in the mold document's unit system first, SI in brackets: `{{process.charge.perCavity_g\|g}}` gives "1.001 lb (454.2 g)" in an imperial document and "454.2 g" in a metric one. |
| `{{path\|gauge}}` | A pressure in MPa printed in the gauge unit the user chose for the press (`process.gauge.unitShown`: psi, bar, MPa or kgf/cm2), MPa in brackets. |

Units a filter may name (the unit the value is stored in), with the imperial unit it prints:

| Filter | Imperial | Filter | Imperial |
|---|---|---|---|
| `mm` | in | `degC` | degF |
| `mm2` | in² | `K` (a temperature difference) | degF difference |
| `mm3` | in³ | `Nm` | lbf·ft |
| `MPa` | psi | `min`, `s` | (same) |
| `kN` | US ton (lbf below 1 US ton) | `cP` | (same) |
| `kg` | lb | `deg` | (same) |
| `g` | lb (oz below 1 lb) | | |

Precision follows the size of the value, because fits and flatness are tolerances: a length under 1 mm prints with 4 decimals in inches and 3 in mm (0.0006 in (0.015 mm)); fit limits always print with 4 decimals in inches and 3 in mm; other lengths with 3 decimals in inches and 2 in mm. Numbers without a filter print as stored. The record's `units.display` ("imperial" or "metric") picks the system.

Keep placeholders out of HTML comments: the filler replaces them there too.

## The design record `mold.json`

- It lives in `<mold file name>.mold\mold.json` next to the mold part. The part's custom property `Unify3D.MoldRecord` names the folder, and `Unify3D.MoldId` holds the record id.
- Consumers ignore unknown keys. Additive changes keep the schema id `unify3d.mold/1`; a breaking change makes it `/2`.
- Every array of objects has a string `id`; `mold_record merge` matches on it.
- Geometry a simulator returns is located by `{bodyId, point, normal, stepFaceId?}` (body ids match `exports\bodies\<bodyId>.step`), never by SolidWorks persistent ids.
- `materials.part.rheology`, `.kinetics`, `.thermal` and `.mechanical` are reserved for simulator data, with `status: "unknown"` until someone fills them.
- A pressure field for the structural check (`structural.loadCases.LC1.pressureField`) is either `(u, v, w, p)` samples in the mold frame or per-face samples `{bodyId, point, normal, p_MPa}`.

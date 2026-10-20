# Integration notes - Track A1 (data + calculator + record)

What Track A1 delivers, what the other tracks must line up with, and the decisions made where the design and the
critique left room. Everything here is pure C# (no SolidWorks types) under `src/SwAgent/Mold/` (namespace `SwAgent.Mold`).

## 1. Files

- `src/SwAgent/Mold/`: `MoldData`, `SizingDefaults(.Builtin)`, `Si`, `Check`, `Design`, `Sizing`, `MoldCalc`,
  `Materials/AtTemperature`, `Process/{Press, Shrink, Charge, Lifting}`, `Structural/{GapBudget, Fatigue, Coefficients,
  Walls, Plates, Bearing, Keys, Bolts, Threads, Fits (+ Pins)}`, `Layout/{CavityLayout, PlateSplit, StockRounding}`,
  `Record/{MoldRecord, Template, SetupSheet, MachiningSheet (+ HardwareBom)}`.
- `src/SwAgent/Mold/Structural/coefficients.csv` - provisional (`# provisional`), embedded as `Mold.coefficients.csv`
  (ItemGroup added to `SwAgent.csproj`). Track A2 replaces the file.
- `tools/builtin/design-compression-mold/data/{materials, mold-materials, shop-standards, sizing-defaults}.json` and
  `data/validation/examples.json`.
- `tools/dev/mold-data/` - generators (see its README). `--check` on each reproduces the shipped files.
- `tests/SwAgent.Tests/Mold/Mold{Data, Formula, Examples, Sizing, Record}Tests.cs` (+ `MoldTestData.cs`).

## 2. Public API (as in the contract, plus extras)

```
MoldData.Load(toolDir, overrideDir = null); MoldData.LoadDefault(); MoldData.DefaultToolDir; MoldData.DefaultOverrideDir
  .PartMaterials .PartMaterial(id) .FindPartMaterial(idOrNameOrShort, family) .MoldGrades .MoldGrade(idOrShortOrName)
  .DefaultGrade(family, use) .Fastener(size) .Fasteners(imperial) .Shop .Defaults .Raw(file) .ShopMm(path, fallback)
  .SaveDefault(overrideDir, table, id, values)   <- for mold_record save_default (checks keys against the shipped table)
CalcResult { Text, Json, RecordPatch (null for lookup and split), Checks }
MoldCalc.Run(action, design, query, overrides, data); MoldCalc.Version = "1.0.0"; MoldCalc.Fix(check) (the fix text)
MoldRecord.Init(folder, moldPartPath, inputs, toolVersion, addinVersion)  - throws InvalidOperationException if mold.json exists
MoldRecord.Open(folder) (FileNotFoundException when none), .Exists(folder), .Folder .Id .Json .FilePath
  .Summary(maxChars = 6000) .Section(dottedPath) .Merge(patch) -> status line .Validate() .Save() (atomic)
  MoldRecord.DeepMerge(target, patch) (static; the same rules, for designs held in memory)
SetupSheet.Write(record, templatePath, data) -> path; SetupSheet.Fill(record, template, data, out unresolved)
MachiningSheet.Write(record, templatePath, data) -> path (also writes hardware-bom.csv); MachiningSheet.Fill(...)
HardwareBom.Build(recordJson, data); HardwareBom.WriteCsv(record, data)
Coefficients.Get(bc, aspect, tOverB, nu, loadFrac) -> (Alpha, Beta); Coefficients.Version; .HeaderSha; .DataSha; .Provisional
Sizing.Size(design, data, cfg, asBuilt)  (MoldCalc "size"/"check" call it; usable as the Monte Carlo delegate)
```

Errors the agent can fix (press meaning "Not sure", missing geometry, unknown material, unknown action) are
`ArgumentException` with the fix in the message; Track D's `mold_calc` should return the message as the tool result.

## 3. For Track D (mold_calc, mold_record)

- `mold_calc` with `design` omitted: pass the record (`MoldRecord.Open(folder).Json`). With `record` true, merge
  `RecordPatch` (skip when null). `split` writes nothing: the agent merges the chosen option as `structural.split`
  (`{"structural": {"split": <options[i]>}}`) and runs `size` again.
- `mold_record init`: the `inputs` it passes are stored as given. The calculator reads each page input as a plain number
  in calculator units (mm, MPa, degC, kg, kN, cP, min, %, deg), or `{"value": x, "unit": "psi"}` (any of psi, bar,
  kgf/cm2, Pa, MPa, in, m, lb, g, lbf, N, US ton, t, degF, Pa*s, s, h), or `{"si": x}` meaning Track B's true SI (Pa,
  m, N, Pa·s, s, rad), converted by `Design.Quantities[id]`. Choices may be plain strings or `{"value": "..."}`.
  A grade/material can be the id, the full name, the short label, or the payload text "name [id; ...]".
- `mold_record save_default {table, id?, values}` -> `data.SaveDefault(MoldData.DefaultOverrideDir, table, id, values)`.
- `mold_record setup_sheet` -> `SetupSheet.Write(record, Path.Combine(toolDir, "data", "setup-sheet.html"), data)`.
  A machining sheet: `MachiningSheet.Write(record, ".../data/machining-sheet.html", data)`.
- What `mold_part` / `mold_bodies` should write for the calculator (record paths):
  - `part.volume_mm3`, `part.projectedArea_mm2`, `part.perimeter_mm`, `part.box_mm` [U, V, W] (unscaled molded part)
  - `part.depthBase_mm`, `part.depthLid_mm` (cavity depth each side of the parting plane; default: all in the base)
  - `part.floorRadius_mm` (smallest wall-to-floor radius; K_t), `part.maxSection_mm`, `part.plugHeight_mm`, `part.wellArea_mm2`
  - `part.fragile[] {id, kind ("core_pin" | "shut_off" | ...), d_mm, h_mm, shutOff (bool), guided (bool)}`
  - `part.drawing.tolerances[] {id, what, tol_mm (+-), closure (bool), nominal_mm}`
  - as built (for `check`): `structural.asBuilt {walls: {"V-B": w_mm, "U-B": w_mm, "V-L": ..., "U-L": ...},
    baseThickness_mm, lidThickness_mm, baseMass_kg, lidMass_kg, bearingArea_mm2}` - wall ids: V = walls facing +-V
    (they run along U), U = walls facing +-U; -B base half, -L lid half.
  - H3 pressure field: `structural.loadCases.LC1.pressureField` = array of `{bodyId, point, normal, p}` (MPa) or
    `{max_MPa}`; the calculator uses the peak when it exceeds the uniform p_LC1.
- `overrides` (one call): any sizing-defaults path (`"bolts.tighteningScatter": 0.1`) or a parameter: `support`
  (A-F), `rail_span_mm`, `span_mm`, `span_length_mm`, `overhang_mm`, `frame` ("integral"), `delta_allow_mm`, `kf`,
  `pattern` ("CxR"), `gap_mm` (wider steel between cavities, kept in the record as `layout.gapRequest_mm`).

## 4. For Track E (inputs.json, TOOL.md, templates, mold.schema.json)

Input ids the calculator reads (units = calculator units; the page's units are Track B's business):

| id | meaning |
|---|---|
| `press_pressure` (MPa), `press_pressure_is` | choices exactly "Gauge reading", "On the part", "Force reading (tons)", "Not sure - ask me" (prefix match) |
| `press_force` (kN) | the reading when `press_pressure_is` = "Force reading (tons)" (alias `press_force_reading`) |
| `press_rated_force` (kN), `press_rated_gauge` (MPa) | always shown (C2); optional `ram_bore` (mm) + `ram_count` |
| `press_temp` (degC), `platen_size` [L, W] mm, `daylight` mm, `stroke` mm (C6) | null = not set |
| `mold_handling` | "Slide in and out each cycle" (default) or "Stays in the press" (prefix "stays") - sets the 30 K / 10 K lid-to-base dT (D1) |
| `mold_support` | "Flat on platen" (default) or anything else -> rails (support B) |
| `cavities`, `cycle_life` | |
| `max_plate_mass`, `max_base_mass`, `max_lid_mass` (kg), `max_plate_size` [3] mm | null = unchecked |
| `lid_lift_at` ("At the slide-out table" / "Inside the press"; contains "press" -> press), `lid_reach` (mm) or `lid_reach_cm`, `lid_lifts_per_hour` | NIOSH lid limit (A2); missing -> 11 kg; an explicit `max_lid_mass` wins |
| `mold_material` ("Aluminium + steel inserts" / "Tool steel"), `mold_grade`, `insert_grade` | id or short label |
| `part_family`, `part_grade` | family: id, label or `familyShort`; grade: id, name or `short` |
| `shrink_pct`, `shrink_basis` | basis choices "Supplier datasheet (steel mold)", "Measured in my molds at this temperature", "Hot cavity to cold part", "v1 style, 1 + S" (A5) |
| `sg`, `viscosity_cp`, `mooney`, `pot_life_min`, `cure_min`, `draft_min_deg` | edited values win over the row |
| `molding_pressure` (MPa), `charge_excess_pct` (%) | C3; raise the class A design pressure when above the table maximum |

Data fields for the page: rows have `familyShort` / `short` (C5 labels), `isDefault`, `processLabel`, `processClass`,
`builtByV1`, `confidence`, `shrinkBasisChoice` (Fills `shrink_basis`), `moldingPressurePsi.typ|psi`,
`flashAllowancePct.typ`. Grades have `short`, `use` (plate/insert/backing/stop), `isDefault`, `swMaterial`.

`mold.schema.json` must accept what the calculator writes:
- `checks[].type` adds: `press`, `temperature`, `thread`, `buckling`, `lifting`, `process`, `scope`, `layout`
  (the list is `Check.Types`); `checks[].stage` is a string ("3"; "5" from `check`).
- `machiningSheet` (string), `structural.*` (inputs, loadCases, materials, results {walls, floors, plates, pieces,
  bearing, keys, pins, layout, stackHeight_mm, lid, partialLoading}, coefficients, gauge, split, asBuilt, checkedAsBuilt),
  `hardware.{bolts, dowels, dowelScheme, fits, stops, tcWells, lift, handlesRule, slideIn, washers}`,
  `process.{class, processType, processLabel, builtByV1, charge, overflowRequired_mm3, grooveRadius_mm, pour, cureMin,
  cureNote, postCure, loadingWell, moldType, gauge {recommended_MPa, max_MPa, maxBasis, basis}, press}`,
  `shrink.{pct, basis, basisLabel, alphaRef, alphaCavity, deltaT_K, scale, effectivePct, feature, pctAxis, about, steelSafe}`,
  `layout.{pattern, footprint_mm, cavities, moats, gap_mm, edge_mm, patternRequest, gapRequest_mm}`,
  `materials.part.{id, used, confidence, sources, rheology, kinetics, thermal, mechanical}` (F2: status "unknown").
- Arrays merged by id (each item needs a string id): checks, simulations, stages, decisions, bodies, airTraps, vents,
  layout.cavities, partingLine.planes, hardware.{bolts, dowels, fits, stops, tcWells}, part.{fragile, criticalFaces,
  drawing.tolerances}. `hardware.lift` has no ids (replaced as a whole).
- Simulations status: not_run | running | done | failed; hook H1 | H2 | H3. Stage status: pending | running |
  in_progress | done | failed | skipped. Record status: planning | sizing | building | verifying | done.

Template language (`data/setup-sheet.html`, `data/machining-sheet.html`): `{{path}}` (dotted, `list.0.x` ok,
HTML-escaped), `{{path|unit}}` dual units, document system first (units: mm, mm2, mm3, kg, g, MPa, kN, Nm, degC, min,
pct, or length/area/volume/mass/pressure/force/torque/temperature), `{{#each path}}...{{/each}}` (inside: item paths
first, then the root; `{{this}}`, `{{@index}}`, `{{@number}}`), `{{#if path}}...{{else}}...{{/if}}`. Missing paths
print "-" and are reported; the tests require none on a full record. The context is the record plus:
- `sheet` (setup sheet): `moldId, title, display, materialName, materialId, confidence, hazards, releaseAgent,
  processLabel, pieces[] {id, role, mass_kg, box_mm, lift}, press {setPointC, gaugeRecommended_MPa, gaugeMax_MPa,
  gaugeMaxBasis, conversion, closedHeight_mm, loadCentre}, partialLoading[] {active, gauge_MPa, force_kN, patterns, of,
  example, note}, neverFewerThan, charge {perCavity_g, shot_g, tolerancePct}, pour {secondsPerCavity, secondsTotal,
  limitSeconds, massRate_g_s, handlingSeconds}, cure {minutes, note, calc}, postCure, bolts[] {joint, count, size, spec,
  torqueDry_Nm, torqueLubed_Nm, preloadLow_kN, preloadHigh_kN, insert, washer, retorque}, dowelScheme, lid {mass_kg,
  limit_kg, liftingIndex, basis}, slideIn[] (lines), preheat, maintenance[] (lines), warnings[] {id, status, note},
  firstArticle[] (lines)`.
- `machining` (machining sheet): `bodies[] {id, role, material, finished_mm[3], stock_mm, blank_mm[3], finish,
  flatness[] {face, tol_mm, note}, holes[] {id, kind, qty, size, dia_mm, depth_mm, fit, limits, note}, notes[]},
  general[] (lines), bom[] {item, size, qty, note}`.

TOOL.md facts from the calculator worth stating: the v1 scope (class C and class B deeper than wide -> advisory, check
ids `SCOPE-CLASS-C` / `SCOPE-SEMI-POSITIVE`); `split` needs dead steel (with the minimum 1.5 in moat gap there is none:
the split text tells the agent to re-run size with `{"gap_mm": ...}`); a class A lid is never split.

## 5. For Track A2 (coefficients, Monte Carlo)

- `coefficients.csv` columns `bc, aspect, tau, nu, loadFrac, alpha, beta, wMaxX, wMaxY`; bc names `SS4`, `C4`,
  `Rails`, `Wall-C`, `Wall-S` (keep them; other sets may be added). Plates: aspect = long/short (Rails: along the rails /
  span), alpha = w E t³/(q b⁴), beta = 6 M/(q b²), b = short side; walls: aspect = L/h, reference h.
- Header comments read by the reader: a line starting `# provisional` (drop it), `# version: x`, `# sha256: <hex>` =
  SHA-256 of the ASCII data part: every non-comment line (header row `bc,...` included), each ending in `\n`. A test
  asserts the header hash equals the computed one.
- The reader clamps wall rows to the cantilever strip with shear (12(1-nu²)/8 + (1+nu)/kappa tau², beta 3) and uses the
  strip above the last L/h row (A4); plates above the last aspect use max(last row, one-way strip); alpha is extended
  linearly in tau² above the last tau. nu: nearest tabulated; loadFrac: interpolated, clamped.
- Monte Carlo delegate: `design => MoldCalc.Run("size", design, null, overrides, data).Json` (or `Sizing.Size(...)`).
  Wall results are in `structural.results.walls[]` of the patch (`w_struct_mm`, `w_drawn_mm`, `sigma_MPa`, `allow_MPa`).

## 6. For the lead (integration)

- After A2 replaces `coefficients.csv`, run `python -I tools/dev/mold-data/examples_check.py` to refresh
  `examples.json` (it records the coefficient version). The C# example tests allow 2% on table-dependent wall values,
  so they should pass either way; E3 uses 2% for the walls.
- The data tests run the generators' `--check` when `python` is on PATH (silently skipped otherwise).
- Mold-schema validation of sample records with JsonSchema.Net was not added here (the schema is Track E's file);
  `MoldRecord.Validate` is the hand-written equivalent and is tested.

## 7. Decisions and deviations (for review)

1. **Gap budget (A11)**: land flatness defaults leave about half the flash gap for deflection (design-rules 13.2.1):
   0.006 / 0.010 / 0.012 mm for liquids < 1,000 / 1,000-10,000 / > 10,000 cP, 0.0025 LSR, 0.030 rubber, 0.012 others;
   platen parallelism 0 (enter it if measured). Walls use 0.25 x the drawing's tightest width tolerance, +-0.125 mm
   (design-rules 19) when the drawing is silent.
2. **never_above (A8)**: a hard block only for grades flagged `agesDimensionally` (2024). Above `derated` (limited) and
   above `never_above` (beyond-limit) the gate is stress-aware: pass when every governing stress <= 0.5 x the derated
   allowable; "limited" otherwise needs N <= 10,000 and the user's confirmation (warn); beyond-limit otherwise fails.
   T > 250 C is refused for every grade.
3. **Bolts (A1)**: E_bolt 205 GPa (E4 now has C = 0.311 instead of 0.306); hot proof factor 0.95/0.92/0.89/0.86 at
   100/150/200/250 C; the chosen preload is the highest fraction (<= 0.60 Al / 0.75 steel) passing S47-S51 with scatter,
   rounded down to 0.005. Head bearing uses the hardened washer with a 45 deg spread. Smallest screw 6 mm (1/4 in, M6).
   Joint screws come from below through counterbores in the subplate (nothing proud underneath, E4).
4. **Threads**: S54 and the conservative S56 are applied together at the joint's maximum bolt load; when S56 needs more
   than 2.5 d, a helical insert is chosen with its length checked at the 1.2 d shear diameter (E8: 2.5 d helical, the
   2 d coil is not enough at 150 C).
5. **A9**: class B force area adds the overflow groove; class A excludes the moat (open to the runners, no pressure).
   Areas and perimeters are the scaled cavity's (the design used the unscaled part).
6. **Layout**: the smallest footprint wins unless `layout.patternRequest` / `pattern` is given (E2 requests the
   user's 2 x 1; left alone it picks 1 x 2). Class A separate-moat gap 1.5 in; edge steel from the corner dowel clearing
   the moat's R0.25 corner (1.640 in for the shop standard).
7. **S63** is new: the A12 shut-off pin axial/Euler check (the design skipped the number).
8. **Pour (D3)**: 10 g/s plus 15 s handling per cavity (shop-standards `pour`).
9. **NIOSH**: frequency multipliers from Waters 1993 Table 5 (V >= 75 cm columns, 8 h by default).
10. **Re-derived examples**: E2 (A4, A9, A11, scaled areas), E3 (L/h = 6, the structural.md geometry), E4/E5 (A1),
    E8 (insert length), E11 (by hand). Numbers are in `examples.json` with the original figures under `hand`.

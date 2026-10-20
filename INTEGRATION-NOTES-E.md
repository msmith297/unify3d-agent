# Integration notes - Track E (tool folder `tools/builtin/design-compression-mold/`)

Track E owns: `TOOL.md`, `inputs.json`, `macro.cs`, `icon.png`, `data/setup-sheet.html`, `data/machining-sheet.html`, `data/mold.schema.json`, `data/README.md`, and `data/examples/mold.sample.json` (a complete sample record). Nothing outside the tool folder was changed except this file.

What was checked here:
- `inputs.json` (44 inputs, 7 groups + the Unify3D Agent group) and the TOOL.md front matter parse with today's `ToolSpec.LoadInputs` / `UserTools.EffectiveFrontMatter` (reflection check against `bin\Debug\net48\builtin-tools`).
- Every `ChoicesFrom`, `ChoiceKey`, `RowFilter` field, `Fills` path (a null parent reads as n/a) and info-`Text` row path resolves against Track A1's data as it stood in its worktree on 2026-10-19 (`familyShort`, `short`, `shrinkBasisChoice`, `processLabel`, `confidence`, `max_service_C.*`). The `shrinkBasisChoice` values are all among `shrink_basis`'s choices.
- `macro.cs` compiles with the add-in's script options (the `Saved_tool_scripts_compile` test pointed at a copy of the folder with `SWAGENT_TOOLS_ROOT`; a planted error is caught).
- `data/examples/mold.sample.json` validates against `data/mold.schema.json` (Python `jsonschema`, draft 2020-12), and a reference renderer fills **every** placeholder of both templates from it with no unknown unit filter and no leftover braces.
- Full test run: 55 passed, 0 failed. Debug build clean.

## For Track A1 (calculator, record, sheet fillers)

1. **Template syntax** (also in `data/README.md`; please implement exactly this in `SetupSheet` / `MachiningSheet`):
   - `{{a.b.c}}`: dotted path; a numeric segment indexes an array (`{{box_mm.0}}`); HTML-escape the value.
   - `{{this}}`: the current item inside a block (used for lists of strings: `setup.demold`, `report.openItems` ...).
   - `{{#each a.b}}...{{/each}}`: blocks nest (the machining sheet nests `machining.holes` inside `bodies`). Lookup order inside a block: the current item, then each enclosing item, then the record root (the machining sheet reads `{{mass_kg|kg}}` and `{{step}}` from the body, and the setup sheet reads root values inside no block).
   - Missing or null value: print `-` and add the path to an "unresolved" list that `Write` returns or logs, so `mold_record setup_sheet` / `machining_sheet` can report it (TOOL.md Phase 6 step 5 relies on that report). A missing array in `#each` prints nothing and is not "unresolved".
   - `{{path|unit}}`: `unit` is the unit the value is **stored** in: `mm, mm2, mm3, MPa, kN, kg, g, degC, K, Nm, min, s, cP, deg`. Print the document system first (`units.display`), SI in brackets; for a metric document print SI only. Imperial targets: in, in², in³, psi, US ton (lbf below 1 ton), lb (oz below 1 lb for `g`), degF (K as a degF difference), lbf·ft.
   - Precision: fits and flatness are tolerances, so a length under 1 mm prints with 4 decimals in inches and 3 in mm (0.0006 in (0.015 mm)), and fit limits (`machining.fits[].upper_mm/lower_mm/nominal_mm`) always do; other lengths use 3 decimals in inches, 2 in mm. (The reference check showed 0.015 mm as "0.001 in" at 3 decimals, which a machinist would misread.)
   - `{{path|gauge}}`: a pressure in MPa printed in `process.gauge.unitShown` (psi, bar, MPa or kgf/cm2: the unit the user picked for `press_pressure` on the page), with MPa in brackets.
   - Comments in the templates contain no placeholders (the filler may replace inside comments).
2. **Keys the templates read** (beyond design 4.3). Please let `MoldRecord.Validate` accept them and, where `mold_calc` computes them, write them:
   - `identity.{moldNumber, revision, partNumber, partName}`; `scope` ("build" | "advisory": an advisory job still ends with `status: "done"`, so the status enum is unchanged).
   - `process.pressTempC`, `process.cureNote` (E6, class-specific text), `process.closedHeight_mm`, `process.loadCentre` (text), `process.minActiveCavities`, `process.partialLoading[] {id, active, pattern, gauge_MPa, pressureOnPart_MPa, allowed, note}` (E3), `process.press.{ramArea_mm2, rated_kN, ratedGauge_MPa, force_kN, required_kN, platen_mm, daylight_mm, stroke_mm}`, `process.gauge.{recommended_MPa, max_MPa, maxBasis, basis, conversion, unitShown}`, `process.pour.{side, tiltDeg, time_s, rate_g_s, handling_s, points[]}`, `process.charge.{perCavity_g, shot_g, tolerancePct, preform}`, `process.postCure`.
   - `handling.lid.{mass_kg, limit_kg, limitSource, rwl_kg, liftingIndex, where, reach_mm, liftsPerHour, method}` and `handling.slideIn[]`.
   - `hardware.bolts[]` with `preloadMin_kN`, `preloadMax_kN` (the scatter band, A1 fix 6), `length`, `cbore`; `hardware.bom[] {id, item, spec, size, length, qty, partNumber, use, note}` (E2).
   - `bodies[].{materialName, lift, machining}`; `machining` = `{stock{grade, finished_mm[], blank_mm[], stockThickness, allowance}, setups, setupsText, minInsideRadius_mm, maxDepth_mm, holes[], fits[{nominal_mm, upper_mm, lower_mm, fit}], tolerances[{what, value_mm, datum}], finish[{faces, spec, ra_um}], reliefs[{pocket, kind, radius_mm, cutter_mm}], notes[]}` (E1). TOOL.md has the agent write these in Phase 6; if `MachiningSheet` prefers to derive some itself, keep the same keys.
   - `setup.{assembly[], preheat, material[], pourSequence[], demold[], release, cleaning, firstArticle[], safety[], maintenance[]}`, `views.{assembly, cavityMap}` (relative image paths), `report.{lowestMargins[], openItems[], summary}`, `structural.inputs.tier`, `tool.calc`, `machiningSheet`.
   - Reserved (F2): `materials.part.{rheology, kinetics, thermal, mechanical}` = `{status: "unknown", source: null}` at init. Portable locations (F1): `checks[].location` and `simulations[].findings[].location` = `{bodyId, point, normal, stepFaceId?}`. Pressure field (F3): `structural.loadCases.LC1.pressureField` = null | `{kind: "uvwp", file?, samples?[[u,v,w,p]]}` | `{kind: "faceSamples", file?, samples?[{bodyId, point, normal, p_MPa}]}`.
   - `data/examples/mold.sample.json` is a ready fixture for `SetupSheetTests` ("every placeholder resolved") and `RecordTests` (schema validation).
3. **Page input ids `mold_calc` must read** (from `record.inputs`, which `init` copies from the page):
   - The press value is split by meaning: `press_pressure` (gauge reading; also present for "Not sure - ask me"), `part_pressure` (On the part), `press_force_reading` (tonnage, "Force reading (tons)"). Only the one matching `press_pressure_is` is in the payload (ShowWhen). `press_rated_force` and `press_rated_gauge` are always present (C2).
   - `shrink_basis` carries the label: "Supplier datasheet (steel mold)" -> `cold_mold_cold_part_steel_ref`, "Measured in my molds at this temperature" -> `measured_in_my_molds`, "Hot cavity to cold part" -> `hot_cavity_cold_part`, "v1 style (1 + S)" -> `v1_style`. The page fills it from `shrinkBasisChoice`.
   - `max_lid_mass`: "not set" (Optional unchecked, the default) means the NIOSH limit from `lid_lift_where` ("At the slide-out table" -> `lifting.locations.table`, "Inside the press" -> `press`, "Not sure" -> the 11 kg fallback), `lid_reach` (mm, used as H in place of the location's H_cm when given) and `lifts_per_hour` (FM, 8 h column). Checked means the user's own limit.
   - `mold_handling`: "Slides in and out each cycle" -> `lidBaseGradientSlideIn_K` (30 K) and slide-in hardware; "Stays in the press" -> 10 K. `mold_support`: "Flat on the platen" -> support case A; "On a carrier or rails" -> ask for the span.
   - `stroke` (optional, mm), `molding_pressure` (MPa, filled from `moldingPressurePsi.typ|psi`), `charge_excess_pct` (filled from `flashAllowancePct.typ`).

## For Track B (page)

- `inputs.json` uses: `UnitChoices` `["psi","bar","MPa","kgf/cm2"]` and `["US ton","t","kN"]`; `Fills` into a **choice** target (`shrink_basis` <- `shrinkBasisChoice`) and with a unit suffix (`moldingPressurePsi.typ|psi`); `ShowWhen` on three alternative pressure fields; `Optional` + `Enabled: false` on `max_lid_mass`, `platen_size`, `daylight`, `stroke`, `max_base_mass`; `GroupExpanded: false` on the first input of "Process data (from table)" and of "Decisions & notes"; `Accept: ["drawing"]`; info `Text` tokens `{id|percent}`, `{id|time}`, `{id|temperature}`, `{id}`, `{choice.path}`, `{choice.path|temperature}`.
- Please carry the user's picked unit of a `UnitChoices` field into the payload (for example `unit: "bar"` next to `display`), so `mold_record init` can record it and the setup sheet can print the gauge in the gauge's own unit (`process.gauge.unitShown`).
- `press_temp` deliberately stays in the Press group (visible) and is not remembered: it is the platen set point, filled from the grade; the critique's "all filled fields in the collapsed group" is applied to the material data only.

## For Track C (categories, long runs, panel)

- Front matter: `category: Molding & Toolmaking`, `ribbon_title: Compression Mold`, `long_run: true`, `max_steps: 600`, `requires: mold_calc, mold_part, mold_bodies, mold_record`.
- G4: TOOL.md has the agent merge `status: "done"` as the **last** record write of Phase 6 (and for advisory jobs). If the sticky-settings rule reads the record status, that is the end marker; the final chat report follows it.
- TOOL.md uses `ask_selection` with `prompt`, `types: ["face"]`, `min`, `max`, and states that its reporting rules replace the one-line finish (the systemPrompt sentence from design 2.2).

## For Track D (tools and helpers)

TOOL.md codes against these; please match them or tell the lead:
- `mold_record` actions: `init`, `read` (`section`), `merge` (`data`), `setup_sheet`, **`machining_sheet`** (calls A1's `MachiningSheet.Write` with `data/machining-sheet.html`), `save_default` (`table`, `id`, `values`), `path`.
  - `setup_sheet` and `machining_sheet` should return the written path **and the unresolved placeholders** (see A1 item 1).
  - `init` on a folder that already holds a record (TOOL.md "Start over") should keep the old file as `mold.json.bak-yyyyMMdd-HHmmss` and start a new record id.
- `mold_part`: `action` "analyze", `body` ("auto"), `pull`, `fill` ("base" | "lid" | "none"), `tilt_deg`, `min_draft_deg`, `sample_mm`, `critical`, `show`, `record`. **The gathering macro calls it** with `{action:"analyze", body:"auto", record:false, show:false}` through `Agent.CallToolAsync` before any record exists: it must not need a record, must not wait for the UI, and should keep its text under about 4,000 characters.
- `mold_bodies`: `report`, `clearance` (`pairs`, `min_clearance_mm`), `export`. TOOL.md runs `export` right after stage 4.1 (hook H1), when only PART-REF and the cavity copies exist: it should then write `exports\part\*.step` without failing on the missing plates.
- `Mold` helper methods named in TOOL.md: `Frame, InsertPart, Orient, ScaleForShrink (about MOLD CS), CopyBody, Plate, Subtract, SplitByPlane, Folder, SetBodyMaterial, Bodies, FlashGrooves, CboreHoles, TappedHoles, Analyze`. TOOL.md tells the agent to list the real signatures by reflection when a call doesn't match, so small differences are tolerated.
- `checkpoint save` names used: `intake`, `stage-4.1` ... `stage-4.9`; TOOL.md records the exact stamped name the tool returns in `stages[].checkpoint`.

## For the lead (integration)

- BuiltInToolsTests for this tool: 44 inputs; kind tool, confirm always, long_run, max_steps 600, category; every ChoicesFrom resolves; every Fills path exists (a null parent such as `viscosityCp: null` is allowed); every Fills value aimed at a choice is one of its Choices; every `requires` tool registered.
- Suggest extending `ScriptingTests.Saved_tool_scripts_compile` to also compile `builtin-tools\*\macro.cs`, so `macro.cs` is covered in CI.
- `icon.png` is 64 x 64 as the contract says (Smart Rebuild's is 128 x 128). The generator below makes either size: `powershell -File icon.ps1 -Out icon.png -Size 128`.
- Shipped extras: `data/README.md` and `data/examples/mold.sample.json` are copied with the tool (small; the sample is the agent's reference for record shapes).

### Icon generator (`icon.ps1`, PowerShell 5.1 + System.Drawing)

```powershell
# Draws tools/builtin/design-compression-mold/icon.png: an open two-plate compression mold in a light oblique view -
# the lid lifted above the base, the base's parting face with a cavity pocket, two dowel pins between - in the Unify3D
# brushed-steel/graphite style of src/SwAgent/Icons.cs (same colours, gradients and dark edge).
# Drawn on a 128 px canvas (the Icons.cs coordinate system) and scaled down to the output size.
param([string]$Out, [int]$Size = 64)
Add-Type -AssemblyName System.Drawing
$SteelTop = [System.Drawing.Color]::FromArgb(0xF4, 0xF5, 0xF6)
$SteelBot = [System.Drawing.Color]::FromArgb(0x9A, 0xA0, 0xA6)
$GraphiteTop = [System.Drawing.Color]::FromArgb(0x55, 0x5A, 0x60)
$GraphiteBot = [System.Drawing.Color]::FromArgb(0x1C, 0x1F, 0x22)
$EdgeColor = [System.Drawing.Color]::FromArgb(0x26, 0x29, 0x2D)

function Pt([float]$x, [float]$y) { New-Object System.Drawing.PointF($x, $y) }
function Poly($pts) { $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $p.AddPolygon([System.Drawing.PointF[]]$pts); return $p }
function Fill($g, $path, $top, $bot, [float]$width = 4, $mode = [System.Drawing.Drawing2D.LinearGradientMode]::Vertical) {
    $b = $path.GetBounds()
    $rect = New-Object System.Drawing.RectangleF(($b.X - 1), ($b.Y - 1), ($b.Width + 2), ($b.Height + 2))
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bot, $mode)
    $g.FillPath($brush, $path); $brush.Dispose()
    if ($width -gt 0) {
        $pen = New-Object System.Drawing.Pen($EdgeColor, $width); $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($pen, $path); $pen.Dispose()
    }
}
$SideTop = [System.Drawing.Color]::FromArgb(0xB4, 0xBA, 0xC0)
$SideBot = [System.Drawing.Color]::FromArgb(0x6E, 0x75, 0x7C)

# One plate as an oblique box: front face at (x0..x1, y..y+h), depth offset (dx, -dy).
function Plate($g, [float]$x0, [float]$x1, [float]$y, [float]$h, [float]$dx, [float]$dy) {
    $top = Poly @((Pt $x0 $y), (Pt $x1 $y), (Pt ($x1 + $dx) ($y - $dy)), (Pt ($x0 + $dx) ($y - $dy)))
    $front = Poly @((Pt $x0 $y), (Pt $x1 $y), (Pt $x1 ($y + $h)), (Pt $x0 ($y + $h)))
    $side = Poly @((Pt $x1 $y), (Pt ($x1 + $dx) ($y - $dy)), (Pt ($x1 + $dx) ($y - $dy + $h)), (Pt $x1 ($y + $h)))
    Fill $g $front $SteelTop $SteelBot 4.5
    Fill $g $side $SideTop $SideBot 4.5
    Fill $g $top ([System.Drawing.Color]::White) $SteelTop 4.5
    $top.Dispose(); $front.Dispose(); $side.Dispose()
}

$big = New-Object System.Drawing.Bitmap(128, 128, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($big)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

$dx = 26; $dy = 20
# Base plate with the parting face on top.
Plate $g 6 92 82 34 $dx $dy
# Cavity pocket in the parting face: an oblique rounded opening with its far wall in shade.
$cav = Poly @((Pt 30 79), (Pt 70 79), (Pt 88 65), (Pt 48 65))
Fill $g $cav $GraphiteTop $GraphiteBot 3.5
$cav.Dispose()
$wall = Poly @((Pt 48 65), (Pt 88 65), (Pt 84 70), (Pt 46 70))
$wb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x0F, 0x11, 0x13)); $g.FillPath($wb, $wall); $wb.Dispose(); $wall.Dispose()

# Dowel pins below the lid's far corners, standing down toward the base.
foreach ($x in 16, 104) {
    $pin = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pin.AddRectangle((New-Object System.Drawing.RectangleF($x, 36, 9, 22)))
    Fill $g $pin $GraphiteTop $GraphiteBot 3 ([System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    $pin.Dispose()
}
# Lid, lifted off the base.
Plate $g 6 92 30 14 $dx $dy

$g.Dispose()
$small = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$s = [System.Drawing.Graphics]::FromImage($small)
$s.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$s.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$s.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$s.Clear([System.Drawing.Color]::Transparent)
$s.DrawImage($big, 0, 0, $Size, $Size)
$s.Dispose()
$small.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$small.Dispose(); $big.Dispose()
Write-Output "wrote $Out ($Size x $Size)"
```

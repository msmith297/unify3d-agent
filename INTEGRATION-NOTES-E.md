# Integration notes - Track E (tool folder `tools/builtin/design-compression-mold/`)

Track E owns `TOOL.md`, `inputs.json`, `macro.cs`, `icon.png`, `data/setup-sheet.html`, `data/machining-sheet.html`, `data/mold.schema.json`, `data/README.md` and `data/examples/mold.sample.json`. Nothing outside the tool folder was changed except this file.

## Where the tracks now meet (second pass, after review)

The first pass wrote templates and a schema against keys and a filter set of its own, which had drifted from what Track A1 actually writes. This pass makes **A1's code the source of truth**:

- The templates read A1's derived view models (`sheet.*` from `SetupSheet.Build`, `machining.*` from `MachiningSheet.Build`, which includes `HardwareBom.Build`) and the record keys A1's `mold_calc` writes. They use only the filters A1's `Template.Format` implements (`mm, mm2, mm3, kg, g, MPa, kN, Nm, degC, min, pct`), `[n]` array indexes (A1 resolves paths with `SelectToken`, which does not take `.0`), `this.` for item keys, and `{{#if}}` for class-specific parts.
- The agent writes only what code can't know: `identity`, `scope`, `files.moldedPart`, `frame`, `part.drawing`, `part.criticalFaces`, `partingLine`, `airTraps`, `vents`, `process.pour` (side, tilt, points), `stages`, `decisions`, `bodies[].machining.notes`, `hardware.bom` (purchased items the calculator doesn't size), `setup` (assembly, material, pourSequence, demold, release, cleaning, safety), `views`, `report`, `simulations`. TOOL.md Phase 6 was rewritten to say exactly that; the old hand-written machining data, hardware BOM and `handling.*` keys are gone.
- `mold.schema.json` now describes what A1, D and the agent really write (checked against real calculator output, below). `data/examples/mold.sample.json` **is** A1's calculator output for worked example E2 plus the agent's keys and a D-style `bodies[]` with PART-REF and a Garbage body, so its numbers are self-consistent (gap budget 0.02 - 0.01 = 0.01 mm, dowel length, shrink-scaled volumes).
- Page ids: the lifting answers were renamed to A1's ids, `lid_lift_at` and `lid_lifts_per_hour` (`lid_reach` unchanged). The press value stays split in three fields by meaning (`press_pressure`, `part_pressure`, `press_force_reading`), because the gauge field and the on-the-part field need different limits (C1: 100 MPa and 150 MPa) and B's page leaves hidden fields out of the payload. A1 must read the matching field (A1 item 1).

## What was run here

- **Cross-track render harness** (scratchpad, not shipped; the lead's test version is below): compiles A1's current `src/SwAgent/Mold/**` (wf-1 working tree, 2026-10-19 evening), runs `MoldCalc` `process`, `layout`, `press`, `size` on E2 (and on a 6-cavity job with a split base, subplate and bolts), merges each `RecordPatch` and the checks like the agent does, merges the agent's keys, and renders E's two templates through `SetupSheet.Fill` and `MachiningSheet.Fill`.
  - `MoldRecord.Validate`: valid for both jobs.
  - Unknown filters in the templates: none.
  - Setup sheet: **0 unresolved** for both jobs.
  - Machining sheet: 18 unresolved, all `this.size` on dowel and thermocouple hole rows that A1's `MachiningSheet.Build` creates without a `size` key (A1 item 4b). No other gap.
- **Schema**: both harness records and the sample validate (Python `jsonschema`, draft 2020-12, 0 errors). Mutation cases: Garbage and unfiled bodies with null mass pass; an unknown role fails; a location without `normal` fails; a face pressure sample without `normal` fails; `in_progress` stages pass; null `summary`, `partNumber` and `max_MPa` pass; 11 of 11 as expected.
- **Page data ranges**: every `Fills` value of every row of A1's `materials.json` (882 values, `|psi` converted) lies inside its target field's Min/Max, and every `shrinkBasisChoice` is one of `shrink_basis`'s choices. The widened ranges: `sg` Min 0.01 (molded flexible foam is 0.05), `mooney` Max 500 (HCR silicone 250, data max 400), `pot_life_min` Max 50000 min (LSR 4320, PVC plastisol 10080, data max 43200), `cure_min` Max 5000 min (data max 2880).
- **Track B's checker**: `ToolSpec.CheckInputs` from B's build (wf-3) on this folder with A1's data: 44 inputs, 0 warnings.
- Today's loader (`ToolSpec.LoadInputs`, `UserTools.EffectiveFrontMatter`): 44 inputs, front matter as the contract asks. `macro.cs` compiles with the add-in's script options (`Saved_tool_scripts_compile` pointed at the folder with `SWAGENT_TOOLS_ROOT`).
- Debug MSBuild clean; `dotnet test tests/SwAgent.Tests`: 55 passed, 0 failed.

## For Track A1 (calculator, record, sheet fillers)

Each item names the file and the change; the lead's tests below check them.

1. **Press value by meaning** (`Design.cs`, the `d.PressValue` line). The payload has only the field that matches `press_pressure_is`:
   ```csharp
   d.PressValue = (d.Meaning == PressMeaning.ForceReading ? N("press_force_reading", "press_force")
                 : d.Meaning == PressMeaning.OnPart ? N("part_pressure", "press_pressure")
                 : N("press_pressure")) ?? 0;
   if (d.PressValue <= 0 && d.Meaning != PressMeaning.Unknown)
       throw new ArgumentException($"No press value for \"{S("press_pressure_is")}\": merge inputs." +
           (d.Meaning == PressMeaning.ForceReading ? "press_force_reading" : d.Meaning == PressMeaning.OnPart ? "part_pressure" : "press_pressure") + " (SI units).");
   ```
   (Today "On the part" gives PressValue 0, so class B is sized for zero pressure.) If the throw breaks `process`, which doesn't need the press, put it in `RequirePress()` instead.
2. **Lid limit** (`Process/Lifting.cs`, `LidLimit`):
   - Inside the press, H = max(reach, `lifting.locations.press.H_cm`, default 55 cm). Today the default 400 mm reach gives 10.2 kg instead of 7.4 kg (the harness's 6-cavity job shows `limit_kg` 10.17).
   - "Not sure" (or any `liftAt` that names neither place) gives the 11 kg fallback, not the table location.
   - Until this lands, TOOL.md Phase 0 step 5 makes the agent merge `inputs.lid_reach = 550` for the press and `inputs.max_lid_mass = 11` for "Not sure", so the result is already right in a real run.
3. **Template language** (`Record/Template.cs`), to match `data/README.md`:
   - a)`{{#if}}` over a missing path is false and is **not** added to `unresolved` (today it is; the setup sheet uses `{{#if sheet.pour.secondsTotal}}`, which is missing for class B).
   - b) `this.x` resolves on the current item only (today, after the item, it still falls back to the root).
   - c) An unknown filter is a template error or an unresolved entry, never `value + " " + filter`.
   - d) Fix the doc comment: array indexes are `[n]` (`bodies[0].id`), since `SelectToken("bodies.0.id")` returns null.
   - e) Optional: a `gauge` filter that prints MPa in the unit the user's gauge reads (`process.gauge.unitShown`, from Track B's picked unit), MPa in brackets. The templates use `|MPa` (psi and MPa) until then; switch the four gauge placeholders in `setup-sheet.html` (recommended, maximum, the partial-loading gauge) when it exists.
4. **Sheet view models** (`Record/SetupSheet.cs`, `Record/MachiningSheet.cs`):
   - a) **Machined bodies only.** `SetupSheet.Build` (`pieces`) and `MachiningSheet.Build` (`bodies`) iterate every `bodies[]` entry, so PART-REF, Garbage and unfiled bodies get a weight row and a full machining section with dowel holes (seen in the harness). Skip roles `reference`, `garbage` and `unfiled` (Track D's `BodyRoles` names).
   - b) Every hole row gets the same keys: add `["size"] = null` (or the pin size) to the dowel and thermocouple rows, like the bolt rows have.
   - c) Append the record body's agent notes: `notes` += each string of `bodies[id].machining.notes` (setups, corner reliefs with the cutter, shut-off heights, finish per face group, engraving).
   - d) `HardwareBom.Build`: append the record's `hardware.bom[]` items (`{item, size, qty, partNumber, note}`; bushings, jack screws, poppets, shims) after the derived rows, in the sheet and in `hardware-bom.csv`.
   - e) A body's `material` falls back to the plate grade for every role; inserts and stops should use `structural.materials.inserts.id`.
   - f) Dowel rows in the base say "dowel slot (radial)" with a press fit: the slot is in the lid; in the base every pin is pressed.
   - g) `sheet.title` reads `inputs.mold_number`, which no page input has: use `identity.moldNumber` (the template prints `identity.*` directly, so this only matters if `title` is used later).
5. **Unresolved list out.** `SetupSheet.Write` / `MachiningSheet.Write` drop the `unresolved` list (`out _`). Return it (for example a `Write(..., out List<string> unresolved)` overload) so `mold_record` can report it (Track D item 1). TOOL.md has a cell-based fallback meanwhile.
6. **Record**: nothing to change for the templates. `MoldRecord.Validate` already accepts everything the harness wrote; the schema now matches A1's key names (`tool.calculator`, `shrink.basisLabel`, `structural.results.lid`, `structural.results.partialLoading.rows`, `hardware.bolts[].preloadBand_kN`, `hardware.slideIn` object ...).

## For Track D (tools and helpers)

1. `mold_record setup_sheet` / `machining_sheet`: return the unresolved placeholders with the path (A1 item 5). TOOL.md Phase 6 step 6 relies on that list, with a cell fallback.
2. `mold_record init` on a folder that has a record **continues** it (current D code). TOOL.md "Start over" now renames `mold.json` to `mold.json.bak-<stamp>` in a cell first, so no change is needed; a built-in backup would still be welcome.
3. `bodies[]` from `mold_bodies report` matches the schema as written (roles incl. `garbage`/`unfiled`, `mass_kg` and `swMaterial` nullable, `folder` nullable).
4. TOOL.md now names the `Mold` methods as D's `MoldApi` has them (meters; `ScaleForShrink(body, su, sv, sw, plW)` creating `MOLD CS`; `CboreHoles` / `TappedHoles` as cuts with their parameters; `PocketCornerReliefs`; `Body`, `PartBody`, `Box`, `Volume`, `RecordFolder`; `InsertPart(path, config, expectedVolumeMm3)` doing the B8 validation). It keeps the reflection hint for mismatches.
5. Unchanged from the first pass: the macro calls `mold_part {action:"analyze", body:"auto", record:false, show:false}` before any record exists; `mold_bodies export` runs right after stage 4.1 (H1) with only PART-REF and cavity copies; checkpoint names `intake`, `stage-4.1` ... `stage-4.9`.
6. TOOL.md rule 6 now states that tools act on the active document and `Model` is fixed per cell, and has the agent check `Unify3D.MoldId` at every phase and stage start.

## For Track B (page)

- `inputs.json` passes B's `CheckInputs` with 0 warnings. It uses `UnitChoices` (`["psi","bar","MPa","kgf/cm2"]`, `["US ton","t","kN"]`), `Fills` into a **choice** target (`shrink_basis` <- `shrinkBasisChoice`) and with a unit suffix (`moldingPressurePsi.typ|psi`), `ShowWhen` on the three pressure fields, `Optional` + `Enabled: false`, `GroupExpanded: false`, `Accept: ["drawing"]`, and info `Text` tokens.
- Still wanted: carry the user's picked unit of a `UnitChoices` field into the payload (for example `unit: "bar"`), so A1 can fill `process.gauge.unitShown` (A1 item 3e).
- `press_temp` deliberately stays visible in the Press group and is not remembered: it is the platen set point the operator sets every run, filled from the grade. Only the material data sits in the collapsed "Process data (from table)" group. Please add it to the page's manual confirm list (design 6.3) as a known deviation from C3.

## For Track C (categories, long runs, panel)

- Front matter: `category: Molding & Toolmaking`, `ribbon_title: Compression Mold`, `long_run: true`, `max_steps: 600`, `requires: mold_calc, mold_part, mold_bodies, mold_record`.
- G4: TOOL.md merges `status: "done"` as the **last** record write of Phase 6 (advisory jobs too); the chat report follows it.
- `ask_selection` with `prompt`, `types: ["face"]`, `min`, `max`.

## For the lead (integration)

- `icon.png` is now 128 x 128 (like Smart Rebuild's), so high-DPI ribbon strips (up to 128 px) scale it down instead of up. This amends the contract's 64 x 64. The generator is below.
- Suggested tests, ready to drop in `tests/SwAgent.Tests/Mold/MoldToolFolderTests.cs` after A1 is merged. The render and lifting tests fail until A1 items 2, 3a and 4a-b land, which is the point:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>Track E's tool folder against Track A1's data and fillers: page ranges, templates, page ids.</summary>
    public class MoldToolFolderTests
    {
        private static string ToolDir => MoldData.DefaultToolDir;
        private static JArray Inputs => JArray.Parse(File.ReadAllText(Path.Combine(ToolDir, "inputs.json")));

        [Fact]
        public void Every_filled_value_fits_its_field()
        {
            var byId = Inputs.OfType<JObject>().ToDictionary(i => (string)i["Id"]);
            var bad = new List<string>();
            foreach (var input in byId.Values.Where(i => i["Fills"] != null))
            {
                var spec = ((string)input["ChoicesFrom"]).Split('#');
                var rows = JObject.Parse(File.ReadAllText(Path.Combine(ToolDir, spec[0])))[spec[1]].OfType<JObject>();
                foreach (var fill in ((JObject)input["Fills"]).Properties())
                {
                    var target = byId[fill.Name];
                    var parts = ((string)fill.Value).Split('|');
                    foreach (var row in rows)
                    {
                        var v = row.SelectToken(parts[0]);
                        if (v == null || v.Type == JTokenType.Null) continue;
                        if ((string)target["Type"] == "choice") { if (!target["Choices"].Any(c => (string)c == (string)v)) bad.Add($"{row["id"]}: {fill.Name} = {v}"); continue; }
                        var x = (double)v * (parts.Length > 1 && parts[1] == "psi" ? Si.PsiToMPa : 1);
                        if (target["Min"] != null && x < (double)target["Min"] || target["Max"] != null && x > (double)target["Max"]) bad.Add($"{row["id"]}: {fill.Name} = {x}");
                    }
                }
            }
            Assert.Empty(bad);
        }

        private static MoldRecord Sample()
        {
            var folder = Path.Combine(Path.GetTempPath(), "mold-sample-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(ToolDir, "data", "examples", "mold.sample.json"), Path.Combine(folder, MoldRecord.FileName));
            return MoldRecord.Open(folder);
        }

        [Fact]
        public void Both_templates_fill_from_the_sample_record_with_nothing_unresolved_and_machined_bodies_only()
        {
            var rec = Sample();
            Assert.Empty(rec.Validate());
            var setup = SetupSheet.Fill(rec, File.ReadAllText(Path.Combine(ToolDir, "data", "setup-sheet.html")), Data, out var u1);
            var mach = MachiningSheet.Fill(rec, File.ReadAllText(Path.Combine(ToolDir, "data", "machining-sheet.html")), Data, out var u2);
            Assert.Empty(u1);
            Assert.Empty(u2);
            foreach (var html in new[] { setup, mach })
            {
                Assert.DoesNotContain("{{", html);
                Assert.DoesNotContain("PART-REF", html);
                Assert.DoesNotContain("GARBAGE", html);
            }
        }

        [Fact]
        public void Templates_use_only_filters_the_filler_knows()
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mm", "mm2", "mm3", "kg", "g", "MPa", "kN", "Nm", "degC", "min", "pct" };
            foreach (var name in new[] { "setup-sheet.html", "machining-sheet.html" })
                foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(ToolDir, "data", name)), @"\{\{[^}#/|]+\|([^}]+)\}\}"))
                    Assert.Contains(m.Groups[1].Value.Trim(), known);
        }

        [Theory]
        [InlineData("Gauge reading", "press_pressure", 13.79)]
        [InlineData("On the part", "part_pressure", 6.895)]
        [InlineData("Force reading (tons)", "press_force_reading", 177.9)]
        public void Every_press_meaning_reads_its_own_page_field(string meaning, string id, double value)
        {
            Assert.Contains(Inputs, i => (string)i["Id"] == id);
            var d = E2Design();
            var inputs = (JObject)d["inputs"];
            inputs.Remove("press_pressure");
            inputs["press_pressure_is"] = meaning;
            inputs[id] = value;
            Assert.Equal(value, Design.Read(d, Data).PressValue, 6);
        }

        [Fact]
        public void Lifting_answers_use_the_page_ids_and_the_press_reach_floor()
        {
            foreach (var id in new[] { "lid_lift_at", "lid_reach", "lid_lifts_per_hour" }) Assert.Contains(Inputs, i => (string)i["Id"] == id);
            var press = Lifting.LidLimit("Inside the press", 40, 12, Cfg());
            Near(7.4, press.Rwl_kg, 1.5, "press, default reach");
            Near(11.67, Lifting.LidLimit("At the slide-out table", 40, 12, Cfg()).Rwl_kg, 1.5, "table");
            Near(11.0, Lifting.LidLimit("Not sure", 40, 12, Cfg()).Rwl_kg, 0.1, "not sure");
        }
    }
}
```

- `BuiltInToolsTests` for this tool (Track C owns the file): 44 inputs; kind tool, confirm always, long_run, max_steps 600, category; every `ChoicesFrom` resolves; every `Fills` path exists (a null parent such as `viscosityCp: null` is allowed); every `requires` tool registered.
- Extend `ScriptingTests.Saved_tool_scripts_compile` to also compile `builtin-tools\*\macro.cs`, so `macro.cs` is covered in CI.
- Shipped extras: `data/README.md` and `data/examples/mold.sample.json` go with the tool (small; the sample is the agent's reference for record shapes and the fixture for the test above).

### Icon generator (`icon.ps1`, PowerShell 5.1 + System.Drawing)

`powershell -File icon.ps1 -Out tools\builtin\design-compression-mold\icon.png -Size 128`

```powershell
# Draws tools/builtin/design-compression-mold/icon.png: an open two-plate compression mold in a light oblique view -
# the lid lifted above the base, the base's parting face with a cavity pocket, two dowel pins between - in the Unify3D
# brushed-steel/graphite style of src/SwAgent/Icons.cs (same colours, gradients and dark edge).
# Drawn on a 128 px canvas (the Icons.cs coordinate system) and scaled to the output size.
param([string]$Out, [int]$Size = 128)
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
# Cavity pocket in the parting face: an oblique opening with its far wall in shade.
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

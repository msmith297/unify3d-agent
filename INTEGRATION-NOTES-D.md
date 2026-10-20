# Integration notes - Track D (SolidWorks geometry tools)

Branch `worktree-wf_71b3d5bb-873-5`. Everything below is what the other tracks and the lead need to know to merge Track D.

## 1. Requests for files Track D doesn't own

| File (owner) | Request | Why |
|---|---|---|
| `src/SwAgent/Scripting/ScriptHost.cs` (no track) | Add `"SwAgent.Mold.Geometry"` to `WithImports(...)` in `BuildOptions`, and one clause to the `execute_csharp` description: "Mold.* (design-compression-mold building blocks: Frame, InsertPart, ScaleForShrink, Plate, Subtract, Folder, FlashGrooves... each checked by measurement)". | Cells can call `Mold.*` today, but naming its types (`MoldFrame`, `FlashOptions`, `PartAnalysisOptions`, `MoldBody`) needs the namespace. `Mold.FlashOptions()` is a factory that works without it. |
| `src/SwAgent/Tools/DesignSnapshot.cs` (no track) | Lines ~120 and ~136 toggle `DocumentVisible` directly. Wrap that open/close in `using (HiddenDocuments.Scope(sw))` (or `LockAsync` + `Hidden` if it awaits in between). | Critique B6: one gate for every hidden open. `HiddenDocuments` lives in `src/SwAgent/ModelFiles/SwModelLoader.cs`. |
| `src/SwAgent/UI/PreviewDisplay.cs` (no track) | Not changed: temporary bodies still ignore transparency. Translucent previews are done without temporary bodies: `Overlay.Box(...)` (pure triangles) and `show_overlay` items `face` / `box`. If a translucent *temporary body* is still wanted, tessellate it with `IBody2.GetTessellation` and draw it as a `tris` item - not verified that it is safe on temp bodies, so not done. | Design 2.3.8 last bullet. |
| `ui/src/**` (Track C) | `files.entries` / `files.describe` now return a `.slddrw` as `kind: "pdf"` with `name: "<file>.slddrw (as PDF)"`, `mediaType`, `data` and an extra `pdfPath`; or `kind: "too_large"` with `error` when the PDF is over 20 MB. The conversion happens in `files.entries` too (a few seconds for a big drawing), so the composer may want a "reading" state for drawings. The mock host can return a drawing as a pdf entry. | Design 2.3.9. |
| `tools/builtin/design-compression-mold/TOOL.md` (Track E) | See section 3 for the exact names and signatures to use. | |
| A1 (`src/SwAgent/Mold/**`) | See section 2. Delete `src/SwAgent/Mold/_A1Stubs.cs` at integration (it only exists so Track D compiles). | |

## 2. What Track D assumes about Track A1's API

Coded against the contract signatures exactly. Behaviour assumed:
- `MoldRecord.Merge(patch)` deep-merges (objects by key, arrays of objects by `id`, other arrays replaced) and returns a short status line; Track D always calls `Save()` after it. Track D merges these keys:
  - `mold_part`: `frame` {origin mm, u, v, w}; `part` {volume_mm3, area_mm2, box_mm, projectedArea_mm2, perimeter_mm, draft {minDeg, faces[] {id "F<n>", class needs_draft|straddle, minDeg, maxDeg, lowShare, area_mm2, persist}}, undercuts[] {id, area_mm2, side, persist}, fragile[] {id "K<n>", kind core_pin|pocket_core|steel_wall, d_mm, h_mm, side, at [mm], flagged, reason}, partingLineCandidates[] {id top|bottom|outline|equator, w_mm, why, lidDepth_mm, baseDepth_mm, draftArea_mm2 {lid, base}, airTraps, airTrapVolume_mm3, veto, score, scoreNotes[]}}; `airTraps[]` {id "AT<n>", at [mm, model], volume_mm3, area_mm2, depth_mm, fill, tiltDeg}.
  - `mold_bodies report`: `bodies[]` {id, role, folder, swName, swMaterial, volume_mm3, mass_kg, massBasis, box_mm (dims), boxFrame_mm [u0 v0 w0 u1 v1 w1], centroid [mm, frame], limits {mass, box}} and `checks[]` (types weight, box, interference; `source: "mold_bodies"`, margin = capacity/demand - 1).
  - `mold_bodies clearance`: `checks[]` type clearance (id `CLR-<a>-<b>`).
  - `mold_bodies export`: `bodies[]` {id, step: "exports/bodies/<ID>.step"} and `files.exports.part` {shrunk, molded}.
  - `mold_calc`: `result.RecordPatch` plus `result.Checks` appended to `patch.checks`.
- `MoldRecord.Init(folder, ...)`: Track D creates `folder`, `part\`, `exports\bodies\`, `exports\part\`, `checks\` before calling it. `inputs` is `ToolRunner.LastInputs["design-compression-mold"]` as JSON: each value as ToolInputs holds it (numbers in SI - lengths m, angles rad - unless Track B changes the payload), selections as [{type, label, detail, persist}], files as [paths].
- `MoldRecord.Json["status"] == "done"` after a merge unregisters the file from in-place checkpoint restore (critique B2).
- `MoldRecord.Section(key)` returns null for a missing key.
- Read from the record (all optional, lenient): `materials.part.used.draftMinDeg`, `materials.part.used.sg`, `inputs.draft_min_deg` ({value, unit} or a bare number; bare values under 0.2 are taken as radians), `inputs.max_plate_mass / max_lid_mass / max_base_mass` (kg, `.value` or bare), `inputs.max_plate_size` (3 numbers, m or mm), `layout.cavities[] {id, origin [mm]}` (insert ids INS-C1-1), `hardware.fits[] {bodies [a, b]}` (intended interference), `materials.plates.id` / `materials.inserts.id` (grade for densities), `files.moldedPart.path`.
- Grade density: `MoldData.MoldGrade(id)` row, field looked up as `density_kg_m3`, `densityKgM3`, `rho_kg_m3`, `density.kg_m3`, `density.typ` or `density` (values under 100 taken as g/cm3). Please make sure one of these exists, or tell the lead the real name.
- `SetupSheet.Write` / `MachiningSheet.Write` get `<tool>/data/setup-sheet.html` and `<tool>/data/machining-sheet.html`.
- `mold_record save_default` writes the override file itself (design 1.5.5 format): `%APPDATA%\SwAgent\tools\builtin-data\design-compression-mold\<same file name>`; for a table with rows, the whole row (shipped row + earlier overrides + the new values) goes into an array of the same name as the shipped one (`rows`, `mold_materials`...) - a row with the same id replaces the shipped row; object tables merge key by key. Every dotted path must exist in the shipped table. `MoldData.Load(toolDir, overrideDir)` must merge exactly that way.
- Every Track D call into A1 catches `NotImplementedException` and reports "the mold calculator library isn't part of this build yet", so the stubs never crash a run.

## 3. For Track E (TOOL.md): names and signatures

Agent tools (descriptions start "For the design-compression-mold tool:"):
- `mold_calc {action: lookup|process|press|layout|size|split|check, design?, query?, overrides?, record?}`
- `mold_part {action: analyze, body: auto|PART-REF|<id>|<name>, pull?: [x,y,z], fill: base|lid|none, tilt_deg, min_draft_deg, sample_mm, critical: ["S7"...], vents: [[x,y,z] mm], sg, show, record}` - air traps are computed for the best candidate's plane; the text lists candidates best first with scores.
- `mold_bodies {action: report|clearance|export, pairs, min_clearance_mm, format: step|x_t, step_ap: 214|242, bodies, densities {role: kg/m3}, limits {max_plate_mass_kg, max_lid_mass_kg, max_base_mass_kg, max_plate_size_mm, blank_allowance_mm}, show, record}`
- `mold_record {action: init|read|merge|setup_sheet|machining_sheet|save_default|path, section, data, table, id, values}` - init needs a saved part; it sets custom properties `Unify3D.MoldRecord` (folder name) and `Unify3D.MoldId` and registers the file for in-place `checkpoint restore`.
- `show_overlay` gained item types `face` (`ref` "S3" and/or `faces` persistent refs, `color`, `alpha`, `text` label) and `box` (two corners, translucent). Fill is capped at 3,000 triangles (more are outlines) and drawn only while the camera rests.
- `checkpoint`: stamps are now `yyyyMMdd-HHmmss-<name>`; restore takes an exact file name, else the NEWEST checkpoint with that name; the last 20 per design are kept. For a mold, checkpoints and the journal are keyed by `Unify3D.MoldId`.

`Mold.*` in cells (meters, radians; every method throws `MoldApiException` with what it measured when its self-check fails, and puts the bodies it made in `Mold.LastBodies`):
- `MoldFrame Frame()` (W = Top Plane normal = 2nd RefPlane; `ToFrame`, `ToModel`, `U/V/W`, `ModelScaleFactors`)
- `IList<MoldBody> Bodies()`, `IBody2 Body("BASE-1" | name)`, `IBody2 PartBody()`, `double[] Box(body)` (frame u0 v0 w0 u1 v1 w1), `double Volume(body)`
- `IFeature InsertPart(path, config = "", expectedVolumeMm3 = 0)` -> feature `MOLD Part <name>`; STEP/IGES/Parasolid converted to `<record>\part\<name>.SLDPRT`; STL refused; B8 checks (one solid, no surfaces, Check3, volume within 0.5%) - on failure the insert is removed and the message says what was found.
- `IFeature Orient(body, {ax, ay, az, angleRad}, {dx, dy, dz})` -> `MOLD Orient`, `MOLD Orient move` (model axes only).
- `IFeature ScaleForShrink(body, su, sv, sw, plW)` -> `MOLD Shrink 1.54%` about `MOLD CS` at the parting-plane centroid (pass the parting plane's W in meters as plW!); fallback: about the origin + `MOLD Shrink re-center` move. Factors are scale factors (1/(1-S)), per U/V/W.
- `IFeature CopyBody(body, {dx,dy,dz}, n)` -> `MOLD Copy <body>`; `LastBodies` = the copies in order.
- `IFeature Plate(id, u0, v0, u1, v1, w0, w1)` -> `MOLD Plate <id>` (+ `MOLD Plate <id> sketch` on the Top Plane).
- `IFeature Subtract(target, tools, name)` (tools are consumed - copy first).
- `IFeature SplitByPlane(body, planeFeature, name)`.
- `IFeature Folder("Mold - Base", bodies...)` (merges with an existing folder of that name).
- `bool SetBodyMaterial(body, "6061-T6 (SS)", db = "SOLIDWORKS Materials")`.
- `FlashResult FlashGrooves(face = null, Mold.FlashOptions())` -> `MOLD Flash moat`, `MOLD Flash runners`, one `MOLD Flash groove R0.25 (<body id>)` per body; measured lip/clearance; throws with a GEOMETRY summary for combined moats, freeform outlines, unclear multi-plate bases. Re-running replaces the earlier `MOLD Flash*` features.
- `IFeature CboreHoles(name, face, points, holeDia, holeDepth, cboreDia, cboreDepth)`, `IFeature TappedHoles(name, face, points, tapDrillDia, drillDepth, thread)` - sketch + blind cuts (Hole Wizard waits for spike S11; the thread spec goes in the record's hardware list and the machining sheet).
- `IFeature PocketCornerReliefs(openingFace, pocketFloor, cutterRadius)` -> `MOLD Pocket reliefs` (dog-bones R = 1.1 x cutter at sharp corners, critique B7). `PocketRelief.InsertChamfer(R)` gives the insert's matching chamfer for the corner-radius style.
- `PartReport Analyze(body = null, options = null)` - the mold_part analysis.
- `Overlay.Faces(faces, color, label, alpha)`, `Overlay.Box(corner0, corner1, color, label, alpha)`, `Overlay.Box(frame, u0, v0, w0, u1, v1, w1, ...)` (plate-stack previews, no temporary bodies).

## 4. SolidWorks spikes to run before release (nothing here could be run without SolidWorks)

Each call was checked against the SW2026 interop by reflection, and each method measures its own result, but these behaviours are unverified:
1. `InsertPart3(sldprt, ImportSolids | DontZoomAll, config)` inserts one body; `LoadFile4` + `SaveAs3` of a hidden STEP import; `IBody2.Check3` on the result.
2. `CreateCoordinateSystemUsingNumericalValues(true, x, y, z, false, 0, 0, 0)` and `InsertScale(swScaleAboutCoordinateSystem, ...)` with the body and the CS selected - which marks (the code tries 0/0, 1/2, 2/1 and checks each by measurement, then falls back to origin + re-center).
3. `InsertMoveCopyBody2` rotation sign (the code checks and retries the other sign), and copies with NumCopies > 1 spaced by the translation.
4. `PreSplitBody2` / `PostSplitBody2` selection marks (tries plane 1 / body 2, then 0 / 0).
5. `RayIntersections` with ENTRY_EXIT | UNBLOCK returns every crossing with ENTER/EXIT flags (if not, mold_part falls back to the envelope and says so in its notes); 20,000 rays under 2 s.
6. Body folders: `InsertSubFolder` with bodies already in another folder moves them; folder membership survives later cuts.
7. `IBody2.SetMaterialProperty` per body reads back with `GetMaterialPropertyName`.
8. Export: `NewDocument` while `DocumentVisible(false)` gives a hidden part; `CreateFeatureFromBody3` of a `Copy2`; `swStepAP` 214/242.
9. `.slddrw` -> PDF from a hidden read-only drawing (`IExportPdfData.SetSheets(ExportAllSheets)`): all sheets, not blank.
10. `FeatureFillet3` with `swFeatureFilletUniformRadius` only (no Propagate), one per body, on the flash grooves (the user's test plate: 40 R0.25 edges; hc-64).
11. Plate extrusion direction (`FeatureExtrusion3` start offset, verified in v1; the code retries reversed if the box is wrong).

## 5. Not done in Track D (and why)

- Hole Wizard holes (spike S11 parameters unknown): holes are plain cuts with measured diameters.
- Translucent temporary bodies (PreviewDisplay is not Track D's; see section 1).
- The flash system's dead-end check (the user's macro doesn't do it either; the TOOL.md procedure checks it).
- Air-trap volumes stay at the column spacing on big parts (high points are refined to sample_mm).

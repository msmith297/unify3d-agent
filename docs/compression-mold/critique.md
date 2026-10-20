# Critique: design for "Design compression mold for plastics and resins"

Reviewed: `design.md` (1,879 lines) against every note it cites: structural.md, design-rules.md, materials.md/.json, mold-materials.md/.json, existing-mold-knowledge.md, pm-page.md, pipeline.md, agent-abilities.md, check/method_check.py and proto/. The repository was read at commit f299941. Nothing in the repository was changed.

How the claims were checked:
- `check/method_check.py` was re-run. E1, E2, E3, E4 (nominal), E5, E6, E8, E10 and T16 reproduce the quoted digits.
- The SolidWorks 2026 interop in `src/SwAgent/bin/Debug/net48` was reflected directly (`scratchpad/critique-work/reflect.ps1`, `reflect2.ps1`). The api-dump files are regex-filtered, so a method missing from them proves nothing.
- Every new number below was computed in `scratchpad/critique-work/numbers.py`.
- The cited code paths were read: ToolPage.cs, ToolRegistry.cs, RebuildTools.cs, SwModelLoader.cs, FileAttachments.cs, PreviewDisplay.cs, runAgent.ts, store.ts.

Severity:
- **High**: can produce a dangerous mold, wrong press settings, data loss or a SolidWorks crash.
- **Medium**: will not work as written, or produces wrong geometry.
- **Low**: a gap, an inconsistency or clean-up.

`design.md:n` means a line in the design document. Each item gives the issue, why it matters and the fix.

---------------------------------------------------------------------------------------------------

## Top items, in priority order

| # | Sev. | Item |
|---|---|---|
| A1 | High | Bolt checks ignore torque-preload scatter and hot proof strength. With them, the design's own E4 corner fails (108% of hot proof; separation 1.64). |
| B1 | High | A `JObject` tool input sends ToolRegistry into infinite recursion. The result is a stack overflow that kills SolidWorks at add-in load, not just a wrong schema. |
| B2 | High | `checkpoint restore` can copy another design's file, or an older attempt, over the mold. Checkpoints are keyed by file name only, stamped HHmmss and matched alphabetically. |
| A2 | High | The 18 kg lid and 23 kg plate defaults exceed the NIOSH limit the design itself cites: about 11.7 kg at the table and 7.4 kg inside the press. |
| A3 | High | Class A joints, keys and bolts are never checked at the abuse pressure LC1x, which is 5.6 times LC1 in E2. |
| E1 | High | The machine shop gets STEP files with no fits, flatness, finish or thread data. The values that make the mold work are lost. |
| A5 | High | The shrink value has no stated basis. The new formula makes cavities 0.07% smaller than v1 for the same number, and may count the aluminium correction twice. |
| A6 | Med-High | Scaling about the model origin moves the part 0.36-0.44 mm off the parting plane. The record example shows exactly this. |
| A7 | Med-High | The planar parting-line test rejects a plain puck with 0° walls, and spheres and O-rings. |
| A4 | Med-High | Walls with L/h > 4 use the L/h = 4 row, which is unconservative, not conservative: deflection 12-24% low, stress 11-13% low. |
| B3 | Medium | Pry slots, lift notches and TC wells are cut after the flash runners, so the runner search can't avoid them. |
| A9 | Medium | The force projected area leaves out overflow grooves and loading wells, so the force and gauge come out about 10% low for rubber. |
| C1-C2 | Medium | Press units follow the document, not the press. "On the part" hides the press rating. A tonnage gauge reading is not an option. |
| G4 | Medium | The tool run's auto-approval stays on for any follow-up in that chat for 24 h. |

---------------------------------------------------------------------------------------------------

## A. Engineering errors and unsafe assumptions

### A1. Bolt checks ignore preload scatter and hot proof strength; E4 actually fails (High)
**Where:**
- S45-S51, design.md:1317-1326.
- `sizing-defaults.bolts`, design.md:575-578.
- E4, design.md:1437.

**Issue:**
- S47 (hot load ≤ 0.90·F_p) and S48 (separation n_0 ≥ 2) both use the nominal torque preload F_i.
- S47 compares against the room-temperature proof load.

**Why:**
- Torque tightening scatters preload. The project's own `mold-materials.json` → `fasteners_common.torque_formula` says "Torque is an indirect measure: +/-25% preload scatter".
- VDI 2230 handles this with the tightening factor α_A, about 1.4-1.6 for a torque wrench. Yield-type checks need the upper bound and the separation check needs the lower bound.
- A574 and 12.9 screws are quenched-and-tempered alloy steel and lose strength when hot. The file has no hot factor for screws. As a stand-in, P20 and 4140 in mold-materials.json keep 0.95, 0.92 and 0.89 of their yield at 100, 150 and 200 °C.

E4 recomputed: 4 x 1/2-13 A574, grip 2.0 in, aluminium joint, ΔT = 100 K, P = 7,062 lbf per bolt.

| | Design (nominal F_i) | ±25% scatter, hot proof factor 0.95 |
|---|---|---|
| Preload | 11,920 lbf (0.60 F_p) | max 14,900, min 8,940 lbf |
| Hot bolt load | 17,479 lbf = 88% F_p: pass | 20,461 lbf = 103% of F_p at 20 °C = 108% of F_p at 100 °C: **fail** |
| Separation n_0 (10% embedding) | 2.19: pass | 1.64: **fail** |

No preload fraction makes 4 x 1/2-13 pass both checks. The first options that pass are:
- 6 x 1/2-13 at 0.49 F_p;
- 4 x 5/8-11 (grip 2.5 in) at 0.47 F_p.

E5 behaves the same way: 0.60 F_p plus 25% is 0.75 F_p, which the design itself shows reaches 98% of proof when hot.

**Fix:**
1. Add to `sizing-defaults.bolts`:
   - `tighteningScatter`: 0.25 for a torque wrench, 0.10 with torque-angle or direct-tension-indicating washers;
   - `hotProofFactor` by temperature: 0.95 / 0.92 / 0.89 at 100 / 150 / 200 °C until supplier data is found.
2. In S45, define F_i,max = (1 + s)·F_i and F_i,min = (1 − s)·(1 − e)·F_i.
3. S47, S49 and S51 use F_i,max and F_p,T = k_T·F_p. S48 uses F_i,min.
4. Re-derive E4 (expected result: it fails, and 4 x 5/8-11 or 6 x 1/2-13 passes) and E5.
5. The default aluminium-joint preload drops from 0.60 to whatever the new checks return, about 0.45-0.50 F_p at 100-150 °C.
6. The setup sheet prints each torque together with the preload band it gives.

### A2. Default lid and plate weight limits exceed the lifting equation they cite (High, operator safety)
**Where:** design.md:336-339 and 345-346 (inputs.json defaults of 23 kg and 18 kg).

**Why:**
- 23 kg is NIOSH's load constant (Waters et al. 1993). It is the limit for an ideal lift only: load close to the body, at knuckle height, occasional, with a good grip.
- The HSE 25 kg / 16 kg figures quoted are for loads held against the body at waist height.
- A hot lid lifted every cycle is neither. The recommended weight limit is RWL = 23 · (25/H) · (1 − 0.003·|V − 75|) · (0.82 + 4.5/D) · AM · FM · CM:
  - Lid lifted at the slide-out table: H 40 cm, V 90 cm, D 25 cm, ≤ 0.2 lifts/min for 2-8 h (FM 0.85), good handles (CM 1.0). RWL = **11.7 kg**.
  - Lid lifted inside the press: H 55 cm, V 100 cm, gloved grip (CM 0.90). RWL = **7.4 kg**.
- The 18 kg default therefore means a lifting index of 1.5-2.4.

**Fix:**
1. Set the lid default from the equation, using three answers kept with the press inputs: where the lid is lifted (table or press), the horizontal reach, and lifts per hour. Use 11 kg (25 lb) when they are unknown.
2. Print the lifting index on the setup sheet.
3. Keep 23 kg only for plates lifted occasionally.
4. When the lid is over the limit, offer, in this order:
   - lightening pockets on its platen face, keeping at least 80% platen contact (`platenCoverageForCaseA`);
   - a lift assist or a hinge;
   - a two-person lift.
5. Do not offer a split lid for class A, because the seam crosses the seal.
6. Revisit the 0.05·L minimum-plate rule (design.md:1280) when it is what pushes a lid over the limit. Under case A, nothing structural needs it.

### A3. Class A joints, keys and bolts are not checked at the abuse pressure (High)
**Where:**
- LC1x (design.md:1160) is applied to "walls and inserts" only.
- S37-S41 (1306-1310) and S47-S48 use p_LC1.

**Why:**
- In E2, LC1x = 8.67 MPa, which is 5.59 times p_LC1 = 1.551 MPa.
- LC1x is not exotic. A charge that gels before the lid seats reaches F/A_proj. So does exotherm or thermal expansion in a cavity whose runners are blocked by a critical-face constraint.
- A keyed multi-piece base sized for p_LC1 with n_0 = 2 separates at LC1x. Hot resin, MBOCA-cured PU for the default material, then leaks or sprays at the seam.

**Fix:**
- Run S37-S41, S47 and S48 for LC1x as well, with SF 1.0 on yield and n_0 ≥ 1.0 (no separation).
- Where that fails, either size the joint for it, or cap the maximum gauge on the setup sheet at the joint's capacity (the same pattern as S35).

### A4. Long cavity walls are under-sized: the L/h = 4 row is not conservative (Medium-High)
**Where:** S20, "For L/h > 4, use the 4.0 row (conservative)" (design.md:1262). structural.md 5.3 says the same.

**Why:**
- α_w and β_w rise with L/h toward the cantilever-strip limit, so the L/h = 4 row under-predicts.
- The strip limit with shear (ν 0.3, κ 5/6) is α = 1.365 + 1.56·τ² and β = 3.0.
- Compared with the Wall-C L/h = 4 row (structural.md, proto/wall.txt), the row gives:
  - deflection 12-24% low, so walls 4-7% thin when deflection governs;
  - stress 11-13% low, so walls 5-6% thin when stress governs.
- E2's long wall (6 in long, 1 in deep, so L/h = 6) and E3 (L/h ≥ 4) both used this row.
- It also breaks Sweep 2's guarantee that max r ≤ 1.00.

**Fix:**
- Tabulate L/h = 6, 8 and 12 in Sweep 1 (6.0 is already in the sweep plan, design.md:1456).
- Above the last row, use the closed-form strip with shear.
- Add a test that α_w and β_w never decrease with L/h and never exceed the strip values.

### A5. Shrink: the page value has no stated basis, and the formula change alters cavities (High for tight-tolerance parts)
**Where:**
- S64 (design.md:1372-1383).
- The Shrinkage tip (design.md:391).
- `shrinkBasis` in the materials rows.

**Why:**
- **The formula change.** v1 scaled by s = 1 + S, with no correction for the mold metal (existing-mold-knowledge 2.1). For S = 1.5% at 100 °C in 6061, S64 gives s = 1.014288 against v1's 1.015.
  - The cavities come out 0.070% smaller: 0.11 mm on a 6 in part and 0.21 mm on 300 mm.
  - That is the whole tolerance band of a ±0.1 mm part.
- **The double count.** Shop shrink numbers are usually measured in the shop's own molds. If the user's 1.5% came from his aluminium molds at his temperature, the steel-to-aluminium term counts the metal twice.
- **The PU rows' basis.** All 25 `liquid_cast_hot` rows are tagged `cold_mold_cold_part_steel_ref`. Yet the TDI 90A row's own `shrinkNotes` describe contraction from a 100-120 °C metal mold, which is the hot-cavity basis (materials.md, Formula 3).
  - Formula 2 and Formula 3 differ by (1 + α_ref·ΔT) = 0.096%, which is 0.15 mm on 6 in.

**Fix:**
1. Add a `shrink_basis` choice right under Shrinkage, with three options:
   - "Supplier datasheet (steel mold)";
   - "Measured in my molds at this temperature";
   - "Hot cavity to cold part".
   Default it from the material row and remember it per grade through `save_default`.
2. "Measured in my molds" gives s = 1/(1 − S) with no metal term. Offer "v1 style, 1 + S" once, so jobs built with v1 can be matched.
3. Retag the hot-cast PU rows as hot-cavity basis, or flag them "basis uncertain".
4. Put the basis and the factor in the feature name and on the setup sheet, for example `MOLD Shrink s=1.014288 (datasheet basis, 6061 at 100 C)`.

### A6. Scaling about the model origin moves the part off the parting plane (Medium-High)
**Where:**
- Stage 4.1 (design.md:236).
- `Mold.ScaleForShrink` (design.md:1004).
- The record example (design.md:1576-1577 and 1587).

**Why:**
- `swScaleAboutOrigin` maps a point at height w to s·w. The parting-plane heights come from the Phase 2 analysis of the unscaled part.
- So a parting plane at 25.4 mm ends up below the scaled part's top edge by:
  - 0.363 mm at 1.5% shrink;
  - 0.442 mm at 1.8% shrink.
- The record example shows exactly this case: `w_mm: 25.4` together with `scale 1.017387`.
- The result is a step at the parting line, and the flash line lands on a side face, possibly a critical one.
- `InsertScale` takes x/y/z factors along model X/Y/Z. The user's template is Z-up, but SolidWorks' default template is Y-up, so per-axis shrink (PEEK, SMC) would go on the wrong axis there.

**Fix:**
1. Create `MOLD CS` with `IFeatureManager.CreateCoordinateSystemUsingNumericalValues` at the parting-plane centroid of the part, with axes U, V, W.
2. Scale with `swScaleType_e.swScaleAboutCoordinateSystem` (value 2 in the SW2026 interop; `IScaleFeatureData.CoordinateSystem` exists).
   - The parting plane then stays put.
   - Cavity copies keep their layout origins.
   - Per-axis factors map to U, V and W.
   Add a spike for the selection marks the scale needs (the bodies plus the coordinate system).
3. Add an acceptance check: every PL plane lies on the scaled part's silhouette within 0.01 mm.

### A7. The planar parting-line test rejects common parts (Medium-High)
**Where:** `mold_part` step 5 (design.md:923-928).

**Why:**
- z_lo and z_hi are computed per face, and "needs draft" faces count as neither lid-demoldable nor base-demoldable.
- Example: a Ø100 x 25 mm puck with 0° side walls. This is normal for cast PU; 0.5° is only the material minimum.
  - z_lo = 25 mm (the top of the side face) is greater than z_hi = 0 (the bottom of the side face).
  - The tool reports "needs a stepped parting line or side action".
- A sphere or an O-ring cross-section, where one face spans both halves, fails the same way. Yet the answer, the equator, is textbook (design-rules 2 #7).

**Fix:**
- Classify per tessellation sample, not per face.
- Samples with |n·W| < sin(min draft) may sit on either side. Flag them for draft and score the draft per half.
- Take z_lo and z_hi from strictly lid-only and strictly base-only samples.
- Fixtures:
  - F1 must expect "parting line at the top face";
  - add a 0° puck, a sphere and an O-ring section, each with its expected parting line.

### A8. Temperature gates are not stress-aware, and creep and temperatures above 250 °C are missing (Medium)
**Where:** S10 and S12 (design.md:1194-1206), and bearing S34.

**Why:**
- **Gates ignore stress.** S12 blocks or limits aluminium by temperature alone.
  - Row `pvc-plastisol` molds at 170 °C and 20 psi, with `moldCompat.aluminium = "good"`. Yet 6061 at 170 °C is "limited" (N ≤ 10,000 plus confirmation), although its stresses are near zero.
  - Meanwhile a rubber mold at 150 °C with 10 MPa on 6061 passes as "ok-derated".
- **Creep is not covered.** The 10,000 h yield basis is not a creep basis. Lands and stop seats hold the clamp load through every cure. Above about 120 °C, aluminium creeps under that sustained bearing stress, and this is how lands hob over months. D_b = 2-3 on the 10,000 h yield does not cover it.
- **Unsafe extrapolation.** S10 runs steels above 250 °C with the 250 °C factors (PEEK at 400 °C). That extrapolates strength data in the unsafe direction.

**Fix:**
1. S12 decides by stress ratio.
   - Allow a "limited" temperature when every governing stress is ≤ 0.5 x the derated allowable, and tell the user why.
   - Keep `never_above` as a hard block only for grades that age and change size (2024).
2. Above 120 °C, cap aluminium bearing stress at a 10,000 h creep allowable (Kaufman has creep data). Until that data is loaded, use D_b = 4 above 150 °C.
3. Refuse T > 250 °C, where there is no data, instead of extrapolating.

### A9. The force projected area leaves out overflow grooves and loading wells (Medium)
**Where:** S2 (design.md:1169-1171), which S6 and S7 use.

**Why:**
- materials.md section 4 says A_projected covers "cavities + lands + overflow grooves + loading wells".
- In class B the rubber fills the groove too. A 3 mm groove around a 6 x 4 in cavity is 1,524 mm², which is 9.8% of the cavity.
  - F_req and the recommended gauge therefore come out about 10% low.
  - The likely result is porosity and back-rind.
- In a positive mold the press force acts on the plunger (loading-well) area, not on the cavity area.

**Fix:**
- F_req uses A_proj,force = Σ(A_i + P_i·w_land + A_groove,i + A_well,i).
- Keep the smaller area for p_LC1 = F/A_proj, because that is the worst case for the steel.

### A10. Positive and semi-positive molds, and class C materials, are planned but can't be built (Medium; scope)
**Where:**
- D4 (design.md:26).
- Phase 2 step 2 (design.md:186-191).
- S70 (design.md:1392).
- The stage table (design.md:234-244).

**Why:**
- Phase 2 can choose semi-positive or landed-positive, and class C (positive) is set automatically for 8 rows.
- But no stage builds a force/plunger with its 0.025-0.075 mm per-side clearance, a loading chamber or a shear edge.
- These materials are outside the PU/resin/rubber shops the tool targets, and beyond the 250 °C data:
  - PEEK and PAEK: 400-420 °C, 5,000-5,800 psi;
  - PTFE preforms: 4,500-10,000 psi;
  - UHMWPE.

**Fix:**
- v1 builds flash-type molds for classes A and B only.
- Class C rows, and class B parts deeper than they are wide, get an advisory plan and a clear "not built by this tool" message, stated in TOOL.md and the plan form.
- Add a "force + loading chamber" stage later.

### A11. Flash-gap allowables are looser than the notes and leave no gap budget (Medium)
**Where:** S13-S14 (design.md:1216-1231) and `deltaFlash_mm` (design.md:565-566).

**Why:**
- **Looser than the source.** design-rules 13.2.1 sets land deflection at ≤ 1/2 of the flash-gap target: ≤ 0.005-0.012 mm for liquids and ≤ 0.02 mm for rubber. The design uses 0.012-0.025 mm and 0.050 mm (from structural.md 4.1) without saying why.
  - In deflection-governed sizing, t ∝ δ^(-1/3), so 0.05 mm against 0.02 mm is a factor of 1.36 in thickness.
- **No budget.** The whole δ_flash goes to deflection. Yet design-rules 19 gives land and PL flatness of 0.01-0.02 mm, and platen parallelism under load adds more. Together they can use up the whole gap.
- **Walls.** In a flash-type mold, a wall's sideways bulge hardly opens the vertical gap: the top-edge rotation is 4δ/(3h), about 0.002 mm across the 0.125 in lip in E2. For walls the limit is really a width tolerance. It does open the clearance in telescoping fits.

**Fix:**
1. State a gap budget per class: land flatness + platen parallelism + deflection ≤ δ_flash. Default δ_allow = δ_flash − the flatness tolerance.
2. Walls in flash molds use 0.25·t_tol on the width. Telescoping fits use half the design clearance.
3. Add the class values to the confirm list (6.3).
4. Put the flatness and lapping call-outs on the machining sheet (E1).

### A12. Shut-off core pins can carry the press force (Medium)
**Where:** S61-S62 (design.md:1355-1368).

**Why:**
- S62 checks lateral bending only.
- A pin that shuts off against the lid and stands proud of the land takes k·Δ before the lid lands.
- A Ø3 x 20 mm steel pin has k = 72.5 kN/mm. At 0.1 mm proud it carries 7.3 kN, above its fixed-free Euler load of about 5.0 kN. It buckles or mushrooms and marks the lid.

**Fix:**
- Add a rule to TOOL.md and the insert stage: shut-off faces are set 0.01-0.03 mm below the land, never proud, and that tolerance goes on the machining sheet.
- Add an axial check: bearing plus Euler buckling (K = 2 unless the tip is guided), using the full height tolerance.

---------------------------------------------------------------------------------------------------

## B. Things that won't work as written (SolidWorks and this codebase)

### B1. A JObject input crashes SolidWorks at add-in load (High)
**Where:** design.md:856-861; ToolRegistry.cs:188-221.

**Why:**
- `BuildSchema` walks every public property of the input type, and `SchemaFor` sends any class back to `BuildSchema` (ToolRegistry.cs:219).
- A `JObject` property therefore recurses without end: JObject → `Parent` (JContainer) → `BuildSchema(JContainer)` → `Parent` → ...
- .NET can't catch a StackOverflowException. SolidWorks dies inside `AgentAddIn.OnConnect` at `tools.RegisterAll(new MoldTools())`.
- The design describes the effect only as "a schema of JObject's own CLR properties".

**Fix:**
1. The proposed `JToken` guard is right. Make it the first commit of stage 4.
2. Add a unit test that builds the schema of every `[AgentTool]` input type.
3. Add a depth limit in `BuildSchema` (depth > 6 gives `{"type":"object"}`), so the next unknown type fails soft.

### B2. Checkpoint restore can load another design or an older attempt (High: data loss)
**Where:**
- D11 (design.md:33) and `mold_record init` (design.md:984).
- RebuildTools.cs:60-64, 229, 251 and 256-257.

**Why:**
- `DesignDir` keys the journal and the checkpoints by file name only (RebuildTools.cs:63). Two molds called "bushing mold.SLDPRT" in different folders share both.
- Checkpoints are stamped `HHmmss` with no date (line 229), and restore takes the alphabetically last match (line 251).
  - The mold tool reuses names such as "stage-4.2" on every attempt.
  - So yesterday's 15:00 checkpoint wins over today's 09:00 one.
- Restore then closes the mold without saving and copies the match over it (lines 256-257).
- `RegisterWorkingCopy(path, path)` marks the user's file as its own working copy, and `working-copies.json` is never pruned. That permanently turns off the "never on the user's original" protection for this file, which Smart Rebuild relies on too.

**Fix:**
1. For molds, key `DesignDir` by the record GUID (custom property `Unify3D.MoldId`).
2. Stamp checkpoints `yyyyMMdd-HHmmss`, and restore by exact name or by newest file time.
3. Register the file with a separate "mold design" flag, not as a working copy of itself. Remove the entry when the record status becomes `done`.
4. Keep only the last N checkpoints per design. Each one is a full copy of the part.

### B3. The stage order cuts handling features after the flash grooves (Medium)
**Where:** the stage table, rows 4.7 and 4.8 (design.md:242-243).

**Why:**
- The flash-groove logic places runners by reading the parting-face outline and keeping 0.25 in from every feature already there (FG macro 213-231). That is why existing-mold-knowledge 5 #2 orders: dowels, bolts, pry slots, lift notches, TC hole, then flash grooves.
- With 4.8 after 4.7, pry slots, lift notches, jack screws and TC wells are cut after the runners exist.
- They then collide with the runners, or 4.8's own "0.25 in clearance" check fails and 4.7 has to be rebuilt.

**Fix:** reorder the stages:
- 4.5 alignment and fastening;
- 4.6 stops;
- 4.7 handling (pry slots, lift notches, jack screws, TC wells, knockouts);
- 4.8 flash system;
- 4.9 finish.

### B4. Column ray casting needs the multi-hit options (Medium)
**Where:** `mold_part` steps 3, 6 and 7 (design.md:906-937).

**Why:**
- In the SW2026 interop, `swRayPtsOpts_e` is NORMALS 1, TOPOLS 2, ENTRY_EXIT 4, UNBLOCK 8.
- The call shown uses TOPOLS|NORMALS only. Each ray then stops at its first hit, which is enough for undercuts.
- It is not enough for the per-column W intervals that air traps, internal ceilings and core sizes need.
- Hits are read with `IModelDoc2.GetRayIntersectionsPoints` and `GetRayIntersectionsTopology`, not through the extension.
- At 1 mm spacing, a 300 x 300 mm part needs 90,000 rays, against the 20,000-ray budget of spike S12.

**Fix:**
- Column rays use TOPOLS|NORMALS|ENTRY_EXIT|UNBLOCK.
- Spacing = max(sample_mm, √(footprint area / 20,000)), refined near traps.
- Extend spike S12 to confirm that UNBLOCK returns every crossing, in order.

### B5. Hook H1 runs before its input exists (Medium)
**Where:** H1 (design.md:1508); Phase 2 step 9 (design.md:210); Phase 7 (design.md:261-263).

**Why:** H1 sends `exports/part/shrunk-part.step`, but nothing has made it yet:
- the shrink factor comes from `mold_calc process` in Phase 3;
- PART-REF comes from stage 4.1;
- the STEP file comes from `mold_bodies export` in Phase 6.

**Fix:** either of these:
- Run `mold_calc process` in Phase 2. Before H1, export the oriented part with `IBody2.Copy2` → hidden document → STEP (the same path as `export`), and put the scale factors in mold.json.
- Move H1 to after stage 4.1.

### B6. DocumentVisible is a global switch with its own lock (Low-Medium)
**Where:**
- `Mold.InsertPart` (design.md:1002).
- `mold_bodies export` (design.md:966-974).
- `.slddrw` → PDF (design.md:1056-1060).

**Why:**
- SwModelLoader guards `DocumentVisible` with a private `SemaphoreSlim Gate` (SwModelLoader.cs:30-31), because chat attachments are read at any time. DesignSnapshot.cs:120 and 136 also toggle it.
- Three new callers without the gate can leave SolidWorks hiding the user's next opened document, or showing a hidden import.

**Fix:** one internal `HiddenDocuments.Scope()` helper that:
- takes the same Gate;
- restores part and assembly visibility in `finally`;
- is used by every caller.

### B7. Insert pockets made by splitting have sharp inside corners (Medium; machinability)
**Where:** stage 4.4 (design.md:239).

**Why:**
- A pocket made by splitting at the insert's outline inherits its sharp vertical corners.
- An end mill leaves a radius equal to the cutter radius, so the insert won't seat without EDM.

**Fix:**
- Pockets get corner reliefs: a dog-bone, or R ≥ 1.1 x the radius of the cutter that reaches that depth.
- Inserts get matching corner chamfers.
- Add both to acceptance checks A4 and A6.

### B8. The imported part isn't validated (Low-Medium)
**Where:** `Mold.InsertPart`'s self-check, "A new solid body appears" (design.md:1002).

**Why:** STEP and IGES imports often arrive as surface bodies, as several bodies, or with faulty faces. A cavity cut from that is wrong.

**Fix:** require all of these, and otherwise stop and ask:
- exactly one solid body and no surface bodies;
- `IBody2.Check3` clean;
- volume within 0.5% of the volume `ModelFileContext` already read from the source file.

---------------------------------------------------------------------------------------------------

## C. PropertyManager page

### C1. Press units must follow the press, not the document (Medium)
**Where:**
- design.md:705 ("No page-level Units switch").
- design.md:408 (`"Max": 700`).
- D6.

**Why:**
- Gauges in metric shops read bar or kgf/cm², not MPa. Nameplates read US tons or metric tonnes, whatever the document units are.
- A 50 US ton nameplate typed into a metric document becomes 490.3 kN instead of 444.8 kN (+10.2%).
  - The "press too small" margin is then overstated.
  - The recommended gauge is 10% low.
- A Max of 700 MPa accepts "200" bar typed into an MPa box.

**Fix:**
- Give each press field its own unit choice, remembered with the press. pm-page.md 4.3 already proposed a `Display` override for this.
  - Pressure: psi, bar, MPa, kgf/cm².
  - Force: US ton, t, kN.
- Set Max to 100 MPa for gauge readings and 150 MPa for pressure on the part.

### C2. "On the part" hides the press rating, and a tonnage gauge is not an option (Medium)
**Where:** design.md:331-332 and 409-411.

**Why:**
- With "On the part", `press_rated_force` and `press_rated_gauge` are hidden.
- Then there is no A_ram, no "press too small" check (S7) and no gauge setting for the operator.
- Many heat-press gauges have a second scale in tons or lbf.

**Fix:**
- Always show the press rating. It is remembered anyway.
- Add the meaning "Force reading (tons)", where F = the reading.

### C3. Molding pressure and charge excess are not editable (Medium)
**Where:** `Fills` (design.md:386-387).

**Why:**
- The user asked for the material's process data "loaded into editable fields".
- `moldingPressurePsi` sets class A p_LC1 (S5) and class B F_req (S6). It is the most consequential number in the table, and it is not on the page.
- Neither is `flashAllowancePct`, which sets the charge.

**Fix:**
- Add `molding_pressure` (pressure unit) and `charge_excess_pct`.
- The data is stored in psi, so give `Fills` a unit, for example `"moldingPressurePsi.typ|psi"`, or store the value in MPa.
- Put all filled fields in a "Process data (from table)" group, collapsed by default, so the page stays short.

### C4. The group-id fix is not required for this page (Low)
**Where:** design.md:451, which says "This breaks today's ids".

**Why:**
- Group ids run 1..n (ToolPage.cs:160) and control ids start at 10 (ToolPage.cs:53).
- The 7 groups use ids 1-7. A collision needs 10 groups.

**Fix:** keep the change, since it is cheap, but don't present it as a blocker. Test it with a 10-group page (spike S6).

### C5. Choice labels will be clipped (Low)
**Where:** design.md:380-389.

**Why:**
- The PropertyManager is about 300 px wide.
- Part-family labels reach 46 characters ("Cast PU - TDI polyester hot-cast (MBOCA cured)") and grade names reach 61.
- The design added `short` labels for mold grades only.

**Fix:** add `short` labels for families and grades, and show the full name in the tooltip and the info line.

### C6. Inputs dropped from the research change the design (Medium)
**Where:** the inputs table (design.md:309-342), compared with structural.md 2.1.

**Why:** three inputs were left out:
- `stroke`: a mold thinner than daylight − stroke can't be clamped and needs a riser;
- `mold_handling`: slide-in each cycle or stays in the press. It drives the lid-to-base temperature difference (D1);
- `mold_support`: flat on the platen, or on a carrier or rails. This is open item 6.3 #5.

**Fix:**
- Add `stroke`, optional and remembered, with the check H_closed ≥ daylight − stroke + 5 mm.
- Add `mold_handling` and `mold_support` options to the Press group, both remembered. Defaults: "Slide in and out each cycle" and "Flat on platen".

### C7. The page order differs from the user's ranking (Low)
**Where:** design.md:305 moves the "Molded part" group to the top.

**Why:** this is defensible, but the user gave an explicit order.

**Fix:** add it to the 6.3 list so the user confirms it.

---------------------------------------------------------------------------------------------------

## D. Assumptions to change for the user's slide-in process

### D1. Lid-to-base temperature difference and the dowel scheme (Medium)
**Where:**
- S58 (design.md:1347-1348).
- `lidBaseGradient_K: 10` (design.md:583).
- The shop-standard dowel count of 4 (design.md:543).
- A13 (design.md:286).

**Why:**
- The lid sits off the mold on the table while the operator demolds and pours.
- On a 16 x 8 in mold the dowel diagonal is 412 mm. Over that span, a lid-to-base difference of:
  - 30 K gives 0.29 mm;
  - 10 K gives 0.10 mm.
  Both are far above a 0.02-0.05 mm pin clearance.
- The shop's four dowels and A13's round-plus-diamond rule are never reconciled.

**Fix:**
- Default ΔT to 30 K for slide-in molds, and confirm it or measure it with the TC wells.
- Four pins become one round pin plus three slotted along radial lines from it (or round + diamond + two relieved).
- Before overriding the shop standard, find out how the user's existing dowels are fitted.

### D2. Handles and the platen envelope contradict each other (Medium)
**Where:** S9 (design.md:1189), against 5 #12 (design.md:1704) and design-rules 17.

**Why:**
- S9 requires the footprint "including handles" to sit inside the platen size minus 25 mm per side.
- design-rules 17 requires handles to sit outside the platen footprint, or below the platen plane, so the press can't crush them.

**Fix:**
- S9's 25 mm cool-edge margin applies to plates and cavities only.
- Handles must be outside the platen outline, or wholly below the lid's platen face.
- Add this as a new acceptance check, A16.

### D3. Pour time should scale with the charge mass (Low)
**Where:** S68 (design.md:1390) and `pour.secondsPerCavity: 60`.

**Why:**
- 60 s per cavity is the same for a 50 g part and a 2 kg part.
- With the default TDI/MBOCA pot life of 6 min, the S68 limit (0.5 x pot life = 180 s) allows three cavities whatever their size.

**Fix:** t_pour = m_cavity / ṁ + a fixed handling time per cavity, where ṁ is a shop value in g/s (default about 10 g/s by hand).

---------------------------------------------------------------------------------------------------

## E. Missing for the shop operator and the machinist

### E1. Machining information (High)
**Why:**
- The deliverable to the machine shop is one STEP file per body (design 4.2). STEP AP214 carries no tolerances, fits, flatness or finish.
- So the values that decide whether the mold works are lost:
  - land flatness and lapping (0.01-0.02 mm);
  - dowel press and slip fits, and guide bushing fits;
  - the insert's hot-fit interference (0.038 mm on 25 mm);
  - thread and STI tap specs for thread inserts;
  - counterbore sizes for washers;
  - cavity finish (SPI);
  - shut-off pin heights (A12).

**Fix:**
- Write a per-body machining sheet into the record folder, containing:
  - a hole table built from the Hole Wizard data (`IWizardHoleFeatureData2`);
  - fits with their limits;
  - flatness and parallelism of the land and platen faces;
  - finish per face group;
  - stock size with the blank allowance.
- Later: drawings, or STEP AP242 with SolidWorks MBD PMI if the shop uses it.

### E2. Purchased-hardware list (Medium)
**Why:**
- The record lists bolt size and count, but no length and no part numbers.
- Bolt length decides L_e and must not bottom out: the tapped depth must be ≥ L_e + a 0.5·d relief (design-rules 19).
- Hardened ASTM F436 washers do not fit a standard SHCS counterbore. For 1/2 in, the washer OD is 1.0625 in and the counterbore is 0.812 in (mold-materials.json).

**Fix:**
- A hardware BOM with stock lengths, thread-insert part numbers and STI taps, dowel lengths and washers.
- Size counterbores for the washer (washer OD + 0.5 mm), or use narrow SHCS washers.

### E3. Partial cavity loading (Medium for rubber shops)
**Why:**
- Shops often run fewer cavities than the mold has.
- At the same gauge, fewer cavities raise the pressure per cavity and move the load centre.

**Fix:** add a setup-sheet table:
- the gauge setting for each number of active cavities;
- the allowed cavity patterns;
- a minimum ("never run fewer than k"), derived from S8 and S35.

### E4. Slide-in hardware (Low-Medium)
**Fix:** add to the design and the setup sheet:
- a positive locating stop, so the mold lands centred under the ram every cycle (this is what makes S8 true in practice);
- pull handles on the base;
- nothing standing proud of the base's underside;
- the bottom chamfer already in the shop standard.

### E5. Drawing-only, or nothing attached (Medium)
**Why:**
- The brief says the user attaches the part file "and/or" a drawing.
- Phase 1 needs a solid. With only a drawing, or nothing at all, the run has no path.
- macro.cs step 3 also assumes `part_file` is set.

**Fix:** either of these:
- Add a Phase 1a: build the part from the drawing in its own SLDPRT next to the mold, have the user confirm the key dimensions, then Insert Part.
- Stop with a clear message.

### E6. Cure-time note (Low)
**Why:** the "halving per 10 °C" note in setup-sheet section 8 applies to rubber only (materials.md 3).

**Fix:** for liquid PU, print the datasheet demold time instead, with a warning when the press is below `moldTempC.min`.

---------------------------------------------------------------------------------------------------

## F. Simulator hooks

### F1. Geometry references a simulator can return (Medium)
**Why:** persistent-reference bytes are internal to SolidWorks. An external simulator can't produce them, so its findings can't be mapped back.

**Fix:** define finding locations as {bodyId, point, normal, optional STEP face id}. Map them back with `IModelDoc2.ClosestDistance` or rays.

### F2. Process data the simulators need (Medium)
**Why:** the fill, cure and demold simulators need data the record doesn't hold. Only the initial viscosity and the pot life exist today. Missing:
- viscosity against time and temperature;
- gel time and cure kinetics;
- thermal properties of the part and the mold;
- part modulus or hardness, and friction or release, for demolding.

**Fix:** reserve `materials.part.rheology`, `.kinetics`, `.thermal` and `.mechanical`, with status "unknown", so a simulator can ask for them.

### F3. The pressure-field shape can't load walls (Low-Medium)
**Why:** a (u, v, p) grid can't represent wall loads, because the wall pressure varies along W.

**Fix:** use per-face samples {bodyId, point, normal, p}, or (u, v, w, p).

---------------------------------------------------------------------------------------------------

## G. Over-built, or aimed at the wrong target

### G1. The validation effort doesn't cover what actually governs (Medium)
**Why:**
- Case A, the default (6.3 #5), never uses the plate tables. In E2 the walls need 9.3 mm structurally, while the flash system draws 40 mm.
- What does govern is not validated by simulation at all:
  - class B walls, where the 1.2 root factor is uncalibrated;
  - bolts, keys and bearing.
  Yet the user asked for plate thickness and bolt count/size "validated by simulating many examples".
- Sweep 2 checks the table against the same MITC4 solver that produced it, so it validates only the interpolation and the patch-load reduction.
- The physics at τ = 0.3-0.5 rests on Sweep 3, which is "manual, if licensed".

**Fix:**
1. Make a small automated 3D check the release gate, about 20 cases, for the wall-root factor and for plates at τ ≥ 0.3. Use a hexahedral solid model in the sweep console, or SOLIDWORKS Simulation through its API.
2. Validate bolts against the VDI 2230 worked examples, plus an axisymmetric FE model of k_m for steel bolts in aluminium.
3. Build the case B-F plate tables only if 6.3 #5 shows that molds sit on rails or carriers.

### G2. 96 materials, 8 of them for molds the tool can't build (Low)
**Fix:** ship classes A and B. Keep the other rows as advisory (see A10).

### G3. The translucent face overlay (Low)
**Why:** GDI-filling up to 20,000 triangles each time the camera moves will stutter while the user rotates.

**Fix:** for v1, choose one:
- cap at about 3,000 triangles and draw outlines only while the camera moves;
- use `capture_views` highlights instead.

### G4. Auto-approval stays on for 24 hours (Medium; safety)
**Where:** design.md:818-819.

**Why:** for 24 h, any follow-up in that chat, even an unrelated one, runs `execute_csharp` without approval at the max tier.

**Fix:**
- Keep the tool run's settings only while mold.json's status is not `done` and the active document is the mold.
- End them at Phase 6, or after 2 h idle.
- Show the state in the banner.

---------------------------------------------------------------------------------------------------

## H. Small errors and inconsistencies

1. **E3 (design.md:1436)** says "232 MPa against a fatigue allowable of 231 MPa, so it passes". It fails by 0.4%, and S23 raises w to 8.66 mm (see the check script).
2. **E11 (design.md:1444)** has expected values "defined ... with the reference implementation", so the test can only confirm itself. Derive the piece count and the masses by hand first.
3. **A10 (design.md:283)** requires "a pitch of 2 in or less". That is stricter than the user's rule, n = max(1, round(L/pitch)): L = 2.9 in gives one runner at 2.9 in. It also ignores dropped runners. Check the rule as implemented and list any drops.
4. **S56 (design.md:1341)** uses the bolt-thread area π·d_minor·L_e·0.75. The aluminium internal thread shears at about π·d·L_e·0.88, which is 1.45 times the area for 1/2-13. The current form is conservative; label it so nobody "fixes" it the wrong way.
5. **S42-S43:** for tapped holes, use Shigley's effective grip, l' = h + t₂/2 when t₂ < d, otherwise h + d/2.
6. **The record example (design.md:1587)** shows `w_mm 25.4` with `scale 1.017387`; see A6.
7. **Add to the confirm list (6.3):**
   - the shrink basis;
   - how the lid limit is set;
   - the flash-gap classes and budget;
   - the slide-in temperature difference and the dowel scheme;
   - which process classes are in scope;
   - the four-dowel shop standard against round + diamond.

---------------------------------------------------------------------------------------------------

## Checked and correct (no change needed)

- **Worked examples.** E1, E2, E4 (nominal), E5, E6, E8, E10 and T16 reproduce with check/method_check.py.
- **SolidWorks 2026 interop.** These exist with the signatures the design uses:
  - `IFeatureManager.HoleWizard5` takes `FeatureScope` and `AutoSelect` arguments, and `IWizardHoleFeatureData2.FeatureScopeBodies` exists;
  - `IFeatureManager.InsertSubFolder` and `IBody2.SetMaterialProperty`;
  - `IModeler.CheckInterference3`, with `swCheckInterferenceOption_e.swBodyInterference_ReturnInterferingObject` = 4;
  - `IModelDoc2.ClosestDistance`;
  - `IPartDoc.InsertPart3`, with `swInsertPartImportSolids` = 1 and `swInsertPartDontZoomAll` = 262144;
  - `PreSplitBody2` and `PostSplitBody2`;
  - `IPartDoc.CreateFeatureFromBody3`;
  - `IModelDocExtension.RayIntersections`, with 7 arguments.
- **Prompt caching (design.md:821).** 1-hour cache writes cost 2x, a read refreshes the timer, and Opus 5.5 reads cost $0.20/MTok, which is 0.05x. runAgent.ts:239 has only the top-level marker, so a 1-hour TTL has no ordering conflict.
- **PDF limits.** 32 MB per request and 600 pages on 1M-context models.
- **Formulas.** Shrink Formula 2 is physically right for steel-basis data. The S59 Lamé expression and the S60 dowel allowance are right. S66 correctly sizes the charge by part mass, not by the scaled cavity volume.

# Integration notes from track A2 (FE solvers, sweeps, 3D validation)

Everything here is backed by files in this branch:
- `src/SwAgent/Mold/Structural/coefficients.csv` (the header lists every gate result),
- `tools/builtin/design-compression-mold/data/validation/solid3d-summary.json`, `montecarlo-summary.json`, `sizing-table.csv`.

Regenerate them with `tools/dev/MoldSweep` (see its README).

## 1. For A1 (calculator): the coefficient table

`coefficients.csv` replaces the provisional file. It keeps the same columns: `bc,aspect,tau,nu,loadFrac,alpha,beta,wMaxX,wMaxY`.
- **Comment lines** start with `#`. One of them is `# sha256: <hex>`, the SHA-256 of the header line plus the data rows, each followed by `\n`. `CoefficientSweep.Sha256` computes it.
- **Size.** The file has 11,328 rows and is 573 KB, so it adds about 0.6 MB to SwAgent.dll as an embedded resource.
- **`bc` names** are the design's: `SS4`, `C4`, `Rails`, `C2S2`, `Wall-C`, `Wall-S`, `Overhang`, `Pillar`. Each geometry is spelled out in the header and in `CoefficientSweep`:
  - `C2S2` has its y edges (length a) clamped and its x edges simply supported.
  - `Rails` is SS on y = 0 and y = b, with free ends.
  - `Overhang` uses b = the overhang length c and aspect = width / c.
  - Walls use b = h, aspect = L/h and tau = w/h.
- **SS4 and C4.** Use aspect >= 1 (b = the short side). The rows below 1 exist because the other sets need them.
- **nu** is 0.29 and 0.33 only. Interpolate linearly between them, and clamp outside.
- **Grid.** It is denser than design 3.20, so that interpolation between rows stays under 1%.
  - Aspects: 0.5, 0.5625, 0.625, 0.6875, 0.75, 0.875, 1.0, 1.1, ... 6.0. Walls go on to 7, 8, 10 and 12.
  - tau: 0.01, 0.05, 0.075, 0.1, 0.15, 0.2, 0.25, 0.3, 0.35, 0.4, 0.45, 0.5.
  - loadFrac: 0.25, 0.5, 0.75 and 1, for SS4, C4, Rails and C2S2 only.
- **Interpolation.** Bilinear in (aspect, tau²) reads up to 0.88% below the FE between rows; this was checked at 4,092 midpoints, and the gate is in the header. **Multiply the interpolated alpha and beta by 1.01.**
  - The Monte Carlo is accepted with that allowance: max r_w 1.000, max r_s 0.999.
  - Without it, walls reach r_w 1.007.
- **Walls past L/h 12** (critique A4). Use alpha = max(alpha at 12, 1.5(1 - nu²) + 1.2(1 + nu)·tau²) and beta = max(beta at 12, 3.0).
  - Do not hold the L/h 4 row; it is not conservative.
  - Thick walls (tau >= 0.25) overshoot the strip by up to 1.0% around L/h 6, then settle back onto it. This is a Mindlin effect next to the sides, and the rows contain it.
- **beta near clamped-free corners.** For Wall-C and Overhang, beta leaves out the plate within 0.1 b of a clamped-free corner. The Mindlin value there is singular and grows without bound under refinement. Real corners are radiused, and K_f covers them.

## 2. For A1: the method changes that the validation found

1. **Wall-root factor (S21): replace the constant 1.2 with 1 + 2.6·(w/h).**
   - **Evidence.** 14 3D solid pocket models give 3D / plate = 1.19 to 2.16, and the slope (ratio - 1)/(w/h) is 1.25 to 2.57. With a rigid floor the ratio is about 1.0 for L/h >= 3, so the plate wall itself is right. What the clamped root misses is the rotation of the wall root in the block (O'Donnell's foundation effect, which is linear in w/h).
   - The constant 1.2 only holds below w/h = 0.08.
   - The factor applies to deflection only, not to stress.
   - **Effect, using my table at nu 0.33:**

     | Example | Old | New |
     |---|---|---|
     | E2 structural wall (L/h 4 row) | 9.26 mm | 11.7 mm; 12.3 mm with the true L/h 6 |
     | E3 P20 | 8.66 mm | about 10.7 to 11.4 mm, and stress no longer governs (150 MPa) |
     | E3 6061 | | w_defl 17.8 mm (w/h 0.71), still flagged as w/h > 0.5 |

   - **E3's expected values in the examples must be re-derived if you adopt this.** The E2 drawn wall (40 mm) is unchanged.
   - Suggested sizing-defaults entry: `"wallRootFactor": { "form": "1 + c w/h", "c": 2.6, "source": "data/validation/solid3d-summary.json" }`, or a plain `wallRootSlope: 2.6`.
2. **Rails / one-way spans: use the table beta, not 0.75.**
   - The thin free-edge moment is 0.787 to 0.798·q·b²/6 (the Levy series, gated to 0.15%), which is 5-6.5% over the beam.
   - The structural.md 4.4 remark that 0.78-0.80 is a recovery artefact is wrong.
3. **Case E (pillars): do not treat the panel as C4.**
   - At the same spacing, a pillar cell deflects about 2.8 times more than a C4 panel.
   - Panels next to the plate's simply supported edges deflect up to about 11% more than the periodic Pillar cell, because they rotate freely like end spans.
   - In the Monte Carlo, design-as-written (C4) reaches r_w 31 on pillar plates. The recommended stub sizes case E with the plate FE of the actual layout (`MindlinPlateSolver`, rigid heads); the design already plans that FE check in `size`.
   - The Pillar rows assume rigid square heads of side 0.2 × the short spacing. Smaller heads make the rows unconservative.
   - Pillar beta is the mid-span moment only. The hogging over a head is singular in plate theory, so check the plate over a pillar by bearing or punching.
   - **Pillar shortening F·L/(E·A) must be added to the gap.** At molding pressures it usually dominates. For example, 20 MPa on a 150 mm pitch through a Ø40 x 100 mm steel pillar shortens it by 0.18 mm, against delta_allow 0.02 to 0.05.
4. **Thick plates.** Against 3D (diaphragm or clamped side faces), the Mindlin tables are conservative by 4-9% at tau 0.3-0.5 and by 0-2% at tau 0.1. The tau = 0.5 cut-off stands.
5. **Bolt member stiffness.** With a rigid head, the axisymmetric FE lands within -14% to +6% of Wileman for d/l 0.1-1.5 (within 6% at d/l 0.5-1.5). Under a uniform head pressure it is up to 22% softer, at long grips.
   - Keep Wileman: it is conservative for the thermal gain and the hot load.
   - It is slightly unconservative for C·P. In E4, C rises from 0.306 to 0.352 with the softer FE value, and C·P gains about 330 lbf, which is under 2% of F_p.
   - My VDI 2230 cone formula is quoted from memory and is labelled "verify" in the summary. Do not cite it without checking.
6. **Plates on rails (B) and over an opening (C)** sized by the centred-patch table are conservative: median r_w 0.58-0.62, so the plates come out about 17% thicker than needed. Sizing them with the layout FE would remove that.

## 3. For A1 and the lead: the FE solver API (`SwAgent.Mold.Structural.Fe`, pure, no SolidWorks types)

```csharp
var m = PlateModel.Rect(a_mm, b_mm, t_mm, E_MPa, nu, EdgeBc.Free, EdgeBc.Free, EdgeBc.SimpleHard, EdgeBc.SimpleHard);
m.Pressures.Add(new PressurePatch(x1, x2, y1, y2, p_MPa));      // cavities (+ land)
m.Lines.Add(new LineSupport('y', y_rail));                         // rails; m.Fixed for rigid heads/pads; m.Springs; m.Foundations (Winkler, unilateral)
PlateResult r = MindlinPlateSolver.Solve(m, ct);                    // r.WMax, r.SigmaMax, r.MaxW(pred, out at), r.MaxMoment(pred, out at), r.EdgeMoments, r.WAt(x, y)
Navier.SimplySupported(a, b, t, E, nu, q, x, y, shear: true);       // reference
```

- **Mesh.** The default is 24 elements on the short side; grid lines pass through every patch, support and region edge. `ElementsOnShortSide` or `ElementSize` changes it.
- **Speed.** A typical layout plate takes 5-50 ms in Release.
- **Sign convention.** w is positive along the pressure. Moments are sagging-positive.
- **EdgeBc.Symmetric** (normal rotation held) is available for mirror planes.
- **Where to find things.**
  - `ClosedForm.CantileverStrip(nu, tau)` gives the A4 strip.
  - `Levy.RailsFreeEdge(aspect, nu)` gives the exact free-edge values.
  - `JointStiffness` holds Wileman, the frustum, the VDI cone and the bolt stiffness.
  - T17 in FeTests uses `JointStiffness.Wileman`.

## 4. For the lead: Monte Carlo after integration

- `MonteCarloHarness.Run(n, seed, Func<JObject,JObject> sizing)`. The input and output JSON are documented at the top of `MonteCarlo.cs` (schema `unify3d.mold-mc/1`: support A/B/C/E, plate, cavities, land, pressure, material id plus tempC, cycles, kf, delta, rails/opening/pillars).
- The output must carry `E_MPa`, `nu`, `sigma_allow_MPa`, `plate {t_mm, governs}` and `walls [{cavity, side: length|width, w_mm, governs}]`.
- **To switch to A1's calculator:**
  1. Write an adapter from this JSON to `MoldCalc.Run("size", design, ...)` and back.
  2. Replace `StubSizing.Create(...)` in `tools/dev/MoldSweep/Program.cs` and in `SweepTests.Monte_carlo_smoke_E12`.
  3. Rerun `MoldSweep montecarlo`.
- `StubSizing` (`src/SwAgent/Mold/Structural/Fe/Sweep/StubSizing.cs`) is a stand-in. **DELETE OR REPLACE AT INTEGRATION.** Its grade data are a small built-in copy, not MoldData.
- The summary also records the same molds sized "as written" in design.md (root 1.2, C4 pillars, no allowance): max r_w 31, max r_s 2.9, 3,668 failures out of 7,095 checks. That is the reason for section 2.

## 5. Files and ownership

- `src/SwAgent/Mold/Structural/coefficients.csv` is mine. A1 owns the `<EmbeddedResource>` ItemGroup that embeds it. The tests read the file from the source tree, so they don't depend on that ItemGroup.
- `tools/builtin/design-compression-mold/data/validation/{sizing-table.csv, montecarlo-summary.json, solid3d-summary.json}` ship with the tool through the existing `builtin-tools` None-include. Track E's `data/README.md` may want one line per file.
- The per-check `montecarlo.csv` stays in `tools/dev/MoldSweep/results/` and is git-ignored. It is 1.6 MB and is not shipped.
- `tests/SwAgent.Tests/Mold/FeTests.cs`, `FeSolidTests.cs` and `SweepTests.cs` add about 5 s to `dotnet test`. The `Category=Sweep` tests run only with `MOLD_SWEEP=1`.

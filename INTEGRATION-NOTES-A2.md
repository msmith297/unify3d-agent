# Integration notes from track A2 (FE solvers, sweeps, 3D validation)

Everything here is backed by files in this branch:
- `src/SwAgent/Mold/Structural/coefficients.csv` (the header lists every gate result and the factors a reader must apply),
- `tools/builtin/design-compression-mold/data/validation/solid3d-summary.json`, `montecarlo-summary.json`, `sizing-table.csv`.

Regenerate them with `tools/dev/MoldSweep` (see its README; `all` takes about 30 minutes).

## 0. What changed since the first A2 commit (review fixes)

- **Pillar beta** now includes the hogging over the heads (it was the mid-span moment only, about 3 x too low): the normal moment on
  the face of a clamped rigid head, from the rotation reactions, read 0.05 b or more from the head corner. It converges (0.1-0.3% from
  48 to 96 elements at t/b >= 0.05). A 3D check of the pillar cell sets a **pillar-head factor 1.2 on Pillar alpha** (the plate
  misses the bearing compression over the head; 3D / plate up to 1.16).
- **Monte Carlo**: the truth never shares a mesh with the sizing (truth 64 elements, heads 8 wide; layout-FE sizing extrapolates
  24/3 and 48/6); sizing exceptions are failures; acceptance needs coverage and a median per group; draws stay in the method's range.
- **Wall-root slope 2.8** (was 2.6): three uniformly refined 3D meshes, extrapolated with the observed (about first) order.
- **Mechanisms** are caught pivot by pivot against each pivot's own diagonal (hinge lines, one supported edge, symmetry-only).
- **VDI 2230**: the cone model (through-bolted and tapped, full cone and cone plus sleeve), the bolt resilience, load factor, embedding
  loss and preload band are code (`Vdi2230`), verified against a published worked example end to end (0.3%), and compared with a new
  tapped-joint axisymmetric FE.
- Smaller: moment sums include rotation reactions; plate inputs are validated; a memory cap; pressure fields are sub-sampled; the CSV
  header carries `# version:` on line 3 and `# allowance: 1.01`; the sizing table's wall section uses cavity depths.

## 1. For A1 (calculator): the coefficient table

`coefficients.csv` replaces the provisional file and keeps its columns: `bc,aspect,tau,nu,loadFrac,alpha,beta,wMaxX,wMaxY`.
- **Header lines a reader uses** (all start with `#`):
  - line 3 is always `# version: a2-2` (`Coefficients.Version` and `tools/dev/mold-data/examples_check.py` read it);
  - `# allowance: 1.01`, the number alone: multiply interpolated alpha and beta by it (A1's reader does);
  - `# wall root: ...` and `# pillar head: ...`: the 3D factors (section 2);
  - `# sha256: <hex>` = SHA-256 of the header row plus the data rows, each followed by `\n` (A1's reader checks it).
- **Size**: 11,328 rows, 0.6 MB embedded.
- **`bc` names**: `SS4`, `C4`, `Rails`, `C2S2`, `Wall-C`, `Wall-S`, `Overhang`, `Pillar` (geometry in the header and in `CoefficientSweep`).
  - `C2S2`: y edges (length a) clamped, x edges simply supported. `Rails`: SS on y = 0 and y = b, free ends.
  - `Overhang`: b = the overhang length, aspect = width / overhang. Walls: b = h, aspect = L/h, tau = w/h.
  - `Pillar`: the periodic cell a x b with rigid square heads of side 0.2 b at the corners; b is the y spacing.
- **Folding**: only SS4 and C4 may read aspect < 1 as 1/aspect. For Rails, C2S2, Overhang and Pillar an aspect below 1 is a different
  plate (A1's reader already folds only SS4 and C4).
- **nu** is 0.29 and 0.33 only; interpolate linearly and clamp. I did not add nu 0.30 rows (the review suggested considering it):
  alpha goes as 1 - nu², which is linear in nu to 0.03% at 0.30, so the rows would add 0.3 MB for nothing; a reader that picks the
  nearest nu (A1's old one) is what was wrong.
- **Grid**: aspects 0.5-6 (walls to 7, 8, 10, 12), tau 0.01-0.5 (12 values), loadFrac 0.25/0.5/0.75/1 for SS4, C4, Rails, C2S2.
- **Interpolation**: bilinear in (aspect, tau²) reads at most 0.88% below the FE between rows (4,092 midpoints): hence the 1.01.
- **Walls past L/h 12**: alpha = max(alpha at 12, 1.5(1 - nu²) + 1.2(1 + nu)tau²), beta = max(beta at 12, 3). Thick walls overshoot the
  strip by up to 1.0% around L/h 6 (a Mindlin effect, real); A1's running maximum over L/h keeps that.
- **beta near clamped-free corners** (Wall-C, Overhang) leaves out 0.1 b around the corner (singular; real corners have radii and K_f).

## 2. For A1: method changes the validation found (blocking for the Monte Carlo against A1's sizing)

A1 at 1486b1c still has `wallRootFactor` 1.2 (sizing-defaults.json, SizingDefaults.Builtin.cs, Sizing.cs:335) and
`Plates.BcFor(Support.E) = "C4"`. Both are unsafe (section 4: as written, walls reach r_w 1.9 and pillar plates 13); the Monte Carlo
against A1's `size` will fail until they change.

1. **Wall-root factor (S21): 1 + 2.8·(w/h)**, not 1.2 (`"wallRootFactor": { "form": "1 + c w/h", "c": 2.8 }` or `wallRootSlope: 2.8`).
   - 14 3D pocket models, three meshes each (h/12, h/16, h/24, uniform refinements of one base mesh), extrapolated with the
     observed order (about 1: the root is a re-entrant corner). 3D / plate 1.24 to 2.23; the largest slope (ratio - 1)/(w/h) is 2.78
     (E2 cavity, L/h 8 pocket), rounded up to 2.8. The constant 1.2 holds only below w/h 0.07.
   - It is a deflection factor only: the 3D root moment at mid-length is 0.50-0.96 x the plate's (a compliant root sheds moment to
     the sides), so Wall-C beta stands.
   - E2 and E3 expected wall values change if A1 adopts it; re-derive them (examples_check.py).
2. **Rails / one-way spans**: use the table beta, not 0.75 (thin free edges carry 0.787-0.798, Levy series, gated to 0.15%).
3. **Case E (pillars)**:
   - Never as C4: a pillar cell deflects about 2.8 x a C4 panel of the same spacing (design-as-written reaches r_w 13).
   - Pillar rows: **alpha x 1.2** (the `# pillar head:` line), beta as tabulated (now with the hogging). For t/b >= 0.1 that beta
     covers the 3D top-surface peak (3D / beta <= 0.92). Thinner plates peak above the head corner (1.07 at t/b 0.05, 1.9 at 0.02):
     radius or chamfer the head corners, apply K_f, or keep t/b >= 0.1 over rigid heads.
   - **The Pillar rows alone are not enough for a real layout**: panels next to the plate's simply supported edges deflect more
     than the periodic cell. Sized by the rows, 2000 random molds reach r_w 1.31 on pillar plates. Finish case E with the layout
     plate FE (section 3), which is what the recommended stub does (max r_w 0.995).
   - **Pillar shortening F·L/(E·A) belongs in the gap.** At molding pressures it often dominates: 20 MPa on a 150 mm pitch through a
     Ø40 x 100 mm steel pillar shortens it by 0.18 mm, against delta_allow 0.02-0.05.
4. **Plates on rails (B) and over an opening (C)**: the centred-patch table is safe but about 17% too thick (median r_w 0.59 and 0.65
   where deflection governs; the Monte Carlo's per-group 0.75 criterion fails). The recommended path, as A1's notes already plan
   ("the plate FE check of the planned layout that `size` should run"): the table's thickness as the first guess, then the layout
   plate FE to finish it (three or four solves, under 0.2 s). The stub's `LayoutFe` shows the method: two meshes (24 and 48 elements
   on the short side; pillar heads 3 and 6 elements wide), Richardson to the converged value, sized to 0.995 of the limits.
5. **Thick plates**: against 3D the Mindlin rows are conservative by 3-9% at tau 0.3-0.5 (gate: 3D never above Mindlin by more
   than 1%). The tau = 0.5 cut-off stands.
6. **Bolts** (critique H5: the mold plates are screwed into tapped holes):
   - Use `Vdi2230` with `Joint.Tapped` for the plates: tan phi = 0.348 + 0.013 ln beta_L + 0.193 ln y, w = 2, plus delta_M =
     0.33 d / (E_M A_N) for the tapped zone. The tapped-joint FE (plate on a tapped base, stiff washer, thread engaged 1 d) lands
     at 0.94-1.25 x VDI's delta_P + delta_M; VDI's delta_P alone is 1.3-1.9 x stiffer than the head-to-thread approach.
   - Through-bolted joints: the FE lands at 0.86-1.04 x the VDI cone (VDI the stiffer for wide plates), Wileman within -14%/+6%
     with a rigid head. Wileman describes two equal through-bolted plates, not a tapped plate.
   - The VDI worked example (Hochschule Anhalt, Berechnungsbeispiel Hydraulikzylinder: M12, l_K 42, D_A 80) is reproduced end to
     end within 0.3%: tan phi 0.566, D_A,Gr 44.9 mm, delta_P 0.3546e-6 mm/N, delta_S 2.88e-6, Phi_K 0.11, F_Z 2.475 kN, F_Mmin
     27.6 kN, F_Mmax 46.9 kN (test `Vdi2230_worked_example_is_reproduced_end_to_end`). The source prints delta_S 2.95e-6 on its bolt
     slide but its own F_Z needs 2.88e-6, which its listed sections give.
   - No published ESV-only worked example was available offline; the ESV cone is checked against the FE instead (above).
   - E4 with the uniform-pressure FE instead of Wileman: C rises from 0.306 to 0.352 and C·P by about 330 lbf (under 2% of F_p).

## 3. For A1 and the lead: the FE solver API (`SwAgent.Mold.Structural.Fe`, pure, no SolidWorks types)

```csharp
var m = PlateModel.Rect(a_mm, b_mm, t_mm, E_MPa, nu, EdgeBc.Free, EdgeBc.Free, EdgeBc.SimpleHard, EdgeBc.SimpleHard);
m.Pressures.Add(new PressurePatch(x1, x2, y1, y2, p_MPa));       // cavities (+ land); x1 < x2, y1 < y2, on the plate
m.Lines.Add(new LineSupport('y', y_rail));                          // rails; m.Fixed for heads/pads (Clamped: also the rotations)
PlateResult r = MindlinPlateSolver.Solve(m, ct);                     // r.WMax, r.SigmaMax, r.MaxW(pred, out at), r.MaxMoment(pred, out at), r.EdgeMoments, r.WAt(x, y)
double hog = CoefficientSweep.HeadFaceMoment(r, m, rc);              // hogging on clamped heads' faces, rc from the head corners
Navier.SimplySupported(a, b, t, E, nu, q, x, y, shear: true);        // reference
Vdi2230.PlateResilience(Vdi2230.Joint.Tapped, E, lK, dW, dh, DA, out tanPhi, out DAGr); // + BoltResilience, LoadFactor, EmbeddingLoss, PreloadBand
```

- **Mesh**: 24 elements on the short side by default; grid lines pass through every patch, support and region edge
  (`XLines`/`YLines` add more, e.g. cavity outlines so the land ring has nodes on both edges). `ElementsOnShortSide` / `ElementSize`.
- **Throws** (InvalidOperationException) for a mechanism (message contains "mechanism"), for a model above `MaxMemoryMB` (400 MB
  of band storage by default; it runs inside SolidWorks), and (ArgumentException) for reversed patches or supports off the plate.
- **Speed**: a layout plate takes 5-50 ms in Release. **Sign convention**: w along the pressure, moments sagging-positive.
- `PressureField` (e.g. a cavity mask) is sampled 8 x 8 per element. `EdgeBc.Symmetric` holds the normal rotation (mirror planes).
- `ClosedForm.CantileverStrip(nu, tau)`, `Levy.RailsFreeEdge(aspect, nu)`, `JointStiffness` (Wileman, frustum, bolt).

## 4. For the lead: Monte Carlo after integration

- `MonteCarloHarness.Run(n, seed, Func<JObject,JObject> sizing, truthMesh)`; input/output JSON documented at the top of
  `MonteCarlo.cs` (schema `unify3d.mold-mc/1`). The output must carry `E_MPa`, `nu`, `sigma_allow_MPa`, `plate {t_mm, governs}`,
  `walls [{cavity, side: length|width, w_mm, governs}]`. A1's notes give the call: `MoldCalc.Run("size", design, null, overrides,
  data).Json` (walls in `structural.results.walls[]`); an adapter maps between the two schemas.
- **Acceptance** (summary schema `unify3d.mold-montecarlo/2`): max r_w and r_s <= 1; no sizing or truth errors (a throwing adapter
  fails the run); per group (plates on A, B, C, E; walls) at least 10 live checks and 50% coverage; per group median r_w >= 0.75
  where deflection governs. `acceptance_failures` lists what does not hold.
- **To switch to A1's calculator**: write the adapter, replace `StubSizing.Create(...)` in `tools/dev/MoldSweep/Program.cs` and in
  `SweepTests` (E12 smoke, Sweep2), call `StubSizing.Calibrate`'s equivalent in A1 (root slope 2.8, pillar factor 1.2), rerun
  `MoldSweep montecarlo`. Expect it to fail on walls and case E until section 2 is adopted, and on B/C efficiency until the layout
  FE finish is in `size`.
- `StubSizing` is a stand-in: **DELETE OR REPLACE AT INTEGRATION**. Its grade data are a small built-in copy, not MoldData.
- **Shipped run** (2000 molds, seed 1, 30 min with the comparisons): recommended sizing max r_w 0.997, max r_s 0.998, medians
  0.991-0.994, coverage 63-100%, accepted. The same molds by the table alone: max r_w 1.31 (pillar plates), B/C medians 0.59/0.65,
  not accepted. As design.md was written (root 1.2, C4 pillars, no allowance): far from accepted (see the summary).
- The truth's walls use the 3D root factor and its pillar plates the 3D pillar factor, the same factors the sizing uses: the
  Monte Carlo checks the table, its interpolation and the fixed point; the factors themselves are checked by solid3d.
- `SweepTests.Monte_carlo_smoke_E12` runs 60 molds on a 40-element truth (default test run); the 2000-mold acceptance runs with
  `MOLD_SWEEP=1` or `MoldSweep montecarlo`.

## 5. For A1 and the lead: merging A1 then A2

- `coefficients.csv` is an add/add conflict: take A2's file. Then refresh `examples.json` with
  `python -I tools/dev/mold-data/examples_check.py` (it records the table version from line 3, now `version: a2-2`).
- See the merge test result in the A2 handover (lead's report): which A1 tests pass on the merged tree.
- `T17` and `T18` in FeTests check the hand arithmetic of structural.md 6.3 and the press example (member and bolt stiffness from
  production code); retarget them to A1's bolt and press functions, or retire them for A1's E1/E4 tests.

## 6. Files and ownership

- `src/SwAgent/Mold/Structural/coefficients.csv` is A2's; A1 owns the `<EmbeddedResource>` ItemGroup. The tests read the file
  from the source tree, so they don't depend on that ItemGroup.
- `tools/builtin/design-compression-mold/data/validation/{sizing-table.csv, montecarlo-summary.json, solid3d-summary.json}` ship
  with the tool through the existing `builtin-tools` None-include. Track E's `data/README.md` may want one line per file.
- The per-check `montecarlo.csv` stays in `tools/dev/MoldSweep/results/` (git-ignored, 2 MB).
- `tests/SwAgent.Tests/Mold/FeTests.cs`, `FeSolidTests.cs` and `SweepTests.cs` add about 1-2 minutes to a Debug `dotnet test`
  (most of it the E12 smoke run). `Category=Sweep` tests run only with `MOLD_SWEEP=1`.

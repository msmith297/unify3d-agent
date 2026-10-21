# MoldSweep

Dev-only console behind the structural tables of the built-in tool `design-compression-mold` (design 2.4 and 3.20). Not shipped.
The solvers and the sweep logic live in `src/SwAgent/Mold/Structural/Fe/` (namespace `SwAgent.Mold.Structural.Fe`), so the
headless tests can run them; this project is only the command line.

Build and run in Release (the sweeps are about 4x faster than Debug) - from a scratch copy, never in the checkout SolidWorks loads
the add-in from (a Release build of this project builds `SwAgent` into `src/SwAgent/bin/Release`; see the end of this file):

```
dotnet build <scratch>/tools/dev/MoldSweep/MoldSweep.csproj -c Release
<scratch>/tools/dev/MoldSweep/bin/Release/net48/MoldSweep.exe all --root <scratch>
```

| Command | Writes | Time (16 threads) |
|---|---|---|
| `solid3d [--quick]` | `tools/builtin/design-compression-mold/data/validation/solid3d-summary.json`: wall-root factor (14 pocket models, three meshes, extrapolated), thick plates (10), pillar cells (14), bolted joints (Wileman 10, VDI 2230 cone 32, the VDI worked example) | 5 min |
| `coefficients [--elements 48]` | `src/SwAgent/Mold/Structural/coefficients.csv` (Sweep 1, 11,328 rows) with its gates, the allowance and the 3D factors in the header | 2 min |
| `table` | `.../data/validation/sizing-table.csv` (thickness vs pressure, span or cavity depth, material, deflection limit) | 1 s |
| `montecarlo [--n 2000] [--seed 1]` | `.../data/validation/montecarlo-summary.json` and `results/montecarlo.csv` (Sweep 2: the recommended sizing, plus the table-only and design-as-written sizings of the same molds for comparison) | 20 min |
| `stockywalls` | `.../data/validation/stocky-walls.csv` (Sweep 4: the wall sizing - side condition, root factor x 1.06 plus the floor-stretch term - against the 3D pocket: 1,920 cases at h/8 and h/12, L/h 0.5-12, cavity width 0.5-8 h, w/h 0.3-3, floor 0.25-1 h, nu 0.29 and 0.33; plus 630 short narrow pockets on fine meshes, L/h 0.5-3.33, cavity 0.25-1.5 h, w/h 0.1-0.5; fails when a case bulges more than the calculator / 1.02) | 10 min |
| `all` | the four above, in that order (`coefficients` reads the 3D factors, `table` and `montecarlo` read the coefficients and the 3D factors) | 30 min |

The exit code is 1 when a gate fails; the failing gates are printed and written into the outputs.

The same checks run from the test project: `dotnet test tests/SwAgent.Tests` covers T1-T17, the 3D and axisymmetric solvers, the VDI
2230 worked example, a 50-row regeneration of the CSV, the data gates and a 60-mold Monte Carlo smoke run of the calculator (E12, on a 40-element truth
mesh). With `MOLD_SWEEP=1` set, the tests in `Category=Sweep` rerun the full sweeps (an hour or more in Debug).

The Monte Carlo sizes each random mold through a delegate `Func<JObject, JObject>` (schema in `MonteCarloHarness`): the calculator
itself, `MoldCalc.Run("size")`, through the adapter `MoldCalcSizing` (one mold_calc design per cavity for its walls; the first also
carries the drawn plate layout and supports as overrides, so `size` finishes the plate with the same layout FE, `LayoutPlate`). The
calculator takes the wall-root slope and the pillar-head factor from `sizing-defaults.json`; `montecarlo` refuses to accept a run
when they differ from `solid3d-summary.json`. `StubSizing` stays for the comparison sizings in the summary (table only, design as
written) and the solver-only tests. The calculator reads the coefficient table embedded at build time: after `coefficients`,
rebuild before `montecarlo`.

Build it without touching the add-in's own Release output (SolidWorks loads `src/SwAgent/bin/Release`): a Release build of this
project also builds `SwAgent` in Release. Copy `src/SwAgent`, `tools/dev/MoldSweep`, `tools/builtin`, `assets/brand` and
`nuget.config` to a scratch folder (drop the `SwAgent.Speech` project reference there), build and run it there with `--root <scratch>`,
and copy `data/validation/montecarlo-summary.json` back. The truth
never uses a mesh the sizing uses (truth 64 elements on the short side and pillar heads 8 elements wide; the layout-FE sizing
extrapolates from 24/3 and 48/6), and a run is accepted only if it is safe, has no errors, covers every group (plates on supports
A, B, C, E; walls) and is not wasteful in any group.

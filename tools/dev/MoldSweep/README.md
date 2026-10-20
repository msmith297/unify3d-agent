# MoldSweep

Dev-only console behind the structural tables of the built-in tool `design-compression-mold` (design 2.4 and 3.20). Not shipped.
The solvers and the sweep logic live in `src/SwAgent/Mold/Structural/Fe/` (namespace `SwAgent.Mold.Structural.Fe`), so the
headless tests can run them; this project is only the command line.

Build and run from the repository root (Release: the sweeps are about 4x faster than Debug):

```
dotnet build tools/dev/MoldSweep/MoldSweep.csproj -c Release
tools/dev/MoldSweep/bin/Release/net48/MoldSweep.exe all
```

| Command | Writes | Time (16 threads) |
|---|---|---|
| `solid3d [--quick]` | `tools/builtin/design-compression-mold/data/validation/solid3d-summary.json`: wall-root factor (14 pocket models, three meshes, extrapolated), thick plates (10), pillar cells (14), bolted joints (Wileman 10, VDI 2230 cone 32, the VDI worked example) | 5 min |
| `coefficients [--elements 48]` | `src/SwAgent/Mold/Structural/coefficients.csv` (Sweep 1, 11,328 rows) with its gates, the allowance and the 3D factors in the header | 2 min |
| `table` | `.../data/validation/sizing-table.csv` (thickness vs pressure, span or cavity depth, material, deflection limit) | 1 s |
| `montecarlo [--n 2000] [--seed 1]` | `.../data/validation/montecarlo-summary.json` and `results/montecarlo.csv` (Sweep 2: the recommended sizing, plus the table-only and design-as-written sizings of the same molds for comparison) | 20 min |
| `all` | the four above, in that order (`coefficients` reads the 3D factors, `table` and `montecarlo` read the coefficients and the 3D factors) | 30 min |

The exit code is 1 when a gate fails; the failing gates are printed and written into the outputs.

The same checks run from the test project: `dotnet test tests/SwAgent.Tests` covers T1-T18, the 3D and axisymmetric solvers, the VDI
2230 worked example, a 50-row regeneration of the CSV, the data gates and a 60-mold Monte Carlo smoke run (E12, on a 40-element truth
mesh). With `MOLD_SWEEP=1` set, the tests in `Category=Sweep` rerun the full sweeps (an hour or more in Debug).

The Monte Carlo sizes each random mold through a delegate `Func<JObject, JObject>` (schema in `MonteCarloHarness`). Until the
calculator (`SwAgent.Mold` sizing) is integrated, `StubSizing` stands in for it; at integration replace it with an adapter. The truth
never uses a mesh the sizing uses (truth 64 elements on the short side and pillar heads 8 elements wide; the layout-FE sizing
extrapolates from 24/3 and 48/6), and a run is accepted only if it is safe, has no errors, covers every group (plates on supports
A, B, C, E; walls) and is not wasteful in any group.

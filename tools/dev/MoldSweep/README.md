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
| `solid3d [--quick]` | `tools/builtin/design-compression-mold/data/validation/solid3d-summary.json`: wall-root factor (14 pocket models), thick plates (10), bolted-joint member stiffness (10) | 45 s |
| `coefficients [--elements 48]` | `src/SwAgent/Mold/Structural/coefficients.csv` (Sweep 1, 11,328 rows) with its gates in the header | 75 s |
| `table` | `.../data/validation/sizing-table.csv` (thickness vs pressure, span, material, deflection limit) | 1 s |
| `montecarlo [--n 2000] [--seed 1]` | `.../data/validation/montecarlo-summary.json` and `results/montecarlo.csv` (Sweep 2) | 30 s |
| `all` | the four above, in that order (`coefficients` reads the wall-root result, `table` and `montecarlo` read the coefficients) | 2.5 min |

The exit code is 1 when a gate fails; the failing gates are printed and written into the outputs.

The same checks run from the test project: `dotnet test tests/SwAgent.Tests` covers T1-T18, the 3D and axisymmetric solvers, a
50-row regeneration of the CSV, the data gates and a 200-mold Monte Carlo smoke run (E12). With `MOLD_SWEEP=1` set, the tests
in `Category=Sweep` rerun the full sweeps (several minutes in Debug).

The Monte Carlo sizes each random mold through a delegate `Func<JObject, JObject>` (schema in `MonteCarloHarness`). Until the
calculator (`SwAgent.Mold` sizing) is integrated, `StubSizing` stands in for it; at integration replace it with an adapter.

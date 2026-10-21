using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Mold.Structural.Fe.Sweep;

namespace MoldSweep
{
    /// <summary>
    /// MoldSweep: the dev console behind the compression-mold structural tables (design 2.4 and 3.20).
    ///   coefficients  Sweep 1: src/SwAgent/Mold/Structural/coefficients.csv, with its gates.
    ///   table         data/validation/sizing-table.csv (thickness vs pressure, span, material, deflection limit).
    ///   montecarlo    Sweep 2: data/validation/montecarlo-summary.json and results/montecarlo.csv - the calculator's own sizing
    ///                 (MoldCalc.Run("size") through MoldCalcSizing), with the stub's table-only and design-as-written sizings of
    ///                 the same molds for comparison. The calculator reads the coefficient table embedded at build time: after
    ///                 `coefficients` rewrites the CSV, rebuild before `montecarlo`.
    ///   solid3d       3D solid cross-check: wall-root factor, thick plates, bolted-joint member stiffness -> solid3d-summary.json.
    ///   stockywalls   Sweep 4: the wall sizing against the 3D pocket over the sizing's range -> data/validation/stocky-walls.csv.
    ///   all           solid3d, coefficients, table, montecarlo.
    /// Options: --root DIR (repository root; found from the working directory by default), --elements N, --n N, --seed N, --quick.
    /// Exit code 1 when a gate fails.
    /// </summary>
    internal static class Program
    {
        static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            if (args.Length == 0) { Console.WriteLine("usage: MoldSweep coefficients|table|montecarlo|solid3d|stockywalls|all [--root DIR] [--elements N] [--n N] [--seed N] [--quick]"); return 2; }
            var opts = Options(args.Skip(1));
            var root = opts.TryGetValue("root", out var r) ? r : FindRoot();
            if (root == null) { Console.Error.WriteLine("Repository root not found; pass --root."); return 2; }
            var paths = new SweepPaths(root);
            bool ok = true;
            switch (args[0])
            {
                case "coefficients": ok = Coefficients(paths, opts); break;
                case "table": ok = Table(paths); break;
                case "montecarlo": ok = MonteCarlo(paths, opts); break;
                case "solid3d": ok = Solid3d(paths, opts); break;
                case "stockywalls": ok = StockyWalls(paths); break;
                case "all":
                    ok = Solid3d(paths, opts);
                    ok &= Coefficients(paths, opts);
                    ok &= Table(paths);
                    ok &= MonteCarlo(paths, opts);
                    break;
                default: Console.Error.WriteLine("Unknown command " + args[0]); return 2;
            }
            Console.WriteLine(ok ? "OK" : "GATE FAILED");
            return ok ? 0 : 1;
        }

        static Dictionary<string, string> Options(IEnumerable<string> args)
        {
            var d = new Dictionary<string, string>();
            string key = null;
            foreach (var a in args)
            {
                if (a.StartsWith("--")) { key = a.Substring(2); d[key] = "true"; }
                else if (key != null) { d[key] = a; key = null; }
            }
            return d;
        }

        static string FindRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppDomain.CurrentDomain.BaseDirectory })
                for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "src", "SwAgent", "SwAgent.csproj"))) return dir.FullName;
            return null;
        }

        static int Int(Dictionary<string, string> o, string k, int def) => o.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : def;

        static bool Coefficients(SweepPaths p, Dictionary<string, string> o)
        {
            int n = Int(o, "elements", CoefficientSweep.DefaultElements);
            Console.WriteLine($"Sweep 1: {CoefficientSweep.Grid().Count()} rows at {n} elements on the short side...");
            var rep = CoefficientSweep.Run(n, log: Console.WriteLine);
            string rootNote = WallRootNote(p);
            File.WriteAllText(p.Coefficients, CoefficientSweep.ToCsv(rep, n, rootNote, DateTime.Now, PillarHeadNote(p)), new UTF8Encoding(false));
            foreach (var g in rep.Gates) Console.WriteLine("  gate: " + g);
            foreach (var f in rep.Failures.Take(40)) Console.WriteLine("  FAIL: " + f);
            Console.WriteLine($"  wrote {p.Coefficients} ({rep.Rows.Count} rows, {rep.Elapsed.TotalSeconds:F0} s)");
            return rep.Passed;
        }

        static string WallRootNote(SweepPaths p)
        {
            if (!File.Exists(p.Solid3dSummary)) return $"1 + {Solid3dValidation.DefaultWallRootSlope} w/h (default; run MoldSweep solid3d to calibrate)";
            var w = JObject.Parse(File.ReadAllText(p.Solid3dSummary))["wallRoot"];
            return w == null ? null : $"{(string)w["recommendedFormula"]}; {(int?)w["cases"]} 3D solid cases, 3D / plate {(string)w["minRatio"]}-{(string)w["maxRatio"]} ({(string)w["meshes"]} extrapolated; {(string)w["source"]}); data/validation/solid3d-summary.json";
        }

        static string PillarHeadNote(SweepPaths p)
        {
            var c = File.Exists(p.Solid3dSummary) ? JObject.Parse(File.ReadAllText(p.Solid3dSummary))["pillarCell"] : null;
            double f = (double?)c?["pillarHeadFactor"] ?? Solid3dValidation.DefaultPillarHeadFactor;
            return $"multiply Pillar alpha by {f.ToString("0.00", CultureInfo.InvariantCulture)} (3D pillar cell / plate deflection, largest {(string)c?["largestAlphaRatio"] ?? "?"}); Pillar beta covers the 3D top-surface peak at t/b >= 0.1 (largest ratio {(string)c?["largestBetaRatioTau0_1Plus"] ?? "?"}); below t/b 0.1 the head corner peaks higher (ratio up to {(string)c?["largestBetaRatioThin"] ?? "?"}): radius the head corners or apply K_f; data/validation/solid3d-summary.json";
        }

        static double WallRootSlope(SweepPaths p)
        {
            if (!File.Exists(p.Solid3dSummary)) return Solid3dValidation.DefaultWallRootSlope;
            return (double?)JObject.Parse(File.ReadAllText(p.Solid3dSummary))["wallRoot"]?["recommendedSlope"] ?? Solid3dValidation.DefaultWallRootSlope;
        }

        static double PillarHeadFactor(SweepPaths p)
        {
            if (!File.Exists(p.Solid3dSummary)) return Solid3dValidation.DefaultPillarHeadFactor;
            return (double?)JObject.Parse(File.ReadAllText(p.Solid3dSummary))["pillarCell"]?["pillarHeadFactor"] ?? Solid3dValidation.DefaultPillarHeadFactor;
        }

        static bool Table(SweepPaths p)
        {
            var table = CoefficientTable.Load(p.Coefficients);
            var csv = SizingTable.Build(table, WallRootSlope(p));
            File.WriteAllText(p.SizingTable, csv, new UTF8Encoding(false));
            Console.WriteLine($"  wrote {p.SizingTable}");
            return true;
        }

        static bool MonteCarlo(SweepPaths p, Dictionary<string, string> o)
        {
            int n = Int(o, "n", 2000), seed = Int(o, "seed", 1);
            var table = CoefficientTable.Load(p.Coefficients);
            double slope = WallRootSlope(p), pillar = PillarHeadFactor(p);
            MonteCarloHarness.TrueRootFactor = wh => 1 + slope * wh;
            MonteCarloHarness.TruePillarFactor = pillar;
            StubSizing.Calibrate(slope, pillar);
            var data = SwAgent.Mold.MoldData.Load(p.ToolDir);
            Console.WriteLine($"Sweep 2: {n} random molds, seed {seed}, truth {MonteCarloHarness.TruthMesh}; root factor 1 + {slope} w/h, pillar-head factor {pillar}");
            // the calculator must use the factors the 3D check found (the truth uses them): sizing-defaults.json against solid3d-summary.json
            var cfg = new SwAgent.Mold.Cfg(data.Defaults);
            var calcSlope = SwAgent.Mold.Sizing.RootFactor(cfg).Slope;
            var calcPillar = cfg.D("pillarHeadFactor", 1.2);
            var mismatch = Math.Abs(calcSlope - slope) > 1e-9 || Math.Abs(calcPillar - pillar) > 1e-9;
            if (mismatch) Console.WriteLine($"  NOT ACCEPTED: sizing-defaults.json has wallRootFactor.c {calcSlope} and pillarHeadFactor {calcPillar}, solid3d-summary.json {slope} and {pillar} - update gen_defaults.py");
            Console.WriteLine($"  sizing: {MoldCalcSizing.Name}...");
            var run = MonteCarloHarness.Run(n, seed, MoldCalcSizing.Create(data), log: Console.WriteLine);
            Report(run);
            var summary = run.Summary(MoldCalcSizing.Name);
            foreach (var (key, opts) in new[] { ("compare_table_only", StubSizing.TableOnly), ("compare_design_as_written", StubSizing.DesignAsWritten) })
            {
                Console.WriteLine($"  the same molds sized by {opts.Name}...");
                var other = MonteCarloHarness.Run(n, seed, StubSizing.Create(table, opts));
                Report(other);
                var s = other.Summary(opts.Name);
                summary[key] = new JObject
                {
                    ["sizing"] = s["sizing"], ["max_r_w"] = s["max_r_w"], ["max_r_s"] = s["max_r_s"],
                    ["median_r_w_deflection_governed"] = s["median_r_w_deflection_governed"], ["accepted"] = s["accepted"],
                    ["acceptance_failures"] = s["acceptance_failures"], ["failures_count"] = other.Failures.Count, ["by_group"] = s["by_group"],
                };
            }
            File.WriteAllText(p.MonteCarloCsv, run.ToCsv(), new UTF8Encoding(false));
            File.WriteAllText(p.MonteCarloSummary, summary.ToString(Formatting.Indented), new UTF8Encoding(false));
            Console.WriteLine($"  wrote {p.MonteCarloCsv} and {p.MonteCarloSummary}");
            return run.Accepted && !mismatch;
        }

        static void Report(MonteCarloRun run)
        {
            Console.WriteLine($"    max r_w {run.MaxRw:F3}, max r_s {run.MaxRs:F3}, median r_w (deflection-governed) {run.MedianRwGoverned:F3}; {run.Failures.Count} failures, {run.Errors.Count} errors, {run.Elapsed.TotalSeconds:F0} s");
            foreach (var g in run.Stats())
                Console.WriteLine($"    {g.Name}: {g.Live}/{g.Checks} live ({g.Coverage:P0}), max r_w {g.MaxRw:F3}, max r_s {g.MaxRs:F3}, median r_w {g.MedianRw:F3} over {g.DeflectionGoverned}");
            foreach (var f in run.AcceptanceFailures()) Console.WriteLine("    NOT ACCEPTED: " + f);
        }

        static bool Solid3d(SweepPaths p, Dictionary<string, string> o)
        {
            bool quick = o.ContainsKey("quick");
            Console.WriteLine("3D solid cross-check" + (quick ? " (quick meshes)" : "") + "...");
            var summary = Solid3dValidation.Run(quick, Console.WriteLine);
            File.WriteAllText(p.Solid3dSummary, summary.ToString(Formatting.Indented), new UTF8Encoding(false));
            Console.WriteLine($"  wrote {p.Solid3dSummary}");
            return (bool?)summary["passed"] ?? false;
        }

        static bool StockyWalls(SweepPaths p)
        {
            Console.WriteLine("Sweep 4: the wall sizing against the 3D pocket...");
            var cases = StockyWallSweep.Run(Console.WriteLine);
            var failures = StockyWallSweep.Failures(cases);
            var lines = new List<string>
            {
                "# Sweep 4 (StockyWallSweep): wall-top bulge, 3D pocket (h/8 and h/12, first-order extrapolation) vs the calculator (Walls.Deflection), p = h = E = 1",
                "# max ratio: " + cases.Max(c => c.Ratio).ToString("F4", CultureInfo.InvariantCulture) + "; margin " + StockyWallSweep.Margin.ToString(CultureInfo.InvariantCulture) + "; gates passed: " + (failures.Count == 0 ? "yes" : "no"),
                StockyWallSweep.CsvHeader,
            };
            lines.AddRange(cases.Select(c => c.Csv()));
            File.WriteAllLines(p.StockyWalls, lines, new UTF8Encoding(false));
            foreach (var f in failures) Console.WriteLine("FAIL " + f);
            Console.WriteLine($"  {cases.Count} pockets, max 3D / calculator {cases.Max(c => c.Ratio):F4}; wrote {p.StockyWalls}");
            return failures.Count == 0;
        }
    }

    sealed class SweepPaths
    {
        public readonly string Coefficients, SizingTable, MonteCarloCsv, MonteCarloSummary, Solid3dSummary, StockyWalls, ToolDir;
        public SweepPaths(string root)
        {
            ToolDir = Path.Combine(root, "tools", "builtin", "design-compression-mold");
            Coefficients = Path.Combine(root, "src", "SwAgent", "Mold", "Structural", "coefficients.csv");
            var v = Path.Combine(root, "tools", "builtin", "design-compression-mold", "data", "validation");
            Directory.CreateDirectory(v);
            // the per-check Monte Carlo rows stay with the console (not shipped with the tool)
            var results = Path.Combine(root, "tools", "dev", "MoldSweep", "results");
            Directory.CreateDirectory(results);
            SizingTable = Path.Combine(v, "sizing-table.csv");
            MonteCarloCsv = Path.Combine(results, "montecarlo.csv");
            MonteCarloSummary = Path.Combine(v, "montecarlo-summary.json");
            Solid3dSummary = Path.Combine(v, "solid3d-summary.json");
            StockyWalls = Path.Combine(v, "stocky-walls.csv");
        }
    }
}

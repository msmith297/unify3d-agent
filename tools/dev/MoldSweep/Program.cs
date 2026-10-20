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
    ///   montecarlo    Sweep 2: data/validation/montecarlo-summary.json and results/montecarlo.csv (stub sizing until integration).
    ///   solid3d       3D solid cross-check: wall-root factor, thick plates, bolted-joint member stiffness -> solid3d-summary.json.
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
            if (args.Length == 0) { Console.WriteLine("usage: MoldSweep coefficients|table|montecarlo|solid3d|all [--root DIR] [--elements N] [--n N] [--seed N] [--quick]"); return 2; }
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
            File.WriteAllText(p.Coefficients, CoefficientSweep.ToCsv(rep, n, rootNote, DateTime.Now), new UTF8Encoding(false));
            foreach (var g in rep.Gates) Console.WriteLine("  gate: " + g);
            foreach (var f in rep.Failures.Take(40)) Console.WriteLine("  FAIL: " + f);
            Console.WriteLine($"  wrote {p.Coefficients} ({rep.Rows.Count} rows, {rep.Elapsed.TotalSeconds:F0} s)");
            return rep.Passed;
        }

        static string WallRootNote(SweepPaths p)
        {
            if (!File.Exists(p.Solid3dSummary)) return "1.2 (design default; run MoldSweep solid3d to calibrate)";
            var w = JObject.Parse(File.ReadAllText(p.Solid3dSummary))["wallRoot"];
            return w == null ? null : $"{(string)w["recommendedFormula"]}; {(int?)w["cases"]} 3D solid cases, 3D / plate {(string)w["minRatio"]}-{(string)w["maxRatio"]} ({(string)w["source"]}); data/validation/solid3d-summary.json";
        }

        static double WallRootSlope(SweepPaths p)
        {
            if (!File.Exists(p.Solid3dSummary)) return 2.6;
            return (double?)JObject.Parse(File.ReadAllText(p.Solid3dSummary))["wallRoot"]?["recommendedSlope"] ?? 2.6;
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
            double slope = WallRootSlope(p);
            MonteCarloHarness.TrueRootFactor = wh => 1 + slope * wh;
            StubSizing.RootFactor = wh => 1 + slope * wh;
            Console.WriteLine($"Sweep 2: {n} random molds, seed {seed}, sizing: {StubSizing.Recommended.Name}...");
            var run = MonteCarloHarness.Run(n, seed, StubSizing.Create(table, StubSizing.Recommended), log: Console.WriteLine);
            Console.WriteLine($"  max r_w {run.MaxRw:F3}, max r_s {run.MaxRs:F3}, median r_w (deflection-governed) {run.MedianRwGoverned:F3}; {run.Failures.Count} failures");
            Console.WriteLine($"  the same molds sized by {StubSizing.DesignAsWritten.Name}...");
            var asWritten = MonteCarloHarness.Run(n, seed, StubSizing.Create(table, StubSizing.DesignAsWritten), log: null);
            Console.WriteLine($"  max r_w {asWritten.MaxRw:F3}, max r_s {asWritten.MaxRs:F3}; {asWritten.Failures.Count} failures");
            var summary = run.Summary(StubSizing.Recommended.Name);
            var other = asWritten.Summary(StubSizing.DesignAsWritten.Name);
            summary["compare_design_as_written"] = new JObject
            {
                ["sizing"] = other["sizing"], ["max_r_w"] = other["max_r_w"], ["max_r_s"] = other["max_r_s"],
                ["median_r_w_deflection_governed"] = other["median_r_w_deflection_governed"], ["accepted"] = other["accepted"],
                ["failures_count"] = asWritten.Failures.Count, ["by_element"] = other["by_element"],
            };
            File.WriteAllText(p.MonteCarloCsv, run.ToCsv(), new UTF8Encoding(false));
            File.WriteAllText(p.MonteCarloSummary, summary.ToString(Formatting.Indented), new UTF8Encoding(false));
            Console.WriteLine($"  wrote {p.MonteCarloCsv} and {p.MonteCarloSummary}");
            return run.Accepted;
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
    }

    sealed class SweepPaths
    {
        public readonly string Coefficients, SizingTable, MonteCarloCsv, MonteCarloSummary, Solid3dSummary;
        public SweepPaths(string root)
        {
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
        }
    }
}

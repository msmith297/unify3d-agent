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
    ///   montecarlo    Sweep 2: data/validation/montecarlo.csv and montecarlo-summary.json (stub sizing until integration).
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
            if (!File.Exists(p.Solid3dSummary)) return "1.2 (default; run MoldSweep solid3d to calibrate)";
            var j = JObject.Parse(File.ReadAllText(p.Solid3dSummary));
            var w = j["wallRoot"];
            return w == null ? null : $"{(string)w["recommendedFactor"]} recommended from {(int?)w["cases"]} 3D solid cases (max ratio {(string)w["maxRatio"]}, {(string)w["source"]}); see data/validation/solid3d-summary.json";
        }

        static bool Table(SweepPaths p)
        {
            var table = CoefficientTable.Load(p.Coefficients);
            var csv = SizingTable.Build(table);
            File.WriteAllText(p.SizingTable, csv, new UTF8Encoding(false));
            Console.WriteLine($"  wrote {p.SizingTable}");
            return true;
        }

        static bool MonteCarlo(SweepPaths p, Dictionary<string, string> o)
        {
            int n = Int(o, "n", 2000), seed = Int(o, "seed", 1);
            var table = CoefficientTable.Load(p.Coefficients);
            Console.WriteLine($"Sweep 2: {n} random molds, seed {seed}, sizing: {StubSizing.Name}...");
            var run = MonteCarloHarness.Run(n, seed, StubSizing.Create(table), log: Console.WriteLine);
            File.WriteAllText(p.MonteCarloCsv, run.ToCsv(), new UTF8Encoding(false));
            File.WriteAllText(p.MonteCarloSummary, run.Summary(StubSizing.Name).ToString(Formatting.Indented), new UTF8Encoding(false));
            Console.WriteLine($"  max r_w {run.MaxRw:F3}, max r_s {run.MaxRs:F3}, median r_w (deflection-governed) {run.MedianRwGoverned:F3}; {run.Failures.Count} failures");
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
            SizingTable = Path.Combine(v, "sizing-table.csv");
            MonteCarloCsv = Path.Combine(v, "montecarlo.csv");
            MonteCarloSummary = Path.Combine(v, "montecarlo-summary.json");
            Solid3dSummary = Path.Combine(v, "solid3d-summary.json");
        }
    }
}

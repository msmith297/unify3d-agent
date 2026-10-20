using System;
using System.IO;
using System.Linq;
using SwAgent.Mold.Structural.Fe;
using SwAgent.Mold.Structural.Fe.Sweep;
using Xunit;
using Xunit.Abstractions;

namespace SwAgent.Tests.Mold
{
    /// <summary>Runs only with MOLD_SWEEP=1 (the full sweeps take minutes); `dotnet test --filter Category=Sweep` with it set.</summary>
    public sealed class SweepFactAttribute : FactAttribute
    {
        public SweepFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MOLD_SWEEP") != "1") Skip = "Set MOLD_SWEEP=1 to run the full mold sweeps.";
        }
    }

    internal static class SweepFiles
    {
        public static string Root
        {
            get
            {
                for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "src", "SwAgent", "SwAgent.csproj"))) return dir.FullName;
                throw new DirectoryNotFoundException("Repository root not found above the test assembly.");
            }
        }

        public static string Coefficients => Path.Combine(Root, "src", "SwAgent", "Mold", "Structural", "coefficients.csv");
        public static string Validation => Path.Combine(Root, "tools", "builtin", "design-compression-mold", "data", "validation");

        static CoefficientTable _table;
        public static CoefficientTable Table => _table ?? (_table = CoefficientTable.Load(Coefficients));
    }

    /// <summary>The shipped coefficients.csv against the generator, and the Sweep 1 gates on the data (default test run).</summary>
    public class SweepTests
    {
        readonly ITestOutputHelper _out;
        public SweepTests(ITestOutputHelper output) { _out = output; }

        [Fact]
        public void Csv_has_the_contract_columns_and_its_hash()
        {
            var lines = File.ReadAllLines(SweepFiles.Coefficients).Where(l => l.Length > 0).ToList();
            var data = lines.Where(l => !l.StartsWith("#")).ToList();
            Assert.Equal("bc,aspect,tau,nu,loadFrac,alpha,beta,wMaxX,wMaxY", data[0]);
            Assert.Equal(CoefficientSweep.Grid().Count(), data.Count - 1);
            Assert.Equal(CoefficientSweep.Sha256(data), SweepFiles.Table.Sha256);
            Assert.Contains(lines, l => l.StartsWith("# gates passed: yes"));
            Assert.Contains(lines, l => l.StartsWith("# wall root: "));
        }

        [Fact]
        public void Fifty_random_rows_regenerate()
        {
            var rows = SweepFiles.Table.Rows;
            var rnd = new Random(7);
            var pick = Enumerable.Range(0, 50).Select(_ => rows[rnd.Next(rows.Count)]).ToList();
            var regen = new CoefficientRow[pick.Count];
            System.Threading.Tasks.Parallel.For(0, pick.Count, k =>
            {
                var r = pick[k];
                regen[k] = CoefficientSweep.Compute(r.Bc, r.Aspect, r.Tau, r.Nu, r.LoadFrac);
            });
            for (int k = 0; k < pick.Count; k++)
            {
                var r = pick[k];
                Assert.True(Math.Abs(regen[k].Alpha / r.Alpha - 1) < 0.005, $"{r.Bc} {r.Aspect} {r.Tau} {r.Nu} {r.LoadFrac}: alpha {regen[k].Alpha} vs {r.Alpha}");
                Assert.True(Math.Abs(regen[k].Beta / r.Beta - 1) < 0.005, $"{r.Bc} {r.Aspect} {r.Tau} {r.Nu} {r.LoadFrac}: beta {regen[k].Beta} vs {r.Beta}");
            }
        }

        [Fact]
        public void Thin_limit_matches_roark()
        {
            // gate 1 at nu = 0.3 (the table itself is at 0.29 and 0.33); alpha alone scales with (1 - nu^2) for SS and clamped plates
            for (int k = 0; k < ClosedForm.RoarkSS.GetLength(0); k++)
            {
                double ab = ClosedForm.RoarkSS[k, 0];
                var r = CoefficientSweep.Compute("SS4", ab, 0.01, 0.3, 1);
                Assert.InRange(r.Alpha / ClosedForm.RoarkSS[k, 1], 0.99, 1.01);
                Assert.InRange(r.Beta / ClosedForm.RoarkSS[k, 2], 0.99, 1.01);
                if (CoefficientSweep.PlateAspects.Contains(ab))
                {
                    var t = SweepFiles.Table.Get("SS4", ab, 0.01, 0.29);
                    Assert.InRange(t.Alpha / (ClosedForm.RoarkSS[k, 1] * (1 - 0.29 * 0.29) / 0.91), 0.99, 1.01);
                }
            }
            for (int k = 0; k < ClosedForm.RoarkClamped.GetLength(0); k++)
            {
                var r = CoefficientSweep.Compute("C4", ClosedForm.RoarkClamped[k, 0], 0.01, 0.3, 1);
                Assert.InRange(r.Alpha / ClosedForm.RoarkClamped[k, 1], 0.99, 1.01);
                Assert.InRange(r.Beta / ClosedForm.RoarkClamped[k, 2], 0.99, 1.01);
            }
        }

        [Fact]
        public void Every_simply_supported_row_matches_navier_fsdt()
        {
            double worst = 0;
            foreach (var r in SweepFiles.Table.Rows.Where(r => r.Bc == "SS4"))
            {
                double a = r.Aspect, s = Math.Sqrt(r.LoadFrac);
                var nav = Navier.Patch(a, 1, r.Tau, 1, r.Nu, 1, a * (1 - s) / 2, a * (1 + s) / 2, (1 - s) / 2, (1 + s) / 2, a / 2, 0.5, true, 99);
                double alpha = nav.W * r.Tau * r.Tau * r.Tau;
                worst = Math.Max(worst, Math.Abs(r.Alpha / alpha - 1));
            }
            _out.WriteLine($"worst SS4 alpha vs Navier FSDT (99 x 99 terms): {worst:P3}");
            Assert.True(worst < 0.005);
        }

        [Fact]
        public void Rails_free_edge_rows_match_levy_not_the_beam()
        {
            foreach (var r in SweepFiles.Table.Rows.Where(r => r.Bc == "Rails" && r.Tau == 0.01 && r.LoadFrac == 1))
            {
                var levy = Levy.RailsFreeEdge(r.Aspect, r.Nu);
                Assert.InRange(r.Beta / levy.Beta, 0.99, 1.005);
            }
            // a one-way span on rails is not the beam's 0.75 when thin
            Assert.True(SweepFiles.Table.Get("Rails", 2, 0.01, 0.33).Beta > 0.79);
        }

        [Fact]
        public void Wall_rows_rise_with_length_toward_the_strip_with_shear()
        {
            // critique A4: alpha and beta never decrease with L/h (thick walls: up to L/h 4, then within 1.5% of the strip) and the
            // L/h 12 rows sit on the strip, so beyond the table the strip is the right (not an unconservative) continuation
            foreach (var bc in new[] { "Wall-C", "Wall-S" })
                foreach (var nu in CoefficientSweep.Nus)
                    foreach (var tau in CoefficientSweep.Taus)
                    {
                        var rows = SweepFiles.Table.Rows.Where(r => r.Bc == bc && r.Nu == nu && r.Tau == tau).OrderBy(r => r.Aspect).ToList();
                        var strip = ClosedForm.CantileverStrip(nu, tau);
                        for (int k = 1; k < rows.Count; k++)
                        {
                            if (tau >= 0.25 && rows[k].Aspect > 4) continue;
                            Assert.True(rows[k].Alpha >= rows[k - 1].Alpha * 0.999, $"{bc} tau {tau}: alpha drops at L/h {rows[k].Aspect}");
                            Assert.True(rows[k].Beta >= rows[k - 1].Beta * 0.999, $"{bc} tau {tau}: beta drops at L/h {rows[k].Aspect}");
                        }
                        double limit = tau >= 0.25 ? 1.015 : 1.002;
                        Assert.All(rows, r => Assert.True(r.Alpha <= strip.Alpha * limit && r.Beta <= strip.Beta * limit, $"{bc} L/h {r.Aspect} tau {tau} above the strip"));
                        var last = rows.Last();
                        Assert.InRange(last.Alpha / strip.Alpha, 0.97, 1.015);
                        // and the lookup past the table continues at or above both
                        var beyond = SweepFiles.Table.Get(bc, 40, tau, nu);
                        Assert.True(beyond.Alpha >= Math.Max(strip.Alpha, last.Alpha) - 1e-12 && beyond.Beta >= 3.0);
                    }
        }

        [Fact]
        public void Table_reproduces_the_worked_plate_example()
        {
            // N/structural.md 4.6 example 2 (SS4 square, 6061 E 66 GPa, delta 0.025 mm; computed there at nu 0.3, here at 0.33):
            // b = 100 mm: 26.3 / 35.4 / 49.7 mm at 5 / 10 / 20 MPa, and t/b over 0.5 ("add support") at 35 MPa
            var t = SweepFiles.Table;
            double T(double p) => t.ThicknessForDeflection("SS4", 1, 100, p, 66000, 0.33, 0.025);
            Assert.InRange(T(5), 26.3 * 0.98, 26.3 * 1.01);
            Assert.InRange(T(10), 35.4 * 0.98, 35.4 * 1.01);
            Assert.InRange(T(20), 49.7 * 0.98, 49.7 * 1.01);
            Assert.True(T(35) / 100 > 0.5);
            var csv = File.ReadAllLines(Path.Combine(SweepFiles.Validation, "sizing-table.csv"));
            Assert.Contains(csv, l => l.StartsWith("SS4,1,") && l.Contains(",0.025,100,35,") && l.EndsWith("add support"));
        }

        [Fact]
        public void Monte_carlo_smoke_E12()
        {
            // E12: 200 random molds, seed 1: the fast path is never unsafe (max r <= 1.00) and not wasteful (median r_w >= 0.75)
            var run = MonteCarloHarness.Run(200, 1, StubSizing.Create(SweepFiles.Table, StubSizing.Recommended));
            _out.WriteLine($"max r_w {run.MaxRw:F4}, max r_s {run.MaxRs:F4}, median r_w {run.MedianRwGoverned:F3}, {run.Checks.Count} checks, {run.Elapsed.TotalSeconds:F1} s");
            Assert.True(run.MaxRw <= 1.0, $"max r_w {run.MaxRw}");
            Assert.True(run.MaxRs <= 1.0, $"max r_s {run.MaxRs}");
            Assert.True(run.MedianRwGoverned >= 0.75);
            Assert.True(run.Accepted);
            // the delegate is the seam for A1's sizing: the harness only reads its JSON
            var summary = run.Summary("smoke");
            Assert.Equal(200, (int)summary["samples"]);
        }

        [Fact]
        public void Design_as_written_is_unsafe_where_the_sweeps_said()
        {
            // the same 60 molds with the root factor 1.2 and pillar panels as C4: walls and pillar plates under-sized
            var run = MonteCarloHarness.Run(60, 3, StubSizing.Create(SweepFiles.Table, StubSizing.DesignAsWritten));
            var walls = run.Checks.Where(c => !c.Skipped && c.Element.StartsWith("wall") && c.Governs == "deflection").ToList();
            Assert.True(walls.Max(c => c.Rw) > 1.2);
        }

        [Fact]
        public void Wall_root_factor_from_a_coarse_3d_pocket()
        {
            // the 3D pocket (L/h 4, w/h 0.25, floor 0.5 h) against the clamped-root Wall-C plate: well above the design's 1.2,
            // near 1 + 2 w/h, and the rigid-floor model shows the wall itself is plate-like
            var c = new Solid3dValidation.WallCase("test", 4, 4, 0.25, 0.5);
            double plate = Solid3dValidation.WallPlate(c);
            var (w3, _, r) = Solid3dValidation.WallPocket(c, 1.0 / 6, false);
            var (wr, _, _) = Solid3dValidation.WallPocket(c, 1.0 / 6, true);
            _out.WriteLine($"3D / plate {w3 / plate:F3}, rigid floor {wr / plate:F3}; {r.Dofs} dofs, {r.Iterations} iterations");
            Assert.True(r.Converged);
            Assert.InRange(w3 / plate, 1.4, 1.65);
            Assert.InRange(wr / plate, 0.95, 1.06);
        }

        [Fact]
        public void Validation_summaries_are_present_and_passed()
        {
            var solid = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(SweepFiles.Validation, "solid3d-summary.json")));
            Assert.True((bool)solid["passed"]);
            Assert.True((double)solid["wallRoot"]["recommendedSlope"] >= 2.0);
            var mc = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(SweepFiles.Validation, "montecarlo-summary.json")));
            Assert.True((bool)mc["accepted"]);
            Assert.True((int)mc["samples"] >= 2000);
        }

        // ---------------- the full sweeps (MOLD_SWEEP=1) ----------------

        [SweepFact, Trait("Category", "Sweep")]
        public void Sweep1_full_table_passes_its_gates()
        {
            var rep = CoefficientSweep.Run(log: _out.WriteLine);
            foreach (var g in rep.Gates) _out.WriteLine(g);
            foreach (var f in rep.Failures) _out.WriteLine("FAIL " + f);
            Assert.True(rep.Passed);
            // and the shipped file is what the generator makes
            var shipped = SweepFiles.Table.Rows;
            Assert.Equal(shipped.Count, rep.Rows.Count);
            for (int k = 0; k < shipped.Count; k++)
                Assert.True(Math.Abs(rep.Rows[k].Alpha / shipped[k].Alpha - 1) < 1e-5 && Math.Abs(rep.Rows[k].Beta / shipped[k].Beta - 1) < 1e-5, $"row {k} differs from the shipped table");
        }

        [SweepFact, Trait("Category", "Sweep")]
        public void Sweep2_monte_carlo_2000_is_accepted()
        {
            var run = MonteCarloHarness.Run(2000, 1, StubSizing.Create(SweepFiles.Table, StubSizing.Recommended), log: _out.WriteLine);
            _out.WriteLine(run.Summary(StubSizing.Recommended.Name).ToString());
            Assert.True(run.Accepted);
        }

        [SweepFact, Trait("Category", "Sweep")]
        public void Sweep3_solid_cross_check_passes()
        {
            var s = Solid3dValidation.Run(false, _out.WriteLine);
            _out.WriteLine(s.ToString());
            Assert.True((bool)s["passed"]);
        }
    }
}

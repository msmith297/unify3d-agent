using System;
using System.Linq;
using SwAgent.Mold.Structural.Fe;
using Xunit;
using Xunit.Abstractions;

namespace SwAgent.Tests.Mold
{
    internal static class PlateModelTestExtensions
    {
        public static PlateModel Also(this PlateModel m, Action<PlateModel> a) { a(m); return m; }
    }

    /// <summary>
    /// T1-T18 of N/structural.md 9.5 for the MITC4 plate solver. E = 1, q = 1, nu = 0.3 unless stated; results are reported as
    /// w D / (q a^4) and M / (q a^2) so the targets read like Timoshenko's tables.
    /// </summary>
    public class FeTests
    {
        readonly ITestOutputHelper _out;
        public FeTests(ITestOutputHelper output) { _out = output; }

        const double Nu = 0.3;
        static readonly EdgeBc S = EdgeBc.SimpleHard, C = EdgeBc.Clamped, F = EdgeBc.Free;

        static PlateResult Run(double a, double b, double t, EdgeBc x0, EdgeBc xa, EdgeBc y0, EdgeBc yb, int nx, int ny, double nu = Nu)
            => MindlinPlateSolver.Solve(PlateModel.Rect(a, b, t, 1, nu, x0, xa, y0, yb).Uniform(1).Mesh(nx, ny));

        static double D(double t, double nu = Nu) => t * t * t / (12 * (1 - nu * nu));

        static void Near(double expected, double actual, double relTol, string what)
            => Assert.True(Math.Abs(actual - expected) <= relTol * Math.Abs(expected), $"{what}: expected {expected:G6}, got {actual:G6} ({(actual / expected - 1) * 100:F3}%)");

        [Fact]
        public void T1_T2_simply_supported_square_thin()
        {
            const double t = 0.01;
            var nav = Navier.SimplySupported(1, 1, t, 1, Nu, 1, 0.5, 0.5, shear: false);
            Near(0.004062, nav.W * D(t), 0.0005, "Navier w");
            Near(0.04789, nav.Mx, 0.001, "Navier Mx");
            var r16 = Run(1, 1, t, S, S, S, S, 16, 16);
            Near(nav.W, r16.W[8, 8], 0.005, "T1 w at 16x16");
            Near(nav.Mx, r16.Mx[8, 8], 0.005, "T2 Mx at 16x16");
            var r32 = Run(1, 1, t, S, S, S, S, 32, 32);
            Near(nav.W, r32.W[16, 16], 0.001, "T1 w at 32x32");
            _out.WriteLine($"T1 16: {r16.W[8, 8] * D(t):F6}  32: {r32.W[16, 16] * D(t):F6}  T2 16: {r16.Mx[8, 8]:F5}");
        }

        [Fact]
        public void T3_T4_T5_clamped_square()
        {
            const double t = 0.01;
            var r = Run(1, 1, t, C, C, C, C, 16, 16);
            Near(0.001265, r.W[8, 8] * D(t), 0.01, "T3 w");
            Near(-0.0513, r.Mx[0, 8], 0.015, "T4 edge moment from reactions");
            Near(-0.0513, r.EdgeMoments["y0"][8], 0.015, "T4 edge moment, y edge");
            Near(0.0231, r.Mx[8, 8], 0.01, "T5 centre moment");
            Assert.Equal(r.MMax, r.MPrincipal[0, 8], 9);
            _out.WriteLine($"T3 {r.W[8, 8] * D(t):F6}  T4 {r.Mx[0, 8]:F5}  T5 {r.Mx[8, 8]:F5}");
        }

        [Fact]
        public void T6_T7_rectangles_b_over_a_2()
        {
            const double t = 0.01;
            var nav = Navier.SimplySupported(1, 2, t, 1, Nu, 1, 0.5, 1.0, shear: false);
            Near(0.010129, nav.W * D(t), 0.001, "Navier b/a = 2");
            Near(0.10168, nav.Mx, 0.002, "Navier Mx b/a = 2");
            var ss = Run(1, 2, t, S, S, S, S, 16, 32);
            Near(0.01013, ss.W[8, 16] * D(t), 0.01, "T6 w");
            Near(0.1017, ss.Mx[8, 16], 0.01, "T6 Mx");
            var cc = Run(1, 2, t, C, C, C, C, 16, 32);
            Near(0.00254, cc.W[8, 16] * D(t), 0.015, "T7 w");
            Near(-0.0829, cc.Mx[0, 16], 0.015, "T7 edge moment");
            _out.WriteLine($"T6 {ss.W[8, 16] * D(t):F6} {ss.Mx[8, 16]:F5}  T7 {cc.W[8, 16] * D(t):F6} {cc.Mx[0, 16]:F5}");
        }

        [Theory]
        [InlineData(0.1, 0.004273)]
        [InlineData(0.2, 0.004904)]
        [InlineData(0.3, 0.005957)]
        public void T8_thick_simply_supported_square_matches_navier_fsdt(double t, double target)
        {
            var nav = Navier.SimplySupported(1, 1, t, 1, Nu, 1, 0.5, 0.5);
            Near(target, nav.W * D(t), 0.0005, "Navier FSDT");
            var r = Run(1, 1, t, S, S, S, S, 16, 16);
            Near(nav.W, r.W[8, 8], 0.005, "T8 FE at 16x16");
        }

        [Theory]
        [InlineData(0.1)]
        [InlineData(0.2)]
        [InlineData(0.3)]
        public void T9_wang_reddy_relation(double t)
        {
            // w_FSDT - w_K = M_Marcus / (kappa G t) at the centre, M_Marcus = (Mx + My) / (1 + nu)
            var k = Navier.SimplySupported(1, 1, t, 1, Nu, 1, 0.5, 0.5, shear: false);
            var f = Navier.SimplySupported(1, 1, t, 1, Nu, 1, 0.5, 0.5, shear: true);
            double kgt = 5.0 / 6.0 / (2 * (1 + Nu)) * t;
            Near((k.Mx + k.My) / (1 + Nu) / kgt, f.W - k.W, 0.005, "Navier");
            // and the FE: the shear part of its deflection (32 x 32) against the same Marcus moment
            var fe = Run(1, 1, t, S, S, S, S, 32, 32);
            Near((fe.Mx[16, 16] + fe.My[16, 16]) / (1 + Nu) / kgt, fe.W[16, 16] - k.W, 0.02, "FE");
        }

        [Fact]
        public void T10_wide_cantilever_strip()
        {
            const double t = 0.01;
            // clamped at x = 0, length L = 1 along x, width 4 along y
            var r = Run(1, 4, t, C, F, F, F, 12, 48);
            Near(0.12589, r.W[12, 24] * D(t), 0.01, "T10 tip w");
            Assert.True(r.W[12, 24] * D(t) > 0.125, "free sides let the strip curl anticlastically: at least the cylindrical-strip value");
            Near(-0.5, r.Mx[0, 24], 0.01, "T10 root moment from reactions");
            _out.WriteLine($"T10 tip {r.W[12, 24] * D(t):F5} root {r.Mx[0, 24]:F4}");
        }

        [Fact]
        public void T11_free_edges_deflect_more_than_the_beam()
        {
            const double t = 0.01;
            double beam = 5.0 / 384 / (1 - Nu * Nu);
            // span b = 1 between hard SS edges y = 0 and y = 1; free edges x = 0, a. Reference: the Levy series (0.01501, 0.01520).
            var r1 = Run(1, 1, t, F, F, S, S, 32, 32);
            var r2 = Run(2, 1, t, F, F, S, S, 64, 32);
            double w1 = r1.W[0, 16] * D(t), w2 = r2.W[0, 16] * D(t);
            double l1 = Levy.Rails(1, 1, 1, Nu, 1, 0.5, 0.5).W, l2 = Levy.Rails(2, 1, 1, Nu, 1, 1.0, 0.5).W;
            _out.WriteLine($"T11 free-edge mid w D/(q b^4): a/b=1 {w1:F5} (Levy {l1:F5}), a/b=2 {w2:F5} (Levy {l2:F5}), beam {beam:F5}");
            Assert.True(w1 > beam && w2 > beam, "free edge must lie above the beam value");
            Near(0.0151, w1, 0.015, "T11 a/b = 1 (Timoshenko, about 0.0151)");
            Near(l1, w1, 0.005, "T11 a/b = 1 against Levy");
            Near(l2, w2, 0.005, "T11 a/b = 2 against Levy");
        }

        [Fact]
        public void T12_thin_limit_matches_roark()
        {
            for (int k = 0; k < ClosedForm.RoarkSS.GetLength(0); k++)
            {
                double ab = ClosedForm.RoarkSS[k, 0];
                var (alpha, beta) = Coef(ab, S, S, S, S);
                Near(ClosedForm.RoarkSS[k, 1], alpha, 0.01, $"SS alpha a/b={ab}");
                Near(ClosedForm.RoarkSS[k, 2], beta, 0.01, $"SS beta a/b={ab}");
            }
            for (int k = 0; k < ClosedForm.RoarkClamped.GetLength(0); k++)
            {
                double ab = ClosedForm.RoarkClamped[k, 0];
                var (alpha, beta) = Coef(ab, C, C, C, C);
                Near(ClosedForm.RoarkClamped[k, 1], alpha, 0.01, $"C alpha a/b={ab}");
                Near(ClosedForm.RoarkClamped[k, 2], beta, 0.01, $"C beta a/b={ab}");
            }
        }

        static (double alpha, double beta) Coef(double ab, EdgeBc x0, EdgeBc xa, EdgeBc y0, EdgeBc yb)
        {
            const double t = 0.01;
            var m = PlateModel.Rect(ab, 1, t, 1, Nu, x0, xa, y0, yb).Uniform(1);
            m.ElementsOnShortSide = 24;
            var r = MindlinPlateSolver.Solve(m);
            return (Math.Abs(r.WMax) * t * t * t, 6 * r.MMax);
        }

        [Fact]
        public void T13_mirrored_patch_gives_mirrored_result()
        {
            var a = PlateModel.Rect(1.5, 1, 0.1, 1, Nu, C, S, F, C);
            a.Pressures.Add(new PressurePatch(0.2, 0.55, 0.1, 0.4, 1));
            var b = PlateModel.Rect(1.5, 1, 0.1, 1, Nu, S, C, F, C);
            b.Pressures.Add(new PressurePatch(1.5 - 0.55, 1.5 - 0.2, 0.1, 0.4, 1));
            a.Mesh(30, 20); b.Mesh(30, 20);
            var ra = MindlinPlateSolver.Solve(a);
            var rb = MindlinPlateSolver.Solve(b);
            double scale = Math.Abs(ra.WMax);
            for (int i = 0; i <= 30; i++)
                for (int j = 0; j <= 20; j++)
                {
                    Assert.True(Math.Abs(ra.W[i, j] - rb.W[30 - i, j]) < 1e-10 * scale, $"w mirror at {i},{j}");
                    Assert.True(Math.Abs(ra.MPrincipal[i, j] - rb.MPrincipal[30 - i, j]) < 1e-9 * ra.MMax, $"M mirror at {i},{j}");
                }
        }

        [Fact]
        public void T14_winkler_free_plate_uniform_load_is_q_over_k()
        {
            var m = PlateModel.Rect(2, 1, 0.05, 1, Nu, F, F, F, F).Uniform(1);
            m.Foundations.Add(new WinklerRegion(0, 2, 0, 1, 250));
            m.Mesh(16, 8);
            var r = MindlinPlateSolver.Solve(m);
            foreach (var w in r.W) Assert.True(Math.Abs(w - 1.0 / 250) < 1e-8 / 250, $"w = {w}");
            Assert.True(Math.Abs(r.AppliedLoad + r.FoundationForce) < 1e-9 * r.AppliedLoad);
        }

        [Fact]
        public void T15_equilibrium_and_rigid_body_modes()
        {
            // (a) supported plate with a patch, springs and a foundation: forces and moments balance
            var m = PlateModel.Rect(1.3, 0.9, 0.08, 200, Nu, S, F, C, F);
            m.Pressures.Add(new PressurePatch(0.3, 0.9, 0.2, 0.7, 2));
            m.Springs.Add(new PointSpring(1.2, 0.8, 50));
            m.PointLoads.Add(new PointLoad(1.0, 0.1, 0.3));
            m.Foundations.Add(new WinklerRegion(0.6, 1.3, 0.0, 0.9, 30));
            var r = MindlinPlateSolver.Solve(m);
            double sum = r.AppliedLoad + r.SupportForce + r.FoundationForce + r.SpringForce;
            Assert.True(Math.Abs(sum) < 1e-9 * Math.Abs(r.AppliedLoad), $"force balance {sum}");
            // moment balance about both axes: the clamped and hard SS edges' rotation reactions count
            double mx = r.AppliedMomentX + r.SupportMomentX + r.OtherMomentX, my = r.AppliedMomentY + r.SupportMomentY + r.OtherMomentY;
            Assert.True(Math.Abs(mx) < 1e-9 * Math.Abs(r.AppliedMomentX), $"moment balance about y: {mx}");
            Assert.True(Math.Abs(my) < 1e-9 * Math.Abs(r.AppliedMomentY), $"moment balance about x: {my}");
            Assert.True(Math.Abs(r.AppliedLoad - (2 * 0.6 * 0.5 + 0.3)) < 1e-12, "consistent loads integrate to the total");

            // (b) free plate held at three points (statically determinate) under self-equilibrated loads: no reactions
            var fp = PlateModel.Rect(1, 1, 0.05, 1, Nu, F, F, F, F);
            fp.Fixed.Add(new FixedRegion(0, 0, 0, 0));
            fp.Fixed.Add(new FixedRegion(1, 1, 0, 0));
            fp.Fixed.Add(new FixedRegion(0.5, 0.5, 1, 1));
            fp.Mesh(10, 10);
            fp.PointLoads.Add(new PointLoad(0.2, 0.3, 1));
            fp.PointLoads.Add(new PointLoad(0.8, 0.3, 1));
            fp.PointLoads.Add(new PointLoad(0.5, 0.3, -2));
            var rf = MindlinPlateSolver.Solve(fp);
            double maxR = 0;
            foreach (var v in rf.Reactions) maxR = Math.Max(maxR, Math.Abs(v));
            Assert.True(maxR < 1e-9, $"reactions under a self-equilibrated load: {maxR}");
            Assert.True(Math.Abs(rf.WMax) > 0, "the plate still bends");
        }

        [Fact]
        public void T16_thickness_fixed_point()
        {
            // t = [alpha(t/b) p b^4 / (E delta)]^(1/3): SS4 a/b = 1, p = 10 MPa, b = 100 mm, E = 66 GPa, delta = 0.025 mm
            double t = 20;
            for (int k = 0; k < 30; k++)
            {
                var r = MindlinPlateSolver.Solve(PlateModel.Rect(100, 100, t, 66000, 0.3, S, S, S, S).Uniform(10).Mesh(16, 16));
                double alpha = Math.Abs(r.WMax) * 66000 * t * t * t / (10 * 1e8);
                double tn = Math.Pow(alpha * 10 * 1e8 / (66000 * 0.025), 1.0 / 3);
                if (Math.Abs(tn - t) < 1e-6 * t) { t = tn; break; }
                t = tn;
            }
            Near(35.4, t, 0.005, "T16 thickness");
        }

        // T17 pins the member and bolt stiffness of N/structural.md 6.3 (production JointStiffness, the A2 solver side). The
        // calculator's own joint arithmetic (load factor, thermal preload change, separation with tightening scatter and the hot
        // proof factor) is checked on the critique-revised example by MoldExamplesTests.E4 (E_bolt 205 GPa there: C 0.311), and
        // T18's press force by MoldExamplesTests.E1 (Press.Compute: 670.8 kN, 3,142 psi on 48 in2), so neither is repeated here.
        [Fact]
        public void T17_bolted_corner_example()
        {
            // N/structural.md 6.3: 4 x 1/2-13, grade-8 proof (120 ksi), grip 2 in, aluminium joint
            double at = 0.1419, l = 2.0, d = 0.5;
            double kb = JointStiffness.Bolt(29e6, at, at, 0, l); // all thread in the grip (structural.md: k_b = A_t E / l)
            Assert.Equal(at * 29e6 / l, kb, 6);
            double km = JointStiffness.Wileman(10e6, d, l, JointStiffness.WilemanAluminium);
            Near(0.306, kb / (kb + km), 0.01, "T17 C");
        }

        // ---------------- beyond T1-T18 ----------------

        [Fact]
        public void Rails_free_edge_recovery_matches_levy_and_does_not_overshoot()
        {
            // The free edge of a plate on two rails carries more than the beam's 0.75 (Levy, thin: 0.787 at a/b = 1, 0.797 at
            // a/b = 2). The recovery must reproduce that without overshooting it, and fall toward 0.75 as shear softens the edge.
            foreach (var ab in new[] { 1.0, 2.0 })
            {
                var levy = Levy.RailsFreeEdge(ab, Nu);
                var r = MindlinPlateSolver.Solve(PlateModel.Rect(ab, 1, 0.01, 1, Nu, F, F, S, S).Uniform(1));
                double beta = 6 * r.MMax;
                _out.WriteLine($"rails a/b={ab} thin: beta {beta:F4} at {r.SigmaMaxAt}, Levy {levy.Beta:F4}; alpha {Math.Abs(r.WMax) * 1e-6:F4}, Levy {levy.Alpha:F4}");
                Assert.InRange(beta / levy.Beta, 0.99, 1.005);
                Assert.True(r.SigmaMaxAt.X < 1e-9 || r.SigmaMaxAt.X > ab - 1e-9, "the peak sits on a free edge");
                var thick = MindlinPlateSolver.Solve(PlateModel.Rect(ab, 1, 0.3, 1, Nu, F, F, S, S).Uniform(1));
                _out.WriteLine($"rails a/b={ab} t/b=0.3: beta {6 * thick.MMax:F4}");
                Assert.InRange(6 * thick.MMax, 0.75, levy.Beta);
            }
        }

        [Fact]
        public void Long_wall_approaches_the_cantilever_strip_with_shear()
        {
            foreach (var tau in new[] { 0.05, 0.2, 0.5 })
            {
                var m = PlateModel.Rect(12, 1, tau, 1, Nu, C, C, C, F).Uniform(1);
                var r = MindlinPlateSolver.Solve(m);
                double alpha = Math.Abs(r.WMax) * tau * tau * tau, beta = 6 * r.MMax;
                var strip = ClosedForm.CantileverStrip(Nu, tau);
                _out.WriteLine($"wall L/h=12 tau={tau}: alpha {alpha:F4} (strip {strip.Alpha:F4}), beta {beta:F4}");
                // at tau = 0.5 the Mindlin layer next to the clamped sides puts the peak 0.2% above the strip (mesh-independent)
                Assert.InRange(alpha / strip.Alpha, 0.97, 1.005);
                Assert.InRange(beta / strip.Beta, 0.97, 1.005);
            }
        }

        [Fact]
        public void Banded_ldlt_matches_dense_solution()
        {
            var rnd = new Random(3);
            int n = 40, bw = 5;
            var k = new BandedLdlt(n, bw);
            var dense = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j <= Math.Min(n - 1, i + bw); j++) { double v = rnd.NextDouble() - 0.5; k.Add(i, j, v); dense[i, j] = dense[j, i] = v; }
                k.Add(i, i, 6); dense[i, i] = 6;
            }
            var b = Enumerable.Range(0, n).Select(i => Math.Sin(i)).ToArray();
            var ax = k.Multiply(Enumerable.Repeat(1.0, n).ToArray());
            for (int i = 0; i < n; i++) Assert.Equal(Enumerable.Range(0, n).Sum(j => dense[i, j]), ax[i], 12);
            var x = k.Solve(b);
            for (int i = 0; i < n; i++) Assert.Equal(b[i], Enumerable.Range(0, n).Sum(j => dense[i, j] * x[j]), 10);
            Assert.Equal(0, k.NegativePivots());
        }

        [Fact]
        public void Mechanism_is_reported()
        {
            var m = PlateModel.Rect(1, 1, 0.1, 1, Nu, F, F, F, F).Uniform(1);
            Assert.Throws<InvalidOperationException>(() => MindlinPlateSolver.Solve(m));
        }

        [Fact]
        public void Every_mechanism_is_reported_not_just_the_free_plate()
        {
            // each of these used to return |w| around 1e14, or a plausible-looking number, without a word
            var cases = new (string Name, Func<PlateModel> Make)[]
            {
                ("one soft SS edge", () => PlateModel.Rect(1, 1, 0.05, 1, Nu, EdgeBc.SimpleSoft, F, F, F).Uniform(1)),
                ("one hard SS edge", () => PlateModel.Rect(1, 1, 0.05, 1, Nu, S, F, F, F).Uniform(1)),
                ("symmetry edges only", () => PlateModel.Rect(1, 1, 0.05, 1, Nu, EdgeBc.Symmetric, EdgeBc.Symmetric, EdgeBc.Symmetric, EdgeBc.Symmetric).Uniform(1)),
                ("a hinge line", () => { var m = PlateModel.Rect(1, 1, 0.05, 1, Nu, F, F, F, F).Uniform(1).Mesh(48, 48); m.Lines.Add(new LineSupport('x', 0.3)); return m; }),
                ("a free plate on one rail", () => { var m = PlateModel.Rect(100, 100, 5, 69000, 0.33, F, F, F, F).Uniform(1); m.Lines.Add(new LineSupport('y', 50)); return m; }),
            };
            foreach (var (name, make) in cases)
            {
                var ex = Record.Exception(() => MindlinPlateSolver.Solve(make()));
                Assert.True(ex is InvalidOperationException && ex.Message.Contains("mechanism"), name + ": " + (ex?.Message ?? "no exception"));
            }
            // and the legitimate extremes still solve: a very thin plate on a coarse mesh, two rails, a pillar cell
            Assert.True(Math.Abs(MindlinPlateSolver.Solve(PlateModel.Rect(1, 1, 0.001, 1, Nu, S, S, S, S).Uniform(1).Mesh(8, 8)).WMax) > 0);
            var rails = PlateModel.Rect(100, 100, 5, 69000, 0.33, F, F, F, F).Uniform(1);
            rails.Lines.Add(new LineSupport('y', 20)); rails.Lines.Add(new LineSupport('y', 80));
            Assert.InRange(MindlinPlateSolver.Solve(rails).WMax, 0.01, 1);
        }

        [Fact]
        public void Bad_geometry_and_oversized_models_throw_before_solving()
        {
            Assert.Throws<ArgumentException>(() => MindlinPlateSolver.Solve(PlateModel.Rect(1, 1, 0.1, 1, Nu, S, S, S, S).Also(m => m.Pressures.Add(new PressurePatch(0.6, 0.2, 0, 1, 1)))));
            Assert.Throws<ArgumentException>(() => MindlinPlateSolver.Solve(PlateModel.Rect(1, 1, 0.1, 1, Nu, S, S, S, S).Uniform(1).Also(m => m.Springs.Add(new PointSpring(1.5, 0.5, 10)))));
            Assert.Throws<ArgumentException>(() => MindlinPlateSolver.Solve(PlateModel.Rect(1, 1, 0.1, 1, Nu, F, F, F, F).Uniform(1).Also(m => m.Fixed.Add(new FixedRegion(0.2, 1.2, 0.2, 0.4)))));
            Assert.Throws<ArgumentException>(() => MindlinPlateSolver.Solve(PlateModel.Rect(1, 1, 0.1, 1, Nu, F, F, F, F).Uniform(1).Also(m => m.Lines.Add(new LineSupport('y', -0.1)))));
            // 1000 x 1 mm at 24 elements on the short side: about 1.2 GB of band; refused before allocating
            var big = PlateModel.Rect(1000, 1, 0.1, 1, Nu, S, S, S, S).Uniform(1);
            var ex = Assert.Throws<InvalidOperationException>(() => MindlinPlateSolver.Solve(big));
            Assert.Contains("too large", ex.Message);
        }

        [Fact]
        public void Pressure_field_follows_an_outline_off_the_grid_lines()
        {
            // a disc of radius 0.3 at the centre, mesh 10 x 10 (outline cuts elements): the applied load is pi r^2 within 1%
            var m = PlateModel.Rect(1, 1, 0.05, 1, Nu, S, S, S, S).Mesh(10, 10);
            m.PressureField = (x, y) => (x - 0.5) * (x - 0.5) + (y - 0.5) * (y - 0.5) <= 0.09 ? 1 : 0;
            var r = MindlinPlateSolver.Solve(m);
            Assert.InRange(r.AppliedLoad / (Math.PI * 0.09), 0.99, 1.01);
        }

        [Fact]
        public void Clamped_head_face_moment_converges_and_exceeds_the_mid_span()
        {
            // a pillar cell (a/b 1, t/b 0.1): the hogging on the head face (read 0.05 b from the corner) converges under refinement
            // and is about 3 x the mid-span moment, so the Pillar rows' beta is the hogging, not the mid-span value
            double Face(int n)
            {
                var m = SwAgent.Mold.Structural.Fe.Sweep.CoefficientSweep.Model("Pillar", 1, 0.1, 0.3, 1, n, clampedHeads: true);
                return SwAgent.Mold.Structural.Fe.Sweep.CoefficientSweep.HeadFaceMoment(MindlinPlateSolver.Solve(m), m, 0.05);
            }
            double f48 = Face(48), f96 = Face(96);
            _out.WriteLine($"head face moment: 48 {6 * f48:F4}, 96 {6 * f96:F4}");
            Assert.InRange(f96 / f48, 0.995, 1.005);
            var row = SwAgent.Mold.Structural.Fe.Sweep.CoefficientSweep.Compute("Pillar", 1, 0.1, 0.3, 1);
            Assert.Equal(6 * f48, row.Beta, 6);
            var w = MindlinPlateSolver.Solve(SwAgent.Mold.Structural.Fe.Sweep.CoefficientSweep.Model("Pillar", 1, 0.1, 0.3, 1, 48));
            double mid = w.MaxMoment((x, y) => Math.Abs(x - 0.5) < 1e-9 || Math.Abs(y - 0.5) < 1e-9, out _);
            Assert.InRange(row.Beta / (6 * mid), 2.5, 3.5);
        }

        [Fact]
        public void Stiff_spring_equals_a_point_support_and_line_support_equals_edge()
        {
            // a rigid line support at y = b on a C-F-F-F strip equals the same strip with the far edge soft-supported
            var a = PlateModel.Rect(1, 2, 0.05, 1, Nu, F, F, C, F).Uniform(1); a.Lines.Add(new LineSupport('y', 2)); a.Mesh(8, 16);
            var b = PlateModel.Rect(1, 2, 0.05, 1, Nu, F, F, C, EdgeBc.SimpleSoft).Uniform(1); b.Mesh(8, 16);
            Assert.Equal(MindlinPlateSolver.Solve(b).WMax, MindlinPlateSolver.Solve(a).WMax, 12);
            // a very stiff spring at a node behaves like a fixed node
            var s = PlateModel.Rect(1, 1, 0.05, 1, Nu, S, S, S, S).Uniform(1); s.Springs.Add(new PointSpring(0.5, 0.5, 1e9)); s.Mesh(10, 10);
            var f = PlateModel.Rect(1, 1, 0.05, 1, Nu, S, S, S, S).Uniform(1); f.Fixed.Add(new FixedRegion(0.5, 0.5, 0.5, 0.5)); f.Mesh(10, 10);
            Assert.Equal(MindlinPlateSolver.Solve(f).WMax, MindlinPlateSolver.Solve(s).WMax, 5);
        }

        [Fact]
        public void Unilateral_foundation_lets_the_plate_lift()
        {
            // a free plate on a tensionless foundation, loaded near one end and lifted at the other by a point load:
            // the bilateral answer pulls the foundation; the unilateral one drops the tension zone and lifts further
            var m = PlateModel.Rect(2, 0.5, 0.05, 1000, Nu, F, F, F, F);
            m.Pressures.Add(new PressurePatch(0, 0.6, 0, 0.5, 1));
            m.PointLoads.Add(new PointLoad(2, 0.25, -0.05));
            m.Foundations.Add(new WinklerRegion(0, 2, 0, 0.5, 20, unilateral: true));
            var r = MindlinPlateSolver.Solve(m);
            m.Foundations[0].Unilateral = false;
            var b = MindlinPlateSolver.Solve(m);
            Assert.True(r.Passes > 1 && r.ContactFraction < 1, "some contact is lost");
            Assert.True(r.WAt(2, 0.25) < b.WAt(2, 0.25), "the free end lifts more without tension springs");
            Assert.True(Math.Abs(r.AppliedLoad + r.FoundationForce) < 1e-9 * Math.Abs(r.AppliedLoad));
        }
    }
}

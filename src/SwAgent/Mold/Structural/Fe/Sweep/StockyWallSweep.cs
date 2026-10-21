using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>
    /// Sweep 4: the wall sizing (Walls.Deflection with the side condition, the 3D root factor, its 1.06 and the floor-stretch
    /// term) against the 3D pocket over the range the sizing meets (2026-10-21; Walls.OwnBulgeFactor has the story). The
    /// quarter pocket of Solid3dValidation.WallPocket: 1,920 cases at h/8 and h/12, extrapolated at first order like the Monte
    /// Carlo's truth (w/h 0.3-3), plus 630 short narrow pockets on fine meshes h/12-h/24 at the observed order (w/h 0.1-0.5,
    /// where a coarse mesh is too stiff). Dimensionless (p = h = E = 1). Passes when no case bulges more than the calculator,
    /// with Margin to spare.
    /// </summary>
    public static class StockyWallSweep
    {
        /// <summary>The fit leaves this much room on every case: 3D / calculator at most 1 / Margin.</summary>
        public const double Margin = 1.02;

        public static readonly double[] LOverH = { 0.5, 1, 2, 3.33, 4, 6, 9, 12 };
        public static readonly double[] BOverH = { 0.5, 1, 2, 4, 8 };
        public static readonly double[] Tau = { 0.3, 0.4, 0.5, 0.75, 1, 1.5, 2, 3 };
        public static readonly double[] FloorOverH = { 0.25, 0.5, 1.0 };
        public static readonly double[] Nu = { 0.29, 0.33 };

        public sealed class Case
        {
            public double L, B, Tau, Floor, Nu, Solid, Calc;
            public bool Converged, Fine;
            public double Ratio => Calc > 0 ? Solid / Calc : double.NaN;

            public string Csv() => string.Join(",", new[] { L, B, Tau, Floor, Nu, Solid, Calc, Ratio }.Select(x => x.ToString("G6", CultureInfo.InvariantCulture))) + "," + (Converged ? 1 : 0) + "," + (Fine ? "fine" : "coarse");
        }

        /// <summary>The calculator's bulge for a case (Walls.Deflection with the side condition, the 3D root factor and the fit the sizing uses).</summary>
        public static double Calculator(double l, double b, double tau, double floor, double nu)
        {
            var clamped = Walls.SidesClampedFor(l, b, 1);
            return Walls.Deflection(new WallInput
            {
                Id = "S", P = 1, H = 1, L = l, BPerp = b, E = 1, Nu = nu, DeltaAllow = 1, Floor = floor, SidesClamped = clamped,
                RootSlope = Walls.DefaultRootSlope, AllowStatic = 1e12, AllowFatigue = 1e12,
            }, clamped ? "Wall-C" : "Wall-S", tau);
        }

        /// <summary>The short-pocket check (fine meshes h/12, h/16, h/24, observed order): walls down to w/h 0.1 in deep narrow pockets.</summary>
        public static readonly double[] ShortL = { 0.5, 1, 1.5, 2, 2.5, 3, 3.33 }, ShortB = { 0.25, 0.5, 0.75, 1, 1.5 }, ShortTau = { 0.1, 0.15, 0.2, 0.3, 0.4, 0.5 };

        /// <summary>The 3D pocket's bulge on three fine meshes, extrapolated at the observed order (at least 1).</summary>
        public static (double W, bool Converged) SolidFine(double l, double b, double tau, double floor, double nu)
        {
            var c = new Solid3dValidation.WallCase("sweep4-short", l, b, tau, floor) { Nu = nu };
            var a = Solid3dValidation.WallPocket(c, 3, false);
            var m = Solid3dValidation.WallPocket(c, 4, false);
            var z = Solid3dValidation.WallPocket(c, 6, false);
            var p = Solid3dValidation.ObservedOrder(1.0 / 12, a.W, 1.0 / 16, m.W, 1.0 / 24, z.W);
            return (Solid3dValidation.Extrapolate(1.0 / 16, m.W, 1.0 / 24, z.W, double.IsNaN(p) ? 1 : Math.Max(1, p)), a.R.Converged && m.R.Converged && z.R.Converged);
        }

        /// <summary>The 3D pocket's wall-top bulge for a case (h/8 and h/12, extrapolated at first order).</summary>
        public static (double W, bool Converged) Solid(double l, double b, double tau, double floor, double nu)
        {
            var c = new Solid3dValidation.WallCase("sweep4", l, b, tau, floor) { Nu = nu };
            var (w1, _, r1) = Solid3dValidation.WallPocket(c, 2, false);
            var (w2, _, r2) = Solid3dValidation.WallPocket(c, 3, false);
            return (Solid3dValidation.Extrapolate(1.0 / 8, w1, 1.0 / 12, w2, 1), r1.Converged && r2.Converged);
        }

        /// <summary>The coarse grid (h/8, h/12) over the whole range, then the short-pocket grid on fine meshes (nu 0.33).</summary>
        public static List<Case> Run(Action<string> log = null, CancellationToken ct = default)
        {
            var grid = (from l in LOverH from b in BOverH from t in Tau from f in FloorOverH from n in Nu select (l, b, t, f, n, fine: false))
                .Concat(from l in ShortL from b in ShortB from t in ShortTau from f in FloorOverH select (l, b, t, f, n: 0.33, fine: true)).ToList();
            var done = new ConcurrentBag<Case>();
            var count = 0;
            Parallel.ForEach(grid, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, g =>
            {
                var (w, ok) = g.fine ? SolidFine(g.l, g.b, g.t, g.f, g.n) : Solid(g.l, g.b, g.t, g.f, g.n);
                done.Add(new Case { L = g.l, B = g.b, Tau = g.t, Floor = g.f, Nu = g.n, Fine = g.fine, Solid = w, Calc = Calculator(g.l, g.b, g.t, g.f, g.n), Converged = ok });
                var k = Interlocked.Increment(ref count);
                if (k % 200 == 0) log?.Invoke($"  {k} / {grid.Count} pockets");
            });
            return done.OrderBy(c => c.Fine).ThenBy(c => c.L).ThenBy(c => c.B).ThenBy(c => c.Tau).ThenBy(c => c.Floor).ThenBy(c => c.Nu).ToList();
        }

        /// <summary>What fails: unconverged pockets and cases above 1 / Margin.</summary>
        public static List<string> Failures(IEnumerable<Case> cases) =>
            cases.Where(c => !c.Converged).Select(c => $"not converged: L/h {c.L}, B/h {c.B}, w/h {c.Tau}, floor {c.Floor}, nu {c.Nu}")
                .Concat(cases.Where(c => c.Ratio * Margin > 1).Select(c => $"3D bulge {c.Ratio:F3} x the calculator's: L/h {c.L}, B/h {c.B}, w/h {c.Tau}, floor {c.Floor}, nu {c.Nu}"))
                .ToList();

        public const string CsvHeader = "LoverH,BoverH,tau,floorOverH,nu,solid,calc,ratio,converged,mesh";
    }
}

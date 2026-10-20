using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>One row of coefficients.csv.</summary>
    public sealed class CoefficientRow
    {
        public string Bc;
        public double Aspect, Tau, Nu, LoadFrac, Alpha, Beta, WMaxX, WMaxY;

        public string ToCsv() => string.Join(",", Bc, F(Aspect), F(Tau), F(Nu), F(LoadFrac), G(Alpha), G(Beta), F(WMaxX), F(WMaxY));
        static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        static string G(double v) => v.ToString("G7", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Sweep 1 (design 3.20): Mindlin coefficients alpha, beta for each boundary set over aspect, tau = t/b, nu and the loaded
    /// fraction of a centred patch, from the MITC4 solver. Linear plates are self-similar, so only these shape parameters are
    /// swept: w = alpha p b^4 / (E t^3), sigma = beta p b^2 / t^2.
    ///
    /// Geometry (b = 1 along y, a = aspect along x):
    ///   SS4       all four edges hard simply supported.
    ///   C4        all four edges clamped.
    ///   Rails     y = 0 and y = b hard SS (the rails, span b), x edges free; a = rail length.
    ///   C2S2      y edges (length a, the long ones when aspect >= 1) clamped, x edges hard SS.
    ///   Wall-C    cavity wall: b = loaded height h, a = wall length L; bottom (y = 0) clamped, top free, sides clamped (pocket milled from solid).
    ///   Wall-S    as Wall-C with the sides simply supported (bolted frame corners).
    ///   Overhang  cantilever plate: clamped on y = 0, free elsewhere; b = overhang length c, a = width along the support.
    ///   Pillar    interior panel of a pillar grid, the periodic cell a x b: every edge a symmetry line, a rigid square pillar
    ///             head of side 0.2 b at each corner (a quarter per corner). alpha from heads that hold w only (the plate rests on
    ///             them; 3D: 0.92-1.14 x this, so A1 multiplies Pillar alpha by the pillar head factor of solid3d-summary.json).
    ///             beta = max(the mid-span moment on the cell centre lines, the hogging moment on the head faces): the faces'
    ///             normal moment from the rotation reactions of a clamped head (the plate cannot rotate on a head it is pressed
    ///             onto), read at least PillarCornerExclusion from the head's corner, where plate theory is singular. That value
    ///             converges under refinement and covers the 3D top-surface peak for t/b >= 0.1 (solid3d-summary.json).
    /// alpha uses the largest |w| anywhere; beta the largest principal moment (clamped edges from reactions, free edges from the
    /// first Gauss row with the natural conditions).
    /// </summary>
    public static class CoefficientSweep
    {
        public const string Version = "a2-2";
        public static readonly string[] BcSets = { "SS4", "C4", "Rails", "C2S2", "Wall-C", "Wall-S", "Overhang", "Pillar" };
        /// <summary>The design's 12 aspects plus intermediate rows, so interpolation between rows stays within 1% of the FE.</summary>
        public static readonly double[] PlateAspects = { 0.5, 0.5625, 0.625, 0.6875, 0.75, 0.875, 1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.8, 2.0, 2.25, 2.5, 2.75, 3.0, 3.5, 4.0, 5.0, 6.0 };
        /// <summary>Walls also get L/h = 7, 8, 10 and 12 (critique A4); above 12 the closed-form strip with shear applies.</summary>
        public static readonly double[] WallAspects = { 0.5, 0.5625, 0.625, 0.6875, 0.75, 0.875, 1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.8, 2.0, 2.25, 2.5, 2.75, 3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0, 7.0, 8.0, 10.0, 12.0 };
        public static readonly double[] Taus = { 0.01, 0.05, 0.075, 0.1, 0.15, 0.2, 0.25, 0.3, 0.35, 0.4, 0.45, 0.5 };
        public static readonly double[] Nus = { 0.29, 0.33 };
        public static readonly double[] LoadFracs = { 0.25, 0.5, 0.75, 1.0 };
        public const double PillarHead = 0.2;

        public static bool IsWall(string bc) => bc == "Wall-C" || bc == "Wall-S";
        static bool TakesPatches(string bc) => bc == "SS4" || bc == "C4" || bc == "Rails" || bc == "C2S2";

        /// <summary>Every (bc, aspect, tau, nu, loadFrac) of the table, in file order.</summary>
        public static IEnumerable<(string bc, double aspect, double tau, double nu, double lf)> Grid()
        {
            foreach (var bc in BcSets)
                foreach (var asp in IsWall(bc) ? WallAspects : PlateAspects)
                    foreach (var nu in Nus)
                        foreach (var lf in TakesPatches(bc) ? LoadFracs : new[] { 1.0 })
                            foreach (var tau in Taus)
                                yield return (bc, asp, tau, nu, lf);
        }

        /// <summary>Default mesh: 48 elements on the short side (24 left thin clamped plates up to 0.9% short of the converged values).</summary>
        public const int DefaultElements = 48;
        /// <summary>beta leaves out the plate within 0.1 b of a clamped-free corner (a Mindlin singularity: the value there grows
        /// without bound under refinement for t/b >= 0.1; real corners are radiused and carry K_f).</summary>
        public const double CornerExclusion = 0.1;
        /// <summary>Hogging on a pillar head face is read at least this far (x b, = a quarter of the head side) from the head's corner.</summary>
        public const double PillarCornerExclusion = 0.05;

        static bool SymX(string bc) => true;
        static bool SymY(string bc) => !IsWall(bc) && bc != "Overhang";

        /// <summary>
        /// The plate for one row. reduced: the quarter (or, for walls and overhangs, the half) cut on the symmetry lines, which
        /// carry EdgeBc.Symmetric; the element size stays that of the full plate, so the results equal the full model's.
        /// </summary>
        public static PlateModel Model(string bc, double aspect, double tau, double nu, double loadFrac, int elementsOnShortSide = DefaultElements, bool reduced = true, bool clampedHeads = false)
        {
            const EdgeBc S = EdgeBc.SimpleHard, C = EdgeBc.Clamped, F = EdgeBc.Free, Y = EdgeBc.Symmetric;
            double a = aspect, b = 1;
            PlateModel m;
            switch (bc)
            {
                case "SS4": m = PlateModel.Rect(a, b, tau, 1, nu, S, S, S, S); break;
                case "C4": m = PlateModel.Rect(a, b, tau, 1, nu, C, C, C, C); break;
                case "Rails": m = PlateModel.Rect(a, b, tau, 1, nu, F, F, S, S); break;
                case "C2S2": m = PlateModel.Rect(a, b, tau, 1, nu, S, S, C, C); break;
                case "Wall-C": m = PlateModel.Rect(a, b, tau, 1, nu, C, C, C, F); break;
                case "Wall-S": m = PlateModel.Rect(a, b, tau, 1, nu, S, S, C, F); break;
                case "Overhang": m = PlateModel.Rect(a, b, tau, 1, nu, F, F, C, F); break;
                case "Pillar": m = PlateModel.Rect(a, b, tau, 1, nu, Y, Y, Y, Y); break;
                default: throw new ArgumentException("Unknown boundary set " + bc);
            }
            m.ElementSize = Math.Min(a, b) / elementsOnShortSide;
            m.MaxMemoryMB = 4000; // the dev sweep runs outside SolidWorks; a refined L/h 12 wall takes about 400 MB
            bool sx = reduced && SymX(bc), sy = reduced && SymY(bc);
            double ra = sx ? a / 2 : a, rb = sy ? b / 2 : b;
            m.A = ra; m.B = rb;
            if (sx) m.XA = Y; else m.XLines.Add(a / 2);
            if (sy) m.YB = Y; else m.YLines.Add(b / 2);
            if (bc == "Pillar")
            {
                double h = PillarHead * b / 2;
                m.Fixed.Add(new FixedRegion(0, h, 0, h, clampedHeads));
                if (!sx) m.Fixed.Add(new FixedRegion(a - h, a, 0, h, clampedHeads));
                if (!sy) m.Fixed.Add(new FixedRegion(0, h, b - h, b, clampedHeads));
                if (!sx && !sy) m.Fixed.Add(new FixedRegion(a - h, a, b - h, b, clampedHeads));
            }
            if (loadFrac >= 1 || !TakesPatches(bc)) m.Uniform(1);
            else
            {
                double s = Math.Sqrt(loadFrac), pa = a * s, pb = b * s;
                m.Pressures.Add(new PressurePatch((a - pa) / 2, Math.Min(ra, (a + pa) / 2), (b - pb) / 2, Math.Min(rb, (b + pb) / 2), 1));
            }
            return m;
        }

        /// <summary>Corners (and pillar heads) whose neighbourhood beta leaves out, in the model's coordinates.</summary>
        static List<(double X, double Y)> Excluded(string bc, PlateModel m, double aspect)
        {
            var list = new List<(double, double)>();
            bool full = Math.Abs(m.A - aspect) < 1e-12;
            if (bc == "Wall-C") { list.Add((0, 1)); if (full) list.Add((aspect, 1)); }
            if (bc == "Overhang") { list.Add((0, 0)); if (full) list.Add((aspect, 0)); }
            return list;
        }

        public static CoefficientRow Compute(string bc, double aspect, double tau, double nu, double loadFrac, int elementsOnShortSide = DefaultElements, CancellationToken ct = default, bool reduced = true)
        {
            var m = Model(bc, aspect, tau, nu, loadFrac, elementsOnShortSide, reduced);
            var r = MindlinPlateSolver.Solve(m, ct);
            double mMax = r.MMax;
            const double eps = 1e-9;
            if (bc == "Pillar")
            {
                // sagging: the mid-span moments on the cell's centre lines x = a/2 and y = b/2 (the panel centre and the points
                // midway between neighbouring pillars). Hogging: the head faces of the same cell with the head clamped, read at
                // least PillarCornerExclusion from the head's corner (the w-only head's moments next to the face keep growing
                // under refinement; the clamped face moment from the reactions converges to 0.1-0.3% from 48 to 96 elements).
                double mid = r.MaxMoment((x, y) => Math.Abs(x - aspect / 2) < eps || Math.Abs(y - 0.5) < eps, out _);
                var mc = Model(bc, aspect, tau, nu, loadFrac, elementsOnShortSide, reduced, clampedHeads: true);
                var rc = MindlinPlateSolver.Solve(mc, ct);
                mMax = Math.Max(mid, HeadFaceMoment(rc, mc, PillarCornerExclusion));
            }
            else
            {
                var corners = Excluded(bc, m, aspect);
                if (corners.Count > 0)
                    mMax = r.MaxMoment((x, y) => corners.All(c => Math.Sqrt((x - c.X) * (x - c.X) + (y - c.Y) * (y - c.Y)) > CornerExclusion + eps), out _);
            }
            return new CoefficientRow
            {
                Bc = bc, Aspect = aspect, Tau = tau, Nu = nu, LoadFrac = loadFrac,
                Alpha = Math.Abs(r.WMax) * tau * tau * tau,
                Beta = 6 * mMax,
                WMaxX = r.WMaxAt.X / aspect, WMaxY = r.WMaxAt.Y,
            };
        }

        /// <summary>
        /// Largest |normal moment| on the faces of the model's clamped fixed regions (pillar heads), at least rc from every head
        /// corner that lies inside the plate (corners on the plate's edges are symmetry points, not corners): the nodal values
        /// there plus the value interpolated at exactly rc, so the result does not depend on where the grid lines fall.
        /// </summary>
        public static double HeadFaceMoment(PlateResult r, PlateModel m, double rc)
        {
            double tol = 1e-9 * Math.Max(m.A, m.B), best = 0;
            bool Inside(double x, double y) => x > tol && x < m.A - tol && y > tol && y < m.B - tol;
            int Find(double[] g, double v) { for (int k = 0; k < g.Length; k++) if (Math.Abs(g[k] - v) <= tol) return k; return -1; }
            foreach (var h in m.Fixed.Where(f => f.Clamped))
            {
                var corners = new[] { (h.X1, h.Y1), (h.X1, h.Y2), (h.X2, h.Y1), (h.X2, h.Y2) }.Where(c => Inside(c.Item1, c.Item2)).ToList();
                // faces x = const (moment Mx, running along y) and y = const (My, along x); only faces inside the plate
                foreach (var (xFace, at, lo, hi) in new[] { (true, h.X1, h.Y1, h.Y2), (true, h.X2, h.Y1, h.Y2), (false, h.Y1, h.X1, h.X2), (false, h.Y2, h.X1, h.X2) })
                {
                    if (at <= tol || at >= (xFace ? m.A : m.B) - tol) continue;
                    var along = xFace ? r.Ys : r.Xs;
                    int fi = Find(xFace ? r.Xs : r.Ys, at);
                    if (fi < 0) continue;
                    double Mn(int k) => Math.Abs(xFace ? r.Mx[fi, k] : r.My[k, fi]);
                    var ends = corners.Where(c => Math.Abs((xFace ? c.Item1 : c.Item2) - at) <= tol).Select(c => xFace ? c.Item2 : c.Item1).ToList();
                    double Dist(double s) => ends.Count == 0 ? double.PositiveInfinity : ends.Min(e => Math.Abs(s - e));
                    for (int k = 0; k < along.Length; k++)
                        if (along[k] >= lo - tol && along[k] <= hi + tol && Dist(along[k]) >= rc - tol) best = Math.Max(best, Mn(k));
                    foreach (var e in ends)
                        foreach (var s in new[] { e - rc, e + rc })
                        {
                            if (s < lo - tol || s > hi + tol) continue;
                            int k = PlateResult.Locate(along, s);
                            double u = (s - along[k]) / (along[k + 1] - along[k]);
                            best = Math.Max(best, (1 - u) * Mn(k) + u * Mn(k + 1));
                        }
                }
            }
            return best;
        }

        public sealed class Report
        {
            public List<CoefficientRow> Rows = new List<CoefficientRow>();
            public List<string> Gates = new List<string>();
            public List<string> Failures = new List<string>();
            public int RefinementChecks;
            public double MaxRefinementChange;
            public string WorstRefinement;
            public TimeSpan Elapsed;
            public Dictionary<string, (double Alpha, double Beta)> InterpolationUnderRead = new Dictionary<string, (double, double)>();
            public double MaxInterpolationUnderRead, MaxInterpolationUnderReadLogLog;
            public bool Passed => Failures.Count == 0;
        }

        /// <summary>
        /// Refinement limit for a row: 0.5%; 1% for the thin-limit rows (tau 0.01, which exist for the Roark gate: a thin plate's
        /// edge layers are narrower than the elements); 3% for thin pillar panels (below tau 0.05 the head-face moment converges
        /// slowly toward the head corner: 2.4% from 48 to 96 elements at tau 0.01).
        /// </summary>
        public static double RefinementLimit(CoefficientRow r) => r.Tau >= 0.05 ? 0.005 : r.Bc == "Pillar" ? 0.03 : 0.01;

        /// <summary>
        /// Runs the whole sweep (parallel), the h-refinement check on a random 5% of the rows (twice the elements; a coefficient
        /// moving more than RefinementLimit fails), and the gates.
        /// </summary>
        public static Report Run(int elementsOnShortSide = DefaultElements, double refineFraction = 0.05, int seed = 1, Action<string> log = null, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var grid = Grid().ToList();
            var rows = new CoefficientRow[grid.Count];
            int done = 0;
            // largest models first, so the tail does not wait on one long wall
            var order = Enumerable.Range(0, grid.Count).OrderByDescending(i => Math.Max(grid[i].aspect, 1 / grid[i].aspect)).ToArray();
            Parallel.ForEach(order, new ParallelOptions { CancellationToken = ct }, i =>
            {
                var g = grid[i];
                rows[i] = Compute(g.bc, g.aspect, g.tau, g.nu, g.lf, elementsOnShortSide, ct);
                int d = Interlocked.Increment(ref done);
                if (log != null && d % 500 == 0) log($"  {d}/{grid.Count} rows, {sw.Elapsed.TotalSeconds:F0} s");
            });
            var rep = new Report { Rows = rows.ToList() };

            // h-refinement on a random subset (half the threads: a fine long wall takes a few hundred MB)
            var rnd = new Random(seed);
            var pick = Enumerable.Range(0, grid.Count).Where(_ => rnd.NextDouble() < refineFraction).ToList();
            var changes = new double[pick.Count];
            Parallel.For(0, pick.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, k =>
            {
                var g = grid[pick[k]];
                var fine = Compute(g.bc, g.aspect, g.tau, g.nu, g.lf, 2 * elementsOnShortSide, ct);
                var c = rows[pick[k]];
                changes[k] = Math.Max(Math.Abs(fine.Alpha / c.Alpha - 1), Math.Abs(fine.Beta / c.Beta - 1));
            });
            rep.RefinementChecks = pick.Count;
            for (int k = 0; k < pick.Count; k++)
                if (changes[k] > rep.MaxRefinementChange)
                {
                    rep.MaxRefinementChange = changes[k];
                    var c = rows[pick[k]];
                    rep.WorstRefinement = $"{c.Bc} aspect {c.Aspect} tau {c.Tau} nu {c.Nu} loadFrac {c.LoadFrac}";
                }
            if (log != null) log($"  refinement check done, {sw.Elapsed.TotalSeconds:F0} s");
            var badRefine = pick.Select((p, k) => (p, k)).Where(x => changes[x.k] > RefinementLimit(rows[x.p])).ToList();
            rep.Gates.Add($"h-refinement {elementsOnShortSide} -> {2 * elementsOnShortSide} elements on {pick.Count} random rows: max change {rep.MaxRefinementChange * 100:F2}% ({rep.WorstRefinement}); limit 0.5% (1% for the tau 0.01 rows, 3% for pillar panels below tau 0.05)");
            foreach (var x in badRefine)
            {
                var c = rows[x.p];
                rep.Failures.Add($"refinement: {c.Bc} aspect {c.Aspect} tau {c.Tau} nu {c.Nu} lf {c.LoadFrac} changed {changes[x.k] * 100:F2}%");
            }

            Gates(rep, elementsOnShortSide, ct);
            InterpolationCheck(rep, elementsOnShortSide, ct);
            if (log != null) log($"  gates and interpolation check done, {sw.Elapsed.TotalSeconds:F0} s");
            rep.Elapsed = sw.Elapsed;
            return rep;
        }

        /// <summary>The release gates of Sweep 1, appended to the report.</summary>
        public static void Gates(Report rep, int elementsOnShortSide = DefaultElements, CancellationToken ct = default)
        {
            // 1. Thin limit (tau = 0.01, nu = 0.3) against Roark Table 11.4 cases 1a and 8a, 1%.
            double worst = 0;
            void Roark(string bc, double[,] tab)
            {
                for (int k = 0; k < tab.GetLength(0); k++)
                {
                    var r = Compute(bc, tab[k, 0], 0.01, 0.3, 1, elementsOnShortSide, ct);
                    double ea = r.Alpha / tab[k, 1] - 1, eb = r.Beta / tab[k, 2] - 1;
                    worst = Math.Max(worst, Math.Max(Math.Abs(ea), Math.Abs(eb)));
                    if (Math.Abs(ea) > 0.01 || Math.Abs(eb) > 0.01)
                        rep.Failures.Add($"Roark {bc} a/b {tab[k, 0]}: alpha {r.Alpha:F5} vs {tab[k, 1]}, beta {r.Beta:F4} vs {tab[k, 2]}");
                }
            }
            Roark("SS4", ClosedForm.RoarkSS);
            Roark("C4", ClosedForm.RoarkClamped);
            rep.Gates.Add($"Roark thin limit (SS4 case 1a, C4 case 8a with beta from the edge reactions), tau 0.01, nu 0.3: worst {worst * 100:F2}%; limit 1%");

            // 2. Every SS4 row against the Navier FSDT series (centre w and centre moment), 0.5%.
            double wn = 0;
            string wnAt = null;
            foreach (var r in rep.Rows.Where(r => r.Bc == "SS4"))
            {
                double a = r.Aspect, s = Math.Sqrt(r.LoadFrac);
                var nav = Navier.Patch(a, 1, r.Tau, 1, r.Nu, 1, a * (1 - s) / 2, a * (1 + s) / 2, (1 - s) / 2, (1 + s) / 2, a / 2, 0.5, true);
                double alpha = nav.W * r.Tau * r.Tau * r.Tau, beta = 6 * Math.Max(Math.Abs(nav.Mx), Math.Abs(nav.My));
                double e = Math.Max(Math.Abs(r.Alpha / alpha - 1), Math.Abs(r.Beta / beta - 1));
                if (e > wn) { wn = e; wnAt = $"aspect {r.Aspect} tau {r.Tau} nu {r.Nu} lf {r.LoadFrac}"; }
                if (e > 0.005) rep.Failures.Add($"Navier SS4 aspect {r.Aspect} tau {r.Tau} nu {r.Nu} lf {r.LoadFrac}: alpha {r.Alpha:G5} vs {alpha:G5}, beta {r.Beta:G5} vs {beta:G5}");
            }
            rep.Gates.Add($"Navier FSDT (199 x 199 terms) on every SS4 row: worst {wn * 100:F2}% ({wnAt}); limit 0.5%");

            // 3. Free edges: the rails rows at tau = 0.01 against the Levy series (no overshoot). The beam's 0.75 is not the target:
            //    the exact thin-plate free-edge moment is 5-6% above it.
            double fe = 0, beamExcess = 0;
            foreach (var r in rep.Rows.Where(r => r.Bc == "Rails" && r.Tau == 0.01 && r.LoadFrac == 1))
            {
                var levy = Levy.RailsFreeEdge(r.Aspect, r.Nu);
                double e = r.Beta / levy.Beta - 1;
                fe = Math.Max(fe, Math.Abs(e));
                beamExcess = Math.Max(beamExcess, r.Beta / 0.75 - 1);
                if (e > 0.005 || e < -0.01) rep.Failures.Add($"free edge rails aspect {r.Aspect} nu {r.Nu}: beta {r.Beta:F4} vs Levy {levy.Beta:F4}");
            }
            double thickMax = rep.Rows.Where(r => r.Bc == "Rails" && r.Tau >= 0.3).Select(r => r.Beta).DefaultIfEmpty(0).Max();
            rep.Gates.Add($"free-edge recovery, rails at tau 0.01 vs the Levy series: worst {fe * 100:F2}% (limit +0.5% / -1%); thin rails beta exceeds the beam's 0.75 by up to {beamExcess * 100:F1}% (physical, not an artefact); at tau >= 0.3 beta <= {thickMax:F3}");

            // 4. Walls (critique A4): alpha and beta rise with L/h toward the cantilever strip with shear. Thin and moderately thick
            //    walls (tau below 0.25) are monotonic all the way and never above the strip; thicker walls (tau >= 0.25) overshoot it slightly
            //    around L/h 6 (a Mindlin edge effect next to the sides, up to about 1%) and settle back onto it, so for them the
            //    rows must be monotonic up to L/h 4 and within 1.5% above the strip. Every L/h 12 row is within 3% of the strip.
            double stripWorst = 0, monoWorst = 0, overWorst = 0;
            foreach (var g in rep.Rows.Where(r => IsWall(r.Bc)).GroupBy(r => (r.Bc, r.Tau, r.Nu)))
            {
                var list = g.OrderBy(r => r.Aspect).ToList();
                var strip = ClosedForm.CantileverStrip(g.Key.Nu, g.Key.Tau);
                bool thick = g.Key.Tau >= 0.25;
                double overLimit = thick ? 1.015 : 1.002;
                for (int k = 0; k < list.Count; k++)
                {
                    var r = list[k];
                    overWorst = Math.Max(overWorst, Math.Max(r.Alpha / strip.Alpha, r.Beta / strip.Beta) - 1);
                    if (r.Alpha > strip.Alpha * overLimit || r.Beta > strip.Beta * overLimit)
                        rep.Failures.Add($"{r.Bc} L/h {r.Aspect} tau {r.Tau} nu {r.Nu}: above the strip (alpha {r.Alpha:F4} / {strip.Alpha:F4}, beta {r.Beta:F4})");
                    if (k > 0 && (!thick || r.Aspect <= 4))
                    {
                        double da = list[k - 1].Alpha / r.Alpha - 1, db = list[k - 1].Beta / r.Beta - 1;
                        monoWorst = Math.Max(monoWorst, Math.Max(da, db));
                        if (da > 0.001 || db > 0.001) rep.Failures.Add($"{r.Bc} tau {r.Tau} nu {r.Nu}: decreases from L/h {list[k - 1].Aspect} to {r.Aspect}");
                    }
                }
                var last = list[list.Count - 1];
                double sa = 1 - last.Alpha / strip.Alpha, sb = 1 - last.Beta / strip.Beta;
                stripWorst = Math.Max(stripWorst, Math.Max(Math.Abs(sa), Math.Abs(sb)));
                if (Math.Abs(sa) > 0.03 || Math.Abs(sb) > 0.03) rep.Failures.Add($"{last.Bc} L/h {last.Aspect} tau {last.Tau}: {sa * 100:F1}% / {sb * 100:F1}% off the strip");
            }
            rep.Gates.Add($"walls: monotonic in L/h (worst drop {monoWorst * 100:F2}%, limit 0.1%; tau >= 0.25 checked up to L/h 4), largest excess over the strip with shear {overWorst * 100:F2}% (limit 0.2%, 1.5% for tau >= 0.25), L/h 12 within {stripWorst * 100:F2}% of the strip (limit 3%). Above L/h 12 use alpha = max(alpha_12, strip), beta = max(beta_12, 3)");
        }

        /// <summary>
        /// How far bilinear interpolation in (aspect, tau^2) under-reads the FE between the rows: the FE at every midpoint
        /// (aspect halfway, tau^2 halfway; nu 0.29 and 0.33; full load) against the table. Positive = the table under-reads
        /// (unconservative). Per boundary set: the worst under-read of alpha and of beta. Gate: 1%.
        /// </summary>
        public static void InterpolationCheck(Report rep, int elementsOnShortSide = DefaultElements, CancellationToken ct = default)
        {
            var table = CoefficientTable.Parse(Header + "\n" + string.Join("\n", rep.Rows.Select(r => r.ToCsv())));
            var tableLog = CoefficientTable.Parse(Header + "\n" + string.Join("\n", rep.Rows.Select(r => r.ToCsv())));
            tableLog.LogLogAspect = true;
            var jobs = new List<(string bc, double asp, double tau, double nu)>();
            foreach (var bc in BcSets)
            {
                var asp = IsWall(bc) ? WallAspects : PlateAspects;
                foreach (var nu in Nus)
                    for (int i = 0; i + 1 < asp.Length; i++)
                        for (int j = 0; j + 1 < Taus.Length; j++)
                            jobs.Add((bc, (asp[i] + asp[i + 1]) / 2, Math.Sqrt((Taus[j] * Taus[j] + Taus[j + 1] * Taus[j + 1]) / 2), nu));
            }
            var under = new (double a, double b)[jobs.Count];
            var underLog = new (double a, double b)[jobs.Count];
            Parallel.For(0, jobs.Count, new ParallelOptions { CancellationToken = ct }, k =>
            {
                var (bc, asp, tau, nu) = jobs[k];
                var fe = Compute(bc, asp, tau, nu, 1, elementsOnShortSide, ct);
                var tb = table.Get(bc, asp, tau, nu, 1);
                var tl = tableLog.Get(bc, asp, tau, nu, 1);
                under[k] = (fe.Alpha / tb.Alpha - 1, fe.Beta / tb.Beta - 1);
                underLog[k] = (fe.Alpha / tl.Alpha - 1, fe.Beta / tl.Beta - 1);
            });
            // thin pillar panels (tau below 0.05) are indicative only (see RefinementLimit); they are left out of the gate
            bool Gated(int k) => !(jobs[k].bc == "Pillar" && jobs[k].tau < 0.05);
            string Report((double a, double b)[] u, out double worst)
            {
                var parts = new List<string>();
                worst = 0;
                foreach (var bc in BcSets)
                {
                    var idx = Enumerable.Range(0, jobs.Count).Where(k => jobs[k].bc == bc && Gated(k)).ToList();
                    double ua = idx.Max(k => u[k].a), ub = idx.Max(k => u[k].b);
                    int ka = idx.OrderByDescending(k => u[k].a).First(), kb = idx.OrderByDescending(k => u[k].b).First();
                    parts.Add($"{bc} {ua * 100:F2}% (aspect {jobs[ka].asp:0.###}, tau {jobs[ka].tau:0.###}) / {ub * 100:F2}% (aspect {jobs[kb].asp:0.###}, tau {jobs[kb].tau:0.###})");
                    worst = Math.Max(worst, Math.Max(ua, ub));
                }
                return string.Join("; ", parts);
            }
            string lin = Report(under, out double worstLin), log = Report(underLog, out double worstLog);
            foreach (var bc in BcSets)
            {
                var idx = Enumerable.Range(0, jobs.Count).Where(k => jobs[k].bc == bc && Gated(k)).ToList();
                rep.InterpolationUnderRead[bc] = (idx.Max(k => under[k].a), idx.Max(k => under[k].b));
            }
            rep.MaxInterpolationUnderRead = worstLin;
            rep.MaxInterpolationUnderReadLogLog = worstLog;
            rep.Gates.Add($"interpolation at {Enumerable.Range(0, jobs.Count).Count(Gated)} midpoints between rows (pillar panels below tau 0.05 left out), worst under-read of the FE (alpha / beta). Bilinear in (aspect, tau^2): {lin}. Log-log in aspect, linear in tau^2 (for comparison): {log}. Limit 1% for bilinear");
            if (worstLin > 0.01) rep.Failures.Add($"bilinear interpolation under-reads the FE by up to {worstLin * 100:F2}%");
        }

        public static string Sha256(IEnumerable<string> dataLines)
        {
            var text = string.Join("\n", dataLines) + "\n";
            using (var h = SHA256.Create())
                return string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }

        public const string Header = "bc,aspect,tau,nu,loadFrac,alpha,beta,wMaxX,wMaxY";

        /// <summary>
        /// The interpolation allowance A1 multiplies interpolated alpha and beta by: the worst under-read of the interpolation check,
        /// rounded up to the next 1% (never below 1.01; the gate fails above 1%).
        /// </summary>
        public static double Allowance(Report rep) => Math.Max(1.01, Math.Ceiling((1 + rep.MaxInterpolationUnderRead) * 100 - 1e-9) / 100);

        /// <summary>
        /// The CSV text: comment lines (#), then the header and rows. The sha256 covers the header and rows. Line 3 is always
        /// "# version: ..." (A1's reader and tools/dev/mold-data/examples_check.py take the table version from it); the lines
        /// "# allowance:", "# wall root:" and "# pillar head:" carry the factors the reader must apply.
        /// </summary>
        public static string ToCsv(Report rep, int elementsOnShortSide, string wallRootNote, DateTime date, string pillarHeadNote = null)
        {
            var data = new List<string> { Header };
            data.AddRange(rep.Rows.Select(r => r.ToCsv()));
            var sb = new StringBuilder();
            sb.Append("# Mindlin plate coefficients for design-compression-mold: w = alpha p b^4 / (E t^3), sigma = beta p b^2 / t^2, tau = t / b.\n");
            sb.Append($"# generator: MoldSweep coefficients, SwAgent.Mold.Structural.Fe.Sweep.CoefficientSweep {Version}; MITC4, {elementsOnShortSide} elements on the short side; {date:yyyy-MM-dd}; {rep.Rows.Count} rows.\n");
            sb.Append($"# version: {Version}\n");
            sb.Append("# allowance: ").Append(Allowance(rep).ToString("0.00", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("# (allowance: multiply interpolated alpha and beta by it; the worst under-read of bilinear interpolation between rows, rounded up. The line above holds the number alone so readers can parse it.)\n");
            sb.Append("# b: plates the y side (the short side when aspect >= 1; rails span; overhang length); walls the loaded height h (aspect = L/h, tau = w/h).\n");
            sb.Append("# bc: SS4 | C4 | Rails (SS y edges, free x edges) | C2S2 (y edges clamped, x edges SS) | Wall-C (bottom C, top free, sides C) | Wall-S (sides SS) | Overhang (C on y = 0, rest free) | Pillar (periodic cell, rigid heads 0.2 b square at the corners).\n");
            sb.Append("# aspect < 1: only SS4 and C4 may be folded (a/b and b/a are the same plate). For Rails, C2S2, Overhang and Pillar an aspect below 1 is a different plate: b stays the y side as defined above.\n");
            sb.Append("# loadFrac: area fraction of a centred patch with the plate's proportions (plates SS4, C4, Rails, C2S2 only). wMaxX, wMaxY: where |w| peaks, as x/a and y/b.\n");
            sb.Append("# nu: 0.29 and 0.33; interpolate linearly (0.03% error at 0.30) and clamp outside. Interpolate bilinearly in (aspect, tau^2). Walls above L/h 12: alpha = max(alpha at 12, 1.5 (1 - nu^2) + 1.2 (1 + nu) tau^2) (cantilever strip with shear), beta = max(beta at 12, 3). Rows are not clamped to the strip: thick walls overshoot it by up to 1% near L/h 6 (a Mindlin effect, real).\n");
            sb.Append("# beta leaves out 0.1 b around clamped-free corners (Wall-C, Overhang: a Mindlin singularity). Pillar beta = max(mid-span moment on the cell centre lines, hogging on the head faces of a clamped rigid head read 0.05 b or more from the head corner); Pillar alpha has heads that hold w only.\n");
            foreach (var g in rep.Gates) sb.Append("# gate: ").Append(g).Append('\n');
            sb.Append("# gates passed: ").Append(rep.Passed ? "yes" : "NO - " + rep.Failures.Count + " failures").Append('\n');
            if (!string.IsNullOrEmpty(wallRootNote)) sb.Append("# wall root: ").Append(wallRootNote).Append('\n');
            if (!string.IsNullOrEmpty(pillarHeadNote)) sb.Append("# pillar head: ").Append(pillarHeadNote).Append('\n');
            sb.Append("# sha256: ").Append(Sha256(data)).Append('\n');
            foreach (var l in data) sb.Append(l).Append('\n');
            return sb.ToString();
        }
    }
}

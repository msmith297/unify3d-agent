using System;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold.Structural.Fe
{
    /// <summary>Plate mesh: elements on the plate's short side, and at least HeadElements across a pillar head.</summary>
    public sealed class MeshSpec
    {
        public int Elements, HeadElements;
        public MeshSpec(int elements, int headElements) { Elements = elements; HeadElements = headElements; }
        public override string ToString() => $"{Elements} elements on the short side, pillar heads {HeadElements} elements wide";
    }

    /// <summary>
    /// The plate FE of a planned layout (design 2.3.2, Track A2 section 3): the base plate as an exact Mindlin plate model -
    /// cavities plus land as pressure patches, rails as line supports (case B), an opening as a simply supported panel (C),
    /// pillars as rigid square heads 0.2 x the short spacing on a plate with simply supported edges (E), the platen as a stiff
    /// tensionless foundation (A) - and the thickness that keeps the land deflection and the stress within their limits.
    ///
    /// The calculator (`Sizing.Size`) finishes support cases B, C and E with it: the coefficient table's thickness is the first
    /// guess, then the plate is solved on two meshes (Coarse 24 and Fine 48 elements on the short side; pillar heads 3 and 6
    /// elements wide), extrapolated to the converged value and sized to Target of the limits (three or four solves). The
    /// Monte Carlo (Sweep 2) checks the result on a finer truth mesh that the sizing never uses.
    ///
    /// The plate is described in the Monte Carlo's JSON (schema "unify3d.mold-mc/1", mm and MPa, from the plate's corner):
    ///   { support: "A"|"B"|"C"|"E", plate: { length_mm, width_mm }, cavities: [ { x_mm, y_mm, length_mm, width_mm } ], land_mm,
    ///     pressure_MPa, kf, delta_allow_mm, rails: { y1_mm, y2_mm } (B), opening: { x1_mm, y1_mm, x2_mm, y2_mm } (C),
    ///     pillars: { xs_mm: [], ys_mm: [] } (E), uniform_MPa (optional: a pressure over the whole modelled plate) }
    /// </summary>
    public static class LayoutPlate
    {
        /// <summary>The two meshes the sizing extrapolates from (never the Monte Carlo truth's).</summary>
        public static readonly MeshSpec Coarse = new MeshSpec(24, 3), Fine = new MeshSpec(48, 6);

        /// <summary>The layout FE sizes to this fraction of the limits: the extrapolation's own error (about 0.1%) stays inside.</summary>
        public const double Target = 0.995;

        /// <summary>Band storage cap for the solver inside SolidWorks (MB).</summary>
        public const int AddInMemoryMB = 400;

        /// <summary>The plate model of a design at thickness t, its x/y offset (case C models the opening only) and the span.</summary>
        public static PlateModel Model(JObject d, double t, double e, double nu, MeshSpec mesh, bool clampedHeads, out double ox, out double oy, out double span, int maxMemoryMB = AddInMemoryMB)
        {
            string support = (string)d["support"];
            double pl = (double)d["plate"]["length_mm"], pw = (double)d["plate"]["width_mm"], p = (double)d["pressure_MPa"], land = (double?)d["land_mm"] ?? 3.175;
            PlateModel m;
            ox = 0; oy = 0;
            double shortSide;
            switch (support)
            {
                case "A":
                    m = PlateModel.Rect(pl, pw, t, e, nu, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free);
                    m.Foundations.Add(new WinklerRegion(0, pl, 0, pw, 1e4, unilateral: true)); // a rigid platen through a thin contact layer
                    span = shortSide = Math.Min(pl, pw);
                    break;
                case "B":
                    m = PlateModel.Rect(pl, pw, t, e, nu, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free);
                    double y1 = (double)d["rails"]["y1_mm"], y2 = (double)d["rails"]["y2_mm"];
                    m.Lines.Add(new LineSupport('y', y1)); m.Lines.Add(new LineSupport('y', y2));
                    span = y2 - y1; shortSide = Math.Min(pl, pw);
                    break;
                case "C":
                    var o = d["opening"];
                    ox = (double)o["x1_mm"]; oy = (double)o["y1_mm"];
                    double ol = (double)o["x2_mm"] - ox, ow = (double)o["y2_mm"] - oy;
                    m = PlateModel.Rect(ol, ow, t, e, nu, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard);
                    span = shortSide = Math.Min(ol, ow);
                    break;
                case "E":
                    m = PlateModel.Rect(pl, pw, t, e, nu, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard);
                    var xs = d["pillars"]["xs_mm"].Select(v => (double)v).ToList();
                    var ys = d["pillars"]["ys_mm"].Select(v => (double)v).ToList();
                    span = PillarSpan(d);
                    shortSide = Math.Min(pl, pw);
                    double hh = Sweep.CoefficientSweep.PillarHead * span / 2;
                    foreach (var px in xs) foreach (var py in ys) m.Fixed.Add(new FixedRegion(px - hh, px + hh, py - hh, py + hh, clampedHeads));
                    break;
                default: throw new ArgumentException("Unknown support " + support);
            }
            m.ElementSize = shortSide / mesh.Elements;
            if (support == "E") m.ElementSize = Math.Min(m.ElementSize, Sweep.CoefficientSweep.PillarHead * span / mesh.HeadElements);
            m.MaxMemoryMB = maxMemoryMB;
            double mw = support == "C" ? m.A : pl, mh = support == "C" ? m.B : pw;
            // the press force beyond what the cavities carry (the calculator spreads it over the footprint)
            if ((double?)d["uniform_MPa"] is double q && q > 0) m.Pressures.Add(new PressurePatch(0, mw, 0, mh, q));
            foreach (var c in d["cavities"])
            {
                double cx = (double)c["x_mm"], cy = (double)c["y_mm"], cl = (double)c["length_mm"], cw = (double)c["width_mm"];
                // a patch that reaches past the modelled plate (an opening smaller than the cavity layout) is clipped to it
                double x1 = Math.Max(0, cx - land - ox), x2 = Math.Min(mw, cx + cl + land - ox), y1p = Math.Max(0, cy - land - oy), y2p = Math.Min(mh, cy + cw + land - oy);
                if (x2 <= x1 || y2p <= y1p) continue;
                m.Pressures.Add(new PressurePatch(x1, x2, y1p, y2p, p));
                // grid lines on the cavity outline too, so both edges of the land ring are nodes on every mesh (the land is
                // narrower than an element: without them a coarse mesh reads the land only at its outer edge)
                foreach (var x in new[] { cx - ox, cx + cl - ox }) if (x > 0 && x < mw) m.XLines.Add(x);
                foreach (var y in new[] { cy - oy, cy + cw - oy }) if (y > 0 && y < mh) m.YLines.Add(y);
            }
            return m;
        }

        /// <summary>The short pillar spacing of case E: min over x and y of the largest gap between pillars and plate edges.</summary>
        public static double PillarSpan(JObject d)
        {
            double pl = (double)d["plate"]["length_mm"], pw = (double)d["plate"]["width_mm"];
            var gx = new[] { 0.0 }.Concat(d["pillars"]["xs_mm"].Select(v => (double)v)).Concat(new[] { pl }).ToList();
            var gy = new[] { 0.0 }.Concat(d["pillars"]["ys_mm"].Select(v => (double)v)).Concat(new[] { pw }).ToList();
            return Math.Min(Enumerable.Range(0, gx.Count - 1).Max(k => gx[k + 1] - gx[k]), Enumerable.Range(0, gy.Count - 1).Max(k => gy[k + 1] - gy[k]));
        }

        /// <summary>The larger pillar spacing of case E (the cell's other side).</summary>
        public static double PillarSpanLong(JObject d)
        {
            double pl = (double)d["plate"]["length_mm"], pw = (double)d["plate"]["width_mm"];
            var gx = new[] { 0.0 }.Concat(d["pillars"]["xs_mm"].Select(v => (double)v)).Concat(new[] { pl }).ToList();
            var gy = new[] { 0.0 }.Concat(d["pillars"]["ys_mm"].Select(v => (double)v)).Concat(new[] { pw }).ToList();
            return Math.Max(Enumerable.Range(0, gx.Count - 1).Max(k => gx[k + 1] - gx[k]), Enumerable.Range(0, gy.Count - 1).Max(k => gy[k + 1] - gy[k]));
        }

        /// <summary>
        /// r_w and r_s of the plate at thickness t on a mesh: the largest land deflection over delta (case A: the spread between
        /// land points, the plate settles into the platen as a whole), and the largest stress x K_f over the allowable. Case E:
        /// deflection over w-only heads times the 3D pillar-head factor; stress the larger of the plate outside the heads (and 0.1
        /// span around them) and the hogging on the faces of clamped heads, read a quarter of the head side or more from the head
        /// corners (what the Pillar rows tabulate).
        /// </summary>
        public static (double Rw, double Rs, double Span, string Note) Ratios(JObject d, double t, double e, double nu, double allow, MeshSpec mesh, double pillarFactor, int maxMemoryMB = AddInMemoryMB, CancellationToken ct = default)
        {
            string support = (string)d["support"];
            double delta = (double)d["delta_allow_mm"], kf = (double?)d["kf"] ?? 1.0, land = (double?)d["land_mm"] ?? 3.175;
            var cavs = d["cavities"].Select(c => (x: (double)c["x_mm"], y: (double)c["y_mm"], l: (double)c["length_mm"], w: (double)c["width_mm"])).ToList();
            var m = Model(d, t, e, nu, mesh, false, out double ox, out double oy, out double span, maxMemoryMB);
            // the land ring of each cavity: between the outline and the outline + land
            Func<double, double, bool> onLand = (x, y) => cavs.Any(c =>
            {
                double lx = x + ox, ly = y + oy, eps = 1e-6;
                bool inOuter = lx >= c.x - land - eps && lx <= c.x + c.l + land + eps && ly >= c.y - land - eps && ly <= c.y + c.w + land + eps;
                bool inInner = lx > c.x + eps && lx < c.x + c.l - eps && ly > c.y + eps && ly < c.y + c.w - eps;
                return inOuter && !inInner;
            });
            var r = MindlinPlateSolver.Solve(m, ct);
            double wl = Math.Abs(r.MaxW(onLand, out _));
            string note = null;
            if (support == "A")
            {
                double lo = double.MaxValue, hi = double.MinValue;
                for (int i = 0; i < r.Xs.Length; i++)
                    for (int j = 0; j < r.Ys.Length; j++)
                        if (onLand(r.Xs[i], r.Ys[j])) { lo = Math.Min(lo, r.W[i, j]); hi = Math.Max(hi, r.W[i, j]); }
                wl = hi - lo;
                note = $"contact {r.ContactFraction:P0}";
            }
            double mMax = r.MMax;
            if (support == "E")
            {
                wl *= pillarFactor;
                double hs = Sweep.CoefficientSweep.PillarHead * span;
                double band = hs / 2 + Sweep.CoefficientSweep.CornerExclusion * span;
                var heads = m.Fixed.Select(f => ((f.X1 + f.X2) / 2, (f.Y1 + f.Y2) / 2)).ToList();
                double interior = r.MaxMoment((x, y) => heads.All(q => Math.Max(Math.Abs(x - q.Item1), Math.Abs(y - q.Item2)) > band), out _);
                double face = 0;
                if (heads.Count > 0)
                {
                    var mc = Model(d, t, e, nu, mesh, true, out _, out _, out _, maxMemoryMB);
                    var rc = MindlinPlateSolver.Solve(mc, ct);
                    face = Sweep.CoefficientSweep.HeadFaceMoment(rc, mc, Sweep.CoefficientSweep.PillarCornerExclusion / Sweep.CoefficientSweep.PillarHead * hs);
                }
                mMax = Math.Max(interior, face);
                note = face >= interior ? "stress: hogging on a head face" : "stress: between the pillars";
            }
            return (wl / delta, 6 * mMax / (t * t) * kf / allow, span, note);
        }

        /// <summary>
        /// r_w and r_s at thickness t extrapolated to the converged value from Coarse and Fine (Richardson, second order:
        /// x_inf = x_fine + (x_fine - x_coarse) / 3), never below the fine mesh's.
        /// </summary>
        public static (double Rw, double Rs) Extrapolated(JObject d, double t, double e, double nu, double allow, double pillarFactor, int maxMemoryMB = AddInMemoryMB, CancellationToken ct = default)
        {
            var c = Ratios(d, t, e, nu, allow, Coarse, pillarFactor, maxMemoryMB, ct);
            var f = Ratios(d, t, e, nu, allow, Fine, pillarFactor, maxMemoryMB, ct);
            double X(double coarse, double fine) => Math.Max(fine, fine + (fine - coarse) / 3);
            return (X(c.Rw, f.Rw), X(c.Rs, f.Rs));
        }

        /// <summary>What the layout FE settled on: thickness, what governs, the extrapolated ratios there and the solves it took.</summary>
        public sealed class Result
        {
            public double T, Rw, Rs, T0;
            public string Governs;
            public int Solves;
        }

        /// <summary>
        /// The thinnest t whose extrapolated land deflection is within Target x delta, then thickened until the extrapolated stress
        /// fits as well. Deflection goes as t^-3 and stress as t^-2 (both drift slowly with t/b), so a power-law step from the
        /// table's answer t0 lands within 0.2% in three or four solves; a bracket and bisection take over if it does not.
        /// </summary>
        public static Result Size(JObject d, double e, double nu, double allow, double t0, double pillarFactor, int maxMemoryMB = AddInMemoryMB, CancellationToken ct = default)
        {
            int solves = 0;
            (double Rw, double Rs) At(double tt) { solves++; return Extrapolated(d, tt, e, nu, allow, pillarFactor, maxMemoryMB, ct); }
            double t = t0;
            var c = At(t);
            double tOk = double.NaN, tBad = double.NaN;
            (double Rw, double Rs) atOk = default;
            for (int it = 0; it < 12; it++)
            {
                if (c.Rw <= Target) { if (double.IsNaN(tOk) || t < tOk) { tOk = t; atOk = c; } }
                else if (double.IsNaN(tBad) || t > tBad) tBad = t;
                if (!double.IsNaN(tOk) && c.Rw <= Target && c.Rw > Target - 0.002) break;
                double next = t * Math.Pow(c.Rw / Target, 1.0 / 3) * (c.Rw > Target ? 1.001 : 1.0);
                if (!double.IsNaN(tOk) && !double.IsNaN(tBad) && (next >= tOk || next <= tBad)) next = Math.Sqrt(tOk * tBad); // keep inside the bracket
                if (Math.Abs(next / t - 1) < 2e-4) { if (c.Rw > Target) next = t * 1.002; else break; }
                t = next;
                c = At(t);
            }
            if (double.IsNaN(tOk))
            {
                t *= 1.01; c = At(t);
                for (int k = 0; k < 60 && c.Rw > Target; k++) { t *= 1.02; c = At(t); }
                tOk = t; atOk = c;
            }
            t = tOk; c = atOk;
            var res = new Result { T = t, Rw = c.Rw, Rs = c.Rs, T0 = t0, Governs = "deflection" };
            if (c.Rs > Target)
            {
                // stress governs: thicken until the extrapolated stress fits (moments shift a little with t/b, so re-check)
                for (int it = 0; it < 20 && c.Rs > Target; it++)
                {
                    t *= Math.Sqrt(c.Rs / Target) * 1.001;
                    c = At(t);
                }
                res.T = t; res.Rw = c.Rw; res.Rs = c.Rs; res.Governs = "stress";
            }
            res.Solves = solves;
            return res;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SwAgent.Mold.Geometry
{
    /// <summary>The shop's flash-groove standard (lengths in meters). Defaults: the user's "exact" rules (lip 0.125 in, moat 0.5 x 0.125 in, runners every 2 in, R0.25 in, 0.25 in steel to every other feature).</summary>
    public sealed class FlashRules
    {
        public const double Inch = 0.0254;
        public double Lip = 0.125 * Inch;
        public double Width = 0.5 * Inch;
        public double Depth = 0.125 * Inch;
        public double Pitch = 2.0 * Inch;
        public double Radius = 0.25 * Inch;
        public double Clearance = 0.25 * Inch;
        public double PastEdge = 0.1 * Inch;
        /// <summary>Runner search step along a side (0.2 mm).</summary>
        public double SearchStep = 0.0002;
        /// <summary>Cavity outlines are cavities when not a circle under this diameter...</summary>
        public double SmallHoleDiameter = 0.75 * Inch;
        /// <summary>...and the smaller side of their box is at least this.</summary>
        public double MinCavitySide = 0.375 * Inch;

        /// <summary>Cavities closer than this share one moat (2 x (lip + width) + clearance = 1.5 in by default).</summary>
        public double SeparateMoatGap => 2 * (Lip + Width) + Clearance;
    }

    /// <summary>
    /// The 2D half of the flash-groove system, ported from the user's flash-grooves macro (FG macro.cs 172-231): the
    /// cavity outline as a sharp polygon with its corner radii, the lip and moat as rounded offset polygons, and the
    /// runner search. All in sketch coordinates (meters) on the parting face. Pure (GeometryFlashLayoutTests); the
    /// SolidWorks half is FlashSystem.
    /// </summary>
    public static class FlashLayout
    {
        /// <summary>Slack on the separate-moat test (m): a layout planned at exactly the separate-moat gap measures a hair under
        /// it (origins rounded to 6 significant digits, part boxes to 0.001 mm), which must not force a combined moat.</summary>
        public const double SeparateMoatSlack = 1e-5;

        /// <summary>Cavities this close (gap in m) need one combined moat instead of separate ones.</summary>
        public static bool TooCloseForSeparateMoats(double gap, double separateMoatGap) => gap < separateMoatGap - SeparateMoatSlack;

        public static double Cross(double[] a, double[] b) => a[0] * b[1] - a[1] * b[0];
        public static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1] };
        public static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1] };
        public static double[] Mul(double[] a, double k) => new[] { a[0] * k, a[1] * k };
        public static double Len(double[] a) => Math.Sqrt(a[0] * a[0] + a[1] * a[1]);
        public static double[] Unit(double[] a) => Mul(a, 1 / Len(a));

        public static double[] Isect(double[] p1, double[] d1, double[] p2, double[] d2)
        {
            var den = Cross(d1, d2);
            if (Math.Abs(den) < 1e-9) return null;
            var t = Cross(Sub(p2, p1), d2) / den;
            return Add(p1, Mul(d1, t));
        }

        public static double Area(IList<double[]> p)
        {
            double s = 0;
            for (var i = 0; i < p.Count; i++) s += Cross(p[i], p[(i + 1) % p.Count]);
            return s / 2;
        }

        /// <summary>One boundary segment of a cavity outline: a line, or a circular arc of radius R.</summary>
        public sealed class Segment
        {
            public bool IsLine;
            public double[] A, B;
            public double R;
        }

        public sealed class Polygon
        {
            /// <summary>Sharp corners (lines intersected), in loop order.</summary>
            public List<double[]> V = new List<double[]>();
            /// <summary>The corner radius of the outline after each corner (0 = sharp).</summary>
            public List<double> Rc = new List<double>();
        }

        /// <summary>
        /// A straight-sided outline (lines plus corner arcs) as a sharp polygon: lines intersected into corners, collinear
        /// pieces merged (a line split by a seam), the largest arc radius between two lines remembered for that corner.
        /// Null for round, slot or freeform outlines (an arc not between two lines, or a spline), or when the sharp polygon
        /// doesn't match the outline's area (0.98-1.3).
        /// </summary>
        public static Polygon FromSegments(IList<Segment> segs, IList<double[]> outline)
        {
            if (segs.Any(s => s == null || (!s.IsLine && s.R <= 0))) return null;
            var lines = new List<(double[] P, double[] D, double RAfter)>();
            for (var i = 0; i < segs.Count; i++)
            {
                if (!segs[i].IsLine) continue;
                double r = 0;
                for (var j = 1; j < segs.Count && !segs[(i + j) % segs.Count].IsLine; j++) r = Math.Max(r, segs[(i + j) % segs.Count].R);
                var d = Sub(segs[i].B, segs[i].A);
                if (Len(d) < 1e-12) continue;
                d = Unit(d);
                if (lines.Count > 0)
                {
                    var last = lines[lines.Count - 1];
                    if (Math.Abs(Cross(last.D, d)) < 1e-7 && Math.Abs(Cross(Sub(segs[i].A, last.P), last.D)) < 1e-6)
                    {
                        lines[lines.Count - 1] = (last.P, last.D, r);
                        continue;
                    }
                }
                lines.Add((segs[i].A, d, r));
            }
            if (lines.Count > 1)
            {
                var f = lines[0];
                var l = lines[lines.Count - 1];
                if (Math.Abs(Cross(l.D, f.D)) < 1e-7 && Math.Abs(Cross(Sub(f.P, l.P), l.D)) < 1e-6)
                {
                    lines[0] = (l.P, l.D, f.RAfter);
                    lines.RemoveAt(lines.Count - 1);
                }
            }
            if (lines.Count < 3) return null;
            var poly = new Polygon();
            for (var i = 0; i < lines.Count; i++)
            {
                var a = lines[i];
                var b = lines[(i + 1) % lines.Count];
                var x = Isect(a.P, a.D, b.P, b.D);
                if (x == null) return null;
                poly.V.Add(x);
                poly.Rc.Add(a.RAfter);
            }
            var ar = Math.Abs(Area(poly.V));
            var al = Math.Abs(Area(outline));
            if (al < 1e-12 || ar / al < 0.98 || ar / al > 1.3) return null;
            return poly;
        }

        /// <summary>The polygon offset outward (away from its inside) by d, corners kept sharp.</summary>
        public static List<double[]> Offset(IList<double[]> p, double d)
        {
            var sg = Area(p) > 0 ? 1 : -1;
            var n = p.Count;
            var lines = new List<(double[] P, double[] Dir)>();
            for (var i = 0; i < n; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % n];
                var dir = Unit(Sub(b, a));
                var nrm = new[] { dir[1] * sg, -dir[0] * sg };
                lines.Add((Add(a, Mul(nrm, d)), dir));
            }
            var o = new List<double[]>();
            for (var i = 0; i < n; i++)
            {
                var prev = lines[(i + n - 1) % n];
                o.Add(Isect(prev.P, prev.Dir, lines[i].P, lines[i].Dir) ?? lines[i].P);
            }
            return o;
        }

        /// <summary>Corner i turns toward the inside (a convex corner).</summary>
        public static bool Convex(IList<double[]> p, int i)
        {
            var n = p.Count;
            var sg = Area(p) > 0 ? 1 : -1;
            return sg * Cross(Sub(p[i], p[(i + n - 1) % n]), Sub(p[(i + 1) % n], p[i])) > 0;
        }

        public sealed class Corner
        {
            /// <summary>Tangent point on the incoming side, on the outgoing side, the arc centre, the radius actually used.</summary>
            public double[] Ta, Tb, C;
            public double R;
            /// <summary>Arc direction for ISketchManager.CreateArc (+1 counter-clockwise, -1 clockwise).</summary>
            public int Dir;
            public bool HasArc => Len(Sub(Ta, Tb)) > 1e-7;
        }

        /// <summary>
        /// A polygon with rounded corners: per corner the tangent points at t = r / tan(phi/2) (clamped to 49% of the
        /// shorter side, which shrinks the radius), the centre on the bisector. Drawn as arc, then line to the next corner.
        /// </summary>
        public static List<Corner> Rounded(IList<double[]> p, IList<double> radii)
        {
            var n = p.Count;
            var result = new List<Corner>();
            for (var i = 0; i < n; i++)
            {
                var prev = p[(i + n - 1) % n];
                var next = p[(i + 1) % n];
                var a = Unit(Sub(prev, p[i]));
                var b = Unit(Sub(next, p[i]));
                var ph = Math.Acos(Math.Max(-1, Math.Min(1, a[0] * b[0] + a[1] * b[1]))) / 2;
                var r = radii[i];
                var t = r / Math.Tan(ph);
                var lim = 0.49 * Math.Min(Len(Sub(prev, p[i])), Len(Sub(next, p[i])));
                if (t > lim)
                {
                    t = lim;
                    r = t * Math.Tan(ph);
                }
                var ta = Add(p[i], Mul(a, t));
                var tb = Add(p[i], Mul(b, t));
                var c = Add(p[i], Mul(Unit(Add(a, b)), r / Math.Sin(ph)));
                result.Add(new Corner { Ta = ta, Tb = tb, C = c, R = r, Dir = Cross(Sub(ta, c), Sub(tb, c)) > 0 ? 1 : -1 });
            }
            return result;
        }

        /// <summary>The moat around one cavity polygon: the lip (inner) and outer rounded outlines.</summary>
        public sealed class Moat
        {
            public string Id;
            public List<double[]> Inner, Outer;
            public List<double> InnerRadii;
            public List<Corner> InnerCorners, OuterCorners;
        }

        public static Moat MoatFor(string id, Polygon cavity, FlashRules rules)
        {
            var inner = Offset(cavity.V, rules.Lip);
            var outer = Offset(cavity.V, rules.Lip + rules.Width);
            // Lip corner: min(R, cavity corner R + lip) on convex corners (a sharp cavity corner gets R = lip); R elsewhere.
            var rin = Enumerable.Range(0, inner.Count).Select(i => Convex(inner, i) ? Math.Min(rules.Radius, cavity.Rc[i] + rules.Lip) : rules.Radius).ToList();
            return new Moat
            {
                Id = id,
                Inner = inner,
                Outer = outer,
                InnerRadii = rin,
                InnerCorners = Rounded(inner, rin),
                OuterCorners = Rounded(outer, Enumerable.Repeat(rules.Radius, outer.Count).ToList()),
            };
        }

        public sealed class Runner
        {
            public string Moat;
            public int Side;
            /// <summary>Start (on the moat's outer wall, mid-width), outward normal, length to past the plate edge.</summary>
            public double[] P, N;
            public double Length;
            public double At, Ideal;
            public bool Shifted => Math.Abs(At - Ideal) > 1e-4;

            /// <summary>The runner's rectangle (4 corners), from half a width inside the moat wall to its end.</summary>
            public double[][] Rectangle(double width)
            {
                var t = new[] { -N[1], N[0] };
                return new[]
                {
                    Add(P, Mul(t, width / 2)), Add(P, Mul(t, -width / 2)),
                    Add(Add(P, Mul(t, -width / 2)), Mul(N, Length)), Add(Add(P, Mul(t, width / 2)), Mul(N, Length)),
                };
            }
        }

        /// <summary>
        /// Runners for one moat: per straight side between the outer wall's corner tangent points, n = max(1, round(L /
        /// pitch)) at (k + 0.5) L / n, each shifted to the nearest position (0.2 mm steps) with the clearance to every
        /// feature point and every seam running along it, and clear of the corner arcs; dropped only if nothing fits.
        /// Each runs from the moat out to `PastEdge` beyond the farthest perimeter point in its strip.
        /// </summary>
        public static List<Runner> PlaceRunners(Moat moat, IList<double[]> perimeter, IList<double[]> features,
            IList<(double[] P, double[] D)> seams, FlashRules rules, StringBuilder log = null)
        {
            var result = new List<Runner>();
            var p = moat.Outer;
            var n = p.Count;
            var sg = Area(p) > 0 ? 1 : -1;
            double W = rules.Width, CLR = rules.Clearance;
            for (var i = 0; i < n; i++)
            {
                var a = moat.OuterCorners[i].Tb;
                var b = moat.OuterCorners[(i + 1) % n].Ta;
                var L = Len(Sub(b, a));
                if (L < 1e-9) continue;
                var dir = Unit(Sub(b, a));
                var nrm = new[] { dir[1] * sg, -dir[0] * sg };
                double lo = CLR + W / 2, hi = L - CLR - W / 2;
                if (hi < lo)
                {
                    log?.AppendLine($"{moat.Id} side {i}: too short for a runner");
                    continue;
                }
                var cnt = Math.Max(1, (int)Math.Round(L / rules.Pitch)); // as the macro (banker's rounding on exact halves)

                double Exit(double s)
                {
                    var o = Add(a, Mul(dir, s));
                    double best = 0;
                    foreach (var q in perimeter)
                    {
                        var v = Sub(q, o);
                        double along = v[0] * nrm[0] + v[1] * nrm[1], lat = v[0] * dir[0] + v[1] * dir[1];
                        if (Math.Abs(lat) <= W / 2 + 0.002 && along > best) best = along;
                    }
                    return best;
                }

                bool Clear(double s, out double len)
                {
                    len = Exit(s) + rules.PastEdge;
                    var len0 = len;
                    var o = Add(a, Mul(dir, s));
                    var back = W / 2;
                    double Gap(double[] q)
                    {
                        // Distance from a point to the runner's rectangle (from half a width inside the moat to its end).
                        var v = Sub(q, o);
                        double along = v[0] * nrm[0] + v[1] * nrm[1], lat = v[0] * dir[0] + v[1] * dir[1];
                        var dx = Math.Max(0, Math.Abs(lat) - W / 2);
                        var dy = along < -back ? -back - along : along > len0 ? along - len0 : 0;
                        return Math.Sqrt(dx * dx + dy * dy);
                    }
                    foreach (var q in features)
                        if (Gap(q) < CLR - 1e-6) return false;
                    if (seams != null)
                        foreach (var sp in seams)
                        {
                            if (Math.Abs(sp.D[0] * nrm[0] + sp.D[1] * nrm[1]) < 0.9) continue; // seams crossing the runner are fine; along it are not
                            if (Gap(sp.P) < CLR - 1e-6) return false;
                        }
                    return true;
                }

                var placed = new List<double>();
                for (var k = 0; k < cnt; k++)
                {
                    var ideal = (k + 0.5) * L / cnt;
                    var bestS = double.NaN;
                    double bestLen = 0;
                    for (var dd = 0.0; dd <= L; dd += rules.SearchStep)
                    {
                        foreach (var s in new[] { ideal - dd, ideal + dd })
                        {
                            if (s < lo - 1e-9 || s > hi + 1e-9) continue;
                            if (placed.Any(x => Math.Abs(x - s) < W + CLR)) continue;
                            if (Clear(s, out var ln))
                            {
                                bestS = s;
                                bestLen = ln;
                                break;
                            }
                        }
                        if (!double.IsNaN(bestS)) break;
                    }
                    if (double.IsNaN(bestS))
                    {
                        log?.AppendLine($"{moat.Id} side {i}: runner {k + 1}/{cnt} dropped (no clear position)");
                        continue;
                    }
                    placed.Add(bestS);
                    var r = new Runner { Moat = moat.Id, Side = i, P = Add(a, Mul(dir, bestS)), N = nrm, Length = bestLen, At = bestS, Ideal = ideal };
                    result.Add(r);
                    log?.AppendLine($"{moat.Id} side {i} (straight {L / FlashRules.Inch:0.###} in): runner at {bestS / FlashRules.Inch:0.###} in" +
                                    (r.Shifted ? $" (shifted {(bestS - ideal) / FlashRules.Inch:+0.###;-0.###} in for clearance)" : ""));
                }
            }
            return result;
        }

        /// <summary>
        /// Dead ends (acceptance A10): flash that can't get out. A moat with no runner, or a runner that doesn't cross the
        /// plate's outline - no outline point in its strip ahead of it, or the farthest one beyond its end. `perimeter` is
        /// the plate's outer outline, sampled densely (any order). Empty when every moat drains through the edge.
        /// </summary>
        public static List<string> DeadEnds(IList<Moat> moats, IList<Runner> runners, IList<double[]> perimeter, double width)
        {
            var problems = new List<string>();
            foreach (var m in moats)
                if (!runners.Any(r => r.Moat == m.Id)) problems.Add($"moat {m.Id} has no runner - its flash has no way out (dead end)");
            foreach (var r in runners)
            {
                var t = new[] { -r.N[1], r.N[0] };
                var far = double.NegativeInfinity;
                foreach (var q in perimeter)
                {
                    var v = Sub(q, r.P);
                    double along = v[0] * r.N[0] + v[1] * r.N[1], lat = v[0] * t[0] + v[1] * t[1];
                    if (Math.Abs(lat) <= width / 2 && along > far) far = along;
                }
                if (far <= 0) problems.Add($"runner on {r.Moat} side {r.Side} meets no plate edge ahead of it (dead end)");
                else if (far > r.Length + 1e-6) problems.Add($"runner on {r.Moat} side {r.Side} stops {(far - r.Length) * 1000:0.##} mm short of the plate edge (dead end)");
            }
            return problems;
        }

        /// <summary>Smallest distance between two sampled outlines.</summary>
        public static double Gap(IList<double[]> a, IList<double[]> b)
        {
            var best = double.MaxValue;
            foreach (var p in a)
                foreach (var q in b)
                {
                    var d = (p[0] - q[0]) * (p[0] - q[0]) + (p[1] - q[1]) * (p[1] - q[1]);
                    if (d < best) best = d;
                }
            return Math.Sqrt(best);
        }

        /// <summary>A cavity outline (sampled) is a cavity, not a hole or a slot to keep clear of.</summary>
        public static bool IsCavity(IList<double[]> outline, bool isSingleCircle, double circleDiameter, FlashRules rules)
        {
            if (isSingleCircle && circleDiameter < rules.SmallHoleDiameter) return false;
            var w = outline.Max(q => q[0]) - outline.Min(q => q[0]);
            var h = outline.Max(q => q[1]) - outline.Min(q => q[1]);
            return Math.Min(w, h) >= rules.MinCavitySide;
        }
    }
}

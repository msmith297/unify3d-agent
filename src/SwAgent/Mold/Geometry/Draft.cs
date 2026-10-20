using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Mold.Geometry
{
    public enum DraftClass
    {
        /// <summary>Every triangle has a >= +min (opens toward +W, the lid).</summary>
        Positive,
        /// <summary>Every triangle has a &lt;= -min (opens toward -W, the base).</summary>
        Negative,
        /// <summary>More than 5% of the face's area has |a| &lt; min.</summary>
        NeedsDraft,
        /// <summary>Both signs at least min: the face spans the two halves (it can only part along a curve on it).</summary>
        Straddle,
    }

    /// <summary>Draft angle a = asin(n . W) per triangle, classes per face (design 2.3.3 step 2). Pure.</summary>
    public static class Draft
    {
        public sealed class FaceDraft
        {
            public DraftClass Class;
            public double MinDeg, MaxDeg;
            /// <summary>Share of the face's area with |a| below the minimum.</summary>
            public double LowFraction;
            public double Area;
        }

        public static double AngleDeg(double nW) => Math.Asin(Math.Max(-1, Math.Min(1, nW))) * 180 / Math.PI;

        /// <summary>Classify one face from its triangles (area, n . W).</summary>
        public static FaceDraft Classify(IEnumerable<(double Area, double NW)> triangles, double minDeg)
        {
            var list = triangles.Where(t => t.Area > 0).ToList();
            var r = new FaceDraft { Area = list.Sum(t => t.Area) };
            if (list.Count == 0 || r.Area <= 0) return new FaceDraft { Class = DraftClass.NeedsDraft, LowFraction = 1 };
            var angles = list.Select(t => (t.Area, A: AngleDeg(t.NW))).ToList();
            r.MinDeg = angles.Min(x => x.A);
            r.MaxDeg = angles.Max(x => x.A);
            var tol = 1e-9;
            r.LowFraction = angles.Where(x => Math.Abs(x.A) < minDeg - tol).Sum(x => x.Area) / r.Area;
            var pos = angles.Any(x => x.A >= minDeg - tol);
            var neg = angles.Any(x => x.A <= -minDeg + tol);
            if (r.LowFraction > 0.05) r.Class = DraftClass.NeedsDraft;
            else if (pos && neg) r.Class = DraftClass.Straddle;
            else r.Class = pos ? DraftClass.Positive : DraftClass.Negative;
            return r;
        }

        /// <summary>A triangle to sample: area, face, n . W of its own normal, and per vertex W and n . W of the surface normal.</summary>
        public struct SampleTriangle
        {
            public double Area;
            public int Face;
            public double NW;
            public double W0, W1, W2;
            /// <summary>n . W of the surface's normal at each vertex (NaN: use NW).</summary>
            public double N0, N1, N2;
        }

        /// <summary>
        /// One sample: barycentric (A, B) on triangle Tri (A toward vertex 1, B toward vertex 2), the area it stands for,
        /// the W range of that area, and the lowest W where it faces the lid / highest where it faces the base (from the
        /// vertices' own normals; +inf / -inf when none). These extents come from vertices, which lie on the true surface,
        /// so the parting-line band doesn't depend on how coarsely the samples are spread (critique A7).
        /// </summary>
        public sealed class Pick
        {
            public int Tri;
            public double A, B, Area;
            public double WLo, WHi;
            public double LidMinW = double.PositiveInfinity, BaseMaxW = double.NegativeInfinity;
            /// <summary>How many triangles this sample stands for (1 unless the budget forced grouping).</summary>
            public int Triangles = 1;
        }

        /// <summary>
        /// Area-weighted sampling that never drops a face. Every triangle gets one sample first (its centroid); the rest of
        /// the budget goes by area: a triangle gets n^2 samples (a regular sub-grid, n = floor(sqrt(area / target)), at most
        /// 8) with the target area chosen so the total stays within maxSamples. If the triangles alone exceed the budget,
        /// each face's lid-, base- and either-facing triangles are grouped (stratified along W, at least one sample per
        /// group, the rest by area) and each group is one sample at its largest triangle, standing for the group's area and
        /// W range; a note says so. The weights add up to the total area exactly.
        /// </summary>
        public static List<Pick> Samples(IList<SampleTriangle> tris, double sampleArea, int maxSamples, double sinMin, List<string> notes = null)
        {
            var result = new List<Pick>();
            var live = Enumerable.Range(0, tris.Count).Where(i => tris[i].Area > 0).ToList();
            if (live.Count == 0) return result;
            var total = live.Sum(i => tris[i].Area);
            maxSamples = Math.Max(1, maxSamples);
            double Nv(int i, int k)
            {
                var t = tris[i];
                var v = k == 0 ? t.N0 : k == 1 ? t.N1 : t.N2;
                return double.IsNaN(v) ? t.NW : v;
            }
            double Wv(int i, int k) => k == 0 ? tris[i].W0 : k == 1 ? tris[i].W1 : tris[i].W2;

            // Extents over points given as (W, n . W) pairs.
            void Bound(Pick p, IEnumerable<(double W, double N)> pts)
            {
                p.WLo = double.PositiveInfinity;
                p.WHi = double.NegativeInfinity;
                foreach (var (w, n) in pts)
                {
                    p.WLo = Math.Min(p.WLo, w);
                    p.WHi = Math.Max(p.WHi, w);
                    if (n >= sinMin) p.LidMinW = Math.Min(p.LidMinW, w);
                    if (n <= -sinMin) p.BaseMaxW = Math.Max(p.BaseMaxW, w);
                }
            }

            if (live.Count <= maxSamples)
            {
                var extra = maxSamples - live.Count;
                var target = extra > 0 ? Math.Max(sampleArea, total / extra) : double.PositiveInfinity;
                foreach (var i in live)
                {
                    var area = tris[i].Area;
                    // floor: n^2 <= area / target, so the n >= 2 triangles use at most total / target <= extra samples.
                    var n = double.IsInfinity(target) ? 1 : Math.Max(1, (int)Math.Floor(Math.Sqrt(area / target)));
                    n = Math.Min(n, 8);
                    var each = area / (n * n);
                    var ii = i;
                    // A point at barycentric (a, b): its W and interpolated n . W.
                    (double W, double N) At(double a, double b) =>
                        (Wv(ii, 0) + a * (Wv(ii, 1) - Wv(ii, 0)) + b * (Wv(ii, 2) - Wv(ii, 0)), Nv(ii, 0) + a * (Nv(ii, 1) - Nv(ii, 0)) + b * (Nv(ii, 2) - Nv(ii, 0)));
                    for (var p = 0; p < n; p++)
                        for (var q = 0; q < n - p; q++)
                        {
                            // Upward sub-triangle (p, q), (p+1, q), (p, q+1) and its centroid.
                            var up = new Pick { Tri = i, A = (p + 1.0 / 3) / n, B = (q + 1.0 / 3) / n, Area = each };
                            Bound(up, new[] { At((double)p / n, (double)q / n), At((p + 1.0) / n, (double)q / n), At((double)p / n, (q + 1.0) / n) });
                            result.Add(up);
                            // Downward sub-triangle (p+1, q), (p, q+1), (p+1, q+1) (exists when p + q < n - 1).
                            if (p + q < n - 1)
                            {
                                var down = new Pick { Tri = i, A = (p + 2.0 / 3) / n, B = (q + 2.0 / 3) / n, Area = each };
                                Bound(down, new[] { At((p + 1.0) / n, (double)q / n), At((double)p / n, (q + 1.0) / n), At((p + 1.0) / n, (q + 1.0) / n) });
                                result.Add(down);
                            }
                        }
                }
                return result;
            }

            // Over budget: groups per face and facing, stratified along W.
            int Side(int i) => tris[i].NW >= sinMin ? 1 : tris[i].NW <= -sinMin ? -1 : 0;
            var groups = live.GroupBy(i => (tris[i].Face, Side(i))).Select(g => g.ToList()).ToList();
            var spare = Math.Max(0, maxSamples - groups.Count);
            foreach (var g in groups)
            {
                var gArea = g.Sum(i => tris[i].Area);
                var k = Math.Min(g.Count, 1 + (int)Math.Floor(spare * gArea / total));
                var sorted = g.OrderBy(i => (Wv(i, 0) + Wv(i, 1) + Wv(i, 2)) / 3).ThenBy(i => i).ToList();
                var chunks = new List<List<int>>();
                var cum = 0.0;
                foreach (var i in sorted)
                {
                    var j = Math.Min(k - 1, (int)Math.Floor(cum / gArea * k));
                    while (chunks.Count <= j) chunks.Add(new List<int>());
                    chunks[j].Add(i);
                    cum += tris[i].Area;
                }
                foreach (var c in chunks.Where(c => c.Count > 0))
                {
                    var rep = c.OrderByDescending(i => tris[i].Area).First();
                    var pick = new Pick { Tri = rep, A = 1.0 / 3, B = 1.0 / 3, Area = c.Sum(i => tris[i].Area), Triangles = c.Count };
                    Bound(pick, c.SelectMany(i => Enumerable.Range(0, 3).Select(v => (Wv(i, v), Nv(i, v)))));
                    result.Add(pick);
                }
            }
            notes?.Add($"{live.Count} triangles exceed the {maxSamples} sample budget: each face's lid-, base- and either-facing triangles were grouped " +
                       $"({result.Count} samples, at least one per face side); undercut rays are cast from one triangle per group");
            return result;
        }

        /// <summary>Samples of bare triangle areas (each its own face, flat in W): (triangle, barycentric a, b, weight area).</summary>
        public static List<(int Tri, double A, double B, double Area)> Samples(IList<double> triangleAreas, double sampleArea, int maxSamples) =>
            Samples(triangleAreas.Select((a, i) => new SampleTriangle { Area = a, Face = i, N0 = double.NaN, N1 = double.NaN, N2 = double.NaN }).ToList(), sampleArea, maxSamples, 1)
                .Select(p => (p.Tri, p.A, p.B, p.Area)).ToList();
    }
}

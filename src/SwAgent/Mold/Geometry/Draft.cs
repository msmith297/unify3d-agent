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

        /// <summary>
        /// Area-weighted sampling of triangles: every triangle gets ceil(area / target) samples (its centroid, or a regular
        /// sub-grid of barycentric points), with the target area chosen so the total stays within maxSamples.
        /// Returns (triangle index, barycentric a, b, weight area).
        /// </summary>
        public static List<(int Tri, double A, double B, double Area)> Samples(IList<double> triangleAreas, double sampleArea, int maxSamples)
        {
            var total = triangleAreas.Sum();
            if (total <= 0) return new List<(int, double, double, double)>();
            var target = Math.Max(sampleArea, total / Math.Max(1, maxSamples));
            var result = new List<(int, double, double, double)>();
            // Pass 1: how many per triangle; if the triangle count alone exceeds the budget, keep the largest ones.
            var order = Enumerable.Range(0, triangleAreas.Count).Where(i => triangleAreas[i] > 0).OrderByDescending(i => triangleAreas[i]).ToList();
            foreach (var i in order)
            {
                if (result.Count >= maxSamples) break;
                var area = triangleAreas[i];
                // n^2 sub-triangles at level n give n^2 centroids; stop at the area target.
                var n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(area / target)));
                n = Math.Min(n, 8);
                var each = area / (n * n);
                for (var p = 0; p < n && result.Count < maxSamples; p++)
                    for (var q = 0; q < n - p && result.Count < maxSamples; q++)
                    {
                        // Upward sub-triangle centroid.
                        result.Add((i, (p + 1.0 / 3) / n, (q + 1.0 / 3) / n, each));
                        // Downward sub-triangle centroid (exists when p + q < n - 1).
                        if (p + q < n - 1 && result.Count < maxSamples) result.Add((i, (p + 2.0 / 3) / n, (q + 2.0 / 3) / n, each));
                    }
            }
            // Re-weight so the sampled area equals the total (dropped small triangles are spread over the rest).
            var sampled = result.Sum(x => x.Item4);
            if (sampled > 0 && Math.Abs(sampled - total) > 1e-12 * total)
            {
                var k = total / sampled;
                for (var i = 0; i < result.Count; i++) result[i] = (result[i].Item1, result[i].Item2, result[i].Item3, result[i].Item4 * k);
            }
            return result;
        }
    }
}

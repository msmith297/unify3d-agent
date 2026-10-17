using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace SwAgent.Scripting
{
    /// <summary>
    /// Measuring helpers for agent cells and tool macros (<c>Geo.EdgePoints(edge)</c>, <c>Geo.Frame(face)</c>, ...). All
    /// lengths in meters, points in model coordinates. They exist so tools don't each re-derive edge sampling and
    /// plane frames: the classic mistake is evaluating an edge's curve at its raw parameters, which for reversed or
    /// unbounded curves gives points that aren't on the edge - every point returned here is snapped onto the edge.
    /// </summary>
    public sealed class Geometry
    {
        /// <summary>Points along an edge from its start to its end, at most about `step` apart, all exactly on the edge.</summary>
        public List<double[]> EdgePoints(IEdge edge, double step = 0.001)
        {
            var result = new List<double[]>();
            var curve = (ICurve)edge.GetCurve();
            var cp = (double[])edge.GetCurveParams2();
            var start = new[] { cp[0], cp[1], cp[2] };
            var end = new[] { cp[3], cp[4], cp[5] };
            var raw = new List<double[]>();
            if (Dist(start, end) > 1e-9)
            {
                // Between the edge's own end points, so the curve's direction and parameter range don't matter.
                var tess = curve.GetTessPts(step / 10, step, start, end) as double[];
                if (tess != null)
                    for (var i = 0; i + 2 < tess.Length; i += 3) raw.Add(new[] { tess[i], tess[i + 1], tess[i + 2] });
            }
            if (raw.Count < 2)
            {
                // Closed edges (full circles, ellipses) or no tessellation: evaluate over the edge's parameter range.
                var range = edge.GetCurveParams3();
                double t0 = range.UMinValue, t1 = range.UMaxValue;
                var length = Math.Max(curve.GetLength3(t0, t1), step);
                var n = Math.Max(8, Math.Min(2000, (int)Math.Ceiling(length / step)));
                for (var k = 0; k <= n; k++)
                {
                    var p = (double[])curve.Evaluate2(t0 + (t1 - t0) * k / n, 0);
                    raw.Add(new[] { p[0], p[1], p[2] });
                }
            }
            // Snap onto the edge: guarantees every point is on it, whatever the curve's sense or extent.
            foreach (var p in raw)
            {
                var q = (double[])edge.GetClosestPointOn(p[0], p[1], p[2]);
                result.Add(q != null && q.Length >= 3 ? new[] { q[0], q[1], q[2] } : p);
            }
            return result;
        }

        /// <summary>Points along all edges of a face loop (outer boundary or a hole/pocket opening).</summary>
        public List<double[]> LoopPoints(ILoop2 loop, double step = 0.001) =>
            ((object[])loop.GetEdges() ?? new object[0]).Cast<IEdge>().SelectMany(e => EdgePoints(e, step)).ToList();

        /// <summary>A u/v/n frame on a planar face: n is the face's outward normal; model point = Origin + u*U + v*V + h*N.</summary>
        public PlaneFrame Frame(IFace2 planarFace)
        {
            var surface = (ISurface)planarFace.GetSurface();
            if (!surface.IsPlane()) throw new ArgumentException("Geo.Frame needs a planar face.");
            var n = Unit((double[])planarFace.Normal);
            var seed = Math.Abs(n[0]) > 0.9 ? new double[] { 0, 1, 0 } : new double[] { 1, 0, 0 };
            var d = Dot(seed, n);
            var u = Unit(new[] { seed[0] - d * n[0], seed[1] - d * n[1], seed[2] - d * n[2] });
            var v = Cross(n, u);
            var tess = (float[])planarFace.GetTessTriangles(true);
            var origin = tess != null && tess.Length >= 3 ? new double[] { tess[0], tess[1], tess[2] } : new double[3];
            // Put the origin on the model origin's projection so u/v read like model coordinates.
            var h = Dot(origin, n);
            return new PlaneFrame(new[] { h * n[0], h * n[1], h * n[2] }, u, v, n);
        }

        /// <summary>A loop as a 2D polygon (u, v) in a face frame.</summary>
        public List<double[]> LoopUV(ILoop2 loop, PlaneFrame frame, double step = 0.001) => LoopPoints(loop, step).Select(frame.ToUV).ToList();

        /// <summary>Shortest distance from a point to an edge (exact, via the edge's closest point).</summary>
        public double Distance(double[] point, IEdge edge)
        {
            var q = (double[])edge.GetClosestPointOn(point[0], point[1], point[2]);
            return q == null ? double.MaxValue : Dist(point, q);
        }

        /// <summary>Shortest distance from a point to any of these edges (e.g. a loop's edges).</summary>
        public double Distance(double[] point, IEnumerable<IEdge> edges) => edges.Select(e => Distance(point, e)).DefaultIfEmpty(double.MaxValue).Min();

        /// <summary>Shortest distance between two loops (sampled every `step` on the first, exact on the second).</summary>
        public double Distance(ILoop2 a, ILoop2 b, double step = 0.0005)
        {
            var edges = ((object[])b.GetEdges() ?? new object[0]).Cast<IEdge>().ToList();
            return LoopPoints(a, step).Select(p => Distance(p, edges)).DefaultIfEmpty(double.MaxValue).Min();
        }

        /// <summary>Is a 2D point inside a closed polygon (even-odd rule)?</summary>
        public bool Inside(double[] uv, IList<double[]> polygon)
        {
            var inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a[1] > uv[1]) != (b[1] > uv[1]) && uv[0] < (b[0] - a[0]) * (uv[1] - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
            }
            return inside;
        }

        /// <summary>Bounding box of 2D points: [uMin, uMax, vMin, vMax].</summary>
        public double[] Box(IEnumerable<double[]> uv)
        {
            var list = uv.ToList();
            return new[] { list.Min(p => p[0]), list.Max(p => p[0]), list.Min(p => p[1]), list.Max(p => p[1]) };
        }

        internal static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        internal static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        internal static double[] Unit(double[] a)
        {
            var l = Math.Sqrt(Dot(a, a));
            return new[] { a[0] / l, a[1] / l, a[2] / l };
        }

        internal static double Dist(double[] a, double[] b) =>
            Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
    }

    /// <summary>A plane frame from Geo.Frame: model point = Origin + u*U + v*V + h*N.</summary>
    public sealed class PlaneFrame
    {
        public PlaneFrame(double[] origin, double[] u, double[] v, double[] n)
        {
            Origin = origin;
            U = u;
            V = v;
            N = n;
        }

        public double[] Origin { get; }
        public double[] U { get; }
        public double[] V { get; }
        public double[] N { get; }

        /// <summary>Model point to (u, v).</summary>
        public double[] ToUV(double[] p)
        {
            var d = new[] { p[0] - Origin[0], p[1] - Origin[1], p[2] - Origin[2] };
            return new[] { Geometry.Dot(d, U), Geometry.Dot(d, V) };
        }

        /// <summary>Height of a model point above the plane (along N).</summary>
        public double Height(double[] p) => Geometry.Dot(new[] { p[0] - Origin[0], p[1] - Origin[1], p[2] - Origin[2] }, N);

        /// <summary>(u, v, h) to a model point.</summary>
        public double[] ToModel(double u, double v, double h = 0) => new[]
        {
            Origin[0] + u * U[0] + v * V[0] + h * N[0],
            Origin[1] + u * U[1] + v * V[1] + h * N[1],
            Origin[2] + u * U[2] + v * V[2] + h * N[2],
        };
    }
}

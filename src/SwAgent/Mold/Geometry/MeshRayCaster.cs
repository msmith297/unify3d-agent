using System;
using System.Collections.Generic;

namespace SwAgent.Mold.Geometry
{
    /// <summary>
    /// Ray casting against a triangle mesh (model meters) in managed code, with a bounding-volume tree. mold_part casts its
    /// rays here, on the body's display tessellation, instead of through IModelDocExtension.RayIntersections: on 2026-10-20
    /// RayIntersections faulted inside SolidWorks (an access violation on a 2,000-ray batch, a freshly inserted part) and took
    /// SolidWorks down - no try/catch stops that - as it had once before from the overlay timer. Hits use the RayHit layout of
    /// Columns.Parse (FACE | ENTER or EXIT by the triangle's outward normal). Pure (MeshRayCasterTests).
    /// </summary>
    public sealed class MeshRayCaster
    {
        /// <summary>swRayPtsOpts_e ENTRY_EXIT | UNBLOCK: every crossing instead of the first hit only.</summary>
        public const int AllHits = 4 | 8;

        private const int LeafSize = 6;
        private const double TMin = 1e-10;

        private readonly double[] _v;   // 9 per triangle
        private readonly double[] _n;   // 3 per triangle, outward unit normal
        private readonly int[] _order;  // triangle ids, grouped by leaf
        private readonly List<Node> _nodes = new List<Node>();

        private struct Node
        {
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            public int Left, Right;   // children, or -1 for a leaf
            public int First, Count;  // leaf range in _order
        }

        /// <summary>
        /// triangles: 9 doubles each. outward: per triangle a direction the outside lies along (for example the sum of its
        /// vertex normals), or null to take the winding's normal as outward.
        /// </summary>
        public MeshRayCaster(IList<double[]> triangles, IList<double[]> outward = null)
        {
            var count = triangles.Count;
            _v = new double[count * 9];
            _n = new double[count * 3];
            var ids = new List<int>(count);
            for (var t = 0; t < count; t++)
            {
                var p = triangles[t];
                Array.Copy(p, 0, _v, t * 9, 9);
                var e1 = new[] { p[3] - p[0], p[4] - p[1], p[5] - p[2] };
                var e2 = new[] { p[6] - p[0], p[7] - p[1], p[8] - p[2] };
                var cr = V3.Cross(e1, e2);
                var len = V3.Len(cr);
                if (len < 1e-30) continue; // degenerate: never hit
                var nrm = V3.Mul(cr, 1 / len);
                var o = outward != null && t < outward.Count ? outward[t] : null;
                if (o != null && V3.Dot(o, nrm) < 0) nrm = V3.Mul(nrm, -1);
                Array.Copy(nrm, 0, _n, t * 3, 3);
                ids.Add(t);
            }
            TriangleCount = ids.Count;
            _order = ids.ToArray();
            if (_order.Length > 0) Build(0, _order.Length);
        }

        public int TriangleCount { get; }

        /// <summary>
        /// Cast rays start..start+n-1 (Hit.Ray relative to start). Without ENTRY_EXIT | UNBLOCK each ray reports its first hit;
        /// with them, every crossing in order along the ray (a crossing on a shared edge once).
        /// </summary>
        public List<RayHit> Cast(IList<double[]> pts, IList<double[]> dirs, int start, int n, int opts)
        {
            var all = (opts & AllHits) == AllHits;
            var hits = new List<RayHit>();
            var found = new List<(double T, int Tri)>();
            var stack = new Stack<int>();
            for (var k = 0; k < n; k++)
            {
                var o = pts[start + k];
                var d = V3.Unit(dirs[start + k]);
                found.Clear();
                Trace(o, d, all, found, stack);
                if (found.Count == 0) continue;
                found.Sort((a, b) => a.T.CompareTo(b.T));
                var prevT = double.NaN;
                var prevType = 0;
                foreach (var (t, tri) in found)
                {
                    var nrm = new[] { _n[tri * 3], _n[tri * 3 + 1], _n[tri * 3 + 2] };
                    var type = 1 | (V3.Dot(d, nrm) < 0 ? Columns.Enter : Columns.Exit);
                    if (!double.IsNaN(prevT) && t - prevT < 1e-9 && type == prevType) continue; // the same crossing on a shared edge
                    hits.Add(new RayHit { Ray = k, Type = type, Point = V3.Add(o, V3.Mul(d, t)), Normal = nrm, Index = hits.Count });
                    prevT = t;
                    prevType = type;
                    if (!all) break;
                }
            }
            return hits;
        }

        private void Trace(double[] o, double[] d, bool all, List<(double T, int Tri)> found, Stack<int> stack)
        {
            if (_nodes.Count == 0) return;
            var best = double.PositiveInfinity;
            double ix = 1 / d[0], iy = 1 / d[1], iz = 1 / d[2];
            stack.Clear();
            stack.Push(0);
            while (stack.Count > 0)
            {
                var node = _nodes[stack.Pop()];
                if (!HitsBox(node, o, ix, iy, iz, all ? double.PositiveInfinity : best)) continue;
                if (node.Left < 0)
                {
                    for (var i = node.First; i < node.First + node.Count; i++)
                    {
                        var tri = _order[i];
                        var t = Intersect(o, d, tri);
                        if (t < TMin) continue;
                        if (all) found.Add((t, tri));
                        else if (t < best)
                        {
                            best = t;
                            found.Clear();
                            found.Add((t, tri));
                        }
                    }
                    continue;
                }
                stack.Push(node.Left);
                stack.Push(node.Right);
            }
        }

        private static bool HitsBox(Node b, double[] o, double ix, double iy, double iz, double tMax)
        {
            double t0 = 0, t1 = tMax;
            if (!Slab(o[0], ix, b.MinX, b.MaxX, ref t0, ref t1)) return false;
            if (!Slab(o[1], iy, b.MinY, b.MaxY, ref t0, ref t1)) return false;
            return Slab(o[2], iz, b.MinZ, b.MaxZ, ref t0, ref t1);
        }

        private static bool Slab(double o, double inv, double lo, double hi, ref double t0, ref double t1)
        {
            const double Pad = 1e-9; // boxes grown a little so rays along a face plane still enter them
            if (double.IsInfinity(inv)) return o >= lo - Pad && o <= hi + Pad;
            var a = (lo - Pad - o) * inv;
            var b = (hi + Pad - o) * inv;
            if (a > b) (a, b) = (b, a);
            if (a > t0) t0 = a;
            if (b < t1) t1 = b;
            return t0 <= t1;
        }

        /// <summary>Möller-Trumbore with a hair of slack on the edges (a ray through a shared edge hits both triangles). -1 if missed.</summary>
        private double Intersect(double[] o, double[] d, int tri)
        {
            var b = tri * 9;
            double e1x = _v[b + 3] - _v[b], e1y = _v[b + 4] - _v[b + 1], e1z = _v[b + 5] - _v[b + 2];
            double e2x = _v[b + 6] - _v[b], e2y = _v[b + 7] - _v[b + 1], e2z = _v[b + 8] - _v[b + 2];
            double px = d[1] * e2z - d[2] * e2y, py = d[2] * e2x - d[0] * e2z, pz = d[0] * e2y - d[1] * e2x;
            var det = e1x * px + e1y * py + e1z * pz;
            if (Math.Abs(det) < 1e-24) return -1;
            var inv = 1 / det;
            double sx = o[0] - _v[b], sy = o[1] - _v[b + 1], sz = o[2] - _v[b + 2];
            var u = (sx * px + sy * py + sz * pz) * inv;
            const double Slack = 1e-9;
            if (u < -Slack || u > 1 + Slack) return -1;
            double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
            var v = (d[0] * qx + d[1] * qy + d[2] * qz) * inv;
            if (v < -Slack || u + v > 1 + Slack) return -1;
            return (e2x * qx + e2y * qy + e2z * qz) * inv;
        }

        /// <summary>Median split on the longest axis of the centroids, iterative so a big mesh can't overflow the stack.</summary>
        private void Build(int first, int count)
        {
            var work = new Stack<(int Node, int First, int Count)>();
            _nodes.Add(default);
            work.Push((0, first, count));
            var centroid = new double[3];
            while (work.Count > 0)
            {
                var (ni, f, c) = work.Pop();
                var node = new Node
                {
                    MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue,
                    MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue,
                    Left = -1, Right = -1, First = f, Count = c,
                };
                double cMinX = double.MaxValue, cMinY = double.MaxValue, cMinZ = double.MaxValue;
                double cMaxX = double.MinValue, cMaxY = double.MinValue, cMaxZ = double.MinValue;
                for (var i = f; i < f + c; i++)
                {
                    var b = _order[i] * 9;
                    for (var k = 0; k < 9; k += 3)
                    {
                        node.MinX = Math.Min(node.MinX, _v[b + k]); node.MaxX = Math.Max(node.MaxX, _v[b + k]);
                        node.MinY = Math.Min(node.MinY, _v[b + k + 1]); node.MaxY = Math.Max(node.MaxY, _v[b + k + 1]);
                        node.MinZ = Math.Min(node.MinZ, _v[b + k + 2]); node.MaxZ = Math.Max(node.MaxZ, _v[b + k + 2]);
                    }
                    Centroid(_order[i], centroid);
                    cMinX = Math.Min(cMinX, centroid[0]); cMaxX = Math.Max(cMaxX, centroid[0]);
                    cMinY = Math.Min(cMinY, centroid[1]); cMaxY = Math.Max(cMaxY, centroid[1]);
                    cMinZ = Math.Min(cMinZ, centroid[2]); cMaxZ = Math.Max(cMaxZ, centroid[2]);
                }
                double sx = cMaxX - cMinX, sy = cMaxY - cMinY, sz = cMaxZ - cMinZ;
                if (c > LeafSize && Math.Max(sx, Math.Max(sy, sz)) > 0)
                {
                    var axis = sx >= sy && sx >= sz ? 0 : sy >= sz ? 1 : 2;
                    var keys = new double[c];
                    var ids = new int[c];
                    for (var i = 0; i < c; i++)
                    {
                        ids[i] = _order[f + i];
                        Centroid(ids[i], centroid);
                        keys[i] = centroid[axis];
                    }
                    Array.Sort(keys, ids);
                    Array.Copy(ids, 0, _order, f, c);
                    var half = c / 2;
                    node.Left = _nodes.Count;
                    _nodes.Add(default);
                    node.Right = _nodes.Count;
                    _nodes.Add(default);
                    work.Push((node.Left, f, half));
                    work.Push((node.Right, f + half, c - half));
                }
                _nodes[ni] = node;
            }
        }

        private void Centroid(int tri, double[] c)
        {
            var b = tri * 9;
            c[0] = (_v[b] + _v[b + 3] + _v[b + 6]) / 3;
            c[1] = (_v[b + 1] + _v[b + 4] + _v[b + 7]) / 3;
            c[2] = (_v[b + 2] + _v[b + 5] + _v[b + 8]) / 3;
        }
    }
}

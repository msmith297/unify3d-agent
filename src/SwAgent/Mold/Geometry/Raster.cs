using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Mold.Geometry
{
    /// <summary>A regular grid on the (u, v) plane: cell (i, j) has its centre at (U0 + (i + 0.5) H, V0 + (j + 0.5) H).</summary>
    public sealed class Grid2
    {
        public Grid2(double u0, double v0, double h, int nu, int nv)
        {
            if (h <= 0) throw new ArgumentException("cell size must be positive");
            if (nu < 1 || nv < 1) throw new ArgumentException("grid needs at least one cell");
            U0 = u0;
            V0 = v0;
            H = h;
            Nu = nu;
            Nv = nv;
        }

        public double U0 { get; }
        public double V0 { get; }
        public double H { get; }
        public int Nu { get; }
        public int Nv { get; }

        public double CU(int i) => U0 + (i + 0.5) * H;
        public double CV(int j) => V0 + (j + 0.5) * H;

        /// <summary>A grid with cell size h covering [u0, u1] x [v0, v1] plus `margin` empty cells on each side.</summary>
        public static Grid2 Covering(double u0, double v0, double u1, double v1, double h, int margin = 1)
        {
            var nu = Math.Max(1, (int)Math.Ceiling((u1 - u0) / h)) + 2 * margin;
            var nv = Math.Max(1, (int)Math.Ceiling((v1 - v0) / h)) + 2 * margin;
            // Centre the cells on the box so symmetric parts give symmetric rasters.
            var cu = (u0 + u1) / 2 - nu * h / 2;
            var cv = (v0 + v1) / 2 - nv * h / 2;
            return new Grid2(cu, cv, h, nu, nv);
        }
    }

    /// <summary>
    /// Projection of a part onto the parting plane: triangles rasterized on a grid (projected area), marching-squares
    /// outlines, Douglas-Peucker simplification and perimeter. Pure arithmetic, unit-tested (RasterTests).
    /// </summary>
    public static class Raster
    {
        /// <summary>Mark every cell whose centre lies inside (or on the edge of) a projected triangle {u0,v0,u1,v1,u2,v2}.</summary>
        public static bool[,] Fill(Grid2 g, IEnumerable<double[]> triangles)
        {
            var m = new bool[g.Nu, g.Nv];
            foreach (var t in triangles) FillTriangle(g, m, t);
            return m;
        }

        public static void FillTriangle(Grid2 g, bool[,] m, double[] t)
        {
            double x0 = t[0], y0 = t[1], x1 = t[2], y1 = t[3], x2 = t[4], y2 = t[5];
            var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
            if (Math.Abs(area) < 1e-18) return; // seen edge-on (a wall parallel to W): projects to a line
            var i0 = Math.Max(0, (int)Math.Floor((Math.Min(x0, Math.Min(x1, x2)) - g.U0) / g.H - 0.5));
            var i1 = Math.Min(g.Nu - 1, (int)Math.Ceiling((Math.Max(x0, Math.Max(x1, x2)) - g.U0) / g.H - 0.5));
            var j0 = Math.Max(0, (int)Math.Floor((Math.Min(y0, Math.Min(y1, y2)) - g.V0) / g.H - 0.5));
            var j1 = Math.Min(g.Nv - 1, (int)Math.Ceiling((Math.Max(y0, Math.Max(y1, y2)) - g.V0) / g.H - 0.5));
            var eps = 1e-9 * Math.Abs(area);
            for (var j = j0; j <= j1; j++)
            {
                var y = g.CV(j);
                for (var i = i0; i <= i1; i++)
                {
                    if (m[i, j]) continue;
                    var x = g.CU(i);
                    var a = ((x1 - x) * (y2 - y) - (x2 - x) * (y1 - y)) / area;
                    var b = ((x2 - x) * (y0 - y) - (x0 - x) * (y2 - y)) / area;
                    var c = 1 - a - b;
                    if (a >= -eps && b >= -eps && c >= -eps) m[i, j] = true;
                }
            }
        }

        public static int Count(bool[,] m)
        {
            var n = 0;
            foreach (var x in m) if (x) n++;
            return n;
        }

        public static double Area(bool[,] m, double h) => Count(m) * h * h;

        /// <summary>
        /// Closed outlines of the marked region (marching squares on the cell centres, contour at 0.5). Outer boundaries
        /// come out counter-clockwise (positive area), holes clockwise. Diagonal-only contacts count as separate.
        /// </summary>
        public static List<List<double[]>> Outlines(bool[,] m, Grid2 g)
        {
            int nu = m.GetLength(0), nv = m.GetLength(1);
            bool S(int i, int j) => i >= 0 && j >= 0 && i < nu && j < nv && m[i, j];
            // Edge ids on the padded sample lattice (samples -1..nu, -1..nv): horizontal edge (i,j)-(i+1,j) and vertical (i,j)-(i,j+1).
            long W = nu + 3;
            long HId(int i, int j) => ((long)(j + 1) * W + (i + 1)) * 2;
            long VId(int i, int j) => ((long)(j + 1) * W + (i + 1)) * 2 + 1;
            double[] Pos(long id)
            {
                var vertical = (id & 1) == 1;
                var cell = id / 2;
                var i = (int)(cell % W) - 1;
                var j = (int)(cell / W) - 1;
                return vertical ? new[] { g.CU(i), g.CV(j) + g.H / 2 } : new[] { g.CU(i) + g.H / 2, g.CV(j) };
            }

            var next = new Dictionary<long, long>();
            void Seg(long a, long b) => next[a] = b;
            for (var j = -1; j < nv; j++)
            {
                for (var i = -1; i < nu; i++)
                {
                    var code = (S(i, j) ? 1 : 0) | (S(i + 1, j) ? 2 : 0) | (S(i + 1, j + 1) ? 4 : 0) | (S(i, j + 1) ? 8 : 0);
                    if (code == 0 || code == 15) continue;
                    long e0 = HId(i, j), e1 = VId(i + 1, j), e2 = HId(i, j + 1), e3 = VId(i, j);
                    // Directed so the inside is on the left (outer loops counter-clockwise).
                    switch (code)
                    {
                        case 1: Seg(e0, e3); break;
                        case 2: Seg(e1, e0); break;
                        case 4: Seg(e2, e1); break;
                        case 8: Seg(e3, e2); break;
                        case 3: Seg(e1, e3); break;
                        case 6: Seg(e2, e0); break;
                        case 12: Seg(e3, e1); break;
                        case 9: Seg(e0, e2); break;
                        case 7: Seg(e2, e3); break;
                        case 11: Seg(e1, e2); break;
                        case 13: Seg(e0, e1); break;
                        case 14: Seg(e3, e0); break;
                        case 5: Seg(e0, e3); Seg(e2, e1); break;
                        case 10: Seg(e1, e0); Seg(e3, e2); break;
                    }
                }
            }

            var loops = new List<List<double[]>>();
            var used = new HashSet<long>();
            foreach (var start in next.Keys.ToList())
            {
                if (used.Contains(start)) continue;
                var loop = new List<double[]>();
                var cur = start;
                while (used.Add(cur))
                {
                    loop.Add(Pos(cur));
                    if (!next.TryGetValue(cur, out cur)) break;
                }
                if (loop.Count >= 3) loops.Add(loop);
            }
            return loops;
        }

        /// <summary>Signed area of a closed polygon (positive = counter-clockwise).</summary>
        public static double SignedArea(IList<double[]> p)
        {
            double s = 0;
            for (var i = 0; i < p.Count; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Count];
                s += a[0] * b[1] - b[0] * a[1];
            }
            return s / 2;
        }

        public static double Length(IList<double[]> p, bool closed = true)
        {
            double s = 0;
            var n = closed ? p.Count : p.Count - 1;
            for (var i = 0; i < n; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Count];
                s += Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]));
            }
            return s;
        }

        /// <summary>
        /// The outline as a measured polygon: marching-squares staircases smoothed (two passes of neighbour averaging,
        /// which moves a point by at most a quarter cell) and simplified with Douglas-Peucker at half a cell. The raw
        /// staircase would overstate a curved perimeter by several percent.
        /// </summary>
        public static List<double[]> Clean(List<double[]> loop, double h)
        {
            var p = loop;
            for (var pass = 0; pass < 2; pass++)
            {
                var n = p.Count;
                var q = new List<double[]>(n);
                for (var i = 0; i < n; i++)
                {
                    var a = p[(i + n - 1) % n];
                    var b = p[i];
                    var c = p[(i + 1) % n];
                    q.Add(new[] { (a[0] + 2 * b[0] + c[0]) / 4, (a[1] + 2 * b[1] + c[1]) / 4 });
                }
                p = q;
            }
            return Simplify(p, h / 2);
        }

        /// <summary>Douglas-Peucker on a closed loop: split at the two points farthest apart, simplify both halves.</summary>
        public static List<double[]> Simplify(List<double[]> loop, double tol)
        {
            if (loop.Count < 4) return new List<double[]>(loop);
            var far = 0;
            double best = -1;
            for (var i = 1; i < loop.Count; i++)
            {
                var d = (loop[i][0] - loop[0][0]) * (loop[i][0] - loop[0][0]) + (loop[i][1] - loop[0][1]) * (loop[i][1] - loop[0][1]);
                if (d > best)
                {
                    best = d;
                    far = i;
                }
            }
            var a = loop.GetRange(0, far + 1);
            var b = loop.GetRange(far, loop.Count - far);
            b.Add(loop[0]);
            var ra = SimplifyOpen(a, tol);
            var rb = SimplifyOpen(b, tol);
            ra.RemoveAt(ra.Count - 1);
            rb.RemoveAt(rb.Count - 1);
            ra.AddRange(rb);
            return ra;
        }

        public static List<double[]> SimplifyOpen(List<double[]> pts, double tol)
        {
            var keep = new bool[pts.Count];
            keep[0] = keep[pts.Count - 1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, pts.Count - 1));
            while (stack.Count > 0)
            {
                var (s, e) = stack.Pop();
                if (e - s < 2) continue;
                double dx = pts[e][0] - pts[s][0], dy = pts[e][1] - pts[s][1];
                var len = Math.Sqrt(dx * dx + dy * dy);
                var idx = -1;
                double dmax = -1;
                for (var k = s + 1; k < e; k++)
                {
                    double px = pts[k][0] - pts[s][0], py = pts[k][1] - pts[s][1];
                    var d = len < 1e-15 ? Math.Sqrt(px * px + py * py) : Math.Abs(px * dy - py * dx) / len;
                    if (d > dmax)
                    {
                        dmax = d;
                        idx = k;
                    }
                }
                if (dmax > tol)
                {
                    keep[idx] = true;
                    stack.Push((s, idx));
                    stack.Push((idx, e));
                }
            }
            var r = new List<double[]>();
            for (var k = 0; k < pts.Count; k++) if (keep[k]) r.Add(pts[k]);
            return r;
        }

        /// <summary>Cells reachable from the grid border through unmarked cells (4-connected): the outside of the region.</summary>
        public static bool[,] Outside(bool[,] m)
        {
            int nu = m.GetLength(0), nv = m.GetLength(1);
            var o = new bool[nu, nv];
            var q = new Queue<(int, int)>();
            void Seed(int i, int j)
            {
                if (!m[i, j] && !o[i, j])
                {
                    o[i, j] = true;
                    q.Enqueue((i, j));
                }
            }
            for (var i = 0; i < nu; i++)
            {
                Seed(i, 0);
                Seed(i, nv - 1);
            }
            for (var j = 0; j < nv; j++)
            {
                Seed(0, j);
                Seed(nu - 1, j);
            }
            while (q.Count > 0)
            {
                var (i, j) = q.Dequeue();
                if (i > 0) Seed(i - 1, j);
                if (j > 0) Seed(i, j - 1);
                if (i < nu - 1) Seed(i + 1, j);
                if (j < nv - 1) Seed(i, j + 1);
            }
            return o;
        }

        /// <summary>
        /// Euclidean distance (in cells) from each cell of `region` to the nearest cell not in it (the grid border counts as
        /// outside). Two-pass exact squared EDT (Felzenszwalb and Huttenlocher).
        /// </summary>
        public static double[,] DistanceToOutside(bool[,] region)
        {
            int nu = region.GetLength(0), nv = region.GetLength(1);
            const double Inf = 1e20;
            // Pad by one so the border counts as outside.
            int pu = nu + 2, pv = nv + 2;
            var f = new double[pu, pv];
            for (var i = 0; i < pu; i++)
                for (var j = 0; j < pv; j++)
                    f[i, j] = i > 0 && j > 0 && i <= nu && j <= nv && region[i - 1, j - 1] ? Inf : 0;
            var col = new double[Math.Max(pu, pv)];
            var res = new double[Math.Max(pu, pv)];
            for (var i = 0; i < pu; i++)
            {
                for (var j = 0; j < pv; j++) col[j] = f[i, j];
                Edt1(col, pv, res);
                for (var j = 0; j < pv; j++) f[i, j] = res[j];
            }
            for (var j = 0; j < pv; j++)
            {
                for (var i = 0; i < pu; i++) col[i] = f[i, j];
                Edt1(col, pu, res);
                for (var i = 0; i < pu; i++) f[i, j] = res[i];
            }
            var d = new double[nu, nv];
            for (var i = 0; i < nu; i++)
                for (var j = 0; j < nv; j++)
                    d[i, j] = Math.Sqrt(f[i + 1, j + 1]);
            return d;
        }

        private static void Edt1(double[] f, int n, double[] d)
        {
            var v = new int[n];
            var z = new double[n + 1];
            var k = 0;
            v[0] = 0;
            z[0] = double.NegativeInfinity;
            z[1] = double.PositiveInfinity;
            for (var q = 1; q < n; q++)
            {
                var s = (f[q] + (double)q * q - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                while (s <= z[k])
                {
                    k--; // z[0] is -infinity, so this stops at k = 0
                    s = (f[q] + (double)q * q - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = double.PositiveInfinity;
            }
            k = 0;
            for (var q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                d[q] = (double)(q - v[k]) * (q - v[k]) + f[v[k]];
            }
        }

        /// <summary>8-connected components of the marked cells, largest first.</summary>
        public static List<List<(int I, int J)>> Components(bool[,] m)
        {
            int nu = m.GetLength(0), nv = m.GetLength(1);
            var seen = new bool[nu, nv];
            var result = new List<List<(int, int)>>();
            for (var i = 0; i < nu; i++)
            {
                for (var j = 0; j < nv; j++)
                {
                    if (!m[i, j] || seen[i, j]) continue;
                    var comp = new List<(int, int)>();
                    var q = new Queue<(int, int)>();
                    seen[i, j] = true;
                    q.Enqueue((i, j));
                    while (q.Count > 0)
                    {
                        var (a, b) = q.Dequeue();
                        comp.Add((a, b));
                        for (var da = -1; da <= 1; da++)
                            for (var db = -1; db <= 1; db++)
                            {
                                int x = a + da, y = b + db;
                                if (x < 0 || y < 0 || x >= nu || y >= nv || seen[x, y] || !m[x, y]) continue;
                                seen[x, y] = true;
                                q.Enqueue((x, y));
                            }
                    }
                    result.Add(comp);
                }
            }
            return result.OrderByDescending(c => c.Count).ToList();
        }
    }
}

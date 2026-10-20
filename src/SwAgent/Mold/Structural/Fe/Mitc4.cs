using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Mold.Structural.Fe
{
    /// <summary>
    /// MITC4 Mindlin plate element (Bathe and Dvorkin 1985) on a rectangle hx x hy. Node order (-1,-1), (1,-1), (1,1), (-1,1);
    /// DOFs per node (w, bx, by) with u = -z bx, v = -z by. Curvatures k = [bx,x; by,y; bx,y + by,x]; shear strains
    /// g = [w,x - bx; w,y - by]. gx is tied at the edge midpoints (0, -1) and (0, 1) and interpolated in eta; gy at (-1, 0) and
    /// (1, 0), interpolated in xi: no shear locking down to t/b = 0.01 and no spurious modes on rectangles.
    /// Port of N/proto/plate.py.
    /// </summary>
    public static class Mitc4
    {
        internal static readonly double[] XiN = { -1, 1, 1, -1 };
        internal static readonly double[] EtN = { -1, -1, 1, 1 };
        internal static readonly double G1 = 1.0 / Math.Sqrt(3.0);

        public static double N(int i, double xi, double et) => 0.25 * (1 + xi * XiN[i]) * (1 + et * EtN[i]);
        static double Nx(int i, double xi, double et, double hx) => 0.25 * XiN[i] * (1 + et * EtN[i]) * 2 / hx;
        static double Ny(int i, double xi, double et, double hy) => 0.25 * EtN[i] * (1 + xi * XiN[i]) * 2 / hy;

        /// <summary>Bending strain matrix (3 x 12) at (xi, eta).</summary>
        public static double[,] Bb(double xi, double et, double hx, double hy)
        {
            var b = new double[3, 12];
            for (int i = 0; i < 4; i++)
            {
                double dx = Nx(i, xi, et, hx), dy = Ny(i, xi, et, hy);
                b[0, 3 * i + 1] = dx; b[1, 3 * i + 2] = dy; b[2, 3 * i + 1] = dy; b[2, 3 * i + 2] = dx;
            }
            return b;
        }

        static double[] Bgx(double xi, double et, double hx)
        {
            var r = new double[12];
            for (int i = 0; i < 4; i++) { r[3 * i] = Nx(i, xi, et, hx); r[3 * i + 1] = -N(i, xi, et); }
            return r;
        }

        static double[] Bgy(double xi, double et, double hy)
        {
            var r = new double[12];
            for (int i = 0; i < 4; i++) { r[3 * i] = Ny(i, xi, et, hy); r[3 * i + 2] = -N(i, xi, et); }
            return r;
        }

        /// <summary>Assumed (tied) shear strain matrix (2 x 12) at (xi, eta).</summary>
        public static double[,] Bs(double xi, double et, double hx, double hy)
        {
            double[] gA = Bgx(0, -1, hx), gC = Bgx(0, 1, hx), gD = Bgy(-1, 0, hy), gB = Bgy(1, 0, hy);
            var b = new double[2, 12];
            for (int k = 0; k < 12; k++)
            {
                b[0, k] = (1 - et) / 2 * gA[k] + (1 + et) / 2 * gC[k];
                b[1, k] = (1 - xi) / 2 * gD[k] + (1 + xi) / 2 * gB[k];
            }
            return b;
        }

        /// <summary>The 2x2 Gauss points in node order: point g sits at (XiN[g], EtN[g]) / sqrt(3).</summary>
        public static (double xi, double et) Gauss(int g) => (XiN[g] * G1, EtN[g] * G1);

        /// <summary>12 x 12 stiffness of one rectangle (row-major). Bending and shear both 2x2 Gauss (exact for the tied field).</summary>
        public static double[] Stiffness(double hx, double hy, double t, double e, double nu, double kappa)
        {
            double d = e * t * t * t / (12 * (1 - nu * nu)), ks = kappa * e / (2 * (1 + nu)) * t, detJ = hx * hy / 4;
            var db = new double[,] { { d, nu * d, 0 }, { nu * d, d, 0 }, { 0, 0, d * (1 - nu) / 2 } };
            var k = new double[144];
            for (int g = 0; g < 4; g++)
            {
                var (xi, et) = Gauss(g);
                var bb = Bb(xi, et, hx, hy);
                var bs = Bs(xi, et, hx, hy);
                for (int r = 0; r < 12; r++)
                    for (int c = 0; c < 12; c++)
                    {
                        double s = 0;
                        for (int p = 0; p < 3; p++)
                        {
                            if (bb[p, r] == 0) continue;
                            for (int q = 0; q < 3; q++) s += bb[p, r] * db[p, q] * bb[q, c];
                        }
                        s += ks * (bs[0, r] * bs[0, c] + bs[1, r] * bs[1, c]);
                        k[r * 12 + c] += s * detJ;
                    }
            }
            return k;
        }

        // ---- exact integrals of the 1D linear shape functions over a sub-interval (used for patches and foundations) ----

        /// <summary>Integrals over [a, b] inside the element [x0, x1] of NL, NR (NL = 1 at x0) and of their products.</summary>
        internal struct Lin
        {
            public double L, R, LL, LR, RR;
        }

        internal static Lin Integrals(double x0, double x1, double a, double b)
        {
            double h = x1 - x0;
            a = Math.Max(a, x0); b = Math.Min(b, x1);
            var r = new Lin();
            if (b <= a) return r;
            double pa = x1 - a, pb = x1 - b, ua = a - x0, ub = b - x0;
            r.L = (pa * pa - pb * pb) / (2 * h);
            r.R = (ub * ub - ua * ua) / (2 * h);
            r.LL = (pa * pa * pa - pb * pb * pb) / (3 * h * h);
            r.RR = (ub * ub * ub - ua * ua * ua) / (3 * h * h);
            r.LR = (h * (ub * ub - ua * ua) / 2 - (ub * ub * ub - ua * ua * ua) / 3) / (h * h);
            return r;
        }

        // node i uses the left (L) or right (R) 1D function in x and y: 0 (L,L), 1 (R,L), 2 (R,R), 3 (L,R)
        static readonly bool[] RightX = { false, true, true, false };
        static readonly bool[] RightY = { false, false, true, true };

        /// <summary>Consistent nodal loads (w rows) of pressure p over the overlap of the element with a rectangle.</summary>
        internal static void PatchLoad(double x0, double x1, double y0, double y1, double rx1, double rx2, double ry1, double ry2, double p, double[] f4)
        {
            var ix = Integrals(x0, x1, rx1, rx2);
            var iy = Integrals(y0, y1, ry1, ry2);
            for (int i = 0; i < 4; i++)
                f4[i] += p * (RightX[i] ? ix.R : ix.L) * (RightY[i] ? iy.R : iy.L);
        }

        /// <summary>Consistent foundation matrix k * integral(Ni Nj) (w rows, 4 x 4) over the overlap with a rectangle.</summary>
        internal static void FoundationMatrix(double x0, double x1, double y0, double y1, double rx1, double rx2, double ry1, double ry2, double k, double[] k16)
        {
            var ix = Integrals(x0, x1, rx1, rx2);
            var iy = Integrals(y0, y1, ry1, ry2);
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double px = RightX[i] == RightX[j] ? (RightX[i] ? ix.RR : ix.LL) : ix.LR;
                    double py = RightY[i] == RightY[j] ? (RightY[i] ? iy.RR : iy.LL) : iy.LR;
                    k16[i * 4 + j] += k * px * py;
                }
        }
    }

    /// <summary>Rectangular grid lines: near-uniform, passing exactly through every requested line.</summary>
    public static class GridLines
    {
        public static double[] Build(double length, IEnumerable<double> lines, double h, int count = 0)
        {
            double eps = 1e-9 * length;
            var bp = new List<double> { 0, length };
            if (lines != null)
                foreach (var v in lines)
                    if (v > eps && v < length - eps) bp.Add(v);
            bp.Sort();
            var pts = new List<double> { bp[0] };
            foreach (var v in bp.Skip(1))
                if (v - pts[pts.Count - 1] > 1e-7 * length) pts.Add(v);
            if (pts[pts.Count - 1] < length) pts[pts.Count - 1] = length;
            if (count > 0) h = length / count;
            var xs = new List<double> { 0 };
            for (int s = 0; s + 1 < pts.Count; s++)
            {
                double len = pts[s + 1] - pts[s];
                int n = Math.Max(1, (int)Math.Round(len / h));
                for (int k = 1; k <= n; k++) xs.Add(k == n ? pts[s + 1] : pts[s] + len * k / n);
            }
            return xs.ToArray();
        }
    }
}

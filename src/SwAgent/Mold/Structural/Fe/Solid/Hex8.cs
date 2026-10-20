using System;

namespace SwAgent.Mold.Structural.Fe.Solid
{
    /// <summary>
    /// 8-node brick with Wilson-Taylor incompatible modes (1 - xi^2, 1 - eta^2, 1 - zeta^2 per displacement component, condensed
    /// out) on an axis-aligned box hx x hy x hz. On boxes the Jacobian is constant and the modes' derivatives integrate to zero,
    /// so the element passes the patch test and represents pure bending exactly: one element through a wall bends correctly,
    /// which is what makes a coarse 3D wall model affordable. Node order: (-1,-1,-1), (1,-1,-1), (1,1,-1), (-1,1,-1), then the
    /// same at zeta = +1. DOFs (ux, uy, uz) per node; strains [exx, eyy, ezz, gxy, gyz, gzx].
    /// </summary>
    public static class Hex8
    {
        internal static readonly int[] Xi = { -1, 1, 1, -1, -1, 1, 1, -1 };
        internal static readonly int[] Et = { -1, -1, 1, 1, -1, -1, 1, 1 };
        internal static readonly int[] Ze = { -1, -1, -1, -1, 1, 1, 1, 1 };

        public static double[,] Elasticity(double e, double nu)
        {
            double l = e * nu / ((1 + nu) * (1 - 2 * nu)), g = e / (2 * (1 + nu));
            var c = new double[6, 6];
            for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) c[i, j] = l; c[i, i] = l + 2 * g; c[i + 3, i + 3] = g; }
            return c;
        }

        // Adds the strain rows of a "shape" with Cartesian derivatives (dx, dy, dz) driving displacement component comp into column col.
        static void Put(double[,] b, int col, int comp, double dx, double dy, double dz)
        {
            switch (comp)
            {
                case 0: b[0, col] += dx; b[3, col] += dy; b[5, col] += dz; break;
                case 1: b[1, col] += dy; b[3, col] += dx; b[4, col] += dz; break;
                default: b[2, col] += dz; b[4, col] += dy; b[5, col] += dx; break;
            }
        }

        static void Matrices(double xi, double et, double ze, double hx, double hy, double hz, out double[,] bu, out double[,] ba)
        {
            bu = new double[6, 24];
            ba = new double[6, 9];
            for (int i = 0; i < 8; i++)
            {
                double dx = Xi[i] * (1 + et * Et[i]) * (1 + ze * Ze[i]) / 8 * 2 / hx;
                double dy = Et[i] * (1 + xi * Xi[i]) * (1 + ze * Ze[i]) / 8 * 2 / hy;
                double dz = Ze[i] * (1 + xi * Xi[i]) * (1 + et * Et[i]) / 8 * 2 / hz;
                for (int c = 0; c < 3; c++) Put(bu, 3 * i + c, c, dx, dy, dz);
            }
            // incompatible modes: mode k (0: 1 - xi^2, 1: 1 - eta^2, 2: 1 - zeta^2), component c -> column 3 k + c
            double[] d = { -2 * xi * 2 / hx, -2 * et * 2 / hy, -2 * ze * 2 / hz };
            for (int k = 0; k < 3; k++)
                for (int c = 0; c < 3; c++)
                    Put(ba, 3 * k + c, c, k == 0 ? d[0] : 0, k == 1 ? d[1] : 0, k == 2 ? d[2] : 0);
        }

        /// <summary>Condensed 24 x 24 stiffness (row-major) and the recovery matrix R (9 x 24) with alpha = -R u.</summary>
        public static double[] Stiffness(double hx, double hy, double hz, double e, double nu, out double[] recover)
        {
            var c = Elasticity(e, nu);
            double g = 1 / Math.Sqrt(3), detJ = hx * hy * hz / 8;
            var kuu = new double[24, 24];
            var kua = new double[24, 9];
            var kaa = new double[9, 9];
            for (int p = 0; p < 8; p++)
            {
                Matrices(Xi[p] * g, Et[p] * g, Ze[p] * g, hx, hy, hz, out var bu, out var ba);
                var cbu = Mul(c, bu, 24);
                var cba = Mul(c, ba, 9);
                for (int r = 0; r < 24; r++)
                {
                    for (int s = 0; s < 24; s++) { double v = 0; for (int q = 0; q < 6; q++) v += bu[q, r] * cbu[q, s]; kuu[r, s] += v * detJ; }
                    for (int s = 0; s < 9; s++) { double v = 0; for (int q = 0; q < 6; q++) v += bu[q, r] * cba[q, s]; kua[r, s] += v * detJ; }
                }
                for (int r = 0; r < 9; r++)
                    for (int s = 0; s < 9; s++) { double v = 0; for (int q = 0; q < 6; q++) v += ba[q, r] * cba[q, s]; kaa[r, s] += v * detJ; }
            }
            var inv = Invert(kaa);
            recover = new double[9 * 24];
            for (int r = 0; r < 9; r++)
                for (int s = 0; s < 24; s++)
                {
                    double v = 0;
                    for (int q = 0; q < 9; q++) v += inv[r, q] * kua[s, q];
                    recover[r * 24 + s] = v;
                }
            var k = new double[24 * 24];
            for (int r = 0; r < 24; r++)
                for (int s = 0; s < 24; s++)
                {
                    double v = kuu[r, s];
                    for (int q = 0; q < 9; q++) v -= kua[r, q] * recover[q * 24 + s];
                    k[r * 24 + s] = v;
                }
            // symmetrise round-off
            for (int r = 0; r < 24; r++)
                for (int s = r + 1; s < 24; s++) { double v = (k[r * 24 + s] + k[s * 24 + r]) / 2; k[r * 24 + s] = k[s * 24 + r] = v; }
            return k;
        }

        /// <summary>Stress [sxx, syy, szz, txy, tyz, tzx] at (xi, eta, zeta) from the element's nodal displacements (24).</summary>
        public static double[] Stress(double hx, double hy, double hz, double e, double nu, double[] recover, double[] ue, double xi = 0, double et = 0, double ze = 0)
        {
            var alpha = new double[9];
            for (int r = 0; r < 9; r++) { double v = 0; for (int s = 0; s < 24; s++) v -= recover[r * 24 + s] * ue[s]; alpha[r] = v; }
            Matrices(xi, et, ze, hx, hy, hz, out var bu, out var ba);
            var eps = new double[6];
            for (int q = 0; q < 6; q++)
            {
                double v = 0;
                for (int s = 0; s < 24; s++) v += bu[q, s] * ue[s];
                for (int s = 0; s < 9; s++) v += ba[q, s] * alpha[s];
                eps[q] = v;
            }
            var c = Elasticity(e, nu);
            var sig = new double[6];
            for (int q = 0; q < 6; q++) for (int s = 0; s < 6; s++) sig[q] += c[q, s] * eps[s];
            return sig;
        }

        static double[,] Mul(double[,] c, double[,] b, int n)
        {
            var r = new double[6, n];
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < n; j++) { double v = 0; for (int k = 0; k < 6; k++) v += c[i, k] * b[k, j]; r[i, j] = v; }
            return r;
        }

        internal static double[,] Invert(double[,] a)
        {
            int n = a.GetLength(0);
            var m = new double[n, 2 * n];
            for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) m[i, j] = a[i, j]; m[i, n + i] = 1; }
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
                if (Math.Abs(m[piv, col]) < 1e-300) throw new InvalidOperationException("Singular incompatible-mode block.");
                if (piv != col) for (int j = 0; j < 2 * n; j++) { var t = m[col, j]; m[col, j] = m[piv, j]; m[piv, j] = t; }
                double d = m[col, col];
                for (int j = 0; j < 2 * n; j++) m[col, j] /= d;
                for (int r = 0; r < n; r++)
                {
                    if (r == col || m[r, col] == 0) continue;
                    double f = m[r, col];
                    for (int j = 0; j < 2 * n; j++) m[r, j] -= f * m[col, j];
                }
            }
            var inv = new double[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = m[i, n + j];
            return inv;
        }
    }
}

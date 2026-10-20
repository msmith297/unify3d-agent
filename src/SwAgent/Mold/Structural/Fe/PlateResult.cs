using System;
using System.Collections.Generic;

namespace SwAgent.Mold.Structural.Fe
{
    /// <summary>
    /// Results of a plate solve on the grid Xs x Ys. Nodal arrays are indexed [i, j] = (Xs[i], Ys[j]).
    /// Moments are per unit length (N mm/mm), sagging-positive: Mx = -D (bx,x + nu by,y), My likewise, Mxy = -D (1 - nu)/2 (bx,y + by,x).
    /// Interior moments come from the Gauss points (extrapolated, averaged); clamped edges from the nodal reactions; free edges from
    /// the first Gauss row with Mn = Mnt = 0 (section 2.4 of the design).
    /// </summary>
    public sealed class PlateResult
    {
        public double[] Xs, Ys;
        public double[,] W, Mx, My, Mxy;
        /// <summary>Largest absolute principal moment at each node.</summary>
        public double[,] MPrincipal;
        /// <summary>Support force on the plate at each node along +w (rigid supports only; 0 elsewhere).</summary>
        public double[,] Reactions;

        /// <summary>Deflection of largest magnitude (signed) and where it is.</summary>
        public double WMax;
        public (double X, double Y) WMaxAt;
        /// <summary>Largest absolute principal moment, sigma = 6 M / t^2, and where.</summary>
        public double MMax, SigmaMax;
        public (double X, double Y) SigmaMaxAt;

        /// <summary>Normal bending moment along each edge, keyed "x0", "xa", "y0", "yb" (nodes in grid order).</summary>
        public Dictionary<string, double[]> EdgeMoments = new Dictionary<string, double[]>();

        /// <summary>Total applied load (sum of consistent nodal loads, N), the rigid supports' force, the foundations' and the springs' (all along +w on the plate).</summary>
        public double AppliedLoad, SupportForce, FoundationForce, SpringForce;
        /// <summary>Moments of the support forces about the origin: sum R x, sum R y (equilibrium checks).</summary>
        public double SupportMomentX, SupportMomentY, AppliedMomentX, AppliedMomentY, OtherMomentX, OtherMomentY;

        public int Dofs, Band, Passes;
        /// <summary>Fraction of the unilateral foundation area still in contact after the last pass (1 when none).</summary>
        public double ContactFraction = 1;
        public double Thickness;
        public TimeSpan Elapsed;

        /// <summary>Bilinear interpolation of w at (x, y).</summary>
        public double WAt(double x, double y) => Interp(W, x, y);
        public double Interp(double[,] f, double x, double y)
        {
            int i = Locate(Xs, x), j = Locate(Ys, y);
            double u = (x - Xs[i]) / (Xs[i + 1] - Xs[i]), v = (y - Ys[j]) / (Ys[j + 1] - Ys[j]);
            return (1 - u) * (1 - v) * f[i, j] + u * (1 - v) * f[i + 1, j] + u * v * f[i + 1, j + 1] + (1 - u) * v * f[i, j + 1];
        }

        internal static int Locate(double[] xs, double x)
        {
            int lo = 0, hi = xs.Length - 2;
            if (x <= xs[0]) return 0;
            if (x >= xs[xs.Length - 1]) return hi;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (xs[mid] <= x) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>Largest |w| among the nodes where include(x, y) holds (signed), or 0 when none.</summary>
        public double MaxW(Func<double, double, bool> include, out (double X, double Y) at)
        {
            double best = 0; at = (double.NaN, double.NaN);
            for (int i = 0; i < Xs.Length; i++)
                for (int j = 0; j < Ys.Length; j++)
                    if (include(Xs[i], Ys[j]) && Math.Abs(W[i, j]) > Math.Abs(best)) { best = W[i, j]; at = (Xs[i], Ys[j]); }
            return best;
        }

        /// <summary>Largest absolute principal moment among the nodes where include(x, y) holds.</summary>
        public double MaxMoment(Func<double, double, bool> include, out (double X, double Y) at)
        {
            double best = 0; at = (double.NaN, double.NaN);
            for (int i = 0; i < Xs.Length; i++)
                for (int j = 0; j < Ys.Length; j++)
                    if (include(Xs[i], Ys[j]) && MPrincipal[i, j] > best) { best = MPrincipal[i, j]; at = (Xs[i], Ys[j]); }
            return best;
        }

        public static double Principal(double mx, double my, double mxy)
        {
            double c = (mx + my) / 2, r = Math.Sqrt((mx - my) * (mx - my) / 4 + mxy * mxy);
            return Math.Max(Math.Abs(c + r), Math.Abs(c - r));
        }
    }
}

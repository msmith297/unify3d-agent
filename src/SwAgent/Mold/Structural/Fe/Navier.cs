using System;

namespace SwAgent.Mold.Structural.Fe
{
    /// <summary>
    /// Navier double-sine series for a rectangular plate a x b with hard simple supports on all four edges (Reddy, Theory and
    /// Analysis of Elastic Plates and Shells, 2nd ed.). With shear on (FSDT, kappa_s = 5/6):
    /// w = sum q_mn [1/(D k^4) + 1/(kappa G t k^2)] sin(am x) sin(bn y), k^2 = am^2 + bn^2. For hard SS plates the FSDT moments
    /// equal the Kirchhoff moments, so Mx = D (am^2 + nu bn^2) wK_mn. The reference for T1, T2, T6, T8 and the Sweep 1 gate.
    /// </summary>
    public static class Navier
    {
        /// <summary>Uniform pressure q over the whole plate. Returns w, Mx, My (sagging-positive) at (x, y).</summary>
        public static (double W, double Mx, double My) SimplySupported(double a, double b, double t, double e, double nu, double q,
            double x, double y, bool shear = true, int terms = 199)
            => Patch(a, b, t, e, nu, q, 0, a, 0, b, x, y, shear, terms);

        /// <summary>Pressure q on the rectangle [x1, x2] x [y1, y2]; every m, n up to terms (even terms vanish for symmetric loads).</summary>
        public static (double W, double Mx, double My) Patch(double a, double b, double t, double e, double nu, double q,
            double x1, double x2, double y1, double y2, double x, double y, bool shear = true, int terms = 199, double kappa = 5.0 / 6.0)
        {
            double d = e * t * t * t / (12 * (1 - nu * nu)), ks = kappa * e / (2 * (1 + nu)) * t;
            bool full = x1 <= 0 && x2 >= a && y1 <= 0 && y2 >= b;
            // Sum the slow series by increasing m, n; terms of the full-plate load vanish for even m or n.
            var sx = new double[terms + 1]; var cx = new double[terms + 1];
            var sy = new double[terms + 1]; var cy = new double[terms + 1];
            for (int m = 1; m <= terms; m++)
            {
                double am = m * Math.PI / a;
                sx[m] = Math.Sin(am * x);
                cx[m] = (Math.Cos(am * x1) - Math.Cos(am * x2)) / am;
            }
            for (int n = 1; n <= terms; n++)
            {
                double bn = n * Math.PI / b;
                sy[n] = Math.Sin(bn * y);
                cy[n] = (Math.Cos(bn * y1) - Math.Cos(bn * y2)) / bn;
            }
            double w = 0, mx = 0, my = 0;
            int step = full ? 2 : 1;
            for (int m = 1; m <= terms; m += step)
            {
                double am = m * Math.PI / a;
                if (cx[m] == 0) continue;
                for (int n = 1; n <= terms; n += step)
                {
                    double bn = n * Math.PI / b, k2 = am * am + bn * bn;
                    double qmn = 4 * q / (a * b) * cx[m] * cy[n];
                    if (qmn == 0) continue;
                    double s = sx[m] * sy[n];
                    double wk = qmn / (d * k2 * k2);
                    w += (wk + (shear ? qmn / (ks * k2) : 0)) * s;
                    mx += d * (am * am + nu * bn * bn) * wk * s;
                    my += d * (bn * bn + nu * am * am) * wk * s;
                }
            }
            return (w, mx, my);
        }
    }

    /// <summary>
    /// Levy series (Kirchhoff, thin) for a uniformly loaded plate simply supported on y = 0 and y = b and free on x = -a/2 and
    /// x = +a/2: the "rails" case. The free edges carry more than the beam value: at the middle of a free edge
    /// M_y = 0.131 q b^2 (a/b = 1) to 0.133 q b^2 (long plates), i.e. beta = 0.787-0.798 against the beam's 0.75, and
    /// w = 0.0150-0.0152 q b^4 / D (Timoshenko and Woinowsky-Krieger section 47). The Sweep 1 free-edge gate uses this.
    /// </summary>
    public static class Levy
    {
        /// <summary>w and My (sagging-positive) at (x, y) for flexural rigidity d; x is measured from the centre line between the free edges.</summary>
        public static (double W, double My) Rails(double a, double b, double d, double nu, double q, double x, double y, int terms = 301)
        {
            double w = 0, my = 0;
            for (int m = 1; m <= terms; m += 2)
            {
                double be = m * Math.PI / b, qm = 4 * q / (m * Math.PI), p = qm / (d * Math.Pow(be, 4));
                double xEdge = be * a / 2, th = Math.Tanh(xEdge);
                // unknowns scaled by cosh(xEdge): f = p + A cosh(be x)/cosh(X) + B be x sinh(be x)/cosh(X)
                double a11 = 1 - nu, a12 = 2 + xEdge * th - nu * xEdge * th, r1 = nu * p;
                double a21 = th - (2 - nu) * th, a22 = 3 * th + xEdge - (2 - nu) * (th + xEdge);
                double det = a11 * a22 - a12 * a21, ca = r1 * a22 / det, cb = -a21 * r1 / det;
                double u = be * Math.Abs(x);
                double cr = Math.Exp(u - xEdge) * (1 + Math.Exp(-2 * u)) / (1 + Math.Exp(-2 * xEdge));
                double sr = Math.Exp(u - xEdge) * (1 - Math.Exp(-2 * u)) / (1 + Math.Exp(-2 * xEdge));
                double f = p + ca * cr + cb * u * sr;
                double fpp = be * be * (ca * cr + cb * (2 * cr + u * sr));
                double s = Math.Sin(be * y);
                w += f * s;
                my += d * (be * be * f - nu * fpp) * s;
            }
            return (w, my);
        }

        /// <summary>Thin-plate alpha (w E t^3 / (q b^4)) and beta (6 M / (q b^2)) at the middle of the free edge.</summary>
        public static (double Alpha, double Beta) RailsFreeEdge(double aOverB, double nu)
        {
            double d = 1.0 / (12 * (1 - nu * nu));
            var (w, my) = Rails(aOverB, 1, d, nu, 1, aOverB / 2, 0.5);
            return (w, 6 * my);
        }
    }

    /// <summary>Closed forms the sweep checks against.</summary>
    public static class ClosedForm
    {
        /// <summary>
        /// Cantilever strip with shear (the infinitely long wall, clamped root, free top): w = alpha p h^4 / (E w^3) with
        /// alpha = 1.5 (1 - nu^2) + (1 + nu) tau^2 / kappa (1.365 + 1.56 tau^2 at nu = 0.3), beta = 3 (sigma = beta p h^2 / w^2).
        /// </summary>
        public static (double Alpha, double Beta) CantileverStrip(double nu, double tau, double kappa = 5.0 / 6.0)
            => (1.5 * (1 - nu * nu) + (1 + nu) * tau * tau / kappa, 3.0);

        /// <summary>Simply supported one-way strip of span b (cylindrical bending, with shear): alpha = (5/32)(1 - nu^2) + (1 + nu) tau^2 / (4 kappa), beta = 0.75.</summary>
        public static (double Alpha, double Beta) SimpleStrip(double nu, double tau, double kappa = 5.0 / 6.0)
            => (5.0 / 32.0 * (1 - nu * nu) + (1 + nu) * tau * tau / (4 * kappa), 0.75);

        /// <summary>
        /// Roark 7th ed. Table 11.4, nu = 0.3, thin plates: (a/b, alpha, beta). Case 1a (all edges simply supported, uniform load,
        /// beta at the centre) and case 8a (all edges fixed; beta1 at the middle of the long edge).
        /// </summary>
        public static readonly double[,] RoarkSS = {
            { 1.0, 0.0444, 0.2874 }, { 1.2, 0.0616, 0.3762 }, { 1.4, 0.0770, 0.4530 }, { 1.6, 0.0906, 0.5172 },
            { 1.8, 0.1017, 0.5688 }, { 2.0, 0.1110, 0.6102 }, { 3.0, 0.1335, 0.7134 }, { 4.0, 0.1400, 0.7410 }, { 5.0, 0.1417, 0.7476 },
        };
        public static readonly double[,] RoarkClamped = {
            { 1.0, 0.0138, 0.3078 }, { 1.2, 0.0188, 0.3834 }, { 1.4, 0.0226, 0.4356 }, { 1.6, 0.0251, 0.4680 },
            { 1.8, 0.0267, 0.4872 }, { 2.0, 0.0277, 0.4974 },
        };
    }
}

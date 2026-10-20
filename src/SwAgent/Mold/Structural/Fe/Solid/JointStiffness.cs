using System;

namespace SwAgent.Mold.Structural.Fe
{
    /// <summary>
    /// Closed-form member and bolt stiffness of a bolted joint, the references the axisymmetric FE check is compared with
    /// (Shigley, Mechanical Engineering Design, 10th ed., chapter 8; VDI 2230 Part 1 cone model). Lengths and E in any
    /// consistent units; stiffness comes back in force/length.
    /// </summary>
    public static class JointStiffness
    {
        /// <summary>Wileman et al. (1991) constants (A, B) of k_m = E d A exp(B d / l), Shigley Table 8-8.</summary>
        public static readonly (double A, double B) WilemanSteel = (0.78715, 0.62873);
        public static readonly (double A, double B) WilemanAluminium = (0.79670, 0.63816);
        public static readonly (double A, double B) WilemanCopper = (0.79568, 0.63553);
        public static readonly (double A, double B) WilemanCastIron = (0.77871, 0.61616);
        public static readonly (double A, double B) WilemanGeneral = (0.78952, 0.62914);

        /// <summary>Member stiffness of two identical members, total grip l (Shigley eq. 8-23).</summary>
        public static double Wileman(double e, double d, double l, (double A, double B) c) => e * d * c.A * Math.Exp(c.B * d / l);

        /// <summary>
        /// One pressure-cone frustum (Shigley eq. 8-20): thickness t, washer-face diameter dw, hole diameter dh, half-angle
        /// alpha (30 degrees in Shigley).
        /// </summary>
        public static double Frustum(double e, double t, double dw, double dh, double alphaDeg = 30)
        {
            double ta = Math.Tan(alphaDeg * Math.PI / 180);
            double ln = Math.Log((2 * t * ta + dw - dh) * (dw + dh) / ((2 * t * ta + dw + dh) * (dw - dh)));
            return Math.PI * e * dh * ta / ln;
        }

        /// <summary>Two identical members of total grip l as two frusta in series (cones meet at the joint's mid-plane), dw = 1.5 d by default.</summary>
        public static double FrustumMembers(double e, double d, double l, double dw = 0, double dh = 0, double alphaDeg = 30)
        {
            if (dw <= 0) dw = 1.5 * d;
            if (dh <= 0) dh = d;
            return Frustum(e, l / 2, dw, dh, alphaDeg) / 2;
        }

        /// <summary>
        /// VDI 2230 Part 1 cone model for a through-bolted joint (DSV, w = 1) with a substitute outside diameter da large enough
        /// for a full cone: tan(phi) = 0.362 + 0.032 ln(beta_L / 2) + 0.153 ln(y), beta_L = l / dw, y = da / dw. The resilience is
        /// the two-frustum expression with that angle. Formula as recalled from the standard: verify before quoting it.
        /// </summary>
        public static double VdiCone(double e, double l, double dw, double dh, double da, out double phiDeg)
        {
            double betaL = l / dw, y = da / dw;
            double tanPhi = 0.362 + 0.032 * Math.Log(betaL / 2) + 0.153 * Math.Log(y);
            phiDeg = Math.Atan(tanPhi) * 180 / Math.PI;
            double ln = Math.Log((dw + dh) * (dw + l * tanPhi - dh) / ((dw - dh) * (dw + l * tanPhi + dh)));
            double delta = 2 * ln / (e * Math.PI * dh * tanPhi);
            return 1 / delta;
        }

        /// <summary>Bolt stiffness k_b = Ad At E / (Ad lt + At ld) (Shigley eq. 8-17): ld unthreaded and lt threaded length in the grip.</summary>
        public static double Bolt(double e, double ad, double at, double ld, double lt) => ad * at * e / (ad * lt + at * ld);
    }
}

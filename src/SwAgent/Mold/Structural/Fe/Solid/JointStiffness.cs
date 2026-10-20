using System;

namespace SwAgent.Mold.Structural.Fe
{
    /// <summary>
    /// Closed-form member and bolt stiffness of a bolted joint, the references the axisymmetric FE check is compared with
    /// (Shigley, Mechanical Engineering Design, 10th ed., chapter 8). Lengths and E in any consistent units; stiffness comes back
    /// in force/length. The VDI 2230 Part 1 model is in Vdi2230.
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

        /// <summary>Bolt stiffness k_b = Ad At E / (Ad lt + At ld) (Shigley eq. 8-17): ld unthreaded and lt threaded length in the grip.</summary>
        public static double Bolt(double e, double ad, double at, double ld, double lt) => ad * at * e / (ad * lt + at * ld);
    }

    /// <summary>
    /// VDI 2230 Part 1 (2015) joint model, as reproduced by the worked example in Hochschule Anhalt, "Übung Maschinenelemente:
    /// Schraubenverbindungen, Tragfähigkeitsnachweis nach VDI 2230" (S. Voigt), Berechnungsbeispiel Hydraulikzylinder (slides
    /// 33-46, 54, 63, 70): ISO 4762 M12, l_K 42 mm, d_h 13.5 mm, D_A 80 mm, mean bearing diameter 21.11 mm gives tan phi 0.566,
    /// D_A,Gr 44.9 mm, delta_P 0.3546e-6 mm/N, Phi_K 0.11, F_Mmin 27.6 kN and F_Mmax 46.9 kN (tests: Vdi2230Tests).
    /// Resiliences delta in length/force; w = 1 for a through-bolted joint (DSV), 2 for a tapped one (ESV).
    /// The cone-angle fits hold for y = D_A/d_W up to about 8.5 (the standard's chart); a wider plate is clamped there.
    /// </summary>
    public static class Vdi2230
    {
        public enum Joint { ThroughBolted, Tapped }

        public const double YMax = 8.5;

        static double W(Joint j) => j == Joint.ThroughBolted ? 1 : 2;

        /// <summary>
        /// Substitute cone angle: DSV tan phi = 0.362 + 0.032 ln(beta_L / 2) + 0.153 ln y; ESV tan phi = 0.348 + 0.013 ln beta_L
        /// + 0.193 ln y; beta_L = l_K / d_W, y = D_A / d_W (clamped to YMax).
        /// </summary>
        public static double TanPhi(Joint j, double lK, double dW, double dA)
        {
            double betaL = lK / dW, y = Math.Min(YMax, dA / dW);
            return j == Joint.ThroughBolted
                ? 0.362 + 0.032 * Math.Log(betaL / 2) + 0.153 * Math.Log(y)
                : 0.348 + 0.013 * Math.Log(betaL) + 0.193 * Math.Log(y);
        }

        /// <summary>Limiting outside diameter D_A,Gr = d_W + w l_K tan phi: a plate at least this wide holds the full cone.</summary>
        public static double LimitDiameter(Joint j, double lK, double dW, double tanPhi) => dW + W(j) * lK * tanPhi;

        /// <summary>
        /// Resilience of the clamped plates delta_P: the full cone(s) when D_A >= D_A,Gr; cones plus a sleeve when d_W &lt; D_A &lt;
        /// D_A,Gr; a plain sleeve when D_A &lt;= d_W.
        /// </summary>
        public static double PlateResilience(Joint j, double eP, double lK, double dW, double dh, double dA, out double tanPhi, out double dAGr)
        {
            double w = W(j);
            tanPhi = TanPhi(j, lK, dW, dA);
            dAGr = LimitDiameter(j, lK, dW, tanPhi);
            if (dA <= dW) return 4 * lK / (eP * Math.PI * (dA * dA - dh * dh));
            if (dA >= dAGr)
                return 2 * Math.Log((dW + dh) * (dW + w * lK * tanPhi - dh) / ((dW - dh) * (dW + w * lK * tanPhi + dh))) / (w * eP * Math.PI * dh * tanPhi);
            double cone = 2 / (w * dh * tanPhi) * Math.Log((dW + dh) * (dA - dh) / ((dW - dh) * (dA + dh)));
            double sleeve = 4 / (dA * dA - dh * dh) * (lK - (dA - dW) / (w * tanPhi));
            return (cone + sleeve) / (eP * Math.PI);
        }

        /// <summary>
        /// Resilience of the bolt delta_S: head (0.5 d hexagon, 0.4 d socket head over E A_N), the unthreaded shank sections
        /// (length / E A_N), the free loaded thread (l_Gew / E A_d3), the engaged thread 0.5 d / E A_d3 and the nut or tapped-hole
        /// zone (0.4 d / E A_N for a nut, 0.33 d / E_M A_N for a tapped hole).
        /// </summary>
        public static double BoltResilience(Joint j, double eS, double d, double d3, double shank, double freeThread, bool socketHead, double eM = 0)
        {
            if (eM <= 0) eM = eS;
            double aN = Math.PI * d * d / 4, a3 = Math.PI * d3 * d3 / 4;
            double head = (socketHead ? 0.4 : 0.5) * d / (eS * aN);
            double sections = shank / (eS * aN) + freeThread / (eS * a3);
            double engaged = 0.5 * d / (eS * a3);
            double nut = j == Joint.ThroughBolted ? 0.4 * d / (eS * aN) : 0.33 * d / (eM * aN);
            return head + sections + engaged + nut;
        }

        /// <summary>Load factor Phi_K = delta_P / (delta_S + delta_P); with the load introduction factor n, Phi_n = n Phi_K.</summary>
        public static double LoadFactor(double deltaS, double deltaP, double n = 1) => n * deltaP / (deltaS + deltaP);

        /// <summary>Preload lost to embedding: F_Z = f_Z / (delta_S + delta_P).</summary>
        public static double EmbeddingLoss(double fZ, double deltaS, double deltaP) => fZ / (deltaS + deltaP);

        /// <summary>
        /// Required assembly preload band: F_Mmin = F_KRmin + (1 - Phi_n) F_A + F_Z (+ dF_Vth, a thermal loss), F_Mmax = alpha_A F_Mmin
        /// (tightening factor: the scatter of the tightening method).
        /// </summary>
        public static (double Min, double Max) PreloadBand(double fKRmin, double fA, double phiN, double fZ, double alphaA, double dFVth = 0)
        {
            double min = fKRmin + (1 - phiN) * fA + fZ + Math.Max(0, dFVth);
            return (min, alphaA * min);
        }
    }
}

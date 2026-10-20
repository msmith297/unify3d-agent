using System;
using System.Collections.Generic;

namespace SwAgent.Mold
{
    /// <summary>Support cases of design 3.8: A flat on the platen (default), B rails, C opening supported all round, D overhang,
    /// E pillars, F ram/platen smaller than the lid (FE only).</summary>
    public enum Support { A, B, C, D, E, F }

    public sealed class SpanResult
    {
        public string Bc;
        public double T, Alpha, Beta, Sigma, Deflection, BMax;
        public bool TooThick;
        public double TSteelEquivalent;
    }

    /// <summary>
    /// S26-S32. Case A plates are in compression on the platen and do not bend: their thickness comes from the minimum
    /// rules (S26 floor, S27 pocket plate, S28 lid). Cases B-E span b and use the Mindlin tables (S29-S31) or the overhang
    /// strip (S32). Units mm, MPa.
    /// </summary>
    public static class Plates
    {
        /// <summary>(S26) t_floor >= max(min floor, 0.25 h_max, TC well d + 2 x 6 mm when it runs under the cavity, extra).</summary>
        public static double Floor(bool aluminium, double hMax, double? tcWellDia, Cfg cfg, double extra = 0)
        {
            var minF = cfg.D(aluminium ? "minFloor_mm.aluminium" : "minFloor_mm.steel", aluminium ? 12 : 10);
            var t = Math.Max(minF, cfg.D("minFloorDepthFraction", 0.25) * hMax);
            if (tcWellDia != null) t = Math.Max(t, tcWellDia.Value + 2 * cfg.D("steelAroundTcWell_mm", 6));
            return Math.Max(t, extra);
        }

        /// <summary>(S27) pocket plate t >= max(t_floor + h_max, 0.05 L, min plate, counterbore + 6 mm, L_e + 0.5 d + 3 mm).</summary>
        /// <remarks>lengthFraction overrides the 0.05 L rule (0 drops it: critique A2.6, case A lids over the lift limit).</remarks>
        public static double PocketPlate(bool aluminium, double tFloor, double hMax, double longestSide, Cfg cfg, double cboreDepth = 0, double threadNeed = 0, double? lengthFraction = null)
        {
            var t = tFloor + hMax;
            t = Math.Max(t, (lengthFraction ?? cfg.D("minPlateFractionOfLongestSide", 0.05)) * longestSide);
            t = Math.Max(t, cfg.D(aluminium ? "minPlate_mm.aluminium" : "minPlate_mm.steel", aluminium ? 12 : 10));
            if (cboreDepth > 0) t = Math.Max(t, cboreDepth + 6);
            if (threadNeed > 0) t = Math.Max(t, threadNeed);
            return t;
        }

        /// <summary>(S28) lid t >= max(0.05 L, min plate, plug height + 10 mm, hardware).</summary>
        public static double Lid(bool aluminium, double longestSide, Cfg cfg, double plugHeight = 0, double hardware = 0, double? lengthFraction = null)
        {
            var t = (lengthFraction ?? cfg.D("minPlateFractionOfLongestSide", 0.05)) * longestSide;
            t = Math.Max(t, cfg.D(aluminium ? "minPlate_mm.aluminium" : "minPlate_mm.steel", aluminium ? 12 : 10));
            if (plugHeight > 0) t = Math.Max(t, plugHeight + 10);
            return Math.Max(t, hardware);
        }

        public static string BcFor(Support s, bool integralFrame = false)
        {
            switch (s)
            {
                case Support.B: return "Rails";
                case Support.C: return integralFrame ? "C4" : "SS4";
                case Support.E: return "C4";   // pillar panel treated as C4 (design 3.8)
                default: return null;
            }
        }

        /// <summary>
        /// (S29-S31) span of short side b (aspect = long/short, for rails the length along the rails / the span),
        /// t = [alpha p b⁴/(E delta)]^(1/3) by fixed point from 0.2 b; sigma = beta p b²/t² is checked by the caller.
        /// </summary>
        public static SpanResult Span(string bc, double aspect, double b, double p, double E, double nu, double delta, double tauMax, double eSteel = 205000)
        {
            var r = new SpanResult { Bc = bc };
            var t = 0.2 * b;
            for (var k = 0; k < 200; k++)
            {
                var a = Coefficients.Get(bc, aspect, t / b, nu, 1.0).Alpha;
                var tn = Math.Pow(a * p * Math.Pow(b, 4) / (E * delta), 1.0 / 3.0);
                if (Math.Abs(tn - t) < 1e-10 * b) { t = tn; break; }
                t = tn;
            }
            var co = Coefficients.Get(bc, aspect, t / b, nu, 1.0);
            r.T = t;
            r.Alpha = co.Alpha;
            r.Beta = co.Beta;
            r.Sigma = co.Beta * p * b * b / (t * t);
            r.Deflection = co.Alpha * p * Math.Pow(b, 4) / (E * Math.Pow(t, 3));
            r.TooThick = t / b > tauMax;
            r.BMax = MaxSpan(bc, aspect, t, p, E, nu, delta);
            r.TSteelEquivalent = t * Math.Pow(E / eSteel, 1.0 / 3.0);
            return r;
        }

        /// <summary>(S31) the largest span for thickness t: b_max = [E delta t³/(alpha p)]^(1/4) (fixed point on alpha(t/b)).</summary>
        public static double MaxSpan(string bc, double aspect, double t, double p, double E, double nu, double delta)
        {
            var b = 5 * t;
            for (var k = 0; k < 200; k++)
            {
                var a = Coefficients.Get(bc, aspect, t / b, nu, 1.0).Alpha;
                var bn = Math.Pow(E * delta * t * t * t / (a * p), 0.25);
                if (Math.Abs(bn - b) < 1e-10 * b) { b = bn; break; }
                b = bn;
            }
            return b;
        }

        /// <summary>(S32) overhang strip of length c: w = 1.5 p c⁴(1-nu²)/(E t³) + p c²/(2 kappa G t); sigma = 3 p c²/t².</summary>
        public static (double W, double Sigma) Overhang(double p, double c, double t, double E, double nu)
        {
            var G = E / (2 * (1 + nu));
            var w = 1.5 * p * Math.Pow(c, 4) * (1 - nu * nu) / (E * t * t * t) + p * c * c / (2 * (5.0 / 6.0) * G * t);
            return (w, 3 * p * c * c / (t * t));
        }

        /// <summary>(S32) overhang thickness for a deflection limit (bisection on t; w falls monotonically with t).</summary>
        public static double OverhangThickness(double p, double c, double E, double nu, double delta)
        {
            double lo = 0.01, hi = 10 * c + 10;
            for (var k = 0; k < 200; k++)
            {
                var mid = 0.5 * (lo + hi);
                if (Overhang(p, c, mid, E, nu).W > delta) lo = mid; else hi = mid;
            }
            return hi;
        }

        public static Support ParseSupport(string s)
        {
            switch ((s ?? "A").Trim().ToUpperInvariant())
            {
                case "B": case "RAILS": return Support.B;
                case "C": case "OPENING": return Support.C;
                case "D": case "OVERHANG": return Support.D;
                case "E": case "PILLARS": return Support.E;
                case "F": return Support.F;
                default: return Support.A;
            }
        }
    }
}

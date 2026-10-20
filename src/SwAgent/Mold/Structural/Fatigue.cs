using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    public sealed class FatigueResult
    {
        /// <summary>Fully reversed strength at N and T (S15), Goodman R=0 maximum stress (S16), notch factor (S17), allowables.</summary>
        public double Sa, SigmaMaxR0, Kf, AllowFatigue, AllowStatic;
        public double Allow => Math.Min(AllowFatigue, AllowStatic);
    }

    /// <summary>
    /// S15-S19: every shot loads from zero to full (R = 0).
    /// Aluminium: S_N from the grade's fatigue_MPa (R = -1, smooth), log-log between the listed N; below 1e5 a Basquin line
    /// from (1e3, 0.9 S_u,20); machined 0.9; scaled by S_u,T/S_u,20. Steel (Shigley ch. 6): S_e = 0.5 min(S_u, 1400) k_a k_b
    /// k_e with k_a = 4.51 S_u^-0.265, Basquin 1e3-1e6, flat after; the JSON steel S-N numbers are not used.
    /// Goodman at R = 0: sigma_max = 2 S_a S_u,T/(S_u,T + S_a), capped at S_y,T. Allowables S_y,T/2 and sigma_max/(K_f 1.5).
    /// </summary>
    public static class Fatigue
    {
        public static double SaAluminium(JObject grade, double su20, double suT, double N, double ka)
        {
            var pts = new List<KeyValuePair<double, double>>();
            if (grade?["fatigue_MPa"] is JObject f)
                foreach (var p in f.Properties())
                    if (double.TryParse(p.Name, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && Si.Num(p.Value) is double s)
                        pts.Add(new KeyValuePair<double, double>(n, s));
            pts = pts.OrderBy(p => p.Key).ToList();
            double SN;
            if (pts.Count == 0)
                SN = 0.3 * su20;   // no data: a conservative stand-in (about the 5e8 strength of wrought aluminium)
            else if (N <= pts[0].Key)
            {
                // Basquin between (1e3, 0.9 S_u,20) and the first listed point
                double a = 1e3, b = pts[0].Key, Sa_ = 0.9 * su20, Sb = pts[0].Value;
                var n = Math.Max(N, 1e3);
                SN = Sa_ * Math.Pow(Sb / Sa_, Math.Log10(n / a) / Math.Log10(b / a));
            }
            else if (N >= pts[pts.Count - 1].Key) SN = pts[pts.Count - 1].Value;
            else
            {
                SN = pts[pts.Count - 1].Value;
                for (var i = 0; i < pts.Count - 1; i++)
                {
                    var p1 = pts[i];
                    var p2 = pts[i + 1];
                    if (N >= p1.Key && N <= p2.Key)
                    {
                        SN = p1.Value * Math.Pow(p2.Value / p1.Value, Math.Log10(N / p1.Key) / Math.Log10(p2.Key / p1.Key));
                        break;
                    }
                }
            }
            return ka * SN * suT / su20;
        }

        public static double SaSteel(double su20, double suT, double N, double kb, double ke)
        {
            var ka = 4.51 * Math.Pow(su20, -0.265);
            var Se = 0.5 * Math.Min(su20, 1400.0) * ka * kb * ke;
            var f = su20 < 500 ? 0.9 : su20 < 1100 ? 0.86 : 0.80;
            double S;
            if (N >= 1e6) S = Se;
            else
            {
                var n = Math.Max(N, 1e3);
                S = f * su20 * Math.Pow(Se / (f * su20), Math.Log10(n / 1e3) / 3.0);
            }
            return S * suT / su20;
        }

        /// <summary>Peterson-type shoulder-fillet K_t by r/w (S17), from sizing-defaults fatigue.ktByFilletRatio.</summary>
        public static double Kt(double rOverW, Cfg cfg)
        {
            var table = cfg.T("fatigue.ktByFilletRatio") as JArray;
            if (table == null) return 2.5;
            foreach (var row in table.OfType<JArray>())
                if (rOverW >= (double)row[0]) return (double)row[1];
            return 2.5;
        }

        public static FatigueResult Allowables(GradeAtT g, double N, double kt, Cfg cfg)
        {
            var r = new FatigueResult();
            if (g.IsAluminium) r.Sa = SaAluminium(g.Row, g.Su20, g.Su, N, cfg.D("fatigue.kaAluminium", 0.9));
            else r.Sa = SaSteel(g.Su20, g.Su, N, cfg.D("fatigue.kbSteel", 0.8), cfg.D("fatigue.keSteel", 0.868));
            r.SigmaMaxR0 = Math.Min(2 * r.Sa * g.Su / (g.Su + r.Sa), g.Sy);
            var q = g.IsAluminium ? cfg.D("fatigue.qAluminium", 0.8) : cfg.D("fatigue.qSteel", 0.95);
            r.Kf = 1 + q * (kt - 1);
            r.AllowFatigue = r.SigmaMaxR0 / (r.Kf * cfg.D("sfFatigue", 1.5));
            r.AllowStatic = g.Sy / cfg.D("sfStatic", 2.0);
            return r;
        }

        /// <summary>Allowables with K_f given directly (E3 uses K_f = 1.5).</summary>
        public static FatigueResult AllowablesKf(GradeAtT g, double N, double kf, Cfg cfg)
        {
            var r = Allowables(g, N, 1.0, cfg);
            r.Kf = kf;
            r.AllowFatigue = r.SigmaMaxR0 / (kf * cfg.D("sfFatigue", 1.5));
            return r;
        }
    }
}

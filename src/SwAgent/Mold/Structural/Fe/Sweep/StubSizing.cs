using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>
    /// Stand-in for A1's Sizing until integration: the fast path of design 3.7-3.8 (S20-S31) on the coefficient table. Two
    /// presets: Recommended (the corrections this track's sweeps found) and DesignAsWritten (design.md 3.7-3.8 literally), so
    /// the Monte Carlo can show what each costs or risks. Materials are a small built-in set (E and allowables at temperature
    /// per N/check/method_check.py and mold-materials.json), not MoldData.
    /// DELETE OR REPLACE AT INTEGRATION with an adapter to SwAgent.Mold sizing (MonteCarloHarness documents the JSON).
    /// </summary>
    public static class StubSizing
    {
        public sealed class Options
        {
            public string Name;
            /// <summary>Wall-root factor as a function of w/h.</summary>
            public Func<double, double> RootFactor = wh => 1 + 2.6 * wh;
            /// <summary>Multiplier on alpha and beta from the table, covering interpolation between rows (the sweep's check: under 1%).</summary>
            public double InterpolationAllowance = 1.01;
            /// <summary>Case E (pillars): "layout" sizes by the plate FE of the actual layout; "pillar" uses the Pillar rows; "c4" the C4 rows (design.md).</summary>
            public string PillarMethod = "layout";
            /// <summary>Round the loaded fraction up to the next table value instead of interpolating.</summary>
            public bool RoundUpLoadFraction;
        }

        public static readonly Options Recommended = new Options
        {
            Name = "StubSizing, recommended (table + fixed point; root factor 1 + 2.6 w/h; +1% interpolation allowance; case E by the layout FE)",
        };

        public static readonly Options DesignAsWritten = new Options
        {
            Name = "StubSizing, design.md as written (root factor 1.2; no interpolation allowance; case E as C4 panels between pillars)",
            RootFactor = _ => 1.2, InterpolationAllowance = 1.0, PillarMethod = "c4",
        };

        public static string Name => Recommended.Name;
        public static Func<double, double> RootFactor { get => Recommended.RootFactor; set => Recommended.RootFactor = value; }

        sealed class Grade { public double E, Nu, Sy, Su; public double[] RE, RY, RU; public bool Aluminium; }

        static readonly double[] Temps = { 20, 100, 150, 200, 250 };
        static Grade G(string id)
        {
            switch (id)
            {
                case "al_6061_t651": return new Grade { E = 68900, Nu = 0.33, Sy = 276, Su = 310, RE = new[] { 0.97, 0.93, 0.86, 0.78 }, RY = new[] { 0.95, 0.78, 0.40, 0.15 }, RU = new[] { 0.94, 0.75, 0.42, 0.19 }, Aluminium = true };
                case "al_7075_t651": return new Grade { E = 71700, Nu = 0.33, Sy = 503, Su = 572, RE = new[] { 0.96, 0.92, 0.85, 0.78 }, RY = new[] { 0.85, 0.62, 0.30, 0.12 }, RU = new[] { 0.85, 0.62, 0.32, 0.15 }, Aluminium = true };
                case "st_p20": return new Grade { E = 205000, Nu = 0.29, Sy = 900, Su = 1020, RE = new[] { 0.99, 0.98, 0.975, 0.965 }, RY = new[] { 0.95, 0.92, 0.89, 0.87 }, RU = new[] { 0.96, 0.93, 0.91, 0.89 } };
                case "st_h13": return new Grade { E = 210000, Nu = 0.29, Sy = 1300, Su = 1550, RE = new[] { 0.99, 0.98, 0.975, 0.965 }, RY = new[] { 0.95, 0.92, 0.89, 0.87 }, RU = new[] { 0.96, 0.93, 0.91, 0.89 } };
                default: throw new ArgumentException("Unknown grade " + id);
            }
        }

        static double F(double[] r, double t)
        {
            if (t <= 20) return 1;
            var ys = new[] { 1.0 }.Concat(r).ToArray();
            for (int i = 0; i + 1 < Temps.Length; i++)
                if (t <= Temps[i + 1]) return ys[i] + (ys[i + 1] - ys[i]) * (t - Temps[i]) / (Temps[i + 1] - Temps[i]);
            return ys[ys.Length - 1];
        }

        /// <summary>Allowable stress min(S_y,T / 2, fatigue at N with R = 0 / 1.5) per S15-S19, simplified (K_f is applied by the caller).</summary>
        static (double e, double nu, double allow) At(string id, double tempC, double cycles)
        {
            var g = G(id);
            double e = g.E * F(g.RE, tempC), sy = g.Sy * F(g.RY, tempC), su = g.Su * F(g.RU, tempC);
            double sa;
            if (g.Aluminium)
            {
                // 6061-like S-N: 0.9 Su at 1e3 to about 0.46 Su at 1e5, 0.36 Su at 1e6 (scaled to the grade), machined surface 0.9
                double s3 = 0.9 * g.Su, s5 = 0.458 * g.Su, s6 = 0.358 * g.Su;
                double sn = cycles <= 1e5 ? s3 * Math.Pow(s5 / s3, Math.Log10(cycles / 1e3) / 2) : s5 * Math.Pow(s6 / s5, Math.Min(1, Math.Log10(cycles / 1e5)));
                sa = 0.9 * sn * su / g.Su;
            }
            else
            {
                double ka = 4.51 * Math.Pow(g.Su, -0.265), se = 0.5 * Math.Min(g.Su, 1400) * ka * 0.80 * 0.868;
                double f = g.Su < 500 ? 0.9 : g.Su < 1100 ? 0.86 : 0.80;
                sa = (cycles >= 1e6 ? se : f * g.Su * Math.Pow(se / (f * g.Su), Math.Log10(cycles / 1e3) / 3)) * su / g.Su;
            }
            double smax = Math.Min(2 * sa * su / (su + sa), sy);
            return (e, g.Nu, Math.Min(sy / 2, smax / 1.5));
        }

        public static Func<JObject, JObject> Create(CoefficientTable table) => d => Size(table, d, Recommended);
        public static Func<JObject, JObject> Create(CoefficientTable table, Options o) => d => Size(table, d, o);

        public static JObject Size(CoefficientTable table, JObject d) => Size(table, d, Recommended);

        public static JObject Size(CoefficientTable table, JObject d, Options o)
        {
            string support = (string)d["support"];
            double p = (double)d["pressure_MPa"], delta = (double)d["delta_allow_mm"], kf = (double)d["kf"], land = (double?)d["land_mm"] ?? 3.175;
            var mat = d["material"];
            var (e, nu, allow) = At((string)mat["id"], (double)mat["tempC"], (double)d["cycles"]);
            bool al = ((string)mat["id"]).StartsWith("al_");
            double pl = (double)d["plate"]["length_mm"], pw = (double)d["plate"]["width_mm"];
            var cavs = d["cavities"].Cast<JObject>().ToList();
            double ex1 = cavs.Min(c => (double)c["x_mm"]) - land, ey1 = cavs.Min(c => (double)c["y_mm"]) - land;
            double ex2 = cavs.Max(c => (double)c["x_mm"] + (double)c["length_mm"]) + land, ey2 = cavs.Max(c => (double)c["y_mm"] + (double)c["width_mm"]) + land;
            double loaded = cavs.Sum(c => ((double)c["length_mm"] + 2 * land) * ((double)c["width_mm"] + 2 * land));
            double k = o.InterpolationAllowance;
            (double, double) Tab(string bc0, double asp, double tau, double lf) { var v = table.Get(bc0, asp, tau, nu, lf); return (v.Alpha * k, v.Beta * k); }

            // ---- plate (S26-S31) ----
            double tMin = Math.Max(al ? 12 : 10, 0.05 * Math.Max(pl, pw));
            var plate = new JObject();
            string bc = null; double a = pl, b = pw, frac = 1;
            switch (support)
            {
                case "B":
                    double y1 = (double)d["rails"]["y1_mm"], y2 = (double)d["rails"]["y2_mm"];
                    bc = "Rails"; b = y2 - y1; a = pl;
                    frac = LoadFrac((ex2 - ex1) * (Math.Min(ey2, y2) - Math.Max(ey1, y1)) / (a * b), o);
                    break;
                case "C":
                    var op = d["opening"];
                    double ol = (double)op["x2_mm"] - (double)op["x1_mm"], ow = (double)op["y2_mm"] - (double)op["y1_mm"];
                    bc = "SS4"; a = Math.Max(ol, ow); b = Math.Min(ol, ow);
                    frac = LoadFrac(loaded / (ol * ow), o);
                    break;
                case "E":
                    var xs = new[] { 0.0 }.Concat(d["pillars"]["xs_mm"].Select(v => (double)v)).Concat(new[] { pl }).ToList();
                    var ys = new[] { 0.0 }.Concat(d["pillars"]["ys_mm"].Select(v => (double)v)).Concat(new[] { pw }).ToList();
                    double sx = Enumerable.Range(0, xs.Count - 1).Max(q => xs[q + 1] - xs[q]), sy = Enumerable.Range(0, ys.Count - 1).Max(q => ys[q + 1] - ys[q]);
                    a = Math.Max(sx, sy); b = Math.Min(sx, sy);
                    bc = o.PillarMethod == "c4" ? "C4" : "Pillar";
                    break;
            }
            if (bc == null)
            {
                plate["t_mm"] = Up(tMin); plate["governs"] = "minimum"; plate["note"] = "case A: platen-backed, minimum rules";
            }
            else if (support == "E" && o.PillarMethod == "layout")
            {
                var (t, g) = LayoutFe(d, e, nu, allow, kf, delta, b);
                if (t / b > 0.5) g = "add support"; else if (tMin > t) g = "minimum";
                plate["t_mm"] = Up(Math.Max(t, tMin)); plate["governs"] = g; plate["bc"] = "layout FE"; plate["span_mm"] = Math.Round(b, 3);
            }
            else
            {
                Func<double, (double, double)> coef = tau => Tab(bc, a / b, tau, frac);
                double tD = FixedPoint(coef, p, b, e, delta, 1);
                double tS = Math.Sqrt(coef(tD / b).Item2 * p * b * b * kf / allow);
                double t = Math.Max(tD, tS);
                if (tS > tD) t = Math.Sqrt(coef(t / b).Item2 * p * b * b * kf / allow); // beta changes slowly with tau: one more pass
                string g = t / b > 0.5 ? "add support" : tMin > t ? "minimum" : tS > tD ? "stress" : "deflection";
                plate["t_mm"] = Up(Math.Max(t, tMin)); plate["governs"] = g; plate["bc"] = bc; plate["span_mm"] = Math.Round(b, 3); plate["loadFrac"] = frac;
            }

            // ---- walls (S20-S25): long and short side of each cavity ----
            var walls = new JArray();
            for (int i = 0; i < cavs.Count; i++)
            {
                var c = cavs[i];
                double h = (double)c["depth_mm"];
                foreach (var side in new[] { "length", "width" })
                {
                    double len = (double)c[side == "length" ? "length_mm" : "width_mm"], perp = (double)c[side == "length" ? "width_mm" : "length_mm"];
                    Func<double, (double, double)> coef = tau => Tab("Wall-C", len / h, tau, 1);
                    double wD = WallFixedPoint(coef, p, h, e, delta, o.RootFactor);
                    // stress: beta p h^2 / w^2 + p perp / (2 w) <= allow / kf, solved for w by fixed point
                    double wS = wD;
                    for (int q = 0; q < 60; q++)
                    {
                        double beta = coef(Math.Min(wS / h, 0.5)).Item2;
                        double need = (beta * p * h * h / (wS * wS) + p * perp / (2 * wS)) * kf;
                        double next = wS * Math.Sqrt(need / allow);
                        if (Math.Abs(next - wS) < 1e-7 * wS) { wS = next; break; }
                        wS = next;
                    }
                    double wMin = al ? 15 : 10;
                    double w = Math.Max(wD, Math.Max(wS, wMin));
                    string g = w == wMin && wMin > Math.Max(wD, wS) ? "minimum" : wS > wD ? "stress" : "deflection";
                    // S24: past w/h = 0.5 plate theory no longer applies (steel, a keyed wall or a 3D check instead)
                    if (w / h > 0.5) g = "out of range";
                    walls.Add(new JObject { ["cavity"] = i, ["side"] = side, ["w_mm"] = Up(w), ["governs"] = g });
                }
            }

            return new JObject
            {
                ["E_MPa"] = Math.Round(e, 1), ["nu"] = nu, ["sigma_allow_MPa"] = Math.Round(allow, 2), ["kf"] = kf,
                ["plate"] = plate, ["walls"] = walls, ["method"] = o.Name,
            };
        }

        /// <summary>
        /// Case E by the plate FE of the actual layout: SS edges, rigid pillar heads 0.2 x the short spacing, cavities plus land as
        /// patches; t by fixed point on the largest land deflection, then stress outside the heads.
        /// </summary>
        static (double t, string governs) LayoutFe(JObject d, double e, double nu, double allow, double kf, double delta, double span)
        {
            var check = new JObject { ["E_MPa"] = e, ["nu"] = nu, ["sigma_allow_MPa"] = allow, ["walls"] = new JArray() };
            MonteCarloHarness.Check Eval(double tt)
            {
                check["plate"] = new JObject { ["t_mm"] = tt, ["governs"] = "deflection" };
                return MonteCarloHarness.CheckDesign(0, d, check, 24).First(x => x.Element == "plate");
            }
            // bracket the thickness where the largest land deflection meets delta, then bisect (geometric) and keep the safe end
            double lo = 0.2 * span, hi = lo;
            var c = Eval(lo);
            if (c.Rw > 1) { do { lo = hi; hi *= 1.3; c = Eval(hi); } while (c.Rw > 1 && hi < 5 * span); }
            else { var keep = c; do { hi = lo; keep = c; lo /= 1.3; c = Eval(lo); } while (c.Rw <= 1 && lo > 1e-3 * span); c = keep; }
            var atHi = c;
            for (int it = 0; it < 30 && atHi.Rw <= 0.99 && hi / lo > 1.0005; it++)
            {
                double mid = Math.Sqrt(lo * hi);
                var cm = Eval(mid);
                if (cm.Rw > 1) lo = mid; else { hi = mid; atHi = cm; }
            }
            double t = hi;
            c = atHi;
            if (c.Rs <= 1) return (t, "deflection");
            // stress governs: thicken until the FE stress fits (moments shift a little with t/b, so re-check)
            for (int it = 0; it < 10 && c.Rs > 1; it++)
            {
                t *= Math.Sqrt(c.Rs) * 1.002;
                check["plate"] = new JObject { ["t_mm"] = t, ["governs"] = "stress" };
                c = MonteCarloHarness.CheckDesign(0, d, check, 24).First(x => x.Element == "plate");
            }
            return (t, "stress");
        }

        /// <summary>Thicknesses go out in micrometres, rounded up (rounding down could put a just-sized part a hair over its limit).</summary>
        static double Up(double v) => Math.Ceiling(v * 1000 - 1e-9) / 1000;

        static double LoadFrac(double f, Options o)
        {
            // the loaded fraction as a centred patch (the worst place for it)
            f = Math.Max(CoefficientSweep.LoadFracs[0], Math.Min(1, f));
            if (!o.RoundUpLoadFraction) return f;
            foreach (var v in CoefficientSweep.LoadFracs) if (f <= v + 1e-9) return v;
            return 1;
        }

        static double FixedPoint(Func<double, (double, double)> coef, double p, double b, double e, double delta, double factor)
        {
            double t = 0.2 * b;
            for (int q = 0; q < 200; q++)
            {
                double tn = Math.Pow(factor * coef(t / b).Item1 * p * Math.Pow(b, 4) / (e * delta), 1.0 / 3);
                if (Math.Abs(tn - t) < 1e-7 * t) return tn;
                t = tn;
            }
            return t;
        }

        static double WallFixedPoint(Func<double, (double, double)> coef, double p, double h, double e, double delta, Func<double, double> root)
        {
            double w = 0.3 * h;
            for (int q = 0; q < 200; q++)
            {
                double wn = Math.Pow(root(w / h) * coef(w / h).Item1 * p * Math.Pow(h, 4) / (e * delta), 1.0 / 3);
                if (Math.Abs(wn - w) < 1e-7 * w) return wn;
                w = wn;
            }
            return w;
        }
    }
}

using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>
    /// Stand-in for A1's Sizing until integration: S20-S31 on the coefficient table, optionally finished by the plate FE of the
    /// actual layout. Three presets, so the Monte Carlo can show what each costs or risks:
    ///   Recommended     walls by the table; plates on rails (B), over an opening (C) and on pillars (E) by the table as a
    ///                   first guess, then the layout FE (two meshes, 24 and 48 elements on the short side, extrapolated);
    ///   TableOnly       the table alone for every case (Pillar rows times the 3D pillar-head factor for E);
    ///   DesignAsWritten design.md 3.7-3.8 literally (root factor 1.2, no interpolation allowance, pillar panels as C4).
    /// Materials are a small built-in set (E and allowables at temperature per N/check/method_check.py and mold-materials.json),
    /// not MoldData. DELETE OR REPLACE AT INTEGRATION with an adapter to SwAgent.Mold sizing (MonteCarloHarness documents the JSON).
    /// </summary>
    public static class StubSizing
    {
        public sealed class Options
        {
            public string Name;
            /// <summary>Wall-root factor as a function of w/h.</summary>
            public Func<double, double> RootFactor = wh => 1 + Solid3dValidation.DefaultWallRootSlope * wh;
            /// <summary>Multiplier on alpha and beta from the table, covering interpolation between rows (the CSV's "# allowance:").</summary>
            public double InterpolationAllowance = 1.01;
            /// <summary>3D pillar-head factor on Pillar alpha (solid3d-summary.json pillarCell).</summary>
            public double PillarFactor = Solid3dValidation.DefaultPillarHeadFactor;
            /// <summary>Case E from the table: "pillar" uses the Pillar rows, "c4" the C4 rows (design.md as written).</summary>
            public string PillarRows = "pillar";
            /// <summary>Supports whose plate the layout FE finishes after the table's first guess (e.g. "BCE"); empty = table only.</summary>
            public string LayoutFe = "";
            /// <summary>Round the loaded fraction up to the next table value instead of interpolating.</summary>
            public bool RoundUpLoadFraction;
        }

        public static readonly Options Recommended = new Options
        {
            Name = "StubSizing, recommended (walls: table + fixed point, root factor 1 + c w/h, +1% interpolation allowance; plates B, C, E: table first guess, finished by the layout plate FE at 24 and 48 elements, extrapolated)",
            LayoutFe = "BCE",
        };

        public static readonly Options TableOnly = new Options
        {
            Name = "StubSizing, table only (root factor 1 + c w/h; +1% interpolation allowance; centred-patch loaded fraction; case E by the Pillar rows x the 3D pillar-head factor)",
        };

        public static readonly Options DesignAsWritten = new Options
        {
            Name = "StubSizing, design.md as written (root factor 1.2; no interpolation allowance; case E as C4 panels between pillars)",
            RootFactor = _ => 1.2, InterpolationAllowance = 1.0, PillarRows = "c4", PillarFactor = 1.0,
        };

        public static string Name => Recommended.Name;

        /// <summary>Sets the 3D calibrations (wall-root slope c and pillar-head factor) on the presets that use them.</summary>
        public static void Calibrate(double wallRootSlope, double pillarHeadFactor)
        {
            foreach (var o in new[] { Recommended, TableOnly })
            {
                o.RootFactor = wh => 1 + wallRootSlope * wh;
                o.PillarFactor = pillarHeadFactor;
            }
        }

        /// <summary>The two meshes the layout FE extrapolates from (never the truth's).</summary>
        public static readonly MonteCarloHarness.MeshSpec CoarseMesh = new MonteCarloHarness.MeshSpec(24, 3), FineMesh = new MonteCarloHarness.MeshSpec(48, 6);

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
            string bc = null; double a = pl, b = pw, frac = 1, alphaFactor = 1;
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
                    // the Pillar cell: b the short spacing along y as the rows define it (heads 0.2 x the short spacing)
                    b = Math.Min(sx, sy); a = Math.Max(sx, sy);
                    bc = o.PillarRows == "c4" ? "C4" : "Pillar";
                    if (bc == "Pillar") alphaFactor = o.PillarFactor;
                    break;
            }
            if (bc == null)
            {
                plate["t_mm"] = Up(tMin); plate["governs"] = "minimum"; plate["note"] = "case A: platen-backed, minimum rules";
            }
            else
            {
                Func<double, (double, double)> coef = tau => { var c = Tab(bc, a / b, tau, frac); return (c.Item1 * alphaFactor, c.Item2); };
                double tD = FixedPoint(coef, p, b, e, delta, 1);
                double tS = Math.Sqrt(coef(tD / b).Item2 * p * b * b * kf / allow);
                double t = Math.Max(tD, tS);
                if (tS > tD) t = Math.Sqrt(coef(t / b).Item2 * p * b * b * kf / allow); // beta changes slowly with tau: one more pass
                string g = tS > tD ? "stress" : "deflection";
                string method = bc;
                if (o.LayoutFe.Contains(support) && t / b <= 0.5)
                {
                    // finish with the layout FE from the table's answer (it brackets the FE thickness within a few tens of percent)
                    (t, g) = LayoutFe(d, e, nu, allow, t);
                    method = "layout FE (from the " + bc + " rows' " + Math.Round(Math.Max(tD, tS), 2) + " mm)";
                }
                if (t / b > 0.5) g = "add support"; else if (tMin > t) g = "minimum";
                plate["t_mm"] = Up(Math.Max(t, tMin)); plate["governs"] = g; plate["bc"] = method; plate["span_mm"] = Math.Round(b, 3); plate["loadFrac"] = frac;
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
        /// The layout FE's r_w and r_s at thickness t: the plate solved on CoarseMesh and FineMesh and extrapolated to the converged
        /// value (Richardson, second order: x_inf = x_fine + (x_fine - x_coarse) / 3), never below the fine mesh's.
        /// </summary>
        public static (double Rw, double Rs) LayoutRatios(JObject d, double t, double e, double nu, double allow)
        {
            var c = MonteCarloHarness.PlateRatios(d, t, e, nu, allow, CoarseMesh);
            var f = MonteCarloHarness.PlateRatios(d, t, e, nu, allow, FineMesh);
            double X(double coarse, double fine) => Math.Max(fine, fine + (fine - coarse) / 3);
            return (X(c.Rw, f.Rw), X(c.Rs, f.Rs));
        }

        /// <summary>The layout FE sizes to this fraction of the limits: the extrapolation's own error (about 0.1%) stays inside.</summary>
        public const double LayoutTarget = 0.995;

        /// <summary>
        /// Plates B, C, E by the layout FE: the thinnest t whose extrapolated land deflection is within LayoutTarget x delta, then
        /// thickened until the extrapolated stress fits as well. Deflection goes as t^-3 and stress as t^-2 (both drift slowly with
        /// t/b), so a power-law step from the table's answer t0 lands within 0.2% in three or four solves; a bracket and bisection
        /// take over if it does not.
        /// </summary>
        static (double t, string governs) LayoutFe(JObject d, double e, double nu, double allow, double t0)
        {
            double t = t0;
            var c = LayoutRatios(d, t, e, nu, allow);
            double tOk = double.NaN, tBad = double.NaN;
            (double Rw, double Rs) atOk = default;
            for (int it = 0; it < 12; it++)
            {
                if (c.Rw <= LayoutTarget) { if (double.IsNaN(tOk) || t < tOk) { tOk = t; atOk = c; } }
                else if (double.IsNaN(tBad) || t > tBad) tBad = t;
                if (!double.IsNaN(tOk) && c.Rw <= LayoutTarget && c.Rw > LayoutTarget - 0.002) break;
                double next = t * Math.Pow(c.Rw / LayoutTarget, 1.0 / 3) * (c.Rw > LayoutTarget ? 1.001 : 1.0);
                if (!double.IsNaN(tOk) && !double.IsNaN(tBad) && (next >= tOk || next <= tBad)) next = Math.Sqrt(tOk * tBad); // keep inside the bracket
                if (Math.Abs(next / t - 1) < 2e-4) { if (c.Rw > LayoutTarget) next = t * 1.002; else break; }
                t = next;
                c = LayoutRatios(d, t, e, nu, allow);
            }
            if (double.IsNaN(tOk)) { t *= 1.01; c = LayoutRatios(d, t, e, nu, allow); while (c.Rw > LayoutTarget) { t *= 1.02; c = LayoutRatios(d, t, e, nu, allow); } tOk = t; atOk = c; }
            t = tOk; c = atOk;
            if (c.Rs <= LayoutTarget) return (t, "deflection");
            // stress governs: thicken until the extrapolated stress fits (moments shift a little with t/b, so re-check)
            for (int it = 0; it < 20 && c.Rs > LayoutTarget; it++)
            {
                t *= Math.Sqrt(c.Rs / LayoutTarget) * 1.001;
                c = LayoutRatios(d, t, e, nu, allow);
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

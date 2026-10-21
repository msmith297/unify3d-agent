using System;
using System.Collections.Generic;

namespace SwAgent.Mold
{
    public sealed class WallInput
    {
        public string Id;
        /// <summary>Cavity pressure (MPa), loaded height = cavity depth in this piece h, wall length L, cavity size at right
        /// angles b_perp (membrane term), mm.</summary>
        public double P, H, L, BPerp;
        public double E, Nu = 0.3;
        /// <summary>Allowed bulge (mm): the wall's width budget (critique A11).</summary>
        public double DeltaAllow;
        /// <summary>Sides clamped (pocket milled from solid, "Wall-C") or simply supported (bolted frame corners, "Wall-S").</summary>
        public bool SidesClamped = true;
        /// <summary>Wall-root factor on the plate deflection (S21): RootFactor + RootSlope x w/h. Track A2's 3D pocket models
        /// (solid3d-summary.json) give 1 + 2.8 w/h; the design's constant 1.2 holds only below w/h 0.07. RootSlope 0 gives a
        /// constant factor.</summary>
        public double RootFactor = 1.0, RootSlope = Walls.DefaultRootSlope, Lambda = 1.0;
        public double AllowStatic, AllowFatigue;
        /// <summary>Drawn width from geometry (lip + groove band + clearance + hardware) and the class B minimum, mm.</summary>
        public double WGeom, WMin;
        /// <summary>Class A abuse pressure F/A_proj (LC1x) and the yield at temperature for the SF 1.0 check, MPa.</summary>
        public double? PAbuse;
        public double SyT;
        /// <summary>Widest wall the sizing may return, as a multiple of h: above it there is no plate-theory answer (S21).</summary>
        public double WMaxOverH = Walls.MaxTau;
    }

    public sealed class WallResult
    {
        public string Id, Bc, Governs;
        public double WDefl, WStress, WStruct, WDrawn;
        public double Alpha, Beta, Sigma, SigmaAllow, Deflection;
        public double LOverH, TauStruct;
        public bool StockyFlag;
        /// <summary>No width up to WMaxOverH x h keeps the bulge within delta_wall: WDefl is that cap and WALL-NOSOLN fails.</summary>
        public bool NoSolution;
        /// <summary>Abuse stress at the drawn width (strip upper bound) and the abuse pressure that brings it to yield.</summary>
        public double? SigmaAbuse, PAbuseCap;
        public List<JCheck> Checks = new List<JCheck>();
    }

    /// <summary>
    /// S20-S25 cavity walls. A plate of width w (thickness of the wall), loaded height h, length L: bottom clamped, top free,
    /// sides clamped or simply supported. alpha_w, beta_w from the coefficient table in (L/h, (w/h)²), with the strip
    /// above the last L/h row (critique A4).
    /// (S21) w = [(1 + 2.8 w/h) alpha lambda p h⁴/(E delta)]^(1/3), fixed point from 0.3 h, bisection when it does not settle
    ///       (the root factor from Track A2's 3D pocket models, calibrated up to w/h 0.53; above that the root's added bulge
    ///       falls as h/w - see RootAt; the design's constant 1.2 is unsafe above w/h 0.07). No width up to 3 h: WALL-NOSOLN.
    /// (S22) sigma = beta lambda p h²/w² + p b_perp/(2w) (membrane from the perpendicular walls).
    /// (S23) if sigma exceeds min(static, fatigue) allowable, w grows until it does not.
    /// (S24) w/h > 0.5 is flagged; the class A abuse case LC1x uses beta p h²/w² + p b/(2w) (the strip upper bound 3 p h²/w²
    ///       + p b/(2w) only for a stocky wall) against S_y,T at SF 1.0 on the drawn wall (it sets the maximum gauge).
    /// (S25) drawn w = max(w_struct, w_geom, class B minimum).
    /// </summary>
    public static class Walls
    {
        /// <summary>The 3D wall-root slope c in 1 + c w/h (Track A2, coefficients.csv "# wall root:").</summary>
        public const double DefaultRootSlope = 2.8;

        /// <summary>
        /// The w/h up to which the 3D pocket models calibrated the root factor 1 + 2.8 w/h (Track A2: up to 0.53). Above it the
        /// factor's added compliance (the root's own rotation) is not multiplied by the plate alpha any more - alpha is
        /// extended in tau^2 there, and the product gave a bulge floor of about 4.4 p h/E that no width could beat (the
        /// review's stocky-wall finding). It is an additive term fixed at its tau_c value and falling as tau_c/tau: the 3D
        /// pockets fall about as (h/w)^1.3 (L/h 2-10, w/h 0.53-2, MoldStockyWallTests), so this stays on the safe side.
        /// </summary>
        public const double RootCalibratedTau = 0.53;

        /// <summary>Widest wall the S21 sizing returns, as a multiple of h (beyond it: WALL-NOSOLN, never the last iterate).</summary>
        public const double MaxTau = 3.0;

        /// <summary>The wall-root factor at width w (S21), as the multiplier on the plate bulge (see RootAt).</summary>
        public static double Root(WallInput i, double w) => RootAt(i.RootFactor, i.RootSlope, w / i.H, i.L / i.H, i.Nu, i.SidesClamped ? "Wall-C" : "Wall-S");

        /// <summary>
        /// The root factor as a multiplier on alpha/tau^3 at tau = w/h: base + slope tau up to tau_c = 0.53; above it the root's
        /// added bulge slope tau_c alpha(tau_c)/tau_c^3 falls as tau_c/tau, i.e. base + slope alpha(tau_c) tau^2 / (tau_c alpha(tau)).
        /// </summary>
        public static double RootAt(double rootBase, double rootSlope, double tau, double lOverH, double nu, string bc = "Wall-C")
        {
            if (tau <= RootCalibratedTau || rootSlope == 0) return rootBase + rootSlope * tau;
            var ac = Coefficients.Get(bc, lOverH, RootCalibratedTau, nu, 1.0).Alpha;
            var a = Coefficients.Get(bc, lOverH, tau, nu, 1.0).Alpha;
            return rootBase + rootSlope * ac * tau * tau / (RootCalibratedTau * a);
        }

        /// <summary>The factor as a formula for check methods: "(1 + 2.8 w/h)" or "1.2".</summary>
        public static string RootText(WallInput i) => i.RootSlope == 0 ? Si.F(i.RootFactor) : "(" + Si.F(i.RootFactor) + " + " + Si.F(i.RootSlope) + " w/h; above w/h " + Si.F(RootCalibratedTau) + " an added bulge falling as h/w)";

        /// <summary>The S21 bulge at width w (mm).</summary>
        public static double Deflection(WallInput i, string bc, double w)
        {
            var a = Coefficients.Get(bc, i.L / i.H, w / i.H, i.Nu, 1.0).Alpha;
            return Root(i, w) * a * i.Lambda * i.P * Math.Pow(i.H, 4) / (i.E * Math.Pow(w, 3));
        }

        public static WallResult Size(WallInput i, double tauMax = 0.5)
        {
            var r = new WallResult { Id = i.Id, Bc = i.SidesClamped ? "Wall-C" : "Wall-S", LOverH = i.L / i.H };
            // (S21) deflection: fixed point from 0.3 h; when it does not settle inside the sane range, bisect the bulge
            // (it falls with w) on [0.01 h, w_max] - and when even w_max bulges too much there is no answer (never the
            // last iterate)
            var wMax = i.WMaxOverH * i.H;
            var w = 0.3 * i.H;
            var converged = false;
            for (var k = 0; k < 500 && w <= wMax; k++)
            {
                var a = Coefficients.Get(r.Bc, r.LOverH, w / i.H, i.Nu, 1.0).Alpha;
                var wn = Math.Pow(Root(i, w) * a * i.Lambda * i.P * Math.Pow(i.H, 4) / (i.E * i.DeltaAllow), 1.0 / 3.0);
                if (Math.Abs(wn - w) / w < 1e-10) { w = wn; converged = true; break; }
                w = wn;
            }
            if (!converged || w > wMax)
            {
                if (Deflection(i, r.Bc, wMax) > i.DeltaAllow * (1 + 1e-9))
                {
                    r.NoSolution = true;
                    w = wMax;
                }
                else
                {
                    double lo = 0.01 * i.H, hi = wMax;
                    for (var k = 0; k < 200 && (hi - lo) > 1e-12 * hi; k++)
                    {
                        var m = 0.5 * (lo + hi);
                        if (Deflection(i, r.Bc, m) > i.DeltaAllow) lo = m; else hi = m;
                    }
                    w = hi;
                }
            }
            r.WDefl = w;

            // (S22/S23) stress: grow w until sigma <= the allowable (the membrane term does not scale with 1/w², so iterate)
            var allow = Math.Min(i.AllowStatic, i.AllowFatigue);
            r.SigmaAllow = allow;
            double Sigma(double ww) => Coefficients.Get(r.Bc, r.LOverH, ww / i.H, i.Nu, 1.0).Beta * i.Lambda * i.P * i.H * i.H / (ww * ww) + i.P * i.BPerp / (2 * ww);
            var ws = w;
            for (var k = 0; k < 200 && Sigma(ws) > allow * (1 + 1e-12); k++)
            {
                var wn = ws * Math.Sqrt(Sigma(ws) / allow);
                if (Math.Abs(wn - ws) / ws < 1e-12) break;
                ws = wn;
            }
            r.WStress = ws;
            r.WStruct = Math.Max(r.WDefl, r.WStress);
            r.TauStruct = r.WStruct / i.H;
            r.StockyFlag = r.TauStruct > tauMax;

            // (S25) the wall that is drawn
            r.WDrawn = Math.Max(r.WStruct, Math.Max(i.WGeom, i.WMin));
            r.Governs = r.WDrawn == r.WStruct ? (r.WStress > r.WDefl ? "stress" : "deflection") : (i.WGeom >= i.WMin ? "flash system and hardware" : "class B minimum");

            var co = Coefficients.Get(r.Bc, r.LOverH, r.WDrawn / i.H, i.Nu, 1.0);
            r.Alpha = co.Alpha;
            r.Beta = co.Beta;
            r.Sigma = Sigma(r.WDrawn);
            r.Deflection = Root(i, r.WDrawn) * co.Alpha * i.Lambda * i.P * Math.Pow(i.H, 4) / (i.E * Math.Pow(r.WDrawn, 3));
            r.Checks.Add(new JCheck { Id = "WALL-DEFL-" + i.Id, Type = "deflection", Demand = r.Deflection, Capacity = i.DeltaAllow, Unit = "mm", LoadCase = "LC1", Method = "S21: " + RootText(i) + " alpha p h^4/(E w^3) <= delta_wall (A11)" });
            r.Checks.Add(new JCheck { Id = "WALL-STRESS-" + i.Id, Type = i.AllowFatigue < i.AllowStatic ? "fatigue" : "stress", Demand = r.Sigma, Capacity = allow, Unit = "MPa", LoadCase = "LC1", Method = "S22-S23: beta p h^2/w^2 + p b/(2w) <= min(Sy/2, fatigue)" });
            if (r.NoSolution)
                r.Checks.Add(new JCheck { Id = "WALL-NOSOLN-" + i.Id, Type = "deflection", Demand = Deflection(i, r.Bc, wMax), Capacity = i.DeltaAllow, Unit = "mm", LoadCase = "LC1", Method = "S21: no wall up to " + Si.F(i.WMaxOverH) + " h keeps the bulge within delta_wall - use steel, a wall keyed into the base, a lower gauge (pressure) or a looser width tolerance" });
            if (r.StockyFlag)
                r.Checks.Add(new JCheck { Id = "WALL-STOCKY-" + i.Id, Type = "stress", Demand = r.TauStruct, Capacity = tauMax, Unit = "w/h", Status = "warn", Method = "S24: w/h > 0.5 - plate theory no longer applies; prefer steel, a wall keyed into the base, or a 3D check" });

            if (i.PAbuse != null && i.PAbuse.Value > 0)
            {
                // (S24) abuse against yield at SF 1.0: the beta formula for a plate-like wall; the strip upper bound only for
                // a stocky wall (w/h > tauMax), where plate theory no longer holds
                var f1 = AbuseFactor(r.WDrawn / i.H > tauMax, co.Beta, i.H, i.BPerp, r.WDrawn);
                r.SigmaAbuse = i.PAbuse.Value * f1;
                r.PAbuseCap = i.SyT / f1;
                r.Checks.Add(new JCheck { Id = "WALL-ABUSE-" + i.Id, Type = "stress", Demand = r.SigmaAbuse.Value, Capacity = i.SyT, Unit = "MPa", LoadCase = "LC1x", Method = AbuseMethod(r.WDrawn / i.H > tauMax) });
            }
            return r;
        }

        /// <summary>LC1x wall stress per MPa of cavity pressure: beta h^2/w^2 + b/(2w), or the strip 3 h^2/w^2 + b/(2w) when stocky (S24).</summary>
        public static double AbuseFactor(bool stocky, double beta, double h, double bPerp, double w) => (stocky ? 3.0 : beta) * h * h / (w * w) + bPerp / (2 * w);

        public static string AbuseMethod(bool stocky) => stocky
            ? "S24/LC1x: stocky wall (w/h > 0.5), strip 3 p h^2/w^2 + p b/(2w) <= Sy,T (SF 1.0) at F/A_proj"
            : "S24/LC1x: beta p h^2/w^2 + p b/(2w) <= Sy,T (SF 1.0) at F/A_proj";
    }
}

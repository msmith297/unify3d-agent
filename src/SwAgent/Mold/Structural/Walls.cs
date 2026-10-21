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
        /// <summary>Floor under the cavity (mm) for the floor-stretch term (Walls.Stretch); 0 = the S26 minimum, 0.25 h.</summary>
        public double Floor;
    }

    public sealed class WallResult
    {
        public string Id, Bc, Governs;
        public double WDefl, WStress, WStruct, WDrawn;
        public double Alpha, Beta, Sigma, SigmaAllow, Deflection;
        /// <summary>The floor-stretch part of the bulge (mm), the same at every width (Walls.Stretch).</summary>
        public double Stretch;
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
    /// (S21) bulge = 1.06 (1 + 2.8 w/h) alpha lambda p h⁴/(E w³) + kappa(L/h) lambda p h b_perp/(2 E t_floor), solved for w by a
    ///       fixed point from 0.3 h, bisection when it does not settle (the root factor from Track A2's 3D pocket models,
    ///       calibrated up to w/h 0.53; above that the root's added bulge falls as h/w - see RootAt; the design's constant 1.2
    ///       is unsafe above w/h 0.07). The 1.06 and the floor-stretch term come from the 3D pocket over the whole range (see
    ///       OwnBulgeFactor). No width up to 3 h, or a floor whose stretch alone uses the budget: WALL-NOSOLN.
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

        /// <summary>
        /// Walls are sized this fraction inside delta_wall and the stress allowable (about 0.03% of the width). The record
        /// stores the layout rounded (cavity origins to 0.001 mm), and a wall sized exactly to its limit then read back up to
        /// that much narrower and failed its own check - at the printed maximum gauge, which is exactly the design force.
        /// </summary>
        public const double SizingHeadroom = 1e-3;

        /// <summary>
        /// The 3D recalibration (2026-10-21) on top of the root factor. The 2,000-mold Monte Carlo judged stocky walls against
        /// the 3D pocket and found them bulging up to 7% past delta_wall; a sweep of the pocket (1,512 cases at h/8 and h/12,
        /// extrapolated: L/h 0.5-12, cavity width 0.5-8 h, floor 0.25-1 h, w/h 0.3-3, nu 0.29 and 0.33, plus fine-mesh
        /// checks at h/12-h/24) showed two effects the plate model misses:
        /// - the floor between the walls stretches under the outward push, so a long wall slides out as a whole (up to 2.5x
        ///   the plate bulge for a stocky wall on a 0.25 h floor across a 4-8 h cavity) - widening the wall does not help;
        /// - long walls near w/h 0.3-0.5 bulge a few percent more than 1 + 2.8 w/h gives.
        /// The fit: own bulge x 1.06 plus kappa(L/h) lambda p h b_perp / (2 E t_floor), kappa = min(1, 0.71 ((L/h - 4)/8)^0.7)
        /// (0 up to L/h 4, where the end walls hold the long walls; 0.71 at 12; capped at the pure membrane stretch, 1). It covers
        /// every case with 2% to spare (the largest 3D / calculator ratio is 0.976). Applied with the 3D root factor only
        /// (RootSlope > 0); the design-as-written constant factor stays as written.
        /// Short walls of narrow pockets are not clamped at their sides at all (SidesClampedFor): there the 3D pocket bulged up
        /// to 2x the clamped-sides plate (L/h 0.5, cavity 0.25 h across, w/h 0.1; fine meshes h/12-h/24, 450 cases) and at most
        /// 0.81x the simply supported one.
        /// </summary>
        public const double OwnBulgeFactor = 1.06;

        /// <summary>
        /// Are the wall's sides clamped (Wall-C) or only simply supported (Wall-S)? A short wall (L/h up to 3) whose cavity is no
        /// wider across than the wall is long gets Wall-S: the short walls at its ends are pushed outward too, the corners
        /// open, and the 3D pocket bulged up to 2x the clamped plate there (2026-10-21, see OwnBulgeFactor); with Wall-S it
        /// stays at or below 0.81x. Longer walls (clamped is within 0.99x of 3D from L/h 3 and 0.97x from 3.33, fine meshes) and walls across a wider cavity
        /// keep Wall-C.
        /// </summary>
        public static bool SidesClampedFor(double l, double bPerp, double h) => !(l <= 3 * h && bPerp <= l);

        /// <summary>The floor-stretch coefficient kappa(L/h) (see OwnBulgeFactor).</summary>
        public static double StretchKappa(double lOverH) => Math.Min(1.0, 0.71 * Math.Pow(Math.Max(0.0, (lOverH - 4) / 8), 0.7));

        /// <summary>The floor-stretch part of the bulge (mm): kappa(L/h) lambda p h b_perp / (2 E t_floor); 0 without the 3D root factor.</summary>
        public static double Stretch(WallInput i)
        {
            if (i.RootSlope == 0 || i.BPerp <= 0) return 0;
            var floor = i.Floor > 0 ? i.Floor : 0.25 * i.H;
            return StretchKappa(i.L / i.H) * i.Lambda * i.P * i.H * i.BPerp / (2 * i.E * floor);
        }

        /// <summary>The factor on the wall's own (plate) bulge: OwnBulgeFactor with the 3D root factor, else 1.</summary>
        public static double OwnFactor(WallInput i) => i.RootSlope == 0 ? 1.0 : OwnBulgeFactor;

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

        /// <summary>The factor as a formula for check methods: "1.06 (1 + 2.8 w/h ...)" or "1.2".</summary>
        public static string RootText(WallInput i) => i.RootSlope == 0 ? Si.F(i.RootFactor) : Si.F(OwnBulgeFactor) + " (" + Si.F(i.RootFactor) + " + " + Si.F(i.RootSlope) + " w/h; above w/h " + Si.F(RootCalibratedTau) + " an added bulge falling as h/w)";

        /// <summary>The floor-stretch term as text for check methods (empty without it).</summary>
        public static string StretchText(WallInput i) => Stretch(i) > 0 ? " + kappa(L/h) p h b/(2 E t_floor) (floor stretch, kappa " + Si.F(StretchKappa(i.L / i.H)) + ")" : "";

        /// <summary>The S21 bulge at width w (mm): the wall's own bulge plus the floor stretch.</summary>
        public static double Deflection(WallInput i, string bc, double w)
        {
            var a = Coefficients.Get(bc, i.L / i.H, w / i.H, i.Nu, 1.0).Alpha;
            return OwnFactor(i) * Root(i, w) * a * i.Lambda * i.P * Math.Pow(i.H, 4) / (i.E * Math.Pow(w, 3)) + Stretch(i);
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
            // sized SizingHeadroom inside the limits, so a record that stores the layout rounded never reads back a wall a hair
            // too narrow for its own check
            var budget = i.DeltaAllow * (1 - SizingHeadroom);
            // the floor's stretch takes its share of the budget first, whatever the width; the wall's own bulge gets the rest
            r.Stretch = Stretch(i);
            var own = budget - r.Stretch;
            for (var k = 0; k < 500 && w <= wMax && own > 0; k++)
            {
                var a = Coefficients.Get(r.Bc, r.LOverH, w / i.H, i.Nu, 1.0).Alpha;
                var wn = Math.Pow(OwnFactor(i) * Root(i, w) * a * i.Lambda * i.P * Math.Pow(i.H, 4) / (i.E * own), 1.0 / 3.0);
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
                        if (Deflection(i, r.Bc, m) > budget) lo = m; else hi = m;
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
            var allowSized = allow * (1 - SizingHeadroom);
            for (var k = 0; k < 200 && Sigma(ws) > allowSized * (1 + 1e-12); k++)
            {
                var wn = ws * Math.Sqrt(Sigma(ws) / allowSized);
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
            r.Deflection = Deflection(i, r.Bc, r.WDrawn);
            r.Checks.Add(new JCheck { Id = "WALL-DEFL-" + i.Id, Type = "deflection", Demand = r.Deflection, Capacity = i.DeltaAllow, Unit = "mm", LoadCase = "LC1", Method = "S21: " + RootText(i) + " alpha p h^4/(E w^3)" + StretchText(i) + " <= delta_wall (A11)" });
            r.Checks.Add(new JCheck { Id = "WALL-STRESS-" + i.Id, Type = i.AllowFatigue < i.AllowStatic ? "fatigue" : "stress", Demand = r.Sigma, Capacity = allow, Unit = "MPa", LoadCase = "LC1", Method = "S22-S23: beta p h^2/w^2 + p b/(2w) <= min(Sy/2, fatigue)" });
            if (r.NoSolution)
                r.Checks.Add(new JCheck { Id = "WALL-NOSOLN-" + i.Id, Type = "deflection", Demand = Deflection(i, r.Bc, wMax), Capacity = i.DeltaAllow, Unit = "mm", LoadCase = "LC1", Method = r.Stretch >= i.DeltaAllow
                    ? "S21: the floor under the cavity stretches " + Si.F(r.Stretch) + " mm under the outward push - more than delta_wall by itself, whatever the wall width: thicken the floor, use steel, key the walls into the base, or lower the gauge (pressure)"
                    : "S21: no wall up to " + Si.F(i.WMaxOverH) + " h keeps the bulge within delta_wall - use steel, a wall keyed into the base, a thicker floor (its stretch is " + Si.F(r.Stretch) + " mm of it), a lower gauge (pressure) or a looser width tolerance" });
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

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
        public double RootFactor = 1.2, Lambda = 1.0;
        public double AllowStatic, AllowFatigue;
        /// <summary>Drawn width from geometry (lip + groove band + clearance + hardware) and the class B minimum, mm.</summary>
        public double WGeom, WMin;
        /// <summary>Class A abuse pressure F/A_proj (LC1x) and the yield at temperature for the SF 1.0 check, MPa.</summary>
        public double? PAbuse;
        public double SyT;
    }

    public sealed class WallResult
    {
        public string Id, Bc, Governs;
        public double WDefl, WStress, WStruct, WDrawn;
        public double Alpha, Beta, Sigma, SigmaAllow, Deflection;
        public double LOverH, TauStruct;
        public bool StockyFlag;
        /// <summary>Abuse stress at the drawn width (strip upper bound) and the abuse pressure that brings it to yield.</summary>
        public double? SigmaAbuse, PAbuseCap;
        public List<JCheck> Checks = new List<JCheck>();
    }

    /// <summary>
    /// S20-S25 cavity walls. A plate of width w (thickness of the wall), loaded height h, length L: bottom clamped, top free,
    /// sides clamped or simply supported. alpha_w, beta_w from the coefficient table in (L/h, (w/h)²), with the strip
    /// above the last L/h row (critique A4).
    /// (S21) w = [1.2 alpha lambda p h⁴/(E delta)]^(1/3), fixed point from 0.3 h.
    /// (S22) sigma = beta lambda p h²/w² + p b_perp/(2w) (membrane from the perpendicular walls).
    /// (S23) if sigma exceeds min(static, fatigue) allowable, w grows until it does not.
    /// (S24) w/h > 0.5 is flagged; the class A abuse case LC1x uses the strip upper bound 3 p h²/w² + p b/(2w) against
    ///       S_y,T at SF 1.0 on the drawn wall (it sets the maximum gauge).
    /// (S25) drawn w = max(w_struct, w_geom, class B minimum).
    /// </summary>
    public static class Walls
    {
        public static WallResult Size(WallInput i, double tauMax = 0.5)
        {
            var r = new WallResult { Id = i.Id, Bc = i.SidesClamped ? "Wall-C" : "Wall-S", LOverH = i.L / i.H };
            // (S21) deflection
            var w = 0.3 * i.H;
            for (var k = 0; k < 200; k++)
            {
                var a = Coefficients.Get(r.Bc, r.LOverH, w / i.H, i.Nu, 1.0).Alpha;
                var wn = Math.Pow(i.RootFactor * a * i.Lambda * i.P * Math.Pow(i.H, 4) / (i.E * i.DeltaAllow), 1.0 / 3.0);
                if (Math.Abs(wn - w) / w < 1e-10) { w = wn; break; }
                w = wn;
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
            r.Deflection = i.RootFactor * co.Alpha * i.Lambda * i.P * Math.Pow(i.H, 4) / (i.E * Math.Pow(r.WDrawn, 3));
            r.Checks.Add(new JCheck { Id = "WALL-DEFL-" + i.Id, Type = "deflection", Demand = r.Deflection, Capacity = i.DeltaAllow, Unit = "mm", LoadCase = "LC1", Method = "S21: 1.2 alpha p h^4/(E w^3) <= delta_wall (A11)" });
            r.Checks.Add(new JCheck { Id = "WALL-STRESS-" + i.Id, Type = i.AllowFatigue < i.AllowStatic ? "fatigue" : "stress", Demand = r.Sigma, Capacity = allow, Unit = "MPa", LoadCase = "LC1", Method = "S22-S23: beta p h^2/w^2 + p b/(2w) <= min(Sy/2, fatigue)" });
            if (r.StockyFlag)
                r.Checks.Add(new JCheck { Id = "WALL-STOCKY-" + i.Id, Type = "stress", Demand = r.TauStruct, Capacity = tauMax, Unit = "w/h", Status = "warn", Method = "S24: w/h > 0.5 - plate theory no longer applies; prefer steel, a wall keyed into the base, or a 3D check" });

            if (i.PAbuse != null && i.PAbuse.Value > 0)
            {
                // (S24) abuse: strip upper bound against yield at SF 1.0
                double Strip(double p) => 3 * p * i.H * i.H / (r.WDrawn * r.WDrawn) + p * i.BPerp / (2 * r.WDrawn);
                r.SigmaAbuse = Strip(i.PAbuse.Value);
                r.PAbuseCap = i.SyT / Strip(1.0);
                r.Checks.Add(new JCheck { Id = "WALL-ABUSE-" + i.Id, Type = "stress", Demand = r.SigmaAbuse.Value, Capacity = i.SyT, Unit = "MPa", LoadCase = "LC1x", Method = "S24: strip 3 p h^2/w^2 + p b/(2w) <= Sy,T (SF 1.0) at F/A_proj" });
            }
            return r;
        }
    }
}

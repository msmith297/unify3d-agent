using System;
using System.Collections.Generic;

namespace SwAgent.Mold
{
    public sealed class DowelScheme
    {
        public int Count;
        public string Scheme, Note;
        public double Mismatch_mm, Clearance_mm, LidBaseDeltaT, Span_mm;
        public bool RoundPlusRelieved, AskShop;
    }

    public sealed class InsertFit
    {
        public double ColdInterference, Pressure, Hoop, HoopAllow;
        public bool HoopOk;
    }

    /// <summary>
    /// S57-S60 fits and thermal.
    /// (S57) differential growth = |a1 - a2| dT L.
    /// (S58, critique D1) dowels: in a slide-in mold the lid cools on the table while the base stays hot, so the lid-to-base
    /// difference defaults to 30 K (10 K when the mold stays in the press); over the dowel span that is far above a
    /// 0.02-0.05 mm pin clearance. Then one round dowel at the datum plus the others slotted along radial lines from it (or
    /// round + diamond + two relieved), instead of four round pins - confirmed against how the shop fits its four dowels.
    /// (S59) steel insert in aluminium: cold interference i = d (a_Al - a_st) dT / (1 + a_Al dT) for line-to-line when hot;
    /// Lamé hoop check at the cold fit p_c = (delta/d) / [(1/E_h)((D²+d²)/(D²-d²) + nu_h) + (1-nu_s)/E_s],
    /// sigma_theta = p_c (D²+d²)/(D²-d²) <= S_y,20/2.
    /// (S60) steel dowels pressed into aluminium use the press_hole_aluminium_150C column (+0.0016 d interference).
    /// </summary>
    public static class Fits
    {
        public static double Growth(double a1, double a2, double dT, double L) => Math.Abs(a1 - a2) * dT * L;

        public static DowelScheme Dowels(double spanDiagonal_mm, double alphaLid, double alphaBase, double tempC, bool slideIn, int shopCount, Cfg cfg)
        {
            var s = new DowelScheme { Count = Math.Max(2, shopCount), Span_mm = spanDiagonal_mm };
            s.Clearance_mm = cfg.D("thermal.runningClearance_mm", 0.02);
            s.LidBaseDeltaT = slideIn ? cfg.D("thermal.lidBaseGradientSlideIn_K", 30) : cfg.D("thermal.lidBaseGradient_K", 10);
            var dT = tempC - 20;
            // lid at T - dT_lb, base at T: mismatch over the span = |a_l (dT - dT_lb) - a_b dT| L
            s.Mismatch_mm = Math.Abs(alphaLid * (dT - s.LidBaseDeltaT) - alphaBase * dT) * spanDiagonal_mm;
            if (s.Mismatch_mm > s.Clearance_mm || Math.Abs(alphaLid - alphaBase) > 1e-7)
            {
                s.RoundPlusRelieved = true;
                s.Scheme = s.Count >= 3 ? "one round dowel at the datum + " + (s.Count - 1) + " slotted along radial lines from it" : "one round + one diamond (or a radial slot)";
                s.Note = "S58/D1: " + Si.F(s.Mismatch_mm) + " mm lid-to-base mismatch over " + Si.F(spanDiagonal_mm) + " mm at a " + Si.F(s.LidBaseDeltaT) + " K difference is more than the " + Si.F(s.Clearance_mm) + " mm running clearance";
            }
            else
            {
                s.Scheme = s.Count + " round dowels";
                s.Note = "S58: mismatch " + Si.F(s.Mismatch_mm) + " mm stays within the " + Si.F(s.Clearance_mm) + " mm clearance";
            }
            s.AskShop = shopCount >= 4 && s.RoundPlusRelieved;
            if (s.AskShop) s.Note += ". Ask how the shop's existing 4 dowels are fitted before overriding the shop standard.";
            return s;
        }

        /// <summary>(S59) cold interference for a line-to-line fit when hot.</summary>
        public static double InsertInterference(double d, double alphaHub, double alphaInsert, double dT) =>
            d * (alphaHub - alphaInsert) * dT / (1 + alphaHub * dT);

        public static InsertFit InsertHoop(double d, double D, double interference, double eHub, double nuHub, double eShaft, double nuShaft, double sy20Hub, Cfg cfg)
        {
            var f = new InsertFit { ColdInterference = interference };
            var k = (D * D + d * d) / (D * D - d * d);
            f.Pressure = (interference / d) / ((1 / eHub) * (k + nuHub) + (1 - nuShaft) / eShaft);
            f.Hoop = f.Pressure * k;
            f.HoopAllow = sy20Hub / cfg.D("sfStatic", 2.0);
            f.HoopOk = f.Hoop <= f.HoopAllow;
            return f;
        }
    }

    public sealed class PinResult
    {
        public bool InsertRequired;
        public string Reason;
        public double LateralStress, LateralDeflection, Allow, DeltaAllow;
        public double AxialStiffness, AxialForce, EulerLoad, AxialStress;
        public List<JCheck> Checks = new List<JCheck>();
    }

    /// <summary>
    /// S61-S63 core pins and fragile features.
    /// (S61) a steel insert for a core pin with D &lt; 3 mm or H/D > 2, steel walls under 3 mm, pinch-offs carrying the closing
    /// load, parting-line cut-offs at tier T3+, areas that take pry or cleaning abuse.
    /// (S62) lateral: q = 0.5 p d; cantilever sigma = 32 M/(pi d³), M = q H²/2; delta = q H⁴/(8 E I).
    /// (S63, new - critique A12) shut-off pins sit 0.01-0.03 mm below the land, never proud. The axial check assumes the
    /// pin ends proud by the full height tolerance band: F = (E A/H) x band against Euler P_cr = pi² E I/(K H)² (K = 2
    /// fixed-free, 0.7 with a guided tip) and bearing F/A <= S_y, both with SF 2.
    /// </summary>
    public static class Pins
    {
        public static PinResult Check(string id, double d, double H, double p, GradeAtT g, double allowStress, double deltaAllow, Cfg cfg, bool shutOff = false, bool guidedTip = false)
        {
            var r = new PinResult { Allow = allowStress, DeltaAllow = deltaAllow };
            if (d < cfg.D("pins.insertIfDiameterBelow_mm", 3.0)) { r.InsertRequired = true; r.Reason = "core pin under " + Si.F(cfg.D("pins.insertIfDiameterBelow_mm", 3.0)) + " mm"; }
            else if (H / d > cfg.D("pins.insertIfHeightOverD", 2.0)) { r.InsertRequired = true; r.Reason = "H/D " + Si.F(H / d) + " > " + Si.F(cfg.D("pins.insertIfHeightOverD", 2.0)); }
            var q = cfg.D("pins.lateralImbalance", 0.5) * p * d;
            var M = q * H * H / 2;
            r.LateralStress = 32 * M / (Math.PI * d * d * d);
            var I = Math.PI * Math.Pow(d, 4) / 64;
            r.LateralDeflection = q * Math.Pow(H, 4) / (8 * g.E * I);
            r.Checks.Add(new JCheck { Id = "PIN-STRESS-" + id, Type = "stress", Demand = r.LateralStress, Capacity = allowStress, Unit = "MPa", LoadCase = "LC1", Method = "S62: 32 M/(pi d^3), M = 0.5 p d H^2/2" });
            r.Checks.Add(new JCheck { Id = "PIN-DEFL-" + id, Type = "deflection", Demand = r.LateralDeflection, Capacity = deltaAllow, Unit = "mm", LoadCase = "LC1", Method = "S62: q H^4/(8 E I)" });
            if (shutOff)
            {
                var band = cfg.Arr("pins.shutOffRecess_mm");
                var tol = band.Length == 2 ? band[1] - band[0] : 0.02;
                var A = Math.PI * d * d / 4;
                r.AxialStiffness = g.E * A / H;
                r.AxialForce = r.AxialStiffness * tol;
                var K = guidedTip ? cfg.D("pins.eulerKGuided", 0.7) : cfg.D("pins.eulerK", 2.0);
                r.EulerLoad = Math.PI * Math.PI * g.E * I / Math.Pow(K * H, 2);
                r.AxialStress = r.AxialForce / A;
                var sf = cfg.D("pins.axialSf", 2.0);
                r.Checks.Add(new JCheck { Id = "PIN-EULER-" + id, Type = "buckling", Demand = r.AxialForce, Capacity = r.EulerLoad / sf, Unit = "N", LoadCase = "LC2", Method = "S63 (A12): (E A/H) x height tolerance <= P_cr/2, K = " + Si.F(K) });
                r.Checks.Add(new JCheck { Id = "PIN-AXIAL-" + id, Type = "bearing", Demand = r.AxialStress, Capacity = g.Sy / sf, Unit = "MPa", LoadCase = "LC2", Method = "S63 (A12): axial bearing <= Sy/2; shut-off face 0.01-0.03 mm below the land" });
            }
            return r;
        }
    }
}

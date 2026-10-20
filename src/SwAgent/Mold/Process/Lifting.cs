using System;
using System.Linq;

namespace SwAgent.Mold
{
    public sealed class LiftResult
    {
        /// <summary>Recommended weight limit (kg) and how it was set.</summary>
        public double Rwl_kg;
        public string Basis;
        public double HM, VM, DM, FM, CM;
    }

    /// <summary>
    /// The lid weight limit from the revised NIOSH lifting equation (Waters et al. 1993; critique A2):
    /// RWL = 23 kg x (25/H)(1 - 0.003|V - 75|)(0.82 + 4.5/D) x AM x FM x CM. The three remembered answers are where the lid
    /// is lifted (slide-out table or inside the press: sets V, D and the coupling), the horizontal reach H and the lifts per
    /// hour. Unknown answers give 11 kg (25 lb). Plates lifted now and then keep the 23 kg load constant.
    /// </summary>
    public static class Lifting
    {
        public static LiftResult LidLimit(string liftAt, double? reach_cm, double? liftsPerHour, Cfg cfg, double asymmetryDeg = 0)
        {
            var unknown = cfg.D("lifting.unknownLid_kg", 11);
            if (string.IsNullOrWhiteSpace(liftAt) || liftsPerHour == null)
                return new LiftResult { Rwl_kg = unknown, Basis = "default 11 kg (25 lb): lifting answers not given" };
            // where the lid comes off: "Inside the press" or "At the slide-out table"; anything else ("Not sure") is the
            // 11 kg fallback, never a guessed location
            var k = liftAt.Trim().ToLowerInvariant();
            var loc = k.Contains("press") ? "press" : k.Contains("table") || k.Contains("slide") ? "table" : null;
            if (loc == null)
                return new LiftResult { Rwl_kg = unknown, Basis = "default 11 kg (25 lb): where the lid is lifted is not known (" + liftAt.Trim() + ")" };
            var hLoc = cfg.D("lifting.locations." + loc + ".H_cm", loc == "press" ? 55 : 40);
            // inside the press the lid is reached over the platen: at least the location's reach (55 cm) whatever was typed
            var H = loc == "press" ? Math.Max(reach_cm ?? hLoc, hLoc) : reach_cm ?? hLoc;
            var V = cfg.D("lifting.locations." + loc + ".V_cm", loc == "press" ? 100 : 90);
            var D = cfg.D("lifting.locations." + loc + ".D_cm", 25);
            var cm = cfg.D("lifting.locations." + loc + ".coupling", loc == "press" ? 0.9 : 1.0);
            return Rwl(H, V, D, liftsPerHour.Value / 60.0, cm, cfg, asymmetryDeg, "NIOSH RWL, lid lifted " + (loc == "press" ? "inside the press" : "at the slide-out table") + ", reach " + Si.F(H) + " cm");
        }

        public static LiftResult Rwl(double H_cm, double V_cm, double D_cm, double liftsPerMin, double coupling, Cfg cfg, double asymmetryDeg = 0, string basis = "NIOSH RWL")
        {
            var lc = cfg.D("lifting.loadConstant_kg", 23);
            var hm = H_cm <= 25 ? 1.0 : H_cm > 63 ? 0.0 : 25.0 / H_cm;
            var vm = V_cm > 175 ? 0.0 : 1.0 - 0.003 * Math.Abs(V_cm - 75);
            var dm = D_cm <= 25 ? 1.0 : D_cm > 175 ? 0.0 : 0.82 + 4.5 / D_cm;
            var am = asymmetryDeg > 135 ? 0.0 : 1.0 - 0.0032 * asymmetryDeg;
            var fm = Frequency(liftsPerMin, cfg);
            var r = new LiftResult { HM = hm, VM = vm, DM = dm, FM = fm, CM = coupling, Basis = basis };
            r.Rwl_kg = lc * hm * vm * dm * am * fm * coupling;
            return r;
        }

        /// <summary>Frequency multiplier for the shop's work duration (default 8 h; V >= 75 cm column), interpolated.</summary>
        public static double Frequency(double liftsPerMin, Cfg cfg)
        {
            var xs = cfg.Arr("lifting.frequencyMultiplier.liftsPerMin");
            var hours = cfg.D("lifting.workHours", 8);
            var col = hours <= 1 ? "upTo1h" : hours <= 2 ? "upTo2h" : "upTo8h";
            var ys = cfg.Arr("lifting.frequencyMultiplier." + col);
            if (xs.Length == 0 || ys.Length != xs.Length) return 0.85;
            if (liftsPerMin <= xs[0]) return ys[0];
            if (liftsPerMin > xs[xs.Length - 1]) return 0.0;
            return Cfg.Interp(xs, ys, liftsPerMin);
        }

        /// <summary>Lifting index = weight / RWL (should be <= 1).</summary>
        public static double Index(double mass_kg, double rwl_kg) => rwl_kg <= 0 ? double.PositiveInfinity : mass_kg / rwl_kg;
    }
}

using System;
using System.Collections.Generic;

namespace SwAgent.Mold
{
    public sealed class KeyResult
    {
        public double FSep, KeyBearing, KeyBearingAllow, KeyShear, KeyShearAllow, PTotTipping;
        public double FSepAbuse, KeyBearingAbuse, KeyShearAbuse;
        /// <summary>The abuse pressure (MPa) at which the key reaches yield (bearing or shear) at SF 1.0 (A3 gauge cap).</summary>
        public double PAbuseCap = double.PositiveInfinity;
        public List<JCheck> Checks = new List<JCheck>();
    }

    /// <summary>
    /// S37-S41 multi-piece bases: keys first, never friction.
    /// (S37) F_sep = p L_c h. (S38) key bearing F_sep/(k L_key) <= S_y,T/2.5 with step k >= 9.5 mm. (S39) root shear
    /// F_sep/(L_key w_key) <= 0.58 S_y,T/2. (S40) tipping: bolt tension P_tot = F_sep (h/2)/w_bar. (S41) friction-only joints
    /// are forbidden. Critique A3: class A joints are also checked at LC1x (F/A_proj) with SF 1.0 on yield; where that
    /// fails, the maximum gauge is capped at the joint's capacity.
    /// </summary>
    public static class Keys
    {
        public static KeyResult Check(string id, double p, double pAbuse, double loadedLength, double depth, double keyLength, double keyStep, double keyRootWidth, double barWidth, GradeAtT g, Cfg cfg)
        {
            var r = new KeyResult();
            var k = Math.Max(keyStep, cfg.D("keys.minStep_mm", 9.5));
            r.FSep = p * loadedLength * depth;
            r.KeyBearing = r.FSep / (k * keyLength);
            r.KeyBearingAllow = g.Sy / cfg.D("keys.bearingSf", 2.5);
            r.KeyShear = r.FSep / (keyLength * keyRootWidth);
            r.KeyShearAllow = 0.58 * g.Sy / cfg.D("keys.shearSf", 2.0);
            r.PTotTipping = r.FSep * (depth / 2) / barWidth;
            r.Checks.Add(new JCheck { Id = "KEY-BEAR-" + id, Type = "bearing", Demand = r.KeyBearing, Capacity = r.KeyBearingAllow, Unit = "MPa", LoadCase = "LC1", Method = "S38: F_sep/(k L) <= Sy,T/2.5" });
            r.Checks.Add(new JCheck { Id = "KEY-SHEAR-" + id, Type = "stress", Demand = r.KeyShear, Capacity = r.KeyShearAllow, Unit = "MPa", LoadCase = "LC1", Method = "S39: F_sep/(L w) <= 0.58 Sy,T/2" });
            if (pAbuse > 0)
            {
                var sf = cfg.D("classA.abuseSf", 1.0);
                r.FSepAbuse = pAbuse * loadedLength * depth;
                r.KeyBearingAbuse = r.FSepAbuse / (k * keyLength);
                r.KeyShearAbuse = r.FSepAbuse / (keyLength * keyRootWidth);
                r.Checks.Add(new JCheck { Id = "KEY-BEAR-X-" + id, Type = "bearing", Demand = r.KeyBearingAbuse, Capacity = g.Sy / sf, Unit = "MPa", LoadCase = "LC1x", Method = "A3: key bearing at F/A_proj <= Sy,T (SF 1.0)" });
                r.Checks.Add(new JCheck { Id = "KEY-SHEAR-X-" + id, Type = "stress", Demand = r.KeyShearAbuse, Capacity = 0.58 * g.Sy / sf, Unit = "MPa", LoadCase = "LC1x", Method = "A3: key shear at F/A_proj <= 0.58 Sy,T (SF 1.0)" });
                var perP = loadedLength * depth;
                r.PAbuseCap = Math.Min(g.Sy / sf * k * keyLength / perP, 0.58 * g.Sy / sf * keyLength * keyRootWidth / perP);
            }
            return r;
        }
    }
}

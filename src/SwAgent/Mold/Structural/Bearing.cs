using System;

namespace SwAgent.Mold
{
    public sealed class BearingResult
    {
        public double Area, Stress, Allow, Db, GaugeMax_MPa;
        public string DbBasis;
    }

    /// <summary>
    /// S33-S36 (LC2: the press force through the land, parting face and stops).
    /// (S34) sigma = F/A_bear <= S_y,T/D_b, D_b = 3 for N >= 1e5, 2 below - judgement against hobbing - on the weaker
    /// contact material; critique A8: aluminium above 150 C creeps under the sustained clamp load, so D_b = 4 there until
    /// 10,000 h creep data is loaded.
    /// (S35) the bearing allows a gauge of A_bear S_y,T/(D_b A_ram).
    /// (S36) class B lands alone: if F/A_land > S_y,T/D_b, stop pads are mandatory.
    /// </summary>
    public static class Bearing
    {
        public static double Divisor(double cycles, GradeAtT weaker, Cfg cfg, out string basis)
        {
            var from = cfg.D("bearingDivisor.highCycleFrom", 100000);
            var db = cycles >= from ? cfg.D("bearingDivisor.highCycle", 3.0) : cfg.D("bearingDivisor.lowCycle", 2.0);
            basis = "S34, D_b = " + Si.F(db) + (cycles >= from ? " (N >= 1e5)" : " (N < 1e5)");
            var hotC = cfg.D("bearingDivisor.aluminiumHotAboveC", 150);
            if (weaker.IsAluminium && weaker.TempC > hotC)
            {
                var hot = cfg.D("bearingDivisor.aluminiumHot", 4.0);
                if (hot > db)
                {
                    db = hot;
                    basis = "S34 + A8, D_b = " + Si.F(db) + " (aluminium creep above " + Si.F(hotC) + " C)";
                }
            }
            return db;
        }

        public static BearingResult Compute(double force_N, double area_mm2, double ramArea_mm2, GradeAtT weaker, double cycles, Cfg cfg)
        {
            var r = new BearingResult { Area = area_mm2 };
            r.Db = Divisor(cycles, weaker, cfg, out r.DbBasis);
            r.Allow = weaker.Sy / r.Db;
            r.Stress = area_mm2 > 0 ? force_N / area_mm2 : double.PositiveInfinity;
            r.GaugeMax_MPa = ramArea_mm2 > 0 ? area_mm2 * weaker.Sy / (r.Db * ramArea_mm2) : double.NaN;
            return r;
        }

        /// <summary>Stop-pad area needed so the press force stays within the bearing allowable (class B).</summary>
        public static double StopAreaNeeded(double force_N, GradeAtT weaker, double cycles, Cfg cfg) =>
            force_N * Divisor(cycles, weaker, cfg, out _) / weaker.Sy;
    }
}

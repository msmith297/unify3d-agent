using System;

namespace SwAgent.Mold
{
    public sealed class GapResult
    {
        public string FlowClass;
        public double DeltaFlash, Flatness, Parallelism;
        /// <summary>Allowed deflection at a land (vertical gap) and for a cavity wall (width), mm.</summary>
        public double DeltaGap, DeltaWall;
        public string GapBasis, WallBasis;
    }

    /// <summary>
    /// S13-S14 with the critique A11 gap budget:
    /// - land flatness + platen parallelism + deflection <= delta_flash, so delta_gap = delta_flash - flatness - parallelism,
    ///   and no more than 0.25 x the closure tolerance when the drawing gives one (S14);
    /// - in a flash mold a wall's sideways bulge hardly opens the vertical gap, so walls are limited on the part width:
    ///   delta_wall = 0.25 x t_tol (the drawing's tightest width tolerance, default +-0.125 mm, design-rules 19);
    ///   telescoping fits (not built in v1) would use half the design clearance.
    /// For liquids the class comes from the page viscosity (the user's edit counts); other classes from the row.
    /// </summary>
    public static class GapBudget
    {
        public static string FlowClass(ProcessClass pc, string processType, string viscosityClass, double? viscosityCp)
        {
            if (processType == "liquid_compression" || viscosityClass == "paste_lsr") return "paste_lsr";
            if (pc == ProcessClass.A_Pour)
            {
                var eta = viscosityCp ?? 1000;
                return eta < 1000 ? "liquidBelow1000cP" : eta <= 10000 ? "liquid1000to10000cP" : "liquidAbove10000cP";
            }
            switch (viscosityClass)
            {
                case "rubber":
                case "rubber_stiff":
                    return "rubber";
                case "compound":
                case "sheet_bulk":
                    return "compound";
                case "melt":
                    return "melt";
                case "no_flow":
                    return "no_flow";
                default:
                    return processType == "rubber_compression" ? "rubber" : "compound";
            }
        }

        /// <summary>
        /// deltaOverride (the `delta_allow_mm` parameter) sets the land/plate gap allowable delta_gap directly - it is
        /// applied before the budget is tested, so it can stand in for a budget that flatness has used up; wallOverride
        /// (`delta_wall_mm`) sets the wall allowable delta_wall. Neither touches the other.
        /// </summary>
        public static GapResult Compute(string flowClass, Cfg cfg, double? closureTol_mm = null, double? widthTol_mm = null, double? deltaOverride = null, double? wallOverride = null)
        {
            var r = new GapResult { FlowClass = flowClass };
            r.DeltaFlash = cfg.D("deltaFlash_mm." + flowClass, 0.025);
            r.Flatness = cfg.D("landFlatness_mm." + flowClass, 0.5 * r.DeltaFlash);
            r.Parallelism = cfg.D("platenParallelism_mm", 0);
            r.DeltaGap = r.DeltaFlash - r.Flatness - r.Parallelism;
            r.GapBasis = $"A11: {Si.F(r.DeltaFlash)} flash gap - {Si.F(r.Flatness)} land flatness - {Si.F(r.Parallelism)} platen parallelism";
            if (closureTol_mm != null && closureTol_mm.Value > 0)
            {
                var t = cfg.D("deltaFromTolerance", 0.25) * closureTol_mm.Value;
                if (t < r.DeltaGap)
                {
                    r.DeltaGap = t;
                    r.GapBasis = $"S14: 0.25 x closure tolerance {Si.F(closureTol_mm.Value)} mm";
                }
            }
            if (deltaOverride != null)
            {
                r.DeltaGap = deltaOverride.Value;
                r.GapBasis = "override delta_allow_mm (lands and plates)";
            }
            if (!(r.DeltaGap > 0))
                throw new ArgumentException(deltaOverride != null
                    ? "delta_allow_mm must be above 0 mm."
                    : "The gap budget is used up: land flatness " + Si.F(r.Flatness) + " mm + platen parallelism " + Si.F(r.Parallelism) + " mm leave nothing of the " + Si.F(r.DeltaFlash) + " mm flash gap. Lap the lands flatter (override landFlatness_mm." + flowClass + "), or set the land allowable with the override delta_allow_mm.");
            var tol = widthTol_mm ?? cfg.D("defaultTolerance_mm", 0.125);
            r.DeltaWall = cfg.D("wallWidthTolFraction", 0.25) * tol;
            r.WallBasis = $"A11: 0.25 x width tolerance +-{Si.F(tol)} mm" + (widthTol_mm == null ? " (default, drawing silent)" : "");
            if (wallOverride != null)
            {
                if (!(wallOverride.Value > 0)) throw new ArgumentException("delta_wall_mm must be above 0 mm.");
                r.DeltaWall = wallOverride.Value;
                r.WallBasis = "override delta_wall_mm (cavity walls)";
            }
            return r;
        }
    }
}

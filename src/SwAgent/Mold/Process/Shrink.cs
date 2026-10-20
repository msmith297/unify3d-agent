using System;
using System.Collections.Generic;
using System.Globalization;

namespace SwAgent.Mold
{
    /// <summary>The basis a shrink value was measured on (critique A5). The page offers the first three plus v1 style.</summary>
    public enum ShrinkBasis { DatasheetSteelMold, HotCavityColdPart, MeasuredInMyMolds, RoomTemperature, PreformSinter, V1Style }

    public sealed class ShrinkResult
    {
        public ShrinkBasis Basis;
        public string BasisCode, BasisLabel;
        /// <summary>Scale factors along U, V, W (cavity / part, at room temperature).</summary>
        public double Su, Sv, Sw;
        public double EffectivePct => (1 - 1 / Su) * 100;
        public double DeltaT, AlphaRef, AlphaCavity;
        public bool IgnoresPressTemperature;
        public string FeatureName;
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// S64 scale factor by basis (critique A5), exact forms (never 1 + S, which is off by S²) except the explicit
    /// "v1 style" option offered once so molds built with v1 can be matched:
    /// - datasheet (steel mold): s = 1/(1-S) * (1 + a_ref dT)/(1 + a_cav dT)  (Formula 2, a_ref = 12e-6 /K)
    /// - hot cavity to cold part: s = 1/((1-S)(1 + a_cav dT))                 (Formula 3)
    /// - measured in my molds at this temperature, room temperature: s = 1/(1-S), no metal term (no double count)
    /// - preform + sinter: s = 1/(1-S), press temperature ignored, plus machining stock (note)
    /// dT = T - 20 C.
    /// </summary>
    public static class Shrink
    {
        public static ShrinkBasis ParseBasis(string s)
        {
            var k = (s ?? "").Trim().ToLowerInvariant();
            if (k.Length == 0 || k == "cold_mold_cold_part_steel_ref" || k.StartsWith("supplier") || k.StartsWith("datasheet")) return ShrinkBasis.DatasheetSteelMold;
            if (k == "hot_cavity_cold_part" || k.StartsWith("hot cavity")) return ShrinkBasis.HotCavityColdPart;
            if (k == "measured_in_my_molds" || k.StartsWith("measured")) return ShrinkBasis.MeasuredInMyMolds;
            if (k == "rt_mold_rt_part" || k.StartsWith("room")) return ShrinkBasis.RoomTemperature;
            if (k == "preform_die_rt_to_sintered_part_rt" || k.StartsWith("preform")) return ShrinkBasis.PreformSinter;
            if (k == "v1_style" || k.StartsWith("v1")) return ShrinkBasis.V1Style;
            throw new ArgumentException("Unknown shrink basis '" + s + "'");
        }

        public static string Code(ShrinkBasis b)
        {
            switch (b)
            {
                case ShrinkBasis.HotCavityColdPart: return "hot_cavity_cold_part";
                case ShrinkBasis.MeasuredInMyMolds: return "measured_in_my_molds";
                case ShrinkBasis.RoomTemperature: return "rt_mold_rt_part";
                case ShrinkBasis.PreformSinter: return "preform_die_rt_to_sintered_part_rt";
                case ShrinkBasis.V1Style: return "v1_style";
                default: return "cold_mold_cold_part_steel_ref";
            }
        }

        public static string Label(ShrinkBasis b)
        {
            switch (b)
            {
                case ShrinkBasis.HotCavityColdPart: return "hot-cavity basis";
                case ShrinkBasis.MeasuredInMyMolds: return "measured basis";
                case ShrinkBasis.RoomTemperature: return "room-temperature basis";
                case ShrinkBasis.PreformSinter: return "preform basis";
                case ShrinkBasis.V1Style: return "v1 style 1+S";
                default: return "datasheet basis";
            }
        }

        public static double Factor(double shrinkPct, ShrinkBasis basis, double tempC, double alphaCavity, double alphaRef = 1.2e-5)
        {
            var S = shrinkPct / 100.0;
            var dT = tempC - 20.0;
            switch (basis)
            {
                case ShrinkBasis.DatasheetSteelMold: return 1.0 / (1.0 - S) * (1.0 + alphaRef * dT) / (1.0 + alphaCavity * dT);
                case ShrinkBasis.HotCavityColdPart: return 1.0 / ((1.0 - S) * (1.0 + alphaCavity * dT));
                case ShrinkBasis.V1Style: return 1.0 + S;
                default: return 1.0 / (1.0 - S);
            }
        }

        /// <summary>
        /// The scale factors for the part. `perAxisPct` (U, V, W) replaces the scalar shrink per axis when given (SMC/BMC,
        /// PEEK, UHMWPE, PTFE). `cavityShort` names the cavity metal for the feature name.
        /// </summary>
        public static ShrinkResult Compute(double shrinkPct, ShrinkBasis basis, double tempC, double alphaCavity, string cavityShort,
            double[] perAxisPct = null, bool preformSinter = false, double alphaRef = 1.2e-5)
        {
            if (preformSinter) basis = ShrinkBasis.PreformSinter;
            var r = new ShrinkResult
            {
                Basis = basis, BasisCode = Code(basis), BasisLabel = Label(basis),
                DeltaT = tempC - 20.0, AlphaRef = alphaRef, AlphaCavity = alphaCavity,
                IgnoresPressTemperature = basis == ShrinkBasis.PreformSinter,
            };
            var su = perAxisPct?[0] ?? shrinkPct;
            var sv = perAxisPct?[1] ?? shrinkPct;
            var sw = perAxisPct?[2] ?? shrinkPct;
            r.Su = Factor(su, basis, tempC, alphaCavity, alphaRef);
            r.Sv = Factor(sv, basis, tempC, alphaCavity, alphaRef);
            r.Sw = Factor(sw, basis, tempC, alphaCavity, alphaRef);
            if (basis == ShrinkBasis.PreformSinter)
                r.Notes.Add("PTFE preform: the press temperature is ignored; add 1.5-3 mm machining stock per side - the sintered part is machined to size");
            if (basis == ShrinkBasis.V1Style)
                r.Notes.Add("v1 style s = 1 + S: only to match a mold built with v1 (it is S² off the exact 1/(1-S))");
            var uniform = Math.Abs(r.Su - r.Sv) < 1e-12 && Math.Abs(r.Sv - r.Sw) < 1e-12;
            var temp = r.IgnoresPressTemperature ? "" : ", " + cavityShort + " at " + tempC.ToString("0", CultureInfo.InvariantCulture) + " C";
            r.FeatureName = uniform
                ? "MOLD Shrink s=" + r.Su.ToString("0.000000", CultureInfo.InvariantCulture) + " (" + r.BasisLabel + temp + ")"
                : "MOLD Shrink s=" + string.Join("/", new[] { r.Su, r.Sv, r.Sw }.Select6()) + " (" + r.BasisLabel + temp + ")";
            return r;
        }

        private static IEnumerable<string> Select6(this double[] xs)
        {
            foreach (var x in xs) yield return x.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        /// <summary>(S65) A dimension needs a steel-safe cavity when the shrink spread across it exceeds half its tolerance.</summary>
        public static bool SteelSafe(double shrinkMinPct, double shrinkMaxPct, double length_mm, double tolerancePlusMinus_mm) =>
            (shrinkMaxPct - shrinkMinPct) / 100.0 * length_mm > 0.5 * tolerancePlusMinus_mm;
    }
}

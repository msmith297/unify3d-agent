using System;
using System.Collections.Generic;

namespace SwAgent.Mold
{
    public sealed class ChargeInput
    {
        public ProcessClass Class;
        public string ProcessType;
        public int Cavities = 1;
        /// <summary>Molded-part volume (mm³, unscaled: the charge is sized by part mass, not by the scaled cavity).</summary>
        public double PartVolume_mm3;
        public double Sg;
        /// <summary>Charge excess over the part mass (%, page charge_excess_pct or the row's flashAllowancePct.typ).</summary>
        public double ExcessPct;
        /// <summary>Row overflowPctOfPartVolume.typ (%).</summary>
        public double OverflowPct;
        /// <summary>Overflow perimeter per cavity (mm) and the row's groove radius range (mm).</summary>
        public double GroovePerimeter;
        public double GrooveRadiusMin, GrooveRadiusMax;
        /// <summary>The shop's class A moat: width x depth x centre-line length per cavity (mm³), and runners exit the plate.</summary>
        public double? MoatCapacity_mm3;
        public bool RunnersExitPlate;
        public double PotLifeMin;
        public double MassRate_g_s = 10, HandlingSeconds = 15;
        /// <summary>Cure: datasheet time at the row's typical temperature, the row's range, the press temperature.</summary>
        public double CureTypMin, MoldTempTypC, MoldTempMinC, MoldTempMaxC, PressTempC;
        /// <summary>Thickest section (mm) for the heat-up adder (rubber).</summary>
        public double MaxSection_mm;
        public double BulkFactor = 1.0;
        /// <summary>Loading-well area (mm²; default the cavity's projected area) and the cavity volume (mm³) for S70.</summary>
        public double? WellArea_mm2;
        public double CavityVolume_mm3;
        public double ExtraRiser_g;
    }

    public sealed class ChargeResult
    {
        public double PerCavity_g, Shot_g, HoldUpPct;
        public double OverflowRequired_mm3, GrooveRadius_mm;
        public bool GrooveRadiusClamped, NeedsSecondGroove;
        public double PourSecondsPerCavity, PourSecondsTotal, PourLimitSeconds;
        public double CureMin;
        public string CureNote;
        public double? LoadingWellDepth_mm, LoadVolume_mm3;
        public List<JCheck> Checks = new List<JCheck>();
        public List<string> Notes = new List<string>();
    }

    /// <summary>S66-S70 with critique D3 (pour time scales with the charge) and E6 (cure note per class).</summary>
    public static class Charge
    {
        public static ChargeResult Compute(ChargeInput i, Cfg cfg)
        {
            var r = new ChargeResult();
            // (S66) per cavity m = V * SG * (1 + excess); g = mm³ * g/cm³ / 1000
            r.PerCavity_g = i.PartVolume_mm3 * i.Sg / 1000.0 * (1 + i.ExcessPct / 100.0);
            r.HoldUpPct = i.Class == ProcessClass.A_Pour ? cfg.D("charge.holdUpPctLiquids", 5) : 0;
            r.Shot_g = i.Cavities * r.PerCavity_g * (1 + r.HoldUpPct / 100.0) + i.ExtraRiser_g;

            // (S67) overflow volume and groove
            var k = i.Class == ProcessClass.A_Pour ? cfg.D("charge.overflowFactorClassA", 2.0) : cfg.D("charge.overflowFactorClassB", 1.5);
            r.OverflowRequired_mm3 = Math.Max(i.OverflowPct * i.PartVolume_mm3, k * i.ExcessPct * i.PartVolume_mm3) / 100.0;
            if (i.MoatCapacity_mm3 != null)
            {
                if (i.RunnersExitPlate)
                    r.Notes.Add("S67: the runners leave the plate edge, so the excess leaves the mold; the moat holds " + Si.F(i.MoatCapacity_mm3.Value / 1000) + " cm³ against " + Si.F(r.OverflowRequired_mm3 / 1000) + " cm³ needed");
                else
                    r.Checks.Add(new JCheck { Id = "CHG-OVERFLOW", Type = "fill", Demand = r.OverflowRequired_mm3, Capacity = i.MoatCapacity_mm3.Value, Unit = "mm3", Method = "S67: flash system holds >= V_req" });
            }
            else if (i.GroovePerimeter > 0 && r.OverflowRequired_mm3 > 0)
            {
                // semicircular groove: V = pi r² / 2 * P
                var rad = Math.Sqrt(2 * r.OverflowRequired_mm3 / (Math.PI * i.GroovePerimeter));
                r.GrooveRadius_mm = rad;
                if (i.GrooveRadiusMax > 0 && rad > i.GrooveRadiusMax)
                {
                    r.GrooveRadius_mm = i.GrooveRadiusMax;
                    r.GrooveRadiusClamped = r.NeedsSecondGroove = true;
                    r.Notes.Add("S67: groove radius " + Si.F(rad) + " mm is above the material's " + Si.F(i.GrooveRadiusMax) + " mm: add a second groove or spill pockets");
                }
                else if (i.GrooveRadiusMin > 0 && rad < i.GrooveRadiusMin)
                {
                    r.GrooveRadius_mm = i.GrooveRadiusMin;
                    r.GrooveRadiusClamped = true;
                }
            }

            // (S68, D3) pour time against pot life: t = m / mass rate + handling per cavity; n t <= 0.5 pot life
            if (i.Class == ProcessClass.A_Pour)
            {
                r.PourSecondsPerCavity = r.PerCavity_g / Math.Max(1e-9, i.MassRate_g_s) + i.HandlingSeconds;
                r.PourSecondsTotal = i.Cavities * r.PourSecondsPerCavity;
                r.PourLimitSeconds = 0.5 * i.PotLifeMin * 60.0;
                if (i.Cavities > 1 && i.PotLifeMin > 0)
                    r.Checks.Add(new JCheck { Id = "CHG-POUR", Type = "fill", Demand = r.PourSecondsTotal, Capacity = r.PourLimitSeconds, Unit = "s", Method = "S68: n x (m/mdot + handling) <= 0.5 x pot life (D3)", Note = "else pour in batches or a recorded sequence" });
            }

            // (S69, E6) cure
            var T = i.PressTempC;
            if (i.ProcessType == "rubber_compression" || i.ProcessType == "compound_compression" || i.ProcessType == "liquid_compression")
            {
                var Tc = Math.Min(Math.Max(T, i.MoldTempMinC), i.MoldTempMaxC);
                r.CureMin = i.CureTypMin * Math.Pow(2, (i.MoldTempTypC - Tc) / 10.0);
                var alpha = i.ProcessType == "compound_compression" ? cfg.D("cure.alphaThermalThermoset_m2_s", 1.3e-7) : cfg.D("cure.alphaThermal_m2_s", 1.0e-7);
                var half = i.MaxSection_mm / 2000.0;
                var heat = half * half / alpha / 60.0;
                r.CureMin += heat;
                r.CureNote = "S69: " + Si.F(i.CureTypMin) + " min at " + Si.F(i.MoldTempTypC) + " C halves per +10 C; +" + Si.F(heat) + " min to heat the " + Si.F(i.MaxSection_mm) + " mm section";
                if (T > i.MoldTempMaxC)
                    r.Checks.Add(new JCheck { Id = "CURE-TEMP-HIGH", Type = "cure", Demand = T, Capacity = i.MoldTempMaxC, Unit = "degC", Method = "S69: above the row's range: reversion or scorch", Status = "warn" });
                if (T < i.MoldTempMinC)
                    r.Checks.Add(new JCheck { Id = "CURE-TEMP-LOW", Type = "cure", Demand = T, Capacity = i.MoldTempMinC, Unit = "degC", Lower = true, Method = "S69: below the row's range", Status = "warn" });
            }
            else
            {
                // liquid PU and resins: demold time does not scale with the rubber rule; print the datasheet time
                r.CureMin = i.CureTypMin;
                r.CureNote = "E6: datasheet demold time at " + Si.F(i.MoldTempTypC) + " C (no temperature scaling for liquid systems)";
                if (T < i.MoldTempMinC)
                    r.Checks.Add(new JCheck { Id = "CURE-TEMP-LOW", Type = "cure", Demand = T, Capacity = i.MoldTempMinC, Unit = "degC", Lower = true, Method = "E6: press below the material's minimum mold temperature: under-cure", Status = "warn" });
            }

            // (S70) loading well when the charge bulks
            if (i.BulkFactor > 1.0 + 1e-9)
            {
                r.LoadVolume_mm3 = r.PerCavity_g * 1000.0 / i.Sg * i.BulkFactor;
                var area = i.WellArea_mm2 ?? 0;
                if (area > 0) r.LoadingWellDepth_mm = Math.Max(0, (r.LoadVolume_mm3.Value - i.CavityVolume_mm3) / area);
            }
            return r;
        }
    }
}

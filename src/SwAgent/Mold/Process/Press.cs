using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Mold
{
    /// <summary>What the page's press value means (design D5, critique C2).</summary>
    public enum PressMeaning { Gauge, OnPart, ForceReading, Unknown }

    /// <summary>One cavity's areas for the press force (S2 with critique A9).</summary>
    public sealed class CavityArea
    {
        public string Id;
        /// <summary>Projected cavity area A_i and outline perimeter P_i (mm², mm).</summary>
        public double Area, Perimeter;
        /// <summary>Overflow groove and loading-well areas that the material fills (A9; mm²).</summary>
        public double GrooveArea, WellArea;
        /// <summary>Cavity origin in the mold frame (mm), for the load centre (S8).</summary>
        public double U, V;
        /// <summary>Cavity depth (mm), for the thermoset depth adder (S6).</summary>
        public double Depth;
    }

    public sealed class PressInput
    {
        public ProcessClass Class;
        public PressMeaning Meaning;
        /// <summary>Page value: a pressure (MPa) for Gauge/OnPart, a force (kN) for ForceReading.</summary>
        public double Value;
        /// <summary>Nameplate force (kN) and the gauge at that force (MPa); give the ram instead with RamArea_mm2.</summary>
        public double RatedForce_kN, RatedGauge_MPa;
        public double? RamArea_mm2;
        public List<CavityArea> Cavities = new List<CavityArea>();
        /// <summary>Land width counted with each cavity (class A lip, class B land; S2).</summary>
        public double LandWidth;
        /// <summary>Part row molding pressure (MPa): typical (page-editable, C3) and table maximum.</summary>
        public double MoldingPressureTyp, MoldingPressureMax;
        public bool ThermosetCompound;
        public double? PlatenL, PlatenW, Daylight, Stroke;
        /// <summary>Mold footprint (plates and cavities only, critique D2) and closed stack height, when known (mm).</summary>
        public double? FootprintL, FootprintW, StackHeight;
        /// <summary>Centre of the mold footprint relative to the platen centre (mm); the locating stop puts it at 0.</summary>
        public double MoldOffsetU, MoldOffsetV;
    }

    public sealed class PressResult
    {
        public double RamArea_mm2, Force_N, AProj_mm2, AProjForce_mm2;
        public double PLC1, PLC1x, PReq, FReq_N, GaugeNeeded_MPa, GaugeSet_MPa;
        /// <summary>Gauge pressure (MPa) that gives the requested force on the part (for "On the part").</summary>
        public double GaugeForInput_MPa;
        public double LoadCentreU, LoadCentreV;
        public List<JCheck> Checks = new List<JCheck>();
        public List<string> Notes = new List<string>();
    }

    /// <summary>A check before it becomes record JSON (keeps Press free of JSON plumbing).</summary>
    public sealed class JCheck
    {
        public string Id, Type, Unit, Method, Note, LoadCase;
        public double Demand, Capacity;
        public bool Lower;
        public string Status;   // null = from the margin
    }

    /// <summary>
    /// S1-S9 with critique A9 (force area includes overflow grooves and loading wells; p_LC1 keeps the smaller area) and
    /// C2 (a force reading is a meaning of its own). Units: N, mm, MPa.
    /// </summary>
    public static class Press
    {
        public static PressResult Compute(PressInput i, Cfg cfg)
        {
            var r = new PressResult();
            // (S1) ram area from the rating: mm² = N / (N/mm²)
            r.RamArea_mm2 = i.RamArea_mm2 ?? (i.RatedGauge_MPa > 0 ? i.RatedForce_kN * 1000.0 / i.RatedGauge_MPa : double.NaN);

            // (S2, A9) projected areas: the land is loaded with the cavity (SPI convention); grooves and wells that the
            // material fills add to the force area but not to the area that sets p_LC1 (the smaller area is worst for steel).
            r.AProj_mm2 = i.Cavities.Sum(c => c.Area + c.Perimeter * i.LandWidth);
            r.AProjForce_mm2 = r.AProj_mm2 + i.Cavities.Sum(c => c.GrooveArea + c.WellArea);

            // (S3) press force
            switch (i.Meaning)
            {
                case PressMeaning.Gauge:
                    r.Force_N = i.Value * r.RamArea_mm2;
                    if (i.Value > i.RatedGauge_MPa * (1 + 1e-9))
                        r.Checks.Add(new JCheck { Id = "PRESS-GAUGE-RATING", Type = "press", Demand = i.Value, Capacity = i.RatedGauge_MPa, Unit = "MPa", Method = "S3: gauge reading must not exceed the gauge at rated force" });
                    break;
                case PressMeaning.OnPart:
                    r.Force_N = i.Value * r.AProjForce_mm2;
                    r.GaugeForInput_MPa = r.Force_N / r.RamArea_mm2;
                    break;
                case PressMeaning.ForceReading:
                    r.Force_N = i.Value * 1000.0;
                    break;
                default:
                    throw new InvalidOperationException("The press pressure meaning is not known yet: ask the user (gauge reading, on the part or a force reading) before sizing.");
            }
            r.GaugeNeeded_MPa = r.Force_N / r.RamArea_mm2;

            // (S4/S5) cavity pressure
            r.PLC1x = r.Force_N / r.AProj_mm2;
            if (i.Class == ProcessClass.A_Pour)
            {
                // liquid pour: the press mostly clamps the lid; hydrostatic head + squeeze is the real load
                r.PLC1 = Math.Max(cfg.D("classA.pressureFactorOnTableMax", 1.5) * i.MoldingPressureMax, cfg.D("classA.minDesignPressure_MPa", 0.35));
            }
            else
            {
                r.PLC1 = r.PLC1x;
            }

            // (S6) force needed
            var hMax = i.Cavities.Count == 0 ? 0 : i.Cavities.Max(c => c.Depth);
            if (i.Class == ProcessClass.A_Pour)
            {
                r.PReq = r.PLC1;
                r.FReq_N = cfg.D("classA.closeForceReserve", 1.25) * r.PLC1 * r.AProjForce_mm2;
            }
            else
            {
                r.PReq = i.MoldingPressureTyp;
                if (i.ThermosetCompound && hMax > 25.0)
                    r.PReq += cfg.D("thermosetDepthAdder_MPa_per_25mm", 4.1) * (hMax - 25.0) / 25.0;
                r.FReq_N = r.PReq * r.AProjForce_mm2;
            }

            // (S7) recommended gauge and flags
            r.GaugeSet_MPa = r.FReq_N / r.RamArea_mm2;
            var reserve = cfg.D("pressReserve", 1.25);
            r.Checks.Add(new JCheck { Id = "PRESS-CAPACITY", Type = "press", Demand = r.FReq_N * reserve / 1000.0, Capacity = i.RatedForce_kN, Unit = "kN", Method = "S7: F_req x " + reserve + " <= rated force" });
            r.Checks.Add(new JCheck { Id = "PRESS-FORCE", Type = "press", Demand = r.Force_N / 1000.0, Capacity = r.FReq_N / 1000.0, Unit = "kN", Lower = true, Method = "S6: the press force at this setting must reach the force needed" });
            if (i.Class == ProcessClass.B_Compression && r.PLC1 > cfg.D("excessForceFlag", 1.5) * r.PReq)
            {
                r.Checks.Add(new JCheck
                {
                    Id = "PRESS-EXCESS", Type = "press", Demand = r.PLC1, Capacity = cfg.D("excessForceFlag", 1.5) * r.PReq, Unit = "MPa", Status = "warn",
                    Method = "S7: class B cavity pressure above 1.5 x the molding pressure wears the lands",
                    Note = "turn the gauge down to " + Si.F(r.GaugeSet_MPa) + " MPa",
                });
            }

            // (S8) load centre: A_proj-weighted centroid within +-5 % of the platen size of the platen centre
            var wsum = i.Cavities.Sum(c => c.Area + c.Perimeter * i.LandWidth + c.GrooveArea + c.WellArea);
            if (wsum > 0)
            {
                r.LoadCentreU = i.Cavities.Sum(c => (c.Area + c.Perimeter * i.LandWidth + c.GrooveArea + c.WellArea) * c.U) / wsum + i.MoldOffsetU;
                r.LoadCentreV = i.Cavities.Sum(c => (c.Area + c.Perimeter * i.LandWidth + c.GrooveArea + c.WellArea) * c.V) / wsum + i.MoldOffsetV;
                var off = cfg.D("offCentre", 0.05);
                if (i.PlatenL != null && i.PlatenW != null)
                {
                    r.Checks.Add(new JCheck { Id = "PRESS-CENTRE-U", Type = "press", Demand = Math.Abs(r.LoadCentreU), Capacity = off * i.PlatenL.Value, Unit = "mm", Method = "S8: load centre within " + off * 100 + " % of the platen length" });
                    r.Checks.Add(new JCheck { Id = "PRESS-CENTRE-V", Type = "press", Demand = Math.Abs(r.LoadCentreV), Capacity = off * i.PlatenW.Value, Unit = "mm", Method = "S8: load centre within " + off * 100 + " % of the platen width" });
                }
            }

            // (S9, D2) envelope: the 25 mm cool-edge margin applies to plates and cavities; handles are checked separately
            // (outside the platen outline or below the lid's platen face, acceptance A16).
            var edge = cfg.D("platenEdgeMargin_mm", 25);
            if (i.PlatenL != null && i.PlatenW != null && i.FootprintL != null && i.FootprintW != null)
            {
                var a = new[] { i.FootprintL.Value, i.FootprintW.Value }.OrderByDescending(x => x).ToArray();
                var p = new[] { i.PlatenL.Value, i.PlatenW.Value }.OrderByDescending(x => x).ToArray();
                r.Checks.Add(new JCheck { Id = "PRESS-PLATEN-L", Type = "press", Demand = a[0], Capacity = p[0] - 2 * edge, Unit = "mm", Method = "S9: footprint <= platen - 2 x " + edge + " mm (plates and cavities; handles stay outside the platen outline)" });
                r.Checks.Add(new JCheck { Id = "PRESS-PLATEN-W", Type = "press", Demand = a[1], Capacity = p[1] - 2 * edge, Unit = "mm", Method = "S9: footprint <= platen - 2 x " + edge + " mm" });
            }
            if (i.Daylight != null && i.StackHeight != null)
            {
                var m = cfg.D("daylightMargin_mm", 25);
                r.Checks.Add(new JCheck { Id = "PRESS-DAYLIGHT", Type = "press", Demand = i.StackHeight.Value, Capacity = i.Daylight.Value - m, Unit = "mm", Method = "S9: closed height <= daylight - " + m + " mm" });
                if (i.Stroke != null)
                {
                    // critique C6: a mold thinner than daylight - stroke cannot be clamped and needs a riser
                    var need = i.Daylight.Value - i.Stroke.Value + cfg.D("strokeMargin_mm", 5);
                    r.Checks.Add(new JCheck { Id = "PRESS-STROKE", Type = "press", Demand = i.StackHeight.Value, Capacity = need, Unit = "mm", Lower = true, Method = "C6: closed height >= daylight - stroke + 5 mm, else add a riser of the difference" });
                }
            }
            return r;
        }

        /// <summary>The page's meaning text to the enum ("Not sure - ask me" = Unknown).</summary>
        public static PressMeaning ParseMeaning(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.StartsWith("gauge")) return PressMeaning.Gauge;
            if (s.StartsWith("on the part") || s == "part" || s == "onpart" || s == "on_part") return PressMeaning.OnPart;
            if (s.StartsWith("force") || s.Contains("tons")) return PressMeaning.ForceReading;
            return PressMeaning.Unknown;
        }
    }
}

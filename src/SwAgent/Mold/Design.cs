using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>
    /// The calculator's view of a design: the record (mold.json, schema unify3d.mold/1) or a hand-written design with the
    /// same keys. Page inputs live under `inputs` as SI-like numbers or {value, unit} objects (ToolRunner payload "si"
    /// values are accepted too); the selected material rows come from `materials.part.id` / `materials.plates.id` when set,
    /// otherwise from the page's grade inputs. Every value the calculator uses is read here once, so Sizing never parses
    /// JSON. Missing optional values stay null; missing required ones throw with a message the agent can act on.
    /// </summary>
    public sealed class Design
    {
        public JObject Json;
        public MoldData Data;
        public bool Imperial = true;

        public JObject PartRow, PlateGrade, InsertGrade;
        public ProcessClass Class;
        public string ProcessType;
        public bool SteelThroughout;

        public double TempC, Cycles;
        public int Cavities;
        public double ShrinkPct, Sg, ExcessPct, OverflowPct, PotLifeMin, CureMin, DraftMinDeg;
        public double? ViscosityCp, Mooney;
        public double MoldingPressureTyp, MoldingPressureMax;
        public string ShrinkBasis;
        public double[] ShrinkPerAxisPct;

        public PressMeaning Meaning;
        public double PressValue, RatedForce_kN, RatedGauge_MPa;
        public double? RamArea_mm2, PlatenL, PlatenW, Daylight, Stroke;
        public bool SlideIn = true, FlatOnPlaten = true;

        public double? MaxPlateMass, MaxBaseMass, MaxLidMass;
        public double[] MaxPlateSize;
        public string LidLiftAt;
        public double? LidReach_cm, LidLiftsPerHour;

        // part geometry (unscaled, mm)
        public double PartVolume, PartArea, PartPerimeter, PartU, PartV, PartH;
        public double DepthBase, DepthLid, PlugHeight, CornerRadius, MaxSection;
        public double? ClosureTol, WidthTol;
        public JArray Fragile, Tolerances;

        public static Design Read(JObject design, MoldData data)
        {
            if (design == null) throw new ArgumentException("No design: pass `design` or open a mold whose record exists.");
            var d = new Design { Json = design, Data = data };
            var inp = design["inputs"] as JObject ?? new JObject();
            JToken I(params string[] keys)
            {
                foreach (var k in keys)
                {
                    var t = inp[k];
                    if (t != null && t.Type != JTokenType.Null && !(t is JObject o && o["value"] == null && o["si"] == null && o.Count > 0 && o["notSet"] != null)) return t;
                }
                return null;
            }
            double? N(params string[] keys) => Si.Num(I(keys));
            string S(params string[] keys)
            {
                var t = I(keys);
                if (t == null) return null;
                if (t is JObject o) return (string)(o["value"] ?? o["display"]);
                return t.Type == JTokenType.String ? (string)t : t.ToString();
            }

            // document units: the record's display units, else the page's, else imperial (the user's IPS template)
            var units = (string)design.SelectToken("units.display") ?? S("units", "unit_system");
            d.Imperial = units == null || !units.Trim().ToLowerInvariant().StartsWith("metric");

            // --- materials
            var partId = (string)design.SelectToken("materials.part.id");
            d.PartRow = data.PartMaterial(partId) ?? data.FindPartMaterial(S("part_grade"), S("part_family"));
            if (d.PartRow == null)
                throw new ArgumentException("Unknown part material: set materials.part.id or inputs.part_grade to a materials.json id or name (e.g. \"pu-tdi-ether-90a\").");
            d.ProcessType = (string)d.PartRow["processType"];
            d.Class = ProcessClasses.From(d.ProcessType);

            var moldMaterial = S("mold_material") ?? "Aluminium + steel inserts";
            d.SteelThroughout = moldMaterial.Trim().ToLowerInvariant().StartsWith("tool steel") || moldMaterial.Trim().ToLowerInvariant() == "steel";
            var plateId = (string)design.SelectToken("materials.plates.id");
            d.PlateGrade = data.MoldGrade(plateId) ?? data.MoldGrade(S("mold_grade")) ?? data.DefaultGrade(d.SteelThroughout ? "steel" : "aluminium", "plate");
            var insId = (string)design.SelectToken("materials.inserts.id");
            d.InsertGrade = data.MoldGrade(insId) ?? data.MoldGrade(S("insert_grade")) ?? data.DefaultGrade("steel", "insert");
            if (d.PlateGrade == null) throw new ArgumentException("Unknown plate grade: set inputs.mold_grade to a mold-materials.json id or label.");

            // --- part material values: the user's page edits win over the row (design 1.4: editable fields)
            var used = design.SelectToken("materials.part.used") as JObject ?? new JObject();
            double Row(string path, double fallback) => Si.Num(d.PartRow.SelectToken(path)) ?? fallback;
            double? RowN(string path) => Si.Num(d.PartRow.SelectToken(path));
            d.ShrinkPct = Si.Num(used["shrinkPct"]) ?? N("shrink_pct") ?? Row("shrinkPct.typ", 0);
            d.Sg = Si.Num(used["sg"]) ?? N("sg") ?? Row("sg.typ", 1.0);
            d.ViscosityCp = Si.Num(used["viscosityCp"]) ?? N("viscosity_cp") ?? RowN("viscosityCp.typ");
            d.Mooney = Si.Num(used["mooney"]) ?? N("mooney") ?? RowN("mooney.typ");
            d.PotLifeMin = Si.Num(used["potLifeMin"]) ?? N("pot_life_min") ?? RowN("potLifeMin.typ") ?? 0;
            d.CureMin = Si.Num(used["cureMin"]) ?? N("cure_min") ?? Row("demoldMin.typ", 0);
            d.DraftMinDeg = Si.Num(used["draftMinDeg"]) ?? N("draft_min_deg") ?? Row("draftDeg.min", 0.5);
            d.ExcessPct = Si.Num(used["chargeExcessPct"]) ?? N("charge_excess_pct") ?? Row("flashAllowancePct.typ", 5);
            d.OverflowPct = Row("overflowPctOfPartVolume.typ", 0);
            var mpTyp = Row("moldingPressurePsi.typ", 0) * Si.PsiToMPa;
            var mpMax = Row("moldingPressurePsi.max", 0) * Si.PsiToMPa;
            var mpUser = Si.Num(used["moldingPressure_MPa"]) ?? N("molding_pressure");
            d.MoldingPressureTyp = mpUser ?? mpTyp;
            // an edited molding pressure above the table maximum raises the class A design pressure too (C3)
            d.MoldingPressureMax = Math.Max(mpMax, mpUser ?? 0);
            d.ShrinkBasis = (string)design.SelectToken("shrink.basis") ?? S("shrink_basis") ?? (string)d.PartRow["shrinkBasisChoice"] ?? (string)d.PartRow["shrinkBasis"];
            var axis = design.SelectToken("shrink.pctAxis") ?? I("shrink_pct_axis") ?? d.PartRow["shrinkPctAxis"];
            if (axis is JObject ax && ax.HasValues)
                d.ShrinkPerAxisPct = new[] { Si.Num(ax["u"] ?? ax["x"]) ?? d.ShrinkPct, Si.Num(ax["v"] ?? ax["y"]) ?? d.ShrinkPct, Si.Num(ax["w"] ?? ax["z"]) ?? d.ShrinkPct };
            else if (axis is JArray aa && aa.Count == 3)
                d.ShrinkPerAxisPct = aa.Select(x => Si.Num(x) ?? d.ShrinkPct).ToArray();

            // --- press and process
            d.TempC = N("press_temp") ?? Si.Num(design.SelectToken("structural.inputs.tempC")) ?? Row("moldTempC.typ", 20);
            d.Cycles = N("cycle_life") ?? Si.Num(design.SelectToken("structural.inputs.cycles")) ?? 5000;
            d.Cavities = (int)Math.Round(N("cavities") ?? (design.SelectToken("layout.cavities") as JArray)?.Count ?? 1);
            if (d.Cavities < 1) d.Cavities = 1;
            d.Meaning = Press.ParseMeaning(S("press_pressure_is"));
            d.PressValue = d.Meaning == PressMeaning.ForceReading ? (N("press_force", "press_force_reading") ?? N("press_pressure") ?? 0) : (N("press_pressure") ?? 0);
            d.RatedForce_kN = N("press_rated_force") ?? 0;
            d.RatedGauge_MPa = N("press_rated_gauge") ?? 0;
            var bore = N("ram_bore");
            var rams = N("ram_count") ?? 1;
            d.RamArea_mm2 = N("ram_area_mm2", "ram_area") ?? (bore != null ? rams * Math.PI * bore.Value * bore.Value / 4 : (double?)null);
            var platen = Si.Lengths(I("platen_size"));
            if (platen != null && platen.Length >= 2) { d.PlatenL = platen[0]; d.PlatenW = platen[1]; }
            d.Daylight = N("daylight");
            d.Stroke = N("stroke");
            var handling = S("mold_handling");
            d.SlideIn = handling == null || !handling.Trim().ToLowerInvariant().StartsWith("stays");
            var support = S("mold_support");
            d.FlatOnPlaten = support == null || support.Trim().ToLowerInvariant().StartsWith("flat");

            // --- limits (null = the page's Optional box was unchecked)
            d.MaxPlateMass = N("max_plate_mass");
            d.MaxBaseMass = N("max_base_mass");
            d.MaxLidMass = N("max_lid_mass");
            d.MaxPlateSize = Si.Lengths(I("max_plate_size"));
            d.LidLiftAt = S("lid_lift_at");
            var reachMm = N("lid_reach");
            d.LidReach_cm = N("lid_reach_cm") ?? (reachMm != null ? reachMm / 10.0 : (double?)null);
            d.LidLiftsPerHour = N("lid_lifts_per_hour");

            // --- part geometry
            var part = design["part"] as JObject ?? new JObject();
            d.PartVolume = Si.Num(part["volume_mm3"]) ?? 0;
            d.PartArea = Si.Num(part["projectedArea_mm2"]) ?? 0;
            d.PartPerimeter = Si.Num(part["perimeter_mm"]) ?? 0;
            var box = Si.Lengths(part["box_mm"]);
            if (box != null && box.Length >= 3) { d.PartU = box[0]; d.PartV = box[1]; d.PartH = box[2]; }
            if (d.PartArea <= 0 && d.PartU > 0) d.PartArea = d.PartU * d.PartV;
            if (d.PartPerimeter <= 0 && d.PartU > 0) d.PartPerimeter = 2 * (d.PartU + d.PartV);
            d.DepthBase = Si.Num(part["depthBase_mm"]) ?? d.PartH;
            d.DepthLid = Si.Num(part["depthLid_mm"]) ?? Math.Max(0, d.PartH - d.DepthBase);
            d.PlugHeight = Si.Num(part["plugHeight_mm"]) ?? 0;
            d.CornerRadius = Si.Num(part["floorRadius_mm"]) ?? Si.Num(part["cornerRadius_mm"]) ?? 0;
            d.MaxSection = Si.Num(part["maxSection_mm"]) ?? Math.Min(d.PartH, Math.Min(d.PartU, d.PartV));
            d.Tolerances = part.SelectToken("drawing.tolerances") as JArray;
            d.Fragile = part["fragile"] as JArray;
            if (d.Tolerances != null)
            {
                var closure = d.Tolerances.OfType<JObject>().Where(t => (bool?)t["closure"] == true).Select(t => Si.Num(t["tol_mm"])).Where(x => x > 0).ToList();
                if (closure.Count > 0) d.ClosureTol = closure.Min();
                var width = d.Tolerances.OfType<JObject>().Where(t => (bool?)t["closure"] != true).Select(t => Si.Num(t["tol_mm"])).Where(x => x > 0).ToList();
                if (width.Count > 0) d.WidthTol = width.Min();
            }
            return d;
        }

        public bool HasGeometry => PartU > 0 && PartV > 0 && DepthBase + DepthLid > 0;

        public void RequireGeometry()
        {
            if (!HasGeometry)
                throw new ArgumentException("The part geometry is missing: run mold_part analyze (it writes part.box_mm, projectedArea_mm2, perimeter_mm and volume_mm3) or pass them in design.part.");
        }

        public void RequirePress()
        {
            if (Meaning == PressMeaning.Unknown)
                throw new ArgumentException("press_pressure_is is \"Not sure\": ask the user whether the number is a gauge reading, the pressure on the part, or a force reading, then merge it into inputs.");
            if (RamArea_mm2 == null && (RatedForce_kN <= 0 || RatedGauge_MPa <= 0) && Meaning != PressMeaning.ForceReading)
                throw new ArgumentException("The ram area is unknown: set press_rated_force and press_rated_gauge (or ram_bore).");
        }
    }
}

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
        /// <summary>The unit the user's gauge reads (the page's picked unit of press_pressure / press_rated_gauge, or
        /// inputs.gauge_unit): psi, bar, kgf/cm2 or MPa; null when not known. The setup sheet prints gauge settings in it.</summary>
        public string GaugeUnit;
        public bool PressValueGiven;
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

        /// <summary>
        /// What each page input measures, so a payload value given as {"si": x} (Track B: Pa, m, N, Pa·s, s, rad) is turned
        /// into calculator units. Plain numbers are taken as calculator units; {"value", "unit"} objects are converted by unit.
        /// </summary>
        public static readonly Dictionary<string, string> Quantities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["press_pressure"] = "pressure", ["part_pressure"] = "pressure", ["press_rated_gauge"] = "pressure", ["molding_pressure"] = "pressure",
            ["press_rated_force"] = "force", ["press_force"] = "force", ["press_force_reading"] = "force",
            ["platen_size"] = "length", ["daylight"] = "length", ["stroke"] = "length", ["max_plate_size"] = "length",
            ["lid_reach"] = "length", ["lid_reach_cm"] = "length_cm", ["ram_bore"] = "length", ["ram_area_mm2"] = "area", ["ram_area"] = "area",
            ["max_plate_mass"] = "mass", ["max_base_mass"] = "mass", ["max_lid_mass"] = "mass",
            ["press_temp"] = "temperature", ["viscosity_cp"] = "viscosity", ["pot_life_min"] = "time", ["cure_min"] = "time",
            ["draft_min_deg"] = "angle", ["shrink_pct"] = "percent", ["charge_excess_pct"] = "percent",
        };

        public static Design Read(JObject design, MoldData data)
        {
            if (design == null) throw new ArgumentException("No design: pass `design` or open a mold whose record exists.");
            var d = new Design { Json = design, Data = data };
            var inp = design["inputs"] as JObject ?? new JObject();
            // the first given input among the keys (an unchecked Optional box arrives as null and counts as not given)
            string Key(string[] keys) => keys.FirstOrDefault(k => inp[k] != null && inp[k].Type != JTokenType.Null);
            JToken I(params string[] keys)
            {
                var k = Key(keys);
                return k == null ? null : inp[k];
            }
            double? N(params string[] keys)
            {
                var k = Key(keys);
                if (k == null) return null;
                try { return Si.Num(inp[k], Quantities.TryGetValue(k, out var q) ? q : null); }
                catch (ArgumentException ex) { throw new ArgumentException("inputs." + k + " (" + inp[k].ToString(Newtonsoft.Json.Formatting.None) + "): " + ex.Message + ".", ex); }
            }
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

            // --- materials. The page inputs always win: the agent merges corrections into `inputs`, and the values the
            // calculator wrote back into the record (materials.*.id, materials.part.used, shrink.basis) are only the
            // fallback for a design that has no such input (a hand-written design, or an input the page left empty).
            var partId = (string)design.SelectToken("materials.part.id");
            var gradeIn = S("part_grade");
            if (gradeIn != null)
            {
                d.PartRow = data.FindPartMaterial(gradeIn, S("part_family"));
                if (d.PartRow == null)
                    throw new ArgumentException("Unknown part material '" + gradeIn + "': set inputs.part_grade (or materials.part.id) to a materials.json id or name (e.g. \"pu-tdi-ether-90a\").");
            }
            else d.PartRow = data.PartMaterial(partId);
            if (d.PartRow == null)
                throw new ArgumentException("Unknown part material: set materials.part.id or inputs.part_grade to a materials.json id or name (e.g. \"pu-tdi-ether-90a\").");
            d.ProcessType = (string)d.PartRow["processType"];
            d.Class = ProcessClasses.From(d.ProcessType);

            var moldMaterial = S("mold_material") ?? "Aluminium + steel inserts";
            d.SteelThroughout = moldMaterial.Trim().ToLowerInvariant().StartsWith("tool steel") || moldMaterial.Trim().ToLowerInvariant() == "steel";
            var plateIn = S("mold_grade");
            if (plateIn != null && data.MoldGrade(plateIn) == null) throw new ArgumentException("Unknown plate grade '" + plateIn + "': set inputs.mold_grade to a mold-materials.json id or label.");
            d.PlateGrade = data.MoldGrade(plateIn) ?? data.MoldGrade((string)design.SelectToken("materials.plates.id")) ?? data.DefaultGrade(d.SteelThroughout ? "steel" : "aluminium", "plate");
            var insIn = S("insert_grade");
            if (insIn != null && data.MoldGrade(insIn) == null) throw new ArgumentException("Unknown insert grade '" + insIn + "': set inputs.insert_grade to a mold-materials.json id or label.");
            d.InsertGrade = data.MoldGrade(insIn) ?? data.MoldGrade((string)design.SelectToken("materials.inserts.id")) ?? data.DefaultGrade("steel", "insert");
            if (d.PlateGrade == null) throw new ArgumentException("Unknown plate grade: set inputs.mold_grade to a mold-materials.json id or label.");

            // --- part material values: the user's page edits win over the row (design 1.4: editable fields). The record's
            // `used` block is what the last run used; it only stands in for a missing input, and only for the same material.
            var sameMaterial = partId == null || partId == (string)d.PartRow["id"];
            var used = sameMaterial ? design.SelectToken("materials.part.used") as JObject ?? new JObject() : new JObject();
            double Row(string path, double fallback) => Si.Num(d.PartRow.SelectToken(path)) ?? fallback;
            double? RowN(string path) => Si.Num(d.PartRow.SelectToken(path));
            d.ShrinkPct = N("shrink_pct") ?? Si.Num(used["shrinkPct"]) ?? Row("shrinkPct.typ", 0);
            d.Sg = N("sg") ?? Si.Num(used["sg"]) ?? Row("sg.typ", 1.0);
            d.ViscosityCp = N("viscosity_cp") ?? Si.Num(used["viscosityCp"]) ?? RowN("viscosityCp.typ");
            d.Mooney = N("mooney") ?? Si.Num(used["mooney"]) ?? RowN("mooney.typ");
            d.PotLifeMin = N("pot_life_min") ?? Si.Num(used["potLifeMin"]) ?? RowN("potLifeMin.typ") ?? 0;
            d.CureMin = N("cure_min") ?? Si.Num(used["cureMin"]) ?? Row("demoldMin.typ", 0);
            d.DraftMinDeg = N("draft_min_deg") ?? Si.Num(used["draftMinDeg"]) ?? Row("draftDeg.min", 0.5);
            d.ExcessPct = N("charge_excess_pct") ?? Si.Num(used["chargeExcessPct"]) ?? Row("flashAllowancePct.typ", 5);
            d.OverflowPct = Row("overflowPctOfPartVolume.typ", 0);
            var mpTyp = Row("moldingPressurePsi.typ", 0) * Si.PsiToMPa;
            var mpMax = Row("moldingPressurePsi.max", 0) * Si.PsiToMPa;
            var mpUser = N("molding_pressure") ?? Si.Num(used["moldingPressure_MPa"]);
            d.MoldingPressureTyp = mpUser ?? mpTyp;
            // an edited molding pressure above the table maximum raises the class A design pressure too (C3)
            d.MoldingPressureMax = Math.Max(mpMax, mpUser ?? 0);
            var recordBasis = sameMaterial ? (string)design.SelectToken("shrink.basis") : null;
            d.ShrinkBasis = S("shrink_basis") ?? recordBasis ?? (string)d.PartRow["shrinkBasisChoice"] ?? (string)d.PartRow["shrinkBasis"];
            var recordAxis = sameMaterial ? design.SelectToken("shrink.pctAxis") : null;
            var axis = I("shrink_pct_axis") ?? (recordAxis != null && recordAxis.HasValues ? recordAxis : null) ?? d.PartRow["shrinkPctAxis"];
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
            // each meaning has its own page field (the page sends only the one press_pressure_is shows): a force reading is its
            // own number (kN), never a pressure typed into the psi box (C2); "On the part" has its own field with a higher limit
            // (C1: 150 MPa against the gauge's 100 MPa). press_force / press_pressure are the older ids, still read.
            var pv = d.Meaning == PressMeaning.ForceReading ? N("press_force_reading", "press_force")
                   : d.Meaning == PressMeaning.OnPart ? N("part_pressure", "press_pressure")
                   : N("press_pressure");
            d.PressValueGiven = pv != null;
            d.PressValue = pv ?? 0;
            string UnitOf(string key) => (inp[key] as JObject)?["unit"]?.Type == JTokenType.String ? (string)inp[key]["unit"] : null;
            var gaugeUnit = S("gauge_unit") ?? UnitOf("press_pressure") ?? UnitOf("press_rated_gauge");
            if (gaugeUnit != null && new[] { "psi", "bar", "kgf/cm2", "kgf/cm²", "mpa" }.Contains(gaugeUnit.Trim().ToLowerInvariant())) d.GaugeUnit = gaugeUnit.Trim();
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
            // the depths per half: merged from the chosen parting-line candidate; else the candidate at the agreed plane
            // (partingLine.planes[0].w_mm, or partingLine.candidate by id); else the whole part in the base
            var chosen = ChosenCandidate(design);
            d.DepthBase = Si.Num(part["depthBase_mm"]) ?? Si.Num(chosen?["baseDepth_mm"]) ?? d.PartH;
            d.DepthLid = Si.Num(part["depthLid_mm"]) ?? Si.Num(chosen?["lidDepth_mm"]) ?? Math.Max(0, d.PartH - d.DepthBase);
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

        /// <summary>The parting-line candidate the plan chose: by id (partingLine.candidate / .id) or by the plane's W (0.01 mm).</summary>
        private static JObject ChosenCandidate(JObject design)
        {
            var cands = design.SelectToken("part.partingLineCandidates") as JArray;
            var pl = design["partingLine"] as JObject;
            if (cands == null || pl == null) return null;
            var id = (string)pl["candidate"] ?? (string)pl["candidateId"];
            if (id != null) return cands.OfType<JObject>().FirstOrDefault(c => string.Equals((string)c["id"], id, StringComparison.OrdinalIgnoreCase));
            var w = Si.Num(pl.SelectToken("planes[0].w_mm"));
            return w == null ? null : cands.OfType<JObject>().FirstOrDefault(c => Si.Num(c["w_mm"]) is double cw && Math.Abs(cw - w.Value) < 0.01);
        }

        public bool HasGeometry => PartU > 0 && PartV > 0 && DepthBase + DepthLid > 0;

        public void RequireGeometry()
        {
            if (!HasGeometry)
                throw new ArgumentException("The part geometry is missing: run mold_part analyze (it writes part.box_mm, projectedArea_mm2, perimeter_mm and volume_mm3) or pass them in design.part.");
        }

        /// <summary>
        /// The press numbers every press calculation needs, whatever the meaning of the user's value: the nameplate force
        /// (PRESS-CAPACITY) and the ram area (from the rated gauge, or ram_bore / ram_area), without which no gauge
        /// setting can be given (critique C2: the rating is always on the page).
        /// </summary>
        public void RequirePress()
        {
            if (Meaning == PressMeaning.Unknown)
                throw new ArgumentException("press_pressure_is is \"Not sure\": ask the user whether the number is a gauge reading, the pressure on the part, or a force reading, then merge it into inputs.");
            if (!PressValueGiven || !(PressValue > 0))
                throw new ArgumentException(Meaning == PressMeaning.ForceReading
                    ? "press_pressure_is is \"Force reading (tons)\" but inputs.press_force_reading is " + (PressValueGiven ? "not above 0" : "not set") + ": ask the user for the force the press shows (US ton, t or kN) and merge it into inputs.press_force_reading."
                    : Meaning == PressMeaning.OnPart
                    ? "press_pressure_is is \"On the part\" but inputs.part_pressure is " + (PressValueGiven ? "not above 0" : "not set") + ": ask the user for the pressure on the part and merge it into inputs.part_pressure."
                    : "press_pressure_is is \"Gauge reading\" but inputs.press_pressure is " + (PressValueGiven ? "not above 0" : "not set") + ": ask the user for the gauge setting and merge it into inputs.press_pressure.");
            if (RatedForce_kN <= 0)
                throw new ArgumentException("The press rating is unknown: set inputs.press_rated_force (the nameplate force) and inputs.press_rated_gauge (the gauge reading at that force).");
            if ((RamArea_mm2 == null || RamArea_mm2 <= 0) && RatedGauge_MPa <= 0)
                throw new ArgumentException("The ram area is unknown: set inputs.press_rated_gauge (the gauge reading at the rated force) or inputs.ram_bore (with ram_count).");
        }
    }
}

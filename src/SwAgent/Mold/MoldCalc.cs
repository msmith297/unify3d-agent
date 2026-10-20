using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>What one mold_calc call returns: the report text, the key numbers, what to merge into the record, the checks.</summary>
    public sealed class CalcResult
    {
        public string Text;
        public JObject Json;
        /// <summary>Patch for MoldRecord.Merge (null = nothing to write, e.g. lookup and split).</summary>
        public JObject RecordPatch;
        public List<JObject> Checks = new List<JObject>();
    }

    /// <summary>
    /// The pure calculator behind the mold_calc agent tool (design 2.3.2): lookup, process, press, layout, size, split and
    /// check. No SolidWorks types, deterministic: the same design, query and overrides always give the same text and JSON.
    /// Errors the agent can fix (missing press meaning, missing geometry, unknown material) come back as ArgumentException
    /// with the fix in the message.
    /// </summary>
    public static class MoldCalc
    {
        public static readonly string Version = "1.0.0";
        public static readonly string[] Actions = { "lookup", "process", "press", "layout", "size", "split", "check" };
        private const int JsonBudget = 4000;

        public static CalcResult Run(string action, JObject design, JObject query, JObject overrides, MoldData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var cfg = new Cfg(data.Defaults, overrides);
            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "lookup": return Lookup(query ?? new JObject(), data, cfg);
                case "process": return ProcessAction(design, data, cfg);
                case "press": return PressAction(design, data, cfg);
                case "layout": return LayoutAction(design, data, cfg);
                case "size": return SizeAction(design, data, cfg, false);
                case "check": return SizeAction(design, data, cfg, true);
                case "split": return SplitAction(design, data, cfg, query ?? new JObject());
                default: throw new ArgumentException("Unknown action '" + action + "'. Use one of: " + string.Join(", ", Actions) + ".");
            }
        }

        // ------------------------------------------------------------------ lookup

        private static CalcResult Lookup(JObject q, MoldData data, Cfg cfg)
        {
            var sb = new StringBuilder();
            var json = new JObject();
            if (q["part"] != null)
            {
                var row = data.FindPartMaterial((string)q["part"], (string)q["family"]) ?? throw new ArgumentException("No part material '" + q["part"] + "' (ids like pu-tdi-ether-90a, or the grade name).");
                string R3(string path)
                {
                    var t = row.SelectToken(path);
                    if (t == null || t.Type == JTokenType.Null) return "n/a";
                    if (t is JObject o && o["typ"] != null) return $"{o["typ"]} ({o["min"]}-{o["max"]})";
                    return t.ToString();
                }
                sb.AppendLine($"{row["name"]} [{row["id"]}] - {row["processLabel"]}, class {row["processClass"]}{((bool?)row["builtByV1"] == false ? " (advisory only in v1)" : "")}, confidence {row["confidence"]}");
                sb.AppendLine($"shrink {R3("shrinkPct")} % ({row["shrinkBasis"]}, basis {row["shrinkBasisConfidence"]}); SG {R3("sg")}; viscosity {R3("viscosityCp")} cP; Mooney {R3("mooney")}");
                sb.AppendLine($"pot life {R3("potLifeMin")} min; mold {R3("moldTempC")} C; demold {R3("demoldMin")} min; pressure {R3("moldingPressurePsi")} psi; draft min {R3("draftDeg.min")} deg");
                sb.AppendLine($"land {R3("flash.landWidthMm")} mm; vent {R3("flash.ventDepthMm")} mm; charge excess {R3("flashAllowancePct")} %; aluminium {row.SelectToken("moldCompat.aluminium")}; corrosive {row["corrosiveness"]}");
                sb.AppendLine("sources: " + string.Join(", ", (row["sources"] as JArray ?? new JArray()).Select(x => (string)x)));
                json = row;
            }
            else if (q["mold"] != null)
            {
                var g = data.MoldGrade((string)q["mold"]) ?? throw new ArgumentException("No mold grade '" + q["mold"] + "'.");
                var t = Si.Num(q["temp_c"] ?? q["tempC"]) ?? 20;
                var a = AtTemperature.Get(g, t);
                sb.AppendLine($"{g["name"]} [{g["id"]}] at {Si.F(t)} C (10,000 h basis): E {Si.F(a.E / 1000)} GPa, Sy {Si.F(a.Sy)} MPa, Su {Si.F(a.Su)} MPa - {a.Service}");
                if (a.Note != null) sb.AppendLine(a.Note);
                sb.AppendLine($"limits: full strength {g.SelectToken("max_service_C.full_strength")} C, derated {g.SelectToken("max_service_C.derated")} C, never above {g.SelectToken("max_service_C.never_above")} C; CTE {g["cte_per_K"]} /K; density {g["density_kg_m3"]} kg/m3; confidence {g["confidence"]}");
                json = new JObject { ["id"] = g["id"], ["tempC"] = t, ["E_GPa"] = Si.R(a.E / 1000, 4), ["Sy_MPa"] = Si.R(a.Sy, 4), ["Su_MPa"] = Si.R(a.Su, 4), ["service"] = a.Service, ["note"] = a.Note };
            }
            else if (q["fastener"] != null)
            {
                var f = data.Fastener((string)q["fastener"]) ?? throw new ArgumentException("No fastener '" + q["fastener"] + "' (e.g. 1/2-13 or M10).");
                var b = BoltSpec.From(f, cfg);
                var t = Si.Num(q["temp_c"]) ?? 20;
                var kT = Bolts.HotProofFactor(t, cfg);
                sb.AppendLine($"{b.Size} {b.Spec}: A_t {Si.F(b.At)} mm², proof {Si.F(b.Fp / 1000)} kN (hot x{Si.F(kT)} at {Si.F(t)} C), Su {Si.F(b.Su)} MPa");
                sb.AppendLine($"torque per 0.10 F_p: dry {Si.F(0.2 * b.D * 0.1 * b.Fp / 1000)} N·m; scatter +-{Si.F(cfg.D("bolts.tighteningScatter", 0.25) * 100)} % (A1)");
                sb.AppendLine($"cbore {Si.F(b.CboreDia)} x {Si.F(b.CboreDepth)} mm; with washer: cbore {Si.F(b.WasherOd + 0.5)} mm; tap drill {Si.F(b.TapDrill)} mm; clearance {Si.F(b.ClearanceHole)} mm");
                json = f;
            }
            else if (q["stock"] != null)
            {
                var s = data.Raw("mold-materials.json")?["stock_thickness"]?[(string)q["stock"]] as JObject ?? throw new ArgumentException("No stock list '" + q["stock"] + "'.");
                sb.AppendLine((string)q["stock"] + ": inch " + s["inch"]?.ToString(Formatting.None) + "; metric " + s["metric_mm"]?.ToString(Formatting.None));
                json = s;
            }
            else if (q["family"] != null)
            {
                var f = (string)q["family"];
                var rows = data.PartMaterials.Where(r => string.Equals((string)r["family"], f, StringComparison.OrdinalIgnoreCase) || string.Equals((string)r["familyLabel"], f, StringComparison.OrdinalIgnoreCase) || string.Equals((string)r["familyShort"], f, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var r in rows) sb.AppendLine($"{r["short"]} [{r["id"]}] {((bool?)r["isDefault"] == true ? "(default) " : "")}- {r["confidence"]}");
                json = new JObject { ["grades"] = new JArray(rows.Select(r => (string)r["id"])) };
            }
            else throw new ArgumentException("lookup needs one of: {part}, {mold, temp_c}, {fastener}, {stock}, {family}.");
            return new CalcResult { Text = sb.ToString().TrimEnd(), Json = json };
        }

        // ------------------------------------------------------------------ process / press / layout

        private static CalcResult ProcessAction(JObject design, MoldData data, Cfg cfg)
        {
            var r = Sizing.ProcessOnly(design, data, cfg);
            var d = r.Design;
            var sx = r.Shrink;
            var ch = r.Charge;
            var u = new Fmt(d.Imperial);
            var sb = Header("process", r);
            sb.AppendLine($"Shrink {Si.F(d.ShrinkPct)} % on the {sx.BasisLabel}: scale {sx.Su:0.000000}{(sx.Sv != sx.Su || sx.Sw != sx.Su ? $" / {sx.Sv:0.000000} / {sx.Sw:0.000000}" : "")} (effective {sx.EffectivePct:0.000} %) -> feature \"{sx.FeatureName}\"");
            sb.AppendLine($"Charge {u.Grams(ch.PerCavity_g)} per cavity, shot {u.Grams(ch.Shot_g)} (+{Si.F(ch.HoldUpPct)} % hold-up), tolerance +-{Si.F(cfg.D("charge.tolerancePct", 2))} %");
            sb.AppendLine($"Overflow needed {Si.F(ch.OverflowRequired_mm3 / 1000)} cm³" + (ch.GrooveRadius_mm > 0 ? $"; groove radius {u.Len(ch.GrooveRadius_mm)}" : ""));
            if (d.Class == ProcessClass.A_Pour)
                sb.AppendLine($"Pour {Si.F(ch.PourSecondsPerCavity)} s per cavity, {Si.F(ch.PourSecondsTotal)} s for {d.Cavities} against {Si.F(ch.PourLimitSeconds)} s (half the pot life)");
            sb.AppendLine($"Cure {Si.F(ch.CureMin)} min - {ch.CureNote}");
            AppendChecks(sb, r.Checks, u);
            AppendNotes(sb, r.Notes);
            var json = new JObject { ["process"] = r.Process, ["shrink"] = r.ShrinkJson };
            var patch = new JObject
            {
                ["process"] = r.Process.DeepClone(), ["shrink"] = r.ShrinkJson.DeepClone(),
                ["materials"] = new JObject { ["part"] = PartMaterialJson(d) },
                ["checks"] = new JArray(r.Checks),
            };
            return Done(sb, json, patch, r.Checks);
        }

        private static CalcResult PressAction(JObject design, MoldData data, Cfg cfg)
        {
            var r = Sizing.PressOnly(design, data, cfg);
            var d = r.Design;
            var p = r.Press;
            var u = new Fmt(d.Imperial);
            var sb = Header("press", r);
            sb.AppendLine($"Ram area {u.Area(p.RamArea_mm2)}; press force {u.Force(p.Force_N / 1000)} ({d.Meaning})");
            sb.AppendLine($"Projected area {u.Area(p.AProj_mm2)}" + (p.AProjForce_mm2 > p.AProj_mm2 ? $" (force area {u.Area(p.AProjForce_mm2)} with grooves/wells, A9)" : ""));
            sb.AppendLine($"Cavity pressure p_LC1 {u.P(p.PLC1)}" + (d.Class == ProcessClass.A_Pour ? $"; abuse F/A_proj {u.P(p.PLC1x)}" : ""));
            sb.AppendLine($"Force needed {u.Force(p.FReq_N / 1000)} -> recommended gauge {u.P(p.GaugeSet_MPa)}" + (r.GaugeMax_MPa != null ? $"; maximum {u.P(r.GaugeMax_MPa.Value)} ({r.GaugeMaxBasis})" : ""));
            if (d.Meaning == PressMeaning.OnPart) sb.AppendLine($"Your {u.P(d.PressValue)} on the part needs a gauge of {u.P(p.GaugeForInput_MPa)}");
            AppendChecks(sb, r.Checks, u);
            AppendNotes(sb, r.Notes);
            var loadCases = new JObject
            {
                ["LC1"] = new JObject { ["p_MPa"] = Si.R(p.PLC1, 5) },
                ["LC1x"] = d.Class == ProcessClass.A_Pour ? new JObject { ["p_MPa"] = Si.R(p.PLC1x, 5) } : null,
                ["LC2"] = new JObject { ["F_kN"] = Si.R(p.Force_N / 1000, 5) },
            };
            var patch = new JObject
            {
                ["process"] = new JObject { ["gauge"] = r.PressJson["gauge"].DeepClone(), ["press"] = r.PressJson.DeepClone() },
                ["structural"] = new JObject { ["loadCases"] = loadCases },
                ["checks"] = new JArray(r.Checks),
            };
            return Done(sb, r.PressJson, patch, r.Checks);
        }

        private static CalcResult LayoutAction(JObject design, MoldData data, Cfg cfg)
        {
            var r = Sizing.LayoutOnly(design, data, cfg);
            var l = r.Layout;
            var u = new Fmt(r.Design.Imperial);
            var sb = Header("layout", r);
            sb.AppendLine($"Pattern {l.Pattern} (columns x rows), footprint {u.Len(l.FootprintL)} x {u.Len(l.FootprintW)}, moats {l.Moats}");
            sb.AppendLine($"Gaps {u.Len(l.GapU)} / {u.Len(l.GapV)}; edge steel {u.Len(l.EdgeU)} / {u.Len(l.EdgeV)} (flash geometry only - size widens them if the walls need it)");
            foreach (var c in l.Cavities) sb.AppendLine($"  {c.Id} at U {u.Len(c.U)}, V {u.Len(c.V)}");
            AppendNotes(sb, r.Notes);
            var json = l.ToJson();
            return Done(sb, json, new JObject { ["layout"] = json.DeepClone() }, r.Checks);
        }

        // ------------------------------------------------------------------ size / check

        private static CalcResult SizeAction(JObject design, MoldData data, Cfg cfg, bool asBuilt)
        {
            var r = Sizing.Size(design, data, cfg, asBuilt);
            var d = r.Design;
            var u = new Fmt(d.Imperial);
            var sb = Header(asBuilt ? "check" : "size", r);
            var s = r.Structural;
            if (r.Press != null)
                sb.AppendLine($"Press: force {u.Force(r.Press.Force_N / 1000)}, p_LC1 {u.P((double)(s.SelectToken("loadCases.LC1.p_MPa") ?? 0))}, recommended gauge {u.P(r.Press.GaugeSet_MPa)}" + (r.GaugeMax_MPa != null ? $", max {u.P(r.GaugeMax_MPa.Value)} ({r.GaugeMaxBasis})" : ""));
            if (r.Gap != null)
                sb.AppendLine($"Gap budget ({r.Gap.FlowClass}): lands {u.Len(r.Gap.DeltaGap)} ({r.Gap.GapBasis}); walls {u.Len(r.Gap.DeltaWall)} ({r.Gap.WallBasis})");
            foreach (var w in (s.SelectToken("results.walls") as JArray ?? new JArray()).OfType<JObject>())
                sb.AppendLine($"Wall {w["id"]} (L/h {w["LoverH"]}): structural {u.Len((double)w["w_struct_mm"])}, drawn {u.Len((double)w["w_drawn_mm"])} - {w["governs"]}; sigma {Si.F((double)w["sigma_MPa"])} of {Si.F((double)w["allow_MPa"])} MPa");
            foreach (var pc in (s.SelectToken("results.pieces") as JArray ?? new JArray()).OfType<JObject>())
                sb.AppendLine($"{pc["id"]}: {u.Len((double)pc["box_mm"][0])} x {u.Len((double)pc["box_mm"][1])} x {u.Len((double)pc["t_mm"])} (stock {u.Len((double)pc["stock_mm"])}), {u.Mass((double)pc["mass_kg"])}");
            var lid = s.SelectToken("results.lid");
            if (lid != null) sb.AppendLine($"Lid limit {u.Mass((double)lid["limit_kg"])} ({lid["limitBasis"]}); lifting index {lid["liftingIndex"]}");
            var bear = s.SelectToken("results.bearing");
            if (bear != null) sb.AppendLine($"Bearing {Si.F((double)bear["stress_MPa"])} of {Si.F((double)bear["allow_MPa"])} MPa on {u.Area((double)bear["area_mm2"])} (D_b {bear["Db"]})");
            foreach (var b in (r.Hardware["bolts"] as JArray ?? new JArray()).OfType<JObject>())
                sb.AppendLine($"Bolts {b["joint"]}: {b["count"]} x {b["size"]} at {b["preloadFraction"]} F_p, torque dry {Si.F((double)b["torqueDry_Nm"])} N·m (preload {b["preloadBand_kN"][0]}-{b["preloadBand_kN"][1]} kN){(b["insert"].Type == JTokenType.Null ? "" : ", " + b["insert"])}");
            sb.AppendLine($"Dowels: {r.Hardware.SelectToken("dowelScheme.scheme")}");
            AppendChecks(sb, r.Checks, u);
            AppendNotes(sb, r.Notes);
            var json = new JObject
            {
                ["class"] = ProcessClasses.Letter(d.Class), ["advisory"] = r.Advisory,
                ["gauge"] = s["gauge"]?.DeepClone(), ["gap"] = s["inputs"]?.DeepClone(),
                ["walls"] = s.SelectToken("results.walls")?.DeepClone(), ["pieces"] = s.SelectToken("results.pieces")?.DeepClone(),
                ["bearing"] = s.SelectToken("results.bearing")?.DeepClone(), ["lid"] = s.SelectToken("results.lid")?.DeepClone(),
                ["layout"] = s.SelectToken("results.layout")?.DeepClone(),
                ["bolts"] = r.Hardware["bolts"]?.DeepClone(),
                ["failing"] = new JArray(r.Checks.Where(Check.Fails).Select(c => (string)c["id"])),
            };
            var patch = new JObject
            {
                ["structural"] = s.DeepClone(), ["hardware"] = r.Hardware.DeepClone(),
                ["process"] = new JObject { ["gauge"] = s["gauge"]?.DeepClone() },
                ["checks"] = new JArray(r.Checks.Select(c => { var x = (JObject)c.DeepClone(); if (asBuilt) x["stage"] = "5"; return x; })),
            };
            if (!asBuilt && r.Layout != null) patch["layout"] = r.Layout.ToJson();
            if (asBuilt) patch["structural"]["checkedAsBuilt"] = true;
            return Done(sb, json, patch, r.Checks);
        }

        // ------------------------------------------------------------------ split (S74)

        private static CalcResult SplitAction(JObject design, MoldData data, Cfg cfg, JObject q)
        {
            var r = Sizing.Prelude(design, data, cfg);
            Sizing.Process(r, cfg, data);
            var d = r.Design;
            var u = new Fmt(d.Imperial);
            var plate = (string)q["plate"] ?? "base";
            var pieces = design.SelectToken("structural.results.pieces") as JArray;
            var piece = pieces?.OfType<JObject>().FirstOrDefault(p => string.Equals((string)p["role"], plate, StringComparison.OrdinalIgnoreCase) || string.Equals((string)p["id"], plate, StringComparison.OrdinalIgnoreCase));
            var box = Si.Lengths(q["box_mm"]) ?? Si.Lengths(piece?["box_mm"]);
            if (box == null) throw new ArgumentException("split needs the plate size: run mold_calc size first, or pass query.box_mm [L, W, T].");
            var pu = d.PartU * r.Shrink.Su;
            var pv = d.PartV * r.Shrink.Sv;
            var depth = (Si.Num(q["depth_mm"]) ?? d.DepthBase) * r.Shrink.Sw;
            var input = new SplitInput
            {
                PlateId = plate.ToUpperInvariant() == "BASE" ? "BASE" : plate.ToUpperInvariant(), L = box[0], W = box[1], T = box[2],
                Density_kg_m3 = (double?)d.PlateGrade["density_kg_m3"] ?? 2700, MaxMass_kg = d.MaxPlateMass, MaxBox = d.MaxPlateSize,
                AllowLW = data.ShopMm("plates.blankAllowancePerSide", 3), AllowT = data.ShopMm("plates.blankAllowanceThicknessPerSide", 1.5),
                CavityClearance = cfg.D("splitClearanceFromCavity_mm", 15),
                SubplateGrade = data.DefaultGrade((string)d.PlateGrade["family"], "backing") ?? d.PlateGrade,
                SubplateMinPlate = cfg.D((string)d.PlateGrade["family"] == "aluminium" ? "minPlate_mm.aluminium" : "minPlate_mm.steel", 12),
                KeyStep = cfg.D("keys.minStep_mm", 9.5),
                SubplateMinFloor = cfg.D((string)d.PlateGrade["family"] == "aluminium" ? "minFloor_mm.aluminium" : "minFloor_mm.steel", 12),
                Imperial = d.Imperial,
            };
            // the subplate defaults to the plate grade (a 'backing' grade like MIC-6 when the agent passes it)
            if (q["subplate_grade"] != null) input.SubplateGrade = data.MoldGrade((string)q["subplate_grade"]) ?? input.SubplateGrade;
            else input.SubplateGrade = d.PlateGrade;
            if (d.Class == ProcessClass.A_Pour)
            {
                input.FlashBand = data.ShopMm("flash.lip", 3.175) + data.ShopMm("flash.grooveWidth", 12.7);
                input.FeatureClearance = data.ShopMm("flash.featureClearance", 6.35);
            }
            var cavs = q["cavities"] as JArray ?? design.SelectToken("layout.cavities") as JArray;
            if (cavs == null) throw new ArgumentException("split needs the cavity layout: run mold_calc layout or size first.");
            foreach (var c in cavs.OfType<JObject>())
            {
                var o = c["origin"] as JArray;
                input.Pockets.Add(new Pocket
                {
                    Id = (string)c["id"], U = Si.Num(o?[0]) ?? 0, V = Si.Num(o?[1]) ?? 0,
                    SizeU = Si.Num(c["size_mm"]?[0]) ?? pu, SizeV = Si.Num(c["size_mm"]?[1]) ?? pv, Depth = Si.Num(c["depth_mm"]) ?? depth,
                });
            }
            var opts = PlateSplit.Options(input, data);
            var sb = new StringBuilder();
            sb.AppendLine($"mold_calc split {Version} - {input.PlateId} {u.Len(input.L)} x {u.Len(input.W)} x {u.Len(input.T)}, limit {(input.MaxMass_kg == null ? "none" : u.Mass(input.MaxMass_kg.Value))}");
            if (opts.Count == 0)
                sb.AppendLine($"No straight split in dead steel meets the limits. A cut needs {u.Len(2 * Math.Max(input.CavityClearance, input.FlashBand + input.FeatureClearance))} of steel between cavity rows or columns: re-run size with overrides {{\"gap_mm\": {Si.F(2 * Math.Max(input.CavityClearance, input.FlashBand + input.FeatureClearance) + 6, 3)}}}, or propose a frame plus cavity inserts, a lighter grade, or a hoist.");
            var k = 0;
            foreach (var o in opts)
            {
                k++;
                sb.AppendLine($"Option {k}: {o.Pieces.Count} pieces (cuts U {string.Join(", ", o.CutsU.Select(x => u.Len(x)))}{(o.CutsV.Count > 0 ? "; V " + string.Join(", ", o.CutsV.Select(x => u.Len(x))) : "")}), heaviest {u.Mass(o.Heaviest)}");
                foreach (var p in o.Pieces) sb.AppendLine($"  {p.Id}: {u.Len(p.Box[0])} x {u.Len(p.Box[1])}, {u.Mass(p.Mass)} ({string.Join(" ", p.Cavities)})");
                sb.AppendLine($"  keyed subplate {u.Len(o.SubplateT)} thick: {o.SubPieces.Count} piece(s), heaviest {u.Mass(o.SubPieces.Max(p => p.Mass))}{(o.SubplateOk ? "" : " - BREAKS THE LIMITS")}{(string.IsNullOrEmpty(o.Note) ? "" : " - " + o.Note)}");
            }
            sb.AppendLine("Merge the chosen option as structural.split, then run size again (keys and bolts are sized for it).");
            var json = new JObject { ["options"] = new JArray(opts.Select(o => o.ToJson())) };
            return Done(sb, json, null, new List<JObject>());
        }

        // ------------------------------------------------------------------ text helpers

        private static StringBuilder Header(string action, SizingResult r)
        {
            var d = r.Design;
            var sb = new StringBuilder();
            sb.Append($"mold_calc {action} {Version} - class {ProcessClasses.Letter(d.Class)} ({d.PartRow["processLabel"]}), {d.PartRow["short"]} [{d.PartRow["id"]}, confidence {d.PartRow["confidence"]}]; ");
            sb.AppendLine($"{r.Plate.Short} at {Si.F(d.TempC)} C ({r.Plate.Service}), N = {d.Cycles.ToString("0", CultureInfo.InvariantCulture)}, {d.Cavities} cavit{(d.Cavities == 1 ? "y" : "ies")}");
            if (r.Advisory) sb.AppendLine("ADVISORY: not built by this tool in v1 (A10) - numbers for planning only.");
            return sb;
        }

        private static void AppendChecks(StringBuilder sb, List<JObject> checks, Fmt u)
        {
            if (checks.Count == 0) return;
            var fails = checks.Count(Check.Fails);
            sb.AppendLine($"Checks, lowest margins first ({fails} fail, {checks.Count(c => (string)c["status"] == "warn")} warn, {checks.Count} total):");
            foreach (var c in checks.Take(40))
            {
                var m = c["margin"]?.Type == JTokenType.Null || c["margin"] == null ? "" : $" margin {(double)c["margin"] * 100:0.#}%";
                var dc = c["demand"] == null || c["demand"].Type == JTokenType.Null ? "" : $" {Si.F((double)c["demand"], 4)} vs {Si.F((double)c["capacity"], 4)} {c["unit"]}";
                sb.Append($"  {((string)c["status"]).ToUpperInvariant(),-5} {c["id"]}{dc}{m} - {c["method"]}");
                var note = (string)c["note"];
                if (!string.IsNullOrEmpty(note)) sb.Append(" [" + note + "]");
                if (Check.Fails(c)) sb.Append(" -> " + Fix(c));
                sb.AppendLine();
            }
            if (checks.Count > 40) sb.AppendLine($"  ... {checks.Count - 40} more passing checks in the record");
        }

        /// <summary>The action that fixes a failing check (the report says it next to the check).</summary>
        public static string Fix(JObject c)
        {
            var id = (string)c["id"];
            var type = (string)c["type"];
            if (id.StartsWith("WEIGHT-LID")) return "lightening pockets on the lid's platen face (keep >= 80% platen contact), a lift assist or hinge, or a two-person lift; never a split lid for class A";
            if (type == "weight" || type == "box") return "run mold_calc split and offer the options";
            if (id.StartsWith("TEMP-")) return "switch to steel (P20) or lower the press temperature";
            if (id == "PRESS-CAPACITY") return "a larger press, fewer cavities, or a lower molding pressure";
            if (id == "PRESS-FORCE") return "raise the gauge to the recommended setting";
            if (id.StartsWith("PRESS-CENTRE")) return "re-centre the layout or add the locating stop (E4)";
            if (id.StartsWith("PRESS-PLATEN")) return "fewer cavities or a smaller footprint";
            if (id == "PRESS-DAYLIGHT") return "thinner plates or a press with more daylight";
            if (id == "PRESS-STROKE") return "add a riser under the mold";
            if (id == "PRESS-GAUGE-MAX") return "more bearing area, a stronger joint, or steel";
            if (id.StartsWith("WALL-")) return "a wider wall, a steel insert or a wall keyed into the base, or P20";
            if (id.StartsWith("BOLT") || id.StartsWith("BOLTS")) return "the next size or more bolts (re-run size), or a keyed joint";
            if (id.StartsWith("KEY-")) return "a longer or deeper key step, or steel";
            if (id.StartsWith("STR-BEAR")) return "more bearing area (stops), or a lower maximum gauge";
            if (id.StartsWith("PIN-")) return "make the feature a steel insert, shorten it, or guide its tip";
            if (id.StartsWith("FIT-")) return "a larger pocket ring or a lighter interference with screws";
            if (id.StartsWith("THREAD")) return "a steel thread insert";
            if (id.StartsWith("SPAN")) return "more support (rails, pillars) or steel";
            if (id.StartsWith("CHG-POUR")) return "pour in batches or a recorded sequence; fewer cavities per shot";
            if (id.StartsWith("CHG-OVERFLOW")) return "a larger groove, a second groove or spill pockets";
            return "see the method";
        }

        private static void AppendNotes(StringBuilder sb, List<string> notes)
        {
            foreach (var n in notes.Distinct()) sb.AppendLine("Note: " + n);
        }

        private static CalcResult Done(StringBuilder sb, JObject json, JObject patch, List<JObject> checks)
        {
            var compact = json.ToString(Formatting.None);
            if (compact.Length > JsonBudget)
            {
                // keep the key numbers inside the 4,000-character block: drop the bulkiest arrays first
                var trimmed = (JObject)json.DeepClone();
                foreach (var key in new[] { "walls", "layout", "bolts", "pieces", "options" })
                    if (trimmed.ToString(Formatting.None).Length > JsonBudget && trimmed[key] != null) trimmed[key] = "(in the record)";
                compact = trimmed.ToString(Formatting.None);
                if (compact.Length > JsonBudget) compact = compact.Substring(0, JsonBudget - 3) + "...";
            }
            sb.AppendLine("```json");
            sb.AppendLine(compact);
            sb.AppendLine("```");
            return new CalcResult { Text = sb.ToString(), Json = json, RecordPatch = patch, Checks = checks };
        }

        private static JObject PartMaterialJson(Design d) => new JObject
        {
            ["id"] = d.PartRow["id"],
            ["used"] = new JObject
            {
                ["shrinkPct"] = d.ShrinkPct, ["shrinkBasis"] = d.ShrinkBasis, ["sg"] = d.Sg, ["viscosityCp"] = d.ViscosityCp, ["mooney"] = d.Mooney,
                ["potLifeMin"] = d.PotLifeMin, ["cureMin"] = d.CureMin, ["draftMinDeg"] = d.DraftMinDeg,
                ["moldingPressure_MPa"] = Si.R(d.MoldingPressureTyp, 5), ["chargeExcessPct"] = d.ExcessPct,
            },
            ["confidence"] = d.PartRow["confidence"], ["sources"] = d.PartRow["sources"]?.DeepClone(),
            ["rheology"] = new JObject { ["status"] = "unknown" }, ["kinetics"] = new JObject { ["status"] = "unknown" },
            ["thermal"] = new JObject { ["status"] = "unknown" }, ["mechanical"] = new JObject { ["status"] = "unknown" },
        };
    }

    /// <summary>Dual-unit formatting for reports: the document's system first, SI in brackets (design unit conventions).</summary>
    public sealed class Fmt
    {
        private readonly bool _imp;
        public Fmt(bool imperial) { _imp = imperial; }

        private static string N(double v, int d = 4) => Si.F(v, d);
        public string P(double mpa) => _imp ? $"{N(mpa / Si.PsiToMPa)} psi ({N(mpa)} MPa)" : $"{N(mpa)} MPa ({N(mpa / Si.PsiToMPa)} psi)";
        public string Len(double mm) => _imp ? $"{N(mm / Si.InToMm)} in ({N(mm)} mm)" : $"{N(mm)} mm";
        public string Area(double mm2) => _imp ? $"{N(mm2 / (Si.InToMm * Si.InToMm))} in² ({N(mm2)} mm²)" : $"{N(mm2)} mm²";
        public string Mass(double kg) => _imp ? $"{N(kg / Si.LbToKg)} lb ({N(kg)} kg)" : $"{N(kg)} kg ({N(kg / Si.LbToKg)} lb)";
        public string Grams(double g) => _imp ? $"{N(g)} g ({N(g / 453.59237)} lb)" : $"{N(g)} g";
        public string Force(double kN) => _imp ? $"{N(kN / Si.UsTonToKN)} US ton ({N(kN)} kN)" : $"{N(kN)} kN ({N(kN / Si.TonneForceToKN)} t)";
        public string Temp(double c) => _imp ? $"{N(Si.CToF(c))} degF ({N(c)} degC)" : $"{N(c)} degC";
    }
}

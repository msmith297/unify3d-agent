using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>
    /// Fills data/setup-sheet.html (Track E's template) from the record and writes setup-sheet.html into the record folder
    /// (design 1.5.6, design-rules 23, critique A1.6, A2, D1, E3, E4, E6). The template sees the whole record plus a derived
    /// `sheet` object with the operator's numbers already put together (dual units through {{x|unit}}):
    ///   sheet.moldId, sheet.title, sheet.display, sheet.materialName, sheet.confidence, sheet.hazards
    ///   sheet.pieces[] {id, role, mass_kg, lift, box_mm}
    ///   sheet.press {setPointC, gaugeRecommended_MPa, gaugeMax_MPa, gaugeMaxBasis, conversion, closedHeight_mm, loadCentre}
    ///   sheet.partialLoading[] {active, gauge_MPa, patterns, example, note}; sheet.neverFewerThan
    ///   sheet.charge {perCavity_g, shot_g, tolerancePct}; sheet.pour {secondsPerCavity, secondsTotal, limitSeconds}
    ///   sheet.cure {minutes, note}; sheet.postCure; sheet.bolts[] {joint, count, size, torqueDry_Nm, torqueLubed_Nm, preloadLow_kN, preloadHigh_kN, insert}
    ///   sheet.dowelScheme; sheet.lid {mass_kg, limit_kg, liftingIndex, basis}; sheet.slideIn[] (lines)
    ///   sheet.preheat; sheet.maintenance[] (lines); sheet.warnings[] {id, status, note}; sheet.firstArticle[] (lines)
    /// </summary>
    public static class SetupSheet
    {
        public const string OutputName = "setup-sheet.html";

        /// <summary>Writes the filled sheet; returns its path. Unresolved placeholders render as "-" (see Fill).</summary>
        public static string Write(MoldRecord r, string templatePath, MoldData data) => Write(r, templatePath, data, out _);

        /// <summary>Writes the filled sheet and lists the placeholders it could not fill (mold_record reports them).</summary>
        public static string Write(MoldRecord r, string templatePath, MoldData data, out List<string> unresolved)
        {
            var html = Fill(r, File.ReadAllText(templatePath), data, out unresolved);
            var path = Path.Combine(r.Folder, OutputName);
            File.WriteAllText(path, html, new UTF8Encoding(false));
            r.Json["setupSheet"] = OutputName;
            r.Save();
            return path;
        }

        public static string Fill(MoldRecord r, string template, MoldData data, out List<string> unresolved)
        {
            unresolved = new List<string>();
            var ctx = (JObject)r.Json.DeepClone();
            ctx["sheet"] = Build(r.Json, data);
            var imperial = (string)r.Json.SelectToken("units.display") != "metric";
            return Template.Render(template, ctx, imperial, unresolved);
        }

        /// <summary>
        /// Bodies that are machined pieces of the mold: not the molded part's reference copy, the Garbage folder or bodies in no
        /// mold folder (Track D's roles reference, garbage, unfiled).
        /// </summary>
        public static bool Machined(JObject body)
        {
            var role = ((string)body?["role"] ?? "").Trim().ToLowerInvariant();
            return role != "reference" && role != "garbage" && role != "unfiled";
        }

        /// <summary>The derived numbers the operator needs, from the record (no recalculation of the design).</summary>
        public static JObject Build(JObject j, MoldData data)
        {
            var row = data?.PartMaterial((string)j.SelectToken("materials.part.id"));
            var cls = (string)j.SelectToken("process.class");
            var pieces = new JArray();
            var asBuilt = j["bodies"] as JArray;
            var planned = j.SelectToken("structural.results.pieces") as JArray;
            var lifts = (j.SelectToken("hardware.lift") as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            foreach (var b in ((asBuilt != null && asBuilt.Count > 0) ? asBuilt : planned ?? new JArray()).OfType<JObject>().Where(Machined))
            {
                var lift = lifts.FirstOrDefault(l => (string)l["body"] == (string)b["id"]);
                pieces.Add(new JObject
                {
                    ["id"] = b["id"], ["role"] = b["role"], ["mass_kg"] = b["mass_kg"], ["box_mm"] = b["box_mm"],
                    ["lift"] = (string)lift?["kind"] ?? ((double?)b["mass_kg"] > 23 ? "hoist" : "by hand"),
                });
            }
            var bolts = new JArray();
            foreach (var b in (j.SelectToken("hardware.bolts") as JArray ?? new JArray()).OfType<JObject>())
                bolts.Add(new JObject
                {
                    ["joint"] = b["joint"], ["count"] = b["count"], ["size"] = b["size"], ["spec"] = b["spec"],
                    ["torqueDry_Nm"] = b["torqueDry_Nm"], ["torqueLubed_Nm"] = b["torqueLubed_Nm"],
                    ["preloadLow_kN"] = b.SelectToken("preloadBand_kN[0]"), ["preloadHigh_kN"] = b.SelectToken("preloadBand_kN[1]"),
                    ["insert"] = b["insert"] ?? "", ["washer"] = b["washer"], ["retorque"] = b["retorque"],
                });
            var gRec = Si.Num(j.SelectToken("process.gauge.recommended_MPa"));
            var ram = Si.Num(j.SelectToken("process.press.ramArea_mm2"));
            var conversion = gRec != null && ram != null
                ? $"gauge x ram area {Si.F(ram.Value / 645.16, 4)} in² ({Si.F(ram.Value, 4)} mm²) = force {Si.F(gRec.Value * ram.Value / 1000, 4)} kN"
                : "";
            var tier = (string)j.SelectToken("structural.inputs.tier") ?? "T2";
            var warnings = new JArray();
            foreach (var c in Check.Ordered((j["checks"] as JArray ?? new JArray()).OfType<JObject>().Where(c => (string)c["status"] == "fail" || (string)c["status"] == "warn")).Take(12))
                warnings.Add(new JObject { ["id"] = c["id"], ["status"] = c["status"], ["note"] = string.IsNullOrEmpty((string)c["note"]) ? c["method"] : c["note"] });
            var slideIn = new JArray();
            if (j.SelectToken("hardware.slideIn") is JObject si && si.HasValues)
            {
                slideIn.Add("Slide the mold in until it touches the locating stop: it then sits centred under the ram (load centre, S8).");
                slideIn.Add("Pull the base out by its pull handles; nothing stands proud of the base's underside; bottom edges are chamfered.");
                slideIn.Add("Handles stay outside the platen outline or below the lid's platen face (A16).");
            }
            var cureNote = cls == "A"
                ? "Datasheet demold time at the press temperature; no temperature scaling for liquid systems (E6). Below the material's minimum mold temperature it under-cures."
                : "Cure time halves for about every 10 C (18 F) rise, within the material's range (rubber rule); thick sections need extra heat-up time.";
            var maint = new JArray
            {
                "Blow out the moat, runners and vents every cycle.",
                tier == "T1" ? "Check dowels and lands before each batch." : tier == "T2" ? "Every 1,000 cycles: check dowels, slots and land flatness; re-torque bolts hot after the first 2-3 heat cycles." : "Every 5,000 cycles: inspect lands, stops, pins and threads for wear; lap lands back to flatness; replace stop shims.",
                "Count cycles against the cycle-life target (" + (j.SelectToken("structural.inputs.cycles") ?? "?") + ").",
            };
            var lid = j.SelectToken("structural.results.lid") as JObject;
            var tcWells = (j.SelectToken("hardware.tcWells") as JArray)?.OfType<JObject>().Select(t => (string)t["id"]).ToList() ?? new List<string>();
            return new JObject
            {
                ["moldId"] = j["id"],
                ["title"] = (string)j.SelectToken("identity.moldNumber") ?? (string)j.SelectToken("inputs.mold_number") ?? Path.GetFileNameWithoutExtension((string)j.SelectToken("files.moldPart") ?? "mold"),
                ["display"] = (string)j.SelectToken("units.display") ?? "imperial",
                ["materialName"] = (string)row?["name"] ?? (string)j.SelectToken("materials.part.id"),
                ["materialId"] = j.SelectToken("materials.part.id"),
                ["confidence"] = (string)row?["confidence"] ?? (string)j.SelectToken("materials.part.confidence"),
                ["hazards"] = (string)row?["hazards"] ?? "",
                ["releaseAgent"] = (string)row?["releaseAgent"] ?? "",
                ["processLabel"] = (string)row?["processLabel"] ?? (string)j.SelectToken("process.processLabel"),
                ["pieces"] = pieces,
                ["press"] = new JObject
                {
                    ["setPointC"] = j.SelectToken("structural.inputs.tempC") ?? j.SelectToken("inputs.press_temp.value") ?? j.SelectToken("inputs.press_temp"),
                    ["gaugeRecommended_MPa"] = j.SelectToken("process.gauge.recommended_MPa"),
                    ["gaugeMax_MPa"] = j.SelectToken("process.gauge.max_MPa"),
                    ["gaugeMaxBasis"] = j.SelectToken("process.gauge.maxBasis") ?? "",
                    ["gaugeUnit"] = j.SelectToken("process.gauge.unitShown") ?? "",
                    ["conversion"] = conversion,
                    ["closedHeight_mm"] = j.SelectToken("structural.results.stackHeight_mm"),
                    ["loadCentre"] = "Mark the press centre on the base; the locating stop puts the cavity centroid on it.",
                },
                ["partialLoading"] = j.SelectToken("structural.results.partialLoading.rows")?.DeepClone() ?? new JArray(),
                ["neverFewerThan"] = j.SelectToken("structural.results.partialLoading.neverFewerThan"),
                ["charge"] = new JObject
                {
                    ["perCavity_g"] = j.SelectToken("process.charge.perCavity_g"), ["shot_g"] = j.SelectToken("process.charge.shot_g"),
                    ["tolerancePct"] = j.SelectToken("process.charge.tolerancePct") ?? 2,
                },
                ["pour"] = j.SelectToken("process.pour")?.DeepClone() ?? new JObject(),
                ["cure"] = new JObject { ["minutes"] = j.SelectToken("process.cureMin"), ["note"] = cureNote, ["calc"] = j.SelectToken("process.cureNote") ?? "" },
                ["postCure"] = j.SelectToken("process.postCure")?.DeepClone() ?? row?["postCure"]?.DeepClone(),
                ["bolts"] = bolts,
                ["dowelScheme"] = j.SelectToken("hardware.dowelScheme.scheme") ?? "",
                ["lid"] = new JObject
                {
                    ["mass_kg"] = lid?["mass_kg"], ["limit_kg"] = lid?["limit_kg"], ["liftingIndex"] = lid?["liftingIndex"], ["basis"] = lid?["limitBasis"] ?? "",
                },
                ["slideIn"] = slideIn,
                ["preheat"] = "Preheat until " + (tcWells.Count > 0 ? string.Join(" and ", tcWells) : "the thermocouples") + " read within 3 C of the set point, then 10 more minutes.",
                ["maintenance"] = maint,
                ["warnings"] = warnings,
                ["firstArticle"] = new JArray(
                    "Measure the critical dimensions against the drawing; steel-safe dimensions are recut after this.",
                    "Check flash thickness at the parting line and the vents.",
                    "Record the shrink actually measured and save it as the shop value (mold_record save_default)."),
            };
        }
    }
}

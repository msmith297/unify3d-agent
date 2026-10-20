using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>
    /// Per-body machining sheet (critique E1): STEP files carry no fits, flatness, finish or thread data, so the values that
    /// make the mold work go to the shop on this sheet. Fills data/machining-sheet.html with the record plus `machining`:
    ///   machining.bodies[] {id, role, material, finished_mm[3], stock_mm, blank_mm[3], flatness[] {face, tol_mm},
    ///                       holes[] {id, kind, size, qty, dia_mm, depth_mm, fit, limits, note}, fits[] {...}, notes[]}
    ///   machining.general[] (lines: default tolerance, corner reliefs, shut-off recess, lapping)
    ///   machining.bom[] - the purchased hardware (HardwareBom.Build)
    /// </summary>
    public static class MachiningSheet
    {
        public const string OutputName = "machining-sheet.html";

        public static string Write(MoldRecord r, string templatePath, MoldData data)
        {
            var html = Fill(r, File.ReadAllText(templatePath), data, out _);
            var path = Path.Combine(r.Folder, OutputName);
            File.WriteAllText(path, html, new UTF8Encoding(false));
            HardwareBom.WriteCsv(r, data);
            r.Json["machiningSheet"] = OutputName;
            r.Save();
            return path;
        }

        public static string Fill(MoldRecord r, string template, MoldData data, out List<string> unresolved)
        {
            unresolved = new List<string>();
            var ctx = (JObject)r.Json.DeepClone();
            ctx["machining"] = Build(r.Json, data);
            var imperial = (string)r.Json.SelectToken("units.display") != "metric";
            return Template.Render(template, ctx, imperial, unresolved);
        }

        public static JObject Build(JObject j, MoldData data)
        {
            var imperial = (string)j.SelectToken("units.display") != "metric";
            var cls = (string)j.SelectToken("process.class") ?? "A";
            var landFlat = Si.Num(j.SelectToken("structural.inputs.landFlatness_mm")) ?? 0.01;
            var platenFlat = data.ShopMm("machining.platenFaceFlatness", 0.05);
            var subParallel = data.ShopMm("machining.subplateParallelism", 0.02);
            var allowLW = data.ShopMm("plates.blankAllowancePerSide", 3);
            var allowT = data.ShopMm("plates.blankAllowanceThicknessPerSide", 1.5);
            var planned = (j.SelectToken("structural.results.pieces") as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var built = (j["bodies"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var source = built.Count > 0 ? built : planned;
            var dowels = (j.SelectToken("hardware.dowels") as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var bolts = (j.SelectToken("hardware.bolts") as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var tcs = (j.SelectToken("hardware.tcWells") as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var fits = (j.SelectToken("hardware.fits") as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var dowelRow = DowelRow(data, dowels.Select(d => Si.Num(d["d_mm"])).FirstOrDefault(x => x != null) ?? 12.7, imperial);
            var bodies = new JArray();
            foreach (var b in source)
            {
                var id = (string)b["id"];
                var role = (string)b["role"] ?? (id.StartsWith("LID") ? "lid" : id.StartsWith("SUB") ? "subplate" : id.StartsWith("INS") ? "insert" : "base");
                var plan = planned.FirstOrDefault(p => (string)p["id"] == id);
                var box = Si.Lengths(b["box_mm"]) ?? new double[3];
                var stock = Si.Num(plan?["stock_mm"]) ?? (box.Length > 2 ? box[2] + 2 * allowT : 0);
                var holes = new JArray();
                var reg = Region(plan ?? b);
                foreach (var d in dowels.Where(x => role == "base" || role == "lid" ? Inside(reg, x["at"] as JArray) : false))
                {
                    var round = ((string)d["kind"] ?? "round") == "round";
                    var pressHere = role == "base";   // pressed in the plate that stays put, slip in the one that comes apart
                    holes.Add(new JObject
                    {
                        ["id"] = d["id"], ["kind"] = round ? "dowel" : "dowel slot (radial)", ["qty"] = 1, ["dia_mm"] = d["d_mm"],
                        ["fit"] = pressHere ? "press (aluminium, 150 C)" : (round ? "slip" : "slot: slip across, free along the line to D1"),
                        ["limits"] = pressHere ? dowelRow?["press"] : dowelRow?["slip"],
                        ["depth_mm"] = Si.R(1.5 * (Si.Num(d["d_mm"]) ?? 12.7), 4), ["note"] = d["at"]?.ToString(Newtonsoft.Json.Formatting.None),
                    });
                }
                foreach (var bt in bolts)
                {
                    // "SUB/BASE-1": tapped in BASE-1, counterbored in the subplate piece(s) under it
                    var joint = ((string)bt["joint"] ?? "").Split('/');
                    var tappedIn = joint.Last();
                    var under = planned.FirstOrDefault(p => (string)p["id"] == tappedIn);
                    var top = role == "subplate" && joint[0] == "SUB" && Overlaps(reg, Region(under));
                    if (!top && id != tappedIn) continue;
                    holes.Add(new JObject
                    {
                        ["id"] = bt["id"], ["kind"] = top ? "counterbored clearance (hardened washer)" : (bt["insert"].Type == JTokenType.Null ? "tapped" : "tapped for insert (STI tap)"),
                        ["qty"] = bt["count"], ["size"] = bt["size"],
                        ["dia_mm"] = top ? bt["cboreForWasher_mm"] : null,
                        ["depth_mm"] = top ? null : bt["tapDepth_mm"],
                        ["fit"] = top ? "counterbore = washer OD + 0.5 mm (E2)" : "thread engagement " + bt["engagement_mm"] + " mm + 0.5 d relief",
                        ["limits"] = "", ["note"] = bt["insert"].Type == JTokenType.Null ? "" : (string)bt["insert"],
                    });
                }
                foreach (var t in tcs.Where(x => (string)x["body"] == id))
                    holes.Add(new JObject
                    {
                        ["id"] = t["id"], ["kind"] = "thermocouple well (from a side face)", ["qty"] = 1, ["dia_mm"] = t["d_mm"], ["depth_mm"] = t["depth_mm"],
                        ["fit"] = "", ["limits"] = "", ["note"] = "ends " + t["endFromCavity_mm"] + " mm from the cavity; engrave the label",
                    });
                var flat = new JArray();
                if (role == "base" || role == "lid" || role == "middle")
                {
                    flat.Add(new JObject { ["face"] = "parting face / lands", ["tol_mm"] = Si.R(landFlat, 3), ["note"] = cls == "A" ? "lap flat (gap budget A11)" : "ground (gap budget A11)" });
                    flat.Add(new JObject { ["face"] = "platen face", ["tol_mm"] = platenFlat, ["note"] = "flat on the platen, no insulating gaps" });
                    flat.Add(new JObject { ["face"] = "parallelism platen face to parting face", ["tol_mm"] = Si.R(Math.Max(landFlat, 0.02), 3), ["note"] = "" });
                }
                if (role == "subplate") flat.Add(new JObject { ["face"] = "both faces parallel", ["tol_mm"] = subParallel, ["note"] = "pieces sit on it" });
                var notes = new JArray();
                foreach (var f in fits.Where(x => (x["bodies"] as JArray)?.Any(y => (string)y == id) == true))
                    notes.Add($"Insert pocket {f["id"]}: d {f["d_mm"]} mm, cold interference {f["coldInterference_mm"]} mm ({f["pocketHot_mm"]}); {f["retention"]}; {f["cornerRelief"]}");
                bodies.Add(new JObject
                {
                    ["id"] = id, ["role"] = role, ["material"] = b["material"] ?? j.SelectToken("structural.materials.plates.id"),
                    ["finished_mm"] = new JArray(box.Select(x => Si.R(x, 6))), ["stock_mm"] = Si.R(stock, 6),
                    ["blank_mm"] = box.Length >= 3 ? new JArray(Si.R(box[0] + 2 * allowLW, 6), Si.R(box[1] + 2 * allowLW, 6), Si.R(stock + 2 * allowT, 6)) : new JArray(),
                    ["flatness"] = flat, ["holes"] = holes, ["notes"] = notes,
                    ["finish"] = role == "base" || role == "lid" ? (string)j.SelectToken("part.drawing.finish") ?? (string)data.Shop.SelectToken("machining.finishDefault") : "as machined",
                });
            }
            var recess = data.Shop.SelectToken("machining.shutOffRecess.mm");
            return new JObject
            {
                ["bodies"] = bodies,
                ["general"] = new JArray(
                    "Default tolerance +-" + Si.F(data.ShopMm("machining.defaultTolerance", 0.125)) + " mm unless stated.",
                    "Inside corners: R >= 1.1 x the radius of the cutter that reaches that depth, or dog-bone reliefs at insert pockets (B7); inserts get matching corner chamfers.",
                    "Shut-off faces and pins sit " + (recess?.ToString(Newtonsoft.Json.Formatting.None) ?? "[0.01, 0.03]") + " mm below the land, never proud (A12).",
                    "Tapped holes: engagement plus a 0.5 d unthreaded relief at the bottom; STI taps for helical inserts.",
                    "One fillet or chamfer per body; seam edges between base pieces are not filleted."),
                ["bom"] = HardwareBom.Build(j, data),
            };
        }

        /// <summary>A piece's footprint {u0, u1, v0, v1} in the mold frame (null = the whole mold).</summary>
        private static double[] Region(JObject piece)
        {
            var u = Si.Lengths(piece?["u_mm"]);
            var v = Si.Lengths(piece?["v_mm"]);
            return u == null || v == null ? null : new[] { u[0], u[1], v[0], v[1] };
        }

        private static bool Inside(double[] reg, JArray at) =>
            reg == null || at == null || at.Count < 2 || ((double)at[0] >= reg[0] - 1e-6 && (double)at[0] <= reg[1] + 1e-6 && (double)at[1] >= reg[2] - 1e-6 && (double)at[1] <= reg[3] + 1e-6);

        private static bool Overlaps(double[] a, double[] b) =>
            a == null || b == null || (a[0] < b[1] - 1e-6 && b[0] < a[1] - 1e-6 && a[2] < b[3] - 1e-6 && b[2] < a[3] - 1e-6);

        private static JObject DowelRow(MoldData data, double dMm, bool imperial)
        {
            var dw = data.Raw("mold-materials.json")?["dowels"];
            if (imperial)
            {
                var row = (dw?.SelectToken("inch_hardened_ground.sizes") as JArray)?.OfType<JObject>().OrderBy(r => Math.Abs((double)r["d_in"] * Si.InToMm - dMm)).FirstOrDefault();
                if (row == null) return null;
                return new JObject
                {
                    ["slip"] = "hole " + Lim(row["slip_hole_in"], "in"), ["press"] = "hole " + Lim(row["press_hole_aluminium_150C_in"], "in"), ["pin"] = Lim(row["standard_pin_in"], "in"),
                };
            }
            var m = (dw?.SelectToken("metric_hardened.sizes") as JArray)?.OfType<JObject>().OrderBy(r => Math.Abs((double)r["d_mm"] - dMm)).FirstOrDefault();
            if (m == null) return null;
            return new JObject { ["slip"] = "hole " + Lim(m["slip_hole_mm"], "mm"), ["press"] = "hole " + Lim(m["press_hole_aluminium_150C_mm"], "mm"), ["pin"] = "m6" };
        }

        private static string Lim(JToken t, string unit) => t is JArray a && a.Count == 2
            ? ((double)a[0]).ToString("0.0000", CultureInfo.InvariantCulture) + " - " + ((double)a[1]).ToString("0.0000", CultureInfo.InvariantCulture) + " " + unit
            : "";
    }

    /// <summary>
    /// The purchased-hardware list (critique E2): screws in stock lengths (never bottoming out: tapped depth >= engagement +
    /// 0.5 d), thread inserts with their STI taps, hardened washers, dowels, handles. Written as hardware-bom.csv next to
    /// mold.json and included in the machining sheet.
    /// </summary>
    public static class HardwareBom
    {
        public const string OutputName = "hardware-bom.csv";

        public static JArray Build(JObject j, MoldData data)
        {
            var bom = new JArray();
            var imperial = (string)j.SelectToken("units.display") != "metric";
            foreach (var b in (j.SelectToken("hardware.bolts") as JArray ?? new JArray()).OfType<JObject>())
            {
                var len = Si.Num(b["stockLength_mm"]);
                var size = (string)b["size"];
                bom.Add(Item("SHCS " + b["spec"], size + (len == null ? " x (length: grip + engagement)" : " x " + LenText(len.Value, imperial)), (int?)b["count"] ?? 0, "joint " + b["joint"] + "; torque dry " + b["torqueDry_Nm"] + " N·m"));
                bom.Add(Item("Hardened washer " + b["washer"], size, (int?)b["count"] ?? 0, "counterbore " + b["cboreForWasher_mm"] + " mm"));
                if (b["insert"] != null && b["insert"].Type != JTokenType.Null)
                {
                    var sti = StiDrill(data, size);
                    bom.Add(Item("Thread insert (" + b["insert"] + ")", size, (int?)b["count"] ?? 0, "STI tap " + size + (sti == null ? "" : ", STI drill " + sti)));
                }
            }
            var dowels = (j.SelectToken("hardware.dowels") as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (dowels.Count > 0)
            {
                var d = Si.Num(dowels[0]["d_mm"]) ?? 12.7;
                var len = Math.Ceiling(3 * d / (imperial ? 6.35 : 5)) * (imperial ? 6.35 : 5);
                bom.Add(Item(imperial ? "Dowel pin, hardened ground (ASME B18.8.2)" : "Dowel pin, hardened m6 (ISO 8734)", LenText(d, imperial) + " x " + LenText(len, imperial), dowels.Count, (string)j.SelectToken("hardware.dowelScheme.scheme") ?? ""));
            }
            foreach (var l in (j.SelectToken("hardware.lift") as JArray ?? new JArray()).OfType<JObject>())
            {
                var kind = (string)l["kind"] ?? "";
                if (kind.Contains("handles")) bom.Add(Item("Handle (heat-insulated) on steel thread inserts", "", 2, "body " + l["body"] + "; outside the platen outline or below the lid's platen face"));
                if (kind.Contains("hoist")) bom.Add(Item("Swivel hoist ring (DIN 580 rated)", "", 2, "body " + l["body"] + ": size from the piece mass"));
            }
            foreach (var s in (j.SelectToken("hardware.stops") as JArray ?? new JArray()).OfType<JObject>())
                bom.Add(Item("Stop pad, hardened " + s["hardness"], s["side_mm"] + " mm square", 1, (string)s["at"] + "; shim to set the closed height"));
            return bom;
        }

        private static JObject Item(string item, string size, int qty, string note) => new JObject { ["item"] = item, ["size"] = size, ["qty"] = qty, ["note"] = note };

        private static string LenText(double mm, bool imperial) => imperial ? Si.F(mm / Si.InToMm, 4) + " in" : Si.F(mm, 4) + " mm";

        private static string StiDrill(MoldData data, string size)
        {
            var hel = data.Raw("mold-materials.json")?.SelectToken("inserts.helical_wire_free_running");
            var inch = (hel?["inch"] as JArray)?.OfType<JObject>().FirstOrDefault(r => (string)r["thread"] == size);
            if (inch != null) return (string)inch["sti_drill"];
            var met = (hel?["metric"] as JArray)?.OfType<JObject>().FirstOrDefault(r => ((string)r["thread"]).StartsWith(size + "x"));
            return met == null ? null : (string)met["sti_drill_mm"] + " mm";
        }

        public static string WriteCsv(MoldRecord r, MoldData data)
        {
            var rows = Build(r.Json, data);
            var sb = new StringBuilder("item,size,qty,note\r\n");
            foreach (var x in rows.OfType<JObject>())
                sb.Append(Csv((string)x["item"])).Append(',').Append(Csv((string)x["size"])).Append(',').Append((int)x["qty"]).Append(',').Append(Csv((string)x["note"])).Append("\r\n");
            var path = Path.Combine(r.Folder, OutputName);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        private static string Csv(string s) => s == null ? "" : (s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s);
    }
}

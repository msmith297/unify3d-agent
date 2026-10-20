using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>
    /// The design record mold.json (schema unify3d.mold/1, design 4.3) - the state that outlives the chat and the manifest
    /// a simulator reads. No SolidWorks calls: the caller (mold_record) passes the record folder and the part path, sets the
    /// part's custom properties and registers checkpoints.
    ///
    /// Merge rules (design 2.3.5): objects merge key by key, arrays of objects that carry an `id` merge by id, any other
    /// array or value is replaced; `updatedAt` is set and the record is validated. Unknown keys are kept (consumers must
    /// ignore what they do not know).
    /// </summary>
    public sealed class MoldRecord
    {
        public const string Schema = "unify3d.mold/1";
        public const string FileName = "mold.json";
        public static readonly string[] StatusValues = { "planning", "sizing", "building", "verifying", "done" };
        public static readonly string[] SimulationStatus = { "not_run", "running", "done", "failed" };
        public static readonly string[] StageStatus = { "pending", "running", "in_progress", "done", "failed", "skipped" };
        public static readonly string[] Subfolders = { "part", Path.Combine("exports", "bodies"), Path.Combine("exports", "part"), "checks" };

        /// <summary>Arrays whose items must carry a string id (merges match on it).</summary>
        public static readonly string[] IdArrays =
        {
            "checks", "simulations", "stages", "decisions", "bodies", "airTraps", "vents", "layout.cavities", "partingLine.planes",
            "hardware.bolts", "hardware.dowels", "hardware.fits", "hardware.stops", "hardware.tcWells", "part.fragile",
            "part.criticalFaces", "part.drawing.tolerances",
        };

        /// <summary>Clock for createdAt/updatedAt (tests pin it).</summary>
        internal static Func<DateTime> Clock = () => DateTime.UtcNow;

        public string Folder { get; private set; }
        public JObject Json { get; private set; }
        public string Id => (string)Json["id"];
        public string FilePath => System.IO.Path.Combine(Folder, FileName);

        private MoldRecord() { }

        /// <summary>
        /// Creates the record folder (with part\, exports\bodies\, exports\part\, checks\) and a new record for the mold part.
        /// `inputs` are the page values (SI-like, as ToolRunner passes them). Throws when a record already exists there.
        /// </summary>
        public static MoldRecord Init(string folder, string moldPartPath, JObject inputs, string toolVersion, string addinVersion)
        {
            if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("The record folder is required (<mold file name>.mold next to the part).");
            if (File.Exists(System.IO.Path.Combine(folder, FileName)))
                throw new InvalidOperationException("A design record already exists in " + folder + ": open it (mold_record read) or archive it before starting over.");
            Directory.CreateDirectory(folder);
            foreach (var s in Subfolders) Directory.CreateDirectory(System.IO.Path.Combine(folder, s));
            var now = Stamp();
            var display = (string)inputs?.SelectToken("units") ?? (string)inputs?.SelectToken("units.display");
            var rec = new JObject
            {
                ["schema"] = Schema,
                ["id"] = Guid.NewGuid().ToString(),
                ["tool"] = new JObject { ["name"] = MoldData.ToolName, ["version"] = toolVersion ?? "1.0.0", ["addin"] = addinVersion ?? "", ["calculator"] = MoldCalc.Version },
                ["status"] = "planning",
                ["createdAt"] = now,
                ["updatedAt"] = now,
                ["units"] = new JObject
                {
                    ["length"] = "mm", ["angle"] = "deg", ["pressure"] = "MPa", ["temperature"] = "degC", ["mass"] = "kg", ["force"] = "kN",
                    ["viscosity"] = "cP", ["time"] = "min", ["display"] = display != null && display.Trim().ToLowerInvariant().StartsWith("metric") ? "metric" : "imperial",
                },
                ["files"] = new JObject { ["moldPart"] = moldPartPath, ["moldedPart"] = null, ["drawings"] = new JArray() },
                ["inputs"] = inputs != null ? inputs.DeepClone() : new JObject(),
                // F2: what the fill, cure and demold simulators will need; "unknown" until a source fills them
                ["materials"] = new JObject
                {
                    ["part"] = new JObject
                    {
                        ["rheology"] = new JObject { ["status"] = "unknown", ["note"] = "viscosity vs time and temperature" },
                        ["kinetics"] = new JObject { ["status"] = "unknown", ["note"] = "gel time and cure kinetics" },
                        ["thermal"] = new JObject { ["status"] = "unknown", ["note"] = "conductivity, heat capacity, density of the part material" },
                        ["mechanical"] = new JObject { ["status"] = "unknown", ["note"] = "modulus or hardness, friction or release for demolding" },
                    },
                },
                ["process"] = new JObject(),
                ["frame"] = new JObject(),
                ["shrink"] = new JObject(),
                ["part"] = new JObject(),
                ["partingLine"] = new JObject(),
                ["airTraps"] = new JArray(),
                ["vents"] = new JArray(),
                ["layout"] = new JObject(),
                ["bodies"] = new JArray(),
                ["hardware"] = new JObject(),
                ["structural"] = new JObject(),
                ["checks"] = new JArray(),
                ["simulations"] = new JArray(),
                ["stages"] = new JArray(),
                ["decisions"] = new JArray(),
                ["setupSheet"] = null,
            };
            var r = new MoldRecord { Folder = folder, Json = rec };
            r.Save();
            return r;
        }

        public static bool Exists(string folder) => !string.IsNullOrEmpty(folder) && File.Exists(System.IO.Path.Combine(folder, FileName));

        public static MoldRecord Open(string folder)
        {
            var path = System.IO.Path.Combine(folder ?? "", FileName);
            if (!File.Exists(path)) throw new FileNotFoundException("No design record (mold.json) in " + folder + ": run mold_record init first.", path);
            JObject json;
            try { json = Si.ParseObject(File.ReadAllText(path)); }
            catch (JsonException ex) { throw new InvalidDataException("mold.json in " + folder + " is not valid JSON: " + ex.Message, ex); }
            return new MoldRecord { Folder = folder, Json = json };
        }

        /// <summary>Writes mold.json atomically (temp file + replace) so a crash never leaves half a record.</summary>
        public void Save()
        {
            Directory.CreateDirectory(Folder);
            var path = FilePath;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.ToString(Formatting.Indented), new UTF8Encoding(false));
            // a virus scanner or the search indexer briefly holds a file that was just written ("Unable to remove the file
            // to be replaced"): retry, then fall back to an overwrite copy (the temp file still holds the full record)
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 8)
                {
                    System.Threading.Thread.Sleep(25 * (attempt + 1));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    File.Copy(tmp, path, true);
                    try { File.Delete(tmp); } catch (IOException) { }
                    return;
                }
            }
        }

        /// <summary>One top-level section (or a dotted path) as JSON; null when absent.</summary>
        public JToken Section(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return Json;
            return Json.SelectToken(key);
        }

        /// <summary>Deep-merges a patch, sets updatedAt, validates; returns a short status line ("merged; 2 checks fail: ...").</summary>
        public string Merge(JObject patch)
        {
            if (patch == null || !patch.HasValues) return "nothing to merge";
            var p = (JObject)patch.DeepClone();
            // the identity of a record never changes through a merge
            p.Remove("schema");
            p.Remove("id");
            p.Remove("createdAt");
            DeepMerge(Json, p);
            Json["updatedAt"] = Stamp();
            var issues = Validate();
            var checks = (Json["checks"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var fails = checks.Where(c => (string)c["status"] == "fail").ToList();
            var warns = checks.Count(c => (string)c["status"] == "warn");
            var sb = new StringBuilder("merged");
            sb.Append("; status ").Append((string)Json["status"]);
            if (fails.Count > 0) sb.Append("; ").Append(fails.Count).Append(fails.Count == 1 ? " check fails: " : " checks fail: ").Append(string.Join(", ", fails.Take(6).Select(c => (string)c["id"]))).Append(fails.Count > 6 ? ", ..." : "");
            if (warns > 0) sb.Append("; ").Append(warns).Append(" warn");
            sb.Append(issues.Count == 0 ? "; valid" : "; " + issues.Count + " validation issue(s): " + string.Join("; ", issues.Take(3)));
            return sb.ToString();
        }

        /// <summary>Patch directive: dotted paths whose current value is dropped before the merge, so the patch replaces it.</summary>
        public const string ReplaceKey = "$replace";
        /// <summary>Patch directive: check groups (Check.Group, or "all") whose earlier mold_calc checks are dropped first.</summary>
        public const string ReplaceChecksKey = "$replaceChecks";

        /// <summary>
        /// The record's merge: objects by key, id-carrying object arrays by id, everything else replaced; an empty array
        /// replaces (clears) the list. Two top-level directives give calculator-owned data replace semantics, so items a
        /// later run no longer produces (a removed cavity, an abandoned split's bolts, a check that now passes and is no
        /// longer emitted) do not linger: "$replace": ["layout.cavities", ...] drops those paths first, and
        /// "$replaceChecks": ["press", ...] drops the earlier mold_calc checks of those groups. Neither key is stored.
        /// </summary>
        public static void DeepMerge(JObject target, JObject patch)
        {
            if (patch[ReplaceKey] != null || patch[ReplaceChecksKey] != null)
            {
                patch = (JObject)patch.DeepClone();
                if (patch[ReplaceKey] is JArray rp)
                    foreach (var p in rp.Select(x => (string)x).Where(x => !string.IsNullOrWhiteSpace(x)))
                        RemovePath(target, p);
                if (patch[ReplaceChecksKey] is JArray groups && target["checks"] is JArray checks)
                {
                    var gs = new HashSet<string>(groups.Select(x => (string)x), StringComparer.OrdinalIgnoreCase);
                    foreach (var c in checks.OfType<JObject>().ToList())
                    {
                        var src = (string)c["source"] ?? "";
                        if (src.StartsWith("mold_calc@", StringComparison.Ordinal) && (gs.Contains("all") || gs.Contains(Check.Group((string)c["id"]))))
                            c.Remove();
                    }
                }
                patch.Remove(ReplaceKey);
                patch.Remove(ReplaceChecksKey);
            }
            MergeCore(target, patch);
        }

        private static void RemovePath(JObject target, string path)
        {
            var parts = path.Split('.');
            JObject o = target;
            for (var i = 0; i < parts.Length - 1 && o != null; i++) o = o[parts[i]] as JObject;
            o?.Remove(parts[parts.Length - 1]);
        }

        private static void MergeCore(JObject target, JObject patch)
        {
            foreach (var prop in patch.Properties())
            {
                var cur = target[prop.Name];
                var val = prop.Value;
                if (cur is JObject co && val is JObject vo) MergeCore(co, vo);
                else if (cur is JArray ca && val is JArray va && va.Count > 0 && IsIdArray(va) && IsIdArray(ca)) MergeById(ca, va);
                else target[prop.Name] = val.DeepClone();
            }
        }

        private static bool IsIdArray(JArray a) => a.Count > 0 ? a.All(x => x is JObject o && o["id"]?.Type == JTokenType.String) : true;

        private static void MergeById(JArray target, JArray patch)
        {
            foreach (var item in patch.OfType<JObject>())
            {
                var id = (string)item["id"];
                var match = target.OfType<JObject>().FirstOrDefault(t => (string)t["id"] == id);
                if (match != null) MergeCore(match, item);
                else target.Add(item.DeepClone());
            }
        }

        /// <summary>Hand-written validation (the add-in takes no schema library): returns the problems, empty when valid.</summary>
        public List<string> Validate()
        {
            var e = new List<string>();
            var j = Json;
            if ((string)j["schema"] != Schema) e.Add("schema must be " + Schema);
            if (!Guid.TryParse((string)j["id"], out _)) e.Add("id must be a GUID");
            var st = (string)j["status"];
            if (!StatusValues.Contains(st)) e.Add("status must be one of " + string.Join(", ", StatusValues));
            foreach (var k in new[] { "createdAt", "updatedAt" })
                if (j[k]?.Type != JTokenType.String && j[k]?.Type != JTokenType.Date) e.Add(k + " must be an ISO-8601 time");
            if (!(j["units"] is JObject u) || (string)u["length"] != "mm" || (string)u["pressure"] != "MPa") e.Add("units must declare mm and MPa (the record is SI-like)");
            if (!(j["tool"] is JObject)) e.Add("tool {name, version} is required");
            foreach (var path in IdArrays)
            {
                var t = j.SelectToken(path);
                if (t == null || t.Type == JTokenType.Null) continue;
                if (!(t is JArray a)) { e.Add(path + " must be an array"); continue; }
                var ids = new HashSet<string>();
                for (var i = 0; i < a.Count; i++)
                {
                    if (!(a[i] is JObject o)) { e.Add(path + "[" + i + "] must be an object"); continue; }
                    var id = o["id"]?.Type == JTokenType.String ? (string)o["id"] : null;
                    if (string.IsNullOrEmpty(id)) e.Add(path + "[" + i + "] needs a string id");
                    else if (!ids.Add(id)) e.Add(path + " repeats id " + id);
                }
            }
            if (j["checks"] is JArray checks)
                foreach (var c in checks.OfType<JObject>())
                {
                    var id = (string)c["id"] ?? "?";
                    if (!Check.Statuses.Contains((string)c["status"])) e.Add("checks." + id + ": status must be one of " + string.Join(", ", Check.Statuses));
                    if (c["type"] != null && !Check.Types.Contains((string)c["type"])) e.Add("checks." + id + ": unknown type " + c["type"]);
                    if (c["margin"] != null && c["margin"].Type != JTokenType.Null && c["margin"].Type != JTokenType.Float && c["margin"].Type != JTokenType.Integer) e.Add("checks." + id + ": margin must be a number or null");
                    if ((string)c["status"] == "waived" && string.IsNullOrWhiteSpace((string)c["waivedBy"])) e.Add("checks." + id + ": a waived check needs waivedBy (only the user can waive)");
                }
            if (j["simulations"] is JArray sims)
                foreach (var s in sims.OfType<JObject>())
                {
                    if (!SimulationStatus.Contains((string)s["status"])) e.Add("simulations." + s["id"] + ": status must be one of " + string.Join(", ", SimulationStatus));
                    var hook = (string)s["hook"];
                    if (hook != null && hook != "H1" && hook != "H2" && hook != "H3") e.Add("simulations." + s["id"] + ": hook must be H1, H2 or H3");
                }
            if (j["stages"] is JArray stages)
                foreach (var s in stages.OfType<JObject>())
                    if (s["status"] != null && !StageStatus.Contains((string)s["status"])) e.Add("stages." + s["id"] + ": status must be one of " + string.Join(", ", StageStatus));
            var scale = j.SelectToken("shrink.scale") as JArray;
            if (scale != null && (scale.Count != 3 || scale.Any(x => x.Type != JTokenType.Float && x.Type != JTokenType.Integer || (double)x <= 0.5 || (double)x >= 1.5)))
                e.Add("shrink.scale must be three factors near 1");
            return e;
        }

        /// <summary>
        /// The phase-start summary (design 1.3 "State"): status, stages, key numbers, failing and warning checks, recent
        /// decisions and simulator status, within maxChars. The agent re-reads it at every phase instead of trusting memory.
        /// </summary>
        public string Summary(int maxChars = 6000)
        {
            var j = Json;
            var sb = new StringBuilder();
            var imperial = (string)j.SelectToken("units.display") != "metric";
            var u = new Fmt(imperial);
            sb.AppendLine($"Mold record {j["id"]} - status {j["status"]}, updated {j["updatedAt"]}, tool {j.SelectToken("tool.version")}");
            sb.AppendLine("Mold part: " + j.SelectToken("files.moldPart") + (j.SelectToken("files.moldedPart.path") != null ? "; molded part: " + j.SelectToken("files.moldedPart.path") : ""));
            var part = j.SelectToken("materials.part");
            if (part?["id"] != null)
                sb.AppendLine($"Part material: {part["id"]} (confidence {part["confidence"]}); used {part["used"]?.ToString(Formatting.None)}");
            if (j.SelectToken("process.class") != null)
                sb.AppendLine($"Process: class {j.SelectToken("process.class")} ({j.SelectToken("process.processLabel")}), mold type {j.SelectToken("process.moldType")}");
            var plates = j.SelectToken("materials.plates.id") ?? j.SelectToken("structural.materials.plates.id");
            if (plates != null) sb.AppendLine("Plates: " + plates + " " + (j.SelectToken("structural.materials.plates.service") ?? ""));
            if (j.SelectToken("shrink.scale") is JArray sc)
                sb.AppendLine($"Shrink: {j.SelectToken("shrink.pct")} % ({j.SelectToken("shrink.basisLabel") ?? j.SelectToken("shrink.basis")}) scale {sc.ToString(Formatting.None)}; feature {j.SelectToken("shrink.feature")}");
            if (j.SelectToken("process.charge.perCavity_g") != null)
                sb.AppendLine($"Charge: {j.SelectToken("process.charge.perCavity_g")} g per cavity, {j.SelectToken("process.charge.shot_g")} g per shot; cure {j.SelectToken("process.cureMin")} min");
            var gRec = Si.Num(j.SelectToken("process.gauge.recommended_MPa"));
            var gMax = Si.Num(j.SelectToken("process.gauge.max_MPa"));
            if (gRec != null) sb.AppendLine($"Gauge: recommended {u.P(gRec.Value)}" + (gMax != null ? $", max {u.P(gMax.Value)} ({j.SelectToken("process.gauge.maxBasis")})" : ""));
            if (j.SelectToken("layout.footprint_mm") is JArray fp && fp.Count == 2)
                sb.AppendLine($"Layout: {j.SelectToken("layout.pattern")}, footprint {u.Len((double)fp[0])} x {u.Len((double)fp[1])}, {((j.SelectToken("layout.cavities") as JArray)?.Count ?? 0)} cavities");
            if (j.SelectToken("structural.results.pieces") is JArray pcs)
                foreach (var p in pcs.OfType<JObject>().Take(8))
                    sb.AppendLine($"  {p["id"]}: t {u.Len((double?)p["t_mm"] ?? 0)}, {u.Mass((double?)p["mass_kg"] ?? 0)}");
            if (j["bodies"] is JArray bodies && bodies.Count > 0)
                sb.AppendLine("Bodies (as built): " + string.Join(", ", bodies.OfType<JObject>().Take(12).Select(b => $"{b["id"]} {Si.F((double?)b["mass_kg"] ?? 0)} kg")));
            if (j["stages"] is JArray stages && stages.Count > 0)
                sb.AppendLine("Stages: " + string.Join("; ", stages.OfType<JObject>().Select(s => $"{s["id"]} {s["status"]}" + (s["checkpoint"] != null ? $" [{s["checkpoint"]}]" : ""))));
            if (j["checks"] is JArray checks)
            {
                var list = checks.OfType<JObject>().ToList();
                var bad = Check.Ordered(list.Where(c => (string)c["status"] == "fail" || (string)c["status"] == "warn"));
                sb.AppendLine($"Checks: {list.Count(c => (string)c["status"] == "pass")} pass, {list.Count(c => (string)c["status"] == "warn")} warn, {list.Count(c => (string)c["status"] == "fail")} fail, {list.Count(c => (string)c["status"] == "waived")} waived");
                foreach (var c in bad.Take(15))
                {
                    var m = c["margin"] == null || c["margin"].Type == JTokenType.Null ? "" : $" ({(double)c["margin"] * 100:0.#}%)";
                    sb.AppendLine($"  {((string)c["status"]).ToUpperInvariant()} {c["id"]}{m}: {c["method"]}" + (string.IsNullOrEmpty((string)c["note"]) ? "" : " - " + c["note"]));
                }
                var low = Check.Ordered(list.Where(c => (string)c["status"] == "pass" && c["margin"] != null && c["margin"].Type != JTokenType.Null)).Take(3).ToList();
                if (low.Count > 0) sb.AppendLine("Lowest passing margins: " + string.Join(", ", low.Select(c => $"{c["id"]} {(double)c["margin"] * 100:0.#}%")));
            }
            if (j["decisions"] is JArray dec && dec.Count > 0)
            {
                sb.AppendLine("Recent decisions:");
                foreach (var dd in dec.OfType<JObject>().Reverse().Take(5).Reverse()) sb.AppendLine($"  {dd["id"]} {dd["topic"]}: {dd["choice"]} (by {dd["by"]})");
            }
            if (j["simulations"] is JArray sims && sims.Count > 0)
                sb.AppendLine("Simulations: " + string.Join("; ", sims.OfType<JObject>().Select(s => $"{s["id"]} {s["hook"]} {s["status"]}" + (s["reason"] != null && s["reason"].Type != JTokenType.Null ? " (" + s["reason"] + ")" : ""))));
            if (j["setupSheet"] != null && j["setupSheet"].Type != JTokenType.Null) sb.AppendLine("Setup sheet: " + j["setupSheet"]);
            var text = sb.ToString();
            const string cut = "\n... (summary cut; read a section for more)";
            if (text.Length > maxChars) text = text.Substring(0, Math.Max(0, maxChars - cut.Length)) + cut;
            return text;
        }

        internal static string Stamp() => Clock().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>
    /// The tool's data tables (design 1.5): part materials, mold grades and hardware, the shop's rules and the sizing
    /// defaults, each merged with the shop's override file of the same name. Built-in tool folders are replaced on every
    /// install, so a shop's own values ("save 2.1% as my EPDM 70A shrink") live in
    /// %APPDATA%\SwAgent\tools\builtin-data\design-compression-mold\ and survive reinstalls.
    ///
    /// Merge rules (design 1.5.5): rows of `rows` and `mold_materials` match by id - an override row is merged key by key
    /// onto the shipped row (a full row therefore replaces it), new ids are added at the end; objects merge key by key;
    /// any other value or array in the override replaces the shipped one.
    /// </summary>
    public sealed class MoldData
    {
        public const string ToolName = "design-compression-mold";
        public static readonly string[] Files = { "materials.json", "mold-materials.json", "shop-standards.json", "sizing-defaults.json" };

        private readonly Dictionary<string, JObject> _files = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _warnings = new List<string>();

        public string DataDir { get; private set; }
        public string OverrideDir { get; private set; }
        public IReadOnlyList<string> Warnings => _warnings;

        public IReadOnlyList<JObject> PartMaterials { get; private set; } = new List<JObject>();
        public IReadOnlyList<JObject> MoldGrades { get; private set; } = new List<JObject>();
        public JObject Shop { get; private set; } = new JObject();
        public JObject Defaults { get; private set; } = new JObject();

        /// <summary>Where the built-in tool ships (next to the add-in).</summary>
        public static string DefaultToolDir => Path.Combine(Tools.UserTools.BuiltInRoot, ToolName);

        /// <summary>The shop's override folder (%APPDATA%\SwAgent\tools\builtin-data\design-compression-mold).</summary>
        public static string DefaultOverrideDir => Path.Combine(Tools.UserTools.Root, "builtin-data", ToolName);

        /// <summary>
        /// Loads the data tables. `toolDir` is the tool folder (its data\ subfolder is read) or the data folder itself.
        /// `overrideDir` (optional) holds files with the same names that are merged on top. Missing sizing defaults fall
        /// back to the compiled copy, so the calculator still runs; other missing files give empty tables and a warning.
        /// </summary>
        public static MoldData Load(string toolDir, string overrideDir = null)
        {
            var d = new MoldData();
            var dataDir = toolDir;
            if (!string.IsNullOrEmpty(toolDir) && Directory.Exists(Path.Combine(toolDir, "data"))) dataDir = Path.Combine(toolDir, "data");
            d.DataDir = dataDir;
            d.OverrideDir = overrideDir;
            foreach (var name in Files)
            {
                var shipped = ReadJson(dataDir == null ? null : Path.Combine(dataDir, name), d._warnings);
                if (name == "sizing-defaults.json")
                {
                    var compiled = SizingDefaults.Compiled();
                    shipped = shipped == null ? compiled : Merge(compiled, shipped, name);
                }
                var over = string.IsNullOrEmpty(overrideDir) ? null : ReadJson(Path.Combine(overrideDir, name), d._warnings, optional: true);
                var merged = over == null ? (shipped ?? new JObject()) : Merge(shipped ?? new JObject(), over, name);
                d._files[name] = merged;
            }
            d.PartMaterials = (d._files["materials.json"]["rows"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            d.MoldGrades = (d._files["mold-materials.json"]["mold_materials"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            d.Shop = d._files["shop-standards.json"];
            d.Defaults = d._files["sizing-defaults.json"];
            return d;
        }

        /// <summary>The built-in tool's data plus the shop overrides: what the agent tools use.</summary>
        public static MoldData LoadDefault() => Load(DefaultToolDir, DefaultOverrideDir);

        private static JObject ReadJson(string path, List<string> warnings, bool optional = false)
        {
            if (path == null || !File.Exists(path))
            {
                if (!optional && path != null) warnings.Add("Missing data file: " + path);
                return null;
            }
            try
            {
                return Si.ParseObject(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read " + path + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>Merges `over` onto a copy of `shipped` by the shop-override rules (see the class comment).</summary>
        public static JObject Merge(JObject shipped, JObject over, string fileName = null)
        {
            var result = (JObject)shipped.DeepClone();
            MergeInto(result, over);
            return result;
        }

        /// <summary>
        /// One shipped row with a shop override row on top, key by key (nested objects merged, other values replaced):
        /// save_default writes partial rows such as {id, shrinkPct: {typ}}, so an override row never replaces the whole
        /// shipped row. The page's data loader (Track B, ToolData) must merge rows the same way.
        /// </summary>
        public static JObject MergeRow(JObject shipped, JObject overrideRow)
        {
            var result = (JObject)shipped.DeepClone();
            if (overrideRow != null) MergeInto(result, overrideRow);
            return result;
        }

        private static void MergeInto(JObject target, JObject over)
        {
            foreach (var p in over.Properties())
            {
                var cur = target[p.Name];
                if ((p.Name == "rows" || p.Name == "mold_materials") && cur is JArray ca && p.Value is JArray oa)
                {
                    foreach (var row in oa.OfType<JObject>())
                    {
                        var id = (string)row["id"];
                        var match = id == null ? null : ca.OfType<JObject>().FirstOrDefault(r => string.Equals((string)r["id"], id, StringComparison.OrdinalIgnoreCase));
                        if (match != null) MergeInto(match, row);
                        else ca.Add(row.DeepClone());
                    }
                }
                else if (cur is JObject co && p.Value is JObject po) MergeInto(co, po);
                else target[p.Name] = p.Value.DeepClone();
            }
        }

        /// <summary>
        /// mold_record save_default (design 2.3.5): writes the shop's own value into the override file of `table`
        /// ("materials", "mold-materials", "shop-standards" or "sizing-defaults") after checking every key against the
        /// shipped table, e.g. ("materials", "epdm-s-70a", {"shrinkPct.typ": 2.1}). Row tables need the row id (a new id adds
        /// a row, which must then carry the shipped row keys it sets). Values are stored as given (the data tables' units).
        /// Returns a short status; throws ArgumentException for unknown tables, ids or keys.
        /// </summary>
        public string SaveDefault(string overrideDir, string table, string id, JObject values)
        {
            if (string.IsNullOrWhiteSpace(overrideDir)) throw new ArgumentException("No override folder.");
            if (values == null || !values.HasValues) throw new ArgumentException("Nothing to save: pass values like {\"shrinkPct.typ\": 2.1}.");
            var file = (table ?? "").Trim().ToLowerInvariant();
            if (file.EndsWith(".json")) file = file.Substring(0, file.Length - 5);
            if (!Files.Contains(file + ".json")) throw new ArgumentException("Unknown table '" + table + "' (materials, mold-materials, shop-standards, sizing-defaults).");
            var shipped = Raw(file + ".json") ?? throw new ArgumentException("The shipped " + file + ".json is missing.");
            var rowKey = file == "materials" ? "rows" : file == "mold-materials" ? "mold_materials" : null;
            JObject template = shipped;
            if (rowKey != null)
            {
                if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException(file + " needs the row id.");
                template = (shipped[rowKey] as JArray)?.OfType<JObject>().FirstOrDefault(r => string.Equals((string)r["id"], id, StringComparison.OrdinalIgnoreCase))
                    ?? (shipped[rowKey] as JArray)?.OfType<JObject>().FirstOrDefault()
                    ?? throw new ArgumentException("The table has no rows.");
            }
            var patch = new JObject();
            foreach (var p in values.Properties())
            {
                var parts = p.Name.Split('.');
                JToken node = template;
                foreach (var part in parts)
                {
                    if (!(node is JObject o) || !o.ContainsKey(part)) throw new ArgumentException("Unknown key '" + p.Name + "' for " + file + (id != null ? " row " + id : "") + ".");
                    node = o[part];
                }
                JObject cur = patch;
                for (var k = 0; k < parts.Length - 1; k++)
                {
                    if (!(cur[parts[k]] is JObject next)) { next = new JObject(); cur[parts[k]] = next; }
                    cur = next;
                }
                cur[parts[parts.Length - 1]] = p.Value.DeepClone();
            }
            Directory.CreateDirectory(overrideDir);
            var path = Path.Combine(overrideDir, file + ".json");
            var existing = File.Exists(path) ? Si.ParseObject(File.ReadAllText(path)) : new JObject();
            JObject merged;
            if (rowKey != null)
            {
                patch["id"] = id;
                merged = Merge(existing, new JObject { [rowKey] = new JArray(patch) });
            }
            else merged = Merge(existing, patch);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, merged.ToString());
            Infrastructure.AtomicFile.Replace(tmp, path);
            return "saved " + string.Join(", ", values.Properties().Select(p => p.Name + " = " + p.Value.ToString(Newtonsoft.Json.Formatting.None))) + " to " + path;
        }

        /// <summary>A whole data file (merged), e.g. Raw("mold-materials.json")["dowels"].</summary>
        public JObject Raw(string file)
        {
            if (file != null && !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) file += ".json";
            return file != null && _files.TryGetValue(file, out var o) ? o : null;
        }

        /// <summary>A part-material row by id (exact, case-insensitive), or null.</summary>
        public JObject PartMaterial(string id) =>
            string.IsNullOrEmpty(id) ? null : PartMaterials.FirstOrDefault(r => string.Equals((string)r["id"], id, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Finds the part-material row the page or the agent meant: an id, a full name, or a short label (within
        /// `family` - a family id, label or short label - when given, since short labels repeat across families).
        /// </summary>
        public JObject FindPartMaterial(string key, string family = null)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            key = StripBracketId(key, out var bracketId);
            if (bracketId != null && PartMaterial(bracketId) != null) return PartMaterial(bracketId);
            var byId = PartMaterial(key);
            if (byId != null) return byId;
            bool Eq(JToken a, string b) => string.Equals(((string)a ?? "").Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
            var byName = PartMaterials.FirstOrDefault(r => Eq(r["name"], key));
            if (byName != null) return byName;
            var shorts = PartMaterials.Where(r => Eq(r["short"], key)).ToList();
            if (!string.IsNullOrWhiteSpace(family))
            {
                var inFamily = shorts.Where(r => Eq(r["family"], family) || Eq(r["familyLabel"], family) || Eq(r["familyShort"], family)).ToList();
                if (inFamily.Count > 0) return inFamily[0];
            }
            return shorts.Count == 1 ? shorts[0] : null;
        }

        /// <summary>A mold grade by id, page label (`short`) or name; null when unknown.</summary>
        public JObject MoldGrade(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = StripBracketId(id, out var bracketId);
            if (bracketId != null)
            {
                var b = MoldGrades.FirstOrDefault(g => string.Equals((string)g["id"], bracketId, StringComparison.OrdinalIgnoreCase));
                if (b != null) return b;
            }
            return MoldGrades.FirstOrDefault(g => string.Equals((string)g["id"], id, StringComparison.OrdinalIgnoreCase))
                ?? MoldGrades.FirstOrDefault(g => string.Equals((string)g["short"], id, StringComparison.OrdinalIgnoreCase))
                ?? MoldGrades.FirstOrDefault(g => string.Equals((string)g["name"], id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The default grade for a family ("aluminium" or "steel") and use ("plate", "insert", ...).</summary>
        public JObject DefaultGrade(string family, string use = "plate") =>
            MoldGrades.FirstOrDefault(g => (string)g["family"] == family && (bool?)g["isDefault"] == true && Uses(g, use))
            ?? MoldGrades.FirstOrDefault(g => (string)g["family"] == family && Uses(g, use));

        private static bool Uses(JObject g, string use) => use == null || (g["use"] as JArray)?.Any(u => (string)u == use) == true;

        /// <summary>A fastener row by size: "1/2-13", "1/2" (first inch match), "M10", "#10-24". Null when unknown.</summary>
        public JObject Fastener(string size)
        {
            if (string.IsNullOrWhiteSpace(size)) return null;
            var s = size.Trim().Replace(" ", "");
            var mm = Raw("mold-materials.json");
            var inch = (mm?["fasteners_inch"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
            var metric = (mm?["fasteners_metric"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
            return inch.FirstOrDefault(f => string.Equals((string)f["size"], s, StringComparison.OrdinalIgnoreCase))
                ?? metric.FirstOrDefault(f => string.Equals((string)f["size"], s, StringComparison.OrdinalIgnoreCase))
                ?? metric.FirstOrDefault(f => s.StartsWith((string)f["size"] + "x", StringComparison.OrdinalIgnoreCase))
                ?? inch.FirstOrDefault(f => ((string)f["size"]).StartsWith(s + "-", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Fastener rows of one system in ascending size (S53).</summary>
        public IReadOnlyList<JObject> Fasteners(bool imperial)
        {
            var mm = Raw("mold-materials.json");
            var arr = mm?[imperial ? "fasteners_inch" : "fasteners_metric"] as JArray;
            var list = arr?.OfType<JObject>().ToList() ?? new List<JObject>();
            // #10-32 is a fine-thread duplicate of #10-24: the coarse thread is the structural choice.
            return list.Where(f => (string)f["size"] != "#10-32").OrderBy(f => imperial ? (double)f["d_in"] : (double)f["d_mm"]).ToList();
        }

        /// <summary>"name [id]" from the page payload -> name, with the id in brackets returned separately.</summary>
        private static string StripBracketId(string s, out string id)
        {
            id = null;
            s = s.Trim();
            var open = s.LastIndexOf('[');
            if (open > 0 && s.EndsWith("]"))
            {
                var inside = s.Substring(open + 1, s.Length - open - 2);
                id = inside.Split(';')[0].Trim();
                s = s.Substring(0, open).Trim();
            }
            return s;
        }

        /// <summary>A shop-standards length in mm (accepts {"in": x}, {"mm": x} or a number in mm).</summary>
        public double ShopMm(string path, double fallbackMm)
        {
            var t = Shop.SelectToken(path);
            return LengthMm(t) ?? fallbackMm;
        }

        public static double? LengthMm(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t is JObject o)
            {
                if (o["mm"] != null && o["mm"].Type != JTokenType.Array) return (double)o["mm"];
                if (o["in"] != null) return (double)o["in"] * Si.InToMm;
                return null;
            }
            return Si.Num(t);
        }

        public double ShopNum(string path, double fallback)
        {
            var t = Shop.SelectToken(path);
            return t == null || t.Type == JTokenType.Null ? fallback : (Si.Num(t) ?? fallback);
        }
    }
}

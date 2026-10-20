using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Scripting;

namespace SwAgent.Tools
{
    /// <summary>
    /// Data files a tool's page reads its choices from ("ChoicesFrom": "data/materials.json#rows"): loaded from the tool's
    /// folder and cached for the page. A built-in tool's folder is replaced by every install, so a shop's own rows live in
    /// %APPDATA%\SwAgent\tools\builtin-data\&lt;tool&gt;\&lt;file name&gt; and are merged in: a row with the same id replaces the
    /// shipped row, new rows are added at the end, objects merge key by key, everything else comes from the shipped file.
    /// No SolidWorks types: tested headless.
    /// </summary>
    internal sealed class ToolData
    {
        private static readonly Regex Reference = new Regex(@"\{([A-Za-z0-9_\-]+)\}");
        private static readonly Regex Token = new Regex(@"\{([^{}|]+)(?:\|([^{}]+))?\}");

        private readonly string _dir;
        private readonly string _overrideDir;
        private readonly Dictionary<string, JToken> _files = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<JObject>> _rows = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);

        public ToolData(string dir, string overrideDir = null)
        {
            _dir = dir;
            _overrideDir = overrideDir;
        }

        public static readonly ToolData None = new ToolData(null);

        /// <summary>The data of a tool: its folder, plus the user's overrides when it is a built-in tool.</summary>
        public static ToolData For(ToolSpec spec)
        {
            if (spec?.Dir == null) return None;
            string overrides = null;
            try
            {
                if (spec.Dir.StartsWith(UserTools.BuiltInRoot, StringComparison.OrdinalIgnoreCase))
                    overrides = Path.Combine(UserTools.Root, "builtin-data", spec.Name);
            }
            catch (Exception ex)
            {
                Log.Warn("Tool data overrides unavailable: " + ex.Message);
            }
            return new ToolData(spec.Dir, overrides);
        }

        /// <summary>"data/x.json#rows" -> "data/x.json".</summary>
        public static string FileOf(string choicesFrom)
        {
            var s = (choicesFrom ?? "").Trim();
            var hash = s.IndexOf('#');
            return hash >= 0 ? s.Substring(0, hash).Trim() : s;
        }

        /// <summary>"data/x.json#rows" -> "rows"; null without '#'.</summary>
        public static string KeyOf(string choicesFrom)
        {
            var s = (choicesFrom ?? "").Trim();
            var hash = s.IndexOf('#');
            return hash >= 0 && hash < s.Length - 1 ? s.Substring(hash + 1).Trim() : null;
        }

        /// <summary>A path inside the tool folder (no rooted paths, no "..").</summary>
        public static bool IsSafeRelative(string file)
        {
            if (string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file) || file.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            return !file.Replace('\\', '/').Split('/').Any(p => p == "..");
        }

        /// <summary>The input ids a RowFilter pattern names: "{mold_material}".</summary>
        public static IEnumerable<string> References(string pattern) =>
            Reference.Matches(pattern ?? "").Cast<Match>().Select(m => m.Groups[1].Value);

        /// <summary>A file of the tool (merged with the user's override), or null when it is missing or unreadable.</summary>
        public JToken File(string file)
        {
            if (_dir == null || !IsSafeRelative(file)) return null;
            if (_files.TryGetValue(file, out var cached)) return cached;
            JToken token = null;
            try
            {
                var path = Path.Combine(_dir, file);
                if (System.IO.File.Exists(path))
                {
                    token = JToken.Parse(System.IO.File.ReadAllText(path));
                    var over = _overrideDir == null ? null : Path.Combine(_overrideDir, Path.GetFileName(file));
                    if (over != null && System.IO.File.Exists(over))
                    {
                        try
                        {
                            token = MergeOverride(token, JToken.Parse(System.IO.File.ReadAllText(over)));
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Ignoring the unreadable override {over}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Log.Warn($"Tool data file missing: {path}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Tool data file {file} unreadable: {ex.Message}");
                token = null;
            }
            _files[file] = token;
            return token;
        }

        /// <summary>The rows ChoicesFrom names (strings in the array become rows { "name": "..." }).</summary>
        public List<JObject> Rows(string choicesFrom)
        {
            if (string.IsNullOrWhiteSpace(choicesFrom)) return new List<JObject>();
            if (_rows.TryGetValue(choicesFrom, out var cached)) return cached;
            var root = File(FileOf(choicesFrom));
            var key = KeyOf(choicesFrom);
            var token = key == null ? root : root is JObject ? ValueAt(root, key) : null;
            var rows = new List<JObject>();
            if (token is JArray array)
            {
                foreach (var item in array)
                {
                    if (item is JObject o) rows.Add(o);
                    else if (item is JValue v && v.Type == JTokenType.String) rows.Add(new JObject { ["name"] = v });
                }
            }
            _rows[choicesFrom] = rows;
            return rows;
        }

        /// <summary>The rows of a choice that pass its RowFilter, given the other inputs' current values.</summary>
        public List<JObject> Filter(ToolInputSpec s, Func<string, string> valueOf)
        {
            var rows = Rows(s?.ChoicesFrom);
            if (s?.RowFilter == null || s.RowFilter.Count == 0) return rows;
            var patterns = s.RowFilter.Select(kv => (Field: kv.Key, Pattern: Substitute(kv.Value, valueOf, s.ValueMap))).ToList();
            return rows.Where(r => patterns.All(p => Matches(ValueAt(r, p.Field), p.Pattern))).ToList();
        }

        /// <summary>The entries of a choice: its filtered rows' ChoiceKey values, distinct, in file order.</summary>
        public List<string> Entries(ToolInputSpec s, Func<string, string> valueOf) => Keys(Filter(s, valueOf), s?.ChoiceKey);

        public static List<string> Keys(IEnumerable<JObject> rows, string choiceKey)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                var k = KeyText(r, choiceKey);
                if (!string.IsNullOrWhiteSpace(k) && seen.Add(k)) list.Add(k);
            }
            return list;
        }

        public static string KeyText(JObject row, string choiceKey) => Text(ValueAt(row, string.IsNullOrWhiteSpace(choiceKey) ? "name" : choiceKey));

        /// <summary>{id} replaced by that input's value (mapped through ValueMap); null when an input has no value (nothing matches).</summary>
        internal static string Substitute(string pattern, Func<string, string> valueOf, IDictionary<string, string> valueMap)
        {
            if (pattern == null) return null;
            var missing = false;
            var result = Reference.Replace(pattern, m =>
            {
                var v = valueOf?.Invoke(m.Groups[1].Value);
                if (v == null)
                {
                    missing = true;
                    return "";
                }
                if (valueMap != null)
                {
                    var hit = valueMap.FirstOrDefault(kv => string.Equals(kv.Key, v, StringComparison.OrdinalIgnoreCase));
                    if (hit.Key != null) v = hit.Value;
                }
                return v;
            });
            return missing ? null : result;
        }

        /// <summary>Case-insensitive equality; an array matches when it contains the pattern; "*" matches anything present.</summary>
        internal static bool Matches(JToken value, string pattern)
        {
            if (pattern == null) return false;
            if (pattern.Trim() == "*") return value != null && value.Type != JTokenType.Null;
            if (value is JArray array) return array.Any(x => !(x is JContainer) && Matches(x, pattern));
            var text = Text(value);
            return text != null && string.Equals(text.Trim(), pattern.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A dotted path in a row: "shrinkPct.typ", "flash.landWidthMm.min"; array items by index ("temps.0").</summary>
        public static JToken ValueAt(JToken row, string path)
        {
            if (row == null || string.IsNullOrWhiteSpace(path)) return null;
            var t = row;
            foreach (var part in path.Trim().Split('.'))
            {
                if (t is JObject o)
                {
                    // Exact name first, then case-insensitive (hand-written paths).
                    t = o.TryGetValue(part, StringComparison.Ordinal, out var v) ? v : o.TryGetValue(part, StringComparison.OrdinalIgnoreCase, out v) ? v : null;
                }
                else if (t is JArray a && int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    t = i >= 0 && i < a.Count ? a[i] : null;
                }
                else
                {
                    return null;
                }
                if (t == null) return null;
            }
            return t;
        }

        /// <summary>A scalar as invariant text (null for null/missing); arrays as "a, b".</summary>
        public static string Text(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined) return null;
            if (t is JValue v)
            {
                if (v.Value is double d) return Units.Format(d, 10);
                if (v.Value is bool b) return b ? "true" : "false";
                return Convert.ToString(v.Value, CultureInfo.InvariantCulture);
            }
            if (t is JArray a) return string.Join(", ", a.Select(Text).Where(x => x != null));
            return t.ToString(Formatting.None);
        }

        /// <summary>A number in a row (numbers, or numeric strings); null otherwise.</summary>
        public static double? Number(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return t.Value<double>();
            if (t.Type == JTokenType.String && double.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return null;
        }

        // ---- user overrides ----

        /// <summary>
        /// The shipped file with the user's override merged in. Arrays of rows with "id": same id replaces, new ids are added
        /// at the end. Objects: key by key. Top-level values that are neither come from the shipped file.
        /// </summary>
        public static JToken MergeOverride(JToken shipped, JToken over)
        {
            if (shipped == null) return over?.DeepClone();
            if (over == null) return shipped.DeepClone();
            if (shipped is JArray sa && over is JArray oa) return MergeRows(sa, oa);
            if (!(shipped is JObject so) || !(over is JObject oo)) return shipped.DeepClone();
            var result = (JObject)so.DeepClone();
            foreach (var p in oo.Properties())
            {
                var mine = result[p.Name];
                if (mine == null) result[p.Name] = p.Value.DeepClone();
                else if (mine is JArray ma && p.Value is JArray pa) result[p.Name] = HasIds(ma) || HasIds(pa) ? MergeRows(ma, pa) : ma;
                else if (mine is JObject mo && p.Value is JObject po) result[p.Name] = MergeObject(mo, po);
                // Anything else (schema, generated...) stays as shipped.
            }
            return result;
        }

        private static JObject MergeObject(JObject a, JObject b)
        {
            var result = (JObject)a.DeepClone();
            foreach (var p in b.Properties())
            {
                var mine = result[p.Name];
                if (mine is JObject mo && p.Value is JObject po) result[p.Name] = MergeObject(mo, po);
                else if (mine is JArray ma && p.Value is JArray pa && (HasIds(ma) || HasIds(pa))) result[p.Name] = MergeRows(ma, pa);
                else result[p.Name] = p.Value.DeepClone();
            }
            return result;
        }

        private static bool HasIds(JArray a) => a.Count > 0 && a.All(x => x is JObject o && o["id"] != null);

        private static JArray MergeRows(JArray shipped, JArray over)
        {
            var result = new JArray(shipped.Select(x => x.DeepClone()));
            foreach (var row in over)
            {
                var id = (row as JObject)?["id"];
                var at = id == null ? -1 : result.Select((x, i) => (x, i)).Where(t => JToken.DeepEquals((t.x as JObject)?["id"], id)).Select(t => t.i).DefaultIfEmpty(-1).First();
                if (at >= 0) result[at] = row.DeepClone();
                else result.Add(row.DeepClone());
            }
            return result;
        }

        // ---- info captions ----

        /// <summary>
        /// An info line: {id} is an input's value, {id.a.b} a field of that choice's selected row, and {...|unit} shows a number
        /// given in that unit (a quantity name such as "temperature" means its spec unit; or a symbol such as "psi") in the
        /// page's system: "Full strength to {grade.max_service_C.full_strength|temperature}" -> "Full strength to 248 °F".
        /// resolve returns preformatted text (string) or a row value (JToken); null shows "n/a". Unknown names stay as written.
        /// </summary>
        public static string Render(string template, Func<string, object> resolve, bool imperial)
        {
            if (string.IsNullOrEmpty(template)) return template ?? "";
            return Token.Replace(template, m =>
            {
                object value;
                try
                {
                    value = resolve?.Invoke(m.Groups[1].Value.Trim());
                }
                catch
                {
                    value = null;
                }
                if (value is Unresolved) return m.Value;
                var unit = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
                if (value is string s) return s;
                var token = value as JToken;
                if (token == null || token.Type == JTokenType.Null) return "n/a";
                var n = token.Type == JTokenType.Integer || token.Type == JTokenType.Float ? Number(token) : null;
                if (n == null) return Text(token) ?? "n/a";
                return unit == null ? Units.Format(n.Value, 6) : FormatIn(n.Value, unit, imperial);
            });
        }

        /// <summary>Returned by a resolver for a name that isn't an input: the {...} text is kept as written.</summary>
        internal sealed class Unresolved
        {
            public static readonly Unresolved Instance = new Unresolved();
        }

        /// <summary>A number given in a unit (quantity name = its spec unit, or a symbol), shown in the page's system.</summary>
        public static string FormatIn(double value, string unit, bool imperial)
        {
            UnitDef from;
            string quantity;
            if (Units.Known.Contains(unit.ToLowerInvariant()))
            {
                quantity = unit.ToLowerInvariant();
                from = Units.Spec(quantity);
            }
            else
            {
                from = Units.FindAny(unit);
                if (from == null) return Units.Format(value, 6) + " " + unit;
                quantity = from.Quantity;
            }
            return Units.Show(from.ToSiValue(value), Units.For(quantity, imperial), 4);
        }
    }

    /// <summary>What changed in a form field since the page last showed it.</summary>
    [Flags]
    internal enum FieldChange
    {
        None = 0,
        Items = 1,
        Value = 2,
        Visible = 4,
        Caption = 8,
        Enabled = 16,
        Range = 32,
        All = 63,
    }

    /// <summary>One input's state on a tool's page, kept in SI (what ToolInputs hands the macro).</summary>
    internal sealed class FormField
    {
        public ToolInputSpec Spec;
        public string Id;
        public string Type;
        public int Order;

        /// <summary>number: the unit quantity ("length" when Unit is null); size: length.</summary>
        public string Quantity;

        /// <summary>A number in a unit SolidWorks has no box for: a unitless box the page converts (pressure, temperature...).</summary>
        public bool Converted;

        /// <summary>The unit the box shows (converted numbers only).</summary>
        public UnitDef Unit;

        public List<UnitDef> UnitChoices = new List<UnitDef>();

        public bool Optional;
        public bool On = true;
        public double Si;
        public double[] SizeSi;
        public bool Checked;

        /// <summary>choice/options: the selected entry (null for none); text: the text.</summary>
        public string Value;

        /// <summary>choice/options: the entries, in order (dynamic for ChoicesFrom).</summary>
        public List<string> Items = new List<string>();

        /// <summary>ChoicesFrom: the rows that pass the filter, and the one the entry names (first, when several share the entry).</summary>
        public List<JObject> Rows = new List<JObject>();
        public JObject Row;
        public bool RowUnique;

        /// <summary>A value another choice filled in (SI), and which choice.</summary>
        public double? TableSi;
        public string FilledBy;

        /// <summary>The filling row has no value for this field (null): hidden, "n/a" for the agent.</summary>
        public bool NotApplicable;

        /// <summary>ShowWhen isn't met.</summary>
        public bool ConditionHidden;

        public bool Remembered;
        public string RememberedChoice;

        public string Caption;
        public FieldChange Changes = FieldChange.All;

        /// <summary>Inputs whose value decides this choice's entries (DependsOn and the ids in RowFilter).</summary>
        public HashSet<string> Parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool IsData => Type == "choice" && !string.IsNullOrWhiteSpace(Spec.ChoicesFrom);
        public bool Hidden => ConditionHidden || NotApplicable;
        public bool Edited => TableSi.HasValue && !ToolForm.Same(Si, TableSi.Value, 1e-4);
        public int Dims => Spec.Dims == 2 ? 2 : 3;

        /// <summary>Uses nothing newer than the original page options (renders, defaults and collects as it always has).</summary>
        public bool IsPlain =>
            (Type == "selection" || Type == "number" || Type == "integer" || Type == "checkbox" || Type == "choice" || Type == "options" || Type == "text" || Type == "files")
            && !Converted && !Optional && !Remembered && TableSi == null && !NotApplicable && FilledBy == null && !IsData && Spec.ShowWhen == null
            && Spec.UnitChoices == null;
    }

    /// <summary>
    /// The state of a tool's PropertyManager page without SolidWorks: defaults (remembered values first), data-backed
    /// choices that cascade (family -> grade), values a choice fills in, visibility, info lines, unit choices, required checks
    /// and the values handed to the macro and the agent. ToolPage mirrors it into native controls; TryFromSelection and
    /// the tests use it directly. Every change is idempotent (setting the current value changes nothing), so events that
    /// SolidWorks raises for values the code set are harmless.
    /// </summary>
    internal sealed class ToolForm
    {
        private const int MaxDepth = 8;

        private readonly ToolSpec _spec;
        private readonly ToolData _data;
        private readonly Dictionary<string, FormField> _byId = new Dictionary<string, FormField>(StringComparer.OrdinalIgnoreCase);

        public readonly List<FormField> Fields = new List<FormField>();
        public readonly bool Imperial;

        public ToolForm(ToolSpec spec, ToolData data, bool imperial, JObject remembered = null)
        {
            _spec = spec;
            _data = data ?? ToolData.None;
            Imperial = imperial;
            var order = 0;
            foreach (var s in spec?.Inputs ?? new List<ToolInputSpec>())
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Id)) continue;
                var f = new FormField { Spec = s, Id = s.Id, Type = ToolSpec.Type(s), Order = order++ };
                Fields.Add(f);
                if (!_byId.ContainsKey(f.Id)) _byId[f.Id] = f;
            }
            foreach (var f in Fields) Init(f, remembered?[f.Id] as JObject);
            foreach (var f in Fields.Where(x => x.IsData))
            {
                if (!string.IsNullOrWhiteSpace(f.Spec.DependsOn)) f.Parents.Add(f.Spec.DependsOn.Trim());
                foreach (var p in (f.Spec.RowFilter ?? new Dictionary<string, string>()).Values)
                    foreach (var r in ToolData.References(p)) f.Parents.Add(r);
                f.Parents.Remove(f.Id);
            }
            // Data choices in file order (parents come first), then what they fill.
            foreach (var f in Fields.Where(x => x.IsData)) RefreshEntries(f, initial: true);
            foreach (var f in Fields.Where(x => x.Spec.Fills != null && (x.Type == "choice" || x.Type == "options"))) ApplyFills(f, initial: true, depth: 0);
            RefreshDerived();
            foreach (var f in Fields) f.Changes = FieldChange.All;
        }

        public FormField Get(string id) => id != null && _byId.TryGetValue(id.Trim(), out var f) ? f : null;

        // ---- defaults ----

        private void Init(FormField f, JObject remembered)
        {
            var s = f.Spec;
            var remember = s.Remember == true && remembered != null;
            var value = remember ? remembered["value"] : null;
            switch (f.Type)
            {
                case "number":
                case "integer":
                {
                    f.Quantity = f.Type == "integer" ? "none" : Units.Quantity(s.Unit);
                    f.Converted = f.Type == "number" && Units.IsConverted(s.Unit);
                    if (f.Converted)
                    {
                        f.UnitChoices = Units.Choices(s.Unit, s.UnitChoices);
                        var system = Units.For(s.Unit, Imperial);
                        var chosen = f.UnitChoices.FirstOrDefault(u => u.Matches((string)remembered?["unit"]));
                        f.Unit = chosen ?? (f.UnitChoices.Count == 0 || f.UnitChoices.Contains(system) ? system : f.UnitChoices[0]);
                    }
                    InitOptional(f, remember ? remembered : null);
                    f.Si = DefaultSi(s, f.Type, f.Unit);
                    var spec = ToolData.Number(value);
                    if (spec != null)
                    {
                        f.Si = SpecToSi(f, Clamp(spec.Value, s.Min, s.Max));
                        f.Remembered = true;
                    }
                    break;
                }
                case "size":
                {
                    f.Quantity = "length";
                    InitOptional(f, remember ? remembered : null);
                    f.SizeSi = DefaultSize(s);
                    if (value is JArray a && a.Count >= f.Dims)
                    {
                        var dims = a.Take(f.Dims).Select(ToolData.Number).ToList();
                        if (dims.All(d => d != null))
                        {
                            f.SizeSi = dims.Select(d => Clamp(d.Value, s.Min, s.Max) / 1000.0).ToArray();
                            f.Remembered = true;
                        }
                    }
                    break;
                }
                case "checkbox":
                    f.Checked = string.Equals(s.Default, "true", StringComparison.OrdinalIgnoreCase);
                    if (value != null && value.Type == JTokenType.Boolean)
                    {
                        f.Checked = (bool)value;
                        f.Remembered = true;
                    }
                    break;
                case "options":
                {
                    f.Items = (s.Choices ?? new string[0]).ToList();
                    // As the page always did: the default, else the first one only when no default is given.
                    f.Value = f.Items.Contains(s.Default) ? s.Default : s.Default == null && f.Items.Count > 0 ? f.Items[0] : null;
                    var r = (string)(value as JValue);
                    if (r != null && f.Items.Contains(r))
                    {
                        f.Value = r;
                        f.Remembered = true;
                    }
                    break;
                }
                case "choice":
                {
                    f.RememberedChoice = (string)(value as JValue);
                    if (f.IsData) break; // entries come from the data, once every field has its default
                    f.Items = (s.Choices ?? new string[0]).ToList();
                    f.Value = f.Items.Count == 0 ? null : f.Items[Math.Max(0, f.Items.IndexOf(s.Default))];
                    if (f.RememberedChoice != null && f.Items.Contains(f.RememberedChoice))
                    {
                        f.Value = f.RememberedChoice;
                        f.Remembered = true;
                    }
                    break;
                }
                case "text":
                    f.Value = s.Default ?? "";
                    break;
            }
        }

        private static void InitOptional(FormField f, JObject remembered)
        {
            f.Optional = f.Spec.Optional == true;
            f.On = !f.Optional || f.Spec.Enabled != false;
            var on = remembered?["on"];
            if (f.Optional && on != null && on.Type == JTokenType.Boolean)
            {
                f.On = (bool)on;
                f.Remembered = true;
            }
        }

        /// <summary>The default in SI. Shown in another unit it is rounded for display (13.79 MPa shows 2000 psi, 20.68 MPa 3000 psi).</summary>
        internal static double DefaultSi(ToolInputSpec s, string type, UnitDef shown)
        {
            var value = double.TryParse(s.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (s.Min ?? 0);
            if (type == "integer") return Math.Round(value);
            if (!Units.IsConverted(s.Unit))
            {
                var unit = (s.Unit ?? "length").ToLowerInvariant();
                return unit == "angle" ? value * Math.PI / 180.0 : unit == "length" ? value / 1000.0 : value;
            }
            var si = Units.SpecToSi(s.Unit, value);
            if (shown == null || shown.Ascii == Units.Spec(s.Unit).Ascii) return si;
            // Spec defaults are often a conversion themselves (20.68 MPa = 3000 psi): snap to 3 digits when that is
            // within 0.05 %, else keep 4 (45.36 t, not 45.4).
            var v = shown.FromSi(si);
            var three = Units.Nice(v, 3);
            return shown.ToSiValue(Math.Abs(three - v) <= 5e-4 * Math.Abs(v) ? three : Units.Nice(v, 4));
        }

        internal static double[] DefaultSize(ToolInputSpec s)
        {
            var dims = s.Dims == 2 ? 2 : 3;
            var parts = (s.Default ?? "").Split(new[] { ',', ';', 'x', 'X', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new double[dims];
            for (var i = 0; i < dims; i++)
            {
                var mm = i < parts.Length && double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
                    : i > 0 && parts.Length > 0 && double.TryParse(parts.Last(), NumberStyles.Float, CultureInfo.InvariantCulture, out var last) ? last
                    : s.Min ?? 0;
                result[i] = mm / 1000.0;
            }
            return result;
        }

        private static double Clamp(double v, double? min, double? max) => Math.Max(min ?? double.MinValue, Math.Min(max ?? double.MaxValue, v));

        /// <summary>Spec unit -> SI for a field (integers and plain numbers are themselves).</summary>
        internal static double SpecToSi(FormField f, double spec)
        {
            if (f.Type == "integer") return Math.Round(spec);
            if (f.Type == "size") return spec / 1000.0;
            return Units.SpecToSi(f.Spec.Unit, spec);
        }

        internal static double SiToSpec(FormField f, double si)
        {
            if (f.Type == "integer") return si;
            if (f.Type == "size") return si * 1000.0;
            return Units.SiToSpec(f.Spec.Unit, si);
        }

        internal static bool Same(double a, double b, double relative = 1e-9) =>
            a == b || Math.Abs(a - b) <= relative * Math.Max(Math.Abs(a), Math.Abs(b)) || Math.Abs(a - b) < 1e-12;

        // ---- data-backed choices ----

        /// <summary>An input's value as RowFilter and ShowWhen see it (choice text, "true"/"false", a number in spec units).</summary>
        public string ValueOf(string id)
        {
            var f = Get(id);
            if (f == null) return null;
            switch (f.Type)
            {
                case "choice":
                case "options":
                case "text":
                    return f.Value;
                case "checkbox":
                    return f.Checked ? "true" : "false";
                case "number":
                case "integer":
                    return f.NotApplicable || f.Optional && !f.On ? null : Units.Format(SiToSpec(f, f.Si), 10);
            }
            return null;
        }

        private void RefreshEntries(FormField f, bool initial)
        {
            f.Rows = _data.Filter(f.Spec, ValueOf);
            var items = ToolData.Keys(f.Rows, f.Spec.ChoiceKey);
            if (!items.SequenceEqual(f.Items))
            {
                f.Items = items;
                f.Changes |= FieldChange.Items | FieldChange.Value;
            }
            var keep = initial ? null : f.Value;
            var value = keep != null && items.Contains(keep) ? keep : PickDefault(f, items, initial);
            if (value != f.Value)
            {
                f.Value = value;
                f.Changes |= FieldChange.Value;
            }
            UpdateRow(f);
        }

        /// <summary>A dependent choice's entry: the remembered one, Default, the row marked isDefault, the first.</summary>
        private string PickDefault(FormField f, List<string> items, bool initial)
        {
            if (items.Count == 0) return null;
            if (f.RememberedChoice != null && items.Contains(f.RememberedChoice))
            {
                if (initial) f.Remembered = true;
                return f.RememberedChoice;
            }
            if (f.Spec.Default != null && items.Contains(f.Spec.Default)) return f.Spec.Default;
            var marked = f.Rows.FirstOrDefault(r => r["isDefault"]?.Type == JTokenType.Boolean && (bool)r["isDefault"]);
            var key = marked == null ? null : ToolData.KeyText(marked, f.Spec.ChoiceKey);
            return key != null && items.Contains(key) ? key : items[0];
        }

        private void UpdateRow(FormField f)
        {
            if (!f.IsData)
            {
                f.Row = null;
                f.RowUnique = false;
                return;
            }
            var matches = f.Value == null ? new List<JObject>() : f.Rows.Where(r => ToolData.KeyText(r, f.Spec.ChoiceKey) == f.Value).ToList();
            f.Row = matches.FirstOrDefault();
            f.RowUnique = matches.Count == 1;
        }

        /// <summary>Choices whose entries depend on <paramref name="id"/> get new entries (keeping the entry when it still exists).</summary>
        private void Cascade(string id, int depth)
        {
            if (depth > MaxDepth) return;
            foreach (var child in Fields.Where(c => c.IsData && c.Parents.Contains(id)))
            {
                var before = child.Value;
                var beforeRow = child.Row;
                RefreshEntries(child, initial: false);
                if (child.Value != before || !ReferenceEquals(child.Row, beforeRow))
                {
                    ApplyFills(child, initial: false, depth: depth + 1);
                    Cascade(child.Id, depth + 1);
                }
            }
        }

        /// <summary>
        /// The selected row's values go into the fields the choice fills (converted from the spec unit, or from the unit after
        /// '|'). A null value hides the field (n/a). At start a remembered value is kept; a pick by the user overwrites.
        /// </summary>
        private void ApplyFills(FormField f, bool initial, int depth)
        {
            if (f.Spec.Fills == null || depth > MaxDepth) return;
            foreach (var kv in f.Spec.Fills)
            {
                var t = Get(kv.Key);
                if (t == null || t == f) continue;
                var (path, unitSymbol) = SplitFill(kv.Value);
                if (f.Row == null)
                {
                    // Nothing picked (no entries): keep the values, drop the table link.
                    if (t.NotApplicable) t.Changes |= FieldChange.Visible;
                    t.NotApplicable = false;
                    t.TableSi = null;
                    t.FilledBy = null;
                    continue;
                }
                var token = ToolData.ValueAt(f.Row, path);
                t.FilledBy = f.Id;
                if (token == null || token.Type == JTokenType.Null)
                {
                    if (!t.NotApplicable) t.Changes |= FieldChange.Visible;
                    t.NotApplicable = true;
                    t.TableSi = null;
                    continue;
                }
                if (t.NotApplicable) t.Changes |= FieldChange.Visible;
                t.NotApplicable = false;
                switch (t.Type)
                {
                    case "number":
                    case "integer":
                    {
                        var n = ToolData.Number(token);
                        if (n == null)
                        {
                            t.TableSi = null;
                            break;
                        }
                        var from = unitSymbol == null ? null : Units.FindAny(unitSymbol);
                        if (unitSymbol != null && from == null) Log.Warn($"Tool '{_spec?.Name}': unknown unit '{unitSymbol}' in fills of '{f.Id}'");
                        var si = from != null ? from.ToSiValue(n.Value) : SpecToSi(t, n.Value);
                        if (t.Type == "integer") si = Math.Round(si);
                        t.TableSi = si;
                        if (initial && t.Remembered) break;
                        if (!Same(t.Si, si))
                        {
                            t.Si = si;
                            t.Changes |= FieldChange.Value;
                        }
                        break;
                    }
                    case "choice":
                    case "options":
                    {
                        var text = ToolData.Text(token);
                        if (initial && t.Remembered) break;
                        if (text != null && t.Items.Contains(text) && text != t.Value)
                        {
                            t.Value = text;
                            UpdateRow(t);
                            t.Changes |= FieldChange.Value;
                            ApplyFills(t, initial, depth + 1);
                            Cascade(t.Id, depth + 1);
                        }
                        break;
                    }
                    case "checkbox":
                        if (token.Type == JTokenType.Boolean && !(initial && t.Remembered) && (bool)token != t.Checked)
                        {
                            t.Checked = (bool)token;
                            t.Changes |= FieldChange.Value;
                        }
                        break;
                    case "text":
                        var s = ToolData.Text(token) ?? "";
                        if (s != t.Value)
                        {
                            t.Value = s;
                            t.Changes |= FieldChange.Value;
                        }
                        break;
                }
            }
        }

        internal static (string Path, string Unit) SplitFill(string fill)
        {
            var s = (fill ?? "").Trim();
            var bar = s.IndexOf('|');
            return bar < 0 ? (s, null) : (s.Substring(0, bar).Trim(), s.Substring(bar + 1).Trim());
        }

        // ---- changes from the page ----

        /// <summary>The user picked an entry of a choice or options input.</summary>
        public bool SetChoice(FormField f, string value)
        {
            if (f == null || (f.Type != "choice" && f.Type != "options") || value == f.Value || value == null || !f.Items.Contains(value)) return false;
            f.Value = value;
            UpdateRow(f);
            ApplyFills(f, initial: false, depth: 0);
            Cascade(f.Id, 0);
            RefreshDerived();
            return true;
        }

        /// <summary>The user typed a number (SI). The box already shows it, so it isn't marked to be written back.</summary>
        public bool SetNumber(FormField f, double si)
        {
            if (f == null || (f.Type != "number" && f.Type != "integer") || Same(f.Si, si)) return false;
            f.Si = si;
            Cascade(f.Id, 0);
            RefreshDerived();
            return true;
        }

        public bool SetSize(FormField f, int index, double si)
        {
            if (f?.SizeSi == null || index < 0 || index >= f.SizeSi.Length || Same(f.SizeSi[index], si)) return false;
            f.SizeSi[index] = si;
            RefreshDerived();
            return true;
        }

        /// <summary>An Optional input's checkbox.</summary>
        public bool SetOn(FormField f, bool on)
        {
            if (f == null || !f.Optional || f.On == on) return false;
            f.On = on;
            f.Changes |= FieldChange.Enabled;
            Cascade(f.Id, 0);
            RefreshDerived();
            return true;
        }

        public bool SetChecked(FormField f, bool on)
        {
            if (f == null || f.Type != "checkbox" || f.Checked == on) return false;
            f.Checked = on;
            Cascade(f.Id, 0);
            RefreshDerived();
            return true;
        }

        public bool SetText(FormField f, string text)
        {
            if (f == null || f.Type != "text" || (text ?? "") == (f.Value ?? "")) return false;
            f.Value = text ?? "";
            RefreshDerived();
            return true;
        }

        /// <summary>A field's own unit choice: the value stays, the box shows it in the new unit.</summary>
        public bool SetUnit(FormField f, string symbol)
        {
            var unit = f?.UnitChoices.FirstOrDefault(u => u.Matches(symbol));
            if (unit == null || ReferenceEquals(unit, f.Unit)) return false;
            f.Unit = unit;
            f.Changes |= FieldChange.Value | FieldChange.Range | FieldChange.Caption;
            RefreshDerived();
            return true;
        }

        // ---- visibility and captions ----

        public void RefreshDerived()
        {
            // ShowWhen may name inputs further down (themselves hidden), so settle in a few passes.
            for (var pass = 0; pass < 4; pass++)
            {
                var changed = false;
                foreach (var f in Fields)
                {
                    var hidden = !ShowWhenMet(f);
                    if (hidden == f.ConditionHidden) continue;
                    f.ConditionHidden = hidden;
                    f.Changes |= FieldChange.Visible;
                    changed = true;
                }
                if (!changed) break;
            }
            foreach (var f in Fields)
            {
                var caption = CaptionOf(f);
                if (caption == f.Caption) continue;
                f.Caption = caption;
                f.Changes |= FieldChange.Caption;
            }
        }

        private bool ShowWhenMet(FormField f)
        {
            if (f.Spec.ShowWhen == null) return true;
            foreach (var kv in f.Spec.ShowWhen)
            {
                var other = Get(kv.Key);
                if (other == null || other == f) continue; // warned when loaded
                if (other.Hidden) return false;
                var v = ValueOf(other.Id);
                if (v == null || kv.Value == null || !kv.Value.Any(x => string.Equals((x ?? "").Trim(), v.Trim(), StringComparison.OrdinalIgnoreCase))) return false;
            }
            return true;
        }

        /// <summary>The label of a field (converted units get their symbol) or an info line's text.</summary>
        public string CaptionOf(FormField f)
        {
            if (f.Type == "info") return ToolData.Render(f.Spec.Text ?? f.Spec.Label ?? "", Resolve, Imperial);
            var label = f.Spec.Label ?? "";
            return f.Converted && !string.IsNullOrEmpty(f.Unit?.Symbol) ? $"{label} ({f.Unit.Symbol})" : label;
        }

        /// <summary>{id} and {id.a.b} for info lines.</summary>
        private object Resolve(string name)
        {
            var dot = name.IndexOf('.');
            var f = Get(dot < 0 ? name : name.Substring(0, dot));
            if (f == null) return ToolData.Unresolved.Instance;
            if (dot >= 0) return f.Row == null ? null : ToolData.ValueAt(f.Row, name.Substring(dot + 1)) ?? JValue.CreateNull();
            return ShowText(f);
        }

        /// <summary>A field's value as the page would show it (info lines): "2000 psi", "600 x 400 mm", "not set".</summary>
        public string ShowText(FormField f)
        {
            switch (f.Type)
            {
                case "number":
                case "integer":
                    if (f.NotApplicable) return "n/a";
                    if (f.Optional && !f.On) return "not set";
                    if (f.Type == "integer") return Units.Format(f.Si, 12);
                    return Units.Show(f.Si, f.Converted ? f.Unit : NativeShown(f.Quantity), 6);
                case "size":
                    if (f.Optional && !f.On) return "not set";
                    return SizeText(f.SizeSi, Units.For("length", Imperial), ascii: false);
                case "checkbox":
                    return f.Checked ? "yes" : "no";
                case "choice":
                case "options":
                case "text":
                    return f.Value ?? "";
            }
            return "";
        }

        private UnitDef NativeShown(string quantity) => quantity == "length" || quantity == "angle" ? Units.For(quantity, Imperial) : Units.Spec(quantity);

        private static string SizeText(double[] si, UnitDef unit, bool ascii) =>
            string.Join(" x ", (si ?? new double[0]).Select(v => Units.Format(unit.FromSi(v), 4))) + " " + (ascii ? unit.Ascii : unit.Symbol);

        // ---- reading out ----

        /// <summary>Why OK is unavailable because of the newer options (null when nothing): the first such field and a message.</summary>
        public (FormField Field, string Message) Blocking()
        {
            foreach (var f in Fields)
            {
                if (f.Hidden) continue;
                var required = f.Spec.Required == true;
                if ((f.Type == "choice" || f.Type == "options") && required && (f.Items.Count == 0 || f.Value == null))
                    return (f, f.Items.Count == 0 ? "There is nothing to pick for the current settings." : "Pick an entry.");
                if (f.Optional && required && !f.On) return (f, "Switch it on and enter a value.");
                if (f.Converted && !(f.Optional && !f.On))
                {
                    var min = f.Spec.Min == null ? (double?)null : Units.SpecToSi(f.Spec.Unit, f.Spec.Min.Value);
                    var max = f.Spec.Max == null ? (double?)null : Units.SpecToSi(f.Spec.Unit, f.Spec.Max.Value);
                    var below = min != null && f.Si < min.Value && !Same(f.Si, min.Value, 1e-6);
                    var above = max != null && f.Si > max.Value && !Same(f.Si, max.Value, 1e-6);
                    if (below || above)
                        return (f, $"Enter a value from {(min == null ? "-" : Units.Show(min.Value, f.Unit, 4))} to {(max == null ? "-" : Units.Show(max.Value, f.Unit, 4))}.");
                }
            }
            return (null, null);
        }

        /// <summary>
        /// Everything except selections and files into the inputs the macro and agent get: numbers in SI (null when an
        /// Optional is off or the data says n/a), sizes as double[] in m, choices as their entry, plus the display text, table
        /// value and selected row. Inputs hidden by ShowWhen are left out.
        /// </summary>
        public void Collect(ToolInputs inputs, Func<FormField, bool> include = null)
        {
            foreach (var f in Fields)
            {
                if (f.Type == "selection" || f.Type == "files" || f.Type == "info") continue;
                if (include != null && !include(f)) continue;
                if (f.ConditionHidden)
                {
                    inputs.SetMeta(f.Id, new ToolInputs.InputMeta { Hidden = true });
                    continue;
                }
                var meta = new ToolInputs.InputMeta { Hidden = f.NotApplicable, NotApplicable = f.NotApplicable };
                switch (f.Type)
                {
                    case "number":
                    case "integer":
                    {
                        var set = !f.NotApplicable && !(f.Optional && !f.On);
                        var si = f.Type == "integer" ? Math.Round(f.Si) : f.Si;
                        inputs.Set(f.Id, set ? (object)si : null);
                        meta.Si = set ? new JValue(si) : JValue.CreateNull();
                        meta.Table = f.TableSi;
                        meta.Edited = f.Edited;
                        meta.Unit = f.Converted ? f.Unit?.Ascii : null;
                        meta.Display = Describe(f);
                        break;
                    }
                    case "size":
                    {
                        var set = !(f.Optional && !f.On);
                        inputs.Set(f.Id, set ? f.SizeSi.ToArray() : null);
                        meta.Si = set ? new JArray(f.SizeSi.Select(v => (object)v)) : (JToken)JValue.CreateNull();
                        meta.Display = Describe(f);
                        break;
                    }
                    case "checkbox":
                        inputs.Set(f.Id, f.Checked);
                        break;
                    case "choice":
                    case "options":
                        inputs.Set(f.Id, f.Value);
                        if (f.IsData)
                        {
                            meta.Row = f.Row;
                            meta.RowUnique = f.RowUnique;
                            meta.Display = Describe(f);
                        }
                        break;
                    case "text":
                        inputs.Set(f.Id, f.Value ?? "");
                        break;
                    default:
                        continue;
                }
                inputs.SetMeta(f.Id, meta);
            }
        }

        /// <summary>
        /// What the agent reads for an input: "2000 psi (13.79 MPa)", "1.8 % (table 1.5 % - edited by the user)", "not set",
        /// "n/a for this grade", "600 x 400 x 150 mm", "6061-T651 [al_6061_t651]". Null for inputs that keep their raw value
        /// (checkboxes, plain choices, text).
        /// </summary>
        public string Describe(FormField f)
        {
            switch (f.Type)
            {
                case "number":
                case "integer":
                {
                    if (f.NotApplicable) return "n/a for this " + SourceName(f);
                    if (f.Optional && !f.On) return "not set";
                    var si = f.Type == "integer" ? Math.Round(f.Si) : f.Si;
                    var text = f.Type == "integer" ? Units.Format(si, 15) : f.Converted ? Units.Describe(f.Spec.Unit, si, f.Unit) : Units.Legacy(f.Spec.Unit, si);
                    // A table value the user kept reads as a plain value; an edited one says what the table had.
                    return f.TableSi != null && f.Edited ? $"{text} (table {Short(f, f.TableSi.Value)} - edited by the user)" : text;
                }
                case "size":
                {
                    if (f.Optional && !f.On) return "not set";
                    var metric = SizeText(f.SizeSi, Units.Spec("length"), ascii: true);
                    return Imperial ? $"{SizeText(f.SizeSi, Units.For("length", true), ascii: true)} ({metric})" : metric;
                }
                case "choice":
                case "options":
                {
                    if (!f.IsData || f.Value == null) return f.Value;
                    if (!f.RowUnique || f.Row == null) return f.Value;
                    var parts = new List<string>();
                    var id = ToolData.Text(f.Row["id"]);
                    if (id != null) parts.Add(id);
                    // Short data notes only, as material sheets have them ("medium", sources "S19, S20"); rows that cite
                    // whole references (mold grades) keep them in the row.
                    var confidence = ToolData.Text(f.Row["confidence"]);
                    var sources = f.Row["sources"] as JArray;
                    var keyed = sources != null && sources.Count > 0 && sources.All(x => x.Type == JTokenType.String && ((string)x).Length <= 12 && !((string)x).Contains(" "));
                    if (confidence != null && confidence.Length <= 20 && (keyed || f.Row["sources"] == null)) parts.Add("data confidence " + confidence);
                    if (keyed) parts.Add("sources " + string.Join(", ", sources.Select(x => (string)x)));
                    return parts.Count == 0 ? f.Value : $"{f.Value} [{string.Join("; ", parts)}]";
                }
            }
            return null;
        }

        private string SourceName(FormField f)
        {
            var source = Get(f.FilledBy);
            var label = (source?.Spec.Label ?? "").Trim();
            return label.Length == 0 ? "entry" : label.ToLowerInvariant();
        }

        private static string Short(FormField f, double si)
        {
            if (f.Type == "integer") return Units.Format(si, 15);
            if (f.Converted) return Units.Show(si, f.Unit, 6, ascii: true);
            switch (f.Quantity)
            {
                case "length": return Units.Format(si * 1000, 6) + " mm";
                case "angle": return Units.Format(si * 180 / Math.PI, 6) + " deg";
                case "percent": return Units.Format(si, 6) + " %";
            }
            return Units.Format(si, 6);
        }

        /// <summary>
        /// What the page remembers on OK (spec units): { id: { value, on, unit } } for inputs with Remember, and the unit
        /// choice of every field that offers one. Files, selections and text are never stored.
        /// </summary>
        public JObject RememberJson()
        {
            var result = new JObject();
            foreach (var f in Fields)
            {
                var entry = new JObject();
                if (f.Spec.Remember == true)
                {
                    switch (f.Type)
                    {
                        case "number":
                        case "integer":
                            if (!f.NotApplicable) entry["value"] = SiToSpec(f, f.Si);
                            if (f.Optional) entry["on"] = f.On;
                            break;
                        case "size":
                            entry["value"] = new JArray(f.SizeSi.Select(v => (object)(v * 1000.0)));
                            if (f.Optional) entry["on"] = f.On;
                            break;
                        case "checkbox":
                            entry["value"] = f.Checked;
                            break;
                        case "choice":
                        case "options":
                            if (f.Value != null) entry["value"] = f.Value;
                            break;
                    }
                }
                if (f.Converted && f.UnitChoices.Count > 0 && f.Unit != null) entry["unit"] = f.Unit.Ascii;
                if (entry.Count > 0) result[f.Id] = entry;
            }
            return result;
        }

        /// <summary>
        /// For pages that can't refill a dropdown once shown (spike S2): every entry list a data choice can have, by trying
        /// each value its parents can take. Null when there are more than <paramref name="cap"/> combinations.
        /// </summary>
        public List<List<string>> PossibleEntryLists(FormField f, int cap = 200)
        {
            if (f == null || !f.IsData) return null;
            var parents = f.Parents.Select(Get).Where(p => p != null).ToList();
            var domains = new List<(string Id, List<string> Values)>();
            foreach (var p in parents)
            {
                var values = PossibleValues(p, cap, 0);
                if (values == null) return null;
                domains.Add((p.Id, values));
            }
            long combos = 1;
            foreach (var d in domains) combos *= Math.Max(1, d.Values.Count);
            if (combos > cap) return null;
            var lists = new List<List<string>>();
            var seen = new HashSet<string>();
            foreach (var combo in Product(domains, 0, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)))
            {
                var items = _data.Entries(f.Spec, id => combo.TryGetValue(id, out var v) ? v : ValueOf(id));
                if (seen.Add(string.Join("\u0001", items))) lists.Add(items);
            }
            return lists;
        }

        private List<string> PossibleValues(FormField p, int cap, int depth)
        {
            if (depth > MaxDepth) return null;
            switch (p.Type)
            {
                case "options":
                    return p.Items.ToList();
                case "choice":
                    if (!p.IsData) return p.Items.ToList();
                    var lists = PossibleEntryLists(p, cap);
                    return lists?.SelectMany(x => x).Distinct().ToList();
                case "checkbox":
                    return new List<string> { "true", "false" };
            }
            var v = ValueOf(p.Id);
            return new List<string> { v };
        }

        private static IEnumerable<Dictionary<string, string>> Product(List<(string Id, List<string> Values)> domains, int i, Dictionary<string, string> current)
        {
            if (i == domains.Count)
            {
                yield return new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
                yield break;
            }
            var values = domains[i].Values.Count == 0 ? new List<string> { null } : domains[i].Values;
            foreach (var v in values)
            {
                current[domains[i].Id] = v;
                foreach (var c in Product(domains, i + 1, current)) yield return c;
            }
            current.Remove(domains[i].Id);
        }

        /// <summary>The page needs nothing newer than the original options (so it keeps every original code path).</summary>
        public bool AllPlain => Fields.All(f => f.IsPlain || f.Type == "selection" || f.Type == "files");
    }

    /// <summary>
    /// The values a tool's page remembers between runs (inputs with "Remember": true, and unit choices), per PC:
    /// %APPDATA%\SwAgent\tools\last-inputs\&lt;tool&gt;.json, in spec units. Saving merges, so an input that had nothing to
    /// store this time (n/a) keeps its last value.
    /// </summary>
    internal static class RememberStore
    {
        public static string DefaultDir => Path.Combine(UserTools.Root, "last-inputs");

        private static string PathOf(string tool, string dir) => Path.Combine(dir ?? DefaultDir, (tool ?? "tool").Trim().ToLowerInvariant() + ".json");

        public static JObject Load(string tool, string dir = null)
        {
            try
            {
                var path = PathOf(tool, dir);
                return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                Log.Warn($"Remembered inputs of '{tool}' unreadable: {ex.Message}");
                return null;
            }
        }

        public static void Save(string tool, JObject values, string dir = null)
        {
            if (values == null || values.Count == 0) return;
            try
            {
                var path = PathOf(tool, dir);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var all = Load(tool, dir) ?? new JObject();
                foreach (var p in values.Properties())
                {
                    if (all[p.Name] is JObject old && p.Value is JObject now)
                        foreach (var q in now.Properties()) old[q.Name] = q.Value.DeepClone();
                    else all[p.Name] = p.Value.DeepClone();
                }
                File.WriteAllText(path, all.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Warn($"Remembering the inputs of '{tool}' failed: {ex.Message}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Scripting;

namespace SwAgent.Tools
{
    /// <summary>
    /// The files and picked entities a form keeps outside ToolForm (the panel's ToolFormSession): a profile holds them only
    /// when the user asks ("Include the files and selections").
    /// </summary>
    internal sealed class DocumentInputs
    {
        public Func<string, IReadOnlyList<string>> Files;
        public Action<string, List<string>> SetFiles;
        public Func<string, IReadOnlyList<ToolInputs.SelectedItem>> Selected;
        public Action<string, List<ToolInputs.SelectedItem>> SetSelection;
    }

    /// <summary>
    /// A tool form's values as a profile holds them, and back. Numbers are text in the unit the user sees ("2000 psi",
    /// "300 mm", "1.5 %", integers "10000"), sizes "600 x 400 x 150 mm", Optional inputs { "on": false } or { "on": true,
    /// "value": "23 kg" }, data choices their row id ("al_6061_t651"), other choices and text as written, checkboxes
    /// true/false; files are paths, selections { type, label, detail, ref }. Loading checks every value against the tool as
    /// it is now: inputs that no longer exist, values outside Min/Max, choices that no longer exist and unknown units are
    /// listed and keep the default. No SolidWorks types: tested headless.
    /// </summary>
    internal static class ProfileValues
    {
        private static readonly Regex NumberPattern = new Regex(@"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?");

        /// <summary>The outcome of loading values into a form: the inputs they set, and what was ignored or kept at its default.</summary>
        internal sealed class Result
        {
            public readonly HashSet<string> Applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Warnings = new List<string>();
        }

        internal enum Mode
        {
            /// <summary>What a profile saves: every value except one a data row filled in and the user left as the table had it.</summary>
            Profile,

            /// <summary>Every value (comparisons: what changed since a profile was loaded, what differs from the defaults).</summary>
            All,
        }

        public static bool IsDocumentInput(FormField f) => f.Type == "files" || f.Type == "selection";

        /// <summary>The unit a number is written in: its own unit choice (converted), the document's length or angle, else the spec unit.</summary>
        internal static UnitDef UnitOf(FormField f, bool imperial)
        {
            if (f.Type == "size") return Units.For("length", imperial);
            if (f.Converted) return f.Unit;
            return f.Quantity == "length" || f.Quantity == "angle" ? Units.For(f.Quantity, imperial) : Units.Spec(f.Spec.Unit);
        }

        /// <summary>The unit a hand-written value without one is read in: the spec unit (mm, degrees, MPa...).</summary>
        private static UnitDef SpecUnit(FormField f) => Units.Spec(f.Type == "size" ? "length" : f.Spec.Unit);

        /// <summary>A value a data row filled in that the user kept as it was: it follows the data (a corrected table value reaches the profile).</summary>
        internal static bool FollowsTable(FormField f)
        {
            if (f.FilledBy == null || f.NotApplicable) return false;
            switch (f.Type)
            {
                case "number":
                case "integer":
                    return f.TableSi != null && !f.Edited;
                case "choice":
                case "options":
                    return f.TableText != null && !ToolForm.ChoiceEdited(f);
                case "checkbox":
                    return f.TableText != null && (f.TableText == "true") == f.Checked;
                case "text":
                    return true;
            }
            return false;
        }

        // ---- form -> profile ----

        /// <summary>The form's values (document inputs only with doc). Hidden inputs keep theirs; n/a values are left out.</summary>
        public static JObject Capture(ToolForm form, Mode mode, DocumentInputs doc = null)
        {
            var values = new JObject();
            foreach (var f in form.Fields)
            {
                if (values[f.Id] != null) continue; // declared twice: the first one
                if (mode == Mode.Profile && FollowsTable(f)) continue;
                var v = ValueOf(f, form.Imperial, doc);
                if (v != null) values[f.Id] = v;
            }
            return values;
        }

        /// <summary>One input's value as a profile holds it (null when there is nothing to hold).</summary>
        public static JToken ValueOf(FormField f, bool imperial, DocumentInputs doc = null)
        {
            switch (f.Type)
            {
                case "number":
                case "integer":
                {
                    JToken value = null;
                    if (!f.NotApplicable && !(f.Optional && !f.On))
                        value = f.Type == "integer" ? (JToken)Units.Format(Math.Round(f.Si), 15) : NumberText(f.Si, UnitOf(f, imperial));
                    if (!f.Optional) return value;
                    var o = new JObject { ["on"] = f.On };
                    if (value != null) o["value"] = value;
                    return o;
                }
                case "size":
                {
                    var unit = UnitOf(f, imperial);
                    var value = string.Join(" x ", f.SizeSi.Select(v => Units.Format(unit.FromSi(v), 10))) + " " + unit.Ascii;
                    if (!f.Optional) return value;
                    return f.On ? new JObject { ["on"] = true, ["value"] = value } : new JObject { ["on"] = false };
                }
                case "checkbox":
                    return f.Checked;
                case "choice":
                case "options":
                    if (f.Value == null) return null;
                    // A data choice names its row by id, so a renamed entry still loads (an entry several rows share, such as a
                    // family, has no row of its own: its text).
                    return f.IsData && f.Row != null && f.RowUnique && ToolData.Text(f.Row["id"] as JValue) is string id ? id : f.Value;
                case "text":
                    return f.Value ?? "";
                case "files":
                {
                    var files = doc?.Files?.Invoke(f.Id);
                    return files == null || files.Count == 0 ? null : new JArray(files);
                }
                case "selection":
                {
                    var items = doc?.Selected?.Invoke(f.Id);
                    if (items == null || items.Count == 0) return null;
                    return new JArray(items.Select(i => new JObject { ["type"] = i.Type, ["label"] = i.Label, ["detail"] = i.Detail, ["ref"] = i.Ref }));
                }
            }
            return null;
        }

        private static string NumberText(double si, UnitDef unit) =>
            Units.Format(unit.FromSi(si), 10) + (string.IsNullOrEmpty(unit.Ascii) ? "" : " " + unit.Ascii);

        // ---- profile -> form ----

        /// <summary>
        /// Values into the form, in the form's order (a choice before what it fills and the choices that depend on it), then
        /// again until nothing changes, so a value a later choice filled over is set back. keepUnits leaves the unit choices
        /// as they are (going back to the tool defaults). The profile itself is never changed.
        /// </summary>
        public static Result Apply(ToolForm form, JObject values, DocumentInputs doc = null, bool keepUnits = false)
        {
            var result = new Result();
            if (values == null) return result;
            foreach (var p in values.Properties())
            {
                var f = form.Get(p.Name);
                if (f == null || f.Type == "info") result.Warnings.Add($"Ignored '{p.Name}': the tool has no such input now.");
            }
            for (var pass = 0; pass < 4; pass++)
            {
                var changed = false;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in form.Fields)
                {
                    if (f.Type == "info" || !seen.Add(f.Id) || !values.TryGetValue(f.Id, StringComparison.OrdinalIgnoreCase, out var token)) continue;
                    var warnings = pass == 0 ? result.Warnings : null;
                    var count = warnings?.Count ?? 0;
                    changed |= ApplyOne(form, f, token, warnings, doc, keepUnits);
                    if (pass == 0 && warnings.Count == count) result.Applied.Add(f.Id);
                }
                if (!changed) break;
            }
            return result;
        }

        private static string Label(FormField f) => string.IsNullOrWhiteSpace(f.Spec.Label) ? f.Id : f.Spec.Label.Trim();

        private static bool ApplyOne(ToolForm form, FormField f, JToken token, List<string> warnings, DocumentInputs doc, bool keepUnits)
        {
            void Warn(string text) => warnings?.Add(text);
            var changed = false;
            var value = token;
            if (token is JObject wrapper && (f.Type == "number" || f.Type == "integer" || f.Type == "size"))
            {
                // { "on": false } / { "on": true, "value": "23 kg" }: an Optional input's switch
                value = wrapper["value"];
                if (wrapper["on"] is JValue on && on.Type == JTokenType.Boolean)
                {
                    if (f.Optional) changed |= form.SetOn(f, (bool)on);
                    else if (!(bool)on) Warn($"Kept '{Label(f)}' on: it can't be switched off now.");
                }
                else if (f.Optional && value != null) changed |= form.SetOn(f, true);
            }
            else if (f.Optional && token != null && token.Type != JTokenType.Null)
            {
                changed |= form.SetOn(f, true);
            }
            if (value == null || value.Type == JTokenType.Null) return changed;

            switch (f.Type)
            {
                case "number":
                case "integer":
                {
                    if (!TryNumber(f, value, out var si, out var unit, out var error))
                    {
                        Warn($"Kept the default for '{Label(f)}': {error}");
                        return changed;
                    }
                    if (f.Type == "integer") si = Math.Round(si);
                    if (Outside(f, si, out var range))
                    {
                        Warn($"Kept the default for '{Label(f)}': {Shown(f, value)} is outside {range}.");
                        return changed;
                    }
                    if (!keepUnits && f.Converted && unit != null && f.UnitChoices.Contains(unit)) changed |= form.SetUnit(f, unit.Ascii);
                    changed |= form.SetNumber(f, si);
                    return changed;
                }
                case "size":
                {
                    if (!TrySize(f, value, out var sizes, out var error))
                    {
                        Warn($"Kept the default for '{Label(f)}': {error}");
                        return changed;
                    }
                    var (min, max) = ToolFormSession.RangeSi(f);
                    if (sizes.Any(v => min != null && v < min.Value && !ToolForm.Same(v, min.Value, ToolForm.LimitTolerance) ||
                                       max != null && v > max.Value && !ToolForm.Same(v, max.Value, ToolForm.LimitTolerance)))
                    {
                        Warn($"Kept the default for '{Label(f)}': {Shown(f, value)} is outside {RangeText(f, min, max)}.");
                        return changed;
                    }
                    for (var k = 0; k < sizes.Length; k++) changed |= form.SetSize(f, k, sizes[k]);
                    return changed;
                }
                case "checkbox":
                {
                    var b = value.Type == JTokenType.Boolean ? (bool)value : ParseBool(ToolData.Text(value));
                    if (b == null)
                    {
                        Warn($"Kept the default for '{Label(f)}': '{ToolData.Text(value)}' is not yes or no.");
                        return changed;
                    }
                    return form.SetChecked(f, b.Value) | changed;
                }
                case "choice":
                case "options":
                {
                    var text = ToolData.Text(value)?.Trim();
                    var entry = Entry(f, text);
                    if (entry == null)
                    {
                        // A data row that exists but isn't offered for the settings above it, or one that is gone.
                        var exists = f.IsData && form.Data.Rows(f.Spec.ChoicesFrom).Any(r => string.Equals(ToolData.Text(r["id"] as JValue), text, StringComparison.OrdinalIgnoreCase));
                        Warn(exists
                            ? $"Kept the default for '{Label(f)}': '{text}' isn't offered with the other settings."
                            : $"Kept the default for '{Label(f)}': '{text}' is no longer in its list.");
                        return changed;
                    }
                    return form.SetChoice(f, entry) | changed;
                }
                case "text":
                    return form.SetText(f, ToolData.Text(value) ?? "") | changed;
                case "files" when doc?.SetFiles == null:
                case "selection" when doc?.SetSelection == null:
                    Warn($"Left '{Label(f)}' as it is: the PropertyManager page takes files and selections only from you.");
                    return changed;
                case "files":
                {
                    var paths = (value is JArray a ? a.Select(x => ToolData.Text(x)) : new[] { ToolData.Text(value) })
                        .Where(x => !string.IsNullOrWhiteSpace(x) && Path.IsPathRooted(x)).ToList();
                    var missing = paths.Where(x => !File.Exists(x)).ToList();
                    if (missing.Count > 0) Warn($"Left out of '{Label(f)}' (not found): {string.Join(", ", missing.Select(Path.GetFileName))}.");
                    doc.SetFiles(f.Id, paths.Except(missing).ToList());
                    return changed;
                }
                case "selection":
                {
                    if (!(value is JArray items)) return changed;
                    // Found again by their persistent reference at OK (or listed as no longer in the model).
                    doc.SetSelection(f.Id, items.OfType<JObject>().Where(i => !string.IsNullOrEmpty((string)i["ref"])).Select(i => new ToolInputs.SelectedItem
                    {
                        Type = (string)i["type"], Label = (string)i["label"], Detail = (string)i["detail"], Ref = (string)i["ref"],
                    }).ToList());
                    return changed;
                }
            }
            return changed;
        }

        /// <summary>The entry a profile value names: a data choice's row id (else its entry), a plain choice's entry (ignoring case).</summary>
        private static string Entry(FormField f, string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (f.IsData)
            {
                var row = f.Rows.FirstOrDefault(r => string.Equals(ToolData.Text(r["id"] as JValue), text, StringComparison.OrdinalIgnoreCase));
                if (row != null) return ToolData.KeyText(row, f.Spec.ChoiceKey);
            }
            return f.Items.FirstOrDefault(i => string.Equals(i, text, StringComparison.Ordinal))
                   ?? f.Items.FirstOrDefault(i => string.Equals(i, text, StringComparison.OrdinalIgnoreCase));
        }

        private static bool? ParseBool(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "true": case "yes": case "on": case "1": return true;
                case "false": case "no": case "off": case "0": return false;
            }
            return null;
        }

        /// <summary>"2000 psi", "300", 300 -> SI; the unit must be one of the input's quantity (none: the spec unit).</summary>
        internal static bool TryNumber(FormField f, JToken value, out double si, out UnitDef unit, out string error)
        {
            si = 0;
            unit = null;
            error = null;
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                unit = f.Type == "integer" ? null : SpecUnit(f);
                si = unit == null ? (double)value : unit.ToSiValue((double)value);
                return true;
            }
            var text = (ToolData.Text(value) ?? "").Trim();
            var m = NumberPattern.Match(text);
            if (!m.Success || m.Index != 0 || !double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                error = $"'{text}' is not a number.";
                return false;
            }
            var symbol = text.Substring(m.Length).Trim();
            if (f.Type == "integer")
            {
                if (symbol.Length > 0)
                {
                    error = $"'{text}' should be a plain number.";
                    return false;
                }
                si = n;
                return true;
            }
            unit = symbol.Length == 0 ? SpecUnit(f) : Units.Find(f.Quantity ?? "length", symbol);
            if (unit == null)
            {
                error = $"'{symbol}' is not a unit it takes.";
                return false;
            }
            si = unit.ToSiValue(n);
            return true;
        }

        /// <summary>"600 x 400 x 150 mm" (or an array in mm) -> metres, as many as the input has boxes.</summary>
        internal static bool TrySize(FormField f, JToken value, out double[] si, out string error)
        {
            si = null;
            error = null;
            var dims = f.SizeSi?.Length ?? f.Dims;
            if (value is JArray array)
            {
                var numbers = array.Select(ToolData.Number).ToList();
                if (numbers.Count < dims || numbers.Any(x => x == null))
                {
                    error = $"it needs {dims} numbers.";
                    return false;
                }
                si = numbers.Take(dims).Select(x => x.Value / 1000.0).ToArray();
                return true;
            }
            var text = (ToolData.Text(value) ?? "").Trim();
            var matches = NumberPattern.Matches(text).Cast<Match>().ToList();
            if (matches.Count < dims)
            {
                error = $"'{text}' needs {dims} numbers.";
                return false;
            }
            var last = matches[dims - 1];
            var symbol = text.Substring(last.Index + last.Length).Trim();
            var unit = symbol.Length == 0 ? SpecUnit(f) : Units.Find("length", symbol);
            if (unit == null)
            {
                error = $"'{symbol}' is not a length unit.";
                return false;
            }
            si = matches.Take(dims).Select(x => unit.ToSiValue(double.Parse(x.Value, NumberStyles.Float, CultureInfo.InvariantCulture))).ToArray();
            return true;
        }

        private static bool Outside(FormField f, double si, out string range)
        {
            var (min, max) = ToolFormSession.RangeSi(f);
            range = RangeText(f, min, max);
            return min != null && si < min.Value && !ToolForm.Same(si, min.Value, ToolForm.LimitTolerance)
                   || max != null && si > max.Value && !ToolForm.Same(si, max.Value, ToolForm.LimitTolerance);
        }

        private static string RangeText(FormField f, double? min, double? max)
        {
            var unit = f.Type == "integer" ? null : f.Type == "size" ? Units.Spec("length") : f.Converted ? Units.Spec(f.Spec.Unit) : SpecUnit(f);
            string Show(double v) => unit == null ? Units.Format(v, 6) : Units.Show(v, unit, 4, ascii: true);
            return $"{(min == null ? "-" : Show(min.Value))} to {(max == null ? "-" : Show(max.Value))}";
        }

        private static string Shown(FormField f, JToken value) => ToolData.Text(value) ?? value.ToString();

        // ---- comparing ----

        /// <summary>The inputs whose values differ between two captures (a unit change alone is no difference).</summary>
        public static List<string> Differences(ToolForm form, JObject a, JObject b)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in form.Fields)
            {
                if (f.Type == "info" || !seen.Add(f.Id)) continue;
                if (!Same(f, a?[f.Id], b?[f.Id])) list.Add(f.Id);
            }
            return list;
        }

        /// <summary>Two values of one input mean the same: numbers within a millionth after conversion, text ignoring case for choices.</summary>
        public static bool Same(FormField f, JToken a, JToken b)
        {
            bool Missing(JToken t) => t == null || t.Type == JTokenType.Null;
            if (Missing(a) || Missing(b)) return Missing(a) && Missing(b);
            switch (f.Type)
            {
                case "number":
                case "integer":
                case "size":
                {
                    var (onA, va) = Split(a);
                    var (onB, vb) = Split(b);
                    if (onA != onB) return false;
                    if (Missing(va) || Missing(vb)) return Missing(va) && Missing(vb);
                    if (f.Type == "size")
                        return TrySize(f, va, out var sa, out _) && TrySize(f, vb, out var sb, out _) && sa.Zip(sb, (x, y) => ToolForm.Same(x, y, 1e-6)).All(x => x);
                    return TryNumber(f, va, out var na, out _, out _) && TryNumber(f, vb, out var nb, out _, out _) && ToolForm.Same(na, nb, 1e-6);
                }
                case "choice":
                case "options":
                    return string.Equals(ToolData.Text(a)?.Trim(), ToolData.Text(b)?.Trim(), StringComparison.OrdinalIgnoreCase);
                default:
                    return JToken.DeepEquals(a, b);
            }
        }

        private static (bool On, JToken Value) Split(JToken t) =>
            t is JObject o ? (o["on"]?.Type != JTokenType.Boolean || (bool)o["on"], o["value"]) : (true, t);
    }
}

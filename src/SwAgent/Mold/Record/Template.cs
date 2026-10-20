using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>
    /// The small template language of data/setup-sheet.html and data/machining-sheet.html (agreed with Track E), so the
    /// add-in needs no template library:
    ///   {{path}}                 a value from the context (dotted path, array index allowed: bodies.0.id); HTML-escaped
    ///   {{path|unit}}            a number in both unit systems, the document's first: "17.1 lb (7.75 kg)"; units:
    ///                            mm, mm2, mm3, kg, g, MPa, kN, Nm, degC, min, pct, plus length/mass/pressure/force/torque/temperature
    ///   {{#each path}}...{{/each}}   repeats for each array item; inside, paths resolve on the item first, then the root;
    ///                            {{this}} is the item, {{@index}} counts from 0 and {{@number}} from 1
    ///   {{#if path}}...{{else}}...{{/if}}   false, null, "", 0 and [] are false
    /// A path that does not exist renders as "-" and is reported in `unresolved` (tests require none on a full record).
    /// </summary>
    public static class Template
    {
        public static string Render(string template, JObject context, bool imperial, List<string> unresolved = null)
        {
            var fmt = new Fmt(imperial);
            var stack = new List<JToken> { context };
            return RenderBlock(template, stack, context, fmt, unresolved ?? new List<string>(), -1);
        }

        private static string RenderBlock(string t, List<JToken> scope, JObject root, Fmt fmt, List<string> unresolved, int index)
        {
            var sb = new StringBuilder();
            var i = 0;
            while (i < t.Length)
            {
                var open = t.IndexOf("{{", i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(t, i, t.Length - i); break; }
                sb.Append(t, i, open - i);
                var close = t.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0) { sb.Append(t, open, t.Length - open); break; }
                var tag = t.Substring(open + 2, close - open - 2).Trim();
                i = close + 2;
                if (tag.StartsWith("#each ") || tag.StartsWith("#if "))
                {
                    var kind = tag.StartsWith("#each ") ? "each" : "if";
                    var path = tag.Substring(kind.Length + 1).Trim();
                    var end = FindClose(t, i, kind, out var elseAt, out var after);
                    if (end < 0) throw new FormatException("{{#" + kind + " " + path + "}} has no {{/" + kind + "}}");
                    var body = t.Substring(i, (elseAt >= 0 ? elseAt : end) - i);
                    var elseBody = elseAt >= 0 ? t.Substring(elseAt + "{{else}}".Length, end - elseAt - "{{else}}".Length) : "";
                    var value = Resolve(path, scope, root, index, out var found);
                    if (!found) unresolved.Add(path);
                    if (kind == "each")
                    {
                        if (value is JArray arr)
                        {
                            for (var k = 0; k < arr.Count; k++)
                            {
                                var inner = new List<JToken>(scope) { arr[k] };
                                sb.Append(RenderBlock(body, inner, root, fmt, unresolved, k));
                            }
                        }
                    }
                    else sb.Append(RenderBlock(Truthy(value) ? body : elseBody, scope, root, fmt, unresolved, index));
                    i = after;
                    continue;
                }
                if (tag.StartsWith("/") || tag == "else") continue;   // stray closers are dropped
                var bar = tag.IndexOf('|');
                var p = bar >= 0 ? tag.Substring(0, bar).Trim() : tag;
                var unit = bar >= 0 ? tag.Substring(bar + 1).Trim() : null;
                var v = Resolve(p, scope, root, index, out var ok);
                if (!ok) { unresolved.Add(p); sb.Append("-"); continue; }
                sb.Append(WebUtility.HtmlEncode(Format(v, unit, fmt)));
            }
            return sb.ToString();
        }

        /// <summary>Finds the matching {{/kind}} (nesting aware) and an {{else}} at the same depth.</summary>
        private static int FindClose(string t, int from, string kind, out int elseAt, out int after)
        {
            elseAt = -1;
            after = -1;
            var depth = 0;
            var i = from;
            while (true)
            {
                var open = t.IndexOf("{{", i, StringComparison.Ordinal);
                if (open < 0) return -1;
                var close = t.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0) return -1;
                var tag = t.Substring(open + 2, close - open - 2).Trim();
                if (tag.StartsWith("#" + kind + " ")) depth++;
                else if (tag == "/" + kind)
                {
                    if (depth == 0) { after = close + 2; return open; }
                    depth--;
                }
                else if (tag == "else" && depth == 0 && kind == "if" && elseAt < 0) elseAt = open;
                i = close + 2;
            }
        }

        private static JToken Resolve(string path, List<JToken> scope, JObject root, int index, out bool found)
        {
            found = true;
            if (path == "this") return scope[scope.Count - 1];
            if (path == "@index") return index;
            if (path == "@number") return index + 1;
            var p = path.StartsWith("this.") ? path.Substring(5) : path;
            for (var s = scope.Count - 1; s >= 0; s--)
            {
                if (scope[s] is JContainer c)
                {
                    var t = Select(c, p);
                    if (t != null) return t;
                }
                if (path.StartsWith("this.")) break;
            }
            var r = Select(root, p);
            if (r != null) return r;
            found = false;
            return null;
        }

        private static JToken Select(JContainer c, string path)
        {
            try { return c.SelectToken(IndexSegments(path)); }
            catch (Newtonsoft.Json.JsonException) { return null; }
        }

        /// <summary>"bodies.0.id" -> "bodies[0].id": SelectToken only indexes arrays with brackets.</summary>
        private static string IndexSegments(string path)
        {
            var parts = path.Split('.');
            var sb = new StringBuilder();
            for (var k = 0; k < parts.Length; k++)
            {
                var seg = parts[k];
                if (k > 0 && seg.Length > 0 && seg.All(char.IsDigit)) sb.Append('[').Append(seg).Append(']');
                else
                {
                    if (k > 0) sb.Append('.');
                    sb.Append(seg);
                }
            }
            return sb.ToString();
        }

        private static bool Truthy(JToken v)
        {
            if (v == null) return false;
            switch (v.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return false;
                case JTokenType.Boolean: return (bool)v;
                case JTokenType.String: return ((string)v).Length > 0;
                case JTokenType.Integer:
                case JTokenType.Float: return Math.Abs((double)v) > 0;
                case JTokenType.Array: return ((JArray)v).Count > 0;
                case JTokenType.Object: return ((JObject)v).HasValues;
                default: return true;
            }
        }

        public static string Format(JToken v, string unit, Fmt fmt)
        {
            if (v == null || v.Type == JTokenType.Null) return "-";
            if (unit != null && (v.Type == JTokenType.Float || v.Type == JTokenType.Integer))
            {
                var x = (double)v;
                switch (unit.ToLowerInvariant())
                {
                    case "mm": case "length": return fmt.Len(x);
                    case "mm2": case "area": return fmt.Area(x);
                    case "mm3": case "volume": return Si.F(x / 1000, 4) + " cm³ (" + Si.F(x / Si.In3ToMm3, 4) + " in³)";
                    case "kg": case "mass": return fmt.Mass(x);
                    case "g": return fmt.Grams(x);
                    case "mpa": case "pressure": return fmt.P(x);
                    case "kn": case "force": return fmt.Force(x);
                    case "nm": case "torque": return Si.F(x / Si.FtLbfToNm, 4) + " lbf·ft (" + Si.F(x, 4) + " N·m)";
                    case "degc": case "temperature": return fmt.Temp(x);
                    case "min": return Si.F(x, 4) + " min";
                    case "pct": case "%": return Si.F(x, 4) + " %";
                    default: return Si.F(x, 4) + " " + unit;
                }
            }
            switch (v.Type)
            {
                case JTokenType.Float: return Si.F((double)v, 4);
                case JTokenType.Integer: return ((long)v).ToString(CultureInfo.InvariantCulture);
                case JTokenType.Boolean: return (bool)v ? "yes" : "no";
                case JTokenType.String: return (string)v;
                case JTokenType.Array: return string.Join(", ", ((JArray)v).Select(e => Format(e, unit, fmt)));
                default: return v.ToString(Newtonsoft.Json.Formatting.None);
            }
        }
    }
}

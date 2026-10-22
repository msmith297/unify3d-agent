using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SwAgent.Plugins
{
    /// <summary>
    /// The YAML front matter of Claude Code commands, skills and agents ("---" lines around "key: value" pairs). The subset
    /// those files use: plain, quoted and block (| and &gt;) scalars, inline [a, b] lists and "- item" lists. Values are
    /// strings or arrays of strings; anything fancier stays as its text. No SolidWorks types: tested headless.
    /// </summary>
    internal sealed class FrontMatter
    {
        /// <summary>Keys (case-insensitive) to a JValue string or a JArray of strings.</summary>
        public readonly Dictionary<string, JToken> Values = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The text after the closing "---" (the whole text when there is no front matter).</summary>
        public string Body = "";

        /// <summary>The file starts with a front-matter block that is closed.</summary>
        public bool Present;

        /// <summary>Problems a writer should fix (an unclosed block, a line that isn't "key: value").</summary>
        public readonly List<string> Errors = new List<string>();

        public string this[string key] => Get(key);

        /// <summary>A value as one string: a list joins with ", "; null when absent or blank.</summary>
        public string Get(string key)
        {
            if (!Values.TryGetValue(key, out var v) || v == null) return null;
            var s = v is JArray a ? string.Join(", ", a.Select(x => (string)x)) : (string)v;
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        /// <summary>A value as a list: a YAML list, or a comma-separated string ("Read, Grep"); null when absent.</summary>
        public string[] List(string key)
        {
            if (!Values.TryGetValue(key, out var v) || v == null) return null;
            if (v is JArray a) return a.Select(x => ((string)x ?? "").Trim()).Where(x => x.Length > 0).ToArray();
            return ((string)v ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        }

        public bool Flag(string key) => new[] { "true", "yes", "1", "on" }.Contains((Get(key) ?? "").ToLowerInvariant());

        public static FrontMatter Parse(string text)
        {
            var fm = new FrontMatter();
            text = (text ?? "").Replace("\r\n", "\n");
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1); // not StartsWith: culture rules ignore a BOM
            var lines = text.Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "---")
            {
                fm.Body = text;
                return fm;
            }
            var end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
            if (end < 0)
            {
                fm.Errors.Add("The front matter has no closing '---' line.");
                fm.Body = text;
                return fm;
            }
            fm.Present = true;
            fm.Body = string.Join("\n", lines.Skip(end + 1)).TrimStart('\n');
            for (var i = 1; i < end; i++)
            {
                var line = lines[i];
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#")) continue;
                if (char.IsWhiteSpace(line[0]))
                {
                    fm.Errors.Add($"Line {i + 1}: unexpected indentation: \"{line.Trim()}\".");
                    continue;
                }
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    fm.Errors.Add($"Line {i + 1}: expected \"key: value\", got \"{line.Trim()}\".");
                    continue;
                }
                var key = line.Substring(0, colon).Trim();
                var rest = line.Substring(colon + 1).Trim();
                // Indented lines that follow belong to this key (a block scalar, a list or a folded plain value).
                var block = new List<string>();
                while (i + 1 < end && (lines[i + 1].Length == 0 || char.IsWhiteSpace(lines[i + 1][0]) || lines[i + 1].StartsWith("- ")))
                    block.Add(lines[++i]);
                while (block.Count > 0 && block[block.Count - 1].Trim().Length == 0) block.RemoveAt(block.Count - 1);

                if (rest == "|" || rest == ">" || rest == "|-" || rest == ">-" || rest == "|+" || rest == ">+")
                {
                    var indent = block.Where(b => b.Trim().Length > 0).Select(b => b.Length - b.TrimStart().Length).DefaultIfEmpty(0).Min();
                    var body = block.Select(b => b.Length >= indent ? b.Substring(indent) : b.Trim()).ToList();
                    fm.Values[key] = rest[0] == '|' ? string.Join("\n", body) : Fold(body);
                }
                else if (rest.Length == 0 && block.Count > 0 && block.All(b => b.Trim().Length == 0 || b.TrimStart().StartsWith("-")))
                {
                    fm.Values[key] = new JArray(block.Where(b => b.Trim().Length > 0).Select(b => Unquote(b.TrimStart().Substring(1).Trim())));
                }
                else if (rest.StartsWith("[") && rest.EndsWith("]"))
                {
                    fm.Values[key] = new JArray(SplitInline(rest.Substring(1, rest.Length - 2)).Select(Unquote).Where(s => s.Length > 0));
                }
                else
                {
                    // A plain value can continue on indented lines (folded with spaces).
                    var all = new[] { rest }.Concat(block.Select(b => b.Trim())).Where(s => s.Length > 0);
                    fm.Values[key] = Unquote(string.Join(" ", all));
                }
            }
            return fm;
        }

        /// <summary>Folded block: lines join with spaces, an empty line is a line break.</summary>
        private static string Fold(List<string> lines)
        {
            var sb = new StringBuilder();
            foreach (var l in lines)
            {
                if (l.Trim().Length == 0) sb.Append('\n');
                else
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append(' ');
                    sb.Append(l.Trim());
                }
            }
            return sb.ToString();
        }

        private static IEnumerable<string> SplitInline(string s)
        {
            var sb = new StringBuilder();
            char quote = '\0';
            foreach (var c in s)
            {
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    sb.Append(c);
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                    sb.Append(c);
                }
                else if (c == ',')
                {
                    yield return sb.ToString().Trim();
                    sb.Clear();
                }
                else sb.Append(c);
            }
            if (sb.ToString().Trim().Length > 0) yield return sb.ToString().Trim();
        }

        internal static string Unquote(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
            if (s.Length >= 2 && s[0] == '\'' && s[s.Length - 1] == '\'') return s.Substring(1, s.Length - 2).Replace("''", "'");
            return s;
        }
    }
}

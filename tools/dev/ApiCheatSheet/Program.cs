using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ApiCheatSheet
{
    /// <summary>
    /// Writes the agent's SolidWorks API cheat sheet (ui/src/agent/cad/swApiCheatsheet.ts) from the curated member list
    /// (members.json) and gotchas (notes.md), with every signature reflected from the interop the add-in loads - so the sheet
    /// can't drift from the real API. Usage (from the repository root, or pass root=&lt;repo&gt;):
    ///   ApiCheatSheet.exe [write]            regenerate the .ts
    ///   ApiCheatSheet.exe check              exit 1 when the shipped .ts differs from the interop, a member is missing or the
    ///                                        sheet is over its token budget
    ///   ApiCheatSheet.exe find Name[,Name]   every interface that has a member of that name, with signatures (for curating)
    ///   ApiCheatSheet.exe type IName [part]  every member of one interface (optionally filtered)
    /// Options: root=&lt;repo&gt;, interop=&lt;path to SolidWorks.Interop.sldworks.dll&gt; (default: the copy next to the exe).
    /// </summary>
    internal static class Program
    {
        private const string OutRel = "ui/src/agent/cad/swApiCheatsheet.ts";
        private const string DirRel = "tools/dev/ApiCheatSheet";

        private static int Main(string[] args)
        {
            try
            {
                var opts = args.Where(a => a.Contains("=")).ToDictionary(a => a.Substring(0, a.IndexOf('=')), a => a.Substring(a.IndexOf('=') + 1));
                var rest = args.Where(a => !a.Contains("=")).ToList();
                var cmd = rest.Count > 0 ? rest[0].TrimStart('-').ToLowerInvariant() : "write";
                var api = Api.Load(opts.TryGetValue("interop", out var dll) ? dll : Path.Combine(AppContext.BaseDirectory, "SolidWorks.Interop.sldworks.dll"));

                switch (cmd)
                {
                    case "find":
                        foreach (var name in rest.Skip(1).SelectMany(r => r.Split(',')))
                        {
                            var hits = api.Find(name.Trim()).ToList();
                            Console.WriteLine(hits.Count == 0 ? $"{name}: no interface has it" : string.Join("\n", hits));
                        }
                        return 0;
                    case "type":
                        foreach (var line in api.Members(rest[1], rest.Count > 2 ? rest[2] : null)) Console.WriteLine(line);
                        return 0;
                    case "write":
                    case "check":
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown command '{cmd}'. Use write, check, find or type.");
                        return 2;
                }

                var root = opts.TryGetValue("root", out var r0) ? Path.GetFullPath(r0) : FindRoot();
                var sheet = Sheet.Build(api, Path.Combine(root, DirRel, "members.json"), Path.Combine(root, DirRel, "notes.md"));
                var outPath = Path.Combine(root, OutRel);
                var problems = sheet.Problems.ToList();
                Console.WriteLine($"{sheet.MemberCount} members, {sheet.Text.Length} chars, ~{sheet.Tokens} tokens (budget {sheet.Budget}).");

                if (cmd == "write")
                {
                    if (problems.Count > 0)
                    {
                        foreach (var p in problems) Console.Error.WriteLine("error: " + p);
                        return 1;
                    }
                    File.WriteAllText(outPath, sheet.TypeScript, new UTF8Encoding(false));
                    Console.WriteLine("Wrote " + OutRel);
                    return 0;
                }

                var shipped = File.Exists(outPath) ? File.ReadAllText(outPath).Replace("\r\n", "\n") : null;
                if (shipped == null) problems.Add($"{OutRel} is missing - run the tool without arguments to write it.");
                else if (shipped != sheet.TypeScript) problems.Add($"{OutRel} differs from what members.json, notes.md and the interop give now - regenerate it (first difference: {FirstDiff(shipped, sheet.TypeScript)}).");
                foreach (var p in problems) Console.Error.WriteLine("error: " + p);
                Console.WriteLine(problems.Count == 0 ? "check passed: the shipped sheet matches the interop." : "check FAILED.");
                return problems.Count == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 1;
            }
        }

        private static string FindRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                {
                    if (File.Exists(Path.Combine(d.FullName, DirRel, "members.json"))) return d.FullName;
                }
            }
            throw new InvalidOperationException("Repository root not found (no tools/dev/ApiCheatSheet/members.json above the current folder). Pass root=<repo>.");
        }

        private static string FirstDiff(string a, string b)
        {
            var la = a.Split('\n');
            var lb = b.Split('\n');
            for (var i = 0; i < Math.Max(la.Length, lb.Length); i++)
            {
                var x = i < la.Length ? la[i] : "(end)";
                var y = i < lb.Length ? lb[i] : "(end)";
                if (x != y) return $"line {i + 1}: shipped \"{Clip(x)}\", expected \"{Clip(y)}\"";
            }
            return "none";
        }

        private static string Clip(string s) => s.Length > 120 ? s.Substring(0, 120) + "..." : s;
    }

    /// <summary>The interop assemblies, loaded by path, and C# signatures of their members (the format of search_sw_api).</summary>
    internal sealed class Api
    {
        private readonly Dictionary<string, Type> _interfaces;
        private readonly Dictionary<string, Type> _enums;

        private Api(Dictionary<string, Type> interfaces, Dictionary<string, Type> enums)
        {
            _interfaces = interfaces;
            _enums = enums;
        }

        public static Api Load(string sldworksDll)
        {
            if (!File.Exists(sldworksDll)) throw new FileNotFoundException("Interop not found: " + sldworksDll);
            var sw = Assembly.LoadFrom(sldworksDll);
            var constDll = Path.Combine(Path.GetDirectoryName(sldworksDll) ?? ".", "SolidWorks.Interop.swconst.dll");
            var swconst = File.Exists(constDll) ? Assembly.LoadFrom(constDll) : null;
            var interfaces = sw.GetExportedTypes().Where(t => t.IsInterface).GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
            var enums = swconst == null
                ? new Dictionary<string, Type>()
                : swconst.GetExportedTypes().Where(t => t.IsEnum).GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
            return new Api(interfaces, enums);
        }

        /// <summary>"IFace.Member(...) : T" for a method, "IFace.Prop { get; set; } : T" for a property; null when the interface has no such member.</summary>
        public string Signature(string iface, string member)
        {
            if (!_interfaces.TryGetValue(iface, out var t)) return null;
            var m = t.GetMethods().FirstOrDefault(x => !x.IsSpecialName && x.Name == member);
            if (m != null) return $"{t.Name}.{m.Name}({Params(m)}) : {TypeName(m.ReturnType)}";
            var p = t.GetProperty(member);
            if (p == null) return null;
            var access = (p.CanRead ? "get;" : "") + (p.CanWrite ? " set;" : "");
            return $"{t.Name}.{p.Name} {{ {access.Trim()} }} : {TypeName(p.PropertyType)}";
        }

        /// <summary>
        /// "IFace.Member(27 parameters) : T" for a member a built-in helper covers (members.json "use"): the name and the shape,
        /// not the long parameter list; null when the interface has no such method.
        /// </summary>
        public string Brief(string iface, string member)
        {
            if (!_interfaces.TryGetValue(iface, out var t)) return null;
            var m = t.GetMethods().FirstOrDefault(x => !x.IsSpecialName && x.Name == member);
            if (m == null) return null;
            var n = m.GetParameters().Length;
            return $"{t.Name}.{m.Name}({n} parameter{(n == 1 ? "" : "s")}) : {TypeName(m.ReturnType)}";
        }

        /// <summary>"swX_e: A=1, B=2" (all values, or only the listed ones in that order); null when the enum or a value is missing.</summary>
        public string Enum(string name, IList<string> only, out string missing)
        {
            missing = null;
            if (!_enums.TryGetValue(name, out var t))
            {
                missing = name;
                return null;
            }
            var names = only != null && only.Count > 0 ? only : System.Enum.GetNames(t);
            var parts = new List<string>();
            foreach (var n in names)
            {
                if (!System.Enum.GetNames(t).Contains(n))
                {
                    missing = name + "." + n;
                    return null;
                }
                parts.Add($"{n}={Convert.ToInt64(System.Enum.Parse(t, n))}");
            }
            return $"{t.Name}: {string.Join(", ", parts)}";
        }

        public IEnumerable<string> Find(string member)
        {
            return _interfaces.Values.OrderBy(t => t.Name)
                .Select(t => Signature(t.Name, member))
                .Where(s => s != null);
        }

        public IEnumerable<string> Members(string iface, string filter)
        {
            if (!_interfaces.TryGetValue(iface, out var t)) return new[] { $"No interface {iface}." };
            return t.GetMethods().Where(m => !m.IsSpecialName).Select(m => m.Name)
                .Concat(t.GetProperties().Select(p => p.Name))
                .Distinct()
                .Where(n => filter == null || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => n)
                .Select(n => Signature(iface, n));
        }

        private static string Params(MethodInfo m)
        {
            return string.Join(", ", m.GetParameters().Select(p =>
            {
                var t = p.ParameterType;
                var prefix = t.IsByRef ? (p.IsOut ? "out " : "ref ") : "";
                return prefix + TypeName(t.IsByRef ? t.GetElementType() : t) + " " + p.Name;
            }));
        }

        private static string TypeName(Type t)
        {
            if (t == typeof(void)) return "void";
            if (t == typeof(int)) return "int";
            if (t == typeof(double)) return "double";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(object)) return "object";
            if (t == typeof(short)) return "short";
            if (t == typeof(long)) return "long";
            if (t == typeof(float)) return "float";
            if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
            return t.Name;
        }
    }

    /// <summary>The cheat sheet text and the TypeScript module that ships it.</summary>
    internal sealed class Sheet
    {
        public string Text;
        public string TypeScript;
        public int MemberCount;
        public int Budget;
        public int Tokens => (Text.Length + 3) / 4;
        public List<string> Problems = new List<string>();

        public static Sheet Build(Api api, string membersPath, string notesPath)
        {
            var spec = JObject.Parse(File.ReadAllText(membersPath));
            var sheet = new Sheet { Budget = (int?)spec["budgetTokens"] ?? 3500 };
            var sb = new StringBuilder();
            sb.Append(((string)spec["header"] ?? "").Trim()).Append('\n');

            foreach (var group in (JArray)spec["groups"])
            {
                sb.Append('\n').Append("## ").Append((string)group["title"]).Append('\n');
                foreach (var entry in (JArray)group["members"])
                {
                    var name = entry.Type == JTokenType.String ? (string)entry : (string)entry["member"];
                    var hint = entry.Type == JTokenType.Object ? (string)entry["hint"] : null;
                    var use = entry.Type == JTokenType.Object ? (string)entry["use"] : null;
                    string line;
                    if (!name.Contains("."))
                    {
                        var only = entry.Type == JTokenType.Object ? entry["values"]?.ToObject<List<string>>() : null;
                        line = api.Enum(name, only, out var missing);
                        if (line == null) sheet.Problems.Add($"members.json: enum value {missing} is not in the interop.");
                    }
                    else
                    {
                        var dot = name.IndexOf('.');
                        line = api.Signature(name.Substring(0, dot), name.Substring(dot + 1));
                        if (line == null)
                        {
                            var owners = api.Find(name.Substring(dot + 1)).Select(s => s.Substring(0, s.IndexOf('.'))).ToList();
                            sheet.Problems.Add($"members.json: {name} is not in the interop" +
                                               (owners.Count > 0 ? $" (it is on {string.Join(", ", owners)})." : "."));
                        }
                    }
                    if (line == null) continue;
                    if (!string.IsNullOrWhiteSpace(use))
                    {
                        // A built-in helper does this call: name the helper; the raw signature is a get_sw_api_type away.
                        var dot = name.IndexOf('.');
                        line = api.Brief(name.Substring(0, dot), name.Substring(dot + 1)) + $" - use {use.Trim()}; get_sw_api_type for the raw signature";
                    }
                    sheet.MemberCount++;
                    sb.Append(line);
                    if (!string.IsNullOrWhiteSpace(hint)) sb.Append(" - ").Append(hint.Trim());
                    sb.Append('\n');
                }
            }

            sb.Append('\n').Append(Notes(File.ReadAllText(notesPath)));
            sheet.Text = sb.ToString().TrimEnd('\n');
            if (sheet.Tokens > sheet.Budget)
            {
                sheet.Problems.Add($"The sheet is ~{sheet.Tokens} tokens (chars/4), over its budget of {sheet.Budget}: trim members.json or notes.md.");
            }
            sheet.TypeScript =
                "// GENERATED by tools/dev/ApiCheatSheet from members.json and notes.md, with signatures reflected from the SolidWorks\n" +
                "// interop the add-in loads. Do not edit by hand: change those files and run the tool (README.md there); its `check`\n" +
                "// command and ui/src/lib/checks/cheatsheet.check.mjs keep this file honest.\n" +
                "\n" +
                "/** The SolidWorks API cheat sheet: exact signatures of the members the agent uses most, grouped by task, and the gotchas. */\n" +
                "export const SW_API_CHEATSHEET = `" + Escape(sheet.Text) + "`;\n";
            return sheet;
        }

        /// <summary>notes.md without its H1 title and HTML comments (the comments are for maintainers).</summary>
        private static string Notes(string md)
        {
            md = Regex.Replace(md.Replace("\r\n", "\n"), "<!--.*?-->\n?", "", RegexOptions.Singleline);
            var lines = md.Split('\n').Where(l => !l.StartsWith("# ")).ToList();
            return string.Join("\n", lines).Trim() + "\n";
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("`", "\\`").Replace("${", "\\${");
    }
}

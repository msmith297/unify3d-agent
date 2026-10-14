using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.Tools
{
    /// <summary>
    /// Searchable reference of the SolidWorks COM API, built by reflection over the interop assemblies that
    /// are actually loaded - so signatures always match the installed SolidWorks version.
    /// </summary>
    public sealed class ApiTools
    {
        private sealed class ApiEntry
        {
            public string Type;
            public string Member;
            public string Signature;
            public string Haystack;
        }

        private static List<ApiEntry> _index;
        private static Dictionary<string, Type> _types;

        public sealed class SearchInput
        {
            [ToolParam("Words to search for in interface, member and enum names, e.g. 'extrude feature', 'fillet', " +
                       "'custom property', 'swEndConditions'. All words must match.", Required = true)]
            public string Query { get; set; }

            [ToolParam("Maximum results (default 40).")]
            public int? Limit { get; set; }
        }

        public sealed class TypeInput
        {
            [ToolParam("Interface or enum name, e.g. 'IFeatureManager', 'IModelDocExtension', 'swEndConditions_e'.", Required = true)]
            public string TypeName { get; set; }

            [ToolParam("Optional substring filter on member names, e.g. 'Extrusion'.")]
            public string MemberFilter { get; set; }
        }

        [AgentTool("search_sw_api",
            "Search the installed SolidWorks API (SolidWorks.Interop.sldworks interfaces and swconst enums) and get " +
            "exact C# signatures. Use it before calling an API you are not certain about. When several numbered " +
            "variants exist (FeatureExtrusion2/3), prefer the highest number - older ones are obsolete.")]
        public ToolResult Search(ToolContext ctx, SearchInput input)
        {
            EnsureIndex();
            var words = input.Query.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.ToLowerInvariant()).ToArray();
            var limit = Math.Max(1, Math.Min(200, input.Limit ?? 40));

            var hits = _index
                .Where(e => words.All(w => e.Haystack.Contains(w)))
                .Select(e => (Entry: e, Score: Score(e, words)))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Entry.Type.Length)
                .ThenBy(x => x.Entry.Signature)
                .Take(limit)
                .Select(x => x.Entry.Signature)
                .ToList();

            return ToolResult.Ok(hits.Count == 0
                ? $"No API members match '{input.Query}'. Try fewer or different words."
                : string.Join("\n", hits));
        }

        [AgentTool("get_sw_api_type",
            "List every member of one SolidWorks API interface (methods and properties with C# signatures) or every value " +
            "of a swconst enum.")]
        public ToolResult GetApiType(ToolContext ctx, TypeInput input)
        {
            EnsureIndex();
            var name = input.TypeName.Trim();
            if (!_types.TryGetValue(name, out var type) && !_types.TryGetValue("I" + name, out type))
            {
                var near = _types.Keys.Where(k => k.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).Take(15);
                return ToolResult.Error($"No interface or enum named '{name}'. Similar: {string.Join(", ", near)}");
            }

            var lines = _index.Where(e => e.Type == type.Name)
                .Where(e => string.IsNullOrEmpty(input.MemberFilter) ||
                            e.Member.IndexOf(input.MemberFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(e => e.Signature)
                .OrderBy(s => s)
                .ToList();
            var header = type.IsEnum ? $"enum {type.FullName}" : $"interface {type.FullName}";
            return ToolResult.Ok(header + "\n" + (lines.Count == 0 ? "(no matching members)" : string.Join("\n", lines)));
        }

        private static int Score(ApiEntry e, string[] words)
        {
            var score = 0;
            var member = e.Member.ToLowerInvariant();
            var type = e.Type.ToLowerInvariant();
            foreach (var w in words)
            {
                if (member == w) score += 20;
                else if (member.StartsWith(w)) score += 8;
                else if (member.Contains(w)) score += 4;
                if (type == w || type == "i" + w) score += 6;
                else if (type.Contains(w)) score += 2;
            }
            return score;
        }

        private static void EnsureIndex()
        {
            if (_index != null) return;
            var index = new List<ApiEntry>();
            var types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in typeof(ISldWorks).Assembly.GetExportedTypes().Where(t => t.IsInterface && t.Name.StartsWith("I")))
            {
                types[t.Name] = t;
                foreach (var m in t.GetMethods().Where(m => !m.IsSpecialName))
                {
                    Add(index, t.Name, m.Name, $"{t.Name}.{m.Name}({Params(m)}) : {TypeName(m.ReturnType)}");
                }
                foreach (var p in t.GetProperties())
                {
                    var access = (p.CanRead ? "get;" : "") + (p.CanWrite ? " set;" : "");
                    Add(index, t.Name, p.Name, $"{t.Name}.{p.Name} {{ {access.Trim()} }} : {TypeName(p.PropertyType)}");
                }
            }

            foreach (var t in typeof(swDocumentTypes_e).Assembly.GetExportedTypes().Where(t => t.IsEnum))
            {
                types[t.Name] = t;
                foreach (var n in Enum.GetNames(t))
                {
                    var value = Convert.ToInt64(Enum.Parse(t, n));
                    Add(index, t.Name, n, $"{t.Name}.{n} = {value}");
                }
            }

            _types = types;
            _index = index;
        }

        private static void Add(List<ApiEntry> index, string type, string member, string signature)
        {
            index.Add(new ApiEntry
            {
                Type = type,
                Member = member,
                Signature = signature,
                Haystack = (type + "." + member + " " + signature).ToLowerInvariant(),
            });
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
}

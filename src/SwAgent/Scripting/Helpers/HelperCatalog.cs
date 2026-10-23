using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using SwAgent.Scripting.Extensions;

namespace SwAgent.Scripting.Helpers
{
    /// <summary>
    /// The built-in helpers a code cell can call, one line each, read from the [Helper] attributes on Bodies.*,
    /// Features.*, Sel.* and the extension methods - so the prompt index and the compile advisor always match the code.
    /// Wraps lists the raw API members each one replaces: a compile error on one of those points the agent to the helper.
    /// </summary>
    public static class HelperCatalog
    {
        private static readonly Lazy<IReadOnlyList<HelperInfo>> AllLazy = new Lazy<IReadOnlyList<HelperInfo>>(Build);

        /// <summary>Every built-in helper (Source "built-in").</summary>
        public static IReadOnlyList<HelperInfo> All => AllLazy.Value;

        /// <summary>The catalog as an IHelperIndex (for code that merges helper sources).</summary>
        public static IHelperIndex Index { get; } = new BuiltIn();

        /// <summary>A built-in interop extension (Model.FeatureByName, body.Volume, x.AsDoubles...), not a Bodies./Features./Sel. helper.</summary>
        public static bool IsExtension(HelperInfo h) =>
            h?.Source == "built-in" && h.Name != null && !h.Name.StartsWith("Bodies.", StringComparison.Ordinal) &&
            !h.Name.StartsWith("Features.", StringComparison.Ordinal) && !h.Name.StartsWith("Sel.", StringComparison.Ordinal);

        /// <summary>A helper by name ("Bodies.Rotate"), or null.</summary>
        public static HelperInfo Find(string name) => All.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.Ordinal));

        /// <summary>
        /// Helpers whose Wraps names this API member: "IFeatureManager.FeatureCut4" or just "FeatureCut4" (any type).
        /// `helpers` defaults to the built-in catalog.
        /// </summary>
        public static IEnumerable<HelperInfo> Wrapping(string member, IEnumerable<HelperInfo> helpers = null)
        {
            if (string.IsNullOrEmpty(member)) return Enumerable.Empty<HelperInfo>();
            var name = member.Contains(".") ? member.Substring(member.LastIndexOf('.') + 1) : member;
            return (helpers ?? All).Where(h => h?.Wraps != null && h.Wraps.Any(w =>
                string.Equals(w, member, StringComparison.Ordinal) ||
                (!member.Contains(".") && (string.Equals(w, name, StringComparison.Ordinal) || w.EndsWith("." + name, StringComparison.Ordinal)))));
        }

        private sealed class BuiltIn : IHelperIndex
        {
            public IEnumerable<HelperInfo> All() => HelperCatalog.All;
        }

        private static IReadOnlyList<HelperInfo> Build()
        {
            var result = new List<HelperInfo>();
            foreach (var (prefix, type) in new[] { ("Bodies", typeof(BodiesApi)), ("Features", typeof(FeaturesApi)), ("Sel", typeof(SelApi)) })
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var a = m.GetCustomAttribute<HelperAttribute>();
                    if (a == null) continue;
                    result.Add(new HelperInfo
                    {
                        Name = prefix + "." + m.Name,
                        Signature = Signature(prefix + "." + m.Name, m.GetParameters(), m.ReturnType),
                        Summary = a.Summary,
                        Wraps = a.Wraps,
                        Source = "built-in",
                    });
                }
            }
            foreach (var type in new[] { typeof(DocExtensions), typeof(EntityExtensions), typeof(VariantExtensions) })
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    var a = m.GetCustomAttribute<HelperAttribute>();
                    if (a == null) continue;
                    var ps = m.GetParameters();
                    var receiver = Receiver(ps[0].ParameterType);
                    var generic = m.IsGenericMethodDefinition ? "<T>" : "";
                    result.Add(new HelperInfo
                    {
                        Name = receiver + "." + m.Name,
                        Signature = Signature(receiver + "." + m.Name + generic, ps.Skip(1).ToArray(), m.ReturnType),
                        Summary = a.Summary,
                        Wraps = a.Wraps,
                        Source = "built-in",
                    });
                }
            }
            return result;
        }

        /// <summary>How a cell writes the receiver of an extension method.</summary>
        private static string Receiver(Type t)
        {
            switch (t.Name)
            {
                case "IModelDoc2": return "Model";
                case "ISelectionMgr": return "selMgr";
                case "IBody2": return "body";
                case "IFeature": return "feature";
                case "Object": return "x";
            }
            var n = t.Name.StartsWith("I", StringComparison.Ordinal) && t.Name.Length > 1 && char.IsUpper(t.Name[1]) ? t.Name.Substring(1) : t.Name;
            return char.ToLowerInvariant(n[0]) + n.Substring(1);
        }

        /// <summary>"Bodies.Rotate(IBody2 body, string axis, double degrees, double[] about = null) -> IBody2".</summary>
        internal static string Signature(string name, ParameterInfo[] ps, Type returns)
        {
            var args = string.Join(", ", ps.Select(p =>
                (p.GetCustomAttribute<ParamArrayAttribute>() != null ? "params " : "") + TypeName(p.ParameterType) + " " + p.Name +
                (p.HasDefaultValue ? " = " + Literal(p.DefaultValue) : "")));
            return $"{name}({args})" + (returns == typeof(void) ? "" : " -> " + TypeName(returns));
        }

        internal static string TypeName(Type t)
        {
            if (t.IsByRef) return "out " + TypeName(t.GetElementType());
            if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
            if (t.IsGenericParameter) return t.Name;
            var u = Nullable.GetUnderlyingType(t);
            if (u != null) return TypeName(u) + "?";
            if (t.IsGenericType)
            {
                var n = t.Name.Substring(0, t.Name.IndexOf('`'));
                return n + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">";
            }
            if (t.IsEnum) return t.Name; // its TypeCode is the underlying int's: Model.Bodies(int type) would be a lie (CS1503)
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: return "bool";
                case TypeCode.Int32: return "int";
                case TypeCode.Double: return "double";
                case TypeCode.String: return "string";
            }
            return t == typeof(object) ? "object" : t.Name;
        }

        private static string Literal(object v)
        {
            switch (v)
            {
                case null: return "null";
                case bool b: return b ? "true" : "false";
                case string s: return "\"" + s + "\"";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case Enum e: return e.GetType().Name + "." + e;
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
            }
            return v.ToString();
        }
    }
}

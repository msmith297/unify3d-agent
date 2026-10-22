using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.Scripting.Extensions;
using SwAgent.Scripting.Helpers;

namespace SwAgent.Scripting
{
    /// <summary>
    /// Short fix hints under a code cell's compile errors, so a wrong guess costs one retry instead of an API search:
    /// "FeatureByName is on IPartDoc: ((IPartDoc)Model).FeatureByName(...)", "SelectData and ISelectData are the same COM
    /// object: cast it", "IMathPoint has MultiplyTransform", "Bodies.Rotate(...) does this in one checked call". It reads
    /// the interop assemblies by reflection and the helpers' Wraps lists (built in, plugins, the agent's own). At most
    /// three hints; it never throws.
    /// </summary>
    public static class CompileAdvisor
    {
        public const int MaxHints = 3;

        /// <summary>More helpers to point at (plugins, the agent's own): set by whoever keeps them. They come before the built-in ones.</summary>
        public static IHelperIndex Extra { get; set; }

        /// <summary>
        /// "\n--- hints ---\n- ..." for the errors, or "" when there is nothing useful to say. `helpers`: every helper a cell can
        /// call now, in rank order (ScriptHost passes HelperIndex.Merge of the agent's own, the plugins' and the built-in ones);
        /// null means Extra's, then the built-in catalog.
        /// </summary>
        public static string Advise(IEnumerable<Diagnostic> errors, Compilation compilation, IEnumerable<HelperInfo> helpers = null)
        {
            try
            {
                var hints = Hints(errors, compilation, helpers);
                return hints.Count == 0 ? "" : "\n--- hints ---\n" + string.Join("\n", hints.Select(h => "- " + h));
            }
            catch (Exception ex)
            {
                Log.Warn("CompileAdvisor: " + ex.Message);
                return "";
            }
        }

        /// <summary>Up to three hints for these compile errors. `helpers` defaults to Extra's then the built-in catalog.</summary>
        public static IReadOnlyList<string> Hints(IEnumerable<Diagnostic> errors, Compilation compilation, IEnumerable<HelperInfo> helpers = null)
        {
            List<HelperInfo> all;
            if (helpers != null) all = helpers.ToList();
            else
            {
                IEnumerable<HelperInfo> extra;
                try
                {
                    extra = Extra?.All()?.ToList() ?? new List<HelperInfo>();
                }
                catch (Exception ex)
                {
                    Log.Warn("CompileAdvisor: reading the extra helpers: " + ex.Message);
                    extra = new List<HelperInfo>();
                }
                all = HelperIndex.Merge(extra);
            }
            var result = new List<string>();
            foreach (var d in (errors ?? Enumerable.Empty<Diagnostic>()).Where(e => e.Severity == DiagnosticSeverity.Error).Take(12))
            {
                IEnumerable<string> found;
                try
                {
                    found = For(d, compilation, all).ToList();
                }
                catch (Exception ex)
                {
                    Log.Warn($"CompileAdvisor: {d.Id}: {ex.Message}");
                    continue;
                }
                foreach (var h in found)
                {
                    if (string.IsNullOrWhiteSpace(h) || result.Contains(h)) continue;
                    result.Add(h);
                    if (result.Count >= MaxHints) return result;
                }
            }
            return result;
        }

        // ---------- per error ----------

        private static IEnumerable<string> For(Diagnostic d, Compilation compilation, List<HelperInfo> helpers)
        {
            var msg = d.GetMessage(CultureInfo.InvariantCulture);
            SyntaxNode node = null;
            SemanticModel model = null;
            if (compilation != null && d.Location.IsInSource && d.Location.SourceTree != null)
            {
                try
                {
                    node = d.Location.SourceTree.GetRoot().FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
                    if (compilation.SyntaxTrees.Contains(d.Location.SourceTree)) model = compilation.GetSemanticModel(d.Location.SourceTree);
                }
                catch
                {
                    node = null;
                }
            }
            Match m;
            switch (d.Id)
            {
                case "CS0837": // The first operand of an 'is' or 'as' operator may not be a ... method group (x.M as T, M only an extension elsewhere)
                case "CS0428": // Cannot convert method group 'M' to non-delegate type
                case "CS8917": // The delegate type could not be inferred (object x = y.M where M is only a method group)
                {
                    var ma = node?.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault() ??
                             node?.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
                    var receiverType = ma == null || model == null ? null : model.GetTypeInfo(ma.Expression).Type;
                    if (receiverType == null) yield break;
                    var name = ma.Name.Identifier.Text;
                    var tn = receiverType.SpecialType == SpecialType.System_Object ? "object" : receiverType.Name;
                    if (IsMethodOf(tn, name))
                    {
                        yield return $"{name} is a method: call it, {ma.Expression}.{name}(...).";
                        yield break;
                    }
                    foreach (var h in HelperHints(name, helpers)) yield return h;
                    foreach (var h in MemberHints(tn, name, ma.Expression.ToString().Trim(), false)) yield return h;
                    yield break;
                }

                case "CS1061": // 'T' does not contain a definition for 'M'
                case "CS1929": // 'T' does not contain a definition for 'M' and the best extension method overload requires a receiver of type ...
                case "CS0117":
                    m = Regex.Match(msg, "'(.+?)' does not contain a definition for '(.+?)'");
                    if (!m.Success) yield break;
                    foreach (var h in HelperHints(m.Groups[2].Value, helpers)) yield return h;
                    foreach (var h in MemberHints(Short(m.Groups[1].Value), m.Groups[2].Value, Receiver(node), IsCall(node))) yield return h;
                    yield break;

                case "CS1501": // No overload for method 'M' takes N arguments
                case "CS7036": // There is no argument given that corresponds to the required parameter 'p' of 'T.M(...)'
                case "CS1739": // The best overload for 'M' does not have a parameter named 'p'
                {
                    var inv = Invocation(node);
                    var name = Regex.Match(msg, d.Id == "CS7036" ? @"of '(?:.*\.)?(\w+)\(" : "'(\\w+)'").Groups[1].Value;
                    foreach (var h in HelperHints(InvokedName(inv) ?? name, helpers)) yield return h;
                    var sig = SignatureHint(inv, model, name);
                    if (sig != null) yield return sig;
                    yield break;
                }

                case "CS1503": // Argument n: cannot convert from 'A' to 'B'
                    m = Regex.Match(msg, "cannot convert from '(.+?)' to '(.+?)'");
                    if (m.Success)
                    {
                        var c = ConversionHint(m.Groups[1].Value, m.Groups[2].Value, ArgumentText(node));
                        if (c != null) yield return c;
                    }
                    foreach (var h in HelperHints(InvokedName(Invocation(node)), helpers)) yield return h;
                    yield break;

                case "CS0266": // Cannot implicitly convert type 'A' to 'B'. An explicit conversion exists
                case "CS0029":
                case "CS0030": // Cannot convert type 'A' to 'B'
                    m = Regex.Match(msg, "onvert type '(.+?)' to '(.+?)'");
                    if (!m.Success) yield break;
                    var conv = ConversionHint(m.Groups[1].Value, m.Groups[2].Value, null);
                    if (conv != null) yield return conv;
                    var data = DataObjectHint(m.Groups[1].Value, m.Groups[2].Value, node, model);
                    if (data != null) yield return data;
                    foreach (var h in HelperHints(InvokedName(Invocation(node)), helpers)) yield return h;
                    yield break;

                case "CS0021": // Cannot apply indexing with [] to an expression of type 'object'
                    if (msg.Contains("'object'"))
                        yield return "SolidWorks arrays come back typed object: index x.AsDoubles()[i] for numbers or x.AsObjects<IBody2>()[i] for objects.";
                    foreach (var h in HelperHints(InvokedName(Invocation(node)), helpers)) yield return h;
                    yield break;

                case "CS0104": // 'X' is an ambiguous reference between 'A' and 'B'
                    m = Regex.Match(msg, "'(.+?)' is an ambiguous reference between '(.+?)' and '(.+?)'");
                    if (m.Success)
                    {
                        var pick = new[] { m.Groups[2].Value, m.Groups[3].Value }.FirstOrDefault(x => !x.StartsWith("SolidWorks.", StringComparison.Ordinal)) ?? m.Groups[2].Value;
                        yield return $"Write the full name: {pick} (SolidWorks.Interop.sldworks has a type of the same name).";
                    }
                    yield break;

                case "CS0201":
                    yield return "A bare value isn't a statement: Print(x) it, assign it, or end the cell with it (no semicolon) to return it; `if (...) \"x\"; else ...` needs Print(\"x\") or a variable.";
                    yield break;

                case "CS8361":
                    yield return "Inside $\"...\" put a ?: in parentheses: {(a ? b : c)} - the ':' would start a format string.";
                    yield break;

                case "CS0103": // The name 'x' does not exist in the current context
                    m = Regex.Match(msg, "The name '(.+?)' does not exist");
                    if (m.Success && node?.Parent is AssignmentExpressionSyntax a && a.Left == node)
                        yield return $"'{m.Groups[1].Value}' was never declared (variables from earlier cells only exist if that cell declared them): var {m.Groups[1].Value} = ...;";
                    yield break;
            }
        }

        // ---------- helpers ----------

        private static IEnumerable<string> HelperHints(string member, List<HelperInfo> helpers)
        {
            if (string.IsNullOrEmpty(member)) yield break;
            foreach (var h in HelperCatalog.Wrapping(member, helpers).Take(1))
                yield return $"{h.Name} does this in one checked call: {h.Signature}" + (h.Source != null && h.Source != "built-in" ? $" ({h.Source})" : "");
        }

        // ---------- members on the wrong interface ----------

        private sealed class InteropIndex
        {
            public readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<(string Owner, bool Method)>> Owners = new Dictionary<string, List<(string, bool)>>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<string>> Extensions = new Dictionary<string, List<string>>(StringComparer.Ordinal); // name -> "this" type names
        }

        private static readonly Lazy<InteropIndex> Index = new Lazy<InteropIndex>(BuildIndex);

        private static InteropIndex BuildIndex()
        {
            var ix = new InteropIndex();
            foreach (var asm in new[] { typeof(ISldWorks).Assembly, typeof(swDocumentTypes_e).Assembly })
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                foreach (var t in types.Where(t => t.IsPublic))
                {
                    ix.Types[t.Name] = t;
                    if (!t.IsInterface || !IsIName(t.Name)) continue;
                    foreach (var name in Members(t))
                    {
                        if (!ix.Owners.TryGetValue(name.Name, out var list)) ix.Owners[name.Name] = list = new List<(string, bool)>();
                        list.Add((t.Name, name.Method));
                    }
                }
            }
            foreach (var t in new[] { typeof(DocExtensions), typeof(EntityExtensions), typeof(VariantExtensions) })
            foreach (var mi in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var first = mi.GetParameters().FirstOrDefault()?.ParameterType;
                if (first == null) continue;
                if (!ix.Extensions.TryGetValue(mi.Name, out var list)) ix.Extensions[mi.Name] = list = new List<string>();
                if (!list.Contains(first.Name)) list.Add(first.Name);
            }
            return ix;
        }

        private static bool IsIName(string n) => n.Length > 1 && n[0] == 'I' && char.IsUpper(n[1]);

        /// <summary>Methods and properties as a cell would write them (no get_/set_, no I-prefixed array variants).</summary>
        private static IEnumerable<(string Name, bool Method)> Members(Type t)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in t.GetProperties())
                if (seen.Add(p.Name)) yield return (p.Name, false);
            foreach (var mi in t.GetMethods())
            {
                if (mi.IsSpecialName) continue;
                if (seen.Add(mi.Name)) yield return (mi.Name, true);
            }
        }

        private static readonly string[] Hubs =
        {
            "ISelectionMgr", "IModelDoc2", "IPartDoc", "IAssemblyDoc", "IDrawingDoc", "IModelDocExtension", "IFeatureManager", "ISketchManager",
            "ISldWorks", "IBody2", "IFace2", "IEdge", "IVertex", "IEntity", "IFeature", "ISketch", "ISketchSegment", "ISketchPoint", "IComponent2",
            "IMathUtility", "IMathTransform", "IMathPoint", "IMathVector", "ICurve", "ISurface", "ILoop2", "ICoEdge", "IRefPlane", "IView",
        };

        private static IEnumerable<string> MemberHints(string typeName, string member, string receiver, bool call)
        {
            var ix = Index.Value;
            ix.Types.TryGetValue(typeName, out var type);
            List<string> similar = null, candidates = null;
            if (type != null)
            {
                candidates = Members(type).Select(x => x.Name).Where(n => !(IsIName(n) && Members(type).Any(y => y.Name == n.Substring(1))))
                    .Concat(ix.Extensions.Where(e => e.Value.Contains(type.Name)).Select(e => e.Key)).Distinct().ToList();
                similar = Similar(member, candidates);
            }
            // 1. The interfaces that do have it (the agent's guess is right, the interface is wrong). On a typed receiver
            // with near-miss names, only the main interfaces (a texture's TransX says nothing about a Move/Copy feature).
            if (ix.Owners.TryGetValue(member, out var owners))
            {
                var ranked = Newest(owners.Select(o => o.Owner).Where(o => o != typeName).Distinct())
                    .OrderBy(o => Array.IndexOf(Hubs, o) is int k && k >= 0 ? k : 100).ThenBy(o => o.Length).ThenBy(o => o, StringComparer.Ordinal).ToList();
                if (typeName != "object" && similar != null && similar.Count > 0) ranked = ranked.Where(o => Array.IndexOf(Hubs, o) >= 0).ToList();
                var names = ranked.Take(3).ToList();
                if (names.Count > 0)
                {
                    var r = receiver != null && receiver.Length <= 40 ? receiver : "x";
                    var isMethod = owners.Any(o => o.Owner == names[0] && o.Method);
                    yield return $"{member} is on {string.Join(" / ", names)}, not {typeName}: (({names[0]}){r}).{member}{(isMethod ? "(...)" : "")}" +
                                 (typeName == "object" ? " - the API returned it typed object, cast it first." : ".");
                }
            }
            // 2. An extension with that name for another type.
            if (ix.Extensions.TryGetValue(member, out var thisTypes) && !thisTypes.Contains(typeName))
                yield return $"{member}() is a built-in extension on {string.Join(" / ", thisTypes.Select(t => t == "Object" ? "any object" : t))}.";
            // 3. Similar names on the type itself (a near miss: TransX -> TransformX, MultiTransform -> MultiplyTransform).
            if (type != null)
            {
                if (similar.Count > 0) yield return $"{typeName} has no {member}; it has {string.Join(", ", similar)}.";
                else if (owners == null && candidates.Count > 0 && candidates.Count <= 40)
                    yield return $"{typeName} has: {string.Join(", ", candidates.Where(n => n != "AccessSelections" && n != "ReleaseSelectionAccess").Take(16))}.";
            }
        }

        /// <summary>Whether the interop type (or a built-in extension for it) has a method of that name.</summary>
        private static bool IsMethodOf(string typeName, string name)
        {
            var ix = Index.Value;
            if (ix.Types.TryGetValue(typeName, out var t) && t.GetMethods().Any(x => x.Name == name && !x.IsSpecialName)) return true;
            return ix.Extensions.TryGetValue(name, out var thisTypes) && (thisTypes.Contains(typeName) || thisTypes.Contains("Object"));
        }

        /// <summary>Of IFoo, IFoo2, IFoo3 keep IFoo3 (the current version).</summary>
        private static IEnumerable<string> Newest(IEnumerable<string> names)
        {
            var list = names.ToList();
            return list.GroupBy(n => n.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'))
                .Select(g => g.OrderByDescending(n => int.TryParse(n.Substring(g.Key.Length), out var v) ? v : 0).First());
        }

        /// <summary>Up to three names close to `member`: same but for case, prefix or containment, the letters in order, a shared start, or 1-2 edits.</summary>
        internal static List<string> Similar(string member, IEnumerable<string> names)
        {
            var baseName = member.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            int? Score(string n)
            {
                if (n == member) return null;
                if (string.Equals(n, member, StringComparison.OrdinalIgnoreCase)) return 0;
                if (n.StartsWith(member, StringComparison.OrdinalIgnoreCase) || (n.Length >= 4 && member.StartsWith(n, StringComparison.OrdinalIgnoreCase))) return 1;
                if ((member.Length >= 3 && n.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (baseName.Length >= 4 && n.IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0)) return 2;
                if (member.Length >= 4 && Subsequence(member, n)) return 3;
                var common = 0;
                while (common < Math.Min(n.Length, member.Length) && char.ToLowerInvariant(n[common]) == char.ToLowerInvariant(member[common])) common++;
                if (common >= Math.Max(4, (int)Math.Ceiling(0.6 * member.Length))) return 4;
                if (Levenshtein(member.ToLowerInvariant(), n.ToLowerInvariant()) <= Math.Min(2, member.Length / 3)) return 5;
                return null;
            }
            return names.Select(n => (Name: n, Score: Score(n))).Where(x => x.Score.HasValue)
                .OrderBy(x => x.Score).ThenBy(x => Math.Abs(x.Name.Length - member.Length)).ThenBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => x.Name).Distinct().Take(3).ToList();
        }

        private static bool Subsequence(string small, string big)
        {
            var i = 0;
            foreach (var c in big)
                if (i < small.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(small[i])) i++;
            return i == small.Length;
        }

        private static int Levenshtein(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        // ---------- conversions ----------

        private static string ConversionHint(string fromFull, string toFull, string argText)
        {
            var from = Short(fromFull);
            var to = Short(toFull);
            var ix = Index.Value;
            if (from == "object" || from == "object[]")
            {
                var elem = to.EndsWith("[]", StringComparison.Ordinal) ? to.Substring(0, to.Length - 2) : null;
                if (to == "double[]") return "SolidWorks returns number arrays typed object: x.AsDoubles() turns object, object[] or double[] into double[] (null gives []).";
                if (to == "int[]") return "SolidWorks returns arrays typed object: x.AsInts() gives int[].";
                if (to == "string[]") return "SolidWorks returns arrays typed object: x.AsStrings() gives string[].";
                if (elem != null && ix.Types.ContainsKey(elem)) return $"SolidWorks returns object arrays typed object: x.AsObjects<{elem}>() gives {elem}[].";
                if (from == "object" && ix.Types.ContainsKey(to)) return $"The API returned it typed object: cast it, ({to})x, or `x as {to}` and check for null.";
                return null;
            }
            if (ix.Types.TryGetValue(from, out var tf) && ix.Types.TryGetValue(to, out var tt) && tf.IsInterface && tt.IsInterface &&
                (tt.IsAssignableFrom(tf) || tf.IsAssignableFrom(tt) || from == "I" + to || to == "I" + from))
            {
                var arg = argText != null && argText.Length <= 30 ? argText : "x";
                return $"{to} and {from} are the same COM object: cast it, ({to}){arg}" +
                       (to == "SelectData" ? " - or make it with Sel.Data(mark) / Model.CreateSelectData(mark), which return SelectData." : ".");
            }
            return null;
        }

        /// <summary>(double[])x where x is a data object (CurveParamData...): its numbers-array sibling, or its properties.</summary>
        private static string DataObjectHint(string fromFull, string toFull, SyntaxNode node, SemanticModel model)
        {
            var from = Short(fromFull);
            var to = Short(toFull);
            var ix = Index.Value;
            if (!to.EndsWith("[]", StringComparison.Ordinal) || !ix.Types.TryGetValue(from, out var tf)) return null;
            var props = new[] { tf }.Concat(tf.GetInterfaces()).SelectMany(i => i.GetProperties()).Select(p => p.Name).Distinct().Take(6).ToList();
            var text = $"{from} is an object with properties ({string.Join(", ", props)}), not an array";
            // The call that returned it, and an older sibling that returns the plain array (GetCurveParams3 -> GetCurveParams2).
            var inv = node?.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault() ?? Invocation(node);
            if (inv != null && model != null && model.GetSymbolInfo(inv).Symbol is IMethodSymbol ms && ix.Types.TryGetValue(ms.ContainingType.Name, out var owner))
            {
                var stem = ms.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                var sibling = owner.GetMethods().Where(x => x.Name != ms.Name && x.Name.StartsWith(stem, StringComparison.Ordinal) && x.ReturnType == typeof(object) &&
                                                            x.Name.Substring(stem.Length).All(char.IsDigit))
                    .OrderByDescending(x => x.Name).FirstOrDefault();
                var recv = (inv.Expression as MemberAccessExpressionSyntax)?.Expression.ToString().Trim();
                if (sibling != null)
                    return text + $"; {ms.ContainingType.Name}.{sibling.Name}() returns the numbers: {(recv != null && recv.Length <= 30 ? recv : "x")}.{sibling.Name}({(sibling.GetParameters().Length > 0 ? "..." : "")}).AsDoubles().";
            }
            return text + (from.Contains("CurveParamData") ? "; .AsDoubles() gives [start xyz, end xyz, uMin, uMax]." : ".");
        }

        // ---------- signatures ----------

        private static string SignatureHint(InvocationExpressionSyntax inv, SemanticModel model, string name)
        {
            IMethodSymbol method = null;
            if (inv != null && model != null)
            {
                var info = model.GetSymbolInfo(inv);
                method = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().OrderByDescending(x => x.Parameters.Length).FirstOrDefault();
            }
            if (method == null || !Index.Value.Types.ContainsKey(method.ContainingType.Name)) return null;
            var ps = method.Parameters;
            var args = string.Join(", ", ps.Select(p => p.Name));
            return $"{method.ContainingType.Name}.{method.Name} takes {ps.Length} arguments ({args}); with that many, name them ({string.Join(", ", ps.Take(2).Select(p => p.Name + ": ..."))}) so none is out of place.";
        }

        // ---------- syntax ----------

        private static InvocationExpressionSyntax Invocation(SyntaxNode node)
        {
            for (var n = node; n != null && !(n is StatementSyntax) && !(n is MemberDeclarationSyntax); n = n.Parent)
                if (n is InvocationExpressionSyntax inv) return inv;
            return null;
        }

        private static string InvokedName(InvocationExpressionSyntax inv)
        {
            switch (inv?.Expression)
            {
                case MemberAccessExpressionSyntax ma:
                    return ma.Name.Identifier.Text;
                case IdentifierNameSyntax id:
                    return id.Identifier.Text;
                case GenericNameSyntax g:
                    return g.Identifier.Text;
            }
            return null;
        }

        /// <summary>The expression before ".Member" at the error, as written.</summary>
        private static string Receiver(SyntaxNode node)
        {
            var ma = node as MemberAccessExpressionSyntax ?? node?.Parent as MemberAccessExpressionSyntax;
            return ma?.Expression.ToString().Trim();
        }

        private static bool IsCall(SyntaxNode node)
        {
            var ma = node as MemberAccessExpressionSyntax ?? node?.Parent as MemberAccessExpressionSyntax;
            return ma?.Parent is InvocationExpressionSyntax;
        }

        private static string ArgumentText(SyntaxNode node)
        {
            for (var n = node; n != null && !(n is StatementSyntax); n = n.Parent)
                if (n is ArgumentSyntax a) return a.Expression.ToString().Trim();
            return null;
        }

        /// <summary>"SolidWorks.Interop.sldworks.ISelectData" -> "ISelectData"; keeps array and generic suffixes.</summary>
        private static string Short(string type)
        {
            var t = type.Trim();
            var lt = t.IndexOf('<');
            var head = lt >= 0 ? t.Substring(0, lt) : t;
            var tail = lt >= 0 ? t.Substring(lt) : "";
            var dot = head.LastIndexOf('.');
            return (dot >= 0 ? head.Substring(dot + 1) : head) + tail;
        }
    }
}

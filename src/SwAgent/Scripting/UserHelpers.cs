using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Plugins;

namespace SwAgent.Scripting
{
    /// <summary>
    /// The helpers the agent wrote itself (save_helper) and the ones enabled plugins ship (helpers/*.cs), compiled into one
    /// assembly that every execute_csharp cell references and imports (namespace SwAgent.UserHelpers). A helper is a public
    /// static class named like the helper; it may hold extension methods, which is how a helper makes a wrong-interface call
    /// (Model.FeatureByName on IModelDoc2) compile and work.
    ///
    /// Each helper is first compiled alone, so one bad helper never breaks the others: it is skipped and its record says why
    /// (lastError). The good ones are compiled together into %LOCALAPPDATA%\SwAgent\helpers-bin\helpers-&lt;hash&gt;.dll; the hash
    /// covers the add-in build and every helper's code, so an unchanged set is never compiled twice. Version goes up whenever
    /// the assembly changes: ScriptHost rebuilds its options then. Compiling only: never touches SolidWorks, so it runs off
    /// the UI thread.
    /// </summary>
    internal sealed class UserHelpers
    {
        public const string Namespace = "SwAgent.UserHelpers";
        public const string AssemblyPrefix = "SwAgent.UserHelpers.";

        private static UserHelpers _instance;

        /// <summary>The add-in's helpers (the knowledge store under Paths, the add-in's plugins). Tests pass their own.</summary>
        public static UserHelpers Instance
        {
            get => _instance ?? (_instance = new UserHelpers(KnowledgeStore.Instance));
            internal set => _instance = value;
        }

        private readonly object _lock = new object();
        private readonly Func<PluginManager> _plugins;
        private PluginManager _subscribedPlugins;
        private Build _current;
        private volatile bool _dirty = true;
        private int _version;
        private bool _pruned;

        public UserHelpers(KnowledgeStore store, Func<PluginManager> plugins = null, string binDir = null)
        {
            Store = store;
            _plugins = plugins ?? (() => PluginManager.Instance);
            BinDir = binDir ?? Path.Combine(Paths.LocalDir, "helpers-bin");
            Store.HelpersChanged += Invalidate;
            InstallResolver();
        }

        public KnowledgeStore Store { get; }
        public string BinDir { get; }

        /// <summary>The helper set changed (Invalidate, after a store or plugin change). Raised on the calling thread.</summary>
        public event Action Changed;

        /// <summary>Helpers or plugins changed: the next Current() builds again (cheap when the set is the same).</summary>
        public void Invalidate()
        {
            _dirty = true;
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("A helpers changed handler failed", ex);
            }
        }

        // ---------------------------------------------------------------- the build

        /// <summary>One helper in the assembly.</summary>
        public sealed class Built
        {
            public string Name;
            public string Source;
            public string Signature;
            public string Summary;
            public string[] Wraps = new string[0];
            public string Path;

            public HelperInfo Info() => new HelperInfo(Name, Signature, Summary, Wraps, Source);
        }

        /// <summary>The current helpers assembly (DllPath null when there are no usable helpers).</summary>
        public sealed class Build
        {
            public int Version;
            public string DllPath;
            public string Hash;
            public List<Built> Helpers = new List<Built>();

            /// <summary>Helpers left out, with the reason (compile errors, a clash).</summary>
            public Dictionary<string, string> Skipped = new Dictionary<string, string>(StringComparer.Ordinal);

            public Built Find(string name) => Helpers.FirstOrDefault(h => h.Name == name);
        }

        /// <summary>The current build; compiles when helpers or plugins changed (call off the UI thread: the first compile takes seconds).</summary>
        public Build Current()
        {
            SubscribePlugins();
            lock (_lock)
            {
                if (_current != null && !_dirty) return _current;
                _dirty = false;
                Build build;
                try
                {
                    build = Compile(Candidates());
                }
                catch (Exception ex)
                {
                    // Never let the helpers stop the agent's cells: run without them and say so in the log.
                    Log.Error("Building the agent's helpers failed", ex);
                    build = new Build();
                }
                if (_current == null || build.DllPath != _current.DllPath) _version++;
                build.Version = _version;
                _current = build;
                return build;
            }
        }

        private void SubscribePlugins()
        {
            PluginManager pm;
            try
            {
                pm = _plugins();
            }
            catch
            {
                return;
            }
            if (pm == null || ReferenceEquals(pm, _subscribedPlugins)) return;
            if (_subscribedPlugins != null) _subscribedPlugins.Changed -= Invalidate;
            _subscribedPlugins = pm;
            pm.Changed += Invalidate;
            _dirty = true;
        }

        /// <summary>A helper to build: from the user's store or an enabled, approved plugin.</summary>
        internal sealed class Candidate
        {
            public string Name;
            public string Source;
            public string Code;
            public string Path;
            public string Summary;
            public string[] Wraps = new string[0];
        }

        /// <summary>Plugin helpers first (a user helper never takes a plugin's name), then the user's in the order they were made.</summary>
        internal List<Candidate> Candidates()
        {
            var list = new List<Candidate>();
            var records = Store.Helpers();
            var disabledPlugin = new HashSet<string>(records.Where(r => r.Source != "user" && r.Disabled).Select(r => r.Source + "/" + r.Name));
            PluginManager pm = null;
            try
            {
                pm = _plugins();
            }
            catch (Exception ex)
            {
                Log.Warn("Reading the plugins for their helpers failed: " + ex.Message);
            }
            if (pm != null)
            {
                foreach (var p in pm.EnabledPlugins().Where(p => p.Manifest != null))
                {
                    var files = p.Manifest.HelperFiles().ToList();
                    if (files.Count == 0) continue;
                    // Helpers are code that runs inside SolidWorks: only once the user approved this exact code (like tool macros).
                    if (!pm.IsTrusted(p)) continue;
                    foreach (var f in files)
                    {
                        var name = System.IO.Path.GetFileNameWithoutExtension(f);
                        var source = "plugin:" + p.Name;
                        if (disabledPlugin.Contains(source + "/" + name)) continue;
                        string code;
                        try
                        {
                            code = File.ReadAllText(f);
                        }
                        catch (IOException ex)
                        {
                            Log.Warn($"Reading the helper {f} failed: {ex.Message}");
                            continue;
                        }
                        var (summary, wraps) = HeaderOf(code);
                        list.Add(new Candidate { Name = name, Source = source, Code = code, Path = f, Summary = summary, Wraps = wraps });
                    }
                }
            }
            foreach (var (r, code) in Store.EnabledUserHelpers())
                list.Add(new Candidate { Name = r.Name, Source = "user", Code = code, Path = Store.HelperPath(r.Name), Summary = r.Summary, Wraps = r.Wraps ?? new string[0] });
            return list;
        }

        /// <summary>A plugin helper's summary and wraps: "// summary: ..." and "// wraps: A, B" lines, or the class's XML summary.</summary>
        internal static (string Summary, string[] Wraps) HeaderOf(string code)
        {
            var summary = Regex.Match(code ?? "", @"^\s*//\s*summary:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            if (summary.Length == 0)
            {
                var xml = Regex.Match(code ?? "", @"<summary>(.*?)</summary>", RegexOptions.Singleline);
                if (xml.Success) summary = Regex.Replace(Regex.Replace(xml.Groups[1].Value, @"^\s*///", "", RegexOptions.Multiline), @"\s+", " ").Trim();
            }
            var wraps = Regex.Match(code ?? "", @"^\s*//\s*wraps:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim()).ToArray();
            return (summary, wraps);
        }

        /// <summary>What a set of candidates compiles to: the good helpers (with the compilation of all of them) and the skipped ones.</summary>
        internal sealed class Plan
        {
            public List<(Candidate C, SyntaxTree Tree)> Good = new List<(Candidate, SyntaxTree)>();
            public Dictionary<string, string> Skipped = new Dictionary<string, string>(StringComparer.Ordinal);
            public CSharpCompilation Compilation;

            public string SignatureOf(string name) => Compilation == null ? null : UserHelpers.SignatureOf(Compilation.GetTypeByMetadataName(Namespace + "." + name));
        }

        /// <summary>
        /// Check every candidate alone, then all the good ones together: a clash (the same type twice, the same extension
        /// method twice) drops the later helper, never the whole set. Plugin helpers come first, so they win.
        /// </summary>
        internal static Plan PlanBuild(List<Candidate> candidates, string assemblyName = AssemblyPrefix + "Plan")
        {
            var plan = new Plan();
            if (candidates.Count == 0) return plan;
            var refs = References();
            var usings = UsingsTree();
            var reserved = ReservedNames();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var good = plan.Good;
            foreach (var c in candidates)
            {
                if (!names.Add(c.Name))
                {
                    var owner = candidates.First(x => x.Name == c.Name).Source;
                    plan.Skipped[c.Name] = $"the name {c.Name} is taken by {(owner == "user" ? "another helper" : owner)}: pick another name";
                    continue;
                }
                var error = reserved.TryGetValue(c.Name, out var what) ? $"the name {c.Name} is taken by {what}: pick another name" : null;
                SyntaxTree tree = null;
                if (error == null) (tree, error) = ValidateAlone(c.Name, c.Code, refs, usings);
                if (error != null) plan.Skipped[c.Name] = error;
                else good.Add((c, tree));
            }

            CSharpCompilation Together() => CSharpCompilation.Create(assemblyName, new[] { usings }.Concat(good.Select(g => g.Tree)), refs, CompileOptions);
            for (var round = 0; round <= candidates.Count && good.Count > 0; round++)
            {
                plan.Compilation = Together();
                var errors = plan.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                if (errors.Count == 0) break;
                var bad = good.LastOrDefault(g => errors.Any(e => e.Location.SourceTree == g.Tree));
                if (bad.C == null)
                {
                    Log.Warn("The helpers don't compile together, and not because of one helper: " + string.Join("; ", errors.Take(5).Select(e => e.ToString())));
                    foreach (var g in good) plan.Skipped[g.C.Name] = "the helpers don't compile together: " + string.Join("; ", errors.Take(3).Select(Format));
                    good.Clear();
                    break;
                }
                plan.Skipped[bad.C.Name] = "doesn't compile together with the other helpers: " +
                    string.Join("; ", errors.Where(e => e.Location.SourceTree == bad.Tree).Take(5).Select(Format));
                good.Remove(bad);
            }

            // Two extension methods with the same name and parameters make every call ambiguous: keep the first.
            if (good.Count > 0)
            {
                var seen = BuiltInExtensions(plan.Compilation);
                var removed = false;
                foreach (var g in good.ToList())
                {
                    var methods = plan.Compilation.GetTypeByMetadataName(Namespace + "." + g.C.Name)?.GetMembers().OfType<IMethodSymbol>()
                        .Where(m => m.IsExtensionMethod && m.DeclaredAccessibility == Accessibility.Public).ToList() ?? new List<IMethodSymbol>();
                    var clash = methods.Select(ExtensionKey).FirstOrDefault(seen.ContainsKey);
                    if (clash != null)
                    {
                        plan.Skipped[g.C.Name] = $"its extension method {clash} already exists in {seen[clash]}: calls would be ambiguous. Rename it, or change that helper instead";
                        good.Remove(g);
                        removed = true;
                        continue;
                    }
                    foreach (var m in methods) seen[ExtensionKey(m)] = g.C.Source == "user" ? "the helper " + g.C.Name : g.C.Source + "/" + g.C.Name;
                }
                if (removed) plan.Compilation = good.Count > 0 ? Together() : null;
            }
            if (good.Count == 0) plan.Compilation = null;
            return plan;
        }

        /// <summary>
        /// Would this helper (new, or a new version) build with the others? Error null when it would (with its signature);
        /// else why not (compile errors, a clash, a taken name). Nothing is written.
        /// </summary>
        public (string Error, string Signature) Check(string name, string code, string summary = null, string[] wraps = null)
        {
            var candidates = Candidates();
            var existing = candidates.FindIndex(c => c.Name == name && c.Source == "user");
            var mine = new Candidate { Name = name, Source = "user", Code = code, Summary = summary, Wraps = wraps ?? new string[0] };
            if (existing >= 0) candidates[existing] = mine;
            else candidates.Add(mine);
            var plan = PlanBuild(candidates);
            return plan.Skipped.TryGetValue(name, out var error) ? (error, null) : (null, plan.SignatureOf(name));
        }

        private Build Compile(List<Candidate> candidates)
        {
            var build = new Build();
            var hash = HashOf(candidates);
            build.Hash = hash;
            if (candidates.Count == 0)
            {
                UpdateRecords(candidates, build);
                return build;
            }

            var dll = System.IO.Path.Combine(BinDir, $"helpers-{hash}.dll");
            var sidecar = System.IO.Path.Combine(BinDir, $"helpers-{hash}.json");
            if (File.Exists(sidecar) && TryLoadSidecar(sidecar, candidates, build) && (build.Helpers.Count == 0 || File.Exists(dll)))
            {
                build.DllPath = build.Helpers.Count > 0 ? dll : null;
                UpdateRecords(candidates, build);
                return build;
            }
            build.Helpers.Clear();
            build.Skipped.Clear();

            // The assembly name carries the hash: a changed set is a new assembly, which cells can load next to the old one.
            var plan = PlanBuild(candidates, AssemblyPrefix + hash.Substring(0, 16));
            foreach (var kv in plan.Skipped) build.Skipped[kv.Key] = kv.Value;
            if (plan.Good.Count > 0)
            {
                Directory.CreateDirectory(BinDir);
                var tmp = dll + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                try
                {
                    using (var stream = File.Create(tmp))
                    {
                        var emit = plan.Compilation.Emit(stream);
                        if (!emit.Success) throw new InvalidOperationException("emit failed: " + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5)));
                    }
                    if (File.Exists(dll)) File.Delete(tmp); // the same set was built before
                    else File.Move(tmp, dll);
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                foreach (var g in plan.Good)
                {
                    build.Helpers.Add(new Built
                    {
                        Name = g.C.Name,
                        Source = g.C.Source,
                        Signature = plan.SignatureOf(g.C.Name),
                        Summary = g.C.Summary,
                        Wraps = g.C.Wraps ?? new string[0],
                        Path = g.C.Path,
                    });
                }
                build.DllPath = dll;
            }
            SaveSidecar(sidecar, build);
            UpdateRecords(candidates, build);
            Prune(dll);
            return build;
        }

        /// <summary>
        /// Compile one helper on its own (with the cells' references and usings). Returns its syntax tree, or the reason it
        /// can't be used: compile errors with line numbers, or a structure problem (namespace, class name).
        /// </summary>
        internal static (SyntaxTree Tree, string Error) ValidateAlone(string name, string code, IReadOnlyList<MetadataReference> refs = null, SyntaxTree usings = null)
        {
            refs = refs ?? References();
            usings = usings ?? UsingsTree();
            if (!KnowledgeStore.NamePattern.IsMatch(name ?? ""))
                return (null, "the name must be a C# class name: a capital letter, then letters, digits or _ (3-48 characters)");
            if (string.IsNullOrWhiteSpace(code)) return (null, "the code is empty");
            var tree = CSharpSyntaxTree.ParseText(code, ParseOptions, path: name + ".cs", encoding: Encoding.UTF8);
            var root = (CompilationUnitSyntax)tree.GetRoot();
            var structure = Structure(name, root);
            if (structure != null) return (null, structure);

            var compilation = CSharpCompilation.Create(AssemblyPrefix + "Check." + name, new[] { usings, tree }, refs, CompileOptions);
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0) return (null, "compile errors:\n" + string.Join("\n", errors.Take(20).Select(Format)));

            // A class named like a type the cells already import (Math, IBody2, MoldFrame...) would make that name ambiguous in every cell.
            foreach (var ns in Imports())
            {
                var symbol = FindNamespace(compilation.GlobalNamespace, ns);
                if (symbol != null && symbol.GetTypeMembers(name).Any())
                    return (null, $"{ns}.{name} already exists and cells import {ns}: a helper named {name} would make it ambiguous. Pick another name");
            }
            return (tree, null);
        }

        /// <summary>namespace SwAgent.UserHelpers with one public static class named like the helper (other types go inside it).</summary>
        private static string Structure(string name, CompilationUnitSyntax root)
        {
            if (root.Members.OfType<GlobalStatementSyntax>().Any()) return "a helper is a class, not statements: put the code in a public static class " + name + " in namespace " + Namespace;
            var namespaces = root.Members.OfType<BaseNamespaceDeclarationSyntax>().ToList();
            if (namespaces.Count != 1 || root.Members.Count != 1 || namespaces[0].Name.ToString() != Namespace)
                return $"put exactly one namespace in the file: namespace {Namespace} {{ public static class {name} {{ ... }} }}";
            var ns = namespaces[0];
            if (ns.Members.OfType<BaseNamespaceDeclarationSyntax>().Any()) return "no nested namespaces: everything goes in namespace " + Namespace;
            var types = ns.Members.OfType<BaseTypeDeclarationSyntax>().ToList();
            var main = types.OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == name);
            if (main == null) return $"the file must declare public static class {name} (the class is named like the helper)";
            if (!main.Modifiers.Any(SyntaxKind.StaticKeyword) || !main.Modifiers.Any(SyntaxKind.PublicKeyword)) return $"class {name} must be public static (extension methods need a static class)";
            if (main.TypeParameterList != null) return $"class {name} can't be generic (its methods can)";
            var others = ns.Members.Where(m => m != main).ToList();
            if (others.Count > 0)
                return $"only class {name} may be in the namespace: put other types (records, enums, small classes) inside it as nested types";
            return null;
        }

        // ---------------------------------------------------------------- the cells' references and usings

        /// <summary>The cells' references (ScriptHost's base options): SolidWorks interop, xCAD, Newtonsoft, SwAgent.dll, ...</summary>
        internal static IReadOnlyList<MetadataReference> References() =>
            ScriptHost.BaseOptions.MetadataReferences.OfType<MetadataReference>().ToList();

        /// <summary>The namespaces cells import (except the helpers' own).</summary>
        internal static IReadOnlyList<string> Imports() => ScriptHost.BaseOptions.Imports.Where(i => i != Namespace).ToList();

        /// <summary>The cells' imports as global usings, so a helper reads like a cell.</summary>
        private static SyntaxTree UsingsTree() =>
            CSharpSyntaxTree.ParseText(string.Concat(Imports().Select(i => "global using " + i + ";\n")), ParseOptions, path: "usings.cs", encoding: Encoding.UTF8);

        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        private static readonly CSharpCompilationOptions CompileOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release, allowUnsafe: false, platform: Platform.AnyCpu);

        /// <summary>Names a helper may not take: ScriptGlobals' members (Model, Mold, Bodies...) and the built-in helpers' classes.</summary>
        internal static Dictionary<string, string> ReservedNames()
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in typeof(ScriptGlobals).GetMembers(BindingFlags.Public | BindingFlags.Instance)) names[m.Name] = "the cells' " + m.Name;
            foreach (var h in HelperIndex.BuiltIn.All())
            {
                var first = h.Name.Split('.')[0];
                if (first.Length > 0) names[first] = "the built-in helper " + h.Name;
            }
            return names;
        }

        private static INamespaceSymbol FindNamespace(INamespaceSymbol global, string ns)
        {
            var current = global;
            foreach (var part in ns.Split('.'))
            {
                current = current?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
                if (current == null) return null;
            }
            return current;
        }

        /// <summary>The extension methods cells already have from the namespaces they import (built-in helpers), by key.</summary>
        private static Dictionary<string, string> BuiltInExtensions(Compilation compilation)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ns in Imports().Where(i => i.StartsWith("SwAgent.", StringComparison.Ordinal)))
            {
                var symbol = FindNamespace(compilation.GlobalNamespace, ns);
                foreach (var type in symbol?.GetTypeMembers().Where(t => t.MightContainExtensionMethods) ?? Enumerable.Empty<INamedTypeSymbol>())
                foreach (var m in type.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsExtensionMethod && m.DeclaredAccessibility == Accessibility.Public))
                    seen[ExtensionKey(m)] = "the built-in " + ns + "." + type.Name;
            }
            return seen;
        }

        private static string ExtensionKey(IMethodSymbol m) =>
            m.Name + "(" + string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))) + ")";

        /// <summary>"IModelDoc2.FeatureByName(string name): IFeature; Lookup.All(IModelDoc2 model): List&lt;IFeature&gt;" (up to 4 methods).</summary>
        internal static string SignatureOf(INamedTypeSymbol type)
        {
            if (type == null) return null;
            var fmt = SymbolDisplayFormat.MinimallyQualifiedFormat;
            var methods = type.GetMembers().OfType<IMethodSymbol>()
                .Where(m => m.DeclaredAccessibility == Accessibility.Public && m.IsStatic && m.MethodKind == MethodKind.Ordinary)
                .ToList();
            var parts = methods.Take(4).Select(m =>
            {
                var ps = m.IsExtensionMethod ? m.Parameters.Skip(1) : m.Parameters;
                var owner = m.IsExtensionMethod ? m.Parameters[0].Type.ToDisplayString(fmt) : type.Name;
                var args = string.Join(", ", ps.Select(p => p.Type.ToDisplayString(fmt) + " " + p.Name + (p.HasExplicitDefaultValue ? " = " + (p.ExplicitDefaultValue?.ToString() ?? "null") : "")));
                return $"{owner}.{m.Name}({args}): {m.ReturnType.ToDisplayString(fmt)}";
            }).ToList();
            if (methods.Count > 4) parts.Add($"+{methods.Count - 4} more");
            return parts.Count == 0 ? type.Name : string.Join("; ", parts);
        }

        internal static string Format(Diagnostic d)
        {
            var span = d.Location.GetLineSpan();
            return $"({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}) {d.Id}: {d.GetMessage()}";
        }

        private static string HashOf(List<Candidate> candidates)
        {
            var sb = new StringBuilder();
            sb.Append(typeof(ScriptGlobals).Assembly.ManifestModule.ModuleVersionId).Append('\n');
            foreach (var c in candidates) sb.Append(c.Source).Append('/').Append(c.Name).Append('\n').Append(c.Code).Append("\n\0\n");
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Take(12).Select(b => b.ToString("x2")));
        }

        // ---------------------------------------------------------------- records, sidecar, old assemblies

        /// <summary>The user helpers' lastError and signature in helpers.json (plugin helpers keep theirs in memory).</summary>
        private void UpdateRecords(List<Candidate> candidates, Build build)
        {
            try
            {
                Store.UpdateHelpers(get =>
                {
                    foreach (var c in candidates.Where(c => c.Source == "user"))
                    {
                        var r = get(c.Name, "user");
                        if (r == null) continue;
                        r.LastError = build.Skipped.TryGetValue(c.Name, out var e) ? e : null;
                        var built = build.Helpers.FirstOrDefault(h => h.Name == c.Name && h.Source == "user");
                        if (built?.Signature != null) r.Signature = built.Signature;
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Warn("Updating helpers.json failed: " + ex.Message);
            }
        }

        private static void SaveSidecar(string path, Build build)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                File.WriteAllText(path, new JObject
                {
                    ["helpers"] = new JArray(build.Helpers.Select(h => new JObject { ["name"] = h.Name, ["source"] = h.Source, ["signature"] = h.Signature })),
                    ["skipped"] = JObject.FromObject(build.Skipped),
                }.ToString());
            }
            catch (Exception ex)
            {
                Log.Warn("Saving the helpers build record failed: " + ex.Message);
            }
        }

        private static bool TryLoadSidecar(string path, List<Candidate> candidates, Build build)
        {
            try
            {
                var j = JObject.Parse(File.ReadAllText(path));
                foreach (var h in (JArray)j["helpers"])
                {
                    var c = candidates.FirstOrDefault(x => x.Name == (string)h["name"] && x.Source == (string)h["source"]);
                    if (c == null) return false;
                    build.Helpers.Add(new Built { Name = c.Name, Source = c.Source, Signature = (string)h["signature"], Summary = c.Summary, Wraps = c.Wraps ?? new string[0], Path = c.Path });
                }
                foreach (var p in ((JObject)j["skipped"]).Properties()) build.Skipped[p.Name] = (string)p.Value;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Reading the helpers build record failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Delete earlier assemblies once per session (one still loaded by a running SolidWorks stays until next time).</summary>
        private void Prune(string keep)
        {
            if (_pruned) return;
            _pruned = true;
            try
            {
                foreach (var f in Directory.GetFiles(BinDir, "helpers-*.*"))
                {
                    if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), System.IO.Path.GetFileNameWithoutExtension(keep), StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        File.Delete(f);
                    }
                    catch
                    {
                        // in use
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Cleaning old helper assemblies failed: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- loading at run time

        private static bool _resolverInstalled;
        private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> BinDirs = new List<string>();

        /// <summary>
        /// Cells reference the helpers assembly by path; if the runtime asks for it by name (outside Roslyn's own loader), load
        /// it from helpers-bin.
        /// </summary>
        private void InstallResolver()
        {
            lock (Loaded)
            {
                if (!BinDirs.Contains(BinDir, StringComparer.OrdinalIgnoreCase)) BinDirs.Add(BinDir);
                if (_resolverInstalled) return;
                _resolverInstalled = true;
            }
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                if (!name.StartsWith(AssemblyPrefix, StringComparison.Ordinal)) return null;
                lock (Loaded)
                {
                    if (Loaded.TryGetValue(name, out var asm)) return asm;
                    var hash = name.Substring(AssemblyPrefix.Length);
                    var file = BinDirs.Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, $"helpers-{hash}*.dll")).FirstOrDefault();
                    if (file == null) return null;
                    asm = Assembly.LoadFrom(file);
                    Loaded[name] = asm;
                    return asm;
                }
            };
        }

        // ---------------------------------------------------------------- what a cell uses

        /// <summary>
        /// The helpers a compiled cell calls (user and plugin helpers, by the class their member belongs to), for the crash
        /// guard and the use counts.
        /// </summary>
        internal static List<Built> UsedBy(Compilation cell, Build build)
        {
            var used = new List<Built>();
            if (cell == null || build == null || build.Helpers.Count == 0) return used;
            foreach (var tree in cell.SyntaxTrees)
            {
                var model = cell.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var info = model.GetSymbolInfo(node);
                    var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
                    while (type?.ContainingType != null) type = type.ContainingType;
                    if (type == null || type.ContainingNamespace?.ToDisplayString() != Namespace) continue;
                    var helper = build.Find(type.Name);
                    if (helper != null && !used.Contains(helper)) used.Add(helper);
                }
            }
            return used;
        }

        /// <summary>
        /// The API members a cell calls (x.Member(...) and x?.Member(...)) that aren't the add-in's own: what a helper or an API
        /// note may know better. By name, so a call that doesn't compile (the wrong interface) still counts.
        /// </summary>
        internal static HashSet<string> RawCalls(Compilation cell)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (cell == null) return names;
            foreach (var tree in cell.SyntaxTrees)
            {
                var model = cell.GetSemanticModel(tree);
                foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    SimpleNameSyntax name = null;
                    if (call.Expression is MemberAccessExpressionSyntax ma) name = ma.Name;
                    else if (call.Expression is MemberBindingExpressionSyntax mb) name = mb.Name;
                    if (name == null) continue;
                    var symbol = model.GetSymbolInfo(name).Symbol;
                    var ns = symbol?.ContainingNamespace?.ToDisplayString() ?? "";
                    if (ns == "SwAgent" || ns.StartsWith("SwAgent.", StringComparison.Ordinal)) continue; // a helper, Mold.*, Geo.*
                    names.Add(name.Identifier.Text);
                }
            }
            return names;
        }
    }
}

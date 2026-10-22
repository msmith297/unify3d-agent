using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.Tools;

namespace SwAgent.Scripting
{
    /// <summary>
    /// A persistent C# REPL per chat thread (Roslyn scripting), running on the SolidWorks UI thread so COM calls
    /// are in-process and fast. Compilation happens on a background thread; execution happens on the caller's
    /// (UI) thread.
    /// </summary>
    public sealed class ScriptHost
    {
        private const int MaxOutputChars = 30000;

        private sealed class Session
        {
            public ScriptGlobals Globals;
            public ScriptState<object> State;
            public CancellationTokenSource Cts = new CancellationTokenSource();

            /// <summary>The helpers assembly version State was compiled against (-1: none yet).</summary>
            public int HelpersVersion = -1;
        }

        private readonly ConcurrentDictionary<string, Session> _sessions = new ConcurrentDictionary<string, Session>();

        /// <summary>Tool inputs for the next cell of a thread (set by RunMacroAsync; not part of the agent's tool schema).</summary>
        private readonly ConcurrentDictionary<string, ToolInputs> _inputs = new ConcurrentDictionary<string, ToolInputs>();
        private static readonly Lazy<ScriptOptions> Base = new Lazy<ScriptOptions>(BuildOptions);
        private readonly ToolRegistry _registry;
        private readonly UserHelpers _helpers;
        private readonly CellCoach _coach = new CellCoach();
        private readonly object _optionsLock = new object();
        private ScriptOptions _options;
        private int _optionsVersion = -1;

        public ScriptHost(ToolRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>A host with its own helpers (tests).</summary>
        internal ScriptHost(ToolRegistry registry, UserHelpers helpers)
        {
            _registry = registry;
            _helpers = helpers;
        }

        /// <summary>The cells' references and imports without the helpers assembly (the helpers compile against these).</summary>
        internal static ScriptOptions BaseOptions => Base.Value;

        /// <summary>The helpers cells see: the agent's own and the enabled plugins' (KnowledgeTools saves them).</summary>
        internal UserHelpers Helpers => _helpers ?? UserHelpers.Instance;

        /// <summary>
        /// The options for the next cell: the base options plus the current helpers assembly, imported. Rebuilt whenever the
        /// helpers change (their Version). Compiles the helpers when they changed, so call it off the UI thread.
        /// </summary>
        internal (ScriptOptions Options, UserHelpers.Build Helpers) CurrentOptions()
        {
            var build = Helpers.Current();
            lock (_optionsLock)
            {
                if (_options == null || _optionsVersion != build.Version)
                {
                    var options = BaseOptions;
                    if (build.DllPath != null)
                        options = options.AddReferences(MetadataReference.CreateFromFile(build.DllPath)).AddImports(UserHelpers.Namespace);
                    _options = options;
                    _optionsVersion = build.Version;
                }
                return (_options, build);
            }
        }

        public sealed class ExecuteInput
        {
            [ToolParam("Short human-readable summary of what the code does (shown in the chat and used as the undo step name). Write it first.")]
            public string Title { get; set; }

            [ToolParam("\"change\" if the code modifies the model or document (creates/edits features, sketches, dimensions, appearance, files); " +
                       "\"check\" if it only reads, measures, lists or verifies. Finished turns show the changes and fold the checks away.")]
            public string Kind { get; set; }

            [ToolParam("C# script to run. Top-level statements; the last expression's value is returned. " +
                       "Variables persist between calls in this chat. In scope: swApp (ISldWorks), Model (IModelDoc2 of this chat's " +
                       "document), App/Doc (xCAD wrappers), Agent (await Agent.CallToolAsync(name, input)), Print(obj), " +
                       "mm(x)/inch(x)/deg(x) unit helpers, Ref(\"S1\")/SelectRefs(\"S1\",\"S2\") for the user's referenced selections, ShowWork()/Focus(...)/Refresh() to keep the work in view, Ct (CancellationToken), WorkDir. Namespaces already imported: System, " +
                       "System.Linq, System.Collections.Generic, System.IO, System.Text, SolidWorks.Interop.sldworks, " +
                       "SolidWorks.Interop.swconst.", Required = true)]
            public string Code { get; set; }

            [ToolParam("Capture an isometric screenshot after running (default false).")]
            public bool? CaptureAfter { get; set; }
        }

        [AgentTool("execute_csharp",
            "Run C# against the live SolidWorks API (full COM API: ISldWorks, IModelDoc2, IFeatureManager, ISketchManager, " +
            "IModelDocExtension, ...). Use for everything the other tools don't cover: creating sketches and features, " +
            "editing dimensions, assemblies and mates, drawings, custom properties, exports. Lengths are meters and angles " +
            "radians (use mm()/deg()). Each call is recorded as one undo step. Measuring helpers: Geo.EdgePoints(edge), " +
            "Geo.LoopPoints(loop), Geo.Frame(planarFace) -> u/v/n frame (ToUV, ToModel, Height), Geo.LoopUV(loop, frame), " +
            "Geo.Distance(point or loop, edge/edges/loop), Geo.Inside(uv, polygon), Geo.Box(uv) - use them instead of hand-written " +
            "edge sampling. Mold.* (design-compression-mold building blocks: Frame, InsertPart, ScaleForShrink, Plate, Subtract, Folder, " +
            "FlashGrooves... each checked by measurement). Returns console output, the last expression's value, compile errors with line " +
            "numbers, or the exception.")]
        public Task<ToolResult> Execute(ToolContext ctx, ExecuteInput input) => ExecuteAsync(ctx, input, learn: true);

        /// <summary>
        /// Run a cell. learn: the agent's own cell (execute_csharp) - it counts for the "that worked after N failed tries"
        /// nudge and gets helper hints and API notes; tool macros and test_helper cells don't.
        /// </summary>
        internal async Task<ToolResult> ExecuteAsync(ToolContext ctx, ExecuteInput input, bool learn)
        {
            var threadKey = ctx.ThreadId ?? "default";
            var session = _sessions.GetOrAdd(ctx.ThreadId ?? "default", _ => new Session());
            var globals = session.Globals ?? (session.Globals = new ScriptGlobals
            {
                WorkDir = Directory.CreateDirectory(Path.Combine(Paths.LocalDir, "work", Safe(ctx.ThreadId ?? "default"))).FullName,
            });
            globals.swApp = ctx.Sw;
            globals.Model = ctx.Model;
            globals.App = ctx.App;
            globals.Doc = ctx.Doc;
            globals.Refs = ctx.Refs;
            globals.Inputs = _inputs.TryRemove(ctx.ThreadId ?? "default", out var toolInputs) ? toolInputs : ToolInputs.Empty;
            globals.Ct = session.Cts.Token;
            globals.Agent = new AgentApi((name, json) =>
                name == "execute_csharp"
                    ? Task.FromResult(ToolResult.Error("execute_csharp cannot call itself"))
                    : _registry.InvokeAsync(name, json, ctx));
            globals.Output.Clear();

            // 1. Compile off the UI thread, with the current helpers. When they changed since this chat's last cell, its script
            // session starts again (an old and a new helpers assembly in one chain would make every helper call ambiguous),
            // carrying the variables over.
            var (options, helpers) = await Task.Run(() => CurrentOptions());
            var notes = new List<string>();
            if (session.State != null && session.HelpersVersion != helpers.Version)
                notes.Add(await RestartSessionAsync(session, globals, options));
            session.HelpersVersion = helpers.Version;
            Script<object> script = session.State == null
                ? CSharpScript.Create(input.Code, options, typeof(ScriptGlobals))
                : session.State.Script.ContinueWith(input.Code, options);
            var compileWatch = Stopwatch.StartNew();
            var diagnostics = await Task.Run(() => script.Compile());
            compileWatch.Stop();
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var (rawCalls, usedHelpers) = await Task.Run(() => Inspect(script, helpers));
            if (errors.Count > 0)
            {
                var failed = "Compilation failed (nothing ran):\n" + string.Join("\n", errors.Take(30).Select(FormatDiagnostic));
                if (learn)
                {
                    notes.AddRange(Advice(input.Code, rawCalls, helpers, failed: true));
                    _coach.Record(threadKey, false, CellCoach.FailureKind(failed));
                }
                return ToolResult.Error(failed + Tail(notes));
            }

            // 2. Run on this (UI) thread, as one undo step.
            var model = ctx.Model;
            var title = string.IsNullOrWhiteSpace(input.Title) ? "Agent edit" : input.Title.Trim();
            var originalOut = Console.Out;
            var runWatch = Stopwatch.StartNew();
            var treeBefore = SwAgent.Tools.FeatureDiff.Take(model);
            var fingerprint = SwAgent.Tools.UndoLedger.Begin(model);
            ScriptState<object> state;
            var guard = CrashGuard.Enter("cell", null, title, input.Code);
            // If SolidWorks dies in this cell, the crash record names the helpers it called (two crashes disable a helper).
            if (usedHelpers.Count > 0) CrashGuard.Annotate("helpers", usedHelpers.Select(HelperKey));
            var quiet = new QuietDialogs(ctx.Sw);
            try
            {
                Console.SetOut(new CellWriter(globals.Output));
                TryStartUndo(model);
                state = session.State == null
                    ? await script.RunAsync(globals, ex => true, session.Cts.Token)
                    : await script.RunFromAsync(session.State, ex => true, session.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Error("Cancelled by the user.");
            }
            finally
            {
                quiet.Dispose();
                guard.Dispose();
                Console.SetOut(originalOut);
                TryFinishUndo(model, title);
                runWatch.Stop();
                SwAgent.Tools.UndoLedger.Record(model, ctx.ToolUseId, title, fingerprint);
            }

            // Keep the new state even if the cell threw, so earlier variables survive a failing cell.
            session.State = state;
            var report = BuildReport(globals, state, compileWatch.ElapsedMilliseconds, runWatch.ElapsedMilliseconds);
            var treeChanges = SwAgent.Tools.FeatureDiff.Describe(treeBefore, SwAgent.Tools.FeatureDiff.Take(model));
            if (treeChanges != null) report += "\n--- " + treeChanges + " (all made by this cell)";
            if (usedHelpers.Count > 0) Infrastructure.Safe.Run("Count helper uses", () => Helpers.Store.CountUses(usedHelpers.Select(h => (h.Name, h.Source))));
            if (learn)
            {
                var ok = state.Exception == null;
                notes.AddRange(Advice(input.Code, rawCalls, helpers, failed: !ok));
                var nudge = _coach.Record(threadKey, ok, ok ? null : CellCoach.FailureKind(report));
                if (nudge != null) notes.Add(nudge);
            }
            report += Tail(notes);
            var result = state.Exception == null ? ToolResult.Ok(report) : ToolResult.Error(report);

            if (input.CaptureAfter == true && model != null)
            {
                try
                {
                    var shot = await _registry.InvokeAsync("capture_views",
                        new JObject { ["views"] = new JArray("isometric") }, ctx);
                    result.Content.AddRange(shot.Content);
                }
                catch (Exception ex)
                {
                    result.Text("Screenshot failed: " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>Run a tool's macro with its PropertyManager inputs, as one undo step (same report as execute_csharp).</summary>
        public Task<ToolResult> RunMacroAsync(ToolContext ctx, string title, string code, ToolInputs inputs)
        {
            _inputs[ctx.ThreadId ?? "default"] = inputs ?? ToolInputs.Empty;
            return ExecuteAsync(ctx, new ExecuteInput { Title = title, Kind = "change", Code = code }, learn: false);
        }

        /// <summary>
        /// Compile a tool's preview code once (off the UI thread) into a delegate that runs in milliseconds, so the
        /// preview can follow every change in the PropertyManager page. Returns null and the errors if it doesn't compile.
        /// </summary>
        public async Task<(ScriptRunner<object> Runner, string Errors)> CompilePreviewAsync(string code)
        {
            var (options, _) = await Task.Run(() => CurrentOptions());
            var script = CSharpScript.Create(code, options, typeof(ScriptGlobals));
            var diagnostics = await Task.Run(() => script.Compile());
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0) return (null, string.Join("\n", errors.Take(10).Select(FormatDiagnostic)));
            return (script.CreateDelegate(), null);
        }

        /// <summary>Request cancellation of the running cell (cooperative: the cell must check Ct).</summary>
        public void Cancel(string threadId)
        {
            if (threadId != null && _sessions.TryGetValue(threadId, out var s))
            {
                s.Cts.Cancel();
                s.Cts = new CancellationTokenSource();
            }
        }

        /// <summary>Forget the variables of a thread (new chat / reset).</summary>
        public void Reset(string threadId)
        {
            if (threadId != null) _sessions.TryRemove(threadId, out _);
            _coach.Reset(threadId);
        }

        // ---------------------------------------------------------------- the self-improving loop

        /// <summary>The API members a cell calls raw, and the user/plugin helpers it calls (empty when that can't be read).</summary>
        private static (HashSet<string> Raw, List<UserHelpers.Built> Used) Inspect(Script<object> script, UserHelpers.Build helpers)
        {
            try
            {
                var compilation = script.GetCompilation();
                return (UserHelpers.RawCalls(compilation), UserHelpers.UsedBy(compilation, helpers));
            }
            catch (Exception ex)
            {
                Log.Warn("Reading a cell's calls failed: " + ex.Message);
                return (new HashSet<string>(), new List<UserHelpers.Built>());
            }
        }

        /// <summary>"Name" for a user helper, "plugin:x/Name" for a plugin's (the crash record's form; KnowledgeStore.RecordCrash reads it back).</summary>
        internal static string HelperKey(UserHelpers.Built h) => h.Source == "user" ? h.Name : h.Source + "/" + h.Name;

        /// <summary>
        /// Lines for an agent cell's result: helpers that wrap the raw calls it made, and (when it failed) the API notes about
        /// those calls and the disabled helpers it tried to use.
        /// </summary>
        private IEnumerable<string> Advice(string code, HashSet<string> rawCalls, UserHelpers.Build helpers, bool failed)
        {
            var lines = new List<string>();
            try
            {
                var index = HelperIndex.BuiltIn.All().Concat(helpers.Helpers.Select(h => h.Info()));
                lines.AddRange(CellCoach.Hints(rawCalls, index).Select(h => "Helper: " + h));
                if (!failed) return lines;
                var store = Helpers.Store;
                var shown = store.NotesForMembers(rawCalls).Take(2).ToList();
                foreach (var n in shown)
                    lines.Add($"API note {n.Id} ({n.Topic}): {n.Right}" + (string.IsNullOrEmpty(n.Wrong) ? "" : $" Not: {n.Wrong}"));
                if (shown.Count > 0) store.Hit(shown.Select(n => n.Id));
                foreach (var r in store.Helpers().Where(r => r.Disabled && code != null && code.Contains(r.Name)).Take(2))
                    lines.Add($"The helper {r.Name} is disabled: {r.DisabledReason}");
            }
            catch (Exception ex)
            {
                Log.Warn("Building a cell's advice failed: " + ex.Message);
            }
            return lines;
        }

        private static string Tail(List<string> lines) => lines.Count == 0 ? "" : string.Concat(lines.Where(l => !string.IsNullOrEmpty(l)).Select(l => "\n--- " + l));

        /// <summary>
        /// The helpers changed since this chat's last cell: start a new script session (its compilation can't hold the old and
        /// the new helpers assembly together) and carry the variables over - each one whose type can be named in C#, through
        /// a first submission of typed assignments. Returns the line that tells the agent.
        /// </summary>
        private async Task<string> RestartSessionAsync(Session session, ScriptGlobals globals, ScriptOptions options)
        {
            var old = session.State;
            session.State = null;
            var latest = old.Variables.GroupBy(v => v.Name).Select(g => g.Last()).Where(v => !v.Name.StartsWith("<", StringComparison.Ordinal)).ToList();
            var dropped = new List<string>();
            var lines = new List<(string Name, string Line, string Key)>();
            foreach (var v in latest)
            {
                var type = CSharpTypeName.Of(v.Type);
                if (type == null)
                {
                    dropped.Add(v.Name);
                    continue;
                }
                var key = ScriptCarry.Put(v.Value);
                lines.Add((v.Name, $"{type} {v.Name} = ({type})global::SwAgent.Scripting.ScriptCarry.Take(\"{key}\");", key));
            }
            var carried = new List<string>();
            try
            {
                for (var attempt = 0; attempt < 3 && lines.Count > 0; attempt++)
                {
                    var preamble = CSharpScript.Create(string.Join("\n", lines.Select(l => l.Line)), options, typeof(ScriptGlobals));
                    var errors = (await Task.Run(() => preamble.Compile())).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                    if (errors.Count == 0)
                    {
                        var state = await preamble.RunAsync(globals, ex => true);
                        if (state.Exception == null)
                        {
                            session.State = state;
                            carried.AddRange(lines.Select(l => l.Name));
                        }
                        else
                        {
                            dropped.AddRange(lines.Select(l => l.Name));
                        }
                        break;
                    }
                    var bad = new HashSet<int>(errors.Select(e => e.Location.GetLineSpan().StartLinePosition.Line));
                    dropped.AddRange(lines.Where((l, i) => bad.Contains(i)).Select(l => l.Name));
                    lines = lines.Where((l, i) => !bad.Contains(i)).ToList();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Carrying variables into the new script session failed: " + ex.Message);
                session.State = null;
                dropped.AddRange(lines.Select(l => l.Name).Where(n => !carried.Contains(n)));
                carried.Clear();
            }
            finally
            {
                foreach (var l in lines) ScriptCarry.Take(l.Key);
            }
            return "The helpers changed since your last cell, so this cell runs in a new script session (the new helpers can't load into the old one)." +
                   (carried.Count > 0 ? " Variables carried over: " + string.Join(", ", carried) + "." : "") +
                   (dropped.Count > 0 ? " Not carried (their type was declared in a cell): " + string.Join(", ", dropped.Distinct()) + "." : "") +
                   " Methods and classes declared in earlier cells are gone: declare them again if you need them.";
        }

        private static string BuildReport(ScriptGlobals globals, ScriptState<object> state, long compileMs, long runMs)
        {
            var sb = new StringBuilder();
            sb.AppendLine(state.Exception == null
                ? $"OK (compile {compileMs} ms, run {runMs} ms)"
                : $"EXCEPTION after {runMs} ms - changes made before the exception are kept (undo to revert)");

            var output = globals.Output.ToString();
            if (output.Length > 0)
            {
                if (output.Length > MaxOutputChars) output = output.Substring(0, MaxOutputChars) + "\n... (output truncated)";
                sb.AppendLine("--- output ---").AppendLine(output.TrimEnd());
            }

            if (state.Exception != null)
            {
                var ex = state.Exception;
                sb.AppendLine("--- exception ---").AppendLine($"{ex.GetType().FullName}: {ex.Message}");
                if (ex is System.Runtime.InteropServices.COMException com) sb.AppendLine($"HRESULT 0x{com.ErrorCode:X8}");
                var frames = (ex.StackTrace ?? "").Split('\n')
                    .Where(l => l.Contains("Submission#") || l.Contains("SolidWorks."))
                    .Take(6)
                    .Select(l => l.Trim());
                foreach (var f in frames) sb.AppendLine(f);
            }
            else if (state.ReturnValue != null)
            {
                sb.AppendLine("--- return value ---").AppendLine(ValueFormatter.Format(state.ReturnValue));
            }
            return sb.ToString().TrimEnd();
        }

        private static string FormatDiagnostic(Diagnostic d)
        {
            var span = d.Location.GetLineSpan();
            return $"({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}) {d.Id}: {d.GetMessage()}";
        }

        private static void TryStartUndo(IModelDoc2 model)
        {
            try
            {
                model?.Extension.StartRecordingUndoObject();
            }
            catch (Exception ex)
            {
                Log.Warn("StartRecordingUndoObject failed: " + ex.Message);
            }
        }

        private static void TryFinishUndo(IModelDoc2 model, string title)
        {
            try
            {
                model?.Extension.FinishRecordingUndoObject2("Agent: " + (title.Length > 60 ? title.Substring(0, 60) : title), false);
            }
            catch (Exception ex)
            {
                Log.Warn("FinishRecordingUndoObject2 failed: " + ex.Message);
            }
        }

        private static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

        private static ScriptOptions BuildOptions()
        {
            var assemblies = new List<Assembly>
            {
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(Uri).Assembly,
                typeof(System.Drawing.Bitmap).Assembly,
                typeof(System.Windows.Forms.Form).Assembly,
                typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly,
                typeof(Microsoft.VisualBasic.Information).Assembly,
                typeof(System.Runtime.InteropServices.Marshal).Assembly,
                typeof(JObject).Assembly,
                typeof(ISldWorks).Assembly,
                typeof(swDocumentTypes_e).Assembly,
                typeof(Xarial.XCad.IXApplication).Assembly,
                typeof(Xarial.XCad.SolidWorks.ISwApplication).Assembly,
                typeof(ScriptGlobals).Assembly,
            };

            var refs = assemblies.Distinct().Select(a => MetadataReference.CreateFromFile(a.Location)).ToList();
            var netstandard = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "netstandard.dll");
            if (File.Exists(netstandard)) refs.Add(MetadataReference.CreateFromFile(netstandard));

            return ScriptOptions.Default
                .WithReferences(refs)
                .WithImports("System", "System.IO", "System.Linq", "System.Text", "System.Collections.Generic",
                    "System.Threading.Tasks", "System.Runtime.InteropServices",
                    "SolidWorks.Interop.sldworks", "SolidWorks.Interop.swconst",
                    // the design-compression-mold building blocks' types (MoldFrame, FlashOptions, PartAnalysisOptions, MoldBody)
                    "SwAgent.Mold.Geometry")
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithEmitDebugInformation(false)
                .WithAllowUnsafe(false);
        }
    }

    /// <summary>
    /// Hands a chat's variables to its new script session after the helpers changed (ScriptHost.RestartSessionAsync): the
    /// first submission of the new session reads each value back once. Not for agent code.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class ScriptCarry
    {
        private static readonly ConcurrentDictionary<string, object> Values = new ConcurrentDictionary<string, object>();

        internal static string Put(object value)
        {
            var key = Guid.NewGuid().ToString("N");
            Values[key] = value;
            return key;
        }

        public static object Take(string key) => key != null && Values.TryRemove(key, out var v) ? v : null;
    }

    /// <summary>A type as C# source ("global::System.Collections.Generic.List&lt;global::SolidWorks.Interop.sldworks.IBody2&gt;"), or null.</summary>
    internal static class CSharpTypeName
    {
        /// <summary>Null for what a new script session can't name: types declared in cells, anonymous types, old helper assemblies.</summary>
        public static string Of(Type t)
        {
            if (t == null || t.IsByRef || t.IsPointer || t.IsGenericParameter) return null;
            if (t.IsArray)
            {
                var element = Of(t.GetElementType());
                return element == null ? null : element + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            }
            var assembly = t.Assembly.GetName().Name ?? "";
            if (t.Assembly.IsDynamic || assembly.StartsWith("ℛ", StringComparison.Ordinal) || assembly.StartsWith(UserHelpers.AssemblyPrefix, StringComparison.Ordinal)) return null;
            if (!t.IsVisible || t.Name.Contains("<") || (t.FullName ?? "").Contains("Submission#")) return null;
            if (t.IsNested)
            {
                if (t.IsGenericType) return null;
                var outer = Of(t.DeclaringType);
                return outer == null ? null : outer + "." + t.Name;
            }
            var prefix = string.IsNullOrEmpty(t.Namespace) ? "global::" : "global::" + t.Namespace + ".";
            if (!t.IsGenericType) return prefix + t.Name;
            var args = t.GetGenericArguments().Select(Of).ToList();
            if (args.Any(a => a == null)) return null;
            return prefix + t.Name.Substring(0, t.Name.IndexOf('`')) + "<" + string.Join(", ", args) + ">";
        }
    }
}

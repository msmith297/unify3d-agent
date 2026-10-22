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
        }

        private readonly ConcurrentDictionary<string, Session> _sessions = new ConcurrentDictionary<string, Session>();

        /// <summary>Tool inputs for the next cell of a thread (set by RunMacroAsync; not part of the agent's tool schema).</summary>
        private readonly ConcurrentDictionary<string, ToolInputs> _inputs = new ConcurrentDictionary<string, ToolInputs>();
        private readonly Lazy<ScriptOptions> _options = new Lazy<ScriptOptions>(BuildOptions);
        private readonly ToolRegistry _registry;

        public ScriptHost(ToolRegistry registry)
        {
            _registry = registry;
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
        public async Task<ToolResult> Execute(ToolContext ctx, ExecuteInput input)
        {
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

            // 1. Compile off the UI thread.
            var options = _options.Value;
            Script<object> script = session.State == null
                ? CSharpScript.Create(input.Code, options, typeof(ScriptGlobals))
                : session.State.Script.ContinueWith(input.Code, options);
            var compileWatch = Stopwatch.StartNew();
            var diagnostics = await Task.Run(() => script.Compile());
            compileWatch.Stop();
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                // Fix hints (reflection over the interop only, no SolidWorks calls): off the UI thread.
                var hints = await Task.Run(() => CompileAdvisor.Advise(errors, script.GetCompilation()));
                return ToolResult.Error("Compilation failed (nothing ran):\n" + string.Join("\n", errors.Take(30).Select(FormatDiagnostic)) + hints);
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
            return Execute(ctx, new ExecuteInput { Title = title, Kind = "change", Code = code });
        }

        /// <summary>
        /// Compile a tool's preview code once (off the UI thread) into a delegate that runs in milliseconds, so the
        /// preview can follow every change in the PropertyManager page. Returns null and the errors if it doesn't compile.
        /// </summary>
        public async Task<(ScriptRunner<object> Runner, string Errors)> CompilePreviewAsync(string code)
        {
            var script = CSharpScript.Create(code, _options.Value, typeof(ScriptGlobals));
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
                    "SwAgent.Mold.Geometry",
                    "SwAgent.Scripting.Extensions") // Model.FeatureByName, x.AsDoubles(), body.Volume(), Select2 with ISelectData ...
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithEmitDebugInformation(false)
                .WithAllowUnsafe(false);
        }
    }
}

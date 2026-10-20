using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.ModelFiles;
using SwAgent.Scripting;
using SwAgent.UI;
using Xarial.XCad.SolidWorks;
using Xarial.XCad.SolidWorks.Documents;

namespace SwAgent.Tools
{
    /// <summary>
    /// Runs one of the user's Tools (ribbon button or hotkey), the way a native command runs:
    ///   1. Its PropertyManager page opens at once (skipped when everything required is already selected), with a live
    ///      holographic preview if the tool has one.
    ///   2. Meanwhile, for tools with an agent in the loop, context is captured quietly in small steps - model data and
    ///      feature history while the page is open, the view and selection when the user presses OK. Nothing moves:
    ///      the camera, the view and the model are left exactly as they are.
    ///   3. The tool's hard-coded macro runs (one undo step). For a tool without an agent that is the whole job.
    ///      3D models among its files are read in the background (ModelFileContext): six views + measurements.
    ///   4. Otherwise everything is staged into one prompt and handed to the agent in the chat the user is in,
    ///      at the tool's intelligence level.
    /// </summary>
    internal sealed class ToolRunner
    {
        private const int HistoryBudget = 24000;

        /// <summary>The inputs of each tool's latest run (hybrid tools' agent runs the macro with them via run_tool_macro).</summary>
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ToolInputs> LastInputs =
            new System.Collections.Concurrent.ConcurrentDictionary<string, ToolInputs>();

        private readonly ISwApplication _app;
        private readonly ToolRegistry _tools;
        private readonly ScriptHost _scripts;
        private readonly Action<string, object> _send;
        private readonly Action<string> _status;
        private bool _busy;

        public ToolRunner(ISwApplication app, ToolRegistry tools, ScriptHost scripts, Action<string, object> send, Action<string> status)
        {
            _app = app;
            _tools = tools;
            _scripts = scripts;
            _send = send;
            _status = status;
        }

        public async Task RunAsync(string name, Action showPane)
        {
            if (_busy) return; // one tool at a time, like native commands
            var spec = ToolSpec.Load(name);
            if (spec == null) return;
            // A tool written for newer agent tools (requires: in TOOL.md) can't do its job with an older add-in.
            var missing = MissingRequirements(spec, _tools.Names);
            if (missing.Count > 0)
            {
                _status($"Unify3D ▸ {spec.Title}: needs a newer add-in (missing {string.Join(", ", missing)})");
                return;
            }
            if (!(_app.Documents.Active is ISwDocument doc) || doc.Model == null)
            {
                _status($"Unify3D ▸ {spec.Title}: open a document first");
                return;
            }
            _busy = true;
            var model = doc.Model;
            var threadId = "tool:" + name;
            try
            {
                var preselected = ToolPage.CurrentSelection(model);
                var context = new JObject();
                var ctx = new ToolContext(_app, doc, threadId, _tools);

                // 1. Inputs: straight from the selection, or from the tool's page.
                var inputs = spec.Inputs.Count == 0 ? new ToolInputs() : ToolPage.TryFromSelection(spec, model, preselected);
                if (inputs == null)
                {
                    var page = new ToolPage(_app.Sw, model, spec, preselected);
                    page.TierChanged += tier =>
                    {
                        spec.Intelligence = tier;
                        try
                        {
                            UserTools.SetValue(name, "intelligence", tier);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("Saving the tool's intelligence failed: " + ex.Message);
                        }
                    };
                    var pageTask = page.ShowAsync(_scripts);
                    // 2a. While the user fills the page in: what doesn't depend on their inputs (hybrid tools: only if needed, below).
                    if (spec.Agent && !spec.AgentFallback) await CaptureModelContextAsync(spec, ctx, context);
                    inputs = await pageTask;
                    if (inputs == null)
                    {
                        _status($"Unify3D ▸ {spec.Title}: cancelled");
                        return;
                    }
                }
                else if (spec.Agent && !spec.AgentFallback)
                {
                    await CaptureModelContextAsync(spec, ctx, context);
                }

                // 3. The hard-coded part. Its inputs are kept for run_tool_macro (the agent re-runs or adapts it after a hand-over).
                LastInputs[spec.Name] = inputs;
                string macroOutput = null;
                if (spec.Macro != null && spec.Paused.Contains("macro"))
                {
                    // SolidWorks closed while this macro ran last time: don't run it blind again.
                    context["failure"] = "This tool's macro is paused because SolidWorks closed (crashed) while it was running last time. " +
                                         "Do the job carefully with execute_csharp, find what in the macro crashed SolidWorks, and fix it with " +
                                         "edit_user_tool (which un-pauses it). The macro:\n" + spec.Macro;
                }
                else if (spec.Macro != null)
                {
                    _status($"Unify3D ▸ {spec.Title}...");
                    var watch = Stopwatch.StartNew();
                    ToolResult result;
                    using (CrashGuard.Enter("macro", spec.Name, spec.Title, spec.Macro))
                        result = await _scripts.RunMacroAsync(ctx, spec.Title, spec.Macro, inputs);
                    var text = TextOf(result);
                    var userError = Regex.Match(text, @"MacroInputException: (.+)");
                    if (userError.Success)
                    {
                        _status($"Unify3D ▸ {spec.Title}: {userError.Groups[1].Value.Trim()}");
                        return;
                    }
                    if (!result.IsError && (!spec.Agent || spec.AgentFallback))
                    {
                        var value = Regex.Match(text, @"--- return value ---\s*\n(.+)");
                        _status($"Unify3D ▸ {spec.Title}: {(value.Success ? value.Groups[1].Value.Trim().Trim('"') : "done")}  ({watch.Elapsed.TotalSeconds:0.0} s, Ctrl+Z to undo)");
                        Log.Info($"Tool '{name}' ran its macro in {watch.ElapsedMilliseconds} ms");
                        return;
                    }
                    if (result.IsError)
                    {
                        var handoff = Regex.Match(text, @"(?:AgentHandoffException|NeedsAgent): (.+)");
                        Log.Warn($"Tool '{name}' macro {(handoff.Success ? "handed over to the agent" : "failed")}:\n{text}");
                        if (handoff.Success)
                            context["handoff"] = $"The macro in the \"{spec.Title}\" tool handed this case to you: " + handoff.Groups[1].Value.Trim() + "\n\nMacro output:\n" + text;
                        else
                            context["failure"] = text;
                        // Hybrid tools capture the model context only when the agent is actually needed.
                        if (spec.AgentFallback) await CaptureModelContextAsync(spec, ctx, context);
                    }
                    else
                    {
                        macroOutput = text;
                    }
                }

                // 2b + 4. The agent: view and selection as they are now, then hand everything over.
                _status($"Unify3D ▸ {spec.Title}: working...");
                await CaptureViewAsync(spec, ctx, context);
                if (macroOutput != null) context["macroOutput"] = macroOutput;
                var payload = await BuildPayloadAsync(spec, inputs, context);
                showPane?.Invoke();
                _send("runTool", payload);
            }
            catch (Exception ex)
            {
                Log.Error($"Tool '{name}' failed", ex);
                _status($"Unify3D ▸ {spec.Title}: {ex.Message}");
            }
            finally
            {
                _scripts.Reset(threadId);
                _busy = false;
            }
        }

        /// <summary>Model facts and feature history - read-only API calls, one at a time so SolidWorks stays responsive.</summary>
        private async Task CaptureModelContextAsync(ToolSpec spec, ToolContext ctx, JObject context)
        {
            if (spec.Context.Contains("model"))
            {
                await Task.Yield();
                context["model"] = TextOf(await _tools.InvokeAsync("get_model_context", new JObject(), ctx));
            }
            if (spec.Context.Contains("history") || spec.Context.Contains("history_full"))
            {
                await Task.Yield();
                var history = TextOf(await _tools.InvokeAsync("describe_model_history",
                    new JObject { ["include_sketch_geometry"] = spec.Context.Contains("history_full") }, ctx));
                context["history"] = history.Length > HistoryBudget
                    ? history.Substring(0, HistoryBudget) + "\n... (history truncated - call describe_model_history for the rest)"
                    : history;
            }
        }

        /// <summary>A screenshot of exactly what's on screen (no camera change) - taken after the page closed.</summary>
        private async Task CaptureViewAsync(ToolSpec spec, ToolContext ctx, JObject context)
        {
            if (!spec.Context.Contains("view")) return;
            await Task.Yield();
            try
            {
                var shot = await _tools.InvokeAsync("capture_views", new JObject { ["views"] = new JArray("current"), ["max_size"] = 1024 }, ctx);
                var image = shot.Content.FirstOrDefault(c => (string)c["type"] == "image");
                if (image != null) context["view"] = image["source"]?["data"];
            }
            catch (Exception ex)
            {
                Log.Warn("Tool view capture failed: " + ex.Message);
            }
        }

        /// <summary>The agent tools a spec requires that this add-in doesn't have.</summary>
        internal static List<string> MissingRequirements(ToolSpec spec, IEnumerable<string> available)
        {
            var have = new HashSet<string>(available ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            return (spec?.Requires ?? new string[0]).Where(r => !have.Contains(r)).ToList();
        }

        /// <summary>
        /// Everything the panel needs to stage the agent's prompt (it numbers the selections as [S1]...). 3D models among
        /// the files are read here (in the background, see ModelFileContext) and arrive as views + measurements.
        /// </summary>
        private async Task<JObject> BuildPayloadAsync(ToolSpec spec, ToolInputs inputs, JObject context)
        {
            var list = new JArray();
            foreach (var s in spec.Inputs)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Id)) continue;
                var entry = InputEntry(s, inputs);
                if (entry == null) continue; // hidden by ShowWhen, or an info line
                if (ToolSpec.Type(s) == "files")
                {
                    var files = new JArray();
                    foreach (var path in inputs.Files(s.Id))
                    {
                        if (ModelFileContext.IsModelFile(path)) _status($"Unify3D ▸ {spec.Title}: reading {Path.GetFileName(path)}...");
                        files.Add(await FileAttachments.EntryAsync(_app.Sw, path));
                    }
                    entry["files"] = files;
                }
                list.Add(entry);
            }
            return new JObject
            {
                ["tool"] = spec.Name,
                ["title"] = spec.Title,
                ["intelligence"] = spec.Intelligence,
                ["maxSteps"] = spec.MaxSteps,
                ["longRun"] = spec.LongRun,
                ["inputs"] = list,
                ["context"] = context,
            };
        }

        /// <summary>
        /// One input for the panel: id, label, type and the value text the agent reads ("2000 psi (13.79 MPa)") - the C# side
        /// formats it, so the panel prints it as it comes - plus structured si / table / row / display for later use. Null for
        /// inputs hidden by ShowWhen and for info lines. Files are added by the caller (they are read asynchronously).
        /// </summary>
        internal static JObject InputEntry(ToolInputSpec s, ToolInputs inputs)
        {
            var type = ToolSpec.Type(s);
            var meta = inputs.Meta(s.Id);
            if (type == "info" || (meta != null && meta.Hidden && !meta.NotApplicable)) return null;
            var entry = new JObject { ["id"] = s.Id, ["label"] = s.Label, ["type"] = type };
            switch (type)
            {
                case "selection":
                    entry["items"] = new JArray(inputs.SelectedItems(s.Id).Select(i => new JObject
                    {
                        ["type"] = i.Type, ["label"] = i.Label, ["detail"] = i.Detail, ["ref"] = i.Ref,
                    }));
                    return entry;
                case "files":
                    return entry;
                case "number":
                    entry["value"] = meta?.Display ?? Units.Legacy(s.Unit, inputs.Number(s.Id));
                    break;
                case "integer":
                case "size":
                    entry["value"] = meta?.Display ?? JToken.FromObject(inputs[s.Id] ?? "");
                    break;
                default:
                    entry["value"] = meta?.Display != null ? (JToken)meta.Display : JToken.FromObject(inputs[s.Id] ?? "");
                    break;
            }
            if (meta != null)
            {
                if (meta.Display != null) entry["display"] = meta.Display;
                if (meta.Si != null) entry["si"] = meta.Si;
                if (meta.Table != null) entry["table"] = meta.Table.Value;
                if (meta.Edited) entry["edited"] = true;
                if (meta.Unit != null) entry["unit"] = meta.Unit;
                if (meta.NotApplicable) entry["notApplicable"] = true;
                if (meta.Row != null) entry["row"] = meta.Row.DeepClone();
            }
            return entry;
        }

        private static string TextOf(ToolResult r) =>
            string.Join("\n", r.Content.Where(c => (string)c["type"] == "text").Select(c => (string)c["text"]));
    }
}

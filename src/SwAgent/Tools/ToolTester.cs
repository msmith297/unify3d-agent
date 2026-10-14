using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.Scripting;
using SwAgent.UI;

namespace SwAgent.Tools
{
    /// <summary>
    /// test_tool: run a saved tool's code the way the user's click will - same compiler, same inputs, same preview
    /// display path - before telling the user it's ready. Whatever would go wrong (a compile error, an exception, a slow
    /// preview, or a call that crashes SolidWorks) happens here, under the crash guard, instead of in the user's hands.
    /// </summary>
    public sealed class ToolTester
    {
        private readonly ScriptHost _scripts;

        public ToolTester(ScriptHost scripts)
        {
            _scripts = scripts;
        }

        public sealed class TestInput
        {
            [ToolParam("The tool's name.", Required = true)]
            public string Name { get; set; }

            [ToolParam("Also run the macro once on the chat's document with the default inputs and the current selection " +
                       "(one undo step - undo it afterwards if it was only a test). Default false.")]
            public bool? RunMacro { get; set; }
        }

        [AgentTool("test_tool",
            "Test a saved tool exactly as the user's click will run it, before saying it's ready: compiles its macro and preview, " +
            "draws the preview through the real display path (default inputs + the current selection) for a moment and removes it, " +
            "reports timing, and optionally runs the macro. Always test a tool after saving or changing its macro or preview.")]
        public async Task<ToolResult> Test(ToolContext ctx, TestInput input)
        {
            var spec = ToolSpec.Load((input.Name ?? "").Trim().ToLowerInvariant());
            if (spec == null) return ToolResult.Error($"No tool named '{input.Name}'.");
            if (spec.Kind != "tool") return ToolResult.Error($"'{spec.Name}' is a workflow; test_tool is for tools.");
            var model = ctx.RequireModel();
            var sb = new StringBuilder();
            var ok = true;

            // 1. Compile.
            foreach (var (label, code) in new[] { ("macro", spec.Macro), ("preview", spec.Preview) })
            {
                if (code == null) continue;
                var (_, errors) = await _scripts.CompilePreviewAsync(code);
                sb.AppendLine(errors == null ? $"{label}: compiles" : $"{label}: COMPILE ERRORS\n{errors}");
                ok &= errors == null;
            }
            if (!ok) return ToolResult.Error(sb.ToString().TrimEnd());

            var inputs = ToolPage.TryFromSelection(spec, model, ToolPage.CurrentSelection(model), forTest: true) ?? new ToolInputs();
            var missing = spec.Inputs.Where(i => ToolSpec.Type(i) == "selection" && ToolSpec.IsRequired(i) && inputs.Selected(i.Id).Count == 0).Select(i => i.Label).ToList();
            if (missing.Count > 0) sb.AppendLine($"note: nothing selected for {string.Join(", ", missing)} - tested with those empty; select suitable entities and test again for a full check");

            // 2. Preview, through the same display code as the PropertyManager page.
            if (spec.Preview != null)
            {
                if (model.GetType() != (int)swDocumentTypes_e.swDocPART)
                {
                    sb.AppendLine("preview: not tested (previews are shown in part documents)");
                }
                else
                {
                    var (runner, _) = await _scripts.CompilePreviewAsync(spec.Preview);
                    var globals = new ScriptGlobals { swApp = ctx.Sw, Model = model, Inputs = inputs };
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        System.Collections.Generic.List<IBody2> shown;
                        using (CrashGuard.Enter("preview", spec.Name, spec.Title + " (test)", spec.Preview))
                        {
                            await runner(globals);
                            shown = PreviewDisplay.Show(model, globals.PreviewBodies);
                        }
                        var ms = watch.ElapsedMilliseconds;
                        await Task.Delay(1200);
                        PreviewDisplay.Hide(model, shown);
                        sb.AppendLine($"preview: {shown.Count} body(ies) in {ms} ms" + (ms > 350 ? " - TOO SLOW for live updates (budget 350 ms); simplify it" : " - fast enough for live updates"));
                        if (shown.Count == 0) sb.AppendLine("preview: drew nothing with these inputs - check that it calls Preview(body)");
                    }
                    catch (Exception ex)
                    {
                        var inner = ex is AggregateException agg ? agg.InnerException ?? ex : ex;
                        sb.AppendLine($"preview: FAILED - {inner.GetType().Name}: {inner.Message}");
                        ok = false;
                    }
                }
            }

            // 3. Macro, if asked.
            if (input.RunMacro == true && spec.Macro != null)
            {
                ToolResult result;
                using (CrashGuard.Enter("macro", spec.Name, spec.Title + " (test)", spec.Macro))
                    result = await _scripts.RunMacroAsync(new ToolContext(ctx.App, ctx.Doc, "test:" + spec.Name, ctx.Registry), spec.Title + " (test)", spec.Macro, inputs);
                _scripts.Reset("test:" + spec.Name);
                var text = string.Join("\n", result.Content.Where(c => (string)c["type"] == "text").Select(c => (string)c["text"]));
                sb.AppendLine("macro: " + (result.IsError ? "FAILED\n" : "") + text);
                ok &= !result.IsError || text.Contains("MacroInputException");
            }

            return ok ? ToolResult.Ok(sb.ToString().TrimEnd()) : ToolResult.Error(sb.ToString().TrimEnd());
        }
    }
}

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SwAgent.Tools;
using Xarial.XCad.SolidWorks;
using Xarial.XCad.SolidWorks.Documents;

namespace SwAgent.Scripting
{
    /// <summary>
    /// Members of this class are in scope in every execute_csharp cell, e.g. <c>Model.Extension.SelectByID2(...)</c>.
    /// One instance lives per chat thread, so variables declared in one cell are visible in the next.
    /// </summary>
    public sealed class ScriptGlobals
    {
        internal readonly StringBuilder Output = new StringBuilder();

        /// <summary>The SolidWorks application (same object a VBA macro gets).</summary>
        public ISldWorks swApp { get; internal set; }

        /// <summary>The document this chat belongs to.</summary>
        public IModelDoc2 Model { get; internal set; }

        /// <summary>xCAD wrapper of the application (higher-level API).</summary>
        public ISwApplication App { get; internal set; }

        /// <summary>xCAD wrapper of the chat's document.</summary>
        public ISwDocument Doc { get; internal set; }

        /// <summary>Plugin helpers: call any agent tool from code, unit conversion.</summary>
        public AgentApi Agent { get; internal set; }

        /// <summary>Signalled when the user presses Stop. Check it in long loops.</summary>
        public CancellationToken Ct { get; internal set; }

        /// <summary>A per-chat scratch folder for exports and temporary files.</summary>
        public string WorkDir { get; internal set; }

        /// <summary>Selection references from the user's messages ([S1] ...), resolved per call.</summary>
        internal System.Collections.Generic.IReadOnlyDictionary<string, string> Refs { get; set; }

        /// <summary>The entity the user had selected as [id] when they sent their message (IFace2, IEdge, IFeature, IComponent2, ...).</summary>
        public object Ref(string id) => SelectionTools.Resolve(Model, Refs, id);

        /// <summary>Select referenced entities (clears the selection first unless append is true).</summary>
        public void SelectRefs(params string[] ids) => SelectRefsWithMark(0, false, ids);

        /// <summary>Select referenced entities with a selection mark, optionally keeping the current selection.</summary>
        public void SelectRefsWithMark(int mark, bool append, params string[] ids)
        {
            if (!append) Model.ClearSelection2(true);
            foreach (var id in ids) SelectionTools.Select(Model, Ref(id), true, mark);
        }

        /// <summary>Isometric + zoom to fit, so the user sees the whole model and the work just done.</summary>
        public void ShowWork()
        {
            Model.ShowNamedView2("", (int)SolidWorks.Interop.swconst.swStandardViews_e.swIsometricView);
            Model.ViewZoomtofit2();
            Model.GraphicsRedraw2();
        }

        /// <summary>Zoom the view to specific entities (Ref ids like "S1", or IFeature/IFace2/IEdge/IComponent2 objects).</summary>
        public void Focus(params object[] targets)
        {
            Model.ClearSelection2(true);
            foreach (var t in targets) SelectionTools.Select(Model, t is string id ? Ref(id) : t, true, 0);
            Model.ViewZoomToSelection();
            Model.ClearSelection2(true);
            Model.GraphicsRedraw2();
        }

        /// <summary>Repaint the graphics area now (use inside long cells so the user sees progress).</summary>
        public void Refresh() => Model.GraphicsRedraw2();

        /// <summary>Write a line to the cell's output (Console.WriteLine works too).</summary>
        public void Print(object value) => Output.AppendLine(ValueFormatter.Format(value));

        /// <summary>A tool's inputs from its PropertyManager page (empty outside tool runs).</summary>
        public ToolInputs Inputs { get; internal set; } = ToolInputs.Empty;

        internal readonly System.Collections.Generic.List<(IBody2 Body, int Color, double Transparency)> PreviewBodies =
            new System.Collections.Generic.List<(IBody2, int, double)>();

        /// <summary>
        /// In a tool's preview code: show a temporary ("holographic") body - e.g. made with IModeler or IBody2.Copy2 and
        /// Operations2 - without adding anything to the model. Color as 0xRRGGBB. Ignored outside previews.
        /// </summary>
        public void Preview(IBody2 body, int color = 0x2F84D6, double transparency = 0.55)
        {
            if (body != null) PreviewBodies.Add((body, color, transparency));
        }

        /// <summary>
        /// Stop with a message for the user, e.g. Fail("Select a planar face first"). In a tool's macro this is shown in
        /// the status bar instead of handing the run to the agent.
        /// </summary>
        public void Fail(string message) => throw new MacroInputException(message);

        /// <summary>
        /// In a hard-coded or hybrid tool's macro: hand the job to the agent for a case the code doesn't cover (e.g. an unusual shape),
        /// with what you found so far. The agent finishes it at the tool's intelligence level.
        /// </summary>
        public void NeedsAgent(string why) => throw new AgentHandoffException(why);

        /// <summary>
        /// Reliable measuring helpers: Geo.EdgePoints(edge), Geo.LoopPoints(loop), Geo.Frame(planarFace) (u/v/n frame),
        /// Geo.LoopUV(loop, frame), Geo.Distance(point|loop, edge|edges|loop), Geo.Inside(uv, polygon), Geo.Box(uv).
        /// </summary>
        public Geometry Geo { get; } = new Geometry();

        /// <summary>
        /// Draw on the user's view (anchored to the model, follows the camera): Overlay.Line/Arrow/Polygon/Circle/Marker/
        /// Dimension/Label(points in meters...), Overlay.Body(tempBody) for exact proposed solids, Overlay.Clear().
        /// </summary>
        public OverlayApi Overlay => _overlay ?? (_overlay = new OverlayApi(this));

        private OverlayApi _overlay;

        /// <summary>Millimeters to meters (the API's length unit).</summary>
        public double mm(double value) => value / 1000.0;

        /// <summary>Inches to meters.</summary>
        public double inch(double value) => value * 0.0254;

        /// <summary>Degrees to radians (the API's angle unit).</summary>
        public double deg(double value) => value * Math.PI / 180.0;
    }

    /// <summary>The plugin's own functionality, callable from agent-written code.</summary>
    public sealed class AgentApi
    {
        private readonly Func<string, JObject, Task<ToolResult>> _invoke;

        internal AgentApi(Func<string, JObject, Task<ToolResult>> invoke)
        {
            _invoke = invoke;
        }

        /// <summary>
        /// Run any registered agent tool and get its text output, e.g.
        /// <c>var tree = await Agent.CallToolAsync("get_feature_tree");</c>
        /// </summary>
        public async Task<string> CallToolAsync(string name, object input = null)
        {
            var json = input == null ? new JObject() : input as JObject ?? JObject.FromObject(input);
            var result = await _invoke(name, json);
            var text = new StringBuilder();
            foreach (var block in result.Content)
            {
                if ((string)block["type"] == "text") text.AppendLine((string)block["text"]);
                else text.AppendLine("[image]");
            }
            if (result.IsError) throw new InvalidOperationException(text.ToString().Trim());
            return text.ToString().Trim();
        }
    }

    /// <summary>Turns cell return values into readable text, including COM objects and SolidWorks arrays.</summary>
    internal static class ValueFormatter
    {
        public static string Format(object value, int depth = 0)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string s:
                    return s;
                case double d:
                    return d.ToString("G10", System.Globalization.CultureInfo.InvariantCulture);
                case IFormattable f:
                    return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
                case Array arr:
                {
                    var items = new StringBuilder("[");
                    var i = 0;
                    foreach (var item in arr)
                    {
                        if (i > 0) items.Append(", ");
                        if (i++ >= 100)
                        {
                            items.Append($"... ({arr.Length} items)");
                            break;
                        }
                        items.Append(depth < 2 ? Format(item, depth + 1) : item?.ToString());
                    }
                    return items.Append("]").ToString();
                }
            }

            if (Marshal_IsComObject(value))
            {
                string name;
                try
                {
                    name = Microsoft.VisualBasic.Information.TypeName(value);
                }
                catch
                {
                    name = "COM object";
                }
                var extra = "";
                try
                {
                    if (value is IFeature f) extra = $" '{f.Name}' [{f.GetTypeName2()}]";
                    else if (value is IComponent2 c) extra = $" '{c.Name2}'";
                    else if (value is IModelDoc2 m) extra = $" '{m.GetTitle()}'";
                }
                catch
                {
                }
                return $"<{name}{extra}>";
            }

            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(value, Newtonsoft.Json.Formatting.Indented,
                    new Newtonsoft.Json.JsonSerializerSettings
                    {
                        ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
                        MaxDepth = 4,
                    });
                return json.Length > 20000 ? json.Substring(0, 20000) + "\n... (truncated)" : json;
            }
            catch
            {
                return value.ToString();
            }
        }

        private static bool Marshal_IsComObject(object o) => System.Runtime.InteropServices.Marshal.IsComObject(o);
    }

    /// <summary>Captures Console output written by a cell.</summary>
    internal sealed class CellWriter : StringWriter
    {
        private readonly StringBuilder _target;

        public CellWriter(StringBuilder target)
        {
            _target = target;
        }

        public override void Write(char value) => _target.Append(value);
        public override void Write(string value) => _target.Append(value);
        public override void WriteLine(string value) => _target.AppendLine(value);
    }

    /// <summary>A macro stopped because its input (usually the selection) is not what it needs; the message is for the user.</summary>
    public sealed class MacroInputException : Exception
    {
        public MacroInputException(string message) : base(message)
        {
        }
    }

    /// <summary>A hard-coded tool's macro handing a case it doesn't cover to the agent (NeedsAgent).</summary>
    public sealed class AgentHandoffException : Exception
    {
        public AgentHandoffException(string message) : base(message)
        {
        }
    }
}

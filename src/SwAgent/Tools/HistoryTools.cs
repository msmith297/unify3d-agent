using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.Tools
{
    /// <summary>
    /// Dumps the complete design intent of a part: every feature in order with its parents, every dimension
    /// (full name + value), extrusion end conditions, and the geometry of every sketch. This is what lets the
    /// agent "learn" a model and turn it into a reusable user tool.
    /// </summary>
    public sealed class HistoryTools
    {
        private const int MaxChars = 120000;

        private static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HistoryFolder", "SelectionSetFolder", "SensorFolder", "DocsFolder", "DetailCabinet", "CommentsFolder",
            "FavoriteFolder", "InkMarkupFolder", "EnvFolder", "MBD3DViewFolder", "LiveSectionFolder", "SurfaceBodyFolder",
            "SolidBodyFolder", "MaterialFolder", "EqnFolder",
        };

        public sealed class HistoryInput
        {
            [ToolParam("Include each sketch's segments with coordinates (default true).")]
            public bool? IncludeSketchGeometry { get; set; }

            [ToolParam("Only features whose name contains this text (default: all).")]
            public string Filter { get; set; }
        }

        [AgentTool("describe_model_history",
            "The full feature history of the chat's document as a build recipe: every feature in order with type, parents " +
            "(e.g. sketch plane), suppression, all dimensions by full name (D1@Sketch1) with values in mm/deg, extrusion end " +
            "conditions and depths, and each sketch's segments (lines, arcs, circles, points with coordinates in sketch space, mm), " +
            "construction geometry and relation counts, plus equations/global variables and material. Use it to understand " +
            "how a model was built, to rebuild or modify it precisely, or to turn it into a reusable tool.")]
        public ToolResult Describe(ToolContext ctx, HistoryInput input)
        {
            var model = ctx.RequireModel();
            var geometry = input.IncludeSketchGeometry ?? true;
            var sb = new StringBuilder();
            sb.AppendLine($"Document: {model.GetTitle()} ({(swDocumentTypes_e)model.GetType()})  - all lengths in mm, angles in degrees");

            Try(() =>
            {
                if (model is IPartDoc part)
                {
                    var mat = part.GetMaterialPropertyName2("", out var db);
                    if (!string.IsNullOrEmpty(mat)) sb.AppendLine($"Material: {mat} [{db}]");
                }
            });
            Try(() =>
            {
                var eq = model.GetEquationMgr();
                var n = eq?.GetCount() ?? 0;
                if (n > 0)
                {
                    sb.AppendLine("Equations / global variables:");
                    for (var i = 0; i < n; i++) sb.AppendLine($"  {eq.Equation[i]}  (= {Num(eq.Value[i])})");
                }
            });
            sb.AppendLine();

            var index = 0;
            var feat = (IFeature)model.FirstFeature();
            while (feat != null)
            {
                var type = feat.GetTypeName2();
                if (!Skip.Contains(type) && (string.IsNullOrEmpty(input.Filter) || feat.Name.IndexOf(input.Filter, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    index++;
                    AppendFeature(sb, feat, $"{index}.", "", geometry);
                    var sub = (IFeature)feat.GetFirstSubFeature();
                    while (sub != null)
                    {
                        AppendFeature(sb, sub, "-", "    ", geometry);
                        sub = (IFeature)sub.GetNextSubFeature();
                    }
                }
                if (sb.Length > MaxChars)
                {
                    sb.AppendLine($"... truncated at {MaxChars:N0} characters. Call again with filter or include_sketch_geometry=false.");
                    break;
                }
                feat = (IFeature)feat.GetNextFeature();
            }
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        private static void AppendFeature(StringBuilder sb, IFeature feat, string bullet, string indent, bool geometry)
        {
            var type = feat.GetTypeName2();
            var line = new StringBuilder($"{indent}{bullet} {feat.Name} [{type}]");
            Try(() =>
            {
                if (feat.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) is bool[] s && s.Length > 0 && s[0]) line.Append(" SUPPRESSED");
            });
            Try(() =>
            {
                if (feat.GetParents() is object[] parents && parents.Length > 0)
                {
                    line.Append(" <- ").Append(string.Join(", ", parents.OfType<IFeature>().Select(p => p.Name)));
                }
            });
            sb.AppendLine(line.ToString());
            var pad = indent + "     ";

            // Extrusions / cuts: direction, end condition, depth.
            Try(() =>
            {
                if (feat.GetDefinition() is IExtrudeFeatureData2 ex)
                {
                    var d1 = $"{(swEndConditions_e)ex.GetEndCondition(true)} {Mm(ex.GetDepth(true))} mm".Replace("swEndCond", "");
                    var text = $"{pad}extrude: dir1 {d1}";
                    if (ex.BothDirections) text += $"; dir2 {((swEndConditions_e)ex.GetEndCondition(false)).ToString().Replace("swEndCond", "")} {Mm(ex.GetDepth(false))} mm";
                    if (ex.ReverseDirection) text += "; reversed";
                    sb.AppendLine(text);
                }
            });

            // Every dimension that drives this feature, by full name.
            Try(() =>
            {
                var dims = new List<string>();
                var dd = feat.GetFirstDisplayDimension() as IDisplayDimension;
                while (dd != null && dims.Count < 200)
                {
                    var dim = dd.GetDimension2(0);
                    if (dim != null)
                    {
                        var angular = dim.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
                        var value = angular ? Num(dim.SystemValue * 180 / Math.PI) + " deg" : Mm(dim.SystemValue) + " mm";
                        var driven = dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven ? " (driven)" : "";
                        dims.Add($"{dim.FullName} = {value}{driven}");
                    }
                    dd = feat.GetNextDisplayDimension(dd) as IDisplayDimension;
                }
                if (dims.Count > 0) sb.AppendLine($"{pad}dims: {string.Join("; ", dims)}");
            });

            if (!geometry) return;
            Try(() =>
            {
                if (!(feat.GetSpecificFeature2() is ISketch sketch)) return;
                var segs = (sketch.GetSketchSegments() as object[])?.OfType<ISketchSegment>().ToList() ?? new List<ISketchSegment>();
                var points = (sketch.GetSketchPoints2() as object[])?.OfType<ISketchPoint>().ToList() ?? new List<ISketchPoint>();
                var relations = 0;
                Try(() => relations = sketch.RelationManager.GetRelationsCount((int)swSketchRelationFilterType_e.swAll));
                sb.AppendLine($"{pad}sketch{(sketch.Is3D() ? " (3D)" : "")}: {segs.Count} segments, {relations} relations");
                foreach (var seg in segs.Take(300))
                {
                    var c = seg.ConstructionGeometry ? " (construction)" : "";
                    switch (seg)
                    {
                        case ISketchLine l:
                            sb.AppendLine($"{pad}  line {Pt(l.GetStartPoint2())} -> {Pt(l.GetEndPoint2())}{c}");
                            break;
                        case ISketchArc a:
                        {
                            var start = (ISketchPoint)a.GetStartPoint2();
                            var end = (ISketchPoint)a.GetEndPoint2();
                            var full = Math.Abs(start.X - end.X) < 1e-9 && Math.Abs(start.Y - end.Y) < 1e-9;
                            sb.AppendLine(full
                                ? $"{pad}  circle center {Pt(a.GetCenterPoint2())} R{Mm(a.GetRadius())} (Ø{Mm(a.GetRadius() * 2)}){c}"
                                : $"{pad}  arc center {Pt(a.GetCenterPoint2())} R{Mm(a.GetRadius())} from {Pt(start)} to {Pt(end)}{c}");
                            break;
                        }
                        default:
                            sb.AppendLine($"{pad}  {((swSketchSegments_e)seg.GetType()).ToString().Replace("swSketch", "").ToLowerInvariant()} length {Mm(seg.GetLength())}{c}");
                            break;
                    }
                }
                if (segs.Count > 300) sb.AppendLine($"{pad}  ... {segs.Count - 300} more segments");
                if (segs.Count == 0 && points.Count > 0)
                {
                    sb.AppendLine($"{pad}  points: {string.Join(", ", points.Take(100).Select(p => Pt(p)))}");
                }
            });
        }

        private static string Pt(object p)
        {
            if (!(p is ISketchPoint sp)) return "(?)";
            return Math.Abs(sp.Z) > 1e-9 ? $"({Mm(sp.X)}, {Mm(sp.Y)}, {Mm(sp.Z)})" : $"({Mm(sp.X)}, {Mm(sp.Y)})";
        }

        private static string Mm(double meters) => ModelTools.Mm(meters);

        private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private static void Try(Action a)
        {
            try
            {
                a();
            }
            catch
            {
                // Best-effort: one unreadable property must not hide the rest of the history.
            }
        }
    }
}

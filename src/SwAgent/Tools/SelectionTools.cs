using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.Tools
{
    /// <summary>
    /// Turns the user's selection into stable, numbered references ([S1], [S2], ...) and describes the camera.
    ///
    /// When a message is sent, every selected entity is captured as a SolidWorks persistent reference
    /// (IModelDocExtension.GetPersistReference3). The UI stores those bytes with the chat and passes them back on
    /// every tool call, so Ref("S1") resolves to the same face even after the user has clicked elsewhere,
    /// rebuilt, or reopened the file.
    /// </summary>
    public sealed class SelectionTools
    {
        public sealed class SelectRefsInput
        {
            [ToolParam("Reference ids from the user's message, e.g. [\"S1\",\"S3\"].", Required = true)]
            public string[] Ids { get; set; }

            [ToolParam("Keep the existing selection and add to it (default false: clear first).")]
            public bool? Append { get; set; }

            [ToolParam("Selection mark to apply (default 0). Some features need marks, e.g. 1 for profile, 2 for path.")]
            public int? Mark { get; set; }
        }

        [AgentTool("select_refs",
            "Select entities the user referenced ([S1], [S2], ... in <sw_context>) so a following feature call can use them. " +
            "In execute_csharp the same is available as Ref(\"S1\") (returns the entity) and SelectRefs(...).")]
        public ToolResult SelectRefs(ToolContext ctx, SelectRefsInput input)
        {
            var model = ctx.RequireModel();
            if (input.Append != true) model.ClearSelection2(true);
            var report = new List<string>();
            foreach (var id in input.Ids)
            {
                try
                {
                    var ok = Select(model, Resolve(model, ctx.Refs, id), true, input.Mark ?? 0);
                    report.Add($"{id}: {(ok ? "selected" : "select call returned false")}");
                }
                catch (ToolException ex)
                {
                    report.Add($"{id}: {ex.Message}");
                }
            }
            model.GraphicsRedraw2();
            return ToolResult.Ok(string.Join("\n", report));
        }

        /// <summary>Snapshot of the current selection with persistent references, plus the camera.</summary>
        internal static JObject Snapshot(ISldWorks sw, IModelDoc2 model, int max = 40)
        {
            var items = new JArray();
            if (model?.SelectionManager is ISelectionMgr sm)
            {
                var count = sm.GetSelectedObjectCount2(-1);
                for (var i = 1; i <= count && i <= max; i++)
                {
                    try
                    {
                        var type = (swSelectType_e)sm.GetSelectedObjectType3(i, -1);
                        var obj = sm.GetSelectedObject6(i, -1);
                        var comp = sm.GetSelectedObjectsComponent4(i, -1) as IComponent2;
                        string persist = null;
                        try
                        {
                            if (model.Extension.GetPersistReference3(obj) is byte[] bytes) persist = Convert.ToBase64String(bytes);
                        }
                        catch
                        {
                            // Some selections (e.g. temporary or sketch-text entities) have no persistent id.
                        }
                        items.Add(new JObject
                        {
                            ["type"] = Pretty(type),
                            ["label"] = Owner(obj, comp),
                            ["detail"] = Describe(obj, comp),
                            ["ref"] = persist,
                        });
                    }
                    catch (Exception ex)
                    {
                        items.Add(new JObject { ["type"] = "Entity", ["label"] = "", ["detail"] = "(could not read: " + ex.Message + ")" });
                    }
                }
            }
            return new JObject { ["items"] = items, ["view"] = Camera(sw, model) };
        }

        /// <summary>Look up a reference id ("S1") from the chat's stored references.</summary>
        internal static object Resolve(IModelDoc2 model, IReadOnlyDictionary<string, string> refs, string id)
        {
            id = (id ?? "").Trim().Trim('[', ']');
            if (refs == null || !refs.TryGetValue(id, out var b64) || string.IsNullOrEmpty(b64))
            {
                throw new ToolException($"Unknown reference '{id}'. Valid ids are listed in <sw_context> of the user's messages.");
            }
            var obj = model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(b64), out var err);
            if (obj == null || err != (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok)
            {
                throw new ToolException($"Reference '{id}' no longer resolves ({(swPersistReferencedObjectStates_e)err}) - the entity was probably deleted or consumed by a feature.");
            }
            return obj;
        }

        internal static bool Select(IModelDoc2 model, object obj, bool append, int mark)
        {
            var data = ((ISelectionMgr)model.SelectionManager).CreateSelectData();
            data.Mark = mark;
            switch (obj)
            {
                case IFeature f: return f.Select2(append, mark);
                case IComponent2 c: return c.Select4(append, data, false);
                case IEntity e: return e.Select4(append, data);
                default: throw new ToolException("This kind of entity can't be selected programmatically.");
            }
        }

        /// <summary>Which way the user is looking, in model coordinates, plus a plain-language name.</summary>
        internal static JObject Camera(ISldWorks sw, IModelDoc2 model)
        {
            try
            {
                if (!(model?.ActiveView is IModelView view) || model.GetType() == (int)swDocumentTypes_e.swDocDRAWING) return null;
                var mu = (IMathUtility)sw.GetMathUtility();
                var inv = (MathTransform)view.Orientation3.Inverse();
                double[] Axis(double x, double y, double z) =>
                    (double[])((IMathVector)((IMathVector)mu.CreateVector(new[] { x, y, z })).MultiplyTransform(inv)).ArrayData;
                var right = Axis(1, 0, 0);
                var up = Axis(0, 1, 0);
                var toward = Axis(0, 0, 1);
                return new JObject
                {
                    ["screenRight"] = new JArray(right.Select(Round)),
                    ["screenUp"] = new JArray(up.Select(Round)),
                    ["towardViewer"] = new JArray(toward.Select(Round)),
                    ["text"] = $"Viewing from {DirectionName(toward)}; screen right = {Vec(right)}, screen up = {Vec(up)} (model coordinates).",
                };
            }
            catch
            {
                return null;
            }
        }

        private static string DirectionName(double[] t)
        {
            var named = new (double[] V, string Name)[]
            {
                (new[] { 0.0, 0, 1 }, "the Front (+Z)"), (new[] { 0.0, 0, -1 }, "the Back (-Z)"),
                (new[] { 0.0, 1, 0 }, "the Top (+Y)"), (new[] { 0.0, -1, 0 }, "the Bottom (-Y)"),
                (new[] { 1.0, 0, 0 }, "the Right (+X)"), (new[] { -1.0, 0, 0 }, "the Left (-X)"),
            };
            foreach (var (v, name) in named)
            {
                if (v[0] * t[0] + v[1] * t[1] + v[2] * t[2] > 0.985) return name;
            }
            var parts = new StringBuilder();
            string[] axes = { "X", "Y", "Z" };
            for (var i = 0; i < 3; i++)
            {
                if (Math.Abs(t[i]) > 0.3) parts.Append(t[i] > 0 ? "+" : "-").Append(axes[i]);
            }
            return $"{parts} (direction {Vec(t)})";
        }

        private static string Owner(object obj, IComponent2 comp)
        {
            string feature = null;
            try
            {
                switch (obj)
                {
                    case IFeature f: feature = f.Name; break;
                    case IFace2 face: feature = (face.GetFeature() as IFeature)?.Name; break;
                    case IEdge edge:
                        feature = ((edge.GetTwoAdjacentFaces2() as object[])?.OfType<IFace2>().FirstOrDefault()?.GetFeature() as IFeature)?.Name;
                        break;
                    case IComponent2 c: return c.Name2;
                }
            }
            catch
            {
            }
            if (comp != null) return feature == null ? comp.Name2 : $"{feature} in {comp.Name2}";
            return feature ?? "";
        }

        private static string Describe(object obj, IComponent2 comp)
        {
            var sb = new StringBuilder();
            try
            {
                switch (obj)
                {
                    case IFace2 face:
                    {
                        var surf = (ISurface)face.GetSurface();
                        if (surf.IsPlane())
                        {
                            sb.Append("planar");
                            if (face.Normal is double[] n) sb.Append($", normal {Vec(n)}");
                        }
                        else if (surf.IsCylinder() && surf.CylinderParams is double[] cp)
                        {
                            sb.Append($"cylindrical, R{Mm(cp[6])} mm (Ø{Mm(cp[6] * 2)}), axis {Vec(new[] { cp[3], cp[4], cp[5] })}");
                        }
                        else if (surf.IsCone()) sb.Append("conical");
                        else if (surf.IsSphere()) sb.Append("spherical");
                        else if (surf.IsTorus()) sb.Append("toroidal (fillet-like)");
                        else sb.Append("freeform");
                        sb.Append($", area {face.GetArea() * 1e6:0.##} mm²");
                        if (face.GetBox() is double[] box && box.Length == 6)
                        {
                            sb.Append($", spans ({Mm(box[0])}, {Mm(box[1])}, {Mm(box[2])}) to ({Mm(box[3])}, {Mm(box[4])}, {Mm(box[5])}) mm");
                        }
                        break;
                    }
                    case IEdge edge:
                    {
                        var curve = (ICurve)edge.GetCurve();
                        var p = edge.GetCurveParams3();
                        if (curve.IsLine() && curve.LineParams is double[] lp)
                        {
                            sb.Append($"straight, direction {Vec(new[] { lp[3], lp[4], lp[5] })}");
                        }
                        else if (curve.IsCircle() && curve.CircleParams is double[] c)
                        {
                            sb.Append($"circular, R{Mm(c[6])} mm, centre ({Mm(c[0])}, {Mm(c[1])}, {Mm(c[2])}) mm, axis {Vec(new[] { c[3], c[4], c[5] })}");
                        }
                        else sb.Append("curved");
                        if (p != null)
                        {
                            sb.Append($", length {Mm(curve.GetLength3(p.UMinValue, p.UMaxValue))} mm");
                            sb.Append($", from ({Mm(p.StartPoint is double[] s ? s[0] : 0)}, {Mm(p.StartPoint is double[] s1 ? s1[1] : 0)}, {Mm(p.StartPoint is double[] s2 ? s2[2] : 0)})");
                            sb.Append($" to ({Mm(p.EndPoint is double[] e ? e[0] : 0)}, {Mm(p.EndPoint is double[] e1 ? e1[1] : 0)}, {Mm(p.EndPoint is double[] e2 ? e2[2] : 0)}) mm");
                        }
                        break;
                    }
                    case IVertex vertex:
                        if (vertex.GetPoint() is double[] pt) sb.Append($"point ({Mm(pt[0])}, {Mm(pt[1])}, {Mm(pt[2])}) mm");
                        break;
                    case IFeature feat:
                        sb.Append($"feature '{feat.Name}' [{feat.GetTypeName2()}]");
                        break;
                    case IComponent2 c:
                        sb.Append($"component '{c.Name2}' -> {c.GetPathName()}");
                        break;
                    case ISketchSegment seg:
                        sb.Append($"sketch {((swSketchSegments_e)seg.GetType()).ToString().Replace("swSketch", "").ToLowerInvariant()}, length {Mm(seg.GetLength())} mm");
                        break;
                }
            }
            catch (Exception ex)
            {
                sb.Append("(" + ex.Message + ")");
            }
            if (comp != null) sb.Append($"; in component '{comp.Name2}' (coordinates are assembly coordinates)");
            return sb.ToString();
        }

        internal static string Pretty(swSelectType_e type)
        {
            var name = type.ToString().Replace("swSel", "").ToLowerInvariant();
            switch (name)
            {
                case "faces": return "Face";
                case "edges": return "Edge";
                case "vertices": return "Vertex";
                case "bodyfeatures": return "Feature";
                case "components": return "Component";
                case "datumplanes": return "Plane";
                case "datumaxes": return "Axis";
                case "sketches": return "Sketch";
                case "sketchsegs": return "Sketch segment";
                case "sketchpoints": return "Sketch point";
                case "dimensions": return "Dimension";
                default: return char.ToUpperInvariant(name[0]) + name.Substring(1);
            }
        }

        private static double Round(double v) => Math.Round(v, 3);

        private static string Vec(double[] v) =>
            "(" + string.Join(", ", v.Take(3).Select(x => Math.Round(x, 3).ToString("0.###", CultureInfo.InvariantCulture))) + ")";

        private static string Mm(double meters) => ModelTools.Mm(meters);
    }
}

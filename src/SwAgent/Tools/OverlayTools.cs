using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SwAgent.UI;

namespace SwAgent.Tools
{
    /// <summary>show_overlay / clear_overlay: the agent sketches on the user's 3D view (see SceneOverlay).</summary>
    public sealed class OverlayTools
    {
        public sealed class ItemInput
        {
            [ToolParam("line | arrow | polygon | circle | marker | dimension | label. line/arrow: a path through the points (arrow head at the last). " +
                       "polygon: closed outline (fill to shade it). circle: a true 3D circle - points[0] centre, normal, radius. marker: a ring of " +
                       "fixed screen size around points[0]. dimension: two points with arrows both ways and the text between. label: text tied to " +
                       "points[0] with a leader (points[1], optional, is where the tag sits).", Required = true)]
            public string Type { get; set; }

            [ToolParam("Model coordinates [x, y, z] in the request's units - measured from the model (your code or the selection data), not guessed.")]
            public double[][] Points { get; set; }

            [ToolParam("Instead of points: anchor on a selection reference such as \"S1\" (its face, edge or vertex).")]
            public string Ref { get; set; }

            [ToolParam("Text for label and dimension items. Keep it short (a few words or a value).")]
            public string Text { get; set; }

            [ToolParam("Colour: #RRGGBB or orange (default), red, green, blue, yellow, cyan, magenta, white.")]
            public string Color { get; set; }

            [ToolParam("Dashed line (e.g. a proposal or a hidden path).")]
            public bool? Dashed { get; set; }

            [ToolParam("polygon / circle: shade the inside.")]
            public bool? Fill { get; set; }

            [ToolParam("circle: radius in the request's units. marker: radius in screen pixels (default 14).")]
            public double? Radius { get; set; }

            [ToolParam("circle: the normal of the circle's plane [x, y, z] (e.g. the face normal). Default +Z.")]
            public double[] Normal { get; set; }
        }

        public sealed class ShowInput
        {
            [ToolParam("What to draw.", Required = true)]
            public ItemInput[] Items { get; set; }

            [ToolParam("Units of the coordinates and circle radii: mm (default), in or m.")]
            public string Units { get; set; }

            [ToolParam("Add to what's already shown instead of replacing it. Default false.")]
            public bool? Append { get; set; }
        }

        public sealed class ClearInput
        {
        }

        [AgentTool("show_overlay",
            "Draw on the user's 3D view to show what you mean: outlines of proposed geometry, arrows to features, circles around holes, " +
            "dimensions and labels, all anchored to model coordinates - they stay on the geometry while the user rotates, pans and zooms. " +
            "Nothing is added to the model. Shown until you call clear_overlay or the user sends their next message.")]
        public ToolResult Show(ToolContext ctx, ShowInput input)
        {
            var model = ctx.RequireModel();
            if (input.Items == null || input.Items.Length == 0) return ToolResult.Error("No items to draw.");
            var scale = Scale(input.Units);
            var items = new List<SceneOverlay.Item>();
            var problems = new List<string>();
            foreach (var (it, i) in input.Items.Select((x, i) => (x, i)))
            {
                var type = (it.Type ?? "").Trim().ToLowerInvariant();
                var points = (it.Points ?? new double[0][]).Where(p => p != null && p.Length >= 3).Select(p => new[] { p[0] * scale, p[1] * scale, p[2] * scale }).ToList();
                if (!string.IsNullOrWhiteSpace(it.Ref))
                {
                    var anchor = AnchorOf(model, ctx, it.Ref.Trim());
                    if (anchor == null) problems.Add($"item {i + 1}: couldn't resolve {it.Ref}");
                    else points.Insert(0, anchor);
                }
                var needed = type == "line" || type == "arrow" || type == "dimension" ? 2 : type == "polygon" ? 3 : 1;
                if (points.Count < needed)
                {
                    problems.Add($"item {i + 1} ({type}): needs at least {needed} point(s)");
                    continue;
                }
                items.Add(new SceneOverlay.Item
                {
                    Type = type,
                    Points = points,
                    Text = it.Text,
                    Color = ParseColor(it.Color),
                    Dashed = it.Dashed == true,
                    Fill = it.Fill == true,
                    Radius = type == "marker" ? it.Radius ?? 14 : (it.Radius ?? 0) * scale,
                    Normal = it.Normal,
                });
            }
            if (items.Count == 0) return ToolResult.Error("Nothing could be drawn: " + string.Join("; ", problems));
            SceneOverlay.ShowItems(ctx.Sw, model, items, input.Append == true);
            return ToolResult.Ok($"Showing {items.Count} item(s) on the user's view (anchored to the model; they follow the camera)." +
                                 (problems.Count > 0 ? " Skipped: " + string.Join("; ", problems) : "") +
                                 " They stay until clear_overlay or the user's next message.");
        }

        [AgentTool("clear_overlay", "Remove everything you drew on the user's view (show_overlay, Overlay.* in code).")]
        public ToolResult Clear(ToolContext ctx, ClearInput input)
        {
            SceneOverlay.ClearAll();
            return ToolResult.Ok("Overlay cleared.");
        }

        internal static double Scale(string units)
        {
            switch ((units ?? "mm").Trim().ToLowerInvariant())
            {
                case "m":
                    return 1;
                case "in":
                case "inch":
                case "inches":
                    return 0.0254;
                default:
                    return 0.001;
            }
        }

        internal static Color ParseColor(string c)
        {
            if (string.IsNullOrWhiteSpace(c)) return SceneOverlay.DefaultColor;
            c = c.Trim();
            if (c.StartsWith("#") && c.Length == 7 && int.TryParse(c.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            switch (c.ToLowerInvariant())
            {
                case "red": return Color.FromArgb(0xE5, 0x39, 0x35);
                case "green": return Color.FromArgb(0x43, 0xC0, 0x5A);
                case "blue": return Color.FromArgb(0x3D, 0x9C, 0xF5);
                case "yellow": return Color.FromArgb(0xFF, 0xD3, 0x3D);
                case "cyan": return Color.FromArgb(0x26, 0xD0, 0xE0);
                case "magenta": return Color.FromArgb(0xE0, 0x4C, 0xD6);
                case "white": return Color.White;
                default: return SceneOverlay.DefaultColor;
            }
        }

        /// <summary>A point on a referenced face, edge or vertex: the point of it nearest its bounding box centre.</summary>
        private static double[] AnchorOf(IModelDoc2 model, ToolContext ctx, string id)
        {
            try
            {
                var entity = SelectionTools.Resolve(model, ctx.Refs, id);
                switch (entity)
                {
                    case IVertex v:
                        return (double[])v.GetPoint();
                    case IEdge e:
                    {
                        var p = (double[])e.GetCurveParams2();
                        var q = (double[])e.GetClosestPointOn((p[0] + p[3]) / 2, (p[1] + p[4]) / 2, (p[2] + p[5]) / 2);
                        return q == null ? null : new[] { q[0], q[1], q[2] };
                    }
                    case IFace2 f:
                    {
                        var b = (double[])f.GetBox();
                        var q = (double[])f.GetClosestPointOn((b[0] + b[3]) / 2, (b[1] + b[4]) / 2, (b[2] + b[5]) / 2);
                        return q == null ? null : new[] { q[0], q[1], q[2] };
                    }
                }
            }
            catch
            {
                // Unresolvable reference: reported by the caller.
            }
            return null;
        }
    }
}

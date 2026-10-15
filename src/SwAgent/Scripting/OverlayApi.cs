using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SwAgent.Tools;
using SwAgent.UI;

namespace SwAgent.Scripting
{
    /// <summary>
    /// Overlay.* in agent code: draw on the user's view from exact computed geometry (meters, model coordinates) - the
    /// same layer as the show_overlay tool. Each call adds to what's shown; call Overlay.Clear() first to start over.
    /// Colours: "#RRGGBB" or orange (default), red, green, blue, yellow, cyan, magenta, white.
    /// </summary>
    public sealed class OverlayApi
    {
        private readonly ScriptGlobals _g;

        internal OverlayApi(ScriptGlobals g)
        {
            _g = g;
        }

        public void Line(params double[][] points) => Add("line", points);
        public void Line(string color, params double[][] points) => Add("line", points, color: color);
        public void Arrow(double[] from, double[] to, string color = null) => Add("arrow", new[] { from, to }, color: color);
        public void Polygon(IEnumerable<double[]> points, string color = null, bool fill = true, bool dashed = false) =>
            Add("polygon", points.ToArray(), color: color, fill: fill, dashed: dashed);
        public void Circle(double[] center, double[] normal, double radius, string color = null, bool fill = false) =>
            Add("circle", new[] { center }, color: color, fill: fill, radius: radius, normal: normal);
        public void Marker(double[] point, string color = null) => Add("marker", new[] { point }, color: color, radius: 14);
        public void Dimension(double[] a, double[] b, string text, string color = null) => Add("dimension", new[] { a, b }, text, color);
        public void Label(double[] point, string text, double[] tagAt = null, string color = null) =>
            Add("label", tagAt == null ? new[] { point } : new[] { point, tagAt }, text, color);

        /// <summary>A temporary body (IModeler.CreateBodyFrom..., IBody2.Copy2 + Operations2) shown in the scene - e.g. the exact proposed cut. Parts only.</summary>
        public void Body(IBody2 body, int rgb = 0xFF8C1A) => SceneOverlay.ShowBody(_g.Model, body, rgb);

        public void Clear() => SceneOverlay.ClearAll();

        private void Add(string type, double[][] points, string text = null, string color = null, bool fill = false, bool dashed = false,
            double radius = 0, double[] normal = null)
        {
            SceneOverlay.ShowItems(_g.swApp, _g.Model, new[]
            {
                new SceneOverlay.Item
                {
                    Type = type,
                    Points = points.Where(p => p != null && p.Length >= 3).Select(p => new[] { p[0], p[1], p[2] }).ToList(),
                    Text = text,
                    Color = OverlayTools.ParseColor(color),
                    Fill = fill,
                    Dashed = dashed,
                    Radius = radius,
                    Normal = normal,
                },
            }, append: true);
        }
    }
}

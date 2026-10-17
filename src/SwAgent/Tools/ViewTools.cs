using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SwAgent.ModelFiles;
using SolidWorks.Interop.swconst;

namespace SwAgent.Tools
{
    /// <summary>Screenshots of the SolidWorks graphics area so the agent can check its work visually.</summary>
    public sealed class ViewTools
    {
        public enum ViewName
        {
            current,
            isometric,
            trimetric,
            dimetric,
            front,
            back,
            left,
            right,
            top,
            bottom,
        }

        public sealed class CaptureInput
        {
            [ToolParam("Views to capture. 'current' keeps the user's view. Several views are tiled into one labelled image. " +
                       "Default: [\"current\"]. A good overview is [\"isometric\",\"front\",\"top\",\"right\"].")]
            public ViewName[] Views { get; set; }

            [ToolParam("Longest side of the returned image in pixels (default 1280, max 1600).")]
            public int? MaxSize { get; set; }

            [ToolParam("Zoom to fit before each named view (default true). 'current' is never re-zoomed.")]
            public bool? ZoomToFit { get; set; }

            [ToolParam("Only for what the off-screen renderer can't show - 3D sketches, sketch geometry that is off screen, annotations, " +
                       "dimensions, RealView appearances in other orientations: true re-orients the user's real view for the named views " +
                       "and restores it afterwards (the user sees it move). Default false: named views are rendered off-screen.")]
            public bool? MoveCamera { get; set; }

            [ToolParam("Entities to highlight in the rendered views: reference ids like \"S1\" (faces, edges, features, bodies, components). " +
                       "Default: whatever is selected now.")]
            public string[] Highlight { get; set; }

            [ToolParam("Frame the rendered views on these references (close-ups of a face, feature or component), e.g. [\"S2\"].")]
            public string[] Focus { get; set; }

            [ToolParam("Assemblies: component names to leave out of the rendered views (e.g. the lid, to see into the cavity).")]
            public string[] Hide { get; set; }

            [ToolParam("A custom viewing direction for one more rendered view: [x, y, z] from the model towards the eye, e.g. [1, -0.5, 1].")]
            public double[] Direction { get; set; }
        }

        [AgentTool("capture_views",
            "Look at the chat's document. 'current' is a real screenshot of exactly what the user sees (true colours and " +
            "appearances - the ground truth). Every other view is rendered off-screen from the live model's geometry, so the " +
            "user's camera never moves: any standard or custom direction, with the selection or [S1] references highlighted, " +
            "close-ups (focus) and components hidden. Use it freely to verify geometry after changes. Only set move_camera for " +
            "the rare things the renderer doesn't show (3D sketches, off-screen sketch geometry, annotations).")]
        public async Task<ToolResult> CaptureViews(ToolContext ctx, CaptureInput input)
        {
            var model = ctx.RequireModel();
            var views = (input.Views == null || input.Views.Length == 0) ? new[] { ViewName.current } : input.Views.Distinct().ToArray();
            var maxSize = Math.Min(1600, Math.Max(320, input.MaxSize ?? 1280));
            var zoom = input.ZoomToFit ?? true;
            var isDrawing = model.GetType() == (int)swDocumentTypes_e.swDocDRAWING;
            if (isDrawing && views.Any(v => v != ViewName.current))
            {
                views = new[] { ViewName.current }; // drawings have no 3D orientations
            }
            var wantsScreen = input.MoveCamera == true || views.Contains(ViewName.current);
            if (wantsScreen && !ReferenceEquals(ctx.Sw.ActiveDoc, model))
            {
                throw new ToolException($"'{model.GetTitle()}' is not the active window. Activate it first " +
                                        "(swApp.ActivateDoc3) - screenshots need the document on screen. (Off-screen views work without it.)");
            }

            List<(string Name, Bitmap Image)> shots;
            string renderNote = null;
            if (input.MoveCamera == true || isDrawing)
            {
                shots = CaptureMany(model, views, zoom);
            }
            else
            {
                shots = views.Contains(ViewName.current) ? CaptureMany(model, new[] { ViewName.current }, false) : new List<(string, Bitmap)>();
                var specs = views.Where(v => v != ViewName.current).Select(Spec).ToList();
                if (input.Direction?.Length == 3) specs.Add(new MeshRenderer.ViewSpec { Name = "custom", Dir = input.Direction, Up = Math.Abs(input.Direction[1]) > 0.95 * Norm(input.Direction) ? new[] { 0.0, 0, -1 } : new[] { 0.0, 1, 0 } });
                if (specs.Count > 0)
                {
                    try
                    {
                        var (rendered, note) = await RenderOffscreen(ctx, model, input, specs, shots.Count + specs.Count == 1 ? maxSize : 560);
                        shots.AddRange(rendered);
                        renderNote = note;
                    }
                    catch
                    {
                        foreach (var sh in shots) sh.Image.Dispose();
                        throw;
                    }
                }
            }
            try
            {
                using (var composed = shots.Count == 1 ? Fit(shots[0].Image, maxSize) : Tile(shots, maxSize))
                {
                    var b64 = Convert.ToBase64String(EncodeJpeg(composed, 85));
                    var note = $"Captured {string.Join(", ", shots.Select(s => s.Name))} ({composed.Width}x{composed.Height}).";
                    if (renderNote != null) note += " " + renderNote;
                    var cam = SelectionTools.Camera(ctx.Sw, model);
                    if (cam != null) note += " Current camera: " + (string)cam["text"];
                    if (isDrawing && input.Views != null && input.Views.Any(v => v != ViewName.current))
                    {
                        note += " Drawings only support the current view.";
                    }
                    return new ToolResult().Text(note).Image(b64);
                }
            }
            finally
            {
                foreach (var s in shots) s.Image.Dispose();
            }
        }

        /// <summary>Capture each requested view and put the user's view back exactly as it was.</summary>
        internal static List<(string Name, Bitmap Image)> CaptureMany(IModelDoc2 model, IEnumerable<ViewName> views, bool zoom)
        {
            var view = (IModelView)model.ActiveView ?? throw new ToolException("The document has no active view.");
            var hwnd = new IntPtr(view.GetViewHWndx64());

            var savedOrientation = view.Orientation3;
            var savedScale = view.Scale2;
            var savedTranslation = view.Translation3;
            var result = new List<(string, Bitmap)>();
            try
            {
                foreach (var v in views)
                {
                    if (v != ViewName.current)
                    {
                        model.ShowNamedView2("", (int)ToStandardView(v));
                        if (zoom) model.ViewZoomtofit2();
                    }
                    model.GraphicsRedraw2();
                    Thread.Sleep(60);
                    result.Add((v.ToString(), CaptureWindow(hwnd)));
                }
            }
            catch
            {
                foreach (var r in result) r.Item2.Dispose();
                throw;
            }
            finally
            {
                if (views.Any(v => v != ViewName.current))
                {
                    // Order matters: setting Scale2 re-centres the view, so the pan (Translation3) must come last.
                    // (Measured on SolidWorks 2026: orientation -> translation -> scale leaves the model off-centre.)
                    view.Orientation3 = savedOrientation;
                    view.Scale2 = savedScale;
                    view.Translation3 = savedTranslation;
                    model.GraphicsRedraw2();
                }
            }
            return result;
        }

        /// <summary>
        /// Render views of the live model without touching the user's view: read the geometry on this (UI) thread,
        /// draw on a background thread.
        /// </summary>
        private static async Task<(List<(string Name, Bitmap Image)> Shots, string Note)> RenderOffscreen(
            ToolContext ctx, IModelDoc2 model, CaptureInput input, List<MeshRenderer.ViewSpec> specs, int tileWidth)
        {
            var live = LiveModelMesh.For(ctx.Sw, model, input.Hide);
            if (live.Mesh.TriangleCount == 0) throw new ToolException("There's no visible solid or surface body to render.");

            var highlight = input.Highlight != null ? Resolve(ctx, model, input.Highlight) : CurrentSelection(model);
            var opts = new MeshRenderer.RenderOptions
            {
                TriColor = live.TriColor,
                Highlight = live.Mark(ctx.Sw, highlight),
                Focus = input.Focus != null ? live.Mark(ctx.Sw, Resolve(ctx, model, input.Focus)) : null,
            };
            var tileHeight = (int)(tileWidth * 0.72);
            var shots = new (string Name, Bitmap Image)[specs.Count];
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() => Parallel.For(0, specs.Count, i =>
                shots[i] = (specs[i].Name + " (rendered)", MeshRenderer.RenderView(live.Mesh, live.TriFace, specs[i], 'Y', tileWidth, tileHeight, default, opts))));
            var note = $"Named views were rendered off-screen from the model's geometry ({live.Mesh.TriangleCount} triangles, {watch.ElapsedMilliseconds} ms) - " +
                       "the user's view didn't move. Colours are the model's face/body/part colours; textures, RealView, sketches and annotations aren't drawn" +
                       (opts.Highlight != null ? "; highlighted faces are blue" : "") + ".";
            return (shots.ToList(), note);
        }

        private static List<object> Resolve(ToolContext ctx, IModelDoc2 model, IEnumerable<string> ids)
        {
            var list = new List<object>();
            foreach (var id in ids)
            {
                try
                {
                    list.Add(SelectionTools.Resolve(model, ctx.Refs, id));
                }
                catch (ToolException)
                {
                    // An id that no longer resolves just isn't highlighted.
                }
            }
            return list;
        }

        private static List<object> CurrentSelection(IModelDoc2 model)
        {
            var list = new List<object>();
            if (!(model.SelectionManager is ISelectionMgr sm)) return list;
            for (var i = 1; i <= sm.GetSelectedObjectCount2(-1); i++)
            {
                try
                {
                    var o = sm.GetSelectedObject6(i, -1);
                    if (o != null) list.Add(o);
                }
                catch
                {
                    // Skip unreadable selections.
                }
            }
            return list;
        }

        /// <summary>Standard views in SolidWorks' convention (Y up; front looks along -Z).</summary>
        private static MeshRenderer.ViewSpec Spec(ViewName v)
        {
            double[] up = { 0, 1, 0 };
            double[] dir;
            switch (v)
            {
                case ViewName.front: dir = new[] { 0.0, 0, 1 }; break;
                case ViewName.back: dir = new[] { 0.0, 0, -1 }; break;
                case ViewName.left: dir = new[] { -1.0, 0, 0 }; break;
                case ViewName.right: dir = new[] { 1.0, 0, 0 }; break;
                case ViewName.top: dir = new[] { 0.0, 1, 0 }; up = new[] { 0.0, 0, -1 }; break;
                case ViewName.bottom: dir = new[] { 0.0, -1, 0 }; up = new[] { 0.0, 0, 1 }; break;
                case ViewName.dimetric: dir = new[] { 1.0, 0.75, 1.9 }; break;
                case ViewName.trimetric: dir = new[] { 1.3, 0.9, 1.9 }; break;
                default: dir = new[] { 1.0, 1, 1 }; break;
            }
            return new MeshRenderer.ViewSpec { Name = v.ToString(), Dir = dir, Up = up };
        }

        private static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

        private static swStandardViews_e ToStandardView(ViewName v)
        {
            switch (v)
            {
                case ViewName.isometric: return swStandardViews_e.swIsometricView;
                case ViewName.trimetric: return swStandardViews_e.swTrimetricView;
                case ViewName.dimetric: return swStandardViews_e.swDimetricView;
                case ViewName.front: return swStandardViews_e.swFrontView;
                case ViewName.back: return swStandardViews_e.swBackView;
                case ViewName.left: return swStandardViews_e.swLeftView;
                case ViewName.right: return swStandardViews_e.swRightView;
                case ViewName.top: return swStandardViews_e.swTopView;
                case ViewName.bottom: return swStandardViews_e.swBottomView;
                default: throw new ArgumentOutOfRangeException(nameof(v));
            }
        }

        /// <summary>PrintWindow first (works when partly covered); fall back to a screen copy if the GPU surface comes back blank.</summary>
        internal static Bitmap CaptureWindow(IntPtr hwnd)
        {
            if (!GetClientRect(hwnd, out var rc) || rc.Right - rc.Left < 8 || rc.Bottom - rc.Top < 8)
            {
                throw new ToolException("The graphics area is minimized or too small to capture.");
            }
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try
                {
                    PrintWindow(hwnd, hdc, PwClientOnly | PwRenderFullContent);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            if (!IsBlank(bmp)) return bmp;

            bmp.Dispose();
            var origin = new Point(0, 0);
            ClientToScreen(hwnd, ref origin);
            var screen = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(screen))
            {
                g.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(w, h));
            }
            return screen;
        }

        private static bool IsBlank(Bitmap bmp)
        {
            var first = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
            for (var y = 1; y < 8; y++)
            for (var x = 1; x < 8; x++)
            {
                if (bmp.GetPixel(bmp.Width * x / 8, bmp.Height * y / 8) != first) return false;
            }
            return true;
        }

        internal static Bitmap Fit(Bitmap src, int maxSize)
        {
            var scale = Math.Min(1.0, (double)maxSize / Math.Max(src.Width, src.Height));
            var dst = new Bitmap(Math.Max(1, (int)(src.Width * scale)), Math.Max(1, (int)(src.Height * scale)));
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, dst.Width, dst.Height);
            }
            return dst;
        }

        /// <summary>Lay views out in a grid with a label in each tile's corner.</summary>
        private static Bitmap Tile(List<(string Name, Bitmap Image)> shots, int maxSize)
        {
            var cols = shots.Count <= 4 ? 2 : 3;
            var rows = (int)Math.Ceiling(shots.Count / (double)cols);
            var tileW = maxSize / cols;
            var aspect = shots[0].Image.Height / (double)shots[0].Image.Width;
            var tileH = (int)(tileW * aspect);
            var canvas = new Bitmap(tileW * cols, tileH * rows);
            using (var g = Graphics.FromImage(canvas))
            using (var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var labelBg = new SolidBrush(Color.FromArgb(200, 32, 38, 46)))
            using (var border = new Pen(Color.FromArgb(160, 160, 160)))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                for (var i = 0; i < shots.Count; i++)
                {
                    var x = i % cols * tileW;
                    var y = i / cols * tileH;
                    g.DrawImage(shots[i].Image, x, y, tileW, tileH);
                    g.DrawRectangle(border, x, y, tileW - 1, tileH - 1);
                    var label = shots[i].Name.ToUpperInvariant();
                    var size = g.MeasureString(label, font);
                    g.FillRectangle(labelBg, x + 6, y + 6, size.Width + 8, size.Height + 2);
                    g.DrawString(label, font, Brushes.White, x + 10, y + 7);
                }
            }
            return canvas;
        }

        internal static byte[] EncodeJpeg(Image img, long quality)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using (var ms = new MemoryStream())
            using (var parms = new EncoderParameters(1))
            {
                parms.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                img.Save(ms, codec, parms);
                return ms.ToArray();
            }
        }

        private const uint PwClientOnly = 0x1;
        private const uint PwRenderFullContent = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    }
}

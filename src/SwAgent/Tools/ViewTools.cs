using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorks.Interop.sldworks;
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
        }

        [AgentTool("capture_views",
            "Screenshot of the SolidWorks graphics area for the chat's document. Named views temporarily re-orient the " +
            "model and restore the user's view afterwards. Use it to verify geometry after changes.")]
        public ToolResult CaptureViews(ToolContext ctx, CaptureInput input)
        {
            var model = ctx.RequireModel();
            if (!ReferenceEquals(ctx.Sw.ActiveDoc, model))
            {
                throw new ToolException($"'{model.GetTitle()}' is not the active window. Activate it first " +
                                        "(swApp.ActivateDoc3) - screenshots need the document on screen.");
            }

            var views = (input.Views == null || input.Views.Length == 0) ? new[] { ViewName.current } : input.Views.Distinct().ToArray();
            var maxSize = Math.Min(1600, Math.Max(320, input.MaxSize ?? 1280));
            var zoom = input.ZoomToFit ?? true;
            var isDrawing = model.GetType() == (int)swDocumentTypes_e.swDocDRAWING;
            if (isDrawing && views.Any(v => v != ViewName.current))
            {
                views = new[] { ViewName.current }; // drawings have no 3D orientations
            }

            var shots = CaptureMany(model, views, zoom);
            try
            {
                using (var composed = shots.Count == 1 ? Fit(shots[0].Image, maxSize) : Tile(shots, maxSize))
                {
                    var b64 = Convert.ToBase64String(EncodeJpeg(composed, 85));
                    var note = $"Captured {string.Join(", ", shots.Select(s => s.Name))} ({composed.Width}x{composed.Height}).";
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

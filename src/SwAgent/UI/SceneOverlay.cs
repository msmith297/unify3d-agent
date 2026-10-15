using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SwAgent.Infrastructure;

namespace SwAgent.UI
{
    /// <summary>
    /// The agent's sketch-on-the-model: annotations anchored to 3D points (lines, arrows, outlines, circles, dimensions,
    /// labels) drawn on a click-through layer over the graphics area, plus temporary bodies shown in the scene itself.
    /// Annotations are re-projected whenever the camera moves (IModelView.Transform maps model meters to view pixels),
    /// so they stay on the geometry while the user rotates, pans and zooms. Nothing is added to the model or the undo
    /// history; everything goes away with Clear (the agent, the panel's button, or the user's next message).
    /// </summary>
    internal sealed class SceneOverlay : Form
    {
        public sealed class Item
        {
            /// <summary>line | arrow | polygon | circle | marker | dimension | label</summary>
            public string Type;
            /// <summary>Model coordinates in meters.</summary>
            public List<double[]> Points = new List<double[]>();
            public string Text;
            public Color Color = DefaultColor;
            public bool Dashed;
            public bool Fill;
            /// <summary>circle: radius (meters) and plane normal; marker: radius in pixels.</summary>
            public double Radius;
            public double[] Normal;
        }

        public static readonly Color DefaultColor = Color.FromArgb(0xFF, 0x8C, 0x1A);

        private static SceneOverlay _current;
        private static readonly List<IBody2> Bodies = new List<IBody2>();
        private static IModelDoc2 _bodiesModel;

        /// <summary>Raised with the number of items and bodies shown (0 when cleared).</summary>
        public static event Action<int> Changed;

        public static int Count => (_current?._items.Count ?? 0) + Bodies.Count;

        /// <summary>Show annotations on this document's view (replacing the current ones unless append).</summary>
        public static void ShowItems(ISldWorks sw, IModelDoc2 model, IEnumerable<Item> items, bool append)
        {
            if (_current != null && (!ReferenceEquals(_current._model, model) || !append))
            {
                if (!ReferenceEquals(_current._model, model)) ClearAll();
                else _current._items.Clear();
            }
            if (_current == null)
            {
                var view = model.ActiveView as IModelView ?? throw new InvalidOperationException("The document has no view to draw on.");
                var owner = new IntPtr(((IFrame)sw.Frame()).GetHWndx64());
                _current = new SceneOverlay(sw, model, new IntPtr(view.GetViewHWndx64()), owner);
                _current.Start();
            }
            _current._items.AddRange(items);
            _current._dirty = true;
            _current.Tick();
            Changed?.Invoke(Count);
        }

        /// <summary>Show a temporary body in the scene (part documents). It belongs to no part and changes nothing.</summary>
        public static void ShowBody(IModelDoc2 model, IBody2 body, int rgb)
        {
            if (!(model is IPartDoc)) throw new InvalidOperationException("Temporary bodies can only be shown in a part document.");
            if (_bodiesModel != null && !ReferenceEquals(_bodiesModel, model)) HideBodies();
            _bodiesModel = model;
            Bodies.AddRange(PreviewDisplay.Show(model, new[] { (body, rgb, 0.5) }));
            Changed?.Invoke(Count);
        }

        public static void ClearAll()
        {
            if (_current != null)
            {
                var c = _current;
                _current = null;
                c._timer.Stop();
                c.Close();
                c.Dispose();
            }
            HideBodies();
            Changed?.Invoke(0);
        }

        private static void HideBodies()
        {
            if (_bodiesModel != null) PreviewDisplay.Hide(_bodiesModel, Bodies);
            Bodies.Clear();
            _bodiesModel = null;
        }

        // ---------- the layer ----------

        private readonly ISldWorks _sw;
        private readonly IModelDoc2 _model;
        private readonly IntPtr _viewHwnd;
        private readonly IntPtr _ownerHwnd;
        private readonly List<Item> _items = new List<Item>();
        private readonly Timer _timer = new Timer { Interval = 30 };
        private Rectangle _screenRect;
        private double[] _lastTransform;
        private bool _dirty;
        private bool _shown;

        private SceneOverlay(ISldWorks sw, IModelDoc2 model, IntPtr viewHwnd, IntPtr ownerHwnd)
        {
            _sw = sw;
            _model = model;
            _viewHwnd = viewHwnd;
            _ownerHwnd = ownerHwnd;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            _timer.Tick += Safe.Tick("Scene overlay", Tick);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // Layered, click-through and never activated: the user keeps working in SolidWorks underneath.
                cp.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;
                cp.Parent = _ownerHwnd;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        private void Start()
        {
            _timer.Start();
        }

        /// <summary>Follow the view: hide when it isn't showing, re-project when the camera or the window changed.</summary>
        private void Tick()
        {
            var view = ReferenceEquals(_sw.ActiveDoc, _model) ? _model.ActiveView as IModelView : null;
            var visible = view != null && IsWindowVisible(_viewHwnd) && !IsIconic(_ownerHwnd) && _items.Count > 0;
            if (!visible)
            {
                if (_shown) Hide();
                _shown = false;
                return;
            }
            if (!GetClientRect(_viewHwnd, out var rc)) return;
            var origin = new Point(0, 0);
            ClientToScreen(_viewHwnd, ref origin);
            var rect = new Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
            var t = (double[])((IMathTransform)view.Transform).ArrayData;
            var changed = _dirty || !_shown || rect != _screenRect || _lastTransform == null || !t.SequenceEqual(_lastTransform);
            if (!changed) return;
            _screenRect = rect;
            _lastTransform = t;
            _dirty = false;
            if (!_shown)
            {
                Bounds = rect;
                Show();
                _shown = true;
            }
            Render(t);
        }

        /// <summary>Model meters -> view pixels (x right, y down), from IModelView.Transform: s * (p · R) + T.</summary>
        internal static PointF Project(double[] t, double[] p) =>
            new PointF((float)(t[12] * (p[0] * t[0] + p[1] * t[3] + p[2] * t[6]) + t[9]), (float)(t[12] * (p[0] * t[1] + p[1] * t[4] + p[2] * t[7]) + t[10]));

        private void Render(double[] t)
        {
            if (_screenRect.Width < 2 || _screenRect.Height < 2) return;
            using (var bmp = new Bitmap(_screenRect.Width, _screenRect.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    var dpi = g.DpiX / 96f;
                    // Shapes first, labels on top.
                    foreach (var item in _items.Where(i => i.Type != "label")) DrawShape(g, t, item, dpi);
                    foreach (var item in _items.Where(i => i.Type == "label")) DrawLabel(g, t, item, dpi);
                    DrawBadge(g, dpi);
                }
                Push(bmp);
            }
        }

        private static Pen PenFor(Item item, float width) =>
            new Pen(item.Color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round, DashStyle = item.Dashed ? DashStyle.Dash : DashStyle.Solid };

        /// <summary>A dark halo under every stroke so it reads on light and dark parts alike.</summary>
        private static void Stroke(Graphics g, Item item, PointF[] pts, float w, bool closed)
        {
            if (pts.Length < 2) return;
            using (var halo = new Pen(Color.FromArgb(120, 0, 0, 0), w + 3) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var pen = PenFor(item, w))
            {
                if (closed)
                {
                    g.DrawPolygon(halo, pts);
                    g.DrawPolygon(pen, pts);
                }
                else
                {
                    g.DrawLines(halo, pts);
                    g.DrawLines(pen, pts);
                }
            }
        }

        private static void ArrowHead(Graphics g, Color color, PointF from, PointF to, float size)
        {
            var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
            var p1 = new PointF((float)(to.X - size * Math.Cos(angle - 0.42)), (float)(to.Y - size * Math.Sin(angle - 0.42)));
            var p2 = new PointF((float)(to.X - size * Math.Cos(angle + 0.42)), (float)(to.Y - size * Math.Sin(angle + 0.42)));
            using (var halo = new Pen(Color.FromArgb(120, 0, 0, 0), 2f))
            using (var brush = new SolidBrush(color))
            {
                g.DrawPolygon(halo, new[] { to, p1, p2 });
                g.FillPolygon(brush, new[] { to, p1, p2 });
            }
        }

        private void DrawShape(Graphics g, double[] t, Item item, float dpi)
        {
            var w = 2.5f * dpi;
            var pts = item.Points.Select(p => Project(t, p)).ToArray();
            switch (item.Type)
            {
                case "line":
                    Stroke(g, item, pts, w, false);
                    break;
                case "arrow":
                    if (pts.Length < 2) return;
                    Stroke(g, item, pts, w, false);
                    ArrowHead(g, item.Color, pts[pts.Length - 2], pts[pts.Length - 1], 14f * dpi);
                    break;
                case "polygon":
                    if (pts.Length < 3) return;
                    if (item.Fill)
                        using (var fill = new SolidBrush(Color.FromArgb(70, item.Color)))
                            g.FillPolygon(fill, pts);
                    Stroke(g, item, pts, w, true);
                    break;
                case "circle":
                {
                    // A true 3D circle (center, plane normal, radius) sampled and projected - it foreshortens with the view.
                    if (pts.Length < 1 || item.Radius <= 0) return;
                    var c = item.Points[0];
                    var n = Unit(item.Normal ?? new double[] { 0, 0, 1 });
                    var u = Unit(Math.Abs(n[0]) > 0.9 ? Cross(n, new double[] { 0, 1, 0 }) : Cross(n, new double[] { 1, 0, 0 }));
                    var v = Cross(n, u);
                    var ring = Enumerable.Range(0, 72).Select(k =>
                    {
                        var a = k * Math.PI * 2 / 72;
                        double cu = Math.Cos(a) * item.Radius, cv = Math.Sin(a) * item.Radius;
                        return Project(t, new[] { c[0] + cu * u[0] + cv * v[0], c[1] + cu * u[1] + cv * v[1], c[2] + cu * u[2] + cv * v[2] });
                    }).ToArray();
                    if (item.Fill)
                        using (var fill = new SolidBrush(Color.FromArgb(70, item.Color)))
                            g.FillPolygon(fill, ring);
                    Stroke(g, item, ring, w, true);
                    break;
                }
                case "marker":
                {
                    // A ring of fixed screen size around a point ("this hole", "here").
                    if (pts.Length < 1) return;
                    var r = (float)(item.Radius > 0 ? item.Radius : 14) * dpi;
                    var p = pts[0];
                    using (var halo = new Pen(Color.FromArgb(120, 0, 0, 0), w + 3))
                    using (var pen = PenFor(item, w))
                    {
                        g.DrawEllipse(halo, p.X - r, p.Y - r, 2 * r, 2 * r);
                        g.DrawEllipse(pen, p.X - r, p.Y - r, 2 * r, 2 * r);
                    }
                    break;
                }
                case "dimension":
                {
                    // Arrows both ways between two points, the text in the middle.
                    if (pts.Length < 2) return;
                    Stroke(g, item, new[] { pts[0], pts[1] }, 1.8f * dpi, false);
                    ArrowHead(g, item.Color, pts[1], pts[0], 11f * dpi);
                    ArrowHead(g, item.Color, pts[0], pts[1], 11f * dpi);
                    if (!string.IsNullOrEmpty(item.Text))
                        DrawTag(g, item.Text, new PointF((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2), item.Color, dpi, center: true);
                    break;
                }
            }
        }

        /// <summary>A label: a tag offset from its anchor with a leader line, kept inside the view.</summary>
        private void DrawLabel(Graphics g, double[] t, Item item, float dpi)
        {
            if (item.Points.Count < 1 || string.IsNullOrEmpty(item.Text)) return;
            var anchor = Project(t, item.Points[0]);
            // Optional second point = where the tag sits; otherwise up and to the right of the anchor.
            var at = item.Points.Count > 1 ? Project(t, item.Points[1]) : new PointF(anchor.X + 46 * dpi, anchor.Y - 40 * dpi);
            using (var halo = new Pen(Color.FromArgb(120, 0, 0, 0), 4f))
            using (var pen = new Pen(item.Color, 1.6f * dpi))
            {
                g.DrawLine(halo, anchor, at);
                g.DrawLine(pen, anchor, at);
            }
            using (var dot = new SolidBrush(item.Color))
                g.FillEllipse(dot, anchor.X - 3.5f * dpi, anchor.Y - 3.5f * dpi, 7 * dpi, 7 * dpi);
            DrawTag(g, item.Text, at, item.Color, dpi, center: false);
        }

        private void DrawTag(Graphics g, string text, PointF at, Color accent, float dpi, bool center)
        {
            using (var font = new Font("Segoe UI", 9f, FontStyle.Regular))
            {
                var size = g.MeasureString(text, font, (int)(260 * dpi));
                var pad = 6 * dpi;
                var r = new RectangleF(center ? at.X - size.Width / 2 - pad : at.X, at.Y - size.Height / 2 - pad / 2, size.Width + pad * 2, size.Height + pad);
                // Keep it inside the view.
                r.X = Math.Max(2, Math.Min(r.X, _screenRect.Width - r.Width - 2));
                r.Y = Math.Max(2, Math.Min(r.Y, _screenRect.Height - r.Height - 2));
                using (var bg = new SolidBrush(Color.FromArgb(225, 0x24, 0x27, 0x2B)))
                using (var border = new Pen(accent, 1.4f * dpi))
                using (var path = RoundRect(r, 4 * dpi))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(border, path);
                }
                g.DrawString(text, font, Brushes.White, new RectangleF(r.X + pad, r.Y + pad / 2, size.Width + 1, size.Height + 1));
            }
        }

        /// <summary>A small note so the user knows what this is and how it goes away.</summary>
        private void DrawBadge(Graphics g, float dpi)
        {
            const string text = "Unify3D Agent overlay  ·  clears with your next message";
            using (var font = new Font("Segoe UI", 8f))
            {
                var size = g.MeasureString(text, font);
                var r = new RectangleF(8 * dpi, _screenRect.Height - size.Height - 14 * dpi, size.Width + 14 * dpi, size.Height + 6 * dpi);
                using (var bg = new SolidBrush(Color.FromArgb(170, 0x24, 0x27, 0x2B)))
                using (var path = RoundRect(r, 4 * dpi))
                    g.FillPath(bg, path);
                using (var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
                    g.DrawString(text, font, brush, r.X + 7 * dpi, r.Y + 3 * dpi);
            }
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static double[] Unit(double[] a)
        {
            var l = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
            return l < 1e-12 ? new double[] { 0, 0, 1 } : new[] { a[0] / l, a[1] / l, a[2] / l };
        }

        private void Push(Bitmap bmp)
        {
            var screenDc = GetDC(IntPtr.Zero);
            var memDc = CreateCompatibleDC(screenDc);
            var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            var old = SelectObject(memDc, hBitmap);
            try
            {
                var size = new NativeSize { cx = bmp.Width, cy = bmp.Height };
                var src = new NativePoint();
                var dst = new NativePoint { x = _screenRect.X, y = _screenRect.Y };
                var blend = new BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, UlwAlpha);
            }
            finally
            {
                SelectObject(memDc, old);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }

        private const int WsExLayered = 0x80000;
        private const int WsExTransparent = 0x20;
        private const int WsExToolWindow = 0x80;
        private const int WsExNoActivate = 0x8000000;
        private const int UlwAlpha = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int x, y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int cx, cy;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize,
            IntPtr hdcSrc, ref NativePoint pprSrc, int crKey, ref BlendFunction pblend, int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);
    }
}

using System;
using SwAgent.Infrastructure;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SwAgent.UI
{
    /// <summary>
    /// A transparent window laid exactly over the SolidWorks graphics area that lets the user scribble on the
    /// view (freehand pen, arrows, circles, highlighter). It is a per-pixel-alpha layered window: the background
    /// is alpha 1 (invisible but still receives the mouse), strokes are opaque. When finished, the strokes are
    /// composited onto a capture of the view and handed to the chat as an image.
    /// </summary>
    internal sealed class MarkupOverlay : Form
    {
        public enum Tool
        {
            pen,
            arrow,
            circle,
            highlighter,
        }

        private sealed class Stroke
        {
            public Tool Tool;
            public Color Color;
            public List<PointF> Points = new List<PointF>();
        }

        private readonly IntPtr _viewHwnd;
        private readonly IntPtr _ownerHwnd;
        private readonly List<Stroke> _strokes = new List<Stroke>();
        private readonly Timer _follow = new Timer { Interval = 250 };
        private Stroke _current;
        private Rectangle _screenRect;

        public MarkupOverlay(IntPtr viewHwnd, IntPtr ownerHwnd)
        {
            _viewHwnd = viewHwnd;
            _ownerHwnd = ownerHwnd;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            _follow.Tick += Safe.Tick("Markup follow view", () => FollowView());
        }

        public Tool CurrentTool { get; set; } = Tool.pen;
        public Color CurrentColor { get; set; } = Color.FromArgb(0xE5, 0x30, 0x2A);
        public int StrokeCount => _strokes.Count;

        /// <summary>Raised when the user presses Esc (cancel) or Enter (finish) inside the overlay.</summary>
        public event Action<string> KeyCommand;

        public event Action StrokesChanged;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExLayered | WsExToolWindow;
                cp.Parent = _ownerHwnd; // owned by the SolidWorks frame: stays above it, minimizes with it
                return cp;
            }
        }

        public void Open()
        {
            FollowView(force: true);
            Show();
            Activate();
            _follow.Start();
            Render();
        }

        public void Undo()
        {
            if (_strokes.Count == 0) return;
            _strokes.RemoveAt(_strokes.Count - 1);
            Render();
            StrokesChanged?.Invoke();
        }

        public void ClearStrokes()
        {
            _strokes.Clear();
            Render();
            StrokesChanged?.Invoke();
        }

        /// <summary>Draw the strokes onto a bitmap of the same size as the view (for compositing with a capture).</summary>
        public void PaintStrokes(Graphics g, float scale = 1f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var s in _strokes) DrawStroke(g, s, scale);
        }

        public Size ViewSize => _screenRect.Size;

        private void FollowView(bool force = false)
        {
            if (!GetClientRect(_viewHwnd, out var rc)) return;
            var origin = new Point(0, 0);
            ClientToScreen(_viewHwnd, ref origin);
            var rect = new Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
            if (!force && rect == _screenRect) return;
            var resized = rect.Size != _screenRect.Size;
            _screenRect = rect;
            Bounds = rect;
            if (resized && _strokes.Count > 0)
            {
                _strokes.Clear(); // geometry under the strokes moved; stale marks would mislead
                StrokesChanged?.Invoke();
            }
            if (Visible) Render();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _current = new Stroke { Tool = CurrentTool, Color = CurrentColor };
            _current.Points.Add(e.Location);
            _current.Points.Add(e.Location);
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_current == null) return;
            if (_current.Tool == Tool.pen || _current.Tool == Tool.highlighter)
            {
                var last = _current.Points[_current.Points.Count - 1];
                if (Math.Abs(last.X - e.X) + Math.Abs(last.Y - e.Y) < 2) return;
                _current.Points.Add(e.Location);
            }
            else
            {
                _current.Points[1] = e.Location;
            }
            Render();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_current == null) return;
            Capture = false;
            var p = _current.Points;
            var span = Math.Abs(p[p.Count - 1].X - p[0].X) + Math.Abs(p[p.Count - 1].Y - p[0].Y);
            if (_current.Tool == Tool.pen || _current.Tool == Tool.highlighter || span > 4) _strokes.Add(_current);
            _current = null;
            Render();
            StrokesChanged?.Invoke();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) KeyCommand?.Invoke("cancel");
            else if (e.KeyCode == Keys.Enter) KeyCommand?.Invoke("finish");
            else if (e.KeyCode == Keys.Z && e.Control) Undo();
        }

        /// <summary>Repaint the whole layer and push it to the screen with UpdateLayeredWindow.</summary>
        private void Render()
        {
            if (_screenRect.Width < 2 || _screenRect.Height < 2) return;
            using (var bmp = new Bitmap(_screenRect.Width, _screenRect.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha 1: invisible, but hit-testable
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    foreach (var s in _strokes) DrawStroke(g, s, 1f);
                    if (_current != null) DrawStroke(g, _current, 1f);
                    DrawChrome(g);
                }
                Push(bmp);
            }
        }

        /// <summary>Accent frame and a hint so it is obvious the view is in markup mode.</summary>
        private void DrawChrome(Graphics g)
        {
            using (var pen = new Pen(Color.FromArgb(220, 0x2F, 0x84, 0xD6), 3))
            {
                g.DrawRectangle(pen, 1, 1, _screenRect.Width - 3, _screenRect.Height - 3);
            }
            const string hint = "Markup: draw on the view  ·  Enter to attach  ·  Esc to cancel  ·  Ctrl+Z to undo";
            using (var font = new Font("Segoe UI", 9f))
            {
                var size = g.MeasureString(hint, font);
                var r = new RectangleF((_screenRect.Width - size.Width) / 2 - 10, 8, size.Width + 20, size.Height + 8);
                using (var bg = new SolidBrush(Color.FromArgb(215, 0x2B, 0x2F, 0x34)))
                using (var path = RoundRect(r, 5))
                {
                    g.FillPath(bg, path);
                }
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(hint, font, Brushes.White, r.X + 10, r.Y + 4);
            }
        }

        private static void DrawStroke(Graphics g, Stroke s, float scale)
        {
            var pts = s.Points.Select(p => new PointF(p.X * scale, p.Y * scale)).ToArray();
            if (pts.Length < 2) return;
            var w = Math.Max(1.5f, 3.5f * scale);
            switch (s.Tool)
            {
                case Tool.pen:
                    using (var pen = new Pen(s.Color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        if (pts.Length > 2) g.DrawCurve(pen, pts, 0.4f);
                        else g.DrawLines(pen, pts);
                    }
                    break;
                case Tool.highlighter:
                    using (var pen = new Pen(Color.FromArgb(110, s.Color), 18 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        g.DrawLines(pen, pts);
                    }
                    break;
                case Tool.arrow:
                {
                    var a = pts[0];
                    var b = pts[pts.Length - 1];
                    using (var pen = new Pen(s.Color, w) { StartCap = LineCap.Round })
                    {
                        g.DrawLine(pen, a, b);
                    }
                    var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
                    var head = 16f * scale;
                    var p1 = new PointF((float)(b.X - head * Math.Cos(angle - 0.45)), (float)(b.Y - head * Math.Sin(angle - 0.45)));
                    var p2 = new PointF((float)(b.X - head * Math.Cos(angle + 0.45)), (float)(b.Y - head * Math.Sin(angle + 0.45)));
                    using (var brush = new SolidBrush(s.Color))
                    {
                        g.FillPolygon(brush, new[] { b, p1, p2 });
                    }
                    break;
                }
                case Tool.circle:
                {
                    var a = pts[0];
                    var b = pts[pts.Length - 1];
                    using (var pen = new Pen(s.Color, w))
                    {
                        g.DrawEllipse(pen, Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                    }
                    break;
                }
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
            if (disposing) _follow.Dispose();
            base.Dispose(disposing);
        }

        private const int WsExLayered = 0x80000;
        private const int WsExToolWindow = 0x80;
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
    }
}

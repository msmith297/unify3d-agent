using System;
using SwAgent.Infrastructure;
using SwAgent.Tools;
using SwAgent.Voice;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Timer = System.Windows.Forms.Timer;
using System.Windows.Forms;

namespace SwAgent.UI
{
    /// <summary>
    /// The markup pen: the user draws on the SolidWorks view (freehand pen, arrows, circles, highlighter) and keeps the
    /// pen while turning the model. The overlay is a click-through, per-pixel-alpha layered window laid exactly over the
    /// graphics area; the left button is taken from a low-level mouse hook (see AgentController), every other button,
    /// the wheel and a 3D mouse still reach SolidWorks, so the view can be rotated, panned and zoomed mid-markup.
    ///
    /// Each view is a page: the view is captured when its first stroke starts, and when the view changes the strokes
    /// are painted onto that capture as one frame and the page is cleared for the next view. Esc (or Enter) finishes;
    /// the frames go to the chat (or into a voice message, in order with what the user said).
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

        /// <summary>One marked-up view: the capture with the strokes painted on, as a JPEG.</summary>
        public sealed class Frame
        {
            public string Data;
            public string MediaType = "image/jpeg";
            public DateTime At;
            public int Strokes;
        }

        private readonly IntPtr _viewHwnd;
        private readonly IntPtr _ownerHwnd;
        private readonly List<Stroke> _strokes = new List<Stroke>();
        private readonly List<Frame> _frames = new List<Frame>();
        private readonly Timer _follow = new Timer { Interval = 250 };
        private readonly Timer _viewPoll = new Timer { Interval = 600 };
        private Stroke _current;
        private Rectangle _screenRect;
        private Bitmap _background;
        private byte[] _backgroundThumb;
        private DateTime _pageStarted;

        public MarkupOverlay(IntPtr viewHwnd, IntPtr ownerHwnd)
        {
            _viewHwnd = viewHwnd;
            _ownerHwnd = ownerHwnd;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            _follow.Tick += Safe.Tick("Markup follow view", () => FollowView());
            _viewPoll.Tick += Safe.Tick("Markup view check", CheckViewChanged);
        }

        public Tool CurrentTool { get; set; } = Tool.pen;
        public Color CurrentColor { get; set; } = Color.FromArgb(0xE5, 0x30, 0x2A);

        /// <summary>Strokes on the current view (not yet a frame).</summary>
        public int StrokeCount => _strokes.Count;

        public int FrameCount => _frames.Count;

        /// <summary>The views marked so far (not the one being drawn on).</summary>
        public List<Frame> Frames => _frames.ToList();

        /// <summary>Raised when strokes or frames change (for the panel's counters).</summary>
        public event Action StrokesChanged;

        /// <summary>Raised when a view's markup becomes a frame (the view changed, or the markup finished).</summary>
        public event Action<Frame> FrameCaptured;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExLayered | WsExToolWindow | WsExTransparent | WsExNoActivate;
                cp.Parent = _ownerHwnd; // owned by the SolidWorks frame: stays above it, minimizes with it
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Keep the overlay itself out of screen captures (Windows 10 2004+), so a capture is always the bare view.
            SetWindowDisplayAffinity(Handle, WdaExcludeFromCapture);
        }

        public void Open()
        {
            FollowView(force: true);
            Show();
            _follow.Start();
            _viewPoll.Start();
            Render();
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<(int Msg, Point Pt)> _input = new System.Collections.Concurrent.ConcurrentQueue<(int, Point)>();
        private int _drainScheduled;
        private volatile bool _drawing;
        private volatile int _strokeCount;

        /// <summary>
        /// From the mouse hook (its own thread - only thread-safe calls here): the left button draws over the view and is
        /// swallowed, so SolidWorks doesn't select; the middle button and the wheel still turn or zoom the view, which ends
        /// the current page. The drawing itself happens on the UI thread. Returns true to swallow the event.
        /// </summary>
        public bool Hook(int msg, Point screen)
        {
            switch (msg)
            {
                case InputHooks.WmLButtonDown:
                    if (!OverView(screen)) return false;
                    _drawing = true;
                    Queue(msg, screen);
                    return true;
                case InputHooks.WmMouseMove:
                    if (_drawing) Queue(msg, screen);
                    return false; // the cursor still moves
                case InputHooks.WmLButtonUp:
                    if (!_drawing) return false;
                    _drawing = false;
                    Queue(msg, screen);
                    return true;
                case InputHooks.WmMButtonDown:
                case InputHooks.WmMouseWheel:
                case InputHooks.WmMouseHWheel:
                    if (_strokeCount > 0 && OverView(screen)) Queue(msg, screen);
                    return false;
                default:
                    return false;
            }
        }

        private void Queue(int msg, Point screen)
        {
            _input.Enqueue((msg, screen));
            if (Interlocked.Exchange(ref _drainScheduled, 1) != 0) return;
            try
            {
                BeginInvoke((MethodInvoker)(() => Safe.Run("Markup input", Drain)));
            }
            catch (InvalidOperationException)
            {
                // Closed.
            }
        }

        /// <summary>UI thread: apply the queued input (mouse moves arrive far faster than the layer can be redrawn).</summary>
        private void Drain()
        {
            Interlocked.Exchange(ref _drainScheduled, 0);
            while (_input.TryDequeue(out var e)) OnMouse(e.Msg, e.Pt);
            Render();
        }

        private void OnMouse(int msg, Point screen)
        {
            var local = new PointF(screen.X - _screenRect.X, screen.Y - _screenRect.Y);
            switch (msg)
            {
                case InputHooks.WmLButtonDown:
                    if (_strokes.Count == 0) BeginPage();
                    _current = new Stroke { Tool = CurrentTool, Color = CurrentColor };
                    _current.Points.Add(local);
                    _current.Points.Add(local);
                    break;
                case InputHooks.WmMouseMove:
                    if (_current == null) return;
                    if (_current.Tool == Tool.pen || _current.Tool == Tool.highlighter)
                    {
                        var last = _current.Points[_current.Points.Count - 1];
                        if (Math.Abs(last.X - local.X) + Math.Abs(last.Y - local.Y) >= 2) _current.Points.Add(local);
                    }
                    else
                    {
                        _current.Points[1] = local;
                    }
                    break;
                case InputHooks.WmLButtonUp:
                    if (_current == null) return;
                    var p = _current.Points;
                    var span = Math.Abs(p[p.Count - 1].X - p[0].X) + Math.Abs(p[p.Count - 1].Y - p[0].Y);
                    if (_current.Tool == Tool.pen || _current.Tool == Tool.highlighter || span > 4) _strokes.Add(_current);
                    _current = null;
                    _strokeCount = _strokes.Count;
                    StrokesChanged?.Invoke();
                    break;
                case InputHooks.WmMButtonDown:
                case InputHooks.WmMouseWheel:
                case InputHooks.WmMouseHWheel:
                    // The view is about to turn or zoom: the marks belong to the view as it is now.
                    if (_strokes.Count > 0) EndPage();
                    break;
            }
        }

        /// <summary>A key that turns the view (arrow keys, the view shortcuts) was pressed.</summary>
        public void ViewMayChange()
        {
            if (_strokes.Count > 0) EndPage();
        }

        public void Undo()
        {
            if (_strokes.Count == 0) return;
            _strokes.RemoveAt(_strokes.Count - 1);
            _strokeCount = _strokes.Count;
            Render();
            StrokesChanged?.Invoke();
        }

        public void ClearStrokes()
        {
            _strokes.Clear();
            _strokeCount = 0;
            Render();
            StrokesChanged?.Invoke();
        }

        /// <summary>End the markup: the current view becomes a frame too. Returns every frame, in order.</summary>
        public List<Frame> Finish()
        {
            EndPage();
            return _frames.ToList();
        }

        private bool OverView(Point screen)
        {
            if (!_screenRect.Contains(screen)) return false;
            var hit = WindowFromPoint(new NativePoint { x = screen.X, y = screen.Y });
            return hit == _viewHwnd || IsChild(_viewHwnd, hit);
        }

        private void BeginPage()
        {
            _pageStarted = DateTime.Now;
            _background?.Dispose();
            _background = null;
            _backgroundThumb = null;
            try
            {
                _background = ViewTools.CaptureWindow(_viewHwnd);
                _backgroundThumb = Thumb(_background);
            }
            catch (Exception ex)
            {
                Log.Warn("Markup: capturing the view failed: " + ex.Message);
            }
        }

        /// <summary>Paint the page's strokes onto the view captured when it started; clear the page.</summary>
        private void EndPage()
        {
            if (_current != null) return; // mid-stroke: finish it first
            if (_strokes.Count == 0) return;
            try
            {
                var shot = _background ?? ViewTools.CaptureWindow(_viewHwnd);
                using (var canvas = new Bitmap(shot.Width, shot.Height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.DrawImage(shot, 0, 0, shot.Width, shot.Height);
                        var scale = _screenRect.Width > 0 ? shot.Width / (float)_screenRect.Width : 1f;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        foreach (var s in _strokes) DrawStroke(g, s, scale);
                    }
                    using (var fitted = ViewTools.Fit(canvas, 1280))
                    {
                        var frame = new Frame
                        {
                            Data = Convert.ToBase64String(ViewTools.EncodeJpeg(fitted, 85)),
                            At = _pageStarted == default ? DateTime.Now : _pageStarted,
                            Strokes = _strokes.Count,
                        };
                        _frames.Add(frame);
                        FrameCaptured?.Invoke(frame);
                    }
                }
                if (!ReferenceEquals(shot, _background)) shot.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Markup: making a frame failed: " + ex.Message);
            }
            _strokes.Clear();
            _strokeCount = 0;
            _background?.Dispose();
            _background = null;
            _backgroundThumb = null;
            Render();
            StrokesChanged?.Invoke();
        }

        /// <summary>A 3D mouse or a view shortcut turned the view without a mouse event we saw: end the page then.</summary>
        private void CheckViewChanged()
        {
            if (_strokes.Count == 0 || _current != null || _backgroundThumb == null || !Visible) return;
            using (var now = ViewTools.CaptureWindow(_viewHwnd))
            {
                if (Difference(_backgroundThumb, Thumb(now)) > 9) EndPage();
            }
        }

        private static byte[] Thumb(Bitmap bmp)
        {
            const int w = 40, h = 30;
            var result = new byte[w * h];
            using (var small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(bmp, 0, 0, w, h);
                }
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var c = small.GetPixel(x, y);
                    result[y * w + x] = (byte)((c.R * 3 + c.G * 6 + c.B) / 10);
                }
            }
            return result;
        }

        private static double Difference(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return 255;
            double sum = 0;
            for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
            return sum / a.Length;
        }

        private void FollowView(bool force = false)
        {
            if (!GetClientRect(_viewHwnd, out var rc)) return;
            var origin = new Point(0, 0);
            ClientToScreen(_viewHwnd, ref origin);
            var rect = new Rectangle(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
            if (!force && rect == _screenRect) return;
            var resized = rect.Size != _screenRect.Size;
            if (resized && _strokes.Count > 0 && _screenRect.Width > 0) EndPage(); // keep what was drawn on the old size
            _screenRect = rect;
            Bounds = rect;
            if (Visible) Render();
        }

        /// <summary>Repaint the whole layer and push it to the screen with UpdateLayeredWindow.</summary>
        private void Render()
        {
            if (_screenRect.Width < 2 || _screenRect.Height < 2) return;
            using (var bmp = new Bitmap(_screenRect.Width, _screenRect.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
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
            var views = _frames.Count == 0 ? "" : $"  ·  {_frames.Count} view{(_frames.Count == 1 ? "" : "s")} kept";
            var hint = "Pen: draw with the left button  ·  turn the model to mark another view" + views + "  ·  Esc when done  ·  Ctrl+Z undo";
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
            if (disposing)
            {
                _follow.Dispose();
                _viewPoll.Dispose();
                _background?.Dispose();
            }
            base.Dispose(disposing);
        }

        private const int WsExLayered = 0x80000;
        private const int WsExToolWindow = 0x80;
        private const int WsExTransparent = 0x20;
        private const int WsExNoActivate = 0x8000000;
        private const int UlwAlpha = 2;
        private const uint WdaExcludeFromCapture = 0x11;

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
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr parent, IntPtr child);

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    }
}

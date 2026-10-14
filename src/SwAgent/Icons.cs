using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace SwAgent
{
    /// <summary>
    /// Ribbon and task pane icons, drawn in code at 128 px (xCAD scales them to every SolidWorks icon size).
    /// Flat glyphs in the SolidWorks blue so they sit naturally next to the native commands.
    /// </summary>
    public static class Icons
    {
        private static readonly Color Blue = Color.FromArgb(0x1E, 0x6F, 0xD9);
        private static readonly Color Ink = Color.FromArgb(0x3B, 0x42, 0x4C);
        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>();

        /// <summary>The Unify3D "U" mark (assets/brand/mark-128.png).</summary>
        public static byte[] Agent
        {
            get
            {
                lock (Cache)
                {
                    if (Cache.TryGetValue(nameof(Agent), out var cached)) return cached;
                    using (var stream = typeof(Icons).Assembly.GetManifestResourceStream("Brand.mark-128.png"))
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        return Cache[nameof(Agent)] = ms.ToArray();
                    }
                }
            }
        }

        public static byte[] Chat => Draw(nameof(Chat), g =>
        {
            ChatBubble(g, Blue, fill: false);
            Sparkle(g, Blue, 64, 56, 20);
        });

        public static byte[] NewChat => Draw(nameof(NewChat), g =>
        {
            ChatBubble(g, Ink, fill: false);
            using (var pen = Pen(Blue, 11))
            {
                g.DrawLine(pen, 64, 34, 64, 78);
                g.DrawLine(pen, 42, 56, 86, 56);
            }
        });

        public static byte[] Selection => Draw(nameof(Selection), g =>
        {
            using (var pen = Pen(Ink, 8))
            {
                pen.DashPattern = new[] { 2.2f, 1.6f };
                g.DrawRectangle(pen, 14, 14, 76, 76);
            }
            using (var fill = new SolidBrush(Blue))
            {
                // cursor arrow
                g.FillPolygon(fill, new[]
                {
                    new PointF(58, 52), new PointF(116, 78), new PointF(90, 86), new PointF(104, 112),
                    new PointF(92, 118), new PointF(78, 92), new PointF(60, 108),
                });
            }
        });

        public static byte[] Camera => Draw(nameof(Camera), g =>
        {
            using (var pen = Pen(Ink, 9))
            {
                g.DrawPath(pen, Rounded(new RectangleF(10, 34, 108, 76), 14));
                g.DrawLine(pen, 42, 34, 50, 18);
                g.DrawLine(pen, 50, 18, 78, 18);
                g.DrawLine(pen, 78, 18, 86, 34);
            }
            using (var pen = Pen(Blue, 10))
            {
                g.DrawEllipse(pen, 42, 46, 44, 44);
            }
        });

        public static byte[] Settings => Draw(nameof(Settings), g =>
        {
            using (var pen = Pen(Ink, 10))
            {
                for (var i = 0; i < 8; i++)
                {
                    var a = i * Math.PI / 4;
                    g.DrawLine(pen, (float)(64 + Math.Cos(a) * 38), (float)(64 + Math.Sin(a) * 38),
                        (float)(64 + Math.Cos(a) * 54), (float)(64 + Math.Sin(a) * 54));
                }
                g.DrawEllipse(pen, 26, 26, 76, 76);
            }
            using (var pen = Pen(Blue, 10))
            {
                g.DrawEllipse(pen, 48, 48, 32, 32);
            }
        });

        /// <summary>Toolbox: the Agent Tools group, and tools that have no icon of their own yet.</summary>
        public static byte[] ToolDefault => Draw(nameof(ToolDefault), g => Toolbox(g));

        public static byte[] NewTool => Draw(nameof(NewTool), g =>
        {
            Toolbox(g);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, 74, 70, 52, 52);
            using (var b = new SolidBrush(Color.FromArgb(0x2E, 0x9E, 0x44))) g.FillEllipse(b, 79, 75, 42, 42);
            using (var pen = Pen(Color.White, 8))
            {
                g.DrawLine(pen, 100, 84, 100, 108);
                g.DrawLine(pen, 88, 96, 112, 96);
            }
        });

        private static void Toolbox(Graphics g)
        {
            using (var pen = Pen(Ink, 9)) g.DrawPath(pen, Rounded(new RectangleF(44, 18, 40, 26), 6));
            using (var body = Rounded(new RectangleF(10, 40, 108, 74), 10))
            {
                using (var b = new SolidBrush(Color.FromArgb(0xD9, 0x4A, 0x38))) g.FillPath(b, body);
                using (var pen = Pen(Color.FromArgb(0x8E, 0x2A, 0x1E), 5)) g.DrawPath(pen, body);
            }
            using (var b = new SolidBrush(Color.FromArgb(0x8E, 0x2A, 0x1E))) g.FillRectangle(b, 12, 66, 104, 8);
            using (var b = new SolidBrush(Color.FromArgb(0xE8, 0xE8, 0xE8))) g.FillRectangle(b, 54, 60, 20, 20);
        }

        private static void ChatBubble(Graphics g, Color color, bool fill)
        {
            using (var path = Rounded(new RectangleF(10, 12, 108, 86), 22))
            {
                path.AddPolygon(new[] { new PointF(30, 92), new PointF(26, 120), new PointF(58, 96) });
                if (fill)
                {
                    using (var b = new SolidBrush(color)) g.FillPath(b, path);
                }
                else
                {
                    using (var pen = Pen(color, 9)) g.DrawPath(pen, Rounded(new RectangleF(12, 14, 104, 82), 20));
                    using (var pen = Pen(color, 9))
                    {
                        g.DrawLine(pen, 34, 95, 28, 118);
                        g.DrawLine(pen, 28, 118, 58, 96);
                    }
                }
            }
        }

        /// <summary>Four-pointed star used as the "AI" accent.</summary>
        private static void Sparkle(Graphics g, Color color, float cx, float cy, float r)
        {
            var k = r * 0.28f;
            using (var b = new SolidBrush(color))
            {
                g.FillPolygon(b, new[]
                {
                    new PointF(cx, cy - r), new PointF(cx + k, cy - k), new PointF(cx + r, cy), new PointF(cx + k, cy + k),
                    new PointF(cx, cy + r), new PointF(cx - k, cy + k), new PointF(cx - r, cy), new PointF(cx - k, cy - k),
                });
            }
        }

        private static Pen Pen(Color c, float w) => new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        private static GraphicsPath Rounded(RectangleF r, float radius)
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

        private static byte[] Draw(string key, Action<Graphics> paint)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
                using (var bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        paint(g);
                    }
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        return Cache[key] = ms.ToArray();
                    }
                }
            }
        }
    }
}

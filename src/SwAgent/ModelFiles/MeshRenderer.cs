using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SwAgent.ModelFiles
{
    /// <summary>
    /// A small software renderer for meshes: orthographic, flat-shaded, with CAD-style outlines (silhouettes and sharp
    /// creases) and one colour per shell. It needs no window, GPU or SolidWorks view, so it runs on a background thread
    /// and can't disturb what the user is looking at.
    /// </summary>
    internal static class MeshRenderer
    {
        internal sealed class ViewSpec
        {
            public string Name;

            /// <summary>From the model towards the eye, in a Y-up world.</summary>
            public double[] Dir;

            public double[] Up;
        }

        /// <summary>The overview the agent gets: the four standard views plus two obliques that show the back and the underside.</summary>
        public static readonly ViewSpec[] Six =
        {
            new ViewSpec { Name = "isometric", Dir = new[] { 1.0, 1, 1 }, Up = new[] { 0.0, 1, 0 } },
            new ViewSpec { Name = "front", Dir = new[] { 0.0, 0, 1 }, Up = new[] { 0.0, 1, 0 } },
            new ViewSpec { Name = "top", Dir = new[] { 0.0, 1, 0 }, Up = new[] { 0.0, 0, -1 } },
            new ViewSpec { Name = "right", Dir = new[] { 1.0, 0, 0 }, Up = new[] { 0.0, 1, 0 } },
            new ViewSpec { Name = "back iso", Dir = new[] { -1.0, 0.8, -1 }, Up = new[] { 0.0, 1, 0 } },
            new ViewSpec { Name = "from below", Dir = new[] { 0.8, -1, 1 }, Up = new[] { 0.0, 1, 0 } },
        };

        private const int Supersample = 2;
        private static readonly int[] Palette =
        {
            Rgb(176, 190, 206), Rgb(214, 182, 128), Rgb(152, 192, 152), Rgb(206, 156, 156), Rgb(164, 164, 212),
            Rgb(124, 190, 200), Rgb(206, 204, 142), Rgb(190, 160, 200),
        };
        private static readonly int EdgeColor = Rgb(38, 46, 56);

        /// <summary>All six views tiled 3 x 2, each labelled and with an axis triad.</summary>
        public static Bitmap RenderSheet(Mesh mesh, int[] triShell, char up, int tileW = 400, int tileH = 300, CancellationToken ct = default)
        {
            var tiles = new Bitmap[Six.Length];
            try
            {
                Parallel.For(0, Six.Length, new ParallelOptions { CancellationToken = ct }, i => tiles[i] = RenderView(mesh, triShell, Six[i], up, tileW, tileH, ct));
                const int cols = 3;
                var sheet = new Bitmap(tileW * cols, tileH * ((Six.Length + cols - 1) / cols));
                using (var g = Graphics.FromImage(sheet))
                using (var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var small = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var labelBg = new SolidBrush(Color.FromArgb(200, 32, 38, 46)))
                using (var border = new Pen(Color.FromArgb(170, 170, 170)))
                {
                    g.Clear(Color.White);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    for (var i = 0; i < tiles.Length; i++)
                    {
                        int x = i % cols * tileW, y = i / cols * tileH;
                        g.DrawImage(tiles[i], x, y, tileW, tileH);
                        g.DrawRectangle(border, x, y, tileW - 1, tileH - 1);
                        var label = Six[i].Name.ToUpperInvariant();
                        var size = g.MeasureString(label, font);
                        g.FillRectangle(labelBg, x + 6, y + 6, size.Width + 8, size.Height + 2);
                        g.DrawString(label, font, Brushes.White, x + 10, y + 7);
                        DrawTriad(g, small, Six[i], up, x + 26, y + tileH - 24);
                    }
                }
                return sheet;
            }
            finally
            {
                foreach (var t in tiles) t?.Dispose();
            }
        }

        public static Bitmap RenderView(Mesh mesh, int[] triShell, ViewSpec view, char up, int width, int height, CancellationToken ct = default)
        {
            int w = width * Supersample, h = height * Supersample;
            var (d, right, u) = Basis(view, up);
            var light = Normalize(new[] { d[0] + 0.55 * u[0] - 0.35 * right[0], d[1] + 0.55 * u[1] - 0.35 * right[1], d[2] + 0.55 * u[2] - 0.35 * right[2] });

            var p = mesh.Positions;
            var idx = mesh.Indices;
            var nv = mesh.VertexCount;
            var sx = new float[nv];
            var sy = new float[nv];
            var sz = new float[nv];
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (var i = 0; i < nv; i++)
            {
                double x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
                var a = x * right[0] + y * right[1] + z * right[2];
                var b = x * u[0] + y * u[1] + z * u[2];
                sx[i] = (float)a;
                sy[i] = (float)b;
                sz[i] = (float)(x * d[0] + y * d[1] + z * d[2]);
                if (a < minX) minX = a;
                if (a > maxX) maxX = a;
                if (b < minY) minY = b;
                if (b > maxY) maxY = b;
            }
            var rangeX = Math.Max(maxX - minX, 1e-9);
            var rangeY = Math.Max(maxY - minY, 1e-9);
            var scale = Math.Min(w * 0.84 / rangeX, h * 0.80 / rangeY);
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            for (var i = 0; i < nv; i++)
            {
                sx[i] = (float)((sx[i] - cx) * scale + w / 2.0);
                sy[i] = (float)(h / 2.0 - (sy[i] - cy) * scale);
            }

            // Per-triangle unit normal and flat-shaded colour.
            var nt = mesh.TriangleCount;
            var normals = new float[nt * 3];
            var shade = new int[nt];
            for (var t = 0; t < nt; t++)
            {
                int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
                double ux = p[b * 3] - p[a * 3], uy = p[b * 3 + 1] - p[a * 3 + 1], uz = p[b * 3 + 2] - p[a * 3 + 2];
                double vx = p[c * 3] - p[a * 3], vy = p[c * 3 + 1] - p[a * 3 + 1], vz = p[c * 3 + 2] - p[a * 3 + 2];
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0)
                {
                    nx /= len;
                    ny /= len;
                    nz /= len;
                }
                // Two-sided: normals in files are often inconsistent, so face the viewer.
                if (nx * d[0] + ny * d[1] + nz * d[2] < 0)
                {
                    nx = -nx;
                    ny = -ny;
                    nz = -nz;
                }
                normals[t * 3] = (float)nx;
                normals[t * 3 + 1] = (float)ny;
                normals[t * 3 + 2] = (float)nz;
                var diffuse = Math.Max(0, nx * light[0] + ny * light[1] + nz * light[2]);
                var sky = 0.5 + 0.5 * (nx * u[0] + ny * u[1] + nz * u[2]);
                var intensity = Math.Min(1.08, 0.34 + 0.56 * diffuse + 0.14 * sky);
                var baseColor = Palette[(triShell != null && t < triShell.Length ? triShell[t] : 0) % Palette.Length];
                shade[t] = Scale(baseColor, intensity);
            }

            var depth = new float[w * h];
            var tri = new int[w * h];
            for (var i = 0; i < depth.Length; i++)
            {
                depth[i] = float.NegativeInfinity;
                tri[i] = -1;
            }
            for (var t = 0; t < nt; t++)
            {
                if ((t & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                Rasterize(t, idx[t * 3], idx[t * 3 + 1], idx[t * 3 + 2], sx, sy, sz, depth, tri, w, h);
            }

            // Colour, then outlines where neighbouring pixels belong to different surfaces.
            var tol = (float)(0.015 * Math.Max(rangeX, rangeY));
            var cosCrease = (float)Math.Cos(38 * Math.PI / 180);
            var cosSmooth = (float)Math.Cos(8 * Math.PI / 180);
            var pixels = new int[w * h];
            for (var y = 0; y < h; y++)
            {
                var bg = Lerp(Rgb(231, 237, 244), Rgb(255, 255, 255), y / (double)h);
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    pixels[i] = tri[i] < 0 ? bg : shade[tri[i]];
                }
            }
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (x + 1 < w && IsEdge(i, i + 1, tri, depth, normals, triShell, tol, cosCrease, cosSmooth)) pixels[i] = pixels[i + 1] = EdgeColor;
                if (y + 1 < h && IsEdge(i, i + w, tri, depth, normals, triShell, tol, cosCrease, cosSmooth)) pixels[i] = pixels[i + w] = EdgeColor;
            }

            using (var big = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                var data = big.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (var y = 0; y < h; y++) Marshal.Copy(pixels, y * w, data.Scan0 + y * data.Stride, w);
                }
                finally
                {
                    big.UnlockBits(data);
                }
                var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(result))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(big, 0, 0, width, height);
                }
                return result;
            }
        }

        private static bool IsEdge(int i, int j, int[] tri, float[] depth, float[] n, int[] triShell, float tol, float cosCrease, float cosSmooth)
        {
            int a = tri[i], b = tri[j];
            if (a == b) return false;
            if (a < 0 || b < 0) return true; // silhouette against the background
            if (triShell != null && triShell[a] != triShell[b]) return true;
            var dot = n[a * 3] * n[b * 3] + n[a * 3 + 1] * n[b * 3 + 1] + n[a * 3 + 2] * n[b * 3 + 2];
            if (dot < cosCrease) return true;
            return dot < cosSmooth && Math.Abs(depth[i] - depth[j]) > tol;
        }

        private static void Rasterize(int t, int a, int b, int c, float[] sx, float[] sy, float[] sz, float[] depth, int[] tri, int w, int h)
        {
            float x0 = sx[a], y0 = sy[a], x1 = sx[b], y1 = sy[b], x2 = sx[c], y2 = sy[c];
            var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
            if (Math.Abs(area) < 1e-12f) return;
            var minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
            var maxX = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
            var minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))));
            var maxY = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
            if (minX > maxX || minY > maxY) return;
            var inv = 1f / area;
            float z0 = sz[a], z1 = sz[b], z2 = sz[c];
            for (var y = minY; y <= maxY; y++)
            {
                var py = y + 0.5f;
                for (var x = minX; x <= maxX; x++)
                {
                    var px = x + 0.5f;
                    var w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) * inv;
                    var w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) * inv;
                    var w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    var z = w0 * z0 + w1 * z1 + w2 * z2;
                    var i = y * w + x;
                    if (z <= depth[i]) continue;
                    depth[i] = z;
                    tri[i] = t;
                }
            }
        }

        /// <summary>Small X/Y/Z arrows so the agent can tell which way the model faces in each tile.</summary>
        private static void DrawTriad(Graphics g, Font font, ViewSpec view, char up, float ox, float oy)
        {
            var (d, right, u) = Basis(view, up);
            var axes = new[] { ("X", new[] { 1.0, 0, 0 }, Color.FromArgb(214, 40, 40)), ("Y", new[] { 0.0, 1, 0 }, Color.FromArgb(40, 160, 60)), ("Z", new[] { 0.0, 0, 1 }, Color.FromArgb(40, 90, 214)) };
            // Draw the axis pointing away from the viewer first, so the nearer ones end up on top.
            foreach (var (name, axis, color) in axes.OrderBy(a => Dot(a.Item2, d)))
            {
                var ex = (float)(Dot(axis, right) * 16);
                var ey = (float)(-Dot(axis, u) * 16);
                using (var pen = new Pen(color, 2f))
                using (var brush = new SolidBrush(color))
                {
                    if (Math.Abs(ex) + Math.Abs(ey) > 1.5f) g.DrawLine(pen, ox, oy, ox + ex, oy + ey);
                    g.DrawString(name, font, brush, ox + ex * 1.3f - 4, oy + ey * 1.3f - 6);
                }
            }
        }

        /// <summary>View direction, screen right and screen up in model coordinates. Z-up files are viewed with Z up.</summary>
        internal static (double[] Dir, double[] Right, double[] Up) Basis(ViewSpec view, char up)
        {
            var d = Normalize(ToModel(view.Dir, up));
            var upv = ToModel(view.Up, up);
            var right = Normalize(Cross(upv, d));
            return (d, right, Cross(d, right));
        }

        /// <summary>The views are defined Y-up; for Z-up models, Y-up (a, b, c) is model (a, -c, b).</summary>
        private static double[] ToModel(double[] v, char up) => up == 'Z' ? new[] { v[0], -v[2], v[1] } : v;

        private static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        private static double[] Normalize(double[] v)
        {
            var len = Math.Sqrt(Dot(v, v));
            return len < 1e-12 ? v : new[] { v[0] / len, v[1] / len, v[2] / len };
        }

        private static int Rgb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

        private static int Scale(int c, double f)
        {
            int Ch(int shift) => Math.Min(255, (int)(((c >> shift) & 0xFF) * f));
            return Rgb(Ch(16), Ch(8), Ch(0));
        }

        private static int Lerp(int a, int b, double t)
        {
            int Ch(int shift) => (int)(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t);
            return Rgb(Ch(16), Ch(8), Ch(0));
        }
    }
}

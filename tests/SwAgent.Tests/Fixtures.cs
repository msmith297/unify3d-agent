using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SwAgent.Tests
{
    /// <summary>Small model files built in memory: boxes as STL (binary/ASCII), 3MF and OBJ.</summary>
    internal static class Fixtures
    {
        /// <summary>12 outward-facing triangles of an axis-aligned box.</summary>
        public static List<float[]> Box(float x0, float y0, float z0, float sx, float sy, float sz)
        {
            float x1 = x0 + sx, y1 = y0 + sy, z1 = z0 + sz;
            var v = new[]
            {
                new[] { x0, y0, z0 }, new[] { x1, y0, z0 }, new[] { x1, y1, z0 }, new[] { x0, y1, z0 },
                new[] { x0, y0, z1 }, new[] { x1, y0, z1 }, new[] { x1, y1, z1 }, new[] { x0, y1, z1 },
            };
            var faces = new[]
            {
                new[] { 0, 2, 1 }, new[] { 0, 3, 2 }, // bottom (-Z)
                new[] { 4, 5, 6 }, new[] { 4, 6, 7 }, // top (+Z)
                new[] { 0, 1, 5 }, new[] { 0, 5, 4 }, // -Y
                new[] { 2, 3, 7 }, new[] { 2, 7, 6 }, // +Y
                new[] { 1, 2, 6 }, new[] { 1, 6, 5 }, // +X
                new[] { 3, 0, 4 }, new[] { 3, 4, 7 }, // -X
            };
            return faces.Select(f => f.SelectMany(i => v[i]).ToArray()).ToList();
        }

        public static byte[] BinaryStl(IEnumerable<float[]> triangles, string header = "binary stl")
        {
            var tris = triangles.ToList();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                var h = new byte[80];
                Encoding.ASCII.GetBytes(header).CopyTo(h, 0);
                w.Write(h);
                w.Write((uint)tris.Count);
                foreach (var t in tris)
                {
                    w.Write(0f);
                    w.Write(0f);
                    w.Write(0f);
                    foreach (var c in t) w.Write(c);
                    w.Write((ushort)0);
                }
                return ms.ToArray();
            }
        }

        public static string AsciiStl(IEnumerable<float[]> triangles)
        {
            var sb = new StringBuilder("solid test\n");
            foreach (var t in triangles)
            {
                sb.Append("  facet normal 0 0 0\n    outer loop\n");
                for (var i = 0; i < 9; i += 3) sb.Append($"      vertex {F(t[i])} {F(t[i + 1])} {F(t[i + 2])}\n");
                sb.Append("    endloop\n  endfacet\n");
            }
            return sb.Append("endsolid test\n").ToString();
        }

        /// <summary>A 3MF package: one box object placed twice by build items (the second one moved by `offset`).</summary>
        public static byte[] ThreeMf(string unit, float size, float offset)
        {
            var verts = new StringBuilder();
            var tris = new StringBuilder();
            var box = Box(0, 0, 0, size, size, size);
            var index = new Dictionary<string, int>();
            foreach (var t in box)
            {
                var ids = new int[3];
                for (var c = 0; c < 3; c++)
                {
                    var key = $"{F(t[c * 3])} {F(t[c * 3 + 1])} {F(t[c * 3 + 2])}";
                    if (!index.TryGetValue(key, out var id))
                    {
                        id = index.Count;
                        index[key] = id;
                        verts.Append($"<vertex x=\"{F(t[c * 3])}\" y=\"{F(t[c * 3 + 1])}\" z=\"{F(t[c * 3 + 2])}\" />");
                    }
                    ids[c] = id;
                }
                tris.Append($"<triangle v1=\"{ids[0]}\" v2=\"{ids[1]}\" v3=\"{ids[2]}\" />");
            }
            var model = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                        $"<model unit=\"{unit}\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">" +
                        $"<resources><object id=\"1\" type=\"model\"><mesh><vertices>{verts}</vertices><triangles>{tris}</triangles></mesh></object></resources>" +
                        "<build><item objectid=\"1\" />" +
                        $"<item objectid=\"1\" transform=\"1 0 0 0 1 0 0 0 1 {F(offset)} 0 0\" /></build></model>";
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    using (var w = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open())) w.Write("<Types/>");
                    using (var w = new StreamWriter(zip.CreateEntry("3D/3dmodel.model").Open())) w.Write(model);
                }
                return ms.ToArray();
            }
        }

        public static string TempFile(string extension, byte[] content)
        {
            var dir = Path.Combine(Path.GetTempPath(), "SwAgent.Tests");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(path, content);
            return path;
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}

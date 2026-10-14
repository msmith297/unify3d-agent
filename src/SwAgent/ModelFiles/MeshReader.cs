using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;

namespace SwAgent.ModelFiles
{
    /// <summary>
    /// Reads the mesh formats directly (no SolidWorks needed): STL (binary and ASCII), 3MF and OBJ.
    /// Everything comes out in millimetres; Units says what the file declared.
    /// </summary>
    internal static class MeshReader
    {
        public sealed class Result
        {
            public Mesh Mesh;

            /// <summary>"mm (assumed - STL has no units)", "inch (from the 3MF file)", ...</summary>
            public string Units;

            /// <summary>Number of separate objects the file itself declares (3MF build items, OBJ groups), or 0.</summary>
            public int Objects;

            /// <summary>Which way is up in the file's world: 'Z' for 3D-printing formats, 'Y' otherwise.</summary>
            public char Up = 'Y';
        }

        public static bool IsMeshFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".stl" || ext == ".3mf" || ext == ".obj";
        }

        public static Result Read(string path, CancellationToken ct = default)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".stl":
                    using (var s = File.OpenRead(path)) return ReadStl(s, ct);
                case ".3mf":
                    using (var s = File.OpenRead(path)) return Read3mf(s, ct);
                case ".obj":
                    using (var s = File.OpenRead(path)) return ReadObj(s, ct);
                default:
                    throw new NotSupportedException("Not a mesh file: " + Path.GetFileName(path));
            }
        }

        // ---- STL ----

        public static Result ReadStl(Stream stream, CancellationToken ct = default)
        {
            var bytes = ReadAll(stream);
            var mesh = new Mesh();
            if (IsBinaryStl(bytes))
            {
                var n = BitConverter.ToUInt32(bytes, 80);
                for (long i = 0; i < n; i++)
                {
                    if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                    var o = 84 + (int)i * 50 + 12; // skip the facet normal
                    mesh.AddTriangle(
                        BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8),
                        BitConverter.ToSingle(bytes, o + 12), BitConverter.ToSingle(bytes, o + 16), BitConverter.ToSingle(bytes, o + 20),
                        BitConverter.ToSingle(bytes, o + 24), BitConverter.ToSingle(bytes, o + 28), BitConverter.ToSingle(bytes, o + 32));
                }
            }
            else
            {
                var corner = new float[9];
                var k = 0;
                var lines = 0;
                foreach (var line in Lines(bytes))
                {
                    if ((++lines & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                    var t = line.TrimStart();
                    if (!t.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
                    var p = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length < 4) continue;
                    corner[k++] = F(p[1]);
                    corner[k++] = F(p[2]);
                    corner[k++] = F(p[3]);
                    if (k < 9) continue;
                    mesh.AddTriangle(corner[0], corner[1], corner[2], corner[3], corner[4], corner[5], corner[6], corner[7], corner[8]);
                    k = 0;
                }
            }
            mesh.Seal();
            return new Result { Mesh = mesh, Units = "mm (assumed - STL files carry no units)", Up = 'Z' };
        }

        /// <summary>Binary when the triangle count in the header matches the file size ("solid" headers happen in binary files too).</summary>
        internal static bool IsBinaryStl(byte[] bytes)
        {
            if (bytes.Length < 84) return false;
            var n = BitConverter.ToUInt32(bytes, 80);
            if (84L + 50L * n == bytes.Length) return true;
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512));
            return !(head.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase) && head.IndexOf("facet", StringComparison.OrdinalIgnoreCase) >= 0)
                   && 84L + 50L * n <= bytes.Length;
        }

        // ---- 3MF ----

        private sealed class MfObject
        {
            public float[] Vertices = new float[0];
            public int[] Triangles = new int[0];
            public readonly List<(string Key, double[] Transform)> Components = new List<(string, double[])>();
        }

        public static Result Read3mf(Stream stream, CancellationToken ct = default)
        {
            var objects = new Dictionary<string, MfObject>(StringComparer.OrdinalIgnoreCase);
            var build = new List<(string Key, double[] Transform)>();
            var scale = 1.0;
            var unit = "millimeter";
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                var parts = zip.Entries.Where(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName.Equals("3D/3dmodel.model", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
                if (parts.Count == 0) throw new InvalidDataException("No 3D model part in the 3MF package.");
                foreach (var part in parts)
                {
                    var isRoot = part == parts[0];
                    var partPath = "/" + part.FullName.TrimStart('/');
                    using (var s = part.Open())
                    {
                        var u = Parse3mfPart(s, partPath, objects, isRoot ? build : null, ct);
                        if (isRoot && u != null)
                        {
                            unit = u;
                            scale = UnitToMm(u);
                        }
                    }
                }
            }

            var mesh = new Mesh();
            var items = build.Count > 0 ? build : objects.Where(o => o.Value.Triangles.Length > 0).Select(o => (o.Key, (double[])null)).ToList();
            foreach (var (key, transform) in items) Emit3mf(mesh, objects, key, transform, scale, 0, ct);
            mesh.Seal();
            return new Result
            {
                Mesh = mesh,
                Units = unit == "millimeter" ? "mm (from the 3MF file)" : $"{unit} in the file, converted to mm",
                Objects = items.Count,
                Up = 'Z',
            };
        }

        /// <summary>Returns the part's unit attribute; fills objects (keyed "path#id") and, for the root part, the build items.</summary>
        private static string Parse3mfPart(Stream s, string partPath, Dictionary<string, MfObject> objects, List<(string, double[])> build, CancellationToken ct)
        {
            string unit = null;
            MfObject current = null;
            var verts = new List<float>();
            var tris = new List<int>();
            var count = 0;
            using (var r = XmlReader.Create(s, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit }))
            {
                while (r.Read())
                {
                    if ((++count & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                    if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "object" && current != null)
                    {
                        current.Vertices = verts.ToArray();
                        current.Triangles = tris.ToArray();
                        verts.Clear();
                        tris.Clear();
                        current = null;
                        continue;
                    }
                    if (r.NodeType != XmlNodeType.Element) continue;
                    switch (r.LocalName)
                    {
                        case "model":
                            unit = r.GetAttribute("unit") ?? "millimeter";
                            break;
                        case "object":
                            current = new MfObject();
                            objects[partPath + "#" + r.GetAttribute("id")] = current;
                            if (r.IsEmptyElement) current = null;
                            break;
                        case "vertex" when current != null:
                            verts.Add(F(r.GetAttribute("x")));
                            verts.Add(F(r.GetAttribute("y")));
                            verts.Add(F(r.GetAttribute("z")));
                            break;
                        case "triangle" when current != null:
                            tris.Add(I(r.GetAttribute("v1")));
                            tris.Add(I(r.GetAttribute("v2")));
                            tris.Add(I(r.GetAttribute("v3")));
                            break;
                        case "component" when current != null:
                            current.Components.Add((RefKey(r, partPath), Matrix(r.GetAttribute("transform"))));
                            break;
                        case "item" when build != null:
                            build.Add((RefKey(r, partPath), Matrix(r.GetAttribute("transform"))));
                            break;
                    }
                }
            }
            return unit;
        }

        /// <summary>"path#id" of the object a component or build item points at (production extension: p:path).</summary>
        private static string RefKey(XmlReader r, string partPath)
        {
            string path = null;
            if (r.HasAttributes)
            {
                for (var i = 0; i < r.AttributeCount; i++)
                {
                    r.MoveToAttribute(i);
                    if (r.LocalName == "path") path = r.Value;
                }
                r.MoveToElement();
            }
            return (string.IsNullOrEmpty(path) ? partPath : "/" + path.TrimStart('/')) + "#" + r.GetAttribute("objectid");
        }

        private static void Emit3mf(Mesh mesh, Dictionary<string, MfObject> objects, string key, double[] transform, double scale, int depth, CancellationToken ct)
        {
            if (depth > 16 || !objects.TryGetValue(key, out var obj)) return;
            ct.ThrowIfCancellationRequested();
            var v = obj.Vertices;
            if (obj.Triangles.Length > 0)
            {
                var map = new int[v.Length / 3];
                for (var i = 0; i < map.Length; i++)
                {
                    double x = v[i * 3], y = v[i * 3 + 1], z = v[i * 3 + 2];
                    if (transform != null)
                    {
                        // 3MF matrices are row-vector: [x y z 1] * M (m00 m01 m02 / m10 m11 m12 / m20 m21 m22 / m30 m31 m32).
                        var tx = x * transform[0] + y * transform[3] + z * transform[6] + transform[9];
                        var ty = x * transform[1] + y * transform[4] + z * transform[7] + transform[10];
                        var tz = x * transform[2] + y * transform[5] + z * transform[8] + transform[11];
                        x = tx;
                        y = ty;
                        z = tz;
                    }
                    map[i] = mesh.AddVertex((float)(x * scale), (float)(y * scale), (float)(z * scale));
                }
                var t = obj.Triangles;
                for (var i = 0; i + 2 < t.Length; i += 3)
                {
                    if (t[i] < map.Length && t[i + 1] < map.Length && t[i + 2] < map.Length) mesh.AddTriangle(map[t[i]], map[t[i + 1]], map[t[i + 2]]);
                }
            }
            foreach (var (childKey, childTransform) in obj.Components)
            {
                Emit3mf(mesh, objects, childKey, Combine(childTransform, transform), scale, depth + 1, ct);
            }
        }

        /// <summary>a then b (row-vector convention), either may be null (identity).</summary>
        internal static double[] Combine(double[] a, double[] b)
        {
            if (a == null) return b;
            if (b == null) return a;
            var r = new double[12];
            for (var row = 0; row < 4; row++)
            for (var col = 0; col < 3; col++)
            {
                var sum = row == 3 ? b[9 + col] : 0;
                for (var k = 0; k < 3; k++) sum += a[row * 3 + k] * b[k * 3 + col];
                r[row * 3 + col] = sum;
            }
            return r;
        }

        private static double[] Matrix(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var p = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return p.Length == 12 ? p.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray() : null;
        }

        internal static double UnitToMm(string unit)
        {
            switch ((unit ?? "").ToLowerInvariant())
            {
                case "micron": return 0.001;
                case "centimeter": return 10;
                case "inch": return 25.4;
                case "foot": return 304.8;
                case "meter": return 1000;
                default: return 1;
            }
        }

        // ---- OBJ ----

        public static Result ReadObj(Stream stream, CancellationToken ct = default)
        {
            var bytes = ReadAll(stream);
            var verts = new List<int>(); // welded index of each OBJ vertex
            var mesh = new Mesh();
            var groups = 0;
            var lines = 0;
            var face = new List<int>();
            foreach (var line in Lines(bytes))
            {
                if ((++lines & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                if (line.Length < 2) continue;
                if (line[0] == 'v' && line[1] == ' ')
                {
                    var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4) verts.Add(mesh.AddVertex(F(p[1]), F(p[2]), F(p[3])));
                }
                else if (line[0] == 'f' && line[1] == ' ')
                {
                    face.Clear();
                    foreach (var token in line.Substring(2).Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var slash = token.IndexOf('/');
                        var i = I(slash < 0 ? token : token.Substring(0, slash));
                        i = i < 0 ? verts.Count + i : i - 1; // negative indices count back from the last vertex
                        if (i >= 0 && i < verts.Count) face.Add(verts[i]);
                    }
                    for (var k = 1; k + 1 < face.Count; k++) mesh.AddTriangle(face[0], face[k], face[k + 1]); // fan
                }
                else if ((line[0] == 'o' || line[0] == 'g') && line[1] == ' ')
                {
                    groups++;
                }
            }
            mesh.Seal();
            return new Result { Mesh = mesh, Units = "mm (assumed - OBJ files carry no units)", Objects = groups };
        }

        // ---- helpers ----

        private static byte[] ReadAll(Stream s)
        {
            if (s is MemoryStream m) return m.ToArray();
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static IEnumerable<string> Lines(byte[] bytes)
        {
            using (var r = new StreamReader(new MemoryStream(bytes), Encoding.ASCII))
            {
                string line;
                while ((line = r.ReadLine()) != null) yield return line;
            }
        }

        private static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        private static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}

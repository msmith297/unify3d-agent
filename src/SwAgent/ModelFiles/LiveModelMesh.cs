using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;

namespace SwAgent.ModelFiles
{
    /// <summary>
    /// The open document as a triangle mesh, read from SolidWorks' own display tessellation (IFace2.GetTessTriangles)
    /// with each face's colour. Only read-only API calls: the user's view, camera and selection are never touched, so
    /// the agent can look at the model from any angle (MeshRenderer) while the user keeps working.
    ///
    /// Cached per document until the model changes (IModelDoc2.GetUpdateStamp) or other components are hidden.
    /// </summary>
    internal sealed class LiveModelMesh
    {
        private static readonly int DefaultColor = Argb(198, 202, 207); // neutral metal grey
        private static readonly Dictionary<string, LiveModelMesh> Cache = new Dictionary<string, LiveModelMesh>();

        public Mesh Mesh { get; } = new Mesh();

        /// <summary>Face index of each triangle (face boundaries are drawn as edges).</summary>
        public int[] TriFace { get; private set; }

        /// <summary>Colour of each triangle.</summary>
        public int[] TriColor { get; private set; }

        public long ReadMs { get; private set; }

        private readonly List<FaceEntry> _faces = new List<FaceEntry>();
        private string _key;

        private sealed class FaceEntry
        {
            public IFace2 Face;
            public IBody2 Body;
            public IComponent2 Component;
            public int First;
            public int Count;
        }

        /// <summary>The mesh of the document as it is now (from the cache when nothing changed).</summary>
        public static LiveModelMesh For(ISldWorks sw, IModelDoc2 model, ICollection<string> hiddenComponents)
        {
            var key = $"{model.GetPathName()}|{model.GetTitle()}|{model.GetUpdateStamp()}|{string.Join(",", (hiddenComponents ?? new string[0]).OrderBy(x => x))}";
            lock (Cache)
            {
                var docKey = model.GetTitle();
                if (Cache.TryGetValue(docKey, out var cached) && cached._key == key) return cached;
                var mesh = Read(sw, model, hiddenComponents);
                mesh._key = key;
                Cache[docKey] = mesh;
                return mesh;
            }
        }

        private static LiveModelMesh Read(ISldWorks sw, IModelDoc2 model, ICollection<string> hidden)
        {
            var watch = Stopwatch.StartNew();
            var result = new LiveModelMesh();
            var faceOfTri = new List<int>();
            var colorOfTri = new List<int>();
            var hide = new HashSet<string>(hidden ?? new string[0], StringComparer.OrdinalIgnoreCase);

            if (model is IPartDoc part)
            {
                var partColor = ColorOf(model.MaterialPropertyValues);
                foreach (var body in Bodies(part.GetBodies2((int)swBodyType_e.swSolidBody, true)).Concat(Bodies(part.GetBodies2((int)swBodyType_e.swSheetBody, true))))
                    result.AddBody(body, null, null, partColor, faceOfTri, colorOfTri);
            }
            else if (model is IAssemblyDoc asm)
            {
                var mu = (IMathUtility)sw.GetMathUtility();
                foreach (var comp in ((object[])asm.GetComponents(false) ?? new object[0]).OfType<IComponent2>())
                {
                    try
                    {
                        if (comp.IsSuppressed() || comp.IsHidden(true) || comp.IsEnvelope()) continue;
                        if (hide.Contains(comp.Name2) || hide.Contains(comp.Name2.Split('/').Last())) continue;
                        if (comp.GetModelDoc2() is IAssemblyDoc) continue; // sub-assemblies: their parts are listed too
                        var bodies = Bodies(comp.GetBodies3((int)swBodyType_e.swSolidBody, out _)).ToList();
                        if (bodies.Count == 0) continue;
                        var compColor = ColorOf(comp.MaterialPropertyValues) ?? ColorOf((comp.GetModelDoc2() as IModelDoc2)?.MaterialPropertyValues);
                        var transform = NeedsTransform(comp, bodies) ? Matrix(mu, comp.Transform2) : null;
                        foreach (var body in bodies) result.AddBody(body, comp, transform, compColor, faceOfTri, colorOfTri);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Reading {comp.Name2} for rendering failed: {ex.Message}");
                    }
                }
            }
            result.Mesh.Seal();
            result.TriFace = faceOfTri.ToArray();
            result.TriColor = colorOfTri.ToArray();
            result.ReadMs = watch.ElapsedMilliseconds;
            Log.Info($"Read {model.GetTitle()} for off-screen views: {result._faces.Count} faces, {result.Mesh.TriangleCount} triangles in {result.ReadMs} ms");
            return result;
        }

        private void AddBody(IBody2 body, IComponent2 comp, double[] m, int? inherited, List<int> faceOfTri, List<int> colorOfTri)
        {
            var bodyColor = ColorOf(body.MaterialPropertyValues2) ?? inherited ?? DefaultColor;
            foreach (var face in ((object[])body.GetFaces() ?? new object[0]).OfType<IFace2>())
            {
                if (!(face.GetTessTriangles(true) is float[] t) || t.Length < 9) continue;
                var color = ColorOf(face.MaterialPropertyValues) ?? bodyColor;
                var index = _faces.Count;
                var first = Mesh.TriangleCount;
                for (var i = 0; i + 8 < t.Length; i += 9)
                {
                    var before = Mesh.TriangleCount;
                    // metres -> millimetres (the Mesh convention); assembly components moved into place.
                    var a = Point(t[i], t[i + 1], t[i + 2], m);
                    var b = Point(t[i + 3], t[i + 4], t[i + 5], m);
                    var c = Point(t[i + 6], t[i + 7], t[i + 8], m);
                    Mesh.AddTriangle(a[0], a[1], a[2], b[0], b[1], b[2], c[0], c[1], c[2]);
                    if (Mesh.TriangleCount > before)
                    {
                        faceOfTri.Add(index);
                        colorOfTri.Add(color);
                    }
                }
                _faces.Add(new FaceEntry { Face = face, Body = body, Component = comp, First = first, Count = Mesh.TriangleCount - first });
            }
        }

        /// <summary>
        /// Triangles of the given entities: faces, edges (their two faces), features, bodies, components. Matching uses
        /// ISldWorks.IsSame, so references from the user's selection or [S1] resolve to the right faces.
        /// </summary>
        public bool[] Mark(ISldWorks sw, IEnumerable<object> entities)
        {
            var mark = new bool[Mesh.TriangleCount];
            var any = false;
            foreach (var e in entities ?? Enumerable.Empty<object>())
            {
                foreach (var entry in _faces.Where(f => Matches(sw, f, e)))
                {
                    for (var t = entry.First; t < entry.First + entry.Count && t < mark.Length; t++) mark[t] = true;
                    any = true;
                }
            }
            return any ? mark : null;
        }

        private static bool Matches(ISldWorks sw, FaceEntry f, object e)
        {
            try
            {
                switch (e)
                {
                    case IFace2 face:
                        return Same(sw, f.Face, face);
                    case IEdge edge:
                        return ((object[])edge.GetTwoAdjacentFaces2() ?? new object[0]).Any(x => Same(sw, f.Face, x));
                    case IFeature feature:
                        return ((object[])feature.GetFaces() ?? new object[0]).Any(x => Same(sw, f.Face, x));
                    case IBody2 body:
                        return Same(sw, f.Body, body);
                    case IComponent2 comp:
                        return f.Component != null && string.Equals(f.Component.Name2, comp.Name2, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // An entity that no longer exists matches nothing.
            }
            return false;
        }

        private static bool Same(ISldWorks sw, object a, object b) =>
            a != null && b != null && (ReferenceEquals(a, b) || sw.IsSame(a, b) == (int)swObjectEquality.swObjectSame);

        private static IEnumerable<IBody2> Bodies(object array) => ((object[])array ?? new object[0]).OfType<IBody2>().Where(b => b.Visible);

        /// <summary>
        /// Component bodies may come in the part's own coordinates or already placed in the assembly; compare the body
        /// boxes with the component's (assembly-space) box to tell.
        /// </summary>
        private static bool NeedsTransform(IComponent2 comp, List<IBody2> bodies)
        {
            if (!(comp.GetBox(false, false) is double[] cb) || cb.Length < 6) return true;
            double[] bb = null;
            foreach (var b in bodies)
            {
                if (!(b.GetBodyBox() is double[] x) || x.Length < 6) continue;
                bb = bb == null ? (double[])x.Clone() : new[] { Math.Min(bb[0], x[0]), Math.Min(bb[1], x[1]), Math.Min(bb[2], x[2]), Math.Max(bb[3], x[3]), Math.Max(bb[4], x[4]), Math.Max(bb[5], x[5]) };
            }
            if (bb == null) return true;
            var size = Math.Max(1e-6, Math.Max(cb[3] - cb[0], Math.Max(cb[4] - cb[1], cb[5] - cb[2])));
            var off = Enumerable.Range(0, 6).Max(i => Math.Abs(cb[i] - bb[i]));
            return off > size * 0.05;
        }

        /// <summary>The component's placement as a 3x4 matrix, measured by transforming the origin and unit axes.</summary>
        private static double[] Matrix(IMathUtility mu, MathTransform t)
        {
            if (t == null) return null;
            double[] P(double x, double y, double z) =>
                (double[])((IMathPoint)((IMathPoint)mu.CreatePoint(new[] { x, y, z })).MultiplyTransform(t)).ArrayData;
            var o = P(0, 0, 0);
            var ex = P(1, 0, 0);
            var ey = P(0, 1, 0);
            var ez = P(0, 0, 1);
            return new[]
            {
                ex[0] - o[0], ey[0] - o[0], ez[0] - o[0], o[0],
                ex[1] - o[1], ey[1] - o[1], ez[1] - o[1], o[1],
                ex[2] - o[2], ey[2] - o[2], ez[2] - o[2], o[2],
            };
        }

        private static float[] Point(float x, float y, float z, double[] m)
        {
            if (m == null) return new[] { x * 1000f, y * 1000f, z * 1000f };
            return new[]
            {
                (float)((m[0] * x + m[1] * y + m[2] * z + m[3]) * 1000),
                (float)((m[4] * x + m[5] * y + m[6] * z + m[7]) * 1000),
                (float)((m[8] * x + m[9] * y + m[10] * z + m[11]) * 1000),
            };
        }

        /// <summary>MaterialPropertyValues (r, g, b in 0..1, ...) as a colour, or null when unset.</summary>
        private static int? ColorOf(object values)
        {
            if (!(values is double[] v) || v.Length < 3 || v[0] < 0) return null;
            return Argb((int)(Math.Max(0, Math.Min(1, v[0])) * 255), (int)(Math.Max(0, Math.Min(1, v[1])) * 255), (int)(Math.Max(0, Math.Min(1, v[2])) * 255));
        }

        private static int Argb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
    }
}

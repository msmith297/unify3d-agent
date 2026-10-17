using System;
using System.Collections.Generic;

namespace SwAgent.ModelFiles
{
    /// <summary>
    /// An indexed triangle mesh in millimetres: what every 3D model file is reduced to so it can be measured and
    /// rendered without SolidWorks' graphics (see MeshStats, MeshRenderer). Vertices are welded on insertion, so
    /// triangles that touch share indices - which is what lets MeshStats find shells and open edges.
    /// </summary>
    internal sealed class Mesh
    {
        private readonly Dictionary<VertexKey, int> _weld = new Dictionary<VertexKey, int>();
        private float[] _positions = new float[3 * 1024];
        private int[] _indices = new int[3 * 1024];

        public int VertexCount { get; private set; }

        public int TriangleCount { get; private set; }

        /// <summary>x, y, z of each vertex (mm).</summary>
        public float[] Positions => _positions;

        /// <summary>Three vertex indices per triangle.</summary>
        public int[] Indices => _indices;

        /// <summary>Triangles dropped because two of their corners coincide (degenerate after welding).</summary>
        public int Degenerate { get; private set; }

        public int AddVertex(float x, float y, float z)
        {
            var key = new VertexKey(x, y, z);
            if (_weld.TryGetValue(key, out var index)) return index;
            if (VertexCount * 3 + 3 > _positions.Length) Array.Resize(ref _positions, _positions.Length * 2);
            index = VertexCount++;
            _positions[index * 3] = x;
            _positions[index * 3 + 1] = y;
            _positions[index * 3 + 2] = z;
            _weld[key] = index;
            return index;
        }

        public void AddTriangle(int a, int b, int c)
        {
            if (a == b || b == c || a == c)
            {
                Degenerate++;
                return;
            }
            if (TriangleCount * 3 + 3 > _indices.Length) Array.Resize(ref _indices, _indices.Length * 2);
            _indices[TriangleCount * 3] = a;
            _indices[TriangleCount * 3 + 1] = b;
            _indices[TriangleCount * 3 + 2] = c;
            TriangleCount++;
        }

        public void AddTriangle(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz) =>
            AddTriangle(AddVertex(ax, ay, az), AddVertex(bx, by, bz), AddVertex(cx, cy, cz));

        /// <summary>Drop the weld table once the mesh is complete (it is the biggest allocation for large meshes).</summary>
        public void Seal() => _weld.Clear();

        public double X(int v) => _positions[v * 3];

        public double Y(int v) => _positions[v * 3 + 1];

        public double Z(int v) => _positions[v * 3 + 2];

        /// <summary>Exact-bits key: files repeat the same float for a shared corner, so no tolerance is needed.</summary>
        private readonly struct VertexKey : IEquatable<VertexKey>
        {
            private readonly float _x, _y, _z;

            public VertexKey(float x, float y, float z)
            {
                // -0 and +0 are the same point.
                _x = x == 0 ? 0 : x;
                _y = y == 0 ? 0 : y;
                _z = z == 0 ? 0 : z;
            }

            public bool Equals(VertexKey o) => _x == o._x && _y == o._y && _z == o._z;

            public override bool Equals(object obj) => obj is VertexKey k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    var h = _x.GetHashCode();
                    h = h * 397 ^ _y.GetHashCode();
                    return h * 397 ^ _z.GetHashCode();
                }
            }
        }
    }
}

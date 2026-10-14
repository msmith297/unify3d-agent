using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SwAgent.ModelFiles
{
    /// <summary>
    /// Measurements of a mesh (mm): bounding box, surface area, enclosed volume, shells (connected pieces - one per
    /// body for a clean export) and whether it is watertight. Shell ids per triangle also colour the rendered views.
    /// </summary>
    internal sealed class MeshStats
    {
        public sealed class Shell
        {
            public int Triangles;
            public double Area;
            public double Volume;
            public double MinX = double.MaxValue, MinY = double.MaxValue, MinZ = double.MaxValue;
            public double MaxX = double.MinValue, MaxY = double.MinValue, MaxZ = double.MinValue;

            public double SizeX => MaxX - MinX;
            public double SizeY => MaxY - MinY;
            public double SizeZ => MaxZ - MinZ;
        }

        public int Triangles;
        public int Vertices;
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public double Area;

        /// <summary>Enclosed volume (divergence theorem); only meaningful when Closed.</summary>
        public double Volume;

        /// <summary>Every edge is shared by exactly two triangles.</summary>
        public bool Closed;

        public int OpenEdges;
        public int NonManifoldEdges;

        /// <summary>Connected pieces, largest first.</summary>
        public List<Shell> Shells = new List<Shell>();

        /// <summary>Shell index (into Shells) of each triangle.</summary>
        public int[] TriangleShell;

        public double SizeX => MaxX - MinX;
        public double SizeY => MaxY - MinY;
        public double SizeZ => MaxZ - MinZ;

        public static MeshStats Compute(Mesh mesh, CancellationToken ct = default)
        {
            var s = new MeshStats { Triangles = mesh.TriangleCount, Vertices = mesh.VertexCount };
            var p = mesh.Positions;
            var idx = mesh.Indices;
            if (mesh.TriangleCount == 0) return s;

            // Shells: union-find over the vertices each triangle joins.
            var parent = new int[mesh.VertexCount];
            for (var i = 0; i < parent.Length; i++) parent[i] = i;
            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                Union(parent, idx[t * 3], idx[t * 3 + 1]);
                Union(parent, idx[t * 3 + 1], idx[t * 3 + 2]);
            }
            var rootShell = new Dictionary<int, int>();
            var shells = new List<Shell>();
            var triShell = new int[mesh.TriangleCount];

            s.MinX = s.MinY = s.MinZ = double.MaxValue;
            s.MaxX = s.MaxY = s.MaxZ = double.MinValue;
            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                if ((t & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
                var root = Find(parent, a);
                if (!rootShell.TryGetValue(root, out var si))
                {
                    si = shells.Count;
                    rootShell[root] = si;
                    shells.Add(new Shell());
                }
                triShell[t] = si;
                var sh = shells[si];

                double ax = p[a * 3], ay = p[a * 3 + 1], az = p[a * 3 + 2];
                double bx = p[b * 3], by = p[b * 3 + 1], bz = p[b * 3 + 2];
                double cx = p[c * 3], cy = p[c * 3 + 1], cz = p[c * 3 + 2];
                // Area: half the cross product's length. Volume: signed tetrahedra against the origin.
                double ux = bx - ax, uy = by - ay, uz = bz - az, vx = cx - ax, vy = cy - ay, vz = cz - az;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                var area = 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
                var vol = (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx)) / 6.0;
                sh.Triangles++;
                sh.Area += area;
                sh.Volume += vol;
                Grow(sh, ax, ay, az);
                Grow(sh, bx, by, bz);
                Grow(sh, cx, cy, cz);
            }

            // Shells with inward-facing normals come out negative; a body's volume is the magnitude.
            foreach (var sh in shells)
            {
                sh.Volume = Math.Abs(sh.Volume);
                s.Area += sh.Area;
                s.Volume += sh.Volume;
                s.MinX = Math.Min(s.MinX, sh.MinX);
                s.MinY = Math.Min(s.MinY, sh.MinY);
                s.MinZ = Math.Min(s.MinZ, sh.MinZ);
                s.MaxX = Math.Max(s.MaxX, sh.MaxX);
                s.MaxY = Math.Max(s.MaxY, sh.MaxY);
                s.MaxZ = Math.Max(s.MaxZ, sh.MaxZ);
            }

            // Largest first; remap the triangle ids to the new order.
            var order = Enumerable.Range(0, shells.Count).OrderByDescending(i => shells[i].Volume).ThenByDescending(i => shells[i].Area).ToArray();
            var remap = new int[shells.Count];
            for (var i = 0; i < order.Length; i++) remap[order[i]] = i;
            for (var t = 0; t < triShell.Length; t++) triShell[t] = remap[triShell[t]];
            s.Shells = order.Select(i => shells[i]).ToList();
            s.TriangleShell = triShell;

            CountEdges(mesh, s, ct);
            return s;
        }

        /// <summary>Open (used once) and non-manifold (used 3+ times) edges, by sorting the undirected edge keys.</summary>
        private static void CountEdges(Mesh mesh, MeshStats s, CancellationToken ct)
        {
            var idx = mesh.Indices;
            var keys = new long[mesh.TriangleCount * 3];
            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                for (var e = 0; e < 3; e++)
                {
                    var a = idx[t * 3 + e];
                    var b = idx[t * 3 + (e + 1) % 3];
                    keys[t * 3 + e] = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                }
            }
            ct.ThrowIfCancellationRequested();
            Array.Sort(keys);
            for (var i = 0; i < keys.Length;)
            {
                var j = i + 1;
                while (j < keys.Length && keys[j] == keys[i]) j++;
                var n = j - i;
                if (n == 1) s.OpenEdges++;
                else if (n > 2) s.NonManifoldEdges++;
                i = j;
            }
            s.Closed = s.OpenEdges == 0 && s.NonManifoldEdges == 0;
        }

        private static void Grow(Shell s, double x, double y, double z)
        {
            if (x < s.MinX) s.MinX = x;
            if (y < s.MinY) s.MinY = y;
            if (z < s.MinZ) s.MinZ = z;
            if (x > s.MaxX) s.MaxX = x;
            if (y > s.MaxY) s.MaxY = y;
            if (z > s.MaxZ) s.MaxZ = z;
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a != b) parent[a] = b;
        }
    }
}

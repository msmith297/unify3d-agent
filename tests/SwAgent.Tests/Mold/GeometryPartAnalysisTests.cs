using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SwAgent.Mold.Geometry;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// The whole mold_part analysis, headless, on triangle meshes of the design's fixtures (6.2): F1 puck, F2 bushing,
    /// F3 block with a side groove, F4 puck with a lid-side dome. A mesh stands in for the SolidWorks body: same faces,
    /// ray casting by brute force (first hit, or every crossing with enter/exit types).
    /// </summary>
    public class GeometryPartAnalysisTests
    {
        private const double Mm = 0.001;

        /// <summary>A triangle mesh with faces, in meters, triangles counter-clockwise seen from outside.</summary>
        internal sealed class MeshPart : IPartGeometry
        {
            private readonly List<List<double[]>> _faces = new List<List<double[]>>(); // each triangle: 9 doubles

            public int AddFace()
            {
                _faces.Add(new List<double[]>());
                return _faces.Count - 1;
            }

            public void Tri(int face, double[] a, double[] b, double[] c) => _faces[face].Add(new[] { a[0], a[1], a[2], b[0], b[1], b[2], c[0], c[1], c[2] });

            public void Quad(int face, double[] a, double[] b, double[] c, double[] d)
            {
                Tri(face, a, b, c);
                Tri(face, a, c, d);
            }

            public string Name => "mesh";

            /// <summary>Behave as if UNBLOCK were ignored: every ray stops at its first hit.</summary>
            public bool FirstHitsOnly { get; set; }

            public int FaceCount => _faces.Count;
            public float[] FaceTriangles(int face) => _faces[face].SelectMany(t => t.Select(x => (float)x)).ToArray();

            public float[] FaceNormals(int face) => _faces[face].SelectMany(t =>
            {
                var n = Normal(t);
                return Enumerable.Range(0, 3).SelectMany(_ => n.Select(x => (float)x));
            }).ToArray();

            private static double[] Normal(double[] t)
            {
                var n = V3.Cross(new[] { t[3] - t[0], t[4] - t[1], t[5] - t[2] }, new[] { t[6] - t[0], t[7] - t[1], t[8] - t[2] });
                return V3.Unit(n);
            }

            public double Volume => _faces.SelectMany(f => f).Sum(t => V3.Dot(new[] { t[0], t[1], t[2] }, V3.Cross(new[] { t[3], t[4], t[5] }, new[] { t[6], t[7], t[8] })) / 6);

            public double Area => _faces.SelectMany(f => f).Sum(t => V3.Len(V3.Cross(new[] { t[3] - t[0], t[4] - t[1], t[5] - t[2] }, new[] { t[6] - t[0], t[7] - t[1], t[8] - t[2] })) / 2);

            public double[] Box(MoldFrame frame)
            {
                var pts = _faces.SelectMany(f => f).SelectMany(t => new[] { new[] { t[0], t[1], t[2] }, new[] { t[3], t[4], t[5] }, new[] { t[6], t[7], t[8] } })
                    .Select(frame.ToFrame).ToList();
                return new[] { pts.Min(p => p[0]), pts.Min(p => p[1]), pts.Min(p => p[2]), pts.Max(p => p[0]), pts.Max(p => p[1]), pts.Max(p => p[2]) };
            }

            public string PersistRef(int face) => "F" + face;

            public List<RayHit> Cast(IList<double[]> pts, IList<double[]> dirs, int start, int n, int opts)
            {
                var all = (opts & 12) == 12 && !FirstHitsOnly;
                var hits = new List<RayHit>();
                var tris = _faces.SelectMany(f => f).ToList();
                for (var k = 0; k < n; k++)
                {
                    var o = pts[start + k];
                    var d = V3.Unit(dirs[start + k]);
                    var mine = new List<(double T, double[] N)>();
                    foreach (var t in tris)
                    {
                        var hit = Intersect(o, d, t);
                        if (hit.HasValue) mine.Add((hit.Value, Normal(t)));
                    }
                    if (mine.Count == 0) continue;
                    foreach (var (tt, nn) in all ? mine.OrderBy(x => x.T) : mine.OrderBy(x => x.T).Take(1))
                        hits.Add(new RayHit { Ray = k, Type = 1 | (V3.Dot(d, nn) < 0 ? 16 : 32), Point = V3.Add(o, V3.Mul(d, tt)), Normal = nn });
                }
                return hits;
            }

            /// <summary>Möller-Trumbore: the ray parameter of the hit, or null.</summary>
            private static double? Intersect(double[] o, double[] d, double[] t)
            {
                var p0 = new[] { t[0], t[1], t[2] };
                var e1 = new[] { t[3] - t[0], t[4] - t[1], t[5] - t[2] };
                var e2 = new[] { t[6] - t[0], t[7] - t[1], t[8] - t[2] };
                var p = V3.Cross(d, e2);
                var det = V3.Dot(e1, p);
                if (Math.Abs(det) < 1e-18) return null;
                var inv = 1 / det;
                var s = V3.Sub(o, p0);
                var u = V3.Dot(s, p) * inv;
                if (u < -1e-12 || u > 1 + 1e-12) return null;
                var q = V3.Cross(s, e1);
                var v = V3.Dot(d, q) * inv;
                if (v < -1e-12 || u + v > 1 + 1e-12) return null;
                var tt = V3.Dot(e2, q) * inv;
                return tt > 1e-9 ? tt : (double?)null;
            }
        }

        private static double[] P(double x, double y, double z) => new[] { x * Mm, y * Mm, z * Mm };

        /// <summary>A cylinder (or tube with an inner radius) on the XY plane from z0 to z1, n segments.</summary>
        internal static MeshPart Tube(double r, double rIn, double z0, double z1, int n)
        {
            var m = new MeshPart();
            int top = m.AddFace(), bottom = m.AddFace(), side = m.AddFace(), inner = rIn > 0 ? m.AddFace() : -1;
            for (var k = 0; k < n; k++)
            {
                double a0 = 2 * Math.PI * k / n, a1 = 2 * Math.PI * (k + 1) / n;
                double[] O(double rr, double a, double z) => P(rr * Math.Cos(a), rr * Math.Sin(a), z);
                if (rIn > 0)
                {
                    m.Quad(top, O(rIn, a0, z1), O(r, a0, z1), O(r, a1, z1), O(rIn, a1, z1));
                    m.Quad(bottom, O(rIn, a0, z0), O(rIn, a1, z0), O(r, a1, z0), O(r, a0, z0));
                    m.Quad(inner, O(rIn, a0, z0), O(rIn, a0, z1), O(rIn, a1, z1), O(rIn, a1, z0));
                }
                else
                {
                    m.Tri(top, P(0, 0, z1), O(r, a0, z1), O(r, a1, z1));
                    m.Tri(bottom, P(0, 0, z0), O(r, a1, z0), O(r, a0, z0));
                }
                m.Quad(side, O(r, a0, z0), O(r, a1, z0), O(r, a1, z1), O(r, a0, z1));
            }
            return m;
        }

        private static readonly MoldFrame ZUp = MoldFrame.FromNormal(new double[] { 0, 0, 1 });

        private static PartReport Analyze(IPartGeometry g, PartAnalysisOptions o = null, IList<(string, float[])> critical = null) =>
            PartAnalysis.Analyze(g, ZUp, o ?? new PartAnalysisOptions { SampleMm = 1.0 }, critical ?? new List<(string, float[])>(), CancellationToken.None);

        [Fact]
        public void Mesh_stand_in_is_a_closed_solid()
        {
            var puck = Tube(50, 0, 0, 25, 256);
            Assert.InRange(puck.Volume / 1e-9, 196350 * 0.999, 196350 * 1.001);
            var hits = puck.Cast(new[] { P(0.3, 0.7, 30) }, new[] { new double[] { 0, 0, -1 } }, 0, 1, 15);
            Assert.Equal(2, hits.Count);
            Assert.True(hits[0].Enter && hits[1].Exit);
        }

        [Fact]
        public void F1_puck_numbers_and_parting_line_at_the_top_face()
        {
            var r = Analyze(Tube(50, 0, 0, 25, 256));
            Assert.InRange(r.Volume / 1e-9, 196350 * 0.999, 196350 * 1.001);
            Assert.InRange(r.ProjectedArea / 1e-6, 7854 * 0.995, 7854 * 1.005);
            Assert.InRange(r.Perimeter / Mm, 314.16 * 0.99, 314.16 * 1.01);
            Assert.Empty(r.PartingLine.UndercutFaces);
            Assert.True(r.PartingLine.Planar);
            Assert.Equal("top", r.PartingLine.Candidates[0].Id);
            Assert.Equal(25, r.PartingLine.Candidates[0].W / Mm, 3);
            Assert.Empty(r.Traps); // base pour
            Assert.Empty(r.Fragile);
            Assert.Contains(2, r.RedFaces); // the 0 deg wall needs draft
            Assert.Equal("multi-hit (ENTRY_EXIT | UNBLOCK)", (string)r.Json["sampling"]["columnRays"]);
        }

        [Fact]
        public void F2_bushing_core_pin_reported_and_flagged_only_when_thin()
        {
            var r = Analyze(Tube(30, 10, 0, 40, 180));
            var pin = Assert.Single(r.Fragile);
            Assert.Equal("core_pin", pin.Kind);
            Assert.InRange(pin.D / Mm, 19, 21);
            Assert.InRange(pin.H / Mm, 39.5, 40.5);
            Assert.False(pin.Flagged);
            Assert.Equal(1, (int)r.Json["outlineHoles"]);
            var thin = Analyze(Tube(30, 4.5, 0, 40, 180));
            Assert.True(Assert.Single(thin.Fragile).Flagged);
        }

        [Fact]
        public void F3_block_with_a_side_groove_has_the_groove_walls_as_undercuts()
        {
            // 40 x 40 x 20 mm block; a groove 2 mm deep, w 8..12, across the +X side.
            var m = new MeshPart();
            int top = m.AddFace(), bottom = m.AddFace(), xm = m.AddFace(), ym = m.AddFace(), yp = m.AddFace(), xpLow = m.AddFace(), xpHigh = m.AddFace(),
                gTop = m.AddFace(), gBottom = m.AddFace(), gBack = m.AddFace();
            m.Quad(top, P(-20, -20, 20), P(20, -20, 20), P(20, 20, 20), P(-20, 20, 20));
            m.Quad(bottom, P(-20, -20, 0), P(-20, 20, 0), P(20, 20, 0), P(20, -20, 0));
            m.Quad(xm, P(-20, -20, 0), P(-20, -20, 20), P(-20, 20, 20), P(-20, 20, 0));
            foreach (var (face, y, sign) in new[] { (ym, -20.0, -1), (yp, 20.0, 1) })
            {
                void Rect(double x0, double x1, double z0, double z1)
                {
                    if (sign < 0) m.Quad(face, P(x0, y, z0), P(x1, y, z0), P(x1, y, z1), P(x0, y, z1));
                    else m.Quad(face, P(x0, y, z0), P(x0, y, z1), P(x1, y, z1), P(x1, y, z0));
                }
                Rect(-20, 18, 0, 20);
                Rect(18, 20, 0, 8);
                Rect(18, 20, 12, 20);
            }
            m.Quad(xpLow, P(20, -20, 0), P(20, 20, 0), P(20, 20, 8), P(20, -20, 8));
            m.Quad(xpHigh, P(20, -20, 12), P(20, 20, 12), P(20, 20, 20), P(20, -20, 20));
            m.Quad(gTop, P(18, -20, 12), P(18, 20, 12), P(20, 20, 12), P(20, -20, 12)); // faces down
            m.Quad(gBottom, P(18, -20, 8), P(20, -20, 8), P(20, 20, 8), P(18, 20, 8)); // faces up
            m.Quad(gBack, P(18, -20, 8), P(18, 20, 8), P(18, 20, 12), P(18, -20, 12)); // faces +X
            Assert.InRange(m.Volume / 1e-9, 32000 - 320 - 1, 32000 - 320 + 1);
            var r = Analyze(m);
            Assert.Equal(new[] { gTop, gBottom }, r.PartingLine.UndercutFaces);
        }

        [Fact]
        public void F4_puck_with_a_lid_side_dome_traps_the_dome()
        {
            // Puck R 50 x 25 with a spherical cap of base radius 20 and height 5 on top.
            const double a = 20, hCap = 5, R0 = 50, T = 25;
            var R = (a * a + hCap * hCap) / (2 * hCap); // 42.5
            const int n = 128, rings = 16;
            var m = new MeshPart();
            int top = m.AddFace(), bottom = m.AddFace(), side = m.AddFace(), dome = m.AddFace();
            var theta0 = Math.Asin(a / R);
            double[] D(int ring, int k)
            {
                var th = theta0 * ring / rings;
                var ang = 2 * Math.PI * k / n;
                var rr = R * Math.Sin(th);
                return P(rr * Math.Cos(ang), rr * Math.Sin(ang), T + R * Math.Cos(th) - (R - hCap));
            }
            for (var k = 0; k < n; k++)
            {
                double a0 = 2 * Math.PI * k / n, a1 = 2 * Math.PI * (k + 1) / n;
                double[] O(double rr, double ang, double z) => P(rr * Math.Cos(ang), rr * Math.Sin(ang), z);
                m.Quad(top, D(rings, k), O(R0, a0, T), O(R0, a1, T), D(rings, k + 1));
                m.Tri(bottom, P(0, 0, 0), O(R0, a1, 0), O(R0, a0, 0));
                m.Quad(side, O(R0, a0, 0), O(R0, a1, 0), O(R0, a1, T), O(R0, a0, T));
                m.Tri(dome, D(0, 0), D(1, k), D(1, k + 1));
                for (var j = 1; j < rings; j++) m.Quad(dome, D(j, k), D(j + 1, k), D(j + 1, k + 1), D(j, k + 1));
            }
            var cap = Math.PI * hCap * hCap * (3 * R - hCap) / 3;
            Assert.InRange((m.Volume - n / 2.0 * R0 * R0 * Math.Sin(2 * Math.PI / n) * T * Mm * Mm * Mm) / 1e-9, cap * 0.99, cap * 1.01);
            var r = Analyze(m);
            Assert.Equal("top", r.PartingLine.Candidates[0].Id);
            var trap = Assert.Single(r.Traps);
            Assert.InRange(trap.Volume / 1e-9, cap * 0.95, cap * 1.05);
            var at = ((Newtonsoft.Json.Linq.JArray)r.Json["airTraps"][0]["at"]).Select(x => (double)x).ToArray();
            Assert.InRange(Math.Sqrt(at[0] * at[0] + at[1] * at[1]), 0, 1.0);
            Assert.InRange(at[2], T + hCap - 1, T + hCap + 0.01);
            // A vent at the dome's top drains it.
            var vented = Analyze(m, new PartAnalysisOptions { SampleMm = 1.0, Vents = { P(0, 0, T + hCap) } });
            Assert.Empty(vented.Traps);
        }

        [Fact]
        public void Without_multi_hit_rays_the_columns_fall_back_to_the_envelope()
        {
            var puck = Tube(50, 0, 0, 25, 256);
            puck.FirstHitsOnly = true;
            var r = Analyze(puck);
            Assert.Equal("envelope (first hits from above and below)", (string)r.Json["sampling"]["columnRays"]);
            Assert.Empty(r.Traps);
            Assert.Equal("top", r.PartingLine.Candidates[0].Id);
            Assert.Contains(r.Json["notes"], n => ((string)n).Contains("envelope"));
        }

        [Fact]
        public void Critical_top_face_vetoes_the_flat_lid_parting_line()
        {
            var puck = Tube(50, 0, 0, 25, 128);
            var r = Analyze(puck, null, new List<(string, float[])> { ("S7", puck.FaceTriangles(0)) });
            var top = r.PartingLine.Candidates.Single(c => c.Id == "top");
            Assert.True(top.Veto);
            Assert.NotEqual("top", r.PartingLine.Candidates[0].Id);
        }
    }
}

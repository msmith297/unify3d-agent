using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SwAgent.Mold.Geometry;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// The parting-line band from real tessellations (critique A7 review): drafted walls and chamfers tessellated the way
    /// SolidWorks does a cone (full-height triangles), sampled exactly as mold_part samples them. The band's extents must
    /// be the true face-edge heights, never a sample centroid's, and no face may lose its samples to the budget.
    /// </summary>
    public class GeometryPartingBandTests
    {
        private const double Mm = 0.001;
        private static readonly double Tan1 = Math.Tan(Math.PI / 180);
        private static readonly MoldFrame ZUp = MoldFrame.FromNormal(new double[] { 0, 0, 1 });

        private static double[] P(double x, double y, double z) => new[] { x * Mm, y * Mm, z * Mm };

        /// <summary>A surface of revolution from radius r0 at z0 to r1 at z1: n segments of two full-height triangles.</summary>
        private static void Cone(GeometryPartAnalysisTests.MeshPart m, int face, double r0, double r1, double z0, double z1, int n = 128)
        {
            for (var k = 0; k < n; k++)
            {
                double a0 = 2 * Math.PI * k / n, a1 = 2 * Math.PI * (k + 1) / n;
                m.Quad(face, P(r0 * Math.Cos(a0), r0 * Math.Sin(a0), z0), P(r0 * Math.Cos(a1), r0 * Math.Sin(a1), z0),
                    P(r1 * Math.Cos(a1), r1 * Math.Sin(a1), z1), P(r1 * Math.Cos(a0), r1 * Math.Sin(a0), z1));
            }
        }

        private static void Disc(GeometryPartAnalysisTests.MeshPart m, int face, double r, double z, bool up, int n = 128)
        {
            for (var k = 0; k < n; k++)
            {
                double a0 = 2 * Math.PI * k / n, a1 = 2 * Math.PI * (k + 1) / n;
                var a = P(r * Math.Cos(a0), r * Math.Sin(a0), z);
                var b = P(r * Math.Cos(a1), r * Math.Sin(a1), z);
                if (up) m.Tri(face, P(0, 0, z), a, b);
                else m.Tri(face, P(0, 0, z), b, a);
            }
        }

        /// <summary>Ø100 x 25 puck whose wall has 1 deg of draft for a base cavity (wider at the top): faces 0 top, 1 bottom, 2 wall.</summary>
        private static GeometryPartAnalysisTests.MeshPart DraftedPuck()
        {
            var m = new GeometryPartAnalysisTests.MeshPart();
            int top = m.AddFace(), bottom = m.AddFace(), wall = m.AddFace();
            Disc(m, top, 50, 25, true);
            Disc(m, bottom, 50 - 25 * Tan1, 0, false);
            Cone(m, wall, 50 - 25 * Tan1, 50, 0, 25);
            return m;
        }

        /// <summary>The same puck with a 3 mm x 45 deg chamfer on the top edge: faces 0 top, 1 bottom, 2 wall (0..22), 3 chamfer (22..25).</summary>
        private static GeometryPartAnalysisTests.MeshPart ChamferedPuck()
        {
            var m = new GeometryPartAnalysisTests.MeshPart();
            int top = m.AddFace(), bottom = m.AddFace(), wall = m.AddFace(), chamfer = m.AddFace();
            Disc(m, top, 47, 25, true);
            Disc(m, bottom, 50 - 22 * Tan1, 0, false);
            Cone(m, wall, 50 - 22 * Tan1, 50, 0, 22);
            Cone(m, chamfer, 50, 47, 22, 25);
            return m;
        }

        private static PartReport Analyze(GeometryPartAnalysisTests.MeshPart m, string fill = "base", int maxSamples = 20000) =>
            PartAnalysis.Analyze(m, ZUp, new PartAnalysisOptions { SampleMm = 1.0, Fill = fill, MaxSamples = maxSamples }, new List<(string, float[])>(), CancellationToken.None);

        [Fact]
        public void Drafted_puck_has_a_single_level_at_its_top_for_every_pour()
        {
            foreach (var fill in new[] { "base", "lid", "none" })
            {
                var r = Analyze(DraftedPuck(), fill).PartingLine;
                Assert.True(r.Planar);
                Assert.Equal(25, r.ZLo / Mm, 3);
                Assert.Equal(25, r.ZHi / Mm, 3);
                var c = Assert.Single(r.Candidates);
                Assert.Equal("top", c.Id);
                Assert.Equal(25, c.W / Mm, 3);
            }
        }

        [Fact]
        public void Chamfered_puck_parts_exactly_at_the_chamfer_edge()
        {
            var report = Analyze(ChamferedPuck());
            var r = report.PartingLine;
            Assert.True(r.Planar);
            Assert.Equal(22, r.ZLo / Mm, 3);
            Assert.Equal(22, r.ZHi / Mm, 3);
            var best = r.Candidates[0];
            Assert.Equal(22, best.W / Mm, 3);
            Assert.Empty(r.UndercutFaces);
            // Every face got samples: the chamfer's area is counted as lid-only, not spread over the other faces.
            Assert.InRange(r.AreaBySide[SampleSide.LidOnly] / 1e-6, Math.PI * 47 * 47 + Math.PI * (50 + 47) * 3 * Math.Sqrt(2) * 0.99, double.MaxValue);
        }

        [Fact]
        public void Chamfered_puck_on_a_tiny_budget_still_parts_at_the_chamfer_edge()
        {
            // 768 triangles, 60 samples: grouped per face and facing, the extents still come from every vertex.
            var report = Analyze(ChamferedPuck(), maxSamples: 60);
            var r = report.PartingLine;
            Assert.True(r.Planar);
            Assert.Equal(22, r.ZLo / Mm, 3);
            Assert.Equal(22, r.ZHi / Mm, 3);
            Assert.Contains(report.Json["notes"], n => ((string)n).Contains("sample budget"));
        }

        [Fact]
        public void Half_millimetre_conflict_between_drafted_walls_is_not_planar()
        {
            // A base-only wall up to 20 and a lid-only wall down to 19.5 (faces of a stepped part, not closed here).
            var tris = new List<(double[] A, double[] B, double[] C)>();
            void ConeTris(double r0, double r1, double z0, double z1, int n = 128)
            {
                for (var k = 0; k < n; k++)
                {
                    double a0 = 2 * Math.PI * k / n, a1 = 2 * Math.PI * (k + 1) / n;
                    var A = P(r0 * Math.Cos(a0), r0 * Math.Sin(a0), z0);
                    var B = P(r0 * Math.Cos(a1), r0 * Math.Sin(a1), z0);
                    var C = P(r1 * Math.Cos(a1), r1 * Math.Sin(a1), z1);
                    var D = P(r1 * Math.Cos(a0), r1 * Math.Sin(a0), z1);
                    tris.Add((A, B, C));
                    tris.Add((A, C, D));
                }
            }
            ConeTris(40 - 20 * Tan1, 40, 0, 20); // faces down: base-only
            var firstLid = tris.Count;
            ConeTris(30 + 10.5 * Tan1, 30, 19.5, 30); // faces up: lid-only
            var samples = SamplesOf(tris, t => t < firstLid ? 0 : 1, 20000);
            var r = PartingLine.Analyze(samples, 0.5, 1e-5);
            Assert.False(r.Planar);
            Assert.Equal(20, r.ZLo / Mm, 3);
            Assert.Equal(19.5, r.ZHi / Mm, 3);
            Assert.Equal(new[] { 0, 1 }, r.ConflictFaces);
        }

        [Fact]
        public void Small_budget_keeps_the_groove_undercuts()
        {
            // The F3 block's side groove on a budget far below its triangle count: the groove's walls are tiny faces,
            // which the largest-first sampler dropped; grouping per face keeps them.
            var m = GeometryPartAnalysisTests.GroovedBlock(out var gTop, out var gBottom);
            var r = Analyze(m, maxSamples: 12);
            Assert.Equal(new[] { gTop, gBottom }, r.PartingLine.UndercutFaces);
            Assert.Contains("Undercuts (2 face(s))", r.Text);
        }

        [Fact]
        public void Every_triangle_is_sampled_within_the_budget_and_the_area_is_exact()
        {
            // One big triangle and many small ones: the small ones each keep one sample.
            var tris = new List<Draft.SampleTriangle> { new Draft.SampleTriangle { Area = 1000, Face = 0, NW = 1, N0 = double.NaN, N1 = double.NaN, N2 = double.NaN } };
            for (var i = 0; i < 300; i++) tris.Add(new Draft.SampleTriangle { Area = 0.01, Face = 1 + i % 3, NW = -1, W0 = i, W1 = i, W2 = i + 1, N0 = double.NaN, N1 = double.NaN, N2 = double.NaN });
            var picks = Draft.Samples(tris, 1, 400, Math.Sin(0.5 * Math.PI / 180));
            Assert.InRange(picks.Count, 301, 400);
            Assert.Equal(tris.Sum(t => t.Area), picks.Sum(p => p.Area), 9);
            Assert.Equal(Enumerable.Range(0, 301), picks.Select(p => p.Tri).Distinct().OrderBy(x => x));
            // Base-facing extents: the highest vertex of a base-facing triangle.
            Assert.Equal(300, picks.Max(p => p.BaseMaxW), 9);

            // Over budget: 2,000 small triangles on one face plus 3 faces of one triangle, 50 samples.
            var many = new List<Draft.SampleTriangle>();
            for (var i = 0; i < 2000; i++) many.Add(new Draft.SampleTriangle { Area = 1, Face = 0, NW = 1, W0 = i, W1 = i + 0.5, W2 = i + 1, N0 = double.NaN, N1 = double.NaN, N2 = double.NaN });
            for (var f = 1; f <= 3; f++) many.Add(new Draft.SampleTriangle { Area = 0.001, Face = f, NW = -1, W0 = -f, W1 = -f, W2 = -f, N0 = double.NaN, N1 = double.NaN, N2 = double.NaN });
            var notes = new List<string>();
            var grouped = Draft.Samples(many, 1, 50, 0.01, notes);
            Assert.InRange(grouped.Count, 4, 50);
            Assert.Equal(many.Sum(t => t.Area), grouped.Sum(p => p.Area), 9);
            Assert.All(new[] { 0, 1, 2, 3 }, f => Assert.Contains(grouped, p => many[p.Tri].Face == f));
            Assert.Equal(0, grouped.Min(p => p.LidMinW), 9); // the face's lowest lid-facing vertex, though not sampled itself
            Assert.Equal(2000, grouped.Max(p => p.WHi), 9);
            Assert.Single(notes);
        }

        [Fact]
        public void Candidates_closer_than_the_sample_spacing_are_merged()
        {
            // Band [0, 25] with the largest outline 0.3 mm below the top: one candidate there, not two.
            var s = new List<PartSample>
            {
                new PartSample { Face = 0, W = 25, NW = 1, Area = 1, UpClear = true },
                new PartSample { Face = 1, W = 0, NW = -1, Area = 1, DownClear = true },
            };
            var r = PartingLine.Analyze(s, 0.5, 0.01, w => w <= 24.7 ? 1 + w : 0, null, 1.0);
            Assert.DoesNotContain(r.Candidates, c => c.Id == "outline");
            var r2 = PartingLine.Analyze(s, 0.5, 0.01, w => w <= 24.7 ? 1 + w : 0);
            Assert.Contains(r2.Candidates, c => c.Id == "outline");
        }

        [Fact]
        public void Critical_face_inside_the_outline_costs_score_but_does_not_veto()
        {
            // A Ø20 bore through a Ø60 x 40 bushing marked critical: a parting plane crosses it at a shut-off inside the
            // outline, so the plane is penalised, not vetoed. The outer wall marked critical vetoes.
            var m = GeometryPartAnalysisTests.Tube(30, 10, 0, 40, 90);
            var inner = PartAnalysis.Analyze(m, ZUp, new PartAnalysisOptions { SampleMm = 1.0 }, new List<(string, float[])> { ("S1", m.FaceTriangles(3)) }, CancellationToken.None);
            Assert.All(inner.PartingLine.Candidates, c => Assert.False(c.Veto));
            Assert.All(inner.PartingLine.Candidates, c => Assert.Equal(0.2, c.Penalty, 9));
            var outer = PartAnalysis.Analyze(m, ZUp, new PartAnalysisOptions { SampleMm = 1.0 }, new List<(string, float[])> { ("S2", m.FaceTriangles(2)) }, CancellationToken.None);
            Assert.All(outer.PartingLine.Candidates, c => Assert.True(c.Veto));
        }

        [Fact]
        public void Trial_pull_records_only_a_trial_line()
        {
            var r = PartAnalysis.Analyze(DraftedPuck(), ZUp, new PartAnalysisOptions { SampleMm = 1.0, Pull = new double[] { 1, 0, 0 } }, new List<(string, float[])>(), CancellationToken.None);
            Assert.Null(r.RecordPatch["frame"]);
            Assert.Null(r.RecordPatch["airTraps"]);
            Assert.NotNull(r.RecordPatch["part"]["pullTrials"]);
            var own = PartAnalysis.Analyze(GeometryPartAnalysisTests.Tube(50, 0, 0, 25, 64), ZUp, new PartAnalysisOptions { SampleMm = 1.0, BodyId = "PART-REF" },
                new List<(string, float[])>(), CancellationToken.None);
            Assert.NotNull(own.RecordPatch["frame"]);
            // Listed faces carry a portable locator (critique F1): body id, a point on the face (model mm) and its normal.
            var wall = own.Json["draft"]["faces"].Single(f => (string)f["id"] == "F2");
            Assert.Equal("PART-REF", (string)wall["bodyId"]);
            var pt = wall["point"].Select(x => (double)x).ToArray();
            Assert.Equal(50, Math.Sqrt(pt[0] * pt[0] + pt[1] * pt[1]), 0);
            var n = wall["normal"].Select(x => (double)x).ToArray();
            Assert.Equal(0, n[2], 6);
            Assert.True(n[0] * pt[0] + n[1] * pt[1] > 0); // outward
        }

        /// <summary>Samples of a triangle soup the way PartAnalysis makes them (triangle normals at the vertices).</summary>
        private static List<PartSample> SamplesOf(List<(double[] A, double[] B, double[] C)> tris, Func<int, int> face, int max)
        {
            var sinMin = Math.Sin(0.5 * Math.PI / 180);
            var normals = tris.Select(t => V3.Unit(V3.Cross(V3.Sub(t.B, t.A), V3.Sub(t.C, t.A)))).ToList();
            var st = tris.Select((t, i) => new Draft.SampleTriangle
            {
                Area = V3.Len(V3.Cross(V3.Sub(t.B, t.A), V3.Sub(t.C, t.A))) / 2, Face = face(i), NW = normals[i][2],
                W0 = t.A[2], W1 = t.B[2], W2 = t.C[2], N0 = double.NaN, N1 = double.NaN, N2 = double.NaN,
            }).ToList();
            return Draft.Samples(st, Mm * Mm, max, sinMin).Select(p =>
            {
                var t = tris[p.Tri];
                var pt = V3.Add(t.A, V3.Add(V3.Mul(V3.Sub(t.B, t.A), p.A), V3.Mul(V3.Sub(t.C, t.A), p.B)));
                return new PartSample
                {
                    Face = face(p.Tri), U = pt[0], V = pt[1], W = pt[2], NW = normals[p.Tri][2], Area = p.Area, UpClear = true, DownClear = true,
                    WLo = p.WLo, WHi = p.WHi, LidMinW = p.LidMinW, BaseMaxW = p.BaseMaxW,
                };
            }).ToList();
        }
    }
}

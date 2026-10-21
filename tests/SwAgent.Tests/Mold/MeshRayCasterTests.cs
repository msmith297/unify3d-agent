using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using SwAgent.Infrastructure;
using SwAgent.Mold.Geometry;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// mold_part's rays are cast in managed code (MeshRayCaster) since RayIntersections crashed SolidWorks on 2026-10-20.
    /// The caster must answer exactly like the brute-force caster of the analysis tests, and the analysis must come out the
    /// same through it. NativeFault turns a fault inside a SolidWorks call into an error instead of a closed SolidWorks.
    /// </summary>
    public class MeshRayCasterTests
    {
        private const int First = 1 | 2;           // TOPOLS | NORMALS
        private const int All = 1 | 2 | 4 | 8;     // + ENTRY_EXIT | UNBLOCK

        /// <summary>The mesh's triangles (model meters, the same doubles the brute-force caster uses) and their winding normals.</summary>
        private static MeshRayCaster CasterOf(GeometryPartAnalysisTests.MeshPart m)
        {
            var tris = new List<double[]>();
            for (var f = 0; f < m.FaceCount; f++)
            {
                var t = m.FaceTriangles(f);
                for (var k = 0; k + 8 < t.Length; k += 9) tris.Add(Enumerable.Range(k, 9).Select(i => (double)t[i]).ToArray());
            }
            return new MeshRayCaster(tris);
        }

        /// <summary>Random rays from a box around the part toward random points inside it.</summary>
        private static (List<double[]> Pts, List<double[]> Dirs) Rays(int n, int seed, double halfMm)
        {
            var rnd = new Random(seed);
            var pts = new List<double[]>();
            var dirs = new List<double[]>();
            double R() => (rnd.NextDouble() * 2 - 1) * halfMm * 0.001;
            for (var k = 0; k < n; k++)
            {
                var o = new[] { R() * 1.5, R() * 1.5, R() * 1.5 + 0.0125 };
                var to = new[] { R() * 0.6, R() * 0.6, rnd.NextDouble() * 0.025 };
                pts.Add(o);
                dirs.Add(V3.Unit(V3.Sub(to, o)));
            }
            return (pts, dirs);
        }

        private static void SameHits(List<RayHit> expected, List<RayHit> actual, int rays)
        {
            for (var r = 0; r < rays; r++)
            {
                var e = expected.Where(h => h.Ray == r).ToList();
                var a = actual.Where(h => h.Ray == r).ToList();
                // The brute-force caster reports a shared-edge crossing twice; the mesh caster once.
                var eDistinct = e.Where((h, i) => i == 0 || V3.Len(V3.Sub(h.Point, e[i - 1].Point)) > 1e-9 || h.Type != e[i - 1].Type).ToList();
                Assert.True(eDistinct.Count == a.Count, $"ray {r}: {eDistinct.Count} hits expected, {a.Count} found");
                for (var i = 0; i < a.Count; i++)
                {
                    Assert.True(V3.Len(V3.Sub(eDistinct[i].Point, a[i].Point)) < 1e-7, $"ray {r} hit {i}: point differs");
                    Assert.Equal(eDistinct[i].Type & 48, a[i].Type & 48);
                }
            }
        }

        [Fact]
        public void First_hits_match_brute_force_on_a_tube()
        {
            var tube = GeometryPartAnalysisTests.Tube(30, 12, 0, 25, 96);
            var (pts, dirs) = Rays(1500, 7, 40);
            SameHits(tube.Cast(pts, dirs, 0, pts.Count, First), CasterOf(tube).Cast(pts, dirs, 0, pts.Count, First), pts.Count);
        }

        [Fact]
        public void Every_crossing_with_enter_and_exit_matches_brute_force()
        {
            var block = GeometryPartAnalysisTests.GroovedBlock(out _, out _);
            var (pts, dirs) = Rays(1500, 11, 30);
            var brute = block.Cast(pts, dirs, 0, pts.Count, All);
            var mesh = CasterOf(block).Cast(pts, dirs, 0, pts.Count, All);
            Assert.True(mesh.Count > 500, "the rays should cross the block");
            SameHits(brute, mesh, pts.Count);
        }

        [Fact]
        public void A_batch_reports_rays_relative_to_its_start()
        {
            var tube = GeometryPartAnalysisTests.Tube(30, 0, 0, 25, 64);
            var down = new[] { 0.0, 0, -1 };
            var pts = new List<double[]> { new[] { 0.5, 0.5, 0.05 }, new[] { 0.0, 0, 0.05 }, new[] { 0.001, 0, 0.05 } };
            var dirs = new List<double[]> { down, down, down };
            var hits = CasterOf(tube).Cast(pts, dirs, 1, 2, All);
            Assert.Equal(new[] { 0, 0, 1, 1 }, hits.Select(h => h.Ray).ToArray());
            Assert.True(hits[0].Enter && hits[1].Exit, "top face entered, bottom face left");
            Assert.InRange(hits[0].Point[2], 0.025 - 1e-9, 0.025 + 1e-9);
        }

        [Fact]
        public void Analysis_is_the_same_through_the_mesh_caster()
        {
            foreach (var part in new[] { GeometryPartAnalysisTests.Tube(50, 20, 0, 25, 128), GeometryPartAnalysisTests.GroovedBlock(out _, out _) })
            {
                var o = new PartAnalysisOptions { SampleMm = 1.0 };
                var frame = MoldFrame.FromNormal(new double[] { 0, 0, 1 });
                var brute = PartAnalysis.Analyze(part, frame, o, new List<(string, float[])>(), CancellationToken.None);
                var viaCaster = PartAnalysis.Analyze(new CasterPart(part), frame, o, new List<(string, float[])>(), CancellationToken.None);
                Assert.NotNull(brute.Json["partingLine"] ?? brute.Json["part"]?["partingLine"] ?? brute.Json["projectedArea_mm2"]);
                Assert.Equal(brute.Json.ToString(), viaCaster.Json.ToString());
            }
        }

        [Fact]
        public void Twenty_thousand_rays_on_a_fine_mesh_are_quick()
        {
            var tube = GeometryPartAnalysisTests.Tube(30, 12, 0, 25, 2000); // 16,000 triangles
            var caster = CasterOf(tube);
            var (pts, dirs) = Rays(20000, 3, 40);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            caster.Cast(pts, dirs, 0, pts.Count, All);
            // About 0.3 s alone; brute force takes minutes. Generous for a loaded machine.
            Assert.True(watch.ElapsedMilliseconds < 15000, $"took {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>The fixture with its own geometry but rays through MeshRayCaster, as SwPartGeometry does.</summary>
        private sealed class CasterPart : IPartGeometry
        {
            private readonly GeometryPartAnalysisTests.MeshPart _m;
            private readonly MeshRayCaster _caster;

            public CasterPart(GeometryPartAnalysisTests.MeshPart m)
            {
                _m = m;
                _caster = CasterOf(m);
            }

            public string Name => _m.Name;
            public int FaceCount => _m.FaceCount;
            public float[] FaceTriangles(int face) => _m.FaceTriangles(face);
            public float[] FaceNormals(int face) => _m.FaceNormals(face);
            public double Volume => _m.Volume;
            public double Area => _m.Area;
            public double[] Box(MoldFrame frame) => _m.Box(frame);
            public string PersistRef(int face) => _m.PersistRef(face);
            public List<RayHit> Cast(IList<double[]> pts, IList<double[]> dirs, int start, int n, int opts) => _caster.Cast(pts, dirs, start, n, opts);
        }

        [Fact]
        public void No_production_code_calls_RayIntersections()
        {
            var src = Path.Combine(SweepFiles.Root, "src");
            var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => File.ReadAllLines(f).Any(l => !l.TrimStart().StartsWith("//") && (l.Contains(".RayIntersections(") || l.Contains("GetRayIntersectionsPoints("))))
                .ToList();
            Assert.Empty(offenders);
        }
    }

    public class NativeFaultTests : IDisposable
    {
        public NativeFaultTests() => NativeFault.Reset();

        public void Dispose() => NativeFault.Reset();

        [Fact]
        public void An_access_violation_becomes_an_error_and_blocks_model_tools()
        {
            var ex = Assert.Throws<NativeFaultException>(() => NativeFault.Run<object>("mold_part", () => throw new AccessViolationException("boom")));
            Assert.Contains("mold_part", ex.Message);
            Assert.Contains("Save As", ex.Message);
            Assert.StartsWith("mold_part at ", NativeFault.Tripped);
        }

        [Fact]
        public void A_fault_wrapped_by_reflection_is_recognised()
        {
            var method = typeof(NativeFaultTests).GetMethod(nameof(Faulting), BindingFlags.NonPublic | BindingFlags.Static);
            Assert.Throws<NativeFaultException>(() => NativeFault.Run("tool", () => method.Invoke(null, null)));
            Assert.NotNull(NativeFault.Tripped);
        }

        [Fact]
        public void Other_exceptions_pass_through_untouched()
        {
            Assert.Throws<InvalidOperationException>(() => NativeFault.Run<object>("tool", () => throw new InvalidOperationException()));
            Assert.Null(NativeFault.Tripped);
        }

        [Fact]
        public void Safe_run_records_a_fault_instead_of_letting_it_out()
        {
            Safe.Run("timer", () => throw new AccessViolationException());
            Assert.StartsWith("timer at ", NativeFault.Tripped);
        }

        private static void Faulting() => throw new AccessViolationException();
    }
}

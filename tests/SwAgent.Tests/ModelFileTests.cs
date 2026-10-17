using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SwAgent.ModelFiles;
using Xunit;

namespace SwAgent.Tests
{
    public class MeshReaderTests
    {
        [Fact]
        public void BinaryStl_IsWelded()
        {
            var r = MeshReader.ReadStl(new MemoryStream(Fixtures.BinaryStl(Fixtures.Box(0, 0, 0, 10, 20, 30))));
            Assert.Equal(12, r.Mesh.TriangleCount);
            Assert.Equal(8, r.Mesh.VertexCount);
            Assert.Equal('Z', r.Up);
            Assert.Contains("assumed", r.Units);
        }

        [Fact]
        public void BinaryStl_WithSolidHeader_IsStillBinary()
        {
            var bytes = Fixtures.BinaryStl(Fixtures.Box(0, 0, 0, 1, 1, 1), "solid exported by some CAD facet");
            Assert.True(MeshReader.IsBinaryStl(bytes));
            Assert.Equal(12, MeshReader.ReadStl(new MemoryStream(bytes)).Mesh.TriangleCount);
        }

        [Fact]
        public void AsciiStl()
        {
            var text = Fixtures.AsciiStl(Fixtures.Box(-5, -5, 0, 10, 10, 2));
            var r = MeshReader.ReadStl(new MemoryStream(Encoding.ASCII.GetBytes(text)));
            Assert.Equal(12, r.Mesh.TriangleCount);
            Assert.Equal(8, r.Mesh.VertexCount);
        }

        [Fact]
        public void ThreeMf_AppliesBuildTransformsAndUnits()
        {
            var r = MeshReader.Read3mf(new MemoryStream(Fixtures.ThreeMf("inch", 1, 2)));
            Assert.Equal(24, r.Mesh.TriangleCount);
            Assert.Equal(2, r.Objects);
            var s = MeshStats.Compute(r.Mesh);
            Assert.Equal(2, s.Shells.Count);
            Assert.Equal(25.4 * 3, s.SizeX, 3); // two 1" cubes, the second moved 2" along X
            Assert.Equal(25.4, s.SizeY, 3);
            Assert.Contains("inch", r.Units);
        }

        [Fact]
        public void Obj_QuadsAndNegativeIndices()
        {
            var obj = "o plate\nv 0 0 0\nv 10 0 0\nv 10 10 0\nv 0 10 0\nf 1 2 3 4\nf -4 -2 -1\n";
            var r = MeshReader.ReadObj(new MemoryStream(Encoding.ASCII.GetBytes(obj)));
            Assert.Equal(3, r.Mesh.TriangleCount);
            Assert.Equal(1, r.Objects);
        }

        [Fact]
        public void Combine_IsChildThenParent()
        {
            var move = new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 0, 0 };
            var rotate = new double[] { 0, 1, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0 }; // 90 deg about Z (row-vector)
            var m = MeshReader.Combine(move, rotate);
            // (0,0,0) moved to (5,0,0), then rotated to (0,5,0).
            Assert.Equal(0, m[9], 9);
            Assert.Equal(5, m[10], 9);
        }
    }

    public class MeshStatsTests
    {
        private static Mesh Build(params System.Collections.Generic.List<float[]>[] boxes)
        {
            var m = new Mesh();
            foreach (var t in boxes.SelectMany(b => b)) m.AddTriangle(t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8]);
            m.Seal();
            return m;
        }

        [Fact]
        public void Box_VolumeAreaClosed()
        {
            var s = MeshStats.Compute(Build(Fixtures.Box(0, 0, 0, 10, 20, 30)));
            Assert.Equal(6000, s.Volume, 3);
            Assert.Equal(2 * (200 + 600 + 300), s.Area, 3);
            Assert.True(s.Closed);
            Assert.Single(s.Shells);
            Assert.Equal(30, s.SizeZ, 6);
        }

        [Fact]
        public void TwoBoxes_AreTwoShells_LargestFirst()
        {
            var s = MeshStats.Compute(Build(Fixtures.Box(0, 0, 0, 1, 1, 1), Fixtures.Box(10, 0, 0, 2, 2, 2)));
            Assert.Equal(2, s.Shells.Count);
            Assert.Equal(8, s.Shells[0].Volume, 6);
            Assert.Equal(1, s.Shells[1].Volume, 6);
            Assert.Equal(12, s.SizeX, 6);
            // Triangle ids follow the new order: the first 12 triangles (small box) are shell 1.
            Assert.All(s.TriangleShell.Take(12), id => Assert.Equal(1, id));
        }

        [Fact]
        public void InvertedBox_StillPositiveVolume()
        {
            var flipped = Fixtures.Box(0, 0, 0, 2, 2, 2).Select(t => new[] { t[0], t[1], t[2], t[6], t[7], t[8], t[3], t[4], t[5] }).ToList();
            Assert.Equal(8, MeshStats.Compute(Build(flipped)).Volume, 6);
        }

        [Fact]
        public void OpenMesh_ReportsOpenEdges()
        {
            var tris = Fixtures.Box(0, 0, 0, 1, 1, 1).Skip(1).ToList();
            var s = MeshStats.Compute(Build(tris));
            Assert.False(s.Closed);
            Assert.Equal(3, s.OpenEdges);
        }
    }

    public class MeshRendererTests
    {
        [Fact]
        public void Sheet_HasSixTilesWithGeometryAndOutlines()
        {
            var m = new Mesh();
            foreach (var t in Fixtures.Box(0, 0, 0, 40, 20, 10)) m.AddTriangle(t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8]);
            var stats = MeshStats.Compute(m);
            using (var sheet = MeshRenderer.RenderSheet(m, stats.TriangleShell, 'Y', 200, 150))
            {
                Assert.Equal(600, sheet.Width);
                Assert.Equal(300, sheet.Height);
                for (var tile = 0; tile < 6; tile++)
                {
                    int ox = tile % 3 * 200, oy = tile / 3 * 150;
                    // The model is centred in every tile: the middle is shaded geometry, not the light background.
                    var c = sheet.GetPixel(ox + 100, oy + 75);
                    Assert.True(c.R < 230 || c.B < 230, $"tile {tile} centre is background ({c})");
                    var dark = 0;
                    for (var y = oy + 20; y < oy + 140; y += 2)
                    for (var x = ox + 10; x < ox + 190; x += 2)
                    {
                        var p = sheet.GetPixel(x, y);
                        if (p.R < 110 && p.G < 110 && p.B < 120) dark++;
                    }
                    Assert.True(dark > 10, $"tile {tile} has no outlines");
                }
            }
        }

        /// <summary>Set SWAGENT_TEST_DUMP to a folder to look at a rendered sheet (an L-bracket with a separate pin).</summary>
        [Fact]
        public void DumpSampleSheet()
        {
            var dir = Environment.GetEnvironmentVariable("SWAGENT_TEST_DUMP");
            if (string.IsNullOrEmpty(dir)) return;
            var m = new Mesh();
            foreach (var t in Fixtures.Box(0, 0, 0, 60, 8, 40).Concat(Fixtures.Box(0, 8, 0, 8, 40, 40)).Concat(Fixtures.Box(30, 8, 15, 6, 20, 6)))
                m.AddTriangle(t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8]);
            var stats = MeshStats.Compute(m);
            using (var sheet = MeshRenderer.RenderSheet(m, stats.TriangleShell, 'Y')) sheet.Save(Path.Combine(dir, "sheet.png"));
        }

        [Fact]
        public void FrontView_IsWiderThanTall_ForAWideBox()
        {
            var m = new Mesh();
            foreach (var t in Fixtures.Box(0, 0, 0, 40, 10, 10)) m.AddTriangle(t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8]);
            using (var img = MeshRenderer.RenderView(m, null, MeshRenderer.Six[1], 'Y', 200, 200))
            {
                int Extent(Func<int, Color> at)
                {
                    var n = 0;
                    for (var i = 0; i < 200; i++)
                    {
                        var p = at(i);
                        if (p.R < 225) n++;
                    }
                    return n;
                }
                var wide = Extent(i => img.GetPixel(i, 100));
                var tall = Extent(i => img.GetPixel(100, i));
                Assert.True(wide > 3 * tall, $"wide {wide}, tall {tall}");
            }
        }
    }

    public class ModelFileContextTests
    {
        [Fact]
        public async Task Stl_IsDescribedWithoutSolidWorks()
        {
            var path = Fixtures.TempFile(".stl", Fixtures.BinaryStl(Fixtures.Box(0, 0, 0, 120, 80, 10)));
            try
            {
                var report = await ModelFileContext.DescribeAsync(null, path);
                Assert.Null(report.Error);
                Assert.Contains("Size: 120 x 80 x 10 mm", report.Summary);
                Assert.Contains("Volume 96,000 mm³", report.Summary);
                Assert.Contains("1 separate shell", report.Summary);
                Assert.Contains("six views", report.Summary);
                Assert.NotNull(report.Views);
                using (var img = new Bitmap(new MemoryStream(Convert.FromBase64String(report.Views))))
                {
                    Assert.Equal(1200, img.Width);
                    Assert.Equal(600, img.Height);
                }
                Assert.Same(report, await ModelFileContext.DescribeAsync(null, path)); // cached
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task Step_WithoutSolidWorks_ExplainsAndKeepsThePath()
        {
            var path = Fixtures.TempFile(".step", Encoding.ASCII.GetBytes("ISO-10303-21;"));
            try
            {
                var report = await ModelFileContext.DescribeAsync(null, path);
                Assert.NotNull(report.Error);
                Assert.Contains("SolidWorks", report.Summary);
                Assert.Contains(path, report.Summary);
                Assert.Null(report.Views);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public async Task MissingFile_IsAnError()
        {
            var report = await ModelFileContext.DescribeAsync(null, @"C:\does\not\exist.stl");
            Assert.NotNull(report.Error);
        }

        [Fact]
        public async Task FileAttachments_KindsByExtension()
        {
            var png = Fixtures.TempFile(".png", new byte[] { 137, 80, 78, 71 });
            var stl = Fixtures.TempFile(".stl", Fixtures.BinaryStl(Fixtures.Box(0, 0, 0, 1, 1, 1)));
            try
            {
                var image = await FileAttachments.EntryAsync(null, png);
                Assert.Equal("image", (string)image["kind"]);
                Assert.Equal("image/png", (string)image["mediaType"]);
                var pending = await FileAttachments.EntryAsync(null, stl, describeModels: false);
                Assert.Equal("model", (string)pending["kind"]);
                Assert.Null(pending["summary"]);
                var model = await FileAttachments.EntryAsync(null, stl);
                Assert.NotNull((string)model["views"]);
                Assert.Contains("STL mesh", (string)model["summary"]);
            }
            finally
            {
                File.Delete(png);
                File.Delete(stl);
            }
        }

        [Fact]
        public void Summary_PrefersSolidWorksMassProperties()
        {
            var m = new Mesh();
            foreach (var t in Fixtures.Box(0, 0, 0, 10, 10, 10)) m.AddTriangle(t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7], t[8]);
            var facts = new SolidWorksFacts { How = "SolidWorks (opened hidden)", DocumentType = "part", SolidBodies = 1, Volume = 999.5, Area = 600, Mass = 0.0027, Material = "6061-T6 (SS)" };
            var text = ModelReport.Summarize("block.step", "STEP", 2048, "mm (document unit: mm)", MeshStats.Compute(m), facts, 'Y', true);
            Assert.Contains("Read by: SolidWorks (opened hidden)", text);
            Assert.Contains("Structure: part, 1 solid body", text);
            Assert.Contains("volume 999.5 mm³", text);
            Assert.Contains("material 6061-T6 (SS)", text);
            Assert.DoesNotContain("closed mesh", text);
            Assert.Contains("Y up", text);
        }
    }
}

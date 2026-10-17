using System.Drawing;
using System.Linq;
using SwAgent.ModelFiles;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>The renderer options used for live SolidWorks models: per-face colour, highlight and focus framing.</summary>
    public class LiveRenderTests
    {
        /// <summary>A 100 x 50 x 20 mm box, two triangles per face, faces numbered 0..5 (+Y top is face 2).</summary>
        private static (Mesh Mesh, int[] TriFace) Box()
        {
            var m = new Mesh();
            float X = 100, Y = 20, Z = 50;
            var faces = new[]
            {
                new[] { 0f, 0, Z, X, 0, Z, X, Y, Z, 0, Y, Z }, // front (+Z)
                new[] { 0f, 0, 0, 0, Y, 0, X, Y, 0, X, 0, 0 }, // back
                new[] { 0f, Y, 0, 0, Y, Z, X, Y, Z, X, Y, 0 }, // top (+Y)
                new[] { 0f, 0, 0, X, 0, 0, X, 0, Z, 0, 0, Z }, // bottom
                new[] { X, 0, 0, X, Y, 0, X, Y, Z, X, 0, Z }, // right
                new[] { 0f, 0, 0, 0, 0, Z, 0, Y, Z, 0, Y, 0 }, // left
            };
            var triFace = new int[12];
            for (var f = 0; f < 6; f++)
            {
                var q = faces[f];
                m.AddTriangle(q[0], q[1], q[2], q[3], q[4], q[5], q[6], q[7], q[8]);
                m.AddTriangle(q[0], q[1], q[2], q[6], q[7], q[8], q[9], q[10], q[11]);
                triFace[f * 2] = triFace[f * 2 + 1] = f;
            }
            m.Seal();
            return (m, triFace);
        }

        private static int CountBlue(Bitmap b)
        {
            var n = 0;
            for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
            {
                var c = b.GetPixel(x, y);
                if (c.B > c.R + 60 && c.B > c.G + 20) n++;
            }
            return n;
        }

        [Fact]
        public void HighlightedFaceIsDrawnInTheHighlightColour()
        {
            var (mesh, triFace) = Box();
            var grey = Enumerable.Repeat(unchecked((int)0xFFC6CACF), 12).ToArray();
            var iso = MeshRenderer.Six[0];
            using (var plain = MeshRenderer.RenderView(mesh, triFace, iso, 'Y', 300, 220, default, new MeshRenderer.RenderOptions { TriColor = grey }))
            using (var lit = MeshRenderer.RenderView(mesh, triFace, iso, 'Y', 300, 220, default, new MeshRenderer.RenderOptions
            {
                TriColor = grey,
                Highlight = triFace.Select(f => f == 2).ToArray(), // the top face, visible from isometric
            }))
            {
                Assert.True(CountBlue(plain) < 50, "a grey model has no highlight-blue pixels");
                Assert.True(CountBlue(lit) > 2000, "the highlighted top face is drawn blue");
            }
        }

        [Fact]
        public void FocusFramesTheChosenFaces()
        {
            var (mesh, triFace) = Box();
            var front = new MeshRenderer.ViewSpec { Name = "front", Dir = new[] { 0.0, 0, 1 }, Up = new[] { 0.0, 1, 0 } };
            int Model(Bitmap b)
            {
                var n = 0;
                for (var y = 0; y < b.Height; y++)
                for (var x = 0; x < b.Width; x++)
                {
                    var c = b.GetPixel(x, y);
                    if (c.R < 225) n++; // not background
                }
                return n;
            }
            using (var whole = MeshRenderer.RenderView(mesh, triFace, front, 'Y', 300, 220))
            using (var focused = MeshRenderer.RenderView(mesh, triFace, front, 'Y', 300, 220, default, new MeshRenderer.RenderOptions
            {
                Focus = triFace.Select(f => f == 0).ToArray(), // the front face: same extent here, so a similar size
            }))
            {
                Assert.True(Model(whole) > 5000);
                Assert.True(Model(focused) > 3000);
            }
        }
    }
}

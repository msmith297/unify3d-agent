using SwAgent.UI;
using Xunit;

namespace SwAgent.Tests
{
    public class OverlayTests
    {
        /// <summary>
        /// IModelView.Transform and the pixel positions SolidWorks itself returned for it (MathPoint.MultiplyTransform),
        /// read from a live session: the overlay's projection must land on the same pixels.
        /// </summary>
        [Theory]
        [InlineData(-0.1651, 0.0378, 0.0, 2407.5204, 523.1242)]
        [InlineData(-0.1727, 0.0371, -0.0065, 2465.5141, 523.1242)]
        [InlineData(-0.1691, 0.0462, 0.0037, 2407.5204, 465.1306)]
        public void Projection_matches_solidworks(double x, double y, double z, double px, double py)
        {
            var t = new[] { -0.7583, 0.4036, -0.512, -0.078, -0.8359, -0.5432, -0.6473, -0.372, 0.6654, 1698.6404, 1092.9793, -1724.1099, 5799.3631, 0, 0, 0 };
            var p = SceneOverlay.Project(t, new[] { x, y, z });
            Assert.InRange(p.X, px - 3, px + 3); // the probe printed the transform rounded to 4 decimals
            Assert.InRange(p.Y, py - 3, py + 3);
        }

        [Fact]
        public void Ray_hits_are_read_by_record_length()
        {
            // 2 hits, 9 values each: [body, ray, type, x, y, z, nx, ny, nz]
            var data = new double[] { 0, 3, 1, 0.1, 0.2, 0.3, 0, 0, 1, 1, 7, 1, 0.4, 0.5, 0.6, 0, 1, 0 };
            Assert.Equal(new[] { 3, 7 }, SceneOverlay.RayIndexes(data, 2, 10, 2));
            // Data that fits no known layout must not be guessed at.
            Assert.Throws<System.InvalidOperationException>(() => System.Linq.Enumerable.ToList(SceneOverlay.RayIndexes(new double[] { 0.5, 2.5, 1 }, 1, 10, 2)));
        }
    }
}

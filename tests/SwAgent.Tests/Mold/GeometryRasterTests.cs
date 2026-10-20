using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Mold.Geometry;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>Projection raster, marching squares, Douglas-Peucker and the distance transform (design 6.2 RasterTests).</summary>
    public class GeometryRasterTests
    {
        internal static IEnumerable<double[]> Disk(double r, int n, double cu = 0, double cv = 0)
        {
            for (var k = 0; k < n; k++)
            {
                double a0 = 2 * Math.PI * k / n, a1 = 2 * Math.PI * (k + 1) / n;
                yield return new[] { cu, cv, cu + r * Math.Cos(a0), cv + r * Math.Sin(a0), cu + r * Math.Cos(a1), cv + r * Math.Sin(a1) };
            }
        }

        internal static IEnumerable<double[]> Rect(double u0, double v0, double u1, double v1)
        {
            yield return new[] { u0, v0, u1, v0, u1, v1 };
            yield return new[] { u0, v0, u1, v1, u0, v1 };
        }

        [Fact]
        public void Square_area_and_perimeter()
        {
            var g = Grid2.Covering(-20, -20, 20, 20, 0.25);
            var m = Raster.Fill(g, Rect(-20, -20, 20, 20));
            Assert.InRange(Raster.Area(m, g.H), 1600 * 0.995, 1600 * 1.005);
            var loops = Raster.Outlines(m, g);
            Assert.Single(loops);
            var simple = Raster.Clean(loops[0], g.H);
            Assert.InRange(Raster.Length(simple), 160 * 0.99, 160 * 1.01);
            Assert.True(Raster.SignedArea(simple) > 0, "outer loops are counter-clockwise");
            Assert.True(simple.Count <= 12, $"a square simplifies to a few points, got {simple.Count}");
        }

        [Fact]
        public void Circle_area_within_half_percent_and_perimeter_within_one_percent()
        {
            const double r = 50; // the F1 puck, mm
            var h = Math.Max(0.1, Math.Min(1.0, 2 * r / 400));
            var g = Grid2.Covering(-r, -r, r, r, h);
            var m = Raster.Fill(g, Disk(r, 720));
            var area = Raster.Area(m, g.H);
            Assert.InRange(area, Math.PI * r * r * 0.995, Math.PI * r * r * 1.005);
            var loop = Raster.Clean(Raster.Outlines(m, g).Single(), h);
            Assert.InRange(Raster.Length(loop), 2 * Math.PI * r * 0.99, 2 * Math.PI * r * 1.01);
        }

        [Fact]
        public void Ring_has_an_outer_loop_and_a_clockwise_hole()
        {
            var g = Grid2.Covering(-30, -30, 30, 30, 0.5);
            var m = Raster.Fill(g, Disk(30, 360));
            var hole = Raster.Fill(g, Disk(10, 360));
            for (var i = 0; i < g.Nu; i++)
                for (var j = 0; j < g.Nv; j++)
                    if (hole[i, j]) m[i, j] = false;
            var loops = Raster.Outlines(m, g).Select(l => Raster.Clean(l, 0.5)).OrderByDescending(l => Math.Abs(Raster.SignedArea(l))).ToList();
            Assert.Equal(2, loops.Count);
            Assert.True(Raster.SignedArea(loops[0]) > 0);
            Assert.True(Raster.SignedArea(loops[1]) < 0);
            Assert.InRange(Math.Abs(Raster.SignedArea(loops[1])), Math.PI * 100 * 0.98, Math.PI * 100 * 1.02);
            var outside = Raster.Outside(m);
            Assert.False(outside[g.Nu / 2, g.Nv / 2]); // the hole is not outside
            Assert.True(outside[0, 0]);
        }

        [Fact]
        public void Triangles_seen_edge_on_add_nothing()
        {
            var g = Grid2.Covering(0, 0, 10, 10, 1);
            var m = Raster.Fill(g, new[] { new double[] { 0, 0, 10, 10, 5, 5 } });
            Assert.Equal(0, Raster.Count(m));
        }

        [Fact]
        public void Distance_transform_is_euclidean()
        {
            var region = new bool[21, 21];
            for (var i = 1; i < 20; i++)
                for (var j = 1; j < 20; j++)
                    region[i, j] = true;
            var d = Raster.DistanceToOutside(region);
            Assert.Equal(10, d[10, 10], 6); // 10 cells to the nearest unmarked cell (row 0 or 20)
            Assert.Equal(1, d[1, 5], 6);
            Assert.Equal(0, d[0, 0], 6);
            // A single marked cell surrounded by unmarked cells.
            var one = new bool[5, 5];
            one[2, 2] = true;
            Assert.Equal(1, Raster.DistanceToOutside(one)[2, 2], 6);
        }

        [Fact]
        public void Components_are_eight_connected_largest_first()
        {
            var m = new bool[6, 6];
            m[0, 0] = m[1, 1] = m[2, 2] = true; // diagonal chain: one component
            m[5, 5] = true;
            var c = Raster.Components(m);
            Assert.Equal(2, c.Count);
            Assert.Equal(3, c[0].Count);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Mold.Geometry;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>Fragile steel from column intervals (fixtures F2 and F5 in their 2.5D form).</summary>
    public class GeometryFragileTests
    {
        private const double H = 0.0005; // 0.5 mm cells

        /// <summary>A column map from a solid-interval function of (u, v) in meters.</summary>
        private static ColumnMap Map(double half, Func<double, double, List<(double, double)>> at)
        {
            var g = Grid2.Covering(-half, -half, half, half, H, 2);
            var m = new ColumnMap(g);
            for (var i = 0; i < g.Nu; i++)
                for (var j = 0; j < g.Nv; j++)
                    m.Cells[i, j] = at(g.CU(i), g.CV(j));
            return m;
        }

        private static ColumnMap Bushing(double outerD, double innerD, double height) =>
            Map(outerD / 2, (u, v) =>
            {
                var r = Math.Sqrt(u * u + v * v);
                return r <= outerD / 2 && r >= innerD / 2 ? new List<(double, double)> { (0, height) } : null;
            });

        [Fact]
        public void Bushing_core_pin_D20_H40_is_reported_but_not_flagged()
        {
            var f = Fragile.Find(Bushing(0.060, 0.020, 0.040));
            var pin = Assert.Single(f);
            Assert.Equal("core_pin", pin.Kind);
            Assert.Equal("through", pin.Side);
            Assert.InRange(pin.D, 0.0195, 0.0205);
            Assert.Equal(0.040, pin.H, 9);
            Assert.True(pin.Enclosed);
            Assert.False(pin.Flagged);
        }

        [Fact]
        public void Bushing_core_pin_D9_is_flagged()
        {
            var pin = Assert.Single(Fragile.Find(Bushing(0.060, 0.009, 0.040)));
            Assert.True(pin.Flagged);
            Assert.Contains("H/D", pin.Reason);
        }

        [Fact]
        public void Thin_pin_is_flagged_for_its_width()
        {
            var pin = Assert.Single(Fragile.Find(Bushing(0.030, 0.0025, 0.004)));
            Assert.True(pin.Flagged);
            Assert.Contains("wide", pin.Reason);
        }

        [Fact]
        public void Cup_plug_is_a_pocket_core_from_the_lid()
        {
            // Cup: 40 mm across, 10 mm floor, 30 mm deep pocket 20 mm across.
            var f = Fragile.Find(Map(0.020, (u, v) =>
            {
                var r = Math.Sqrt(u * u + v * v);
                if (r > 0.020) return null;
                return r < 0.010 ? new List<(double, double)> { (0, 0.010) } : new List<(double, double)> { (0, 0.040) };
            }));
            var plug = Assert.Single(f);
            Assert.Equal("pocket_core", plug.Kind);
            Assert.Equal("lid", plug.Side);
            Assert.Equal(0.030, plug.H, 9);
            Assert.False(plug.Flagged); // H/D = 30 / 20 = 1.5
        }

        [Fact]
        public void Steel_between_close_ribs_is_a_flagged_wall_and_a_chamfer_is_not()
        {
            // 40 x 40 mm slab 5 mm thick with two 2 mm ribs 15 mm high, 2 mm apart, open to the edges; bottom edge chamfered 1 mm.
            var f = Fragile.Find(Map(0.020, (u, v) =>
            {
                if (Math.Abs(u) > 0.020 || Math.Abs(v) > 0.020) return null;
                var edge = 0.020 - Math.Max(Math.Abs(u), Math.Abs(v));
                var bottom = edge < 0.001 ? 0.001 - edge : 0;
                var rib = Math.Abs(u) >= 0.001 && Math.Abs(u) <= 0.003;
                return new List<(double, double)> { (bottom, rib ? 0.015 : 0.005) };
            }));
            var wall = Assert.Single(f, x => x.Flagged);
            Assert.Equal("steel_wall", wall.Kind);
            Assert.InRange(wall.D, 0.0014, 0.0025);
            Assert.InRange(Math.Abs(wall.U), 0, 0.0005);
            Assert.DoesNotContain(f, x => x.Side == "base"); // the chamfer round the bottom edge isn't fragile steel
        }

        [Fact]
        public void Plain_puck_has_no_fragile_steel()
        {
            Assert.Empty(Fragile.Find(Map(0.050, (u, v) => u * u + v * v <= 0.0025 ? new List<(double, double)> { (0, 0.025) } : null)));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Mold.Geometry;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>Ray-hit parsing, column intervals, the mold frame and body ids.</summary>
    public class GeometryColumnsTests
    {
        [Fact]
        public void Parses_hit_records_with_and_without_normals()
        {
            var nine = new double[] { 0, 3, 17, 0.1, 0.2, 0.3, 0, 0, 1, 0, 1, 33, 0.4, 0.5, 0.6, 0, 0, -1 };
            var hits = Columns.Parse(nine, 2, 5, 1, true);
            Assert.Equal(2, hits.Count);
            Assert.Equal(3, hits[0].Ray);
            Assert.True(hits[0].Enter);
            Assert.True(hits[1].Exit);
            Assert.Equal(-1, hits[1].Normal[2]);
            var six = new double[] { 0, 1, 1, 0.1, 0.2, 0.3 };
            var h6 = Columns.Parse(six, 1, 2, 1, false);
            Assert.Equal(0.3, h6[0].Point[2]);
            Assert.Null(h6[0].Normal);
            Assert.Throws<InvalidOperationException>(() => Columns.Parse(new double[] { 5, 9, 1, 0, 0, 0 }, 1, 2, 1, false));
        }

        [Fact]
        public void Typed_hits_give_intervals_top_first()
        {
            // A cup wall column: enter 30, exit 25, enter 10, exit 0.
            var iv = Columns.Intervals(new[] { (30.0, 17), (25.0, 33), (10.0, 17), (0.0, 33) });
            Assert.Equal(new[] { (25.0, 30.0), (0.0, 10.0) }, iv);
        }

        [Fact]
        public void Untyped_hits_pair_in_order_and_drop_tangent_touches()
        {
            var iv = Columns.Intervals(new[] { (0.0, 1), (30.0, 1), (10.0, 1), (25.0, 1) });
            Assert.Equal(new[] { (25.0, 30.0), (0.0, 10.0) }, iv);
            // A ray grazing a sphere's equator: one double hit, no interval.
            Assert.Empty(Columns.Intervals(new[] { (5.0, 2), (5.0, 2) }));
            // A hit on an edge reported twice is one crossing.
            var dup = Columns.Intervals(new[] { (20.0, 17), (20.0, 17), (0.0, 33) });
            Assert.Equal(new[] { (0.0, 20.0) }, dup);
        }

        [Fact]
        public void Column_map_ceiling_section_and_internal_ceilings()
        {
            var g = new Grid2(0, 0, 1, 2, 1);
            var map = new ColumnMap(g);
            map.Cells[0, 0] = new List<(double, double)> { (0, 10) };
            map.Cells[1, 0] = new List<(double, double)> { (8, 10), (0, 3) };
            Assert.Equal(10, map.Ceiling(false)[1, 0]);
            Assert.Equal(0, map.Ceiling(true)[1, 0]); // lid pour: -(bottom)
            Assert.Equal(2, map.SectionArea(9));
            Assert.Equal(1, map.SectionArea(5));
            var internals = map.InternalCeilings(false);
            Assert.Single(internals);
            Assert.Equal(3, internals[0].CeilingW);
        }

        [Fact]
        public void Frame_from_the_top_plane_is_right_handed_along_model_axes()
        {
            var z = MoldFrame.FromNormal(new double[] { 0, 0, 1 });
            Assert.Equal(new double[] { 1, 0, 0 }, z.U);
            Assert.Equal(new double[] { 0, 1, 0 }, z.V);
            var y = MoldFrame.FromNormal(new double[] { 0, 1, 0 }); // the default SolidWorks template is Y-up
            Assert.Equal(1, V3.Dot(V3.Cross(y.U, y.V), y.W), 12);
            Assert.True(y.AxisAligned);
            // Per-axis shrink along U, V, W lands on the right model axes.
            var s = y.ModelScaleFactors(1.01, 1.02, 1.03);
            Assert.Equal(1.03, s[1]); // W = model Y
            Assert.Equal(1.01, s[0]); // U = model X
            Assert.Equal(1.02, s[2]); // V = model -Z
            var tilted = MoldFrame.FromNormal(new double[] { 1, 1, 0 });
            Assert.False(tilted.AxisAligned);
            Assert.Equal(1, V3.Dot(V3.Cross(tilted.U, tilted.V), tilted.W), 12);
            Assert.Throws<InvalidOperationException>(() => tilted.ModelScaleFactors(1, 1, 1.1));
            Assert.Equal(new[] { 1.1, 1.1, 1.1 }, tilted.ModelScaleFactors(1.1, 1.1, 1.1));
            // Round trip.
            var f = z.At(new[] { 0.01, 0.02, 0.03 });
            var p = new[] { 0.5, -0.25, 0.125 };
            var back = f.ToModel(f.ToFrame(p));
            for (var k = 0; k < 3; k++) Assert.Equal(p[k], back[k], 12);
        }

        [Fact]
        public void Body_ids_follow_geometry_not_creation_order()
        {
            var bodies = new List<BodyRoles.Entry>
            {
                new BodyRoles.Entry { Tag = "Cut-Extrude7", Folder = "Mold - Base", Centroid = new[] { 0.1, 0, 0 } },
                new BodyRoles.Entry { Tag = "Boss1", Folder = "Mold - Base", Centroid = new[] { -0.1, 0, 0 } },
                new BodyRoles.Entry { Tag = "Lid", Folder = "mold - lid", Centroid = new[] { 0, 0, 0.05 } },
                new BodyRoles.Entry { Tag = "Ref", Folder = "Mold - Reference", Centroid = new[] { 0, 0, 0.01 } },
                new BodyRoles.Entry { Tag = "Pin2", Folder = "Mold - Inserts", Centroid = new[] { 0.1, 0.01, 0 } },
                new BodyRoles.Entry { Tag = "Pin1", Folder = "Mold - Inserts", Centroid = new[] { -0.1, 0.01, 0 } },
                new BodyRoles.Entry { Tag = "Pin3", Folder = "Mold - Inserts", Centroid = new[] { 0.1, -0.01, 0 } },
                new BodyRoles.Entry { Tag = "Loose", Folder = null, Centroid = new[] { 0.0, 0, 0 } },
                // Same U within 0.1 mm: V decides, not noise in U.
                new BodyRoles.Entry { Tag = "Stop-b", Folder = "Mold - Stops", Centroid = new[] { 0.00004, 0.02, 0 } },
                new BodyRoles.Entry { Tag = "Stop-a", Folder = "Mold - Stops", Centroid = new[] { 0.00006, -0.02, 0 } },
            };
            var ids = BodyRoles.Assign(bodies, new[] { ("C1", -0.1, 0.0), ("C2", 0.1, 0.0) }).ToDictionary(b => (string)b.Tag, b => b.Id);
            Assert.Equal("BASE-1", ids["Boss1"]);
            Assert.Equal("BASE-2", ids["Cut-Extrude7"]);
            Assert.Equal("LID-1", ids["Lid"]);
            Assert.Equal("PART-REF", ids["Ref"]);
            Assert.Equal("INS-C1-1", ids["Pin1"]);
            Assert.Equal("INS-C2-1", ids["Pin3"]);
            Assert.Equal("INS-C2-2", ids["Pin2"]);
            Assert.Equal("BODY-1", ids["Loose"]);
            Assert.Equal("STOP-1", ids["Stop-a"]);
            Assert.Equal("STOP-2", ids["Stop-b"]);
            Assert.Equal("base", BodyRoles.ForName("BASE").Name);
            Assert.Null(BodyRoles.RoleOf("Solid Bodies"));
        }
    }
}

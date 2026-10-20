using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SwAgent.Mold.Geometry;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// The flash-groove layout ported from the user's macro, against its verified test plate: a 10 x 8 x 1.5 in plate with a
    /// 5 x 3.5 in cavity (R0.375 in corners) and 1.5 in lift notches mid short sides gave lip 0.125 in, R0.25 in corners,
    /// 3 runners per long side and 2 per short side, the short-side runners shifted 0.189 in to clear the notches by 0.25 in.
    /// </summary>
    public class GeometryFlashLayoutTests
    {
        private const double In = 0.0254;

        private static (List<FlashLayout.Segment> Segs, List<double[]> Outline) RoundedRect(double w, double h, double r)
        {
            double x = w / 2 * In, y = h / 2 * In, rr = r * In;
            var segs = new List<FlashLayout.Segment>
            {
                new FlashLayout.Segment { IsLine = true, A = new[] { -x + rr, -y }, B = new[] { x - rr, -y } },
                new FlashLayout.Segment { R = rr, A = new[] { x - rr, -y }, B = new[] { x, -y + rr } },
                new FlashLayout.Segment { IsLine = true, A = new[] { x, -y + rr }, B = new[] { x, y - rr } },
                new FlashLayout.Segment { R = rr, A = new[] { x, y - rr }, B = new[] { x - rr, y } },
                new FlashLayout.Segment { IsLine = true, A = new[] { x - rr, y }, B = new[] { -x + rr, y } },
                new FlashLayout.Segment { R = rr, A = new[] { -x + rr, y }, B = new[] { -x, y - rr } },
                new FlashLayout.Segment { IsLine = true, A = new[] { -x, y - rr }, B = new[] { -x, -y + rr } },
                new FlashLayout.Segment { R = rr, A = new[] { -x, -y + rr }, B = new[] { -x + rr, -y } },
            };
            var outline = new List<double[]>();
            void Arc(double cx, double cy, double a0)
            {
                for (var k = 0; k <= 8; k++)
                {
                    var a = a0 + Math.PI / 2 * k / 8;
                    outline.Add(new[] { cx + rr * Math.Cos(a), cy + rr * Math.Sin(a) });
                }
            }
            Arc(x - rr, -y + rr, -Math.PI / 2);
            Arc(x - rr, y - rr, 0);
            Arc(-x + rr, y - rr, Math.PI / 2);
            Arc(-x + rr, -y + rr, Math.PI);
            return (segs, outline);
        }

        /// <summary>The 10 x 8 in plate outline with a 1.5 in wide, 0.375 in deep lift notch mid each short side, sampled every 0.5 mm.</summary>
        private static List<double[]> PlateWithNotches()
        {
            var corners = new List<double[]>
            {
                new[] { -5.0, -4 }, new[] { 5.0, -4 }, new[] { 5.0, -0.75 }, new[] { 4.625, -0.75 }, new[] { 4.625, 0.75 }, new[] { 5.0, 0.75 },
                new[] { 5.0, 4 }, new[] { -5.0, 4 }, new[] { -5.0, 0.75 }, new[] { -4.625, 0.75 }, new[] { -4.625, -0.75 }, new[] { -5.0, -0.75 },
            };
            var pts = new List<double[]>();
            for (var i = 0; i < corners.Count; i++)
            {
                var a = corners[i];
                var b = corners[(i + 1) % corners.Count];
                var len = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1])) * In;
                var n = Math.Max(1, (int)(len / 0.0005));
                for (var k = 0; k < n; k++)
                    pts.Add(new[] { (a[0] + (b[0] - a[0]) * k / n) * In, (a[1] + (b[1] - a[1]) * k / n) * In });
            }
            return pts;
        }

        /// <summary>Outline points that are not on the plate's bounding rectangle: the notches (features to keep clear of).</summary>
        private static List<double[]> Features(List<double[]> outline)
        {
            double x0 = outline.Min(p => p[0]), x1 = outline.Max(p => p[0]), y0 = outline.Min(p => p[1]), y1 = outline.Max(p => p[1]);
            return outline.Where(p => Math.Min(Math.Min(p[0] - x0, x1 - p[0]), Math.Min(p[1] - y0, y1 - p[1])) > 1e-5).ToList();
        }

        [Fact]
        public void Cavity_outline_becomes_a_sharp_polygon_with_its_corner_radii()
        {
            var (segs, outline) = RoundedRect(5, 3.5, 0.375);
            var poly = FlashLayout.FromSegments(segs, outline);
            Assert.NotNull(poly);
            Assert.Equal(4, poly.V.Count);
            Assert.All(poly.V, v => Assert.Equal(2.5 * In, Math.Abs(v[0]), 9));
            Assert.All(poly.V, v => Assert.Equal(1.75 * In, Math.Abs(v[1]), 9));
            Assert.All(poly.Rc, r => Assert.Equal(0.375 * In, r, 9));
        }

        [Fact]
        public void Round_or_freeform_outlines_are_not_polygons()
        {
            var circle = new List<FlashLayout.Segment> { new FlashLayout.Segment { R = 0.02, A = new[] { 0.02, 0.0 }, B = new[] { 0.02, 0.0 } } };
            var outline = Enumerable.Range(0, 36).Select(k => new[] { 0.02 * Math.Cos(k * Math.PI / 18), 0.02 * Math.Sin(k * Math.PI / 18) }).ToList();
            Assert.Null(FlashLayout.FromSegments(circle, outline));
            Assert.Null(FlashLayout.FromSegments(new List<FlashLayout.Segment> { null }, outline)); // a spline
        }

        [Fact]
        public void Moat_lip_and_corners_follow_the_rules()
        {
            var (segs, outline) = RoundedRect(5, 3.5, 0.375);
            var rules = new FlashRules();
            var moat = FlashLayout.MoatFor("C1", FlashLayout.FromSegments(segs, outline), rules);
            Assert.All(moat.Inner, v => Assert.Equal(2.625 * In, Math.Abs(v[0]), 9));
            Assert.All(moat.Outer, v => Assert.Equal(3.125 * In, Math.Abs(v[0]), 9));
            Assert.All(moat.Outer, v => Assert.Equal(2.375 * In, Math.Abs(v[1]), 9));
            Assert.All(moat.InnerRadii, r => Assert.Equal(0.25 * In, r, 9)); // min(0.25, 0.375 + 0.125)
            Assert.All(moat.OuterCorners, c => Assert.Equal(0.25 * In, c.R, 9));
            // A sharp cavity corner gets a lip corner of R = lip (0.125 in).
            var sharp = FlashLayout.MoatFor("C2", new FlashLayout.Polygon { V = moat.Inner.Select(v => new[] { v[0] * 0.5, v[1] * 0.5 }).ToList(), Rc = new List<double> { 0, 0, 0, 0 } }, rules);
            Assert.All(sharp.InnerRadii, r => Assert.Equal(0.125 * In, r, 9));
            Assert.Equal(1.5 * In, rules.SeparateMoatGap, 9);
        }

        [Fact]
        public void Runners_on_the_verified_test_plate()
        {
            var (segs, outline) = RoundedRect(5, 3.5, 0.375);
            var rules = new FlashRules();
            var moat = FlashLayout.MoatFor("C1", FlashLayout.FromSegments(segs, outline), rules);
            var plate = PlateWithNotches();
            var log = new StringBuilder();
            var runners = FlashLayout.PlaceRunners(moat, plate, Features(plate), null, rules, log);

            var alongV = runners.Where(r => Math.Abs(Math.Abs(r.N[1]) - 1) < 1e-9).ToList(); // long sides (exit through +-V)
            var alongU = runners.Where(r => Math.Abs(Math.Abs(r.N[0]) - 1) < 1e-9).ToList(); // short sides
            Assert.Equal(6, alongV.Count);
            Assert.Equal(4, alongU.Count);
            Assert.All(alongV, r => Assert.False(r.Shifted));
            Assert.All(alongU, r => Assert.True(r.Shifted));
            // Shifted 0.189 in (0.2 mm search steps) so the runner clears the notch by 0.25 in.
            Assert.All(alongU, r => Assert.Equal(0.189, Math.Abs(r.At - r.Ideal) / In, 3));
            Assert.All(alongU, r => Assert.True(Math.Abs(r.P[1]) / In - 0.25 - 0.75 >= 0.25 - 1e-6));
            // Runners reach 0.1 in past the plate edge.
            Assert.All(alongV, r => Assert.Equal(4.1 * In, Math.Abs(r.P[1] + r.N[1] * r.Length), 6));
            Assert.All(alongU, r => Assert.Equal(5.1 * In, Math.Abs(r.P[0] + r.N[0] * r.Length), 6));
            Assert.Contains("shifted", log.ToString());
        }

        [Fact]
        public void Runner_with_no_clear_position_is_dropped()
        {
            var (segs, outline) = RoundedRect(5, 3.5, 0.375);
            var rules = new FlashRules();
            var moat = FlashLayout.MoatFor("C1", FlashLayout.FromSegments(segs, outline), rules);
            var plate = PlateWithNotches();
            // A row of holes all along the +U side's runner strip.
            var blockers = Enumerable.Range(-25, 51).Select(k => new[] { 4.0 * In, k * 0.1 * In }).ToList();
            var log = new StringBuilder();
            var runners = FlashLayout.PlaceRunners(moat, plate, blockers, null, rules, log);
            Assert.DoesNotContain(runners, r => r.N[0] > 0.9);
            Assert.Contains("dropped", log.ToString());
        }

        [Fact]
        public void Seams_along_a_runner_block_it_but_seams_across_do_not()
        {
            var (segs, outline) = RoundedRect(5, 3.5, 0.375);
            var rules = new FlashRules();
            var moat = FlashLayout.MoatFor("C1", FlashLayout.FromSegments(segs, outline), rules);
            var plate = PlateWithNotches();
            // A seam across the plate at U = 0 (runs along V): parallel to the long-side runners' exit direction at U = 0.
            var seam = Enumerable.Range(-80, 161).Select(k => (new[] { 0.0, k * 0.05 * In }, new[] { 0.0, 1.0 })).ToList();
            var runners = FlashLayout.PlaceRunners(moat, plate, new List<double[]>(), seam, rules);
            var middle = runners.Where(r => Math.Abs(r.N[1]) > 0.9).OrderBy(r => Math.Abs(r.P[0])).First();
            Assert.True(Math.Abs(middle.P[0]) / In >= 0.5 - 1e-6, "a runner keeps 0.25 in of steel to a seam running along it");
            // The short-side runners cross that seam? No - they exit along U, perpendicular to it: never blocked by it.
            Assert.Equal(4, runners.Count(r => Math.Abs(r.N[0]) > 0.9));
        }

        [Fact]
        public void Small_round_holes_are_features_not_cavities()
        {
            var rules = new FlashRules();
            var hole = Enumerable.Range(0, 36).Select(k => new[] { 0.005 * Math.Cos(k * Math.PI / 18), 0.005 * Math.Sin(k * Math.PI / 18) }).ToList();
            Assert.False(FlashLayout.IsCavity(hole, true, 0.01, rules));
            var big = hole.Select(p => new[] { p[0] * 4, p[1] * 4 }).ToList();
            Assert.True(FlashLayout.IsCavity(big, true, 0.04, rules));
            var slot = new List<double[]> { new[] { 0.0, 0 }, new[] { 0.1, 0 }, new[] { 0.1, 0.005 }, new[] { 0.0, 0.005 } };
            Assert.False(FlashLayout.IsCavity(slot, false, 0, rules)); // 5 mm < 0.375 in
        }
    }
}

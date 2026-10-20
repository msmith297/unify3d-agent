using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Mold.Geometry;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// Planar parting-line candidates classified per sample (critique A7) on synthetic parts: a 0 deg puck, a sphere and an
    /// O-ring each have a parting plane; undercuts and conflicting faces are reported.
    /// </summary>
    public class GeometryPartingLineTests
    {
        private const double Min = 0.5; // deg

        /// <summary>Puck of radius r and height t, 0 deg side wall: face 0 top, 1 bottom, 2 side.</summary>
        private static List<PartSample> Puck(double r = 50, double t = 25)
        {
            var s = new List<PartSample>();
            for (var k = 0; k < 64; k++)
            {
                var a = 2 * Math.PI * k / 64;
                s.Add(new PartSample { Face = 0, U = r / 2 * Math.Cos(a), V = r / 2 * Math.Sin(a), W = t, NW = 1, Area = 1, UpClear = true });
                s.Add(new PartSample { Face = 1, U = r / 2 * Math.Cos(a), V = r / 2 * Math.Sin(a), W = 0, NW = -1, Area = 1, DownClear = true });
                for (var z = 0; z < 5; z++) s.Add(new PartSample { Face = 2, U = r * Math.Cos(a), V = r * Math.Sin(a), W = t * (z + 0.5) / 5, NW = 0, Area = 0.5 });
            }
            return s;
        }

        /// <summary>A sphere (one face) sampled on latitude bands; every ray from it is clear (convex).</summary>
        private static List<PartSample> Sphere(double r = 20)
        {
            var s = new List<PartSample>();
            for (var lat = -89; lat <= 89; lat += 2)
                for (var lon = 0; lon < 360; lon += 10)
                {
                    var phi = lat * Math.PI / 180;
                    s.Add(new PartSample { Face = 0, W = r * Math.Sin(phi), NW = Math.Sin(phi), Area = Math.Cos(phi), UpClear = true, DownClear = true });
                }
            return s;
        }

        /// <summary>O-ring: torus with a W axis (major 20, minor 3); every vertical ray from its surface leaves it.</summary>
        private static List<PartSample> ORing(double major = 20, double minor = 3)
        {
            var s = new List<PartSample>();
            for (var p = 0; p < 360; p += 3)
            {
                var phi = (p + 1.5) * Math.PI / 180;
                for (var lon = 0; lon < 360; lon += 15)
                    s.Add(new PartSample { Face = 0, W = minor * Math.Sin(phi), NW = Math.Sin(phi), Area = major + minor * Math.Cos(phi), UpClear = true, DownClear = true });
            }
            return s;
        }

        [Fact]
        public void Puck_with_zero_draft_walls_parts_at_the_top_face_for_a_base_pour()
        {
            var r = PartingLine.Analyze(Puck(), Min, 0.01, w => Math.PI * 50 * 50);
            Assert.True(r.Planar);
            Assert.Equal(0, r.ZLo, 9);
            Assert.Equal(25, r.ZHi, 9);
            Assert.Empty(r.UndercutFaces);
            Assert.True(r.AreaBySide[SampleSide.Either] > 0); // the 0 deg wall needs draft wherever it goes
            PartingLine.Score(r, "base");
            Assert.Equal("top", r.Candidates[0].Id);
            Assert.Equal(25, r.Candidates[0].W, 9);
            Assert.Equal(0, r.Candidates[0].LidDepth, 9);
            Assert.Equal(25, r.Candidates[0].BaseDepth, 9);
            // The wall's draft area all sits in the base for that candidate.
            Assert.Equal(0, r.Candidates[0].DraftAreaLid, 9);
            Assert.Contains(r.Candidates, c => c.Id == "bottom" && Math.Abs(c.W) < 1e-9);
        }

        [Fact]
        public void Puck_poured_in_the_lid_parts_at_the_bottom_face()
        {
            var r = PartingLine.Analyze(Puck(), Min, 0.01);
            PartingLine.Score(r, "lid");
            Assert.Equal("bottom", r.Candidates[0].Id);
        }

        [Fact]
        public void Sphere_parts_at_the_equator()
        {
            var r = PartingLine.Analyze(Sphere(), Min, 0.001, w => Math.PI * (400 - w * w));
            Assert.True(r.Planar);
            Assert.InRange(r.ZLo, -0.5, 0);
            Assert.InRange(r.ZHi, 0, 0.5);
            PartingLine.Score(r, "base");
            Assert.InRange(r.Candidates[0].W, -0.05, 0.05);
            Assert.True(r.Candidates[0].Id == "outline" || r.Candidates[0].Id == "equator", r.Candidates[0].Id);
        }

        [Fact]
        public void ORing_parts_at_its_mid_plane()
        {
            var r = PartingLine.Analyze(ORing(), Min, 0.001, w => Math.Abs(w) < 3 ? 2 * Math.PI * 20 * 2 * Math.Sqrt(9 - w * w) : 0);
            Assert.True(r.Planar);
            PartingLine.Score(r, "base");
            Assert.InRange(r.Candidates[0].W, -0.05, 0.05);
        }

        [Fact]
        public void Critical_face_at_the_plane_vetoes_the_candidate()
        {
            var r = PartingLine.Analyze(Puck(), Min, 0.01, null, new[] { ("S7", 25.0, 25.0) });
            PartingLine.Score(r, "base");
            var top = r.Candidates.Single(c => c.Id == "top");
            Assert.True(top.Veto);
            Assert.Equal(0, top.Score);
            Assert.NotEqual("top", r.Candidates[0].Id);
        }

        [Fact]
        public void Blocked_pull_is_an_undercut()
        {
            var s = Puck();
            s.Add(new PartSample { Face = 9, W = 10, NW = 1, Area = 1, UpClear = false }); // a ledge under an overhang
            var r = PartingLine.Analyze(s, Min, 0.01);
            Assert.Equal(new[] { 9 }, r.UndercutFaces);
        }

        [Fact]
        public void Lid_only_below_base_only_has_no_planar_parting_line()
        {
            // A step: an up-facing ledge at 5 must be in the lid, a down-facing overhang at 15 must be in the base.
            var s = new List<PartSample>
            {
                new PartSample { Face = 1, W = 5, NW = 1, Area = 1, UpClear = true },
                new PartSample { Face = 2, W = 15, NW = -1, Area = 1, DownClear = true },
                new PartSample { Face = 3, W = 20, NW = 1, Area = 1, UpClear = true },
                new PartSample { Face = 4, W = 0, NW = -1, Area = 1, DownClear = true },
            };
            var r = PartingLine.Analyze(s, Min, 0.01);
            Assert.False(r.Planar);
            Assert.Equal(15, r.ZLo, 9);
            Assert.Equal(5, r.ZHi, 9);
            Assert.Equal(new[] { 1, 2 }, r.ConflictFaces);
            Assert.Empty(r.Candidates);
        }

        [Fact]
        public void Draft_classes_per_face()
        {
            Assert.Equal(DraftClass.Positive, Draft.Classify(new[] { (1.0, Math.Sin(2 * Math.PI / 180)), (1.0, 1.0) }, Min).Class);
            Assert.Equal(DraftClass.Negative, Draft.Classify(new[] { (1.0, -0.5) }, Min).Class);
            Assert.Equal(DraftClass.NeedsDraft, Draft.Classify(new[] { (1.0, 0.0), (9.0, 1.0) }, Min).Class); // 10% low
            Assert.Equal(DraftClass.Positive, Draft.Classify(new[] { (1.0, 0.0), (99.0, 1.0) }, Min).Class); // 1% low is tolerated
            Assert.Equal(DraftClass.Straddle, Draft.Classify(new[] { (1.0, 0.5), (1.0, -0.5) }, Min).Class);
            var d = Draft.Classify(new[] { (1.0, Math.Sin(3 * Math.PI / 180)), (1.0, Math.Sin(7 * Math.PI / 180)) }, Min);
            Assert.Equal(3, d.MinDeg, 6);
            Assert.Equal(7, d.MaxDeg, 6);
        }

        [Fact]
        public void Samples_are_area_weighted_and_capped()
        {
            var areas = new double[] { 100, 1, 1 };
            var s = Draft.Samples(areas, 1, 50);
            Assert.True(s.Count <= 50);
            Assert.Equal(102, s.Sum(x => x.Area), 6);
            Assert.True(s.Count(x => x.Tri == 0) > s.Count(x => x.Tri == 1));
            Assert.Equal(new[] { 0, 1, 2 }, s.Select(x => x.Tri).Distinct().OrderBy(x => x)); // no triangle dropped
            Assert.All(s, x => Assert.True(x.A >= 0 && x.B >= 0 && x.A + x.B <= 1));
        }
    }
}

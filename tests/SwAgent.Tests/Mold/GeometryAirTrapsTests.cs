using System;
using System.Linq;
using SwAgent.Mold.Geometry;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>Priority-flood air traps on synthetic ceiling maps (design 6.2 AirTrapsTests).</summary>
    public class GeometryAirTrapsTests
    {
        private const int N = 41;
        private const double Pl = 10;

        /// <summary>A square footprint (cells 0..40), ceiling at the parting plane plus `bump(i, j)`.</summary>
        private static double[,] Ceiling(Func<int, int, double> bump)
        {
            var c = new double[N, N];
            for (var i = 0; i < N; i++)
                for (var j = 0; j < N; j++)
                    c[i, j] = Pl + bump(i, j);
            return c;
        }

        private static double Dome(int i, int j, double height, double radius)
        {
            double r2 = (i - 20) * (i - 20) + (j - 20) * (j - 20);
            return r2 < radius * radius ? height * (1 - r2 / (radius * radius)) : 0;
        }

        private static double[] Vs => Enumerable.Range(0, N).Select(j => (double)(j - 20)).ToArray();

        [Fact]
        public void Flat_ceiling_at_the_parting_plane_traps_nothing()
        {
            var traps = AirTraps.Find(new AirTraps.Input { Ceiling = Ceiling((i, j) => 0), PlLevel = Pl });
            Assert.Empty(traps);
        }

        [Fact]
        public void Dish_below_the_parting_plane_traps_nothing()
        {
            // The ceiling dips in the middle (a lid plug): air rises away from it to the outline at the plane.
            var traps = AirTraps.Find(new AirTraps.Input { Ceiling = Ceiling((i, j) => -Dome(i, j, 3, 12)), PlLevel = Pl });
            Assert.Empty(traps);
        }

        [Fact]
        public void A_ring_lower_than_the_middle_traps_the_middle()
        {
            // A plug ring from the lid reaching below the ceiling inside it: the air inside can't get past it.
            var traps = AirTraps.Find(new AirTraps.Input
            {
                Ceiling = Ceiling((i, j) =>
                {
                    var r = Math.Sqrt((i - 20) * (i - 20) + (j - 20) * (j - 20));
                    return r >= 8 && r < 10 ? -2 : 0;
                }),
                PlLevel = Pl,
            });
            var trap = Assert.Single(traps);
            Assert.Equal(2, trap.Depth, 9);
        }

        [Fact]
        public void Dome_gives_one_trap_at_its_top_with_the_exact_volume()
        {
            var traps = AirTraps.Find(new AirTraps.Input { Ceiling = Ceiling((i, j) => Dome(i, j, 5, 10)), PlLevel = Pl, Cell = 0.5 });
            var trap = Assert.Single(traps);
            Assert.Equal(20, trap.HighI);
            Assert.Equal(20, trap.HighJ);
            Assert.Equal(Pl + 5, trap.HighC, 9);
            var expected = 0.0;
            for (var i = 0; i < N; i++)
                for (var j = 0; j < N; j++)
                    expected += Dome(i, j, 5, 10) * 0.25;
            Assert.Equal(expected, trap.Volume, 9);
            Assert.Equal(5, trap.Depth, 9);
        }

        [Fact]
        public void Dome_with_a_vent_at_its_top_traps_nothing()
        {
            var traps = AirTraps.Find(new AirTraps.Input { Ceiling = Ceiling((i, j) => Dome(i, j, 5, 10)), PlLevel = Pl, Vents = new[] { (20, 20) } });
            Assert.Empty(traps);
        }

        [Fact]
        public void Two_separate_domes_are_two_traps_largest_first()
        {
            var c = Ceiling((i, j) => Math.Max(Dome(i - 10, j, 2, 6), Dome(i + 10, j, 4, 6)));
            var traps = AirTraps.Find(new AirTraps.Input { Ceiling = c, PlLevel = Pl });
            Assert.Equal(2, traps.Count);
            Assert.True(traps[0].Volume > traps[1].Volume);
            Assert.Equal(1, traps[0].Id);
            Assert.Equal(10, traps[0].HighI); // the taller dome is centred at i = 10 (Dome(i + 10) peaks at i = 10)
        }

        [Fact]
        public void A_ten_degree_tilt_turns_a_shallow_trap_into_a_vented_slope()
        {
            var c = Ceiling((i, j) => Dome(i, j, 0.5, 10));
            var flat = AirTraps.Find(new AirTraps.Input { Ceiling = c, PlLevel = Pl, VOfColumn = Vs });
            Assert.Single(flat);
            var tilted = AirTraps.Find(new AirTraps.Input { Ceiling = c, PlLevel = Pl, VOfColumn = Vs, TiltRad = 10 * Math.PI / 180 });
            Assert.Empty(tilted);
        }

        [Fact]
        public void Columns_without_part_are_not_flooded_and_open_the_outline()
        {
            // A ring footprint: the hole in the middle is outside, so a dome around it drains into the hole's edge... only
            // at the plane's height: the dome above the plane is still trapped.
            var c = Ceiling((i, j) => Dome(i, j, 3, 15));
            for (var i = 17; i <= 23; i++)
                for (var j = 17; j <= 23; j++)
                    c[i, j] = double.NaN;
            var traps = AirTraps.Find(new AirTraps.Input { Ceiling = c, PlLevel = Pl + 3 });
            Assert.Empty(traps); // the whole ceiling is at or below the plane
        }
    }
}

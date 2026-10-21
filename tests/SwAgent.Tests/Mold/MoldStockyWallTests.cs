using System;
using System.Linq;
using SwAgent.Mold;
using SwAgent.Mold.Structural.Fe.Sweep;
using Xunit;
using Xunit.Abstractions;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// Review finding (engineering, high): the S21 sizing had no bounded solution for stocky walls - the root factor
    /// 1 + 2.8 w/h multiplied an alpha extended in tau^2, so the bulge levelled off at about 4.4 p h/E and the fixed point grew
    /// w geometrically (the last iterate went into the layout). The factor is now held at its calibrated w/h 0.53 value, the
    /// sizing bisects when the fixed point does not settle, and a wall no width can carry fails with WALL-NOSOLN.
    /// </summary>
    public class MoldStockyWallTests
    {
        private readonly ITestOutputHelper _out;
        public MoldStockyWallTests(ITestOutputHelper o) { _out = o; }

        private static WallInput Unit(double lOverH, double nu = 0.3, double deltaAllow = 1) => new WallInput
        {
            Id = "T", P = 1, H = 1, L = lOverH, BPerp = 0, E = 1, Nu = nu, DeltaAllow = deltaAllow,
            AllowStatic = 1e12, AllowFatigue = 1e12,
        };

        [Theory]
        [InlineData(3.33)]
        [InlineData(12)]
        public void The_bulge_keeps_falling_with_the_wall_width(double lOverH)
        {
            // the old model's floor: dE/(p h) about 4.4-4.7 for every w; now it falls with w (shear ~ h/w, root held)
            var i = Unit(lOverH);
            double prev = double.PositiveInfinity;
            foreach (var tau in new[] { 0.3, 0.5, 1.0, 2.0, 3.0, 7.6 })
            {
                var d = Walls.Deflection(i, "Wall-C", tau);
                Assert.True(d < prev, $"L/h {lOverH}: bulge at w/h {tau} ({d}) must be below the narrower wall's ({prev})");
                prev = d;
            }
            Assert.True(Walls.Deflection(i, "Wall-C", 7.6) < 2.5, "the old 4.4 p h/E floor is gone");
        }

        [Fact]
        public void A_stocky_wall_converges_to_a_bounded_width()
        {
            // the reviewer's cases: required dE/(p h) 6.0 (converged at w/h 1.96 before), 4.7 (hit the 200-step cap at w/h
            // 5.98) and 4.0 (no fixed point at all before)
            foreach (var need in new[] { 6.0, 4.7, 4.0 })
            {
                var r = Walls.Size(Unit(3.33, deltaAllow: need));
                Assert.False(r.NoSolution);
                Assert.InRange(r.WDefl, 0.01, Walls.MaxTau);
                Assert.Equal(need, Walls.Deflection(Unit(3.33, deltaAllow: need), "Wall-C", r.WDefl), 6);
            }
        }

        [Fact]
        public void A_wall_no_width_can_carry_fails_instead_of_returning_the_last_iterate()
        {
            // a bulge limit below what even a 3 h wide wall gives
            var i = Unit(3.33, deltaAllow: 0.5);
            Assert.True(Walls.Deflection(i, "Wall-C", Walls.MaxTau) > 0.5);
            var r = Walls.Size(i);
            Assert.True(r.NoSolution);
            Assert.Equal(Walls.MaxTau, r.WDefl, 9);
            var c = r.Checks.Single(x => x.Id == "WALL-NOSOLN-T");
            Assert.True(c.Demand > c.Capacity);
            Assert.Contains("steel", c.Method);
        }

        [Fact]
        public void Above_the_calibrated_range_the_roots_added_bulge_falls_as_h_over_w()
        {
            Assert.Equal(1 + 2.8 * 0.3, Walls.RootAt(1, 2.8, 0.3, 6, 0.3), 12);
            Assert.Equal(1.2, Walls.RootAt(1.2, 0, 5, 6, 0.3), 12);
            // continuous at w/h 0.53, and the added bulge (root - 1) alpha / tau^3 halves when tau doubles
            var tc = Walls.RootCalibratedTau;
            Assert.Equal(1 + 2.8 * tc, Walls.RootAt(1, 2.8, tc + 1e-12, 6, 0.3), 6);
            double Added(double tau) => (Walls.RootAt(1, 2.8, tau, 6, 0.3) - 1) * Coefficients.Get("Wall-C", 6, tau, 0.3, 1.0).Alpha / Math.Pow(tau, 3);
            Assert.Equal(0.5, Added(2 * 0.8) / Added(0.8), 9);
        }

        [Theory]
        [InlineData(3.33, 1.0)]   // P4b-like: a stocky class B wall at the recommended gauge
        [InlineData(6, 0.8)]      // E3: the aluminium class B wall
        [InlineData(10, 1.2)]     // a long stocky wall: the root's share is largest here
        public void Stocky_walls_stay_conservative_against_the_3d_pocket(double lOverH, double wOverH)
        {
            // the 3D pocket (floor 0.5 h, on the platen) at two meshes, extrapolated at first order (the ratio rises with
            // refinement): the calculator's bulge must not be below it
            var c = new Solid3dValidation.WallCase("stocky", lOverH, 3, wOverH, 0.5) { Nu = 0.3 };
            var (w1, _, r1) = Solid3dValidation.WallPocket(c, 2, false);
            var (w2, _, r2) = Solid3dValidation.WallPocket(c, 3, false);
            Assert.True(r1.Converged && r2.Converged);
            var solid = Solid3dValidation.Extrapolate(1.0 / 8, w1, 1.0 / 12, w2, 1);
            var calc = Walls.Deflection(Unit(lOverH, 0.3), "Wall-C", wOverH);
            _out.WriteLine($"L/h {lOverH}, w/h {wOverH}: 3D {w1:F4} / {w2:F4} -> {solid:F4}; calculator {calc:F4} (ratio {calc / solid:F3})");
            Assert.True(calc >= solid, $"calculator {calc} below the 3D pocket {solid}");
            Assert.True(calc <= 3 * solid, $"calculator {calc} more than 3x the 3D pocket {solid}");
        }
    }
}

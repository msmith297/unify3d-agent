using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>The single formulas and the critique corrections, each against a hand number or a property it must have.</summary>
    public class MoldFormulaTests
    {
        // ---------------------------------------------------------------- A4 walls and the coefficient table

        [Theory]
        [InlineData("Wall-C")]
        [InlineData("Wall-S")]
        public void Wall_coefficients_never_fall_with_L_over_h_and_never_pass_the_strip(string bc)
        {
            foreach (var tau in new[] { 0.02, 0.05, 0.1, 0.2, 0.3, 0.4, 0.5 })
            {
                var prev = (Alpha: 0.0, Beta: 0.0);
                var strip = Coefficients.Strip(tau, 0.3);
                for (var lh = 0.5; lh <= 20; lh += 0.25)
                {
                    var c = Coefficients.Get(bc, lh, tau, 0.3, 1.0);
                    Assert.True(c.Alpha >= prev.Alpha - 1e-9, $"{bc} alpha fell at L/h {lh}, tau {tau}");
                    Assert.True(c.Beta >= prev.Beta - 0.02, $"{bc} beta fell at L/h {lh}, tau {tau}");   // beta rows wobble ~1% at small L/h (mesh)
                    Assert.True(c.Alpha <= strip.Alpha + 1e-9 && c.Beta <= strip.Beta + 1e-9, $"{bc} above the strip at L/h {lh}, tau {tau}");
                    prev = c;
                }
                // above the last tabulated row: exactly the cantilever strip with shear (critique A4), never the L/h = 4 row
                var far = Coefficients.Get(bc, 50, tau, 0.3, 1.0);
                Assert.Equal(strip.Alpha, far.Alpha, 9);
                Assert.Equal(3.0, far.Beta, 9);
            }
            Assert.Equal(1.365 + 1.56 * 0.25, Coefficients.Strip(0.5, 0.3).Alpha, 9);
        }

        [Theory]
        [InlineData("SS4")]
        [InlineData("C4")]
        [InlineData("Rails")]
        public void Long_plates_tend_to_the_one_way_strip_and_never_fall_below_the_last_row(string bc)
        {
            foreach (var tau in new[] { 0.02, 0.2, 0.5 })
            {
                var last = Coefficients.Get(bc, 4.0, tau, 0.3, 1);
                var far = Coefficients.Get(bc, 10.0, tau, 0.3, 1);
                Assert.True(far.Alpha >= last.Alpha - 1e-12 && far.Beta >= last.Beta - 1e-12);
                var one = Coefficients.OneWay(bc, tau, 0.3);
                Assert.True(far.Alpha >= one.Alpha - 1e-12);
            }
            Assert.Equal(5 * 12 * 0.91 / 384, Coefficients.OneWay("SS4", 0, 0.3).Alpha, 12);   // 0.1422
        }

        [Fact]
        public void A_long_wall_is_sized_thicker_than_the_old_L_over_h_4_row_would()
        {
            var four = Walls.Size(new WallInput { Id = "x", P = 10, H = 25, L = 100, E = 66000, DeltaAllow = 0.05, AllowStatic = 1e9, AllowFatigue = 1e9 });
            var six = Walls.Size(new WallInput { Id = "x", P = 10, H = 25, L = 150, E = 66000, DeltaAllow = 0.05, AllowStatic = 1e9, AllowFatigue = 1e9 });
            Assert.True(six.WDefl > four.WDefl * 1.03, "L/h = 6 must be sized from its own row (A4)");
        }

        [Fact]
        public void Plate_coefficients_treat_a_over_b_and_b_over_a_alike_and_hit_the_thin_plate_values()
        {
            var a = Coefficients.Get("SS4", 2.0, 0.1, 0.3, 1);
            var b = Coefficients.Get("SS4", 0.5, 0.1, 0.3, 1);
            Assert.Equal(a.Alpha, b.Alpha, 12);
            // thin-plate limit (Roark 11.4 case 1a: alpha 0.0444 / beta 0.2874 for a/b = 1, nu 0.3)
            var thin = Coefficients.Get("SS4", 1.0, 0.02, 0.3, 1);
            Assert.Equal(0.0444, thin.Alpha, 3);
            Assert.Equal(0.287, thin.Beta, 2);
        }

        // ---------------------------------------------------------------- A11 gap budget

        [Fact]
        public void Gap_budget_subtracts_flatness_and_walls_use_the_width_tolerance()
        {
            var c = Cfg();
            var g = GapBudget.Compute("liquid1000to10000cP", c);
            Assert.Equal(0.010, g.DeltaGap, 9);
            Assert.Equal(0.03125, g.DeltaWall, 9);
            var t = GapBudget.Compute("rubber", c, closureTol_mm: 0.04, widthTol_mm: 0.05);
            Assert.Equal(0.25 * 0.04, t.DeltaGap, 9);       // S14: tighter than the budget
            Assert.Equal(0.25 * 0.05, t.DeltaWall, 9);
            Assert.Equal("liquidBelow1000cP", GapBudget.FlowClass(ProcessClass.A_Pour, "liquid_cast_hot", "liquid_low", 600));
            Assert.Equal("liquidAbove10000cP", GapBudget.FlowClass(ProcessClass.A_Pour, "liquid_cast_rt", "liquid_low", 40000)); // the page value, not the class
            Assert.Equal("paste_lsr", GapBudget.FlowClass(ProcessClass.B_Compression, "liquid_compression", "paste_lsr", 300000));
            Assert.Throws<InvalidOperationException>(() => GapBudget.Compute("rubber", new Cfg(Data.Defaults, new JObject { ["landFlatness_mm.rubber"] = 0.06 })));
        }

        // ---------------------------------------------------------------- A9 press force area, C2 force reading, S7 flags

        [Fact]
        public void Class_B_force_counts_the_overflow_groove_but_cavity_pressure_uses_the_smaller_area()
        {
            var c = Cfg();
            PressInput I(double groove) => new PressInput
            {
                Class = ProcessClass.B_Compression, Meaning = PressMeaning.Gauge, Value = 10, RamArea_mm2 = 30000, RatedGauge_MPa = 20, RatedForce_kN = 600,
                LandWidth = 1, MoldingPressureTyp = 10, MoldingPressureMax = 20,
                Cavities = { new CavityArea { Area = 15484, Perimeter = 508, GrooveArea = groove } },
            };
            var a = Press.Compute(I(0), c);
            var b = Press.Compute(I(1524), c);
            Assert.Equal(a.PLC1, b.PLC1, 9);                       // the steel sees the worst (smaller) area
            Assert.True(b.FReq_N > a.FReq_N * 1.09);                // about 10% more force needed (critique A9)
        }

        [Fact]
        public void A_force_reading_is_the_force()
        {
            var r = Press.Compute(new PressInput
            {
                Class = ProcessClass.A_Pour, Meaning = PressMeaning.ForceReading, Value = 20 * Si.UsTonToKN, RamArea_mm2 = 20000, RatedGauge_MPa = 20, RatedForce_kN = 400,
                LandWidth = 3.175, MoldingPressureTyp = 0.3, MoldingPressureMax = 1.0, Cavities = { new CavityArea { Area = 10000, Perimeter = 400 } },
            }, Cfg());
            Assert.Equal(20 * Si.UsTonToKN * 1000, r.Force_N, 6);
            Assert.Equal(PressMeaning.ForceReading, Press.ParseMeaning("Force reading (tons)"));
            Assert.Equal(PressMeaning.Unknown, Press.ParseMeaning("Not sure - ask me"));
        }

        [Fact]
        public void Gauge_reading_above_the_rating_and_a_small_press_fail()
        {
            var r = Press.Compute(new PressInput
            {
                Class = ProcessClass.B_Compression, Meaning = PressMeaning.Gauge, Value = 25, RatedGauge_MPa = 20, RatedForce_kN = 100, LandWidth = 0,
                MoldingPressureTyp = 20, MoldingPressureMax = 30, Cavities = { new CavityArea { Area = 20000, Perimeter = 600 } },
            }, Cfg());
            Assert.Contains(r.Checks, x => x.Id == "PRESS-GAUGE-RATING");
            var cap = r.Checks.Single(x => x.Id == "PRESS-CAPACITY");
            Assert.True(cap.Demand > cap.Capacity, "F_req x 1.25 > rated force");
        }

        // ---------------------------------------------------------------- A5 shrink

        [Fact]
        public void Shrink_bases_give_the_critique_factors()
        {
            Assert.Equal(1.014288, Shrink.Factor(1.5, ShrinkBasis.DatasheetSteelMold, 100, 2.36e-5), 6);
            Assert.Equal(1.013315, Shrink.Factor(1.5, ShrinkBasis.HotCavityColdPart, 100, 2.36e-5), 6);
            Assert.Equal(1 / 0.985, Shrink.Factor(1.5, ShrinkBasis.MeasuredInMyMolds, 100, 2.36e-5), 12);
            Assert.Equal(1.015, Shrink.Factor(1.5, ShrinkBasis.V1Style, 100, 2.36e-5), 12);
            Assert.Equal(ShrinkBasis.MeasuredInMyMolds, Shrink.ParseBasis("Measured in my molds at this temperature"));
            Assert.Equal(ShrinkBasis.V1Style, Shrink.ParseBasis("v1 style, 1 + S"));
            var axis = Shrink.Compute(1.0, ShrinkBasis.MeasuredInMyMolds, 150, 1.27e-5, "P20", new[] { 0.5, 0.6, 1.2 });
            Assert.True(axis.Sw > axis.Sv && axis.Sv > axis.Su);
            Assert.StartsWith("MOLD Shrink s=1.005025/1.006036/1.012146 (measured basis, P20 at 150 C)", axis.FeatureName);
            var ptfe = Shrink.Compute(2.5, ShrinkBasis.DatasheetSteelMold, 23, 1.27e-5, "P20", preformSinter: true);
            Assert.True(ptfe.IgnoresPressTemperature);
            Assert.Equal(1 / 0.975, ptfe.Su, 12);
            Assert.True(Shrink.SteelSafe(1.0, 2.0, 100, 1.0));   // 1 mm spread > 0.5 mm
            Assert.False(Shrink.SteelSafe(1.4, 1.6, 100, 1.0));
        }

        // ---------------------------------------------------------------- D3 pour time, E6 cure

        [Fact]
        public void Pour_time_scales_with_the_charge_and_cure_follows_the_class()
        {
            var c = Cfg();
            ChargeInput I(double volume) => new ChargeInput
            {
                Class = ProcessClass.A_Pour, ProcessType = "liquid_cast_hot", Cavities = 3, PartVolume_mm3 = volume, Sg = 1.1, ExcessPct = 5, OverflowPct = 5,
                PotLifeMin = 6, CureTypMin = 30, MoldTempTypC = 100, MoldTempMinC = 90, MoldTempMaxC = 110, PressTempC = 85,
            };
            var small = Charge.Compute(I(50000), c);
            var big = Charge.Compute(I(2000000), c);
            Assert.Equal(small.PerCavity_g / 10 + 15, small.PourSecondsPerCavity, 9);   // m / 10 g/s + 15 s handling
            Assert.True(big.PourSecondsPerCavity > small.PourSecondsPerCavity * 10);
            Assert.Contains(big.Checks, x => x.Id == "CHG-POUR" && x.Demand > x.Capacity);
            Assert.Equal(30, small.CureMin, 9);                                             // liquid PU: the datasheet time (E6)
            Assert.Contains(small.Checks, x => x.Id == "CURE-TEMP-LOW");                    // 85 C is below the 90 C minimum
            var rubber = Charge.Compute(new ChargeInput
            {
                Class = ProcessClass.B_Compression, ProcessType = "rubber_compression", Cavities = 1, PartVolume_mm3 = 10000, Sg = 1.2, ExcessPct = 8,
                CureTypMin = 10, MoldTempTypC = 170, MoldTempMinC = 160, MoldTempMaxC = 190, PressTempC = 180, MaxSection_mm = 0, GroovePerimeter = 300, GrooveRadiusMax = 2,
            }, c);
            Assert.Equal(5, rubber.CureMin, 9);                                              // halves per +10 C
        }


        // ---------------------------------------------------------------- A2 lifting

        [Fact]
        public void Lid_limit_comes_from_the_NIOSH_equation()
        {
            var c = Cfg();
            Assert.Equal(11.7, Lifting.LidLimit("At the slide-out table", 40, 12, c).Rwl_kg, 1);     // critique A2: 11.7 kg
            Assert.Equal(7.4, Lifting.LidLimit("Inside the press", 55, 12, c).Rwl_kg, 1);            // 7.4 kg
            Assert.Equal(11, Lifting.LidLimit(null, null, null, c).Rwl_kg);                          // unknown: 11 kg (25 lb)
            Assert.True(Lifting.LidLimit("table", 40, 120, c).Rwl_kg < 11.7, "more lifts per hour lower the limit");
            Assert.Equal(0.0, Lifting.Frequency(20, c));
        }

        // ---------------------------------------------------------------- A8 temperature

        [Fact]
        public void Temperature_statuses_refuse_beyond_the_data_and_block_ageing_grades()
        {
            var g6061 = Data.MoldGrade("al_6061_t651");
            Assert.Equal("ok", AtTemperature.Get(g6061, 100).Service);
            Assert.Equal("ok-derated", AtTemperature.Get(g6061, 140).Service);
            Assert.Equal("limited", AtTemperature.Get(g6061, 170).Service);
            Assert.Equal("beyond-limit", AtTemperature.Get(g6061, 200).Service);
            Assert.Equal("no-data", AtTemperature.Get(Data.MoldGrade("st_h13"), 400).Service);      // PEEK in H13: refused, not extrapolated
            Assert.Equal("blocked", AtTemperature.Get(Data.MoldGrade("al_2024_t351"), 160).Service);
            var a = AtTemperature.Get(g6061, 150);
            Assert.Equal(68900 * 0.93, a.E, 6);
            Assert.Equal(276 * 0.78, a.Sy, 6);
            Assert.Equal(1.0, AtTemperature.Factor(new JArray(0.9, 0.8, 0.7, 0.6), 10));
        }

        [Fact]
        public void Aluminium_bearing_divisor_rises_above_150_C()
        {
            var c = Cfg();
            Assert.Equal(2, Bearing.Divisor(5000, AtTemperature.Get(Data.MoldGrade("al_6061_t651"), 120), c, out _));
            Assert.Equal(3, Bearing.Divisor(2e5, AtTemperature.Get(Data.MoldGrade("al_6061_t651"), 120), c, out _));
            Assert.Equal(4, Bearing.Divisor(5000, AtTemperature.Get(Data.MoldGrade("al_6061_t651"), 160), c, out var basis));
            Assert.Contains("A8", basis);
            Assert.Equal(2, Bearing.Divisor(5000, AtTemperature.Get(Data.MoldGrade("st_p20"), 200), c, out _));
        }

        // ---------------------------------------------------------------- fatigue

        [Fact]
        public void Fatigue_allowables_fall_with_cycles_and_with_the_notch()
        {
            var c = Cfg();
            foreach (var id in new[] { "al_6061_t651", "al_7075_t651", "st_p20", "st_h13" })
            {
                var g = AtTemperature.Get(Data.MoldGrade(id), 100);
                var prev = double.MaxValue;
                foreach (var n in new[] { 1e3, 1e4, 1e5, 1e6, 1e7 })
                {
                    var f = Fatigue.Allowables(g, n, 2.5, c);
                    Assert.True(f.AllowFatigue <= prev + 1e-9, id + " at " + n);
                    Assert.True(f.SigmaMaxR0 <= g.Sy + 1e-9);
                    prev = f.AllowFatigue;
                }
                Assert.True(Fatigue.Allowables(g, 1e5, 1.5, c).AllowFatigue > Fatigue.Allowables(g, 1e5, 2.5, c).AllowFatigue);
            }
            Assert.Equal(2.5, Fatigue.Kt(0.0, c));
            Assert.Equal(1.5, Fatigue.Kt(0.3, c));
            Assert.Equal(2.2, Fatigue.Kt(0.06, c));
        }

        // ---------------------------------------------------------------- bolts: A1 scatter, H5 grip, frustum, A3 abuse cap

        [Fact]
        public void Scatter_tightens_the_preload_window()
        {
            var c = Cfg();
            var j = new JointInput
            {
                Bolt = BoltSpec.From(Data.Fastener("1/2-13"), c), Grip = 50.8, Layers = { new Layer { T = 50.8, E = 66833, Aluminium = true } },
                TempC = 100, P = 4708 * Si.LbfToN, MemberSuT = 291.4,
            };
            var wide = Bolts.Design(new JointInput { Bolt = j.Bolt, Grip = j.Grip, Layers = j.Layers, TempC = 100, P = j.P, MemberSuT = j.MemberSuT, Scatter = 0.10 }, c, false);
            var narrow = Bolts.Design(j, c, false);
            Assert.True(wide.FractionHi - wide.FractionLo > narrow.FractionHi - narrow.FractionLo);
            Assert.True(narrow.FiMax == 1.25 * narrow.Fi && Math.Abs(narrow.FiMin - 0.75 * 0.9 * narrow.Fi) < 1e-6);
        }

        [Fact]
        public void Tapped_holes_use_Shigleys_effective_grip()
        {
            Assert.Equal(20 + 3, Bolts.EffectiveGrip(20, 6, 12), 9);    // t2 < d: h + t2/2
            Assert.Equal(20 + 6, Bolts.EffectiveGrip(20, 30, 12), 9);   // otherwise h + d/2
        }

        [Fact]
        public void Mixed_stacks_use_the_frustum_method_and_land_between_the_materials()
        {
            var c = Cfg();
            var b = BoltSpec.From(Data.Fastener("M10"), c);
            double Km(params Layer[] layers) => Bolts.MemberStiffness(new JointInput { Bolt = b, Layers = layers.ToList() }, 40);
            var al = Km(new Layer { T = 40, E = 68900, Aluminium = true });
            var st = Km(new Layer { T = 40, E = 205000 });
            var mix = Km(new Layer { T = 20, E = 68900, Aluminium = true }, new Layer { T = 20, E = 205000 });
            Assert.InRange(mix, al, st);
            // the frustum method and Wileman agree within ~15% on a uniform stack
            var frustum = 1 / (2 / Bolts.Frustum(68900, 10, 20, 15));
            Assert.InRange(frustum / al, 0.85, 1.15);
        }

        [Fact]
        public void The_abuse_case_caps_what_a_joint_carries()
        {
            var c = Cfg();
            var j = new JointInput
            {
                Bolt = BoltSpec.From(Data.Fastener("3/8-16"), c), Tapped = true, TopThickness = 22, TappedThickness = 20,
                Layers = { new Layer { T = 22, E = 66833, Aluminium = true } }, TempC = 100, P = 2000, PAbuse = 40000, MemberSuT = 291.4,
            };
            var nominal = Bolts.Design(j, c, false);
            Assert.True(nominal.Passes, nominal.Failing);
            Assert.False(nominal.AbusePasses);
            var cap = Bolts.AbuseCapacity(j, c);
            Assert.InRange(cap, 2000, 40000);
            j.PAbuse = cap * 0.99;
            Assert.True(Bolts.Design(j, c, true).AbusePasses);
        }

        // ---------------------------------------------------------------- threads, fits, pins (A12)

        [Fact]
        public void Shut_off_pins_get_the_axial_and_Euler_check()
        {
            var c = Cfg();
            var p20 = AtTemperature.Get(Data.MoldGrade("st_p20"), 20);
            var r = Pins.Check("K1", 3, 20, 1.5, p20, 200, 0.03, c, shutOff: true);
            // critique A12: a 3 x 20 mm steel pin is about 72 kN/mm axially; fixed-free Euler about 5.0 kN
            Assert.Equal(205000 * Math.PI * 9 / 4 / 20, r.AxialStiffness, 6);
            Assert.InRange(r.EulerLoad, 4900, 5100);
            Assert.Contains(r.Checks, x => x.Id == "PIN-EULER-K1");
            Assert.True(r.InsertRequired, "H/D 6.7 > 2");
            var guided = Pins.Check("K1", 3, 20, 1.5, p20, 200, 0.03, c, shutOff: true, guidedTip: true);
            Assert.True(guided.EulerLoad > 8 * r.EulerLoad);
        }

        [Fact]
        public void Slide_in_dowels_become_round_plus_radial_slots()
        {
            var c = Cfg();
            var a = 2.36e-5;
            var slide = Fits.Dowels(412, a, a, 100, true, 4, c);
            Assert.Equal(0.29, slide.Mismatch_mm, 2);          // critique D1: 30 K over 412 mm
            Assert.True(slide.RoundPlusRelieved);
            Assert.True(slide.AskShop);
            var stays = Fits.Dowels(100, a, a, 100, false, 2, c);
            Assert.Equal(10, stays.LidBaseDeltaT);
            var hoop = Fits.InsertHoop(25, 75, 0.0377, 68900, 0.33, 205000, 0.29, 276, c);
            Assert.True(hoop.HoopOk);
        }

        // ---------------------------------------------------------------- stock rounding (S75), layout, split helpers

        [Fact]
        public void Stock_rounding_finishes_to_the_shop_step_and_buys_the_next_plate_up()
        {
            var g = Data.MoldGrade("al_6061_t651");
            var t = StockRounding.Thickness(43.75, g, Data, true);
            Assert.Equal(44.45, t.Finished, 9);                 // 1.75 in
            Assert.Equal(50.8, t.Stock, 9);                     // 2.0 in plate, skimmed 1.5 mm a face
            var mic6 = StockRounding.Thickness(20, Data.MoldGrade("al_mic6"), Data, true);
            Assert.Equal(25.4, mic6.Finished, 9);               // sold to finished thickness: a list size
            var metric = StockRounding.Thickness(43.2, g, Data, false);
            Assert.Equal(44, metric.Finished, 9);
            Assert.Equal(50, metric.Stock, 9);
            Assert.Equal(431.8, StockRounding.Planform(426.2, Data, true), 9);
        }

        [Fact]
        public void Class_A_wall_from_the_flash_standard_matches_the_hand_value()
        {
            var w = CavityLayout.ClassAWall(3.175, 12.7, 6.35, 6.35, 12.7, 15.875, 6.35);
            Assert.Equal(1.6404, w / 25.4, 4);
        }

        [Fact]
        public void Layout_numbers_cavities_in_reading_order_and_centres_a_short_row()
        {
            var r = CavityLayout.Compute(new LayoutInput { Cavities = 5, PartU = 50, PartV = 40, GapU = 20, GapV = 20, EdgeU = 30, EdgeV = 30, Pattern = "3x2" }, Data);
            Assert.Equal(new[] { "C1", "C2", "C3", "C4", "C5" }, r.Cavities.Select(c => c.Id));
            Assert.True(r.Cavities[0].V > 0 && r.Cavities[3].V < 0);
            Assert.Equal(-35, r.Cavities[3].U, 9);              // the two in the short row sit symmetric about the axis
            Assert.Equal(35, r.Cavities[4].U, 9);
            Assert.Throws<ArgumentException>(() => CavityLayout.Compute(new LayoutInput { Cavities = 5, PartU = 50, PartV = 40, Pattern = "2x2" }, Data));
        }

        [Fact]
        public void Split_candidates_never_cross_a_cavity()
        {
            var cuts = PlateSplit.Candidates(new List<(double, double)> { (-100, -20), (-30, 10), (60, 100) }, -200, 200, 15);
            Assert.Single(cuts);                                // (-100,-20) and (-30,10) overlap: no cut between them
            Assert.Equal(35, cuts[0], 9);
            Assert.True(PlateSplit.BoxFits(new[] { 598.0, 300, 100 }, new[] { 150.0, 600, 400 }, 3, 1.5) == false);
            Assert.True(PlateSplit.BoxFits(new[] { 300.0, 590, 100 }, new[] { 400.0, 600, 150 }, 0, 0));
        }

        // ---------------------------------------------------------------- units

        [Fact]
        public void Numbers_with_units_are_read_in_calculator_units()
        {
            Assert.Equal(13.789514586, Si.Num(new JValue("2000 psi")).Value, 6);
            Assert.Equal(25.4, Si.Num(new JObject { ["value"] = 1, ["unit"] = "in" }).Value, 9);
            Assert.Equal(13.789514586, Si.Num(new JObject { ["value"] = 13.789514586, ["unit"] = "MPa", ["display"] = "2000 psi" }).Value, 9);
            Assert.Equal(100, Si.Num(new JValue("212 degF")).Value, 9);
            Assert.Equal(444.8221615, Si.Num(new JValue("50 US ton")).Value, 6);
            Assert.Null(Si.Num(JValue.CreateNull()));
            Assert.Null(Si.Num(new JValue("not set")));
            Assert.All(Si.Lengths(new JObject { ["value"] = new JArray(24, 24), ["unit"] = "in" }), x => Assert.Equal(609.6, x, 9));
            Assert.Equal(new[] { 600.0, 400, 150 }, Si.Lengths(new JArray(600, 400, 150)));
        }
    }
}

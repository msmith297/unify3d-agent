using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;
using Xunit.Abstractions;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// Regression tests for the second (integrated) review, engineering lens: the setup sheet's maximum gauge is checked,
    /// the press rating for every meaning, unit strings, the LC1x wall formula, the as-built span, the stack height, and
    /// the smaller items.
    /// </summary>
    public class MoldReview2EngineeringTests
    {
        private readonly ITestOutputHelper _out;
        public MoldReview2EngineeringTests(ITestOutputHelper o) { _out = o; }

        private static CalcResult Run(string action, JObject design, JObject overrides = null) => MoldCalc.Run(action, design, null, overrides, Data);

        private static CalcResult RunMerge(string action, JObject design, JObject overrides = null)
        {
            var r = Run(action, design, overrides);
            if (r.RecordPatch != null) MoldRecord.DeepMerge(design, r.RecordPatch);
            return r;
        }

        /// <summary>The verifier's case: P20, NBR 70A, 2 cavities 100 x 60 x 30 mm, gauge 1000 psi on a 50 ton / 3000 psi press.</summary>
        private static JObject ClassB(double gaugePsi = 1000)
        {
            var d = MoldReviewTests.Rubber();
            var i = (JObject)d["inputs"];
            i["press_pressure"] = gaugePsi * Si.PsiToMPa;
            i["cavities"] = 2;
            i["part_family"] = null;
            i["part_grade"] = "nbr-s-70a";
            i["press_temp"] = 170;
            d["part"] = new JObject
            {
                ["volume_mm3"] = 100 * 60 * 30, ["projectedArea_mm2"] = 100 * 60, ["perimeter_mm"] = 320, ["box_mm"] = new JArray(100, 60, 30), ["floorRadius_mm"] = 0.5,
            };
            return d;
        }

        private static string[] StructuralFails(CalcResult r) => r.Checks.Where(c => Check.Fails(c) && Check.Group((string)c["id"]) == "structural").Select(c => (string)c["id"] + " " + c["demand"] + "/" + c["capacity"]).ToArray();

        [Theory]
        [InlineData("A")]
        [InlineData("B")]
        public void Class_B_is_checked_at_the_printed_maximum_gauge(string support)
        {
            var d = ClassB();
            var over = support == "A" ? null : new JObject { ["support"] = support };
            var size = RunMerge("size", d, over);
            var max = (double)d.SelectToken("structural.gauge.max_MPa");
            var rec = (double)d.SelectToken("structural.gauge.recommended_MPa");
            _out.WriteLine($"support {support}: recommended {rec / Si.PsiToMPa:F0} psi, max {max / Si.PsiToMPa:F0} psi ({d.SelectToken("structural.gauge.maxBasis")})");
            Assert.True(rec <= max * (1 + 1e-9), "the recommended gauge must be inside the maximum");
            // before the review the maximum here was the press rating (3000 psi), at which the walls failed
            Assert.True(max < 3000 * Si.PsiToMPa, "a structural cap governs, not the press rating");
            Assert.Empty(StructuralFails(size));
            // the operator raises the gauge to the printed maximum, then to the recommended setting: nothing structural fails
            foreach (var g in new[] { max, rec })
            {
                var atG = (JObject)d.DeepClone();
                atG["inputs"]["press_pressure"] = g;
                var check = Run("check", atG, over);
                Assert.True(StructuralFails(check).Length == 0, "at " + g + " MPa: " + string.Join(", ", StructuralFails(check)));
            }
        }

        [Fact]
        public void Class_B_walls_are_sized_for_the_recommended_gauge_when_the_setting_is_lower()
        {
            var d = ClassB(500);
            var r = Run("size", d);
            var p = (double)r.RecordPatch.SelectToken("structural.loadCases.LC1.p_MPa");
            var press = r.Checks.Single(c => (string)c["id"] == "PRESS-FORCE");
            Assert.Equal("fail", (string)press["status"]);
            Assert.Contains("re-run mold_calc size", MoldCalc.Fix(press));
            // p_LC1 is the cavity pressure at the force needed, not at the user's lower setting
            Assert.True(p >= (double)r.RecordPatch.SelectToken("structural.gauge.recommended_MPa") * 0.999 * 1.0 * 0 + 1e-9);
            var fReq = (double)((JObject)r.Json["gauge"])["recommended_MPa"];
            Assert.True(fReq > 500 * Si.PsiToMPa);
        }

        [Theory]
        [InlineData("Force reading (tons)", "press_force_reading", 74.5 * Si.UsTonToKN)]
        [InlineData("On the part", "part_pressure", 60.0)]
        public void Every_press_meaning_is_checked_against_the_rating(string meaning, string key, double value)
        {
            var d = ClassB();
            var i = (JObject)d["inputs"];
            i["press_pressure_is"] = meaning;
            i.Remove("press_pressure");
            i[key] = value;
            var r = Run("size", d);
            var rating = r.Checks.SingleOrDefault(c => (string)c["id"] == "PRESS-GAUGE-RATING");
            Assert.NotNull(rating);
            Assert.Equal("fail", (string)rating["status"]);
            // the user's equivalent gauge is compared with the maximum too
            Assert.Contains(r.Checks, c => (string)c["id"] == "PRESS-SETTING");
        }

        [Fact]
        public void Without_a_rated_gauge_the_rating_cap_is_the_rated_force_over_the_ram()
        {
            var d = ClassB();
            var i = (JObject)d["inputs"];
            i.Remove("press_rated_gauge");
            i["ram_bore"] = 4 * 25.4;
            var r = Run("size", d);
            var caps = (JArray)r.RecordPatch.SelectToken("structural.gauge.caps");
            var ram = Math.PI * Math.Pow(4 * 25.4, 2) / 4;
            Assert.Contains(caps, c => ((string)c["basis"]).StartsWith("press rating") && Math.Abs((double)c["gauge_MPa"] / (50 * Si.UsTonToKN * 1000 / ram) - 1) < 1e-3);
        }

        [Fact]
        public void Unit_strings_are_converted_by_quantity_and_unknown_units_throw()
        {
            var tons = 50 * Si.UsTonToKN;
            Assert.Equal(tons, Si.Num("50 US tons", "force").Value, 6);
            Assert.Equal(tons, Si.Num("50 short tons", "force").Value, 6);
            Assert.Equal(tons, Si.Num(new JObject { ["value"] = 50, ["unit"] = "US tons" }, "force").Value, 6);
            Assert.Equal(444.822, Si.Num("100000 lb", "force").Value, 2);            // lb in a force field is lbf
            Assert.Equal(444.822, Si.Num("100 kip", "force").Value, 2);
            Assert.Equal(444.82, Si.Num(new JObject { ["value"] = 45359, ["unit"] = "kgf" }, "force").Value, 1);
            Assert.Equal(490.33, Si.Num("50 metric tons", "force").Value, 2);
            Assert.Equal(13.79, Si.Num("2000 psig", "pressure").Value, 2);
            Assert.Equal(0.101325, Si.Num("1 atm", "pressure").Value, 6);
            Assert.Equal(55.88, Si.Num("22 in", "length_cm").Value, 6);              // lid reach in cm
            Assert.Equal(609.6, Si.Num("2 ft", "length").Value, 6);
            Assert.Equal(45.359237, Si.Num("100 lb").Value, 6);                      // no quantity: lb is a mass
            Assert.Throws<ArgumentException>(() => Si.Num("50 bananas", "force"));
            Assert.Throws<ArgumentException>(() => Si.Num("50 psi", "force"));      // mis-dimensioned
            Assert.Throws<ArgumentException>(() => Si.Num(new JObject { ["value"] = 50, ["unit"] = "furlongs" }, "length"));
            Assert.Throws<ArgumentException>(() => Si.Num("50 bananas"));
            // the page's own display strings still read
            Assert.Equal(13.7895, Si.Num(new JObject { ["value"] = "2000 psi (13.79 MPa)", ["si"] = 13789514.6 }).Value, 4);
        }

        [Fact]
        public void A_bad_unit_in_the_inputs_names_the_field()
        {
            var d = ClassB();
            d["inputs"]["press_rated_force"] = "50 US tons";
            Assert.Equal(50 * Si.UsTonToKN, Design.Read(d, Data).RatedForce_kN, 6);
            d["inputs"]["press_rated_force"] = "50 bananas";
            var ex = Assert.Throws<ArgumentException>(() => Design.Read(d, Data));
            Assert.Contains("inputs.press_rated_force", ex.Message);
        }

        [Fact]
        public void LC1x_uses_beta_for_a_plate_like_wall_and_the_strip_only_when_stocky()
        {
            WallInput W(double wGeom) => new WallInput
            {
                Id = "T", P = 1, H = 100, L = 50, BPerp = 50, E = 70000, DeltaAllow = 1, AllowStatic = 1e9, AllowFatigue = 1e9,
                WGeom = wGeom, PAbuse = 10, SyT = 262,
            };
            var plate = Walls.Size(W(40));     // w/h 0.4
            var beta = Coefficients.Get("Wall-C", 0.5, 0.4, 0.3, 1.0).Beta;
            Assert.Equal(10 * (beta * 100 * 100 / (40 * 40) + 50 / 80.0), plate.SigmaAbuse.Value, 6);
            Assert.True(plate.SigmaAbuse.Value < 10 * (3 * 100 * 100 / (40 * 40.0) + 50 / 80.0) / 5, "the strip is 10-25x conservative for deep cavities");
            var stocky = Walls.Size(W(60));    // w/h 0.6
            Assert.Equal(10 * (3 * 100 * 100 / (60 * 60.0) + 50 / 120.0), stocky.SigmaAbuse.Value, 6);
        }

        [Fact]
        public void A_deep_class_A_cavity_keeps_a_workable_maximum_gauge_and_warns_about_an_unreadable_gauge()
        {
            // the verifier's case: 1 cavity 60 x 60 x 120 mm, TDI 90A, 6061 at 100 C, 50 ton / 3000 psi set at 2000 psi
            var d = E2Design();
            d["inputs"]["cavities"] = 1;
            ((JObject)d["layout"]).Remove("patternRequest");
            d["part"] = new JObject { ["volume_mm3"] = 60 * 60 * 120, ["projectedArea_mm2"] = 3600, ["perimeter_mm"] = 240, ["box_mm"] = new JArray(60, 60, 120), ["floorRadius_mm"] = 0 };
            var r = Run("size", d);
            var max = (double)r.RecordPatch.SelectToken("structural.gauge.max_MPa");
            _out.WriteLine($"max {max} MPa ({r.RecordPatch.SelectToken("structural.gauge.maxBasis")})");
            Assert.True(max > 10, "the strip bound collapsed the maximum to 2.5 MPa (362 psi)");
            Assert.Contains(r.Checks, c => (string)c["id"] == "PRESS-GAUGE-READ" && (string)c["status"] == "warn");
        }

        [Fact]
        public void The_as_built_check_checks_the_span_at_the_as_built_floor()
        {
            var d = ClassB();
            var over = new JObject { ["support"] = "B" };
            RunMerge("size", d, over);
            var tPlanned = (double)d.SelectToken("structural.results.plates.base.t_mm");
            var ok = Run("check", (JObject)d.DeepClone(), over);
            Assert.Contains(ok.Checks, c => (string)c["id"] == "SPAN-DEFL" && (string)c["status"] == "pass");
            Assert.True((bool?)ok.RecordPatch.SelectToken("structural.results.plates.span.asBuilt") == true);
            // a base machined much thinner than planned: the span fails (before the review it was re-sized and always passed)
            var thin = (JObject)d.DeepClone();
            var h = (double)d.SelectToken("structural.results.walls[0].h_mm");
            thin["structural"]["asBuilt"] = new JObject { ["baseThickness_mm"] = h + 6 };
            var bad = Run("check", thin, over);
            Assert.Contains(bad.Checks, c => (string)c["id"] == "SPAN-DEFL" && (string)c["status"] == "fail");
            Assert.Contains(bad.Checks, c => (string)c["id"] == "PLATE-BASE-T" && (string)c["status"] == "fail");
            // the base piece and its stock follow the as-built thickness
            var piece = ((JArray)bad.RecordPatch.SelectToken("structural.results.pieces")).OfType<JObject>().Single(x => (string)x["id"] == "BASE-1");
            Assert.Equal(h + 6, (double)piece["t_mm"], 3);
            Assert.True((double)piece["stock_mm"] < tPlanned);
        }

        [Fact]
        public void The_closed_stack_includes_a_split_bases_subplate()
        {
            var d = E2Design();
            RunMerge("size", d);
            var stack0 = (double)d.SelectToken("structural.results.stackHeight_mm");
            d["structural"]["split"] = new JObject { ["base"] = new JObject { ["cutsU_mm"] = new JArray(0.0), ["cutsV_mm"] = new JArray(), ["subplate"] = new JObject { ["t_mm"] = 22.225 } } };
            var r = Run("size", d);
            Assert.Equal(stack0 + 22.225, (double)r.RecordPatch.SelectToken("structural.results.stackHeight_mm"), 3);
        }

        [Fact]
        public void The_as_built_check_skips_bodies_without_a_mass()
        {
            var d = E2Design();
            RunMerge("size", d);
            var pieces = (JArray)d.SelectToken("structural.results.pieces");
            d["bodies"] = new JArray(
                new JObject { ["id"] = "BASE-1", ["role"] = "base", ["mass_kg"] = pieces[0]["mass_kg"], ["box_mm"] = pieces[0]["box_mm"].DeepClone() },
                new JObject { ["id"] = "LID-1", ["role"] = "lid", ["mass_kg"] = pieces[1]["mass_kg"], ["box_mm"] = pieces[1]["box_mm"].DeepClone() },
                new JObject { ["id"] = "PART-REF", ["role"] = "reference", ["mass_kg"] = null, ["box_mm"] = new JArray(150, 100, 25) },
                new JObject { ["id"] = "GARBAGE-1", ["role"] = "garbage", ["mass_kg"] = null, ["box_mm"] = new JArray(10, 10, 10) });
            var r = Run("check", d);   // threw "Can not convert Null to Double." before
            Assert.Contains(r.Checks, c => (string)c["id"] == "WEIGHT-LID");
        }

        [Fact]
        public void A_temperature_without_data_gets_the_right_fix()
        {
            var c = Check.Status("TEMP-st_p20", "temperature", "fail", "A8: no strength data above 250 C", "");
            Assert.StartsWith("lower the press temperature to 250 C", MoldCalc.Fix(c));
            var al = Check.Status("TEMP-al_6061_t651", "temperature", "fail", "S12 + A8: limited at this stress", "");
            Assert.Contains("P20", MoldCalc.Fix(al));
        }
    }
}

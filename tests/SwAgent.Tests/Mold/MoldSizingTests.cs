using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>The calculator actions end to end (design 2.3.2): determinism, the process classes, splits, the as-built check.</summary>
    public class MoldSizingTests
    {
        private static CalcResult Run(string action, JObject design, JObject query = null, JObject overrides = null) => MoldCalc.Run(action, design, query, overrides, Data);

        private static JObject Rubber() => new JObject
        {
            ["units"] = new JObject { ["display"] = "imperial" },
            ["inputs"] = new JObject
            {
                ["press_pressure"] = 1500 * Si.PsiToMPa, ["press_pressure_is"] = "Gauge reading", ["press_rated_force"] = 50 * Si.UsTonToKN,
                ["press_rated_gauge"] = 3000 * Si.PsiToMPa, ["press_temp"] = 170, ["cavities"] = 8, ["cycle_life"] = 20000,
                ["mold_material"] = "Aluminium + steel inserts", ["mold_grade"] = "6061-T651", ["part_family"] = "EPDM", ["part_grade"] = "EPDM sulfur 70A",
                ["max_plate_mass"] = 23, ["max_plate_size"] = new JArray(600, 400, 150), ["platen_size"] = new JArray(610, 610), ["daylight"] = 300,
            },
            ["part"] = new JObject
            {
                ["volume_mm3"] = Math.PI * (625 - 225) * 5, ["projectedArea_mm2"] = Math.PI * (625 - 225), ["perimeter_mm"] = 2 * Math.PI * 40,
                ["box_mm"] = new JArray(50, 50, 5), ["floorRadius_mm"] = 0.5,
            },
        };

        [Fact]
        public void Same_design_gives_byte_identical_output()
        {
            foreach (var action in new[] { "process", "press", "layout", "size" })
            {
                var a = MoldCalc.Run(action, E2Design(), null, null, MoldData.Load(MoldData.DefaultToolDir));
                var b = MoldCalc.Run(action, E2Design(), null, null, MoldData.Load(MoldData.DefaultToolDir));
                Assert.Equal(a.Text, b.Text);
                Assert.Equal(a.Json.ToString(Formatting.None), b.Json.ToString(Formatting.None));
                Assert.Equal(a.RecordPatch?.ToString(Formatting.None), b.RecordPatch?.ToString(Formatting.None));
            }
        }

        [Fact]
        public void Size_text_is_ordered_lowest_margin_first_with_a_json_block_under_4000_chars()
        {
            var r = Run("size", E2Design());
            Assert.Contains("Checks, lowest margins first", r.Text);
            var json = r.Text.Substring(r.Text.IndexOf("```json", StringComparison.Ordinal) + 7);
            json = json.Substring(0, json.IndexOf("```", StringComparison.Ordinal)).Trim();
            Assert.True(json.Length <= 4000, "JSON block " + json.Length);
            JObject.Parse(json);
            var margins = r.Checks.Where(c => (string)c["status"] == "pass" && c["margin"].Type != JTokenType.Null).Select(c => (double)c["margin"]).ToList();
            Assert.Equal(margins.OrderBy(x => x), margins);
        }

        [Fact]
        public void Not_sure_press_meaning_asks_instead_of_guessing()
        {
            var d = E2Design();
            d["inputs"]["press_pressure_is"] = "Not sure - ask me";
            var ex = Assert.Throws<ArgumentException>(() => Run("press", d));
            Assert.Contains("ask the user", ex.Message);
            Assert.Contains("process", Run("process", d).Text);   // process numbers do not need the press
        }

        [Fact]
        public void Missing_geometry_and_unknown_material_say_what_to_do()
        {
            var d = E2Design();
            d.Remove("part");
            Assert.Contains("mold_part analyze", Assert.Throws<ArgumentException>(() => Run("size", d)).Message);
            var m = E2Design();
            m["inputs"]["part_grade"] = "unobtainium";
            Assert.Contains("materials.part.id", Assert.Throws<ArgumentException>(() => Run("process", m)).Message);
            Assert.Throws<ArgumentException>(() => Run("frobnicate", E2Design()));
        }

        [Fact]
        public void Left_to_itself_the_layout_picks_the_smaller_footprint()
        {
            var d = E2Design();
            d.Remove("layout");
            var l = Run("layout", d).Json;
            Assert.Equal("grid 1x2", (string)l["pattern"]);
            Assert.Equal(241.3, (double)l["footprint_mm"][0], 6);   // 9.5 x 13 in (hand)
            Assert.Equal(330.2, (double)l["footprint_mm"][1], 6);
        }

        [Fact]
        public void Rubber_at_170_C_in_6061_fails_the_temperature_gate_and_P20_passes()
        {
            var r = Run("size", Rubber());
            var temp = r.Checks.Single(c => ((string)c["id"]).StartsWith("TEMP-"));
            Assert.Equal("fail", (string)temp["status"]);                 // limited (150-175 C) at high stress and N > 10,000
            Assert.Contains("P20", (string)temp["note"]);
            Assert.Contains(r.Checks, c => (string)c["id"] == "PRESS-EXCESS" && (string)c["status"] == "warn");
            var stops = r.RecordPatch.SelectToken("hardware.stops") as JArray;
            Assert.Equal(4, stops.Count);
            Assert.Equal(4.0, (double)r.RecordPatch.SelectToken("structural.results.bearing.Db"));   // A8 creep divisor above 150 C

            var steel = Rubber();
            steel["inputs"]["mold_material"] = "Tool steel";
            steel["inputs"]["mold_grade"] = "P20 pre-hard";
            var s = Run("size", steel);
            Assert.DoesNotContain(s.Checks, c => ((string)c["id"]).StartsWith("TEMP-"));
            Assert.DoesNotContain(s.Checks, c => (string)c["status"] == "fail");
        }

        [Fact]
        public void Low_stress_limited_temperature_is_allowed_with_the_reason()
        {
            // critique A8: PVC plastisol at 170 C and 20 psi in 6061 - "limited", but the stresses are near zero
            var d = E2Design();
            d["inputs"]["part_grade"] = "pvc-plastisol";
            d["inputs"]["press_temp"] = 170;
            d["inputs"].Value<JObject>().Remove("shrink_basis");
            var r = Run("size", d);
            var temp = r.Checks.Single(c => ((string)c["id"]).StartsWith("TEMP-"));
            Assert.Equal("pass", (string)temp["status"]);
            Assert.Contains("A8", (string)temp["method"]);
        }

        [Fact]
        public void Class_C_is_advisory_and_refused_above_250_C()
        {
            var d = E2Design();
            d["inputs"]["part_grade"] = "peek-450pf";
            d["inputs"]["mold_material"] = "Tool steel";
            d["inputs"]["mold_grade"] = "st_h13";
            d["inputs"].Value<JObject>().Remove("press_temp");             // the row's 400 C
            var r = Run("size", d);
            Assert.Contains("ADVISORY", r.Text);
            Assert.Contains(r.Checks, c => (string)c["id"] == "SCOPE-CLASS-C" && (string)c["status"] == "warn");
            Assert.Contains(r.Checks, c => ((string)c["id"]).StartsWith("TEMP-") && (string)c["status"] == "fail");
        }

        [Fact]
        public void Lid_limit_uses_the_lifting_answers()
        {
            var d = E2Design();
            d["inputs"]["lid_lift_at"] = "Inside the press";
            d["inputs"]["lid_reach_cm"] = 55;
            d["inputs"]["lid_lifts_per_hour"] = 12;
            var lid = Run("size", d).RecordPatch.SelectToken("structural.results.lid");
            Assert.Equal(7.4, (double)lid["limit_kg"], 1);
            Assert.StartsWith("NIOSH", (string)lid["limitBasis"]);
            d["inputs"]["max_lid_mass"] = 15;                              // the user's own limit wins, the index is still shown
            Assert.Equal(15, (double)Run("size", d).RecordPatch.SelectToken("structural.results.lid.limit_kg"));
        }

        [Fact]
        public void Drawing_tolerances_tighten_the_gap_and_wall_budgets()
        {
            var d = E2Design();
            d["part"]["drawing"] = new JObject
            {
                ["tolerances"] = new JArray(
                    new JObject { ["id"] = "T1", ["what"] = "thickness 25.4", ["tol_mm"] = 0.02, ["closure"] = true, ["nominal_mm"] = 25.4 },
                    new JObject { ["id"] = "T2", ["what"] = "width 152.4", ["tol_mm"] = 0.05, ["closure"] = false, ["nominal_mm"] = 152.4 }),
            };
            var r = Run("size", d);
            Assert.Equal(0.005, (double)r.RecordPatch.SelectToken("structural.inputs.deltaGap_mm"), 6);
            Assert.Equal(0.0125, (double)r.RecordPatch.SelectToken("structural.inputs.deltaWall_mm"), 6);
            // S65: a 0.05 mm tolerance on 152.4 mm is far tighter than the shrink spread (1.0-2.0%): steel-safe
            var p = Run("process", d).Json;
            Assert.Contains(p.SelectToken("shrink.steelSafe"), t => (string)t["id"] == "T2");
        }

        [Fact]
        public void A_heavy_base_splits_into_keyed_pieces_with_bolts_sized_for_the_abuse_case()
        {
            var d = E2Design();
            d["inputs"]["cavities"] = 6;
            d["layout"] = new JObject { ["patternRequest"] = "3x2" };
            d["part"]["box_mm"] = new JArray(152.4, 101.6, 50.8);
            d["part"]["volume_mm3"] = 152.4 * 101.6 * 50.8;
            var tight = (JObject)d.DeepClone();
            var first = Run("size", tight);
            MoldRecord.DeepMerge(tight, first.RecordPatch);
            // with the minimum 1.5 in moat gap there is no dead steel for a cut: the split says how much gap it needs
            var none = Run("split", tight, new JObject { ["plate"] = "base" });
            Assert.Empty((JArray)none.Json["options"]);
            Assert.Contains("gap_mm", none.Text);

            var size = Run("size", d, null, new JObject { ["gap_mm"] = 51 });
            Assert.Contains(size.Checks, c => (string)c["id"] == "WEIGHT-BASE-1" && (string)c["status"] == "fail");
            Assert.Contains("never a split lid", MoldCalc.Fix(size.Checks.Single(c => (string)c["id"] == "WEIGHT-LID")));

            // the agent merges the size result, asks for split options, merges the chosen one, sizes again
            MoldRecord.DeepMerge(d, size.RecordPatch);
            Assert.Equal(51, (double)d.SelectToken("layout.gapRequest_mm"));   // the wider gap stays with the record
            var split = Run("split", d, new JObject { ["plate"] = "base" });
            var options = (JArray)split.Json["options"];
            Assert.NotEmpty(options);
            Assert.Null(split.RecordPatch);
            var chosen = (JObject)options[0];
            Assert.All(chosen["pieces"], p => Assert.Equal("pass", (string)p["mass"]));
            d["structural"]["split"] = chosen;
            var again = Run("size", d);
            var bolts = (JArray)again.RecordPatch.SelectToken("hardware.bolts");
            Assert.NotEmpty(bolts);
            Assert.All(bolts, b => Assert.InRange((double)b["preloadFraction"], 0.05, 0.60));      // aluminium joint: never above 0.60 F_p (A1)
            Assert.All(bolts, b => Assert.True((double)b["preloadBand_kN"][1] > (double)b["preloadBand_kN"][0]));
            Assert.Contains(again.Checks, c => ((string)c["id"]).StartsWith("KEY-BEAR-X-"));     // A3: joints at LC1x
            Assert.Contains(again.Checks, c => ((string)c["id"]).StartsWith("BOLT-SEP-X-"));
            Assert.DoesNotContain(again.Checks, c => ((string)c["id"]).StartsWith("BOLT") && (string)c["loadCase"] != "LC1x" && (string)c["status"] == "fail");
        }

        [Fact]
        public void The_as_built_check_uses_measured_walls_and_reports_their_margins()
        {
            var d = E2Design();
            var size = Run("size", d);
            MoldRecord.DeepMerge(d, size.RecordPatch);
            d["structural"]["asBuilt"] = new JObject { ["walls"] = new JObject { ["V-B"] = 3.0, ["U-B"] = 38.1 } };
            var check = Run("check", d);
            var stress = check.Checks.Single(c => (string)c["id"] == "WALL-STRESS-V-B");
            var sized = size.Checks.Single(c => (string)c["id"] == "WALL-STRESS-V-B");
            Assert.True((double)stress["demand"] > 5 * (double)sized["demand"]);
            Assert.Equal("5", (string)check.RecordPatch["checks"][0]["stage"]);
            Assert.True((bool)check.RecordPatch.SelectToken("structural.checkedAsBuilt"));

            // the bodies mold_bodies measured replace the planned pieces
            d["bodies"] = new JArray(
                new JObject { ["id"] = "BASE-1", ["role"] = "base", ["mass_kg"] = 24.0, ["box_mm"] = new JArray(431.8, 190.5, 44.45) },
                new JObject { ["id"] = "LID-1", ["role"] = "lid", ["mass_kg"] = 4.9, ["box_mm"] = new JArray(431.8, 190.5, 22.225) });
            var bodies = Run("check", d);
            Assert.Contains(bodies.Checks, c => (string)c["id"] == "WEIGHT-BASE-1" && (string)c["status"] == "fail" && (double)c["demand"] == 24.0);
            Assert.Contains(bodies.Checks, c => (string)c["id"] == "WEIGHT-LID" && (double)c["demand"] == 4.9);
        }

        [Fact]
        public void A_pressure_field_above_the_uniform_pressure_drives_the_walls()
        {
            var d = E2Design();
            var plain = Run("size", d).RecordPatch.SelectToken("structural.results.walls[0].w_struct_mm");
            d["structural"] = new JObject { ["loadCases"] = new JObject { ["LC1"] = new JObject { ["pressureField"] = new JArray(new JObject { ["bodyId"] = "BASE-1", ["point"] = new JArray(0, 0, 0), ["normal"] = new JArray(1, 0, 0), ["p"] = 5.0 }) } } };
            var r = Run("size", d);
            Assert.True((double)r.RecordPatch.SelectToken("structural.results.walls[0].w_struct_mm") > (double)plain);
            Assert.Contains("H3", r.Text);
        }

        [Fact]
        public void Rails_support_sizes_the_base_as_a_span()
        {
            var d = E2Design();
            var r = Run("size", d, null, new JObject { ["support"] = "B", ["rail_span_mm"] = 150 });
            var span = r.RecordPatch.SelectToken("structural.results.plates.span");
            Assert.Equal("Rails", (string)span["bc"]);
            Assert.Contains(r.Checks, c => (string)c["id"] == "SPAN-STRESS");
        }

        [Fact]
        public void Metric_documents_use_metric_screws_stock_and_text()
        {
            var d = E2Design();
            d["units"]["display"] = "metric";
            var r = Run("size", d);
            Assert.Contains(" mm", r.Text);
            Assert.DoesNotContain(" in (", r.Text);
            var t = (double)r.RecordPatch.SelectToken("structural.results.plates.base.t_mm");
            Assert.Equal(Math.Round(t), t, 9);                         // whole millimetres
        }

        [Fact]
        public void Partial_cavity_loading_table_scales_the_gauge()
        {
            var r = Run("size", Rubber());
            var rows = (JArray)r.RecordPatch.SelectToken("structural.results.partialLoading.rows");
            Assert.Equal(8, rows.Count);
            Assert.Equal((double)rows[0]["gauge_MPa"] / 8 * 4, (double)rows[4]["gauge_MPa"], 3);
            Assert.InRange((int)r.RecordPatch.SelectToken("structural.results.partialLoading.neverFewerThan"), 1, 8);
            Assert.True((int)rows[7]["patterns"] == 0, "one cavity of eight cannot be centred");
        }

        [Fact]
        public void Lookup_answers_compactly()
        {
            Assert.Contains("TDI-polyether / MBOCA 90A", Run("lookup", null, new JObject { ["part"] = "pu-tdi-ether-90a" }).Text);
            var m = Run("lookup", null, new JObject { ["mold"] = "6061-T651", ["temp_c"] = 150 });
            Assert.Contains("ok-derated", m.Text);
            Assert.Equal(215.3, (double)m.Json["Sy_MPa"], 1);
            Assert.Contains("washer", Run("lookup", null, new JObject { ["fastener"] = "1/2-13" }).Text);
            Assert.Contains("inch", Run("lookup", null, new JObject { ["stock"] = "6061_T651_plate" }).Text);
            Assert.Contains("(default)", Run("lookup", null, new JObject { ["family"] = "EPDM" }).Text);
            Assert.Throws<ArgumentException>(() => Run("lookup", null, new JObject()));
        }

        [Fact]
        public void Every_check_type_and_status_is_one_the_record_accepts()
        {
            foreach (var design in new[] { E2Design(), Rubber() })
                foreach (var c in Run("size", design).Checks)
                {
                    Assert.Contains((string)c["type"], Check.Types);
                    Assert.Contains((string)c["status"], Check.Statuses);
                    Assert.StartsWith("mold_calc@", (string)c["source"]);
                }
        }
    }
}

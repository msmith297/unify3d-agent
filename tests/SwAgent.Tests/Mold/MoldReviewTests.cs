using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// Regression tests for the Track A1 review: payload units, input precedence over values written back, the maximum
    /// gauge across actions, replace semantics of calculator lists, broken numbers, the press rating, partial loading,
    /// dowel fits by plate grade, F2 sections, LC1x on pins, S61, joint bolts, split lids, and the smaller items.
    /// </summary>
    public class MoldReviewTests
    {
        private static CalcResult Run(string action, JObject design, JObject query = null, JObject overrides = null) => MoldCalc.Run(action, design, query, overrides, Data);

        /// <summary>Runs an action and merges its patch into the design, as mold_calc with record = true does.</summary>
        private static CalcResult RunMerge(string action, JObject design, JObject query = null, JObject overrides = null)
        {
            var r = Run(action, design, query, overrides);
            if (r.RecordPatch != null) MoldRecord.DeepMerge(design, r.RecordPatch);
            return r;
        }

        internal static JObject Rubber() => new JObject
        {
            ["units"] = new JObject { ["display"] = "imperial" },
            ["inputs"] = new JObject
            {
                ["press_pressure"] = 1500 * Si.PsiToMPa, ["press_pressure_is"] = "Gauge reading", ["press_rated_force"] = 50 * Si.UsTonToKN,
                ["press_rated_gauge"] = 3000 * Si.PsiToMPa, ["press_temp"] = 150, ["cavities"] = 8, ["cycle_life"] = 20000,
                ["mold_material"] = "Tool steel", ["mold_grade"] = "P20 pre-hard", ["part_family"] = "EPDM", ["part_grade"] = "EPDM sulfur 70A",
                ["platen_size"] = new JArray(610, 610), ["daylight"] = 300,
            },
            ["part"] = new JObject
            {
                ["volume_mm3"] = Math.PI * (625 - 225) * 5, ["projectedArea_mm2"] = Math.PI * (625 - 225), ["perimeter_mm"] = 2 * Math.PI * 40,
                ["box_mm"] = new JArray(50, 50, 5), ["floorRadius_mm"] = 0.5,
            },
        };

        // ---------------------------------------------------------------- 1. payload {value: display, si}

        [Fact]
        public void Payload_entries_with_a_display_value_and_si_are_converted_by_quantity()
        {
            Assert.Equal(13.7895, Si.Num(new JObject { ["value"] = "2000 psi (13.79 MPa)", ["si"] = 13789514.6 }, "pressure").Value, 4);
            Assert.Equal(300.0, Si.Num(new JObject { ["value"] = "300 mm (0.3 m)", ["si"] = 0.3 }, "length").Value, 9);
            Assert.Equal(6.0, Si.Num(new JObject { ["value"] = "6 min", ["si"] = 360 }, "time").Value, 9);
            Assert.Equal(1200.0, Si.Num(new JObject { ["value"] = "1200 cP", ["si"] = 1.2 }, "viscosity").Value, 9);
            Assert.Equal(0.5, Si.Num(new JObject { ["value"] = "0.5 deg", ["si"] = 0.5 * Math.PI / 180 }, "angle").Value, 9);
            Assert.Equal(55.0, Si.Num(new JObject { ["si"] = 0.55 }, "length_cm").Value, 9);
            // without a quantity the bare SI number of a dimensional value is never returned: the value says its unit
            Assert.Equal(13.7895, Si.Num(new JObject { ["value"] = "2000 psi (13.79 MPa)", ["si"] = 13789514.6 }).Value, 4);
            Assert.Equal(25.4, Si.Num(new JObject { ["value"] = 1.0, ["unit"] = "in", ["si"] = 0.0254 }).Value, 9);
            Assert.Equal(4.0, Si.Num(new JObject { ["value"] = "4", ["si"] = 4 }).Value, 9);                         // a count
            // sizes: a unit written once applies to every number; SI arrays are metres
            Assert.Equal(new[] { 609.6, 609.6 }, Si.Lengths("24 x 24 in").Select(x => Math.Round(x, 6)));
            Assert.Equal(new[] { 609.6, 609.6 }, Si.Lengths("24 x 24 in (610 x 610 mm)").Select(x => Math.Round(x, 6)));
            Assert.Equal(new[] { 610.0, 500.0 }, Si.Lengths("610 x 500"));
            Assert.Equal(new[] { 610.0, 500.0 }, Si.Lengths(new JObject { ["value"] = "24 x 19.7 in", ["si"] = new JArray(0.61, 0.5) }).Select(x => Math.Round(x, 6)));
        }

        [Fact]
        public void A_design_with_payload_inputs_reads_the_same_as_plain_numbers()
        {
            var plain = E2Design();
            plain["inputs"]["pot_life_min"] = 6;
            plain["inputs"]["cure_min"] = 30;
            plain["inputs"]["viscosity_cp"] = 1200;
            plain["inputs"]["draft_min_deg"] = 1.0;
            plain["inputs"]["lid_reach"] = 400;
            var payload = (JObject)plain.DeepClone();
            var i = (JObject)payload["inputs"];
            i["press_pressure"] = new JObject { ["value"] = "2000 psi (13.79 MPa)", ["si"] = 2000 * Si.PsiToMPa * 1e6 };
            i["press_rated_force"] = new JObject { ["value"] = "50 US ton (444.8 kN)", ["si"] = 444822.16152605 };
            i["press_rated_gauge"] = new JObject { ["value"] = "3000 psi", ["si"] = 3000 * Si.PsiToMPa * 1e6 };
            i["pot_life_min"] = new JObject { ["value"] = "6 min", ["si"] = 360 };
            i["cure_min"] = new JObject { ["value"] = "30 min", ["si"] = 1800 };
            i["viscosity_cp"] = new JObject { ["value"] = "1200 cP", ["si"] = 1.2 };
            i["draft_min_deg"] = new JObject { ["value"] = "1 deg", ["si"] = Math.PI / 180 };
            i["lid_reach"] = new JObject { ["value"] = "15.75 in (400 mm)", ["si"] = 0.4 };
            var a = Design.Read(plain, Data);
            var b = Design.Read(payload, Data);
            Assert.Equal(a.PressValue, b.PressValue, 6);
            Assert.Equal(a.RatedForce_kN, b.RatedForce_kN, 6);
            Assert.Equal(a.RatedGauge_MPa, b.RatedGauge_MPa, 6);
            Assert.Equal(6.0, b.PotLifeMin, 9);
            Assert.Equal(30.0, b.CureMin, 9);
            Assert.Equal(1200.0, b.ViscosityCp.Value, 9);
            Assert.Equal(1.0, b.DraftMinDeg, 9);
            Assert.Equal(40.0, b.LidReach_cm.Value, 9);
            Assert.Equal(Run("size", plain).Text, Run("size", payload).Text);
        }

        // ---------------------------------------------------------------- 2. inputs win over values written back

        [Fact]
        public void Edited_inputs_win_over_what_an_earlier_run_wrote_into_the_record()
        {
            var d = E2Design();
            RunMerge("process", d);
            Assert.Equal("pu-tdi-ether-90a", (string)d.SelectToken("materials.part.id"));
            d["inputs"]["shrink_pct"] = 2.1;
            var p = RunMerge("process", d);
            Assert.Equal(2.1, (double)p.Json.SelectToken("shrink.pct"), 9);
            Assert.NotEqual(1.014288, (double)p.Json.SelectToken("shrink.scale[0]"), 6);

            d["inputs"]["shrink_basis"] = "Measured in my molds at this temperature";
            Assert.Equal(Shrink.ParseBasis("Measured in my molds at this temperature").ToString(), Shrink.ParseBasis((string)Run("process", d).Json.SelectToken("shrink.basis")).ToString());

            d["inputs"]["part_grade"] = "epdm-s-70a";
            d["inputs"].Value<JObject>().Remove("shrink_pct");
            d["inputs"].Value<JObject>().Remove("shrink_basis");
            var e = Run("process", d);
            Assert.Equal("B", (string)e.Json.SelectToken("process.class"));
            Assert.Equal("epdm-s-70a", (string)e.RecordPatch.SelectToken("materials.part.id"));
            // the old material's `used` values never carry over to the new material
            var row = Data.PartMaterial("epdm-s-70a");
            Assert.Equal((double)row.SelectToken("shrinkPct.typ"), (double)e.Json.SelectToken("shrink.pct"), 9);

            var unknown = E2Design();
            RunMerge("process", unknown);
            unknown["inputs"]["part_grade"] = "unobtainium";
            Assert.Throws<ArgumentException>(() => Run("process", unknown));        // never silently the old material
        }

        // ---------------------------------------------------------------- 3. press keeps the abuse caps of size/check

        [Fact]
        public void A_later_press_run_keeps_the_wall_abuse_cap_on_the_setup_sheet()
        {
            var d = E2Design();
            RunMerge("size", d);
            d["structural"]["asBuilt"] = new JObject { ["walls"] = new JObject { ["V-B"] = 6.0, ["U-B"] = 6.0 } };
            var check = RunMerge("check", d);
            var capCheck = Si.Num(d.SelectToken("process.gauge.max_MPa")).Value;
            Assert.Contains("wall abuse", (string)d.SelectToken("process.gauge.maxBasis"));
            Assert.True(capCheck < (double)d.SelectToken("inputs.press_rated_gauge"));
            Assert.Contains(d.SelectToken("structural.gauge.caps"), c => (string)c["basis"] == "wall abuse LC1x (S24)" && c["pCavity_MPa"].Type == JTokenType.Float);

            var press = RunMerge("press", d);
            Near(capCheck, (double)d.SelectToken("process.gauge.max_MPa"), 0.01, "cap after press");
            Assert.Contains("wall abuse", (string)d.SelectToken("process.gauge.maxBasis"));
            Assert.Contains(press.Checks, c => (string)c["id"] == "PRESS-GAUGE-MAX");
            // the user's gauge (2000 psi) is above the wall cap: the press run says so too
            Assert.Contains(press.Checks, c => (string)c["id"] == "PRESS-SETTING" && (string)c["status"] == "warn");
            var sheet = SetupSheet.Build(d, Data);
            Near(capCheck, (double)sheet.SelectToken("press.gaugeMax_MPa"), 0.01, "cap on the sheet");

            // a bigger press: the stored cavity-pressure cap converts with the new ram area
            d["inputs"]["press_rated_force"] = 2 * (double)d["inputs"]["press_rated_force"];
            RunMerge("press", d);
            Near(capCheck / 2, (double)d.SelectToken("process.gauge.max_MPa"), 0.01, "cap with the bigger press");
        }

        // ---------------------------------------------------------------- 4. calculator lists are replaced, not accumulated

        [Fact]
        public void A_fixed_failure_and_a_removed_cavity_do_not_linger_in_the_record()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mold-review-" + Guid.NewGuid().ToString("N"));
            try
            {
                var d = E2Design();
                var rec = MoldRecord.Init(dir, "x.SLDPRT", (JObject)d["inputs"], "1.0.0", "0");
                rec.Merge(new JObject { ["part"] = d["part"].DeepClone(), ["layout"] = d["layout"].DeepClone() });
                rec.Json["inputs"]["press_pressure"] = 3500 * Si.PsiToMPa;                  // above the 3000 psi rating
                var bad = Run("press", rec.Json);
                Assert.Contains(bad.Checks, c => (string)c["id"] == "PRESS-GAUGE-RATING" && Check.Fails(c));
                Assert.Contains("PRESS-GAUGE-RATING", rec.Merge(bad.RecordPatch));
                rec.Json["inputs"]["press_pressure"] = 2000 * Si.PsiToMPa;
                var status = rec.Merge(Run("press", rec.Json).RecordPatch);
                Assert.DoesNotContain("PRESS-GAUGE-RATING", status);
                Assert.DoesNotContain(rec.Json["checks"], c => (string)c["id"] == "PRESS-GAUGE-RATING");
                // a check another tool wrote survives a calculator run
                rec.Merge(new JObject { ["checks"] = new JArray(new JObject { ["id"] = "DRAFT-F12", ["source"] = "mold_part@1", ["type"] = "draft", ["status"] = "warn" }) });

                rec.Merge(Run("size", rec.Json).RecordPatch);
                Assert.Equal(2, ((JArray)rec.Json.SelectToken("layout.cavities")).Count);
                rec.Json["inputs"]["cavities"] = 1;
                rec.Json["layout"]["patternRequest"] = null;
                rec.Merge(Run("size", rec.Json).RecordPatch);
                Assert.Single((JArray)rec.Json.SelectToken("layout.cavities"));
                var check = Run("check", rec.Json);
                Assert.DoesNotContain(check.Checks, c => Check.Fails(c) && ((string)c["id"]).StartsWith("WALL-"));
                Assert.Contains(rec.Json["checks"], c => (string)c["id"] == "DRAFT-F12");
                Assert.Empty(rec.Validate());
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }

        [Fact]
        public void Replace_directives_and_empty_arrays_clear_lists()
        {
            var t = new JObject
            {
                ["hardware"] = new JObject { ["bolts"] = new JArray(new JObject { ["id"] = "B-BASE-1" }, new JObject { ["id"] = "B-BASE-2" }), ["note"] = "keep" },
                ["layout"] = new JObject { ["cavities"] = new JArray(new JObject { ["id"] = "C1" }, new JObject { ["id"] = "C2" }) },
                ["checks"] = new JArray(new JObject { ["id"] = "PRESS-X", ["source"] = "mold_calc@1.0.0" }, new JObject { ["id"] = "WALL-X", ["source"] = "mold_calc@1.0.0" }, new JObject { ["id"] = "PRESS-Y", ["source"] = "mold_bodies@1" }),
            };
            MoldRecord.DeepMerge(t, new JObject
            {
                [MoldRecord.ReplaceKey] = new JArray("hardware.bolts"), [MoldRecord.ReplaceChecksKey] = new JArray("press"),
                ["hardware"] = new JObject { ["bolts"] = new JArray(new JObject { ["id"] = "B-BASE-1", ["count"] = 4 }) },
                ["layout"] = new JObject { ["cavities"] = new JArray() },
            });
            Assert.Single((JArray)t.SelectToken("hardware.bolts"));
            Assert.Equal("keep", (string)t.SelectToken("hardware.note"));
            Assert.Empty((JArray)t.SelectToken("layout.cavities"));
            Assert.Equal(new[] { "WALL-X", "PRESS-Y" }, t["checks"].Select(c => (string)c["id"]));
            Assert.Null(t[MoldRecord.ReplaceKey]);
            Assert.Null(t[MoldRecord.ReplaceChecksKey]);
            // the cure checks belong to the process step, which every action re-runs
            Assert.Equal("process", Check.Group("CURE-TEMP-HIGH"));
            Assert.Equal("process", Check.Group("CHG-POUR"));
            Assert.Equal("structural", Check.Group("WALL-DEFL-V-B"));
            Assert.Equal(new[] { "press", "process", "scope" }, Run("press", E2Design()).RecordPatch[MoldRecord.ReplaceChecksKey].Select(x => (string)x));
        }

        // ---------------------------------------------------------------- 5. broken numbers never pass

        [Fact]
        public void NaN_infinite_and_negative_demands_fail_and_stay_out_of_the_record()
        {
            var nan = Check.Upper("X", "stress", double.NaN, 100, "MPa", "m");
            Assert.Equal("fail", (string)nan["status"]);
            Assert.Equal(JTokenType.Null, nan["demand"].Type);
            Assert.Equal("fail", (string)Check.Upper("X", "deflection", -0.131, 0.03, "mm", "m")["status"]);
            Assert.Equal("fail", (string)Check.Upper("X", "press", 5, double.NaN, "MPa", "m")["status"]);
            Assert.Equal("pass", (string)Check.Upper("X", "press", 0, 10, "mm", "m")["status"]);                 // a centred load
            Assert.Equal("pass", (string)Check.Lower("X", "boltSeparation", double.PositiveInfinity, 2, "n0", "m")["status"]);
            var lower = Check.Lower("X", "fit", double.NaN, 2, "n0", "m");
            Assert.Equal(JTokenType.Null, lower["demand"].Type);
            Assert.Equal("fail", (string)lower["status"]);

            // a stale layout whose cavities no longer fit the plate is refused with the fix
            var d = E2Design();
            RunMerge("size", d);
            d["layout"]["footprint_mm"] = new JArray(200, 200);
            Assert.Contains("run mold_calc layout or size", Assert.Throws<ArgumentException>(() => Run("check", d)).Message);
        }

        // ---------------------------------------------------------------- 6. the press rating and the force reading

        [Fact]
        public void A_force_reading_needs_its_force_and_the_press_rating_and_never_prints_NaN()
        {
            var d = E2Design();
            d["inputs"]["press_pressure_is"] = "Force reading (tons)";
            Assert.Contains("press_force", Assert.Throws<ArgumentException>(() => Run("press", d)).Message);   // never the psi box read as kN
            d["inputs"]["press_force"] = 30 * Si.UsTonToKN;
            var ok = Run("size", d);
            Assert.DoesNotContain("NaN", ok.Text);
            Assert.DoesNotContain("NaN", ok.RecordPatch.ToString(Formatting.None));
            Near(30 * Si.UsTonToKN, (double)ok.RecordPatch.SelectToken("structural.loadCases.LC2.F_kN"), 0.01, "force");

            var noRating = (JObject)d.DeepClone();
            noRating["inputs"].Value<JObject>().Remove("press_rated_gauge");
            Assert.Contains("press_rated_gauge", Assert.Throws<ArgumentException>(() => Run("press", noRating)).Message);
            noRating["inputs"]["ram_bore"] = 150;                          // or the ram
            Assert.DoesNotContain("NaN", Run("press", noRating).Text);
            var noForce = (JObject)d.DeepClone();
            noForce["inputs"].Value<JObject>().Remove("press_rated_force");
            Assert.Contains("press_rated_force", Assert.Throws<ArgumentException>(() => Run("size", noForce)).Message);
        }

        // ---------------------------------------------------------------- 7. layout vs inputs.cavities

        [Fact]
        public void A_record_layout_that_disagrees_with_the_cavity_count_asks_for_a_new_layout()
        {
            var d = E2Design();
            RunMerge("size", d);
            d["inputs"]["cavities"] = 4;
            d["layout"]["patternRequest"] = null;
            Assert.Contains("run mold_calc layout or size", Assert.Throws<ArgumentException>(() => Run("check", d)).Message);
            Assert.Contains("run mold_calc layout or size", Assert.Throws<ArgumentException>(() => Run("press", d)).Message);
            Assert.Equal(4, ((JArray)Run("size", d).RecordPatch.SelectToken("layout.cavities")).Count);
        }

        // ---------------------------------------------------------------- 8. dowel press fits by plate grade

        [Fact]
        public void Steel_molds_get_the_steel_press_fit_for_their_dowels()
        {
            var d = E2Design();
            d["inputs"]["mold_material"] = "Tool steel";
            d["inputs"]["mold_grade"] = "P20 pre-hard";
            RunMerge("process", d);
            RunMerge("size", d);
            var body = (JObject)MachiningSheet.Build(d, Data)["bodies"].First(b => (string)b["role"] == "base");
            var press = body["holes"].First(h => ((string)h["fit"]).StartsWith("press"));
            Assert.Equal("press (steel plate)", (string)press["fit"]);
            Assert.Contains("0.4997 - 0.5000 in", (string)press["limits"]);

            var metric = E2Design();
            metric["units"]["display"] = "metric";
            metric["inputs"]["mold_material"] = "Tool steel";
            metric["inputs"]["mold_grade"] = "P20 pre-hard";
            RunMerge("size", metric);
            var mb = (JObject)MachiningSheet.Build(metric, Data)["bodies"].First(b => (string)b["role"] == "base");
            Assert.Contains(mb["holes"], h => ((string)h["limits"] ?? "").Contains("N7"));
        }

        // ---------------------------------------------------------------- 9. F2 sections belong to whoever measured them

        [Fact]
        public void A_process_run_never_resets_the_simulator_sections()
        {
            var d = E2Design();
            d["materials"] = new JObject { ["part"] = new JObject { ["rheology"] = new JObject { ["status"] = "measured", ["source"] = "SIM-2" } } };
            var p = Run("process", d);
            Assert.Null(p.RecordPatch.SelectToken("materials.part.rheology"));
            Assert.Equal("unknown", (string)p.RecordPatch.SelectToken("materials.part.kinetics.status"));   // absent: created
            MoldRecord.DeepMerge(d, p.RecordPatch);
            Assert.Equal("measured", (string)d.SelectToken("materials.part.rheology.status"));
        }

        // ---------------------------------------------------------------- 10/11. LC1x on pins, S61

        [Fact]
        public void Pins_carry_the_abuse_case_and_cap_the_gauge()
        {
            var d = E2Design();
            d["part"]["fragile"] = new JArray(new JObject { ["id"] = "K1", ["kind"] = "core_pin", ["d_mm"] = 6, ["h_mm"] = 20 });
            var r = Run("size", d);
            var abuse = r.Checks.Single(c => (string)c["id"] == "PIN-ABUSE-K1");
            Assert.Equal("LC1x", (string)abuse["loadCase"]);
            Assert.Contains(r.RecordPatch.SelectToken("structural.gauge.caps"), c => ((string)c["basis"]).StartsWith("pin/insert abuse"));
            // the pin cap is the pressure at which the pin yields: sigma(p) = Sy at p = cap
            var pin = r.RecordPatch.SelectToken("structural.results.pins[0]");
            var pAbuse = (double)r.RecordPatch.SelectToken("structural.loadCases.LC1x.p_MPa");
            Near((double)pin["sigmaAbuse_MPa"], (double)abuse["demand"], 0.1, "sigma abuse");
            Assert.True((double)pin["sigmaAbuse_MPa"] > 0 && pAbuse > 0);
        }

        [Fact]
        public void S61_flags_thin_steel_walls_cut_offs_by_tier_and_reports_what_it_cannot_judge()
        {
            var d = E2Design();
            d["part"]["fragile"] = new JArray(
                new JObject { ["id"] = "W1", ["kind"] = "steel_wall", ["w_mm"] = 2.0 },
                new JObject { ["id"] = "W2", ["kind"] = "steel_wall", ["w_mm"] = 5.0 },
                new JObject { ["id"] = "X1", ["kind"] = "cut_off" },
                new JObject { ["id"] = "P1", ["kind"] = "pinch_off" },
                new JObject { ["id"] = "Q1", ["kind"] = "core_pin" });
            var r = Run("size", d);
            var pins = (JArray)r.RecordPatch.SelectToken("structural.results.pins");
            bool Insert(string id) => (bool)pins.Single(p => (string)p["id"] == id)["insert"];
            Assert.True(Insert("W1"));
            Assert.False(Insert("W2"));
            Assert.False(Insert("X1"));                                   // 5,000 cycles: tier T2
            Assert.True(Insert("P1"));                                    // pinch-off carries the closing load
            Assert.Contains(r.Checks, c => (string)c["id"] == "FRAGILE-Q1" && (string)c["status"] == "warn");
            d["inputs"]["cycle_life"] = 50000;                             // T3
            var t3 = (JArray)Run("size", d).RecordPatch.SelectToken("structural.results.pins");
            Assert.True((bool)t3.Single(p => (string)p["id"] == "X1")["insert"]);
        }

        // ---------------------------------------------------------------- 12/13. joint bolts: A3 fallback and spacing

        private static JObject SplitBase(double gaugePsi)
        {
            var d = E2Design();
            d["inputs"]["cavities"] = 6;
            d["inputs"]["press_pressure"] = gaugePsi * Si.PsiToMPa;
            d["layout"] = new JObject { ["patternRequest"] = "3x2" };
            d["part"]["box_mm"] = new JArray(152.4, 101.6, 50.8);
            d["part"]["volume_mm3"] = 152.4 * 101.6 * 50.8;
            RunMerge("size", d, null, new JObject { ["gap_mm"] = 51 });
            var opt = (JObject)Run("split", d, new JObject { ["plate"] = "base" }).Json["options"][0];
            MoldRecord.DeepMerge(d, new JObject { [MoldRecord.ReplaceKey] = new JArray("structural.split.base"), ["structural"] = new JObject { ["split"] = new JObject { ["base"] = opt } } });
            return d;
        }

        [Fact]
        public void Hot_aluminium_joints_that_fail_only_the_abuse_case_cap_the_gauge_instead_of_failing()
        {
            var d = SplitBase(3000);                                       // the press at its rating: a large abuse load
            var r = Run("size", d);
            Assert.DoesNotContain(r.Checks, c => ((string)c["id"]).StartsWith("BOLTS-"));
            Assert.DoesNotContain(r.Checks, c => ((string)c["id"]).StartsWith("BOLT") && (string)c["loadCase"] != "LC1x" && Check.Fails(c));
            var bolts = (JArray)r.RecordPatch.SelectToken("hardware.bolts");
            Assert.NotEmpty(bolts);
            // either every joint passes the abuse case, or the gauge is capped by a joint (A3) and the abuse checks warn
            var abuseFails = r.Checks.Where(c => (string)c["loadCase"] == "LC1x" && ((string)c["id"]).StartsWith("BOLT") && (string)c["status"] != "pass").ToList();
            if (abuseFails.Count > 0)
                Assert.Contains(r.RecordPatch.SelectToken("structural.gauge.caps"), c => ((string)c["basis"]).StartsWith("joint abuse"));
        }

        [Fact]
        public void A_joint_without_an_abuse_window_still_has_its_LC1_window_and_an_abuse_capacity()
        {
            // the A3 fallback the joint sizing uses: hot aluminium, a large abuse load per bolt
            var cfg = Cfg();
            var b = BoltSpec.From(Data.Fastener("1/2-13"), cfg);
            var g = AtTemperature.Get(Data.MoldGrade("al_6061_t651"), 100);
            var j = new JointInput
            {
                Bolt = b, Tapped = true, TopThickness = 20 + b.WasherT, TappedThickness = 2.5 * b.D,
                Layers = new List<Layer> { new Layer { T = 20, E = g.E, Aluminium = true } }, AlphaMember = g.Cte_perK,
                TempC = 100, P = 2000, PAbuse = 80000, MemberSuT = g.Su,
            };
            var both = Bolts.Design(j, cfg, true);
            Assert.False(both.FractionHi >= both.FractionLo && both.AbusePasses, "an abuse window exists - the case does not test the fallback");
            var lc1 = Bolts.Design(j, cfg, false);
            Assert.True(lc1.Passes, lc1.Failing);
            j.PreloadFraction = lc1.Fraction;
            var cap = Bolts.AbuseCapacity(j, cfg);
            Assert.InRange(cap, j.P, j.PAbuse);
            // at the capacity the abuse checks just pass at the LC1 preload
            j.PAbuse = cap * 0.999;
            Assert.True(Bolts.Evaluate(j, cfg, lc1.Fraction).AbusePasses);
            j.PAbuse = cap * 1.01;
            Assert.False(Bolts.Evaluate(j, cfg, lc1.Fraction).AbusePasses);
        }

        [Fact]
        public void Joint_screws_keep_the_edge_distance_and_spacing()
        {
            var d = SplitBase(2000);
            var r = Run("size", d);
            var keys = (JArray)r.RecordPatch.SelectToken("structural.results.keys");
            foreach (var b in ((JArray)r.RecordPatch.SelectToken("hardware.bolts")).OfType<JObject>())
            {
                var piece = ((string)b["joint"]).Split('/')[1];
                var L = (double)keys.Single(k => (string)k["piece"] == piece)["keyLength_mm"];
                var dia = Data.Fastener((string)b["size"]);
                var dMm = dia["d_in"] != null ? (double)dia["d_in"] * Si.InToMm : (double)dia["d_mm"];
                var perRow = (int)Math.Floor((L - 3 * dMm) / (4 * dMm) + 1e-9) + 1;
                Assert.True((int)b["count"] <= perRow * (int)b["rows"], $"{b["id"]}: {b["count"]} x {b["size"]} on {L} mm");
                Assert.Equal(1.5 * dMm, (double)b["edgeDistance_mm"], 3);
            }
        }

        // ---------------------------------------------------------------- 14. split lids

        [Fact]
        public void A_class_B_lid_splits_by_its_own_plan_and_a_class_A_lid_never_does()
        {
            Assert.Contains("never split", Assert.Throws<ArgumentException>(() => Run("split", E2Design(), new JObject { ["plate"] = "lid", ["box_mm"] = new JArray(400, 300, 30) })).Message);

            var d = Rubber();
            RunMerge("size", d, null, new JObject { ["gap_mm"] = 40 });
            var split = Run("split", d, new JObject { ["plate"] = "lid" });
            Assert.Equal("lid", (string)split.Json["plate"]);
            Assert.Contains("structural.split.lid", split.Text);
            var opts = (JArray)split.Json["options"];
            Assert.NotEmpty(opts);
            Assert.All(opts[0]["pieces"], p => Assert.StartsWith("LID-", (string)p["id"]));
            Assert.All(opts[0]["subplate"]["pieces"], p => Assert.StartsWith("LSUB-", (string)p["id"]));
            MoldRecord.DeepMerge(d, new JObject { ["structural"] = new JObject { ["split"] = new JObject { ["lid"] = opts[0] } } });
            var r = Run("size", d);
            var pieces = (JArray)r.RecordPatch.SelectToken("structural.results.pieces");
            Assert.Contains(pieces, p => (string)p["id"] == "BASE-1");                          // the base stays whole
            Assert.Contains(pieces, p => (string)p["id"] == "LID-2" && (string)p["role"] == "lid");
            Assert.Contains(pieces, p => ((string)p["id"]).StartsWith("LSUB-") && (string)p["role"] == "subplate");
            var bolts = (JArray)r.RecordPatch.SelectToken("hardware.bolts");
            Assert.Contains(bolts, b => ((string)b["joint"]).StartsWith("LSUB/LID-"));
            MoldRecord.DeepMerge(d, r.RecordPatch);
            var mach = MachiningSheet.Build(d, Data);
            var lsub = mach["bodies"].First(b => ((string)b["id"]).StartsWith("LSUB-"));
            Assert.Contains(lsub["holes"], h => ((string)h["kind"]).StartsWith("counterbored"));
        }

        // ---------------------------------------------------------------- smaller items

        [Fact]
        public void Template_paths_index_arrays_with_dots()
        {
            var ctx = new JObject { ["bodies"] = new JArray(new JObject { ["id"] = "BASE-1" }, new JObject { ["id"] = "LID-1" }) };
            var unresolved = new List<string>();
            Assert.Equal("BASE-1 LID-1", Template.Render("{{bodies.0.id}} {{bodies[1].id}}", ctx, true, unresolved));
            Assert.Empty(unresolved);
        }

        [Fact]
        public void Lookup_refuses_numbers_above_250_C_and_hands_out_copies()
        {
            var hot = Run("lookup", null, new JObject { ["mold"] = "6061-T651", ["temp_c"] = 300 });
            Assert.Contains("no data", hot.Text);
            Assert.DoesNotContain("Sy ", hot.Text);
            Assert.Equal(JTokenType.Null, hot.Json["Sy_MPa"].Type);
            var j = Run("lookup", null, new JObject { ["part"] = "pu-tdi-ether-90a" }).Json;
            j["name"] = "MUTATED";
            Assert.NotEqual("MUTATED", (string)Data.PartMaterial("pu-tdi-ether-90a")["name"]);
            var f = Run("lookup", null, new JObject { ["fastener"] = "1/2-13" }).Json;
            f["size"] = "MUTATED";
            Assert.Equal("1/2-13", (string)Data.Fastener("1/2-13")["size"]);
        }

        [Fact]
        public void A_short_last_row_keeps_the_load_centre_on_the_plate_centre()
        {
            var lay = CavityLayout.Compute(new LayoutInput { Cavities = 3, PartU = 100, PartV = 100, GapU = 30, GapV = 30, EdgeU = 40, EdgeV = 40, Pattern = "2x2" }, Data);
            Assert.Equal(0, lay.Cavities.Average(c => c.V), 9);
            Assert.Equal(0, lay.Cavities.Average(c => c.U), 9);
            Assert.True(lay.EdgeV >= 40 - 1e-9, "edge " + lay.EdgeV);
            // every cavity stays inside the plate with at least the edge steel
            Assert.All(lay.Cavities, c => Assert.True(lay.FootprintW / 2 - (Math.Abs(c.V) + 50) >= 40 - 1e-9));
            Assert.Equal(0, CavityLayout.RowShift(4, 2, 2, 130), 9);
        }

        [Fact]
        public void Class_B_steel_between_cavities_and_the_edge_hold_the_minimum_wall_and_the_stop_pads()
        {
            var d = Rubber();
            d["inputs"]["mold_material"] = "Aluminium + steel inserts";
            d["inputs"]["mold_grade"] = "6061-T651";
            d["inputs"]["press_temp"] = 120;
            var r = Run("size", d);
            var gap = (JArray)r.RecordPatch.SelectToken("layout.gap_mm");
            Assert.True((double)gap[0] >= 15 - 1e-9 && (double)gap[1] >= 15 - 1e-9, gap.ToString(Formatting.None));
            var side = (double)r.RecordPatch.SelectToken("hardware.stops[0].side_mm");
            var edge = (JArray)r.RecordPatch.SelectToken("layout.edge_mm");
            Assert.True((double)edge[0] >= side && (double)edge[1] >= side, "pads " + side + " in edges " + edge.ToString(Formatting.None));
        }

        [Fact]
        public void Pressure_fields_as_tuples_are_read_and_a_file_path_warns()
        {
            var d = E2Design();
            d["structural"] = new JObject { ["loadCases"] = new JObject { ["LC1"] = new JObject { ["pressureField"] = new JArray(new JArray(0, 0, 0, 5.0), new JArray(10, 0, 0, 2.0)) } } };
            Assert.Contains("H3", Run("size", d).Text);
            d["structural"]["loadCases"]["LC1"]["pressureField"] = "C:/sim/field.csv";
            Assert.Contains(Run("size", d).Checks, c => (string)c["id"] == "LOAD-PFIELD" && (string)c["status"] == "warn");
        }

        [Fact]
        public void A_closed_moat_must_hold_the_overflow_itself()
        {
            var d = E2Design();
            Assert.DoesNotContain(Run("process", d).Checks, c => (string)c["id"] == "CHG-OVERFLOW");
            d["layout"]["runnersExitPlate"] = false;
            Assert.Contains(Run("process", d).Checks, c => (string)c["id"] == "CHG-OVERFLOW");
            Assert.Contains(Run("process", E2Design(), null, new JObject { ["runners_exit_plate"] = false }).Checks, c => (string)c["id"] == "CHG-OVERFLOW");
        }

        [Fact]
        public void S31_and_S32_invert_and_hold_the_deflection_limit()
        {
            // S32 overhang strip: the thickness found meets the deflection limit exactly; sigma = 3 p c^2/t^2
            var t = Plates.OverhangThickness(2.0, 60, 68900, 0.33, 0.01);
            var ov = Plates.Overhang(2.0, 60, t, 68900, 0.33);
            Assert.Equal(0.01, ov.W, 6);
            Assert.Equal(3 * 2.0 * 60 * 60 / (t * t), ov.Sigma, 9);
            // S31: the largest span for the thickness S29 gave is the span it was sized for
            var sp = Plates.Span("SS4", 1.5, 200, 1.5, 68900, 0.33, 0.01, 0.5);
            Assert.Equal(200, Plates.MaxSpan("SS4", 1.5, sp.T, 1.5, 68900, 0.33, 0.01), 3);
        }

        [Fact]
        public void Lift_threads_are_sized_at_twice_the_weight_on_inserts()
        {
            var d = E2Design();
            var r = Run("size", d);
            var lid = r.RecordPatch.SelectToken("hardware.lift").First(l => (string)l["body"] == "LID-1");
            if (((string)lid["kind"]).Contains("handles"))
            {
                var th = lid["threads"].First();
                Assert.Equal(4, (int)th["count"]);
                Assert.Contains(r.Checks, c => (string)c["id"] == "THREAD-LIFT-LID-1-HANDLE" && (string)c["loadCase"] == "LC4");
            }
            var heavy = SplitBase(2000);
            var hr = Run("size", heavy).RecordPatch.SelectToken("hardware.lift").First(l => (string)l["body"] == "BASE-1");
            Assert.Contains("hoist", (string)hr["kind"]);
            var ring = hr["threads"].First(t => ((string)t["what"]).StartsWith("hoist"));
            Assert.NotNull((string)ring["insert"]);                       // removed routinely: a steel insert in aluminium (S55)
            Assert.Contains("WLL", (string)ring["rating"]);
        }

        [Fact]
        public void A_lid_over_the_lift_limit_drops_the_005_L_rule_on_the_platen()
        {
            var d = E2Design();
            var normal = Run("size", d).RecordPatch.SelectToken("structural.results.plates.lid");
            d["inputs"]["max_lid_mass"] = 1.0;
            var r = Run("size", d);
            var lid = r.RecordPatch.SelectToken("structural.results.plates.lid");
            // E2's 432 mm lid: 0.05 L = 21.6 mm governs; without it the plug + 10 mm / TC well rule leaves about 18 mm
            Assert.True((double)normal["required_mm"] > (double)lid["required_mm"] + 1e-9);
            Assert.Contains("A2.6", r.Text);
            Assert.Contains("A2.6", (string)lid["basis"]);
            Assert.True((double)lid["t_mm"] < (double)normal["t_mm"]);
            Assert.DoesNotContain("A2.6", Run("size", E2Design()).Text);   // under the limit the rule stays
        }

        [Fact]
        public void The_temperature_gate_names_the_governing_check()
        {
            var d = Rubber();
            d["inputs"]["mold_material"] = "Aluminium + steel inserts";
            d["inputs"]["mold_grade"] = "6061-T651";
            d["inputs"]["press_temp"] = 170;
            var temp = Run("size", d).Checks.Single(c => ((string)c["id"]).StartsWith("TEMP-"));
            Assert.Matches(@"governing stress ratio [0-9.]+ of the derated allowable \([A-Z]", (string)temp["note"]);
        }

        [Fact]
        public void Override_rows_merge_key_by_key()
        {
            var shipped = new JObject { ["id"] = "x", ["name"] = "X", ["shrinkPct"] = new JObject { ["min"] = 1, ["typ"] = 1.5, ["max"] = 2 } };
            var merged = MoldData.MergeRow(shipped, new JObject { ["id"] = "x", ["shrinkPct"] = new JObject { ["typ"] = 1.7 } });
            Assert.Equal("X", (string)merged["name"]);
            Assert.Equal(1.7, (double)merged.SelectToken("shrinkPct.typ"));
            Assert.Equal(2, (double)merged.SelectToken("shrinkPct.max"));
            Assert.Equal(1.5, (double)shipped.SelectToken("shrinkPct.typ"));
        }
    }
}

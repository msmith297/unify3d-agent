using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Scripting;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>The dev test tool (tools/dev/page-test) as a fixture: a page that uses every newer option.</summary>
    internal static class PageFixture
    {
        public static string RepoRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "tools", "dev", "page-test", "inputs.json"))) d = d.Parent;
            Assert.True(d != null, "tools/dev/page-test not found above the test folder");
            return d.FullName;
        }

        public static string Dir => Path.Combine(RepoRoot(), "tools", "dev", "page-test");

        public static ToolSpec Spec(string dir = null, List<ToolInputSpec> inputs = null)
        {
            dir = dir ?? Dir;
            return new ToolSpec
            {
                Name = "page-test", Title = "Page test", Dir = dir, Inputs = inputs ?? ToolSpec.LoadInputs(dir),
                Paused = new HashSet<string>(), Context = new HashSet<string>(),
            };
        }

        public static ToolForm Form(bool imperial = false, JObject remembered = null) => new ToolForm(Spec(), new ToolData(Dir), imperial, remembered);

        public static string Temp()
        {
            var dir = Path.Combine(Path.GetTempPath(), "swagent-tooldata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>The value line the agent reads for an input (null when it is left out).</summary>
        public static string Line(ToolForm form, string id)
        {
            var inputs = new ToolInputs();
            form.Collect(inputs);
            var spec = form.Get(id).Spec;
            return (string)ToolRunner.InputEntry(spec, inputs)?["value"];
        }
    }

    /// <summary>Data-backed choices, fills, visibility, units on the page, remembered values and what reaches the macro and agent.</summary>
    public class ToolDataTests
    {
        private static void Near(double expected, double actual, double relative = 1e-9) =>
            Assert.True(Math.Abs(expected - actual) <= relative * Math.Max(1, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");

        [Fact]
        public void Entries_are_distinct_in_file_order()
        {
            var form = PageFixture.Form();
            Assert.Equal(new[] { "Cast PU - TDI polyether", "Rubber - EPDM", "Epoxy - room temperature" }, form.Get("part_family").Items);
            Assert.Equal("Cast PU - TDI polyether", form.Get("part_family").Value);
            Assert.Equal(new[] { "TDI-polyether / MBOCA 90A", "TDI-polyether / MBOCA 70A" }, form.Get("part_grade").Items);
            Assert.False(form.Get("part_family").RowUnique); // a family names several rows
            Assert.True(form.Get("part_grade").RowUnique);
        }

        [Fact]
        public void Row_filter_uses_value_map_and_array_containment()
        {
            var form = PageFixture.Form();
            var grade = form.Get("mold_grade");
            foreach (var f in form.Fields) f.Changes = FieldChange.None;
            Assert.Equal(new[] { "6061-T651", "7075-T651", "MIC-6 cast plate" }, grade.Items); // use contains "plate"
            Assert.Equal("6061-T651", grade.Value); // isDefault
            Assert.Equal(new[] { "P20 pre-hard", "H13 44-50 HRC", "S7 54-56 HRC" }, form.Get("insert_grade").Items);
            Assert.False(form.Get("insert_grade").Hidden);

            Assert.True(form.SetChoice(form.Get("mold_material"), "Tool steel"));
            Assert.Equal(new[] { "P20 pre-hard", "H13 44-50 HRC" }, grade.Items);
            Assert.Equal("P20 pre-hard", grade.Value);
            Assert.Equal("st_p20", (string)grade.Row["id"]);
            Assert.True((grade.Changes & FieldChange.Items) != 0);
            Assert.True(form.Get("insert_grade").ConditionHidden); // ShowWhen: aluminium only
            Assert.True((form.Get("insert_grade").Changes & FieldChange.Visible) != 0);
        }

        [Fact]
        public void A_kept_entry_survives_a_cascade()
        {
            var form = PageFixture.Form();
            var grade = form.Get("mold_grade");
            form.SetChoice(form.Get("mold_material"), "Tool steel");
            form.SetChoice(grade, "H13 44-50 HRC");
            form.SetChoice(form.Get("mold_material"), "Aluminium + steel inserts");
            Assert.Equal("6061-T651", grade.Value); // H13 isn't an aluminium plate
            form.SetChoice(form.Get("mold_material"), "Tool steel");
            Assert.Equal("P20 pre-hard", grade.Value); // back to the default, not the old pick
        }

        [Fact]
        public void Paths_are_dotted_with_indexes_and_case_fallback()
        {
            var row = JObject.Parse("{ \"a\": { \"b\": [ 10, { \"c\": 3 } ] }, \"Name\": \"x\", \"n\": null }");
            Assert.Equal(10, (int)ToolData.ValueAt(row, "a.b.0"));
            Assert.Equal(3, (int)ToolData.ValueAt(row, "a.b.1.c"));
            Assert.Equal("x", (string)ToolData.ValueAt(row, "name"));
            Assert.Null(ToolData.ValueAt(row, "a.zz"));
            Assert.Null(ToolData.ValueAt(row, "n.typ"));
            Assert.Equal(JTokenType.Null, ToolData.ValueAt(row, "n").Type);
        }

        [Fact]
        public void A_grade_fills_its_fields_in_their_spec_units()
        {
            var form = PageFixture.Form();
            Near(1.5, form.Get("shrink_pct").Si);
            Near(1.1, form.Get("sg").Si);
            Near(1.2, form.Get("viscosity_cp").Si); // 1200 cP in Pa*s
            Near(360, form.Get("pot_life_min").Si); // 6 min in s
            Near(1800, form.Get("cure_min").Si);
            Near(0.5 * Math.PI / 180, form.Get("draft_min_deg").Si);
            Near(100, form.Get("press_temp").Si);
            Near(50 * 6894.757293168, form.Get("molding_pressure").Si); // stored in psi: "moldingPressurePsi.typ|psi"
            Assert.True(form.Get("mooney").NotApplicable); // PU has no Mooney value: hidden
            Assert.True(form.Get("mooney").Hidden);
            Assert.False(form.Get("shrink_pct").Edited);
            Assert.Equal("part_grade", form.Get("shrink_pct").FilledBy);
        }

        [Fact]
        public void A_null_in_the_data_hides_the_field_and_reads_n_a()
        {
            var form = PageFixture.Form();
            form.SetChoice(form.Get("part_family"), "Rubber - EPDM");
            Assert.Equal("EPDM 70A (sulfur cure)", form.Get("part_grade").Value); // isDefault of the family
            Assert.True(form.Get("viscosity_cp").Hidden);
            Assert.True(form.Get("pot_life_min").Hidden);
            Assert.False(form.Get("mooney").Hidden);
            Near(60, form.Get("mooney").Si);
            Near(170, form.Get("press_temp").Si);
            Near(1500 * 6894.757293168, form.Get("molding_pressure").Si);

            Assert.Equal("n/a for this grade", PageFixture.Line(form, "viscosity_cp"));
            var inputs = new ToolInputs();
            form.Collect(inputs);
            Assert.True(inputs.Has("viscosity_cp"));
            Assert.Null(inputs.Limit("viscosity_cp"));
            Assert.Equal(7, inputs.Number("viscosity_cp", 7)); // the macro's fallback
            Assert.True(inputs.Hidden("viscosity_cp"));
        }

        [Fact]
        public void An_edit_is_reported_and_a_new_pick_overwrites_it()
        {
            var form = PageFixture.Form();
            var shrink = form.Get("shrink_pct");
            Assert.True(form.SetNumber(shrink, 1.8));
            Assert.True(shrink.Edited);
            Assert.Equal("1.8 % (table 1.5 % - edited by the user)", PageFixture.Line(form, "shrink_pct"));
            Assert.False(form.SetNumber(shrink, 1.8)); // a repeat (e.g. an event for a value the page set) changes nothing

            form.SetChoice(form.Get("part_grade"), "TDI-polyether / MBOCA 70A");
            Near(1.7, shrink.Si); // picking a grade is an explicit action: it overwrites
            Assert.False(shrink.Edited);
            Assert.Equal("1.7 %", PageFixture.Line(form, "shrink_pct"));
        }

        [Fact]
        public void Remembered_values_win_at_start_but_unremembered_fields_follow_the_table()
        {
            var remembered = JObject.Parse(@"{
                ""part_family"": { ""value"": ""Cast PU - TDI polyether"" },
                ""part_grade"": { ""value"": ""TDI-polyether / MBOCA 70A"" },
                ""shrink_pct"": { ""value"": 1.8 },
                ""press_temp"": { ""value"": 140 },
                ""mold_material"": { ""value"": ""Tool steel"" },
                ""mold_grade"": { ""value"": ""H13 44-50 HRC"" },
                ""cycle_life"": { ""value"": 20000 },
                ""daylight"": { ""value"": 450, ""on"": true },
                ""cavities"": { ""value"": 8 }
            }");
            var form = PageFixture.Form(remembered: remembered);
            Assert.Equal("TDI-polyether / MBOCA 70A", form.Get("part_grade").Value);
            Near(1.8, form.Get("shrink_pct").Si); // remembered: not overwritten by the table
            Assert.True(form.Get("shrink_pct").Edited); // ...and differs from the table (1.7)
            Near(100, form.Get("press_temp").Si); // not "Remember": follows the material
            Assert.Equal("Tool steel", form.Get("mold_material").Value);
            Assert.Equal("H13 44-50 HRC", form.Get("mold_grade").Value);
            Near(20000, form.Get("cycle_life").Si);
            Assert.True(form.Get("daylight").On);
            Near(0.45, form.Get("daylight").Si);
            Near(1, form.Get("cavities").Si); // not "Remember"
        }

        [Fact]
        public void Default_entry_order_is_remembered_default_marked_first()
        {
            var dir = PageFixture.Temp();
            File.WriteAllText(Path.Combine(dir, "rows.json"), "[ {\"name\":\"a\"}, {\"name\":\"b\",\"isDefault\":true}, {\"name\":\"c\"} ]");
            File.WriteAllText(Path.Combine(dir, "plain.json"), "[ \"x\", \"y\" ]");
            ToolForm Make(string def, string remembered, string file = "rows.json")
            {
                var inputs = new List<ToolInputSpec> { new ToolInputSpec { Id = "p", Type = "choice", ChoicesFrom = file, Default = def, Remember = true } };
                var rem = remembered == null ? null : new JObject { ["p"] = new JObject { ["value"] = remembered } };
                return new ToolForm(PageFixture.Spec(dir, inputs), new ToolData(dir), false, rem);
            }
            Assert.Equal("a", Make("c", "a").Get("p").Value);
            Assert.Equal("c", Make("c", null).Get("p").Value);
            Assert.Equal("c", Make("c", "gone").Get("p").Value);
            Assert.Equal("b", Make("zzz", null).Get("p").Value);
            Assert.Equal("x", Make(null, null, "plain.json").Get("p").Value); // an array of strings works too
        }

        [Fact]
        public void A_shops_override_replaces_rows_by_id_and_adds_new_ones()
        {
            var dir = PageFixture.Temp();
            var over = PageFixture.Temp();
            Directory.CreateDirectory(Path.Combine(dir, "data"));
            File.WriteAllText(Path.Combine(dir, "data", "m.json"),
                "{ \"schema\": \"1\", \"rows\": [ {\"id\":\"a\",\"x\":1}, {\"id\":\"b\",\"x\":2} ], \"obj\": {\"k1\":1,\"k2\":2}, \"list\": [1,2] }");
            File.WriteAllText(Path.Combine(over, "m.json"),
                "{ \"schema\": \"2\", \"rows\": [ {\"id\":\"b\",\"x\":20}, {\"id\":\"c\",\"x\":3} ], \"obj\": {\"k2\":22,\"k3\":3}, \"list\": [9], \"extra\": true }");
            var data = new ToolData(dir, over);
            var rows = data.Rows("data/m.json#rows");
            Assert.Equal(new[] { "a", "b", "c" }, rows.Select(r => (string)r["id"]));
            Assert.Equal(20, (int)rows[1]["x"]);
            var merged = data.File("data/m.json");
            Assert.Equal("1", (string)merged["schema"]); // everything else comes from the shipped file
            Assert.Equal(new[] { 1, 2 }, merged["list"].Select(x => (int)x));
            Assert.Equal(1, (int)merged["obj"]["k1"]);
            Assert.Equal(22, (int)merged["obj"]["k2"]);
            Assert.Equal(3, (int)merged["obj"]["k3"]);

            // A bad override is ignored, not fatal.
            File.WriteAllText(Path.Combine(over, "m.json"), "{ not json");
            Assert.Equal(2, new ToolData(dir, over).Rows("data/m.json#rows").Count);
            // Paths outside the tool folder are refused.
            Assert.Empty(new ToolData(dir).Rows("../m.json#rows"));
            Assert.False(ToolData.IsSafeRelative("..\\x.json"));
            Assert.False(ToolData.IsSafeRelative("C:\\x.json"));
            Assert.True(ToolData.IsSafeRelative("data/x.json"));
        }

        [Fact]
        public void Info_lines_format_values_in_the_documents_system()
        {
            Assert.Equal("Full strength to 120 °C, derated to 150 °C", PageFixture.Form().Get("grade_info").Caption);
            Assert.Equal("Full strength to 248 °F, derated to 302 °F", PageFixture.Form(imperial: true).Get("grade_info").Caption);
            Assert.Equal("Liquid pour (hot cast) - data confidence medium; molding 0.3447 MPa", PageFixture.Form().Get("material_info").Caption);
            Assert.Equal("Liquid pour (hot cast) - data confidence medium; molding 50 psi", PageFixture.Form(imperial: true).Get("material_info").Caption);

            var form = PageFixture.Form();
            foreach (var f in form.Fields) f.Changes = FieldChange.None;
            form.SetChoice(form.Get("mold_material"), "Tool steel");
            Assert.Equal("Full strength to 300 °C, derated to 400 °C", form.Get("grade_info").Caption);
            Assert.True((form.Get("grade_info").Changes & FieldChange.Caption) != 0);

            Assert.Equal("{nope} and n/a and 2 x", ToolData.Render("{nope} and {gone} and {two} x",
                n => n == "nope" ? ToolData.Unresolved.Instance : n == "two" ? (object)new JValue(2) : null, false));
            Assert.Equal("1 °C", ToolData.FormatIn(33.8, "degF", false));
        }

        [Fact]
        public void Converted_numbers_show_the_documents_system_with_round_defaults()
        {
            var ips = PageFixture.Form(imperial: true);
            var p = ips.Get("press_pressure");
            Assert.Equal("psi", p.Unit.Ascii);
            Assert.Equal(2000, p.Unit.FromSi(p.Si), 9); // 13.79 MPa shows a round 2000 psi
            Assert.Equal("Press pressure (psi)", p.Caption);
            Assert.Equal("2000 psi (13.79 MPa)", PageFixture.Line(ips, "press_pressure"));
            Assert.Equal("212 degF (100 degC)", PageFixture.Line(ips, "press_temp"));
            Assert.Equal("50 US ton (444.8 kN)", PageFixture.Line(ips, "press_rated_force"));
            Assert.Equal("1200 cP (1.2 Pa*s)", PageFixture.Line(ips, "viscosity_cp"));

            var mm = PageFixture.Form();
            Assert.Equal("MPa", mm.Get("press_pressure").Unit.Ascii);
            Near(13.79e6, mm.Get("press_pressure").Si);
            Assert.Equal("13.79 MPa", PageFixture.Line(mm, "press_pressure"));
            Assert.Equal("100 degC", PageFixture.Line(mm, "press_temp"));
            Assert.Equal("45.36 t (444.8 kN)", PageFixture.Line(mm, "press_rated_force"));
            Assert.Equal("50 kN", PageFixture.Line(mm, "clamp_force"));
        }

        [Fact]
        public void A_fields_own_unit_choice_is_kept_and_remembered()
        {
            var form = PageFixture.Form(remembered: JObject.Parse("{ \"press_pressure\": { \"unit\": \"bar\" } }"));
            var p = form.Get("press_pressure");
            Assert.Equal("bar", p.Unit.Ascii); // remembered even though the document is metric
            var si = p.Si;
            p.Changes = FieldChange.None;
            Assert.True(form.SetUnit(p, "kgf/cm2"));
            Assert.Equal(si, p.Si); // the value doesn't move, only its unit
            Assert.True((p.Changes & (FieldChange.Range | FieldChange.Value | FieldChange.Caption)) == (FieldChange.Range | FieldChange.Value | FieldChange.Caption));
            Assert.Equal("Press pressure (kgf/cm²)", p.Caption);
            Assert.False(form.SetUnit(p, "kgf/cm²")); // same unit
            Assert.False(form.SetUnit(p, "lb")); // not offered
            var json = form.RememberJson();
            Assert.Equal("kgf/cm2", (string)json["press_pressure"]["unit"]);
            Near(13.79, (double)json["press_pressure"]["value"]);
            Assert.Equal("140.619 kgf/cm2 (13.79 MPa)", PageFixture.Line(form, "press_pressure"));
        }

        [Fact]
        public void Optional_values_and_sizes()
        {
            var form = PageFixture.Form();
            var platen = form.Get("platen_size");
            Assert.False(platen.On);
            Assert.Equal("not set", PageFixture.Line(form, "platen_size"));
            Assert.Equal("not set", PageFixture.Line(form, "daylight"));
            var inputs = new ToolInputs();
            form.Collect(inputs);
            Assert.Null(inputs.Size("platen_size"));
            Assert.Null(inputs.Limit("daylight"));
            Assert.Equal(new[] { 0.6, 0.4, 0.15 }, inputs.Size("max_plate_size").Select(v => Math.Round(v, 9)));
            Assert.Equal("600 x 400 x 150 mm", PageFixture.Line(form, "max_plate_size"));
            Near(23, inputs.Limit("max_plate_mass").Value);

            platen.Changes = FieldChange.None;
            Assert.True(form.SetOn(platen, true));
            Assert.True((platen.Changes & FieldChange.Enabled) != 0);
            Assert.Equal("610 x 610 mm", PageFixture.Line(form, "platen_size"));
            Assert.True(form.SetSize(platen, 1, 0.5));
            Assert.Equal("610 x 500 mm", PageFixture.Line(form, "platen_size"));

            var ips = PageFixture.Form(imperial: true);
            Assert.Equal("23.62 x 15.75 x 5.906 in (600 x 400 x 150 mm)", PageFixture.Line(ips, "max_plate_size"));
            Assert.Equal("50.7 lb (23 kg)", PageFixture.Line(ips, "max_plate_mass"));
            Assert.Equal("3000 psi (20.68 MPa)", PageFixture.Line(ips, "press_rated_gauge")); // 20.68 MPa is 3000 psi rounded: snapped back
        }

        [Fact]
        public void Data_choices_tell_the_agent_their_row()
        {
            var form = PageFixture.Form();
            Assert.Equal("6061-T651 [al_6061_t651]", PageFixture.Line(form, "mold_grade")); // long citations stay in the row
            Assert.Equal("TDI-polyether / MBOCA 90A [pu-tdi-ether-90a; data confidence medium; sources S19, S20, K-PU]", PageFixture.Line(form, "part_grade"));
            Assert.Equal("Cast PU - TDI polyether", PageFixture.Line(form, "part_family"));
            Assert.Equal("5000", PageFixture.Line(form, "cycle_life"));

            var inputs = new ToolInputs();
            form.Collect(inputs);
            Assert.Equal("pu-tdi-ether-90a", (string)inputs.Row("part_grade")["id"]);
            Assert.Equal("TDI-polyether / MBOCA 90A", inputs.Text("part_grade"));
            var entry = ToolRunner.InputEntry(form.Get("part_grade").Spec, inputs);
            Assert.Equal("pu-tdi-ether-90a", (string)entry["row"]["id"]);
            var shrink = ToolRunner.InputEntry(form.Get("shrink_pct").Spec, inputs);
            Assert.Equal(1.5, (double)shrink["si"], 9);
            Assert.Equal(1.5, (double)shrink["table"], 9);
            Assert.Null(ToolRunner.InputEntry(form.Get("grade_info").Spec, inputs)); // info lines stay on the page
        }

        [Fact]
        public void Hidden_inputs_are_left_out()
        {
            var form = PageFixture.Form();
            Assert.True(form.Get("extra_note").ConditionHidden); // show_extra is off
            Assert.Null(PageFixture.Line(form, "extra_note"));
            form.SetChecked(form.Get("show_extra"), true);
            Assert.False(form.Get("extra_note").ConditionHidden);
            Assert.Equal("", PageFixture.Line(form, "extra_note"));

            form.SetChoice(form.Get("press_pressure_is"), "On the part");
            Assert.True(form.Get("press_rated_gauge").Hidden);
            var inputs = new ToolInputs();
            form.Collect(inputs);
            Assert.False(inputs.Has("press_rated_gauge"));
            Assert.True(inputs.Hidden("press_rated_gauge"));
            Assert.Null(ToolRunner.InputEntry(form.Get("press_rated_gauge").Spec, inputs));
            Assert.NotNull(ToolRunner.InputEntry(form.Get("press_rated_force").Spec, inputs)); // the rating is always shown
        }

        [Fact]
        public void Ok_waits_for_required_optionals_and_ranges()
        {
            var form = PageFixture.Form();
            Assert.Equal("must_set", form.Blocking().Field?.Id);
            form.SetOn(form.Get("must_set"), true);
            Assert.Null(form.Blocking().Field);
            form.SetNumber(form.Get("press_pressure"), 200e6); // above Max 100 MPa (a box that can't re-range wouldn't stop it)
            Assert.Equal("press_pressure", form.Blocking().Field?.Id);
            Assert.Contains("100 MPa", form.Blocking().Message);
            form.SetNumber(form.Get("press_pressure"), 100e6);
            Assert.Null(form.Blocking().Field);
        }

        [Fact]
        public void Every_entry_list_is_known_for_the_one_list_per_choice_fallback()
        {
            var form = PageFixture.Form();
            var lists = form.PossibleEntryLists(form.Get("mold_grade"));
            Assert.Equal(2, lists.Count);
            Assert.Contains(lists, l => l.SequenceEqual(new[] { "P20 pre-hard", "H13 44-50 HRC" }));
            Assert.Equal(3, form.PossibleEntryLists(form.Get("part_grade")).Count);
            Assert.Null(form.PossibleEntryLists(form.Get("part_grade"), cap: 2));
        }

        [Fact]
        public void Remembered_values_are_stored_in_spec_units_and_merged()
        {
            var dir = PageFixture.Temp();
            var form = PageFixture.Form(imperial: true);
            form.SetChoice(form.Get("part_grade"), "TDI-polyether / MBOCA 70A");
            form.SetOn(form.Get("daylight"), true);
            var json = form.RememberJson();
            Assert.Equal("TDI-polyether / MBOCA 70A", (string)json["part_grade"]["value"]);
            Assert.Equal(1.7, (double)json["shrink_pct"]["value"], 9);
            Assert.Equal(300, (double)json["daylight"]["value"], 9); // mm
            Assert.True((bool)json["daylight"]["on"]);
            Assert.Equal(new[] { 610.0, 610.0 }, json["platen_size"]["value"].Select(x => Math.Round((double)x, 6)));
            Assert.Null(json["press_temp"]); // not remembered
            Assert.Null(json["notes"]); // text never
            Assert.Null(json["mooney"]?["value"]); // n/a: keeps whatever was stored before

            RememberStore.Save("page-test", json, dir);
            RememberStore.Save("page-test", JObject.Parse("{ \"daylight\": { \"on\": false }, \"x\": { \"value\": 1 } }"), dir);
            var loaded = RememberStore.Load("page-test", dir);
            Assert.Equal(300, (double)loaded["daylight"]["value"], 9);
            Assert.False((bool)loaded["daylight"]["on"]);
            Assert.Equal(1, (int)loaded["x"]["value"]);
            Assert.Null(RememberStore.Load("other-tool", dir));

            var again = new ToolForm(PageFixture.Spec(), new ToolData(PageFixture.Dir), true, loaded);
            Assert.Equal("TDI-polyether / MBOCA 70A", again.Get("part_grade").Value);
            Assert.False(again.Get("daylight").On);
        }

        [Fact]
        public void Original_options_keep_their_values_and_text()
        {
            var form = PageFixture.Form();
            foreach (var id in new[] { "legacy_len", "legacy_angle", "legacy_plain", "legacy_choice", "legacy_flag", "cavities", "faces", "drawing", "notes" })
                Assert.True(form.Get(id).IsPlain, id);
            Assert.Equal("12.5 mm (0.0125 m)", PageFixture.Line(form, "legacy_len"));
            Assert.Equal("5 deg", PageFixture.Line(form, "legacy_angle"));
            Assert.Equal("3.25", PageFixture.Line(form, "legacy_plain"));
            var inputs = new ToolInputs();
            form.Collect(inputs);
            Assert.Equal("Green", inputs.Text("legacy_choice"));
            Assert.True(inputs.Flag("legacy_flag"));
            Assert.Equal(0.0125, inputs.Number("legacy_len"), 12);
            Assert.Null(inputs.Display("faces"));

            // Smart Rebuild uses only the original options: same defaults as the page always gave.
            var dir = Path.Combine(PageFixture.RepoRoot(), "tools", "builtin", "smart-rebuild");
            var smart = new ToolForm(PageFixture.Spec(dir), new ToolData(dir), false);
            Assert.True(smart.AllPlain);
            var si = new ToolInputs();
            smart.Collect(si);
            Assert.Equal("New customer part", si.Text("change"));
            Assert.True(si.Flag("copy"));
            Assert.Equal("", si.Text("copy_name"));
            Assert.Equal("Ask on big ones", si.Text("decisions"));
            Assert.Null(smart.Blocking().Field);
        }

        [Fact]
        public void Options_without_a_matching_default_start_unselected_as_before()
        {
            var inputs = new List<ToolInputSpec>
            {
                new ToolInputSpec { Id = "o", Type = "options", Choices = new[] { "a", "b" }, Default = "zzz" },
                new ToolInputSpec { Id = "n", Type = "options", Choices = new[] { "a", "b" } },
                new ToolInputSpec { Id = "c", Type = "choice", Choices = new[] { "a", "b" }, Default = "zzz" },
                new ToolInputSpec { Id = "e", Type = "choice" },
            };
            var form = new ToolForm(PageFixture.Spec(PageFixture.Temp(), inputs), ToolData.None, false);
            Assert.Null(form.Get("o").Value);
            Assert.Equal("a", form.Get("n").Value);
            Assert.Equal("a", form.Get("c").Value); // the dropdown always showed its first entry
            Assert.Null(form.Get("e").Value);
        }

        [Fact]
        public void A_missing_data_file_gives_an_empty_choice_not_an_error()
        {
            var inputs = new List<ToolInputSpec>
            {
                new ToolInputSpec { Id = "g", Type = "choice", ChoicesFrom = "nothing.json#rows", Required = true, Fills = new Dictionary<string, string> { ["x"] = "a.b" } },
                new ToolInputSpec { Id = "x", Type = "number", Unit = "pressure", Default = "2" },
            };
            var form = new ToolForm(PageFixture.Spec(PageFixture.Temp(), inputs), new ToolData(PageFixture.Temp()), false);
            Assert.Empty(form.Get("g").Items);
            Assert.Null(form.Get("g").Value);
            Near(2e6, form.Get("x").Si);
            Assert.Equal("g", form.Blocking().Field?.Id);
        }
    }
}

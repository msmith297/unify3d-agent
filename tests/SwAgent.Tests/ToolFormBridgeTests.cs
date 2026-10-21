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
    /// <summary>
    /// The panel's tool form (ToolFormSession, the state ToolFormBridge sends) on the real compression-mold tool: the same
    /// behaviours ToolDataTests checks for the page - cascades, fills, units, ShowWhen, Optional, remembered values, OK.
    /// </summary>
    public class ToolFormBridgeTests
    {
        private const string Mold = "design-compression-mold";

        private static void Near(double expected, double actual, double relative = 1e-6) =>
            Assert.True(Math.Abs(expected - actual) <= relative * Math.Max(1, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");

        /// <summary>A form on the shipped tool, remembering into its own folder (never the shared test data folder).</summary>
        private static (ToolFormSession Session, string RememberDir) Open(bool imperial = false, string rememberDir = null)
        {
            var spec = ToolSpec.Load(Mold);
            Assert.NotNull(spec);
            rememberDir = rememberDir ?? PageFixture.Temp();
            return (new ToolFormSession(spec, ToolFormSession.CreateForm(spec, imperial, rememberDir), imperial), rememberDir);
        }

        /// <summary>Missing or null in the JSON the panel gets (a null string is a JValue whose value is null).</summary>
        private static bool IsNull(JToken t) => t == null || t.Type == JTokenType.Null || t is JValue v && v.Value == null;

        private static JObject Input(JObject state, string id) =>
            state["groups"].SelectMany(g => g["inputs"]).Cast<JObject>().Single(i => (string)i["id"] == id);

        private static JObject Change(ToolFormSession s, object change)
        {
            s.Change(JObject.FromObject(change));
            return s.State();
        }

        [Fact]
        public void The_form_has_the_pages_groups_inputs_and_defaults()
        {
            var (s, _) = Open();
            var state = s.State();
            Assert.Equal(new[] { "Molded part", "Mold", "Part material", "Process data (from table)", "Press", "Cavities & limits", "Decisions & notes" },
                state["groups"].Select(g => (string)g["title"]));
            Assert.Equal(44, state["groups"].SelectMany(g => g["inputs"]).Count());
            // The first input of a group decides whether it starts open, as on the page.
            Assert.False((bool)state["groups"].Single(g => (string)g["id"] == "Process data (from table)")["expanded"]);
            Assert.True((bool)state["groups"].Single(g => (string)g["id"] == "Press")["expanded"]);
            Assert.True((bool)state["okEnabled"]); // nothing is required
            Assert.Equal(Mold, (string)state["tool"]);
            Assert.Equal("max", (string)state["agent"]["tier"]);
            Assert.True(IsNull(state["preview"])); // the mold tool has no preview.cs

            var family = Input(state, "part_family");
            Assert.Equal("choice", (string)family["type"]);
            Assert.Equal("PU TDI-ether (MBOCA)", (string)family["value"]);
            Assert.Contains("EPDM", family["choices"].Select(x => (string)x));
            Assert.Equal("TDI-ether 90A", (string)Input(state, "part_grade")["value"]); // the row marked isDefault

            var shrink = Input(state, "shrink_pct");
            Near(1.5, (double)shrink["value"]);
            Assert.Equal("%", (string)shrink["unit"]);
            Near(1.5, (double)shrink["table"]);
            Assert.False((bool)shrink["edited"]);
            Assert.False((bool)Input(state, "mooney")["visible"]); // PU has no Mooney value: n/a, hidden

            var critical = Input(state, "critical_faces");
            Assert.Equal("selection", (string)critical["type"]);
            Assert.Equal(new[] { "face" }, critical["filter"].Select(x => (string)x));
            Assert.Empty(critical["items"]);
            var drawing = Input(state, "drawing");
            Assert.True((bool)drawing["multiple"]);
            Assert.Equal(new[] { "pdf", "image", "drawing" }, drawing["accept"].Select(x => (string)x));
            Assert.StartsWith("6061-T6 / T651 rolled plate: full strength to", (string)Input(state, "grade_info")["text"]); // info lines render their {...}
        }

        [Fact]
        public void A_choice_cascades_into_the_dependent_lists_and_show_when()
        {
            var (s, _) = Open();
            Assert.Equal(new[] { "6061-T651", "7075-T651", "MIC-6 cast plate", "QC-10", "Alumold 500", "5083" },
                Input(s.State(), "mold_grade")["choices"].Select(x => (string)x));
            Assert.True((bool)Input(s.State(), "insert_grade")["visible"]);

            var state = Change(s, new { id = "mold_material", value = "Tool steel" });
            var grade = Input(state, "mold_grade");
            Assert.Equal(new[] { "P20 pre-hard", "4140 pre-hard", "H13 44-50 HRC", "420 SS pre-hard" }, grade["choices"].Select(x => (string)x));
            Assert.Equal("P20 pre-hard", (string)grade["value"]);
            Assert.False((bool)Input(state, "insert_grade")["visible"]); // ShowWhen: aluminium only
            Assert.StartsWith("P20", (string)Input(state, "grade_info")["text"]);

            state = Change(s, new { id = "mold_grade", value = "H13 44-50 HRC" });
            Assert.Equal("H13 44-50 HRC", (string)Input(state, "mold_grade")["value"]);
            // An entry that isn't in the list changes nothing.
            state = Change(s, new { id = "mold_grade", value = "6061-T651" });
            Assert.Equal("H13 44-50 HRC", (string)Input(state, "mold_grade")["value"]);

            // ShowWhen on a plain choice: what the press reads decides which pressure box shows.
            Assert.True((bool)Input(state, "press_pressure")["visible"]);
            Assert.False((bool)Input(state, "part_pressure")["visible"]);
            state = Change(s, new { id = "press_pressure_is", value = "On the part" });
            Assert.False((bool)Input(state, "press_pressure")["visible"]);
            Assert.True((bool)Input(state, "part_pressure")["visible"]);
        }

        [Fact]
        public void A_grade_fills_its_fields_and_a_null_hides_one()
        {
            var (s, _) = Open();
            var state = Change(s, new { id = "part_family", value = "EPDM" });
            Assert.Equal("EPDM sulfur 60A", (string)Input(state, "part_grade")["value"]);
            var mooney = Input(state, "mooney");
            Assert.True((bool)mooney["visible"]);
            Near(60, (double)mooney["value"]);
            Assert.False((bool)Input(state, "viscosity_cp")["visible"]); // no viscosity for a rubber compound: n/a
            Assert.True((bool)Input(state, "viscosity_cp")["notApplicable"]);
            Near(170, (double)Input(state, "press_temp")["value"]);
            Assert.Equal("°C", (string)Input(state, "press_temp")["unit"]);

            // An edit of a filled value says so, and a new pick overwrites it.
            state = Change(s, new { id = "press_temp", value = 175 });
            Assert.True((bool)Input(state, "press_temp")["edited"]);
            Near(170, (double)Input(state, "press_temp")["table"]);
            state = Change(s, new { id = "part_grade", value = "EPDM peroxide 70A" });
            Near(177, (double)Input(state, "press_temp")["value"]);
            Assert.False((bool)Input(state, "press_temp")["edited"]);
        }

        [Fact]
        public void Numbers_show_in_the_documents_system_and_their_own_unit_choice()
        {
            var (metric, _) = Open(imperial: false);
            var p = Input(metric.State(), "press_pressure");
            Assert.Equal("MPa", (string)p["unit"]);
            Near(13.79, (double)p["value"]);
            Assert.Equal(new[] { "psi", "bar", "MPa", "kgf/cm²" }, p["unitChoices"].Select(x => (string)x));
            Assert.True(IsNull(Input(metric.State(), "press_temp")["unitChoices"])); // no choices: follows the document
            Near(300, (double)Input(metric.State(), "daylight")["value"]);
            Assert.Equal("mm", (string)Input(metric.State(), "daylight")["unit"]);

            var (s, _) = Open(imperial: true);
            var state = s.State();
            p = Input(state, "press_pressure");
            Assert.Equal("psi", (string)p["unit"]);
            Near(2000, (double)p["value"]); // 13.79 MPa shows as the round 2000 psi
            Assert.Equal("°F", (string)Input(state, "press_temp")["unit"]);
            Near(212, (double)Input(state, "press_temp")["value"]);
            Near(50, (double)Input(state, "molding_pressure")["value"]); // stored in psi in the data ("|psi")
            Assert.Equal("in", (string)Input(state, "daylight")["unit"]);
            Near(300 / 25.4, (double)Input(state, "daylight")["value"]);
            Assert.Equal("°", (string)Input(state, "draft_min_deg")["unit"]);
            Near(0.5, (double)Input(state, "draft_min_deg")["value"]);

            // A unit choice keeps the value and shows it in the new unit; a value is read in the unit shown.
            state = Change(s, new { id = "press_pressure", unit = "bar" });
            Near(2000 * 6894.757293168 / 1e5, (double)Input(state, "press_pressure")["value"], 1e-4);
            state = Change(s, new { id = "press_pressure", value = 150 });
            Near(150e5, s.Form.Get("press_pressure").Si);
            Assert.Equal("bar", (string)Input(state, "press_pressure")["unit"]);
            state = Change(s, new { id = "daylight", on = true });
            state = Change(s, new { id = "daylight", value = 12 });
            Near(12 * 0.0254, s.Form.Get("daylight").Si);
            Assert.Equal("150 bar (15 MPa)", PageFixture.Line(s.Form, "press_pressure"));
        }

        [Fact]
        public void Optionals_switch_and_native_boxes_keep_their_range()
        {
            var (s, _) = Open();
            var daylight = Input(s.State(), "daylight");
            Assert.True((bool)daylight["optional"]);
            Assert.False((bool)daylight["on"]);
            Assert.False((bool)daylight["enabled"]);
            Assert.Null(s.Collect().Limit("daylight")); // off: "not set"

            var state = Change(s, new { id = "daylight", on = true });
            Assert.True((bool)Input(state, "daylight")["enabled"]);
            state = Change(s, new { id = "daylight", value = 5000 }); // the page's box stops at Max (3000 mm)
            Near(3000, (double)Input(state, "daylight")["value"]);
            Near(3.0, s.Collect().Limit("daylight").Value);

            state = Change(s, new { id = "cavities", value = 2.6 });
            Near(3, (double)Input(state, "cavities")["value"]);
            state = Change(s, new { id = "cavities", value = 500 });
            Near(64, (double)Input(state, "cavities")["value"]);

            var platen = Input(state, "platen_size");
            Assert.Equal(2, (int)platen["dims"]);
            Assert.Equal(new[] { 610.0, 610.0 }, platen["value"].Select(x => (double)x));
            state = Change(s, new { id = "platen_size", on = true });
            state = Change(s, new { id = "platen_size", index = 0, value = 700 });
            Assert.Equal(new[] { 700.0, 610.0 }, Input(state, "platen_size")["value"].Select(x => (double)x));
            state = Change(s, new { id = "platen_size", value = new[] { 800, 900 } });
            var size = s.Collect().Size("platen_size");
            Near(0.8, size[0]);
            Near(0.9, size[1]);
        }

        [Fact]
        public void A_converted_value_out_of_range_holds_ok_back_at_its_box()
        {
            var (s, _) = Open();
            var state = Change(s, new { id = "press_pressure", unit = "bar" });
            state = Change(s, new { id = "press_pressure", value = 2000 }); // 200 MPa, over the 100 MPa maximum
            Assert.False((bool)state["okEnabled"]);
            Assert.Equal("press_pressure", (string)state["okField"]);
            Assert.StartsWith("Enter a value from", (string)Input(state, "press_pressure")["error"]);
            Assert.Null(s.Finish(PageFixture.Temp()));

            state = Change(s, new { id = "press_pressure", value = 150 });
            Assert.True((bool)state["okEnabled"]);
            Assert.True(IsNull(Input(state, "press_pressure")["error"]));
        }

        [Fact]
        public void Ok_remembers_values_and_unit_choices_for_the_next_form()
        {
            var (s, dir) = Open();
            Change(s, new { id = "mold_material", value = "Tool steel" });
            Change(s, new { id = "mold_grade", value = "H13 44-50 HRC" });
            Change(s, new { id = "press_pressure", unit = "bar" });
            Change(s, new { id = "press_pressure", value = 150 });
            Change(s, new { id = "shrink_pct", value = 2.0 }); // the user's edit of a filled-in value
            Change(s, new { id = "cavities", value = 4 }); // not remembered
            Change(s, new { id = "notes", value = "Lid comes off at the table" }); // text is never stored
            var inputs = s.Finish(dir);
            Assert.NotNull(inputs);
            Assert.Equal("Tool steel", inputs.Text("mold_material"));
            Assert.Equal(4, inputs.Integer("cavities"));
            Assert.True(File.Exists(Path.Combine(dir, Mold + ".json")));

            var (again, _) = Open(rememberDir: dir);
            var state = again.State();
            Assert.Equal("Tool steel", (string)Input(state, "mold_material")["value"]);
            Assert.Equal("H13 44-50 HRC", (string)Input(state, "mold_grade")["value"]);
            Assert.Equal("bar", (string)Input(state, "press_pressure")["unit"]);
            Near(150, (double)Input(state, "press_pressure")["value"]);
            var shrink = Input(state, "shrink_pct");
            Near(2.0, (double)shrink["value"]); // an edit made against this same grade stays over the table
            Assert.True((bool)shrink["edited"]);
            Near(1, (double)Input(state, "cavities")["value"]);
            Assert.Equal("", (string)Input(state, "notes")["value"]);
        }

        [Fact]
        public void Cancel_stores_nothing()
        {
            var (s, dir) = Open();
            Change(s, new { id = "mold_material", value = "Tool steel" });
            // Cancel is closing without Finish: the next form starts from the shipped defaults.
            var (again, _) = Open(rememberDir: dir);
            Assert.Equal("Aluminium + steel inserts", (string)Input(again.State(), "mold_material")["value"]);
            Assert.False(File.Exists(Path.Combine(dir, Mold + ".json")));
        }

        [Fact]
        public void Ok_hands_the_macro_and_agent_what_the_page_would()
        {
            var (s, dir) = Open();
            var page = ToolFormSession.CreateForm(s.Spec, false, dir); // the page's form, given the same edits
            foreach (var (id, value) in new[] { ("mold_material", (object)"Tool steel"), ("part_family", "EPDM"), ("cavities", 4), ("decisions", "Decide everything") })
            {
                Change(s, new { id, value });
                var f = page.Get(id);
                if (f.Type == "integer") page.SetNumber(f, Convert.ToDouble(value));
                else page.SetChoice(f, (string)value);
            }
            var panel = s.Finish(dir);
            foreach (var spec in s.Spec.Inputs.Where(i => ToolSpec.Type(i) != "files" && ToolSpec.Type(i) != "selection"))
            {
                var a = ToolRunner.InputEntry(spec, panel);
                var b = PageFixture.Line(page, spec.Id);
                Assert.Equal(b, (string)a?["value"]);
            }
            Assert.True(panel.Hidden("insert_grade")); // ShowWhen not met: left out
            Assert.Equal("n/a for this grade", panel.Display("viscosity_cp"));
        }

        [Fact]
        public void Files_and_selections_reach_the_inputs()
        {
            var (s, dir) = Open();
            var state = Change(s, new { id = "part_file", value = new[] { @"C:\Parts\puck.step", @"C:\Parts\puck-rev-b.step" } });
            // A single-file input keeps the last file; relative paths are ignored.
            Assert.Equal(new[] { "puck-rev-b.step" }, Input(state, "part_file")["files"].Select(f => (string)f["name"]));
            s.AddFiles("drawing", new[] { @"C:\Parts\puck.pdf", "relative.pdf", @"C:\Parts\puck.png", @"C:\Parts\PUCK.pdf" });
            Assert.Equal(new[] { @"C:\Parts\puck.pdf", @"C:\Parts\puck.png" }, s.Files("drawing"));
            state = Change(s, new { id = "drawing", remove = 0 });
            Assert.Equal(new[] { "puck.png" }, Input(state, "drawing")["files"].Select(f => (string)f["name"]));

            var face = new object();
            s.SetSelection("critical_faces", new[]
            {
                new ToolInputs.SelectedItem { Entity = face, Type = "Face", Label = "Imported1", Detail = "planar", Ref = "cmVm" },
                new ToolInputs.SelectedItem { Entity = new object(), Type = "Face", Label = "Imported1", Detail = "cylindrical" },
            });
            state = s.State();
            Assert.Equal(new[] { "planar", "cylindrical" }, Input(state, "critical_faces")["items"].Select(i => (string)i["detail"]));

            var inputs = s.Finish(dir);
            Assert.Equal(new[] { @"C:\Parts\puck-rev-b.step" }, inputs.Files("part_file"));
            Assert.Same(face, inputs.Selected("critical_faces")[0]);
            var entry = ToolRunner.InputEntry(s.Spec.Inputs.Single(i => i.Id == "critical_faces"), inputs);
            Assert.Equal("cmVm", (string)entry["items"][0]["ref"]);

            Change(s, new { id = "critical_faces", clear = true });
            Assert.Empty(Input(s.State(), "critical_faces")["items"]);
        }

        [Fact]
        public void Required_selections_files_and_text_hold_ok_back()
        {
            var spec = PageFixture.Spec(PageFixture.Temp(), new List<ToolInputSpec>
            {
                new ToolInputSpec { Id = "edges", Type = "selection", Label = "Edges", Filter = new[] { "edge" }, Multiple = false },
                new ToolInputSpec { Id = "photo", Type = "files", Label = "Photo", Required = true },
                new ToolInputSpec { Id = "why", Type = "text", Label = "Why", Required = true },
            });
            var s = new ToolFormSession(spec, ToolFormSession.CreateForm(spec, false, PageFixture.Temp()), false);
            Assert.Equal("edges", (string)s.State()["okField"]); // a selection input is required by default
            Assert.Equal("Select an edge in SolidWorks, then press Use selection.", (string)Input(s.State(), "edges")["error"]);
            s.SetSelection("edges", new[] { new ToolInputs.SelectedItem { Type = "Edge" }, new ToolInputs.SelectedItem { Type = "Edge" } });
            Assert.Single(s.Selected("edges")); // not Multiple: one entity
            Assert.Equal("photo", (string)s.State()["okField"]);
            s.AddFiles("photo", new[] { @"C:\x.png" });
            Assert.Equal("why", (string)s.State()["okField"]);
            Assert.Null(s.Finish(PageFixture.Temp()));
            var state = Change(s, new { id = "why", value = "  " });
            Assert.False((bool)state["okEnabled"]);
            state = Change(s, new { id = "why", value = "Worn" });
            Assert.True((bool)state["okEnabled"]);
            Assert.True(IsNull(state["agent"])); // no agent: no tier picker
            Assert.NotNull(s.Finish(PageFixture.Temp()));
        }

        [Fact]
        public void The_tier_changes_only_to_a_known_tier_and_unknown_inputs_are_refused()
        {
            var (s, _) = Open();
            Assert.True(s.Change(JObject.FromObject(new { tier = "high" })));
            Assert.Equal("high", (string)s.State()["agent"]["tier"]);
            Assert.False(s.Change(JObject.FromObject(new { tier = "high" })));
            Assert.False(s.Change(JObject.FromObject(new { tier = "turbo" })));
            Assert.Equal("high", s.Tier);
            Assert.Throws<InvalidOperationException>(() => s.Change(JObject.FromObject(new { id = "nope", value = 1 })));
            Assert.Throws<InvalidOperationException>(() => s.SetSelection("cavities", new ToolInputs.SelectedItem[0]));
        }

        [Fact]
        public void The_panel_is_the_default_and_page_is_a_choice()
        {
            Assert.True(ToolRunner.InputsInPanel(null));
            Assert.True(ToolRunner.InputsInPanel(new JObject()));
            Assert.True(ToolRunner.InputsInPanel(new JObject { ["toolInputs"] = "panel" }));
            Assert.True(ToolRunner.InputsInPanel(new JObject { ["toolInputs"] = 3 }));
            Assert.False(ToolRunner.InputsInPanel(new JObject { ["toolInputs"] = "page" }));
            Assert.False(ToolRunner.InputsInPanel(new JObject { ["toolInputs"] = " Page " }));
        }
    }
}

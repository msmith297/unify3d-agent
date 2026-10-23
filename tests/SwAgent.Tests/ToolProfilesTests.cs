using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using SwAgent.Scripting;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// Tool profiles (docs/tool-profiles.md section 4) on the real compression-mold tool: save, load, update and save-as round
    /// trips with units, grade ids and Optional switches; the default profile at open; "(modified)" and the markers; outdated
    /// profiles; locked shipped profiles; the run's record of the profile. Every test keeps its profiles in its own folder.
    /// </summary>
    public class ToolProfilesTests
    {
        private const string Mold = "design-compression-mold";

        private static void Near(double expected, double actual, double relative = 1e-6) =>
            Assert.True(Math.Abs(expected - actual) <= relative * Math.Max(1, Math.Abs(expected)), $"expected {expected:R}, got {actual:R}");

        /// <summary>A panel form on the shipped tool as ToolFormBridge opens it, with profiles and remembered values in temp folders.</summary>
        private static ToolFormSession Open(string root, bool imperial = false, string profile = null, ToolSpec spec = null)
        {
            spec = spec ?? ToolSpec.Load(Mold);
            Assert.NotNull(spec);
            var form = ProfileSession.OpenForm(spec, imperial, profile, out var profiles, PageFixture.Temp(), root, documentInputs: true);
            var s = new ToolFormSession(spec, form, imperial) { Profiles = profiles };
            profiles.Attach(s.DocumentInputs());
            return s;
        }

        private static JObject Input(JObject state, string id) =>
            state["groups"].SelectMany(g => g["inputs"]).Cast<JObject>().Single(i => (string)i["id"] == id);

        private static JObject Change(ToolFormSession s, object change)
        {
            s.Change(JObject.FromObject(change));
            return s.State();
        }

        private static JObject Act(ToolFormSession s, object action)
        {
            s.Profile(JObject.FromObject(action));
            return s.State();
        }

        private static JObject FileOf(string root, string name) =>
            ToolProfile.ParseJson(File.ReadAllText(ToolProfiles.Find(Mold, name, root).Path));

        [Fact]
        public void A_profile_saves_values_with_units_row_ids_and_switches_and_loads_them_back()
        {
            var root = PageFixture.Temp();
            var s = Open(root);
            Change(s, new { id = "press_pressure_is", value = "Gauge reading" });
            Change(s, new { id = "press_pressure", unit = "psi" });
            Change(s, new { id = "press_pressure", value = 2000 });
            Change(s, new { id = "mold_grade", value = "7075-T651" });
            Change(s, new { id = "part_family", value = "EPDM" });
            Change(s, new { id = "max_lid_mass", on = true });
            Change(s, new { id = "max_lid_mass", value = 23 });
            Change(s, new { id = "daylight", on = false });
            Change(s, new { id = "cycle_life", value = 10000 });
            var state = Act(s, new { action = "save", name = "Shop A" });
            Assert.Equal("Shop A", (string)state["profile"]["name"]);
            Assert.False((bool)state["profile"]["modified"]);
            Assert.Null((string)state["profile"]["error"]);

            var file = FileOf(root, "Shop A");
            Assert.Equal(1, (int)file["schema"]);
            Assert.Equal(Mold, (string)file["tool"]);
            Assert.Equal("2026-10-19T00:00:00Z", (string)file["toolVersion"]); // TOOL.md updated
            var v = (JObject)file["values"];
            Assert.Equal("2000 psi", (string)v["press_pressure"]);
            Assert.Equal("al_7075_t651", (string)v["mold_grade"]); // the row id, not the label
            Assert.Equal("EPDM", (string)v["part_family"]); // a family is an entry several rows share: its text
            Assert.Equal(JToken.Parse("{\"on\": true, \"value\": \"23 kg\"}"), v["max_lid_mass"]);
            Assert.Equal(JToken.Parse("{\"on\": false}"), v["daylight"]);
            Assert.Equal("10000", (string)v["cycle_life"]);
            // document-specific inputs are left out by default; a value the grade filled in follows the table
            Assert.Null(v["part_file"]);
            Assert.Null(v["critical_faces"]);
            Assert.Null(v["shrink_pct"]);

            // Loaded into a new form in a metric document: psi with the field's unit choice, the grade, the switches.
            var again = Open(root, profile: "Shop A");
            var st = again.State();
            var press = Input(st, "press_pressure");
            Assert.Equal("psi", (string)press["unit"]);
            Near(2000, (double)press["value"]);
            Assert.Equal("7075-T651", (string)Input(st, "mold_grade")["value"]);
            Assert.Equal("EPDM", (string)Input(st, "part_family")["value"]);
            Assert.True((bool)Input(st, "max_lid_mass")["on"]);
            Near(23, (double)Input(st, "max_lid_mass")["value"]);
            Assert.False((bool)Input(st, "daylight")["on"]);
            Assert.Equal("Shop A", (string)st["profile"]["name"]);
            Assert.Equal("Loaded the profile Shop A.", (string)st["profile"]["note"]);
            Assert.Equal("set", (string)Input(st, "press_pressure")["mark"]);
            Assert.Equal("set", (string)Input(st, "lid_reach")["mark"]); // a full profile holds every value...
            Assert.Null((string)Input(st, "shrink_pct")["mark"]); // ...except what the grade fills in
        }

        [Fact]
        public void Lengths_saved_in_an_inch_document_load_in_mm()
        {
            var root = PageFixture.Temp();
            var s = Open(root, imperial: true);
            Change(s, new { id = "daylight", on = true });
            Change(s, new { id = "daylight", value = 12 }); // inches in an IPS document
            Act(s, new { action = "save", name = "Inch press" });
            Assert.Equal("12 in", (string)FileOf(root, "Inch press")["values"]["daylight"]["value"]);

            var metric = Open(root, profile: "Inch press");
            var daylight = Input(metric.State(), "daylight");
            Assert.Equal("mm", (string)daylight["unit"]);
            Near(304.8, (double)daylight["value"]);
        }

        [Fact]
        public void Grade_ids_survive_a_label_change_in_the_data()
        {
            var root = PageFixture.Temp();
            var spec = ToolSpec.Load(Mold);
            var copy = PageFixture.Temp();
            foreach (var file in Directory.GetFiles(spec.Dir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, file.Substring(spec.Dir.Length).TrimStart('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }
            var materials = Path.Combine(copy, "data", "mold-materials.json");
            File.WriteAllText(materials, File.ReadAllText(materials).Replace("\"7075-T651\"", "\"7075-T651 rolled\""));
            spec.Dir = copy;
            ToolProfiles.Save(Mold, "Shop A", new JObject { ["mold_grade"] = "al_7075_t651" }, root: root, toolDir: copy);

            var s = Open(root, profile: "Shop A", spec: spec);
            Assert.Equal("7075-T651 rolled", (string)Input(s.State(), "mold_grade")["value"]);
            Assert.Null(s.State()["profile"]["warnings"]?.FirstOrDefault());
        }

        [Fact]
        public void The_default_profile_opens_the_form_and_changes_show_as_modified()
        {
            var root = PageFixture.Temp();
            ToolProfiles.Save(Mold, "Shop A", new JObject { ["cycle_life"] = "20000", ["mold_material"] = "Tool steel" }, root: root);
            ToolProfiles.SetDefault(Mold, "Shop A", root);

            var s = Open(root);
            var state = s.State();
            Assert.Equal("Loaded your default profile: Shop A.", (string)state["profile"]["note"]);
            Assert.True((bool)state["profile"]["isDefault"]);
            Assert.Equal("Tool steel", (string)Input(state, "mold_material")["value"]);
            Assert.False((bool)state["profile"]["modified"]);
            Assert.Equal("set", (string)Input(state, "cycle_life")["mark"]);
            Assert.Contains(state["profile"]["profiles"], p => (string)p["name"] == "Shop A" && (bool)p["isDefault"]);
            Assert.Contains(state["profile"]["profiles"], p => (string)p["name"] == "PU shop starter" && (bool)p["locked"]); // shipped

            state = Change(s, new { id = "cycle_life", value = 30000 });
            Assert.True((bool)state["profile"]["modified"]);
            Assert.Equal("edited", (string)Input(state, "cycle_life")["mark"]);
            state = Change(s, new { id = "lid_reach", value = 450 }); // not in the profile: edited too
            Assert.Equal("edited", (string)Input(state, "lid_reach")["mark"]);
            state = Change(s, new { id = "cycle_life", value = 20000 }); // back to the profile's value
            Assert.Equal("set", (string)Input(state, "cycle_life")["mark"]);

            // a unit change alone is no change
            state = Change(s, new { id = "lid_reach", value = 400 });
            Assert.False((bool)state["profile"]["modified"]);
            state = Change(s, new { id = "press_pressure", unit = "bar" });
            Assert.False((bool)state["profile"]["modified"]);

            // the run records the profile and what changed after loading it
            Change(s, new { id = "cycle_life", value = 25000 });
            var inputs = s.Finish(PageFixture.Temp());
            Assert.Equal("Shop A", (string)inputs.Profile["name"]);
            Assert.Equal(new[] { "cycle_life" }, inputs.Profile["modified"].Select(x => (string)x));
            Assert.Equal("Shop A", ToolProfiles.LastUsed(Mold, root));
            Assert.Equal("25000", (string)ToolProfiles.LastRun[Mold]["cycle_life"]);
        }

        [Fact]
        public void Without_a_default_the_form_opens_as_before_and_without_marks()
        {
            var root = PageFixture.Temp();
            var s = Open(root);
            var state = s.State();
            Assert.Null((string)state["profile"]["name"]);
            Assert.Null((string)state["profile"]["note"]);
            Assert.All(state["groups"].SelectMany(g => g["inputs"]), i => Assert.Null(i["mark"]));
            Assert.Null(s.Finish(PageFixture.Temp()).Profile);
        }

        [Fact]
        public void An_outdated_profile_loads_with_warnings_and_is_never_changed()
        {
            var root = PageFixture.Temp();
            var values = new JObject
            {
                ["old_field"] = "3",
                ["cavities"] = "500", // Max 64
                ["mold_grade"] = "al_9999",
                ["press_pressure"] = "2000 furlongs",
                ["cycle_life"] = "8000",
            };
            var saved = ToolProfiles.Save(Mold, "Old", values, root: root);
            var before = File.ReadAllText(saved.Path);

            var s = Open(root, profile: "Old");
            var state = s.State();
            var warnings = state["profile"]["warnings"].Select(x => (string)x).ToList();
            Assert.Contains("Ignored 'old_field': the tool has no such input now.", warnings);
            Assert.Contains(warnings, w => w.StartsWith("Kept the default for 'Number of cavities'") && w.Contains("500 is outside 1 to 64"));
            Assert.Contains(warnings, w => w.StartsWith("Kept the default for 'Plate grade'") && w.Contains("no longer in its list"));
            Assert.Contains(warnings, w => w.Contains("'furlongs' is not a unit it takes"));
            Assert.Equal(4, warnings.Count);
            Assert.Equal(1, (int)Input(state, "cavities")["value"]); // the default kept
            Near(8000, (double)Input(state, "cycle_life")["value"]); // the rest loaded
            Assert.Null((string)Input(state, "cavities")["mark"]);
            Assert.Equal("set", (string)Input(state, "cycle_life")["mark"]);
            Assert.Equal(before, File.ReadAllText(saved.Path));
        }

        [Fact]
        public void A_grade_not_offered_with_the_family_says_so()
        {
            var root = PageFixture.Temp();
            ToolProfiles.Save(Mold, "Mixed", new JObject { ["part_family"] = "EPDM", ["part_grade"] = "pu-tdi-ether-90a" }, root: root);
            var warnings = Open(root, profile: "Mixed").State()["profile"]["warnings"].Select(x => (string)x).ToList();
            Assert.Equal(new[] { "Kept the default for 'Grade': 'pu-tdi-ether-90a' isn't offered with the other settings." }, warnings);
        }

        [Fact]
        public void Shipped_profiles_load_cleanly_and_are_locked()
        {
            var root = PageFixture.Temp();
            var shipped = ToolProfiles.List(Mold, root).Where(p => p.Locked).Select(p => p.Name).ToList();
            Assert.Equal(new[] { "PU shop starter", "Rubber shop starter" }, shipped);
            foreach (var name in shipped)
            {
                var state = Open(root, profile: name).State();
                Assert.Null(state["profile"]["warnings"]?.FirstOrDefault());
                Assert.True((bool)state["profile"]["locked"]);
            }
            var rubber = Open(root, profile: "Rubber shop starter");
            Assert.Equal("EPDM sulfur 70A", (string)Input(rubber.State(), "part_grade")["value"]);
            Change(rubber, new { id = "cavities", value = 6 });
            var state2 = Act(rubber, new { action = "update" });
            Assert.Equal("'Rubber shop starter' comes with the tool and can't be changed. Save it under a new name.", (string)state2["profile"]["error"]);
            Assert.Throws<ToolException>(() => ToolProfiles.Delete(Mold, "Rubber shop starter", root));
            Assert.Throws<ToolException>(() => ToolProfiles.Save(Mold, "rubber shop starter", new JObject(), root: root));
            // Save as new is how a locked profile is changed.
            state2 = Act(rubber, new { action = "save", name = "My rubber" });
            Assert.Equal("My rubber", (string)state2["profile"]["name"]);
            Assert.False((bool)state2["profile"]["locked"]);
            Assert.Empty(ToolProfiles.CheckShipped(ToolSpec.Load(Mold).Dir, Mold)); // what plugin_validate checks
        }

        [Fact]
        public void Update_keeps_the_profiles_inputs_and_adds_the_changed_ones()
        {
            var root = PageFixture.Temp();
            ToolProfiles.Save(Mold, "Partial", new JObject { ["cycle_life"] = "20000" }, root: root);
            var s = Open(root, profile: "Partial");
            Change(s, new { id = "lid_reach", value = 450 });
            var state = Act(s, new { action = "update" });
            Assert.Equal("Updated the profile Partial.", (string)state["profile"]["note"]);
            Assert.False((bool)state["profile"]["modified"]);
            var v = (JObject)FileOf(root, "Partial")["values"];
            Assert.Equal(new[] { "cycle_life", "lid_reach" }, v.Properties().Select(p => p.Name).OrderBy(x => x));
            Assert.Equal("450 mm", (string)v["lid_reach"]);
            Assert.Equal("set", (string)Input(state, "lid_reach")["mark"]);
        }

        [Fact]
        public void Only_the_changed_fields_and_the_document_inputs_on_request()
        {
            var root = PageFixture.Temp();
            var s = Open(root);
            Change(s, new { id = "cycle_life", value = 12345 });
            var pdf = Path.Combine(PageFixture.Temp(), "drawing.pdf");
            File.WriteAllText(pdf, "%PDF-1.4");
            s.AddFiles("drawing", new[] { pdf });
            Act(s, new { action = "save", name = "Job 42", onlyChanged = true, includeDocument = true, makeDefault = true });
            var v = (JObject)FileOf(root, "Job 42")["values"];
            Assert.Equal(new[] { "cycle_life", "drawing" }, v.Properties().Select(p => p.Name).OrderBy(x => x));
            Assert.Equal(pdf, (string)v["drawing"][0]);
            Assert.Equal("Job 42", ToolProfiles.DefaultName(Mold, root));

            // the files come back with the profile (a missing one is listed)
            var again = Open(root);
            Assert.Equal(pdf, again.Files("drawing").Single());
            File.Delete(pdf);
            var third = Open(root);
            Assert.Empty(third.Files("drawing"));
            Assert.Contains(third.State()["profile"]["warnings"], w => ((string)w).Contains("not found"));

            var nothing = Open(PageFixture.Temp());
            Assert.Equal("Nothing to save: every input is at the tool default.", (string)Act(nothing, new { action = "save", name = "Empty", onlyChanged = true })["profile"]["error"]);
        }

        [Fact]
        public void Loading_tool_defaults_resets_values_but_keeps_files()
        {
            var root = PageFixture.Temp();
            ToolProfiles.Save(Mold, "Shop A", new JObject { ["cycle_life"] = "20000" }, root: root);
            var s = Open(root, profile: "Shop A");
            var pdf = Path.Combine(PageFixture.Temp(), "d.pdf");
            File.WriteAllText(pdf, "x");
            s.AddFiles("drawing", new[] { pdf });
            var state = Act(s, new { action = "load", name = (string)null });
            Assert.Null((string)state["profile"]["name"]);
            Near(5000, (double)Input(state, "cycle_life")["value"]);
            Assert.Single(s.Files("drawing"));
        }

        [Fact]
        public void A_value_a_grade_fills_is_saved_only_when_the_user_changed_it()
        {
            var root = PageFixture.Temp();
            var s = Open(root);
            Change(s, new { id = "shrink_pct", value = 1.8 });
            Act(s, new { action = "save", name = "Measured" });
            Assert.Equal("1.8 %", (string)FileOf(root, "Measured")["values"]["shrink_pct"]);
            var again = Open(root, profile: "Measured");
            var shrink = Input(again.State(), "shrink_pct");
            Near(1.8, (double)shrink["value"]);
            Assert.True((bool)shrink["edited"]); // still shows the table's 1.5 %
        }

        [Fact]
        public void Rename_duplicate_delete_import_export_and_copy_keep_the_settings_right()
        {
            var root = PageFixture.Temp();
            ToolProfiles.Save(Mold, "Shop A", new JObject { ["cycle_life"] = "20000" }, root: root);
            ToolProfiles.SetDefault(Mold, "Shop A", root);
            ToolProfiles.Rename(Mold, "Shop A", "Shop B", root);
            Assert.Equal("Shop B", ToolProfiles.DefaultName(Mold, root));
            Assert.Throws<ToolException>(() => ToolProfiles.Rename(Mold, "Shop B", "PU shop starter", root));
            Assert.Equal("Shop B copy", ToolProfiles.Duplicate(Mold, "Shop B", root: root).Name);
            Assert.Equal("PU shop starter copy", ToolProfiles.Duplicate(Mold, "PU shop starter", root: root).Name);

            var exported = Path.Combine(PageFixture.Temp(), "shop-b.json");
            ToolProfiles.Export(Mold, "Shop B", exported, root);
            var imported = ToolProfiles.Import(Mold, File.ReadAllText(exported), out var note, root);
            Assert.Equal("Shop B (2)", imported.Name);
            Assert.Null(note);
            Assert.Throws<ToolException>(() => ToolProfiles.Import(Mold, "{\"not\": \"a profile\"}", out _, root));
            ToolProfiles.Import(Mold, File.ReadAllText(exported).Replace(Mold, "other-tool"), out note, root);
            Assert.Contains("other-tool", note);

            ToolProfiles.Delete(Mold, "Shop B", root);
            Assert.Null(ToolProfiles.DefaultName(Mold, root));
            Assert.Null(ToolProfiles.Find(Mold, "Shop B", root));

            Assert.Equal(5, ToolProfiles.CopyAll(Mold, "my-mold", root)); // four profiles and the settings
            var forked = ToolProfiles.List("my-mold", root, toolDir: PageFixture.Temp());
            Assert.Equal(4, forked.Count);
            Assert.All(forked, p => Assert.Equal("my-mold", (string)JObject.Parse(File.ReadAllText(p.Path))["tool"]));
        }

        [Fact]
        public void Names_are_checked_and_slugs_are_unique()
        {
            Assert.Equal("Give the profile a name.", ToolProfiles.CheckName("  "));
            Assert.NotNull(ToolProfiles.CheckName(new string('x', 61)));
            Assert.NotNull(ToolProfiles.CheckName("(Tool defaults)"));
            Assert.Equal("shop-a-pu-on-the-50-t-press", ToolProfiles.Slug("Shop A - PU on the 50 t press"));
            var root = PageFixture.Temp();
            var a = ToolProfiles.Save(Mold, "Shop A", new JObject(), root: root);
            var b = ToolProfiles.Save(Mold, "shop-a", new JObject(), root: root);
            Assert.NotEqual(a.Path, b.Path);
            Assert.Throws<ToolException>(() => ToolProfiles.Save(Mold, "SHOP A", new JObject(), root: root));
        }

        [Fact]
        public void A_profile_asked_for_that_does_not_exist_opens_the_form_as_usual()
        {
            var s = Open(PageFixture.Temp(), profile: "Nope");
            Assert.Equal("There is no profile 'Nope' for this tool, so it opened as usual.", (string)s.State()["profile"]["note"]);
            Assert.Null((string)s.State()["profile"]["name"]);
        }

        [Fact]
        public void The_page_without_document_inputs_says_it_left_files_alone()
        {
            var spec = ToolSpec.Load(Mold);
            var form = new ToolForm(spec, ToolData.For(spec), false);
            var result = ProfileValues.Apply(form, new JObject { ["drawing"] = new JArray(@"C:\x.pdf"), ["cycle_life"] = "9000" });
            Assert.Equal(new[] { "Left 'Engineering drawing' as it is: the PropertyManager page takes files and selections only from you." }, result.Warnings);
            Assert.Contains("cycle_life", result.Applied);
        }

        [Fact]
        public void The_requested_profile_is_taken_once()
        {
            ToolProfiles.Request("some-tool", "Shop A");
            Assert.Equal("Shop A", ToolProfiles.TakeRequest("SOME-TOOL"));
            Assert.Null(ToolProfiles.TakeRequest("some-tool"));
        }

        [Fact]
        public void The_mold_record_and_setup_sheet_name_the_profile()
        {
            Assert.Equal("None", SetupSheet.ProfileText(null));
            Assert.Equal("Shop A", SetupSheet.ProfileText(JObject.Parse("{\"name\": \"Shop A\", \"modified\": []}")));
            Assert.Equal("Shop A, changed: press_pressure, cycle_life",
                SetupSheet.ProfileText(JObject.Parse("{\"name\": \"Shop A\", \"modified\": [\"press_pressure\", \"cycle_life\"]}")));
            var template = File.ReadAllText(Path.Combine(ToolSpec.Load(Mold).Dir, "data", "setup-sheet.html"));
            Assert.Contains("<tr><th>Profile</th><td>{{sheet.profile}}</td></tr>", template);
        }

        [Fact]
        public void The_tool_index_lists_profile_names()
        {
            var mold = UserTools.ListTools().Single(t => (string)t["name"] == Mold);
            Assert.Contains("PU shop starter", mold["profiles"].Select(x => (string)x));
        }
    }
}

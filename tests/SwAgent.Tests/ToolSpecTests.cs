using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Scripting;
using SwAgent.Tools;
using SwAgent.UI;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>The tool spec: new front-matter fields, inputs.json options and their checks, the payload and the agent's schema.</summary>
    public class ToolSpecTests
    {
        private static Dictionary<string, string> Fm(params string[] kv)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        [Fact]
        public void Run_settings_come_from_the_front_matter()
        {
            var plain = new ToolSpec();
            ToolSpec.ApplyRunSettings(plain, Fm());
            Assert.Null(plain.Category);
            Assert.Null(plain.RibbonTitle);
            Assert.False(plain.LongRun);
            Assert.Equal(200, plain.MaxSteps);
            Assert.Empty(plain.Requires);

            var mold = new ToolSpec();
            ToolSpec.ApplyRunSettings(mold, Fm("category", "Molding & Toolmaking", "ribbon_title", "Compression Mold", "long_run", "true",
                "requires", "[mold_calc, mold_part, mold_bodies, mold_record]"));
            Assert.Equal("Molding & Toolmaking", mold.Category);
            Assert.Equal("Compression Mold", mold.RibbonTitle);
            Assert.True(mold.LongRun);
            Assert.Equal(600, mold.MaxSteps); // long runs default to 600
            Assert.Equal(new[] { "mold_calc", "mold_part", "mold_bodies", "mold_record" }, mold.Requires);

            var s = new ToolSpec();
            ToolSpec.ApplyRunSettings(s, Fm("max_steps", "5000", "requires", "mold_calc mold_part", "long_run", "no"));
            Assert.Equal(1000, s.MaxSteps); // clamped
            Assert.False(s.LongRun);
            Assert.Equal(new[] { "mold_calc", "mold_part" }, s.Requires);
            ToolSpec.ApplyRunSettings(s, Fm("max_steps", "3"));
            Assert.Equal(50, s.MaxSteps);
            ToolSpec.ApplyRunSettings(s, Fm("ribbon_title", "\"Quoted\""));
            Assert.Equal("Quoted", s.RibbonTitle);
        }

        [Fact]
        public void A_tool_that_needs_newer_agent_tools_is_refused()
        {
            var spec = new ToolSpec { Requires = new[] { "mold_calc", "get_model_context" } };
            Assert.Equal(new[] { "mold_calc" }, ToolRunner.MissingRequirements(spec, new[] { "get_model_context", "rebuild" }));
            Assert.Empty(ToolRunner.MissingRequirements(new ToolSpec(), new string[0]));
        }

        [Fact]
        public void Built_in_tools_know_their_folder_and_keep_defaults()
        {
            var spec = ToolSpec.Load("smart-rebuild");
            Assert.NotNull(spec);
            Assert.Equal(Path.Combine(UserTools.BuiltInRoot, "smart-rebuild"), spec.Dir);
            Assert.Equal(200, spec.MaxSteps);
            Assert.False(spec.LongRun);
            Assert.Empty(spec.Requires);
            Assert.Null(spec.Category);
            Assert.Empty(ToolSpec.CheckInputs(spec.Dir, spec.Inputs));
        }

        [Fact]
        public void Page_test_inputs_parse_with_every_new_option_and_no_warnings()
        {
            var dir = PageFixture.Dir;
            var inputs = ToolSpec.LoadInputs(dir);
            Assert.Empty(ToolSpec.CheckInputs(dir, inputs));
            var grade = inputs.Single(i => i.Id == "part_grade");
            Assert.Equal("data/materials.json#rows", grade.ChoicesFrom);
            Assert.Equal("part_family", grade.DependsOn);
            Assert.Equal("{part_family}", grade.RowFilter["familyLabel"]);
            Assert.Equal("moldingPressurePsi.typ|psi", grade.Fills["molding_pressure"]);
            var mold = inputs.Single(i => i.Id == "mold_grade");
            Assert.Equal("steel", mold.ValueMap["Tool steel"]);
            Assert.Equal(new[] { "Aluminium + steel inserts" }, inputs.Single(i => i.Id == "insert_grade").ShowWhen["mold_material"]);
            var platen = inputs.Single(i => i.Id == "platen_size");
            Assert.Equal(2, platen.Dims);
            Assert.True(platen.Optional);
            Assert.False(platen.Enabled);
            Assert.True(platen.Remember);
            Assert.Equal(new[] { "psi", "bar", "MPa", "kgf/cm2" }, inputs.Single(i => i.Id == "press_pressure").UnitChoices);
            Assert.False(inputs.Single(i => i.Id == "shrink_pct").GroupExpanded);
            Assert.StartsWith("Full strength", inputs.Single(i => i.Id == "grade_info").Text);
            // At least 10 groups (spike S6: ids of groups and controls don't collide).
            Assert.True(inputs.Select(i => i.Group).Distinct().Count() >= 10);

            // Saving keeps every option (the agent edits user tools through SaveInputs).
            var copy = PageFixture.Temp();
            ToolSpec.SaveInputs(copy, inputs);
            var again = ToolSpec.LoadInputs(copy);
            Assert.Equal(JArray.FromObject(inputs).ToString(), JArray.FromObject(again).ToString());
        }

        [Fact]
        public void Page_test_data_resolves_every_choice_and_fill_path()
        {
            var dir = PageFixture.Dir;
            var inputs = ToolSpec.LoadInputs(dir);
            var data = new ToolData(dir);
            foreach (var s in inputs.Where(i => i.ChoicesFrom != null))
            {
                var rows = data.Rows(s.ChoicesFrom);
                Assert.True(rows.Count > 0, s.Id + ": no rows");
                foreach (var kv in s.Fills ?? new Dictionary<string, string>())
                {
                    var (path, unit) = ToolForm.SplitFill(kv.Value);
                    var first = path.Split('.')[0];
                    Assert.True(rows.All(r => r[first] != null), $"{s.Id}: {path} missing in a row"); // a null value is allowed, a missing key is not
                    if (unit != null) Assert.NotNull(Units.FindAny(unit));
                }
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Page_test_macro_compiles_against_the_new_accessors()
        {
            AssemblyResolver.Install();
            var host = new ScriptHost(new ToolRegistry());
            var (_, errors) = await host.CompilePreviewAsync(File.ReadAllText(Path.Combine(PageFixture.Dir, "macro.cs")));
            Assert.True(errors == null, errors);
        }

        [Fact]
        public void Mistakes_in_the_new_options_are_warned()
        {
            var dir = PageFixture.Temp();
            var inputs = new List<ToolInputSpec>
            {
                new ToolInputSpec { Id = "grade", Type = "choice", ChoicesFrom = "data/none.json#rows", DependsOn = "family",
                    Fills = new Dictionary<string, string> { ["nothere"] = "a.b" }, RowFilter = new Dictionary<string, string> { ["x"] = "{ghost}" } },
                new ToolInputSpec { Id = "family", Type = "choice", Choices = new[] { "a" } },
                new ToolInputSpec { Id = "orphan", Type = "choice", DependsOn = "missing", ShowWhen = new Dictionary<string, string[]> { ["nobody"] = new[] { "x" } } },
                new ToolInputSpec { Id = "p", Type = "number", Unit = "furlongs" },
                new ToolInputSpec { Id = "q", Type = "number", Unit = "pressure", UnitChoices = new[] { "psi", "kg" } },
                new ToolInputSpec { Id = "box", Type = "size", Dims = 4 },
                new ToolInputSpec { Id = "line", Type = "info" },
                new ToolInputSpec { Id = "up", Type = "choice", ChoicesFrom = "../outside.json" },
                new ToolInputSpec { Id = "family", Type = "text" },
            };
            var w = ToolSpec.CheckInputs(dir, inputs);
            Assert.Contains(w, x => x.Contains("'grade' depends on 'family', which must be declared before it"));
            Assert.Contains(w, x => x.Contains("'grade' choices_from file 'data/none.json' doesn't exist"));
            Assert.Contains(w, x => x.Contains("'grade' fills 'nothere'"));
            Assert.Contains(w, x => x.Contains("{ghost}"));
            Assert.Contains(w, x => x.Contains("'orphan' depends on 'missing', which doesn't exist"));
            Assert.Contains(w, x => x.Contains("'orphan' show_when names 'nobody'"));
            Assert.Contains(w, x => x.Contains("unknown unit 'furlongs'"));
            Assert.Contains(w, x => x.Contains("'q' unit_choices"));
            Assert.Contains(w, x => x.Contains("'box' dims must be 2 or 3"));
            Assert.Contains(w, x => x.Contains("'line' is an info line without text"));
            Assert.Contains(w, x => x.Contains("'up' choices_from"));
            Assert.Contains(w, x => x.Contains("'family' is declared twice"));
        }

        [Fact]
        public void Payload_entries_keep_the_original_text_without_page_data()
        {
            // Inputs built without the form (the way they always were): the same text as before.
            var inputs = new ToolInputs();
            inputs.Set("len", 0.012);
            inputs.Set("ang", 5 * Math.PI / 180);
            inputs.Set("count", 3.0);
            inputs.Set("flag", true);
            inputs.Set("pick", "Blue");
            Assert.Equal("12 mm (0.012 m)", (string)ToolRunner.InputEntry(new ToolInputSpec { Id = "len", Type = "number" }, inputs)["value"]);
            Assert.Equal("5 deg", (string)ToolRunner.InputEntry(new ToolInputSpec { Id = "ang", Type = "number", Unit = "angle" }, inputs)["value"]);
            Assert.Equal(3.0, (double)ToolRunner.InputEntry(new ToolInputSpec { Id = "count", Type = "integer" }, inputs)["value"]);
            Assert.True((bool)ToolRunner.InputEntry(new ToolInputSpec { Id = "flag", Type = "checkbox" }, inputs)["value"]);
            var pick = ToolRunner.InputEntry(new ToolInputSpec { Id = "pick", Type = "choice", Label = "Pick" }, inputs);
            Assert.Equal("Blue", (string)pick["value"]);
            Assert.Equal("Pick", (string)pick["label"]);
            Assert.Equal("choice", (string)pick["type"]);
            Assert.Null(pick["si"]);
            Assert.Null(ToolRunner.InputEntry(new ToolInputSpec { Id = "i", Type = "info", Text = "x" }, inputs));
        }

        [Fact]
        public void Drawings_can_be_picked_in_file_inputs()
        {
            var filter = ToolPage.FileFilter(new[] { "pdf", "image", "drawing" });
            Assert.StartsWith("Supported files (", filter);
            Assert.Contains("*.slddrw", filter);
            Assert.Contains("DXF/DWG (*.dxf;*.dwg)|*.dxf;*.dwg", filter);
            Assert.EndsWith("All files (*.*)|*.*", filter);
            // Inputs without drawings keep their dialog exactly.
            Assert.Equal("PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*", ToolPage.FileFilter(new[] { "pdf" }));
            Assert.Equal(4, ToolPage.FileFilter(null).Split('|').Length / 2);
        }

        [Fact]
        public void Page_quirks_round_trip()
        {
            var path = Path.Combine(PageFixture.Temp(), "page-quirks.json");
            var q = PageQuirks.Load(path);
            Assert.False(q.ComboVariants || q.NoRangeAfterShow || q.HideByDisabling);
            q.ComboVariants = true;
            q.HideByDisabling = true;
            q.Save();
            var back = PageQuirks.Load(path);
            Assert.True(back.ComboVariants);
            Assert.False(back.NoRangeAfterShow);
            Assert.True(back.HideByDisabling);
            File.WriteAllText(path, "{ \"rangeAfterShow\": false }");
            Assert.True(PageQuirks.Load(path).NoRangeAfterShow);
        }

        [Fact]
        public void The_agents_tool_schema_still_builds_with_the_new_options()
        {
            var registry = new ToolRegistry();
            registry.RegisterAll(new UserTools());
            var save = registry.Definitions().Single(t => (string)t["name"] == "save_user_tool");
            var item = save["input_schema"]["properties"]["inputs"]["items"]["properties"];
            foreach (var name in new[] { "choices_from", "choice_key", "depends_on", "row_filter", "value_map", "fills", "optional", "enabled",
                         "show_when", "remember", "dims", "text", "group_expanded", "unit_choices" })
                Assert.NotNull(item[name]);
            Assert.Equal("boolean", (string)item["optional"]["type"]);
            Assert.Equal("array", (string)item["unit_choices"]["type"]);
        }

        [Fact(Skip = "Needs Track D's ToolRegistry.SchemaFor dictionary case (INTEGRATION-NOTES-B.md): un-skip at integration.")]
        public void Dictionary_options_are_objects_in_the_agents_schema()
        {
            var registry = new ToolRegistry();
            registry.RegisterAll(new UserTools());
            var item = registry.Definitions().Single(t => (string)t["name"] == "save_user_tool")["input_schema"]["properties"]["inputs"]["items"]["properties"];
            foreach (var name in new[] { "row_filter", "value_map", "fills", "show_when" })
                Assert.Equal("object", (string)item[name]["type"]);
            // additionalProperties (string vs string[] values) helps the agent but is optional: when given, it must be right.
            if (item["show_when"]["additionalProperties"] is JObject values) Assert.Equal("array", (string)values["type"]);
            if (item["fills"]["additionalProperties"] is JObject fills) Assert.Equal("string", (string)fills["type"]);
        }

        [Fact]
        public void Reading_the_boxes_at_ok_keeps_small_deliberate_edits()
        {
            // Only display rounding counts as the box echoing what the page wrote; a real edit is far larger.
            Assert.True(ToolPage.RoundingSlack("number", "pressure", true, 2000) < 9); // 2000 -> 2009 psi is read
            Assert.True(ToolPage.RoundingSlack("number", "temperature", true, 212) < 1); // 212 -> 213 degF
            Assert.True(ToolPage.RoundingSlack("integer", null, false, 5000) < 1); // 5000 -> 5001
            Assert.True(ToolPage.RoundingSlack("number", "pressure", true, 13.7895) >= 0.0045); // 13.79 shown for 13.7895
            Assert.True(ToolPage.RoundingSlack("number", "length", false, 0.3) < 0.00002); // 0.02 mm
            Assert.True(ToolPage.RoundingSlack("number", "angle", false, 0.1) < 0.2 * Math.PI / 180);
        }

        [Fact]
        public void Category_is_one_line_of_at_most_48_characters_like_the_tool_index()
        {
            var spec = new ToolSpec();
            ToolSpec.ApplyRunSettings(spec, new Dictionary<string, string> { ["category"] = "  Molding & Toolmaking  " });
            Assert.Equal("Molding & Toolmaking", spec.Category);
            ToolSpec.ApplyRunSettings(spec, new Dictionary<string, string> { ["category"] = new string('x', 60) });
            Assert.Equal(48, spec.Category.Length);
            ToolSpec.ApplyRunSettings(spec, new Dictionary<string, string> { ["category"] = "\"\"" });
            Assert.Null(spec.Category);
        }
    }
}

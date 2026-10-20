using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using SwAgent.Scripting;
using SwAgent.Tools;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>Track E's tool folder against Track A1's data and fillers and Track D's mold_record: page ranges, templates, page ids.</summary>
    public class MoldToolFolderTests
    {
        private static string ToolDir => MoldData.DefaultToolDir;
        private static JArray Inputs => JArray.Parse(File.ReadAllText(Path.Combine(ToolDir, "inputs.json")));

        [Fact]
        public void Every_filled_value_fits_its_field()
        {
            var byId = Inputs.OfType<JObject>().ToDictionary(i => (string)i["Id"]);
            var bad = new List<string>();
            foreach (var input in byId.Values.Where(i => i["Fills"] != null))
            {
                var spec = ((string)input["ChoicesFrom"]).Split('#');
                var rows = JObject.Parse(File.ReadAllText(Path.Combine(ToolDir, spec[0])))[spec[1]].OfType<JObject>();
                foreach (var fill in ((JObject)input["Fills"]).Properties())
                {
                    var target = byId[fill.Name];
                    var parts = ((string)fill.Value).Split('|');
                    foreach (var row in rows)
                    {
                        var v = row.SelectToken(parts[0]);
                        if (v == null || v.Type == JTokenType.Null) continue;
                        if ((string)target["Type"] == "choice") { if (!target["Choices"].Any(c => (string)c == (string)v)) bad.Add($"{row["id"]}: {fill.Name} = {v}"); continue; }
                        var x = (double)v * (parts.Length > 1 && parts[1] == "psi" ? Si.PsiToMPa : 1);
                        if (target["Min"] != null && x < (double)target["Min"] || target["Max"] != null && x > (double)target["Max"]) bad.Add($"{row["id"]}: {fill.Name} = {x}");
                    }
                }
            }
            Assert.Empty(bad);
        }

        private static MoldRecord Sample()
        {
            var folder = Path.Combine(Path.GetTempPath(), "mold-sample-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(ToolDir, "data", "examples", "mold.sample.json"), Path.Combine(folder, MoldRecord.FileName));
            return MoldRecord.Open(folder);
        }

        [Fact]
        public void Both_templates_fill_from_the_sample_record_with_nothing_unresolved_and_machined_bodies_only()
        {
            var rec = Sample();
            Assert.Empty(rec.Validate());
            var setup = SetupSheet.Fill(rec, File.ReadAllText(Path.Combine(ToolDir, "data", "setup-sheet.html")), Data, out var u1);
            var mach = MachiningSheet.Fill(rec, File.ReadAllText(Path.Combine(ToolDir, "data", "machining-sheet.html")), Data, out var u2);
            Assert.Empty(u1);
            Assert.Empty(u2);
            foreach (var html in new[] { setup, mach })
            {
                Assert.DoesNotContain("{{", html);
                Assert.DoesNotContain("PART-REF", html);
                Assert.DoesNotContain("GARBAGE", html);
            }
            // the written sheets report what they could not fill (mold_record lists it)
            var path = SetupSheet.Write(rec, Path.Combine(ToolDir, "data", "setup-sheet.html"), Data, out var written);
            Assert.True(File.Exists(path));
            Assert.Empty(written);
            Assert.Equal("\nUnresolved placeholders: none.", MoldTools.Unresolved(written));
            Assert.Contains("a.b, c", MoldTools.Unresolved(new List<string> { "a.b", "c", "c" }));
        }

        [Fact]
        public void Templates_use_only_filters_the_filler_knows()
        {
            foreach (var name in new[] { "setup-sheet.html", "machining-sheet.html" })
                foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(ToolDir, "data", name)), @"\{\{[^}#/|]+\|([^}]+)\}\}"))
                    Assert.Contains(m.Groups[1].Value.Trim(), Template.Filters);
            // and an unknown filter is reported, never printed as "value filter"
            var unresolved = new List<string>();
            Assert.Equal("-", Template.Render("{{x|furlongs}}", new JObject { ["x"] = 3 }, true, unresolved));
            Assert.Equal(new[] { "x|furlongs (unknown filter)" }, unresolved);
        }

        [Fact]
        public void Template_this_is_the_item_only_and_a_missing_if_is_just_false()
        {
            var ctx = new JObject { ["size"] = "ROOT", ["rows"] = new JArray(new JObject { ["id"] = "A" }, new JObject { ["id"] = "B", ["size"] = "M6" }) };
            var unresolved = new List<string>();
            var html = Template.Render("{{#each rows}}[{{this.id}} {{this.size}}]{{/each}}{{#if nothing.here}}X{{else}}none{{/if}}", ctx, true, unresolved);
            Assert.Equal("[A -][B M6]none", html);
            Assert.Equal(new[] { "this.size" }, unresolved);   // never the root's "size"; the missing #if path is not reported
        }

        [Fact]
        public void Gauge_settings_print_in_the_unit_the_gauge_reads()
        {
            var ctx = new JObject { ["process"] = new JObject { ["gauge"] = new JObject { ["recommended_MPa"] = 13.789514, ["unitShown"] = "bar" } } };
            Assert.Equal("137.9 bar (13.79 MPa)", Template.Render("{{process.gauge.recommended_MPa|gauge}}", ctx, true));
            ctx["process"]["gauge"]["unitShown"] = "psi";
            Assert.Equal("2000 psi (13.79 MPa)", Template.Render("{{process.gauge.recommended_MPa|gauge}}", ctx, false));
            ctx["process"]["gauge"]["unitShown"] = null;
            Assert.Equal(Template.Render("{{process.gauge.recommended_MPa|MPa}}", ctx, true), Template.Render("{{process.gauge.recommended_MPa|gauge}}", ctx, true));
            // the calculator carries the page's picked unit into process.gauge.unitShown
            var d = E2Design();
            d["inputs"]["press_pressure"] = new JObject { ["value"] = "2000 psi (13.79 MPa)", ["si"] = 13789514.6, ["unit"] = "psi" };
            Assert.Equal("psi", (string)MoldCalc.Run("press", d, null, null, Data).RecordPatch.SelectToken("process.gauge.unitShown"));
        }

        [Theory]
        [InlineData("Gauge reading", "press_pressure", 13.79)]
        [InlineData("On the part", "part_pressure", 6.895)]
        [InlineData("Force reading (tons)", "press_force_reading", 177.9)]
        public void Every_press_meaning_reads_its_own_page_field(string meaning, string id, double value)
        {
            Assert.Contains(Inputs, i => (string)i["Id"] == id);
            var d = E2Design();
            var inputs = (JObject)d["inputs"];
            inputs.Remove("press_pressure");
            inputs["press_pressure_is"] = meaning;
            inputs[id] = value;
            Assert.Equal(value, Design.Read(d, Data).PressValue, 6);
        }

        [Fact]
        public void On_the_part_without_its_value_fails_loudly()
        {
            var d = E2Design();
            ((JObject)d["inputs"]).Remove("press_pressure");
            d["inputs"]["press_pressure_is"] = "On the part";
            Assert.Contains("part_pressure", Assert.Throws<ArgumentException>(() => MoldCalc.Run("press", d, null, null, Data)).Message);
            d["inputs"]["part_pressure"] = 0;
            Assert.Contains("part_pressure", Assert.Throws<ArgumentException>(() => MoldCalc.Run("size", d, null, null, Data)).Message);
        }

        [Fact]
        public void Lifting_answers_use_the_page_ids_and_the_press_reach_floor()
        {
            foreach (var id in new[] { "lid_lift_at", "lid_reach", "lid_lifts_per_hour" }) Assert.Contains(Inputs, i => (string)i["Id"] == id);
            var press = Lifting.LidLimit("Inside the press", 40, 12, Cfg());
            Near(7.4, press.Rwl_kg, 1.5, "press, default reach");
            Near(11.67, Lifting.LidLimit("At the slide-out table", 40, 12, Cfg()).Rwl_kg, 1.5, "table");
            Near(11.0, Lifting.LidLimit("Not sure", 40, 12, Cfg()).Rwl_kg, 0.1, "not sure");
        }

        [Fact]
        public void The_page_says_n_a_for_this_grade_like_TOOL_md()
        {
            // the page writes "n/a for this <label of the filling choice>"; TOOL.md must name the line the agent gets
            var grade = Inputs.OfType<JObject>().Single(i => (string)i["Id"] == "part_grade");
            Assert.Equal("Grade", (string)grade["Label"]);
            Assert.Contains("\"n/a for this grade\"", File.ReadAllText(Path.Combine(ToolDir, "TOOL.md")));
        }

        // ---------------------------------------------------------------- mold_record (Track D) pieces that need no SolidWorks

        [Fact]
        public void The_record_status_line_is_its_own_line_at_column_0()
        {
            var folder = Path.Combine(Path.GetTempPath(), "mold-status-" + Guid.NewGuid().ToString("N"));
            try
            {
                var rec = MoldRecord.Init(folder, @"C:\Molds\x mold.SLDPRT", new JObject(), "1.0.0", "0.2.0");
                var text = "merged; status planning; valid" + MoldTools.StatusLine(rec);
                Assert.Contains("\nrecord_status: planning", text);
                Assert.Matches(new Regex("^record_status: planning$", RegexOptions.Multiline), text);
                rec.Merge(new JObject { ["status"] = "done" });
                Assert.EndsWith("\nrecord_status: done", MoldTools.StatusLine(rec));
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        }

        [Fact]
        public void Start_over_keeps_the_old_record_under_a_new_name()
        {
            var folder = Path.Combine(Path.GetTempPath(), "mold-bak-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "mold.json");
                File.WriteAllText(file, "{}");
                var b1 = MoldTools.BackupPath(file);
                Assert.Matches(@"mold\.json\.bak-\d{8}-\d{6}$", b1);
                File.WriteAllText(b1, "{}");
                Assert.NotEqual(b1, MoldTools.BackupPath(file));   // never overwrites an earlier backup
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        }

        [Fact]
        public void Page_inputs_reach_the_record_with_their_SI_value_and_the_calculator_reads_them_right()
        {
            var page = new ToolInputs();
            page.Set("press_pressure", 13789514.6);
            page.SetMeta("press_pressure", new ToolInputs.InputMeta { Si = new JValue(13789514.6), Display = "2000 psi (13.79 MPa)", Unit = "psi" });
            page.Set("lid_reach", 0.55);
            page.SetMeta("lid_reach", new ToolInputs.InputMeta { Si = new JValue(0.55), Display = "550 mm (0.55 m)" });
            page.Set("max_plate_size", new[] { 0.6, 0.4, 0.15 });
            page.SetMeta("max_plate_size", new ToolInputs.InputMeta { Si = new JArray(0.6, 0.4, 0.15), Display = "600 x 400 x 150 mm" });
            page.Set("cavities", 2.0);
            page.SetMeta("cavities", new ToolInputs.InputMeta { Si = new JValue(2.0) });
            page.Set("part_grade", "TDI-polyether / MBOCA 90A");
            var j = MoldTools.InputsJson(page);
            Assert.Equal("psi", (string)j["press_pressure"]["unit"]);
            var d = E2Design();
            foreach (var p in j.Properties()) d["inputs"][p.Name] = p.Value.DeepClone();
            var read = Design.Read(d, Data);
            Assert.Equal(13.7895, read.PressValue, 3);          // MPa, not 13.8 million
            Assert.Equal(55.0, read.LidReach_cm.Value, 9);
            Assert.Equal(new[] { 600.0, 400.0, 150.0 }, read.MaxPlateSize.Select(x => Math.Round(x, 6)));
            Assert.Equal(2, read.Cavities);
            Assert.Equal("psi", read.GaugeUnit);
        }
    }
}

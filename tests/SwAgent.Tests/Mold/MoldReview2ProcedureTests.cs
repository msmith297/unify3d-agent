using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Mold;
using SwAgent.Mold.Geometry;
using SwAgent.ModelFiles;
using SwAgent.Tools;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// Regression tests for the second (integrated) review: procedure, page/panel and SolidWorks-safety items that can be
    /// tested without SolidWorks - waivers, record replacement, grades for body masses, page changes on Continue, split
    /// options, depths per half, the busy gate, the attachment budget, the separate-moat slack and choice edits.
    /// </summary>
    public class MoldReview2ProcedureTests
    {
        private static CalcResult RunMerge(string action, JObject design, JObject query = null, JObject overrides = null)
        {
            var r = MoldCalc.Run(action, design, query, overrides, Data);
            if (r.RecordPatch != null) MoldRecord.DeepMerge(design, r.RecordPatch);
            return r;
        }

        [Fact]
        public void A_waiver_survives_size_and_check()
        {
            // a lid limit far below the lid: WEIGHT-LID fails, the user waives it, size and check run again
            var d = E2Design();
            d["inputs"]["max_lid_mass"] = 1.0;
            RunMerge("size", d);
            var lid = ((JArray)d["checks"]).OfType<JObject>().Single(c => (string)c["id"] == "WEIGHT-LID");
            Assert.Equal("fail", (string)lid["status"]);
            MoldRecord.DeepMerge(d, new JObject { ["checks"] = new JArray(new JObject { ["id"] = "WEIGHT-LID", ["status"] = "waived", ["waivedBy"] = "user", ["note"] = "two-person lift agreed" }) });
            RunMerge("size", d);
            RunMerge("check", d);
            lid = ((JArray)d["checks"]).OfType<JObject>().Single(c => (string)c["id"] == "WEIGHT-LID");
            Assert.Equal("waived", (string)lid["status"]);
            Assert.Equal("user", (string)lid["waivedBy"]);
            Assert.StartsWith("two-person lift agreed", (string)lid["note"]);
            // the user can withdraw it
            MoldRecord.DeepMerge(d, new JObject { ["checks"] = new JArray(new JObject { ["id"] = "WEIGHT-LID", ["status"] = "fail", ["source"] = "agent", ["waivedBy"] = null }) });
            RunMerge("size", d);
            Assert.Equal("fail", (string)((JArray)d["checks"]).OfType<JObject>().Single(c => (string)c["id"] == "WEIGHT-LID")["status"]);
        }

        [Fact]
        public void The_body_report_replaces_bodies_and_its_own_checks()
        {
            var rec = new JObject
            {
                ["bodies"] = new JArray(new JObject { ["id"] = "BASE-1" }, new JObject { ["id"] = "INS-C1-1" }),
                ["checks"] = new JArray(
                    new JObject { ["id"] = "INTERF-BASE-1-INS-C1-1", ["source"] = "mold_bodies", ["status"] = "fail" },
                    new JObject { ["id"] = "A7-C1", ["source"] = "agent", ["status"] = "pass" }),
            };
            MoldRecord.DeepMerge(rec, new JObject
            {
                [MoldRecord.ReplaceKey] = new JArray("bodies"), [MoldRecord.ReplaceChecksFromKey] = new JArray("mold_bodies"),
                ["bodies"] = new JArray(new JObject { ["id"] = "BASE-1" }),
            });
            Assert.Single((JArray)rec["bodies"]);
            Assert.DoesNotContain(((JArray)rec["checks"]).OfType<JObject>(), c => ((string)c["id"]).StartsWith("INTERF-"));
            Assert.Contains(((JArray)rec["checks"]).OfType<JObject>(), c => (string)c["id"] == "A7-C1");
        }

        [Fact]
        public void Body_masses_use_the_grade_mold_calc_wrote()
        {
            var d = E2Design();
            d["inputs"]["mold_grade"] = "al_qc10";
            RunMerge("size", d);
            Assert.Equal("al_qc10", MoldTools.GradeOf(d, "base"));
            Assert.Equal(2830, MoldTools.GradeDensity(Data, MoldTools.GradeOf(d, "base")));
            Assert.NotNull(MoldTools.GradeOf(d, "insert"));
            // before size: the page's grade
            var page = new JObject { ["inputs"] = new JObject { ["mold_grade"] = new JObject { ["value"] = "al_mic6" } } };
            Assert.Equal("al_mic6", MoldTools.GradeOf(page, "lid"));
        }

        [Fact]
        public void Changed_page_values_are_listed_against_the_record()
        {
            var record = new JObject { ["cavities"] = 2, ["press_rated_force"] = new JObject { ["value"] = "50 US ton (444.8 kN)", ["si"] = 444822 }, ["notes"] = "x" };
            var page = new JObject { ["cavities"] = 4, ["press_rated_force"] = new JObject { ["value"] = "60 US ton (533.8 kN)", ["si"] = 533787 }, ["notes"] = "x" };
            var diff = MoldTools.InputDifferences(page, record);
            Assert.Equal(2, diff.Count);
            Assert.Contains(diff, x => x.StartsWith("cavities: page 4, record 2"));
            Assert.Contains(diff, x => x.Contains("60 US ton") && x.Contains("50 US ton"));
            Assert.Empty(MoldTools.InputDifferences((JObject)record.DeepClone(), record));
        }

        [Fact]
        public void Split_writes_the_chosen_option_itself_and_never_claims_unrecorded_options_are_in_the_record()
        {
            var d = E2Design();
            d["inputs"]["max_plate_mass"] = 6.0;
            d["layout"]["gapRequest_mm"] = 70;
            RunMerge("size", d);
            var list = MoldCalc.Run("split", d, new JObject { ["plate"] = "base" }, null, Data);
            Assert.Null(list.RecordPatch);
            Assert.DoesNotContain("(in the record)", list.Text);
            Assert.Contains("\"option\": i", list.Text);
            var n = ((JArray)list.Json["options"]).Count;
            Assert.True(n >= 1);
            var pick = MoldCalc.Run("split", d, new JObject { ["plate"] = "base", ["option"] = 1 }, null, Data);
            Assert.NotNull(pick.RecordPatch);
            MoldRecord.DeepMerge(d, pick.RecordPatch);
            Assert.True(JToken.DeepEquals(list.Json["options"][0], d.SelectToken("structural.split.base")));
            Assert.Throws<ArgumentException>(() => MoldCalc.Run("split", d, new JObject { ["plate"] = "base", ["option"] = n + 1 }, null, Data));
        }

        [Fact]
        public void The_depths_per_half_come_from_the_chosen_parting_line()
        {
            var d = E2Design();
            d["part"]["partingLineCandidates"] = new JArray(
                new JObject { ["id"] = "PL-A", ["w_mm"] = 0.0, ["baseDepth_mm"] = 25.4, ["lidDepth_mm"] = 0.0 },
                new JObject { ["id"] = "PL-B", ["w_mm"] = 15.0, ["baseDepth_mm"] = 15.0, ["lidDepth_mm"] = 10.4 });
            d["partingLine"] = new JObject { ["planes"] = new JArray(new JObject { ["id"] = "PL1", ["w_mm"] = 15.0 }) };
            var x = Design.Read(d, Data);
            Assert.Equal(15.0, x.DepthBase, 9);
            Assert.Equal(10.4, x.DepthLid, 9);
            d["partingLine"]["candidate"] = "PL-A";
            Assert.Equal(25.4, Design.Read(d, Data).DepthBase, 9);
            d["part"]["depthBase_mm"] = 20;
            d["part"]["depthLid_mm"] = 5.4;
            x = Design.Read(d, Data);
            Assert.Equal(20, x.DepthBase, 9);
            Assert.Equal(5.4, x.DepthLid, 9);
        }

        [Fact]
        public async Task Tool_calls_wait_while_SolidWorks_is_in_a_command()
        {
            Assert.True(SwBusyGate.ModelFree("mold_calc"));
            Assert.False(SwBusyGate.ModelFree("mold_part"));
            Assert.False(SwBusyGate.ModelFree("execute_csharp"));
            var calls = 0;
            Assert.True(await SwBusyGate.WaitAsync(() => ++calls < 3, TimeSpan.FromSeconds(5), pollMs: 10));
            Assert.Equal(3, calls);
            Assert.False(await SwBusyGate.WaitAsync(() => true, TimeSpan.FromMilliseconds(80), pollMs: 10));
        }

        [Fact]
        public void A_run_attaches_at_most_its_budget_of_files()
        {
            string B64(int bytes) => Convert.ToBase64String(new byte[bytes]);
            var a = new JObject { ["name"] = "a.pdf", ["kind"] = "pdf", ["data"] = B64(1200) };
            var b = new JObject { ["name"] = "b.pdf", ["kind"] = "pdf", ["data"] = B64(1200) };
            var c = new JObject { ["name"] = "c.png", ["kind"] = "image", ["data"] = B64(300) };
            var used = FileAttachments.ApplyRunBudget(new[] { a, b, c }, budget: 2000);
            Assert.NotNull((string)a["data"]);
            Assert.Null(b["data"]);
            Assert.Equal("file", (string)b["kind"]);
            Assert.Contains("attachment budget", (string)b["error"]);
            Assert.NotNull((string)c["data"]);
            Assert.True(used <= 2000);
        }

        [Fact]
        public void A_layout_at_exactly_the_separate_moat_gap_keeps_separate_moats()
        {
            // the verifier's case: a 4.0 in part at s = 1.014288, origin rounded to 6 digits, measures 38.0999392 mm
            const double gap = 0.0381;
            Assert.False(FlashLayout.TooCloseForSeparateMoats(0.0380999392, gap));
            Assert.True(FlashLayout.TooCloseForSeparateMoats(0.0380, gap));
        }

        [Fact]
        public void An_edited_filled_choice_says_so()
        {
            var dir = MoldTools.ToolDir;
            Assert.True(File.Exists(Path.Combine(dir, "inputs.json")));
            var spec = new ToolSpec { Name = MoldTools.ToolName, Title = "Mold", Dir = dir, Inputs = ToolSpec.LoadInputs(dir), Paused = new System.Collections.Generic.HashSet<string>(), Context = new System.Collections.Generic.HashSet<string>() };
            var form = new ToolForm(spec, new ToolData(dir), true, null);
            var basis = form.Get("shrink_basis");
            var filled = basis.Value;
            Assert.False(string.IsNullOrEmpty(filled));
            var other = new[] { "Supplier datasheet (steel mold)", "Measured in my molds at this temperature", "Hot cavity to cold part" }.First(x => x != filled);
            form.SetChoice(basis, other);
            var line = PageFixture.Line(form, "shrink_basis");
            Assert.Equal($"{other} (table {basis.TableText} - edited by the user)", line);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Memory;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>docs/agent-memory.md 2a: fork_tool makes the user's own copy of a read-only tool, which runs exactly like the original.</summary>
    [Collection("Memory")]
    public class ForkToolTests
    {
        private const string Mold = "design-compression-mold";

        private static string Unique(string stem) => stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

        [Fact]
        public void Forking_the_mold_tool_copies_it_renames_it_copies_the_notes_and_records_the_original()
        {
            using (var env = new MemoryTestEnv())
            {
                var name = Unique("mold-mine");
                env.Track(name);
                env.Call("remember", new JObject { ["scope"] = "tool:" + Mold, ["text"] = "Hide leftover part copies when done." });

                var r = env.Call("fork_tool", new JObject { ["source"] = Mold, ["name"] = name, ["title"] = "My compression mold" });
                Assert.False(r.IsError, MemoryTestEnv.Text(r));
                var text = MemoryTestEnv.Text(r);
                Assert.Contains("offer_restart", text);
                Assert.Contains("edit_user_tool", text);
                Assert.Contains("1 note on the original is copied", text);

                var dir = Path.Combine(UserTools.Root, name);
                foreach (var f in new[] { "inputs.json", "macro.cs", "data/materials.json", "data/shop-standards.json", "data/setup-sheet.html" })
                    Assert.True(File.Exists(Path.Combine(dir, f)), f);
                var fm = UserTools.FrontMatter(File.ReadAllText(Path.Combine(dir, "TOOL.md")));
                Assert.Equal(name, fm["name"]);
                Assert.Equal("My compression mold", fm["title"]);
                Assert.StartsWith(Mold + "@", fm["forked_from"]);
                Assert.Equal("Compression Mold (mine)", fm["ribbon_title"]);
                Assert.Equal("Molding & Toolmaking", fm["category"]);
                Assert.Equal("part", fm["icon"]);
                Assert.Equal("mold_calc, mold_part, mold_bodies, mold_record", fm["requires"]);
                Assert.Contains("Hide leftover part copies when done.", UserTools.StandingSection(File.ReadAllText(Path.Combine(dir, "TOOL.md"))));
                Assert.True(File.Exists(env.Store.ToolOverlayPath(Mold))); // copied: the original keeps its notes

                var listed = UserTools.ListTools().Single(t => (string)t["name"] == name);
                Assert.Equal(Mold, (string)listed["forkedFrom"]);
                Assert.False((bool)listed["sourceChanged"]);
                Assert.False((bool)listed["readOnly"]);
                Assert.True((bool)listed["hasNotes"]);
                Assert.Equal(Mold, ToolForks.SourceOf(name));

                // the copy is editable like any user tool
                Assert.False(env.Call("edit_user_tool", new JObject { ["name"] = name, ["append"] = "## My rule\n\nAlways 4 dowels." }).IsError);
                // a second copy gets another name; an existing name is refused
                Assert.True(env.Call("fork_tool", new JObject { ["source"] = Mold, ["name"] = "smart-rebuild" }).IsError);
                Assert.True(env.Call("fork_tool", new JObject { ["source"] = name }).IsError); // already the user's own
            }
        }

        [Fact]
        public void The_copy_runs_on_its_own_data()
        {
            using (var env = new MemoryTestEnv())
            {
                var name = Unique("mold-data");
                env.Track(name);
                Assert.False(env.Call("fork_tool", new JObject { ["source"] = Mold, ["name"] = name }).IsError);
                var dir = Path.Combine(UserTools.Root, name);

                var shop = JObject.Parse(File.ReadAllText(Path.Combine(dir, "data", "shop-standards.json")));
                shop["flash"]["lip"] = new JObject { ["in"] = 0.2 };
                File.WriteAllText(Path.Combine(dir, "data", "shop-standards.json"), shop.ToString());
                var materials = JObject.Parse(File.ReadAllText(Path.Combine(dir, "data", "materials.json")));
                materials["rows"].First(row => (string)row["id"] == "pu-tdi-ether-70a")["name"] = "Our shop 70A";
                File.WriteAllText(Path.Combine(dir, "data", "materials.json"), materials.ToString());

                var run = new ToolContext(null, null, "thread-fork", env.Registry) { RunTool = name };
                Assert.Equal(name, MoldTools.ActiveTool(run));
                Assert.Equal(Mold, MoldTools.ActiveTool(env.Ctx));
                Assert.Equal(5.08, MoldTools.LoadData(MoldTools.ActiveTool(run)).ShopMm("flash.lip", 0), 6);
                Assert.NotEqual(5.08, MoldTools.LoadData(Mold).ShopMm("flash.lip", 0), 6);

                var lookup = new JObject { ["action"] = "lookup", ["query"] = new JObject { ["part"] = "pu-tdi-ether-70a" } };
                Assert.Contains("Our shop 70A", MemoryTestEnv.Text(env.Call("mold_calc", lookup, run)));
                Assert.DoesNotContain("Our shop 70A", MemoryTestEnv.Text(env.Call("mold_calc", lookup)));
            }
        }

        [Fact]
        public void Hiding_the_original_is_per_PC_and_undoable()
        {
            using (var env = new MemoryTestEnv())
            {
                var name = Unique("mold-hide");
                env.Track(name);
                try
                {
                    Assert.False(env.Call("fork_tool", new JObject { ["source"] = Mold, ["name"] = name, ["hide_original"] = true }).IsError);
                    Assert.True((bool)UserTools.ListTools().Single(t => (string)t["name"] == Mold)["hidden"]);
                    Assert.Equal(name, MoldTools.ActiveTool(env.Ctx)); // the original hidden: the user's copy does the work

                    // with the copy's name, fork_tool only shows or hides the original
                    var r = env.Call("fork_tool", new JObject { ["source"] = Mold, ["name"] = name, ["hide_original"] = false });
                    Assert.False(r.IsError, MemoryTestEnv.Text(r));
                    Assert.False((bool)UserTools.ListTools().Single(t => (string)t["name"] == Mold)["hidden"]);
                }
                finally
                {
                    UserTools.SetValue(Mold, "hidden", null);
                }
            }
        }

        [Fact]
        public void Compare_fork_tells_the_originals_changes_from_the_users_and_takes_them_on_request()
        {
            using (var env = new MemoryTestEnv())
            {
                var name = Unique("mold-cmp");
                env.Track(name);
                Assert.False(env.Call("fork_tool", new JObject { ["source"] = Mold, ["name"] = name }).IsError);
                var dir = Path.Combine(UserTools.Root, name);
                Assert.Contains("hasn't changed", MemoryTestEnv.Text(env.Call("compare_fork", new JObject { ["name"] = name })));

                // the user changes the copy's macro; the original "changed" its materials (the record says it was different then)
                File.AppendAllText(Path.Combine(dir, "macro.cs"), "\n// mine\n");
                var record = ToolForks.ReadRecord(dir);
                record["base"]["data/materials.json"] = "0000";
                File.WriteAllText(Path.Combine(dir, ToolForks.RecordFile), record.ToString());
                File.WriteAllText(Path.Combine(dir, "data", "materials.json"), "{\"rows\": []}");
                record["mine"]["data/materials.json"] = ToolForks.Hashes(dir, fresh: true)["data/materials.json"];
                File.WriteAllText(Path.Combine(dir, ToolForks.RecordFile), record.ToString());
                Assert.True(ToolForks.SourceChanged(dir));
                Assert.True((bool)UserTools.ListTools().Single(t => (string)t["name"] == name)["sourceChanged"]);
                Assert.Contains("compare_fork", MemoryTestEnv.Text(env.Call("read_user_tool", new JObject { ["name"] = name })));

                var text = MemoryTestEnv.Text(env.Call("compare_fork", new JObject { ["name"] = name }));
                Assert.Contains("Changed only in the original (take them as they are): data/materials.json", text);
                Assert.Contains("Changed only in the copy: macro.cs", text);
                Assert.Contains("--- data/materials.json", text);

                Assert.False(env.Call("compare_fork", new JObject { ["name"] = name, ["take"] = new JArray("data/materials.json") }).IsError);
                Assert.Equal(File.ReadAllText(Path.Combine(UserTools.BuiltInRoot, Mold, "data", "materials.json")), File.ReadAllText(Path.Combine(dir, "data", "materials.json")));
                Assert.False(ToolForks.SourceChanged(dir));
                Assert.Contains("// mine", File.ReadAllText(Path.Combine(dir, "macro.cs"))); // the user's change stays
            }
        }

        [Fact]
        public void Rewrite_renames_self_references_and_keeps_the_rest()
        {
            var md = "---\nname: flash-grooves\ntitle: Flash grooves\nribbon_title: Grooves\nintelligence: balanced\n---\n\n# Flash grooves\n\n" +
                     "Run [Run tool: flash-grooves] or read_user_tool(flash-grooves). Not flash-grooves-old, not data/flash-grooves.json.\n";
            var text = ToolForks.Rewrite(md, "flash-grooves", "Flash grooves", "flash-grooves-custom", "Flash grooves (my version)", "flash-grooves@2026-10-01",
                new System.Collections.Generic.Dictionary<string, string> { ["intelligence"] = "high", ["hidden"] = "true" });
            var fm = UserTools.FrontMatter(text);
            Assert.Equal("flash-grooves-custom", fm["name"]);
            Assert.Equal("Flash grooves (my version)", fm["title"]);
            Assert.Equal("Grooves (mine)", fm["ribbon_title"]);
            Assert.Equal("high", fm["intelligence"]); // the user's setting for the original carries over
            Assert.False(fm.ContainsKey("hidden"));
            Assert.Equal("flash-grooves@2026-10-01", fm["forked_from"]);
            Assert.Contains("# Flash grooves (my version)", text);
            Assert.Contains("[Run tool: flash-grooves-custom]", text);
            Assert.Contains("read_user_tool(flash-grooves-custom)", text);
            Assert.Contains("flash-grooves-old", text);
            Assert.Contains("data/flash-grooves-custom.json", text);
        }

        [Fact]
        public void Diff_shows_changed_lines_with_context()
        {
            var d = ToolForks.Diff("a\nb\nc\nd\ne", "a\nb\nC\nd\ne", 1000);
            Assert.Equal("  b\n- c\n+ C\n  d", d.Replace("\r\n", "\n"));
            Assert.Equal("", ToolForks.Diff("x\ny", "x\r\ny", 1000));
        }
    }
}

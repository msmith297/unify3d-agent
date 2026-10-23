using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Memory;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// Standing instructions and the user's copies of read-only tools write the shared test data folder (tools, overlays,
    /// builtin-settings.json): one at a time.
    /// </summary>
    [CollectionDefinition("Memory", DisableParallelization = true)]
    public class MemoryCollection
    {
    }

    /// <summary>A throw-away memory store and the agent tools that use it, under the test data folder (SWAGENT_DATA_DIR).</summary>
    internal sealed class MemoryTestEnv : IDisposable
    {
        public readonly string Dir;
        public readonly MemoryStore Store;
        public readonly ToolRegistry Registry = new ToolRegistry();
        public readonly MemoryTools Tools;
        public ToolContext Ctx;
        private readonly MemoryStore _previous;

        public MemoryTestEnv()
        {
            Dir = Path.Combine(Infrastructure.Paths.LocalDir, "memory-tests", Guid.NewGuid().ToString("N").Substring(0, 10));
            Store = new MemoryStore(Path.Combine(Dir, "agent-knowledge"));
            _previous = MemoryStore.Instance;
            MemoryStore.Instance = Store; // read_user_tool, use_skill and the tool list read the add-in's store
            Tools = new MemoryTools(() => Store);
            Registry.RegisterAll(Tools);
            Registry.RegisterAll(new UserTools());
            Registry.RegisterAll(new ForkTools());
            Registry.RegisterAll(new MoldTools());
            Ctx = new ToolContext(null, null, "thread-memory", Registry);
        }

        public ToolResult Call(string tool, JObject input, ToolContext ctx = null) => Registry.InvokeAsync(tool, input, ctx ?? Ctx).GetAwaiter().GetResult();

        public static string Text(ToolResult r) => string.Join("\n", r.Content.Where(c => (string)c["type"] == "text").Select(c => (string)c["text"]));

        public static string IdIn(string text) => System.Text.RegularExpressions.Regex.Match(text, @"\(id ([gts][0-9a-f]{6})\)").Groups[1].Value;

        /// <summary>A user tool for a test, removed again on Dispose.</summary>
        public string UserTool(string instructions = "# Steps\n\n1. Do it.")
        {
            var name = "mem-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _tools.Add(name);
            Call("save_user_tool", new JObject { ["name"] = name, ["title"] = "Memory test " + name, ["description"] = "For a test.", ["instructions"] = instructions });
            return name;
        }

        private readonly System.Collections.Generic.List<string> _tools = new System.Collections.Generic.List<string>();

        public void Track(string tool) => _tools.Add(tool);

        public void Dispose()
        {
            MemoryStore.Instance = _previous;
            foreach (var t in _tools)
            {
                try
                {
                    var dir = Path.Combine(UserTools.Root, t);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
                catch
                {
                    // best effort
                }
            }
            try
            {
                Directory.Delete(Dir, true);
            }
            catch
            {
                // best effort
            }
        }
    }

    /// <summary>docs/agent-memory.md: remember, list_memory, forget, the overlays, Undo, and where the notes reach the agent.</summary>
    [Collection("Memory")]
    public class MemoryTests
    {
        [Fact]
        public void General_notes_go_to_memory_md_and_the_prompt_index_and_can_be_forgotten()
        {
            using (var env = new MemoryTestEnv())
            {
                var r = env.Call("remember", new JObject { ["scope"] = "general", ["text"] = "Always answer in inches." });
                Assert.False(r.IsError, MemoryTestEnv.Text(r));
                var text = MemoryTestEnv.Text(r);
                Assert.Contains("Saved to your general memory: Always answer in inches.", text);
                var id = MemoryTestEnv.IdIn(text);
                Assert.StartsWith("g", id);

                var file = File.ReadAllText(env.Store.GeneralFile);
                Assert.Contains("- " + DateTime.Now.ToString("yyyy-MM-dd") + ": Always answer in inches.", file);
                var index = env.Store.Index();
                Assert.Equal("Always answer in inches.", (string)index["general"][0]["text"]);
                Assert.Equal(id, (string)index["general"][0]["id"]);

                // the same text again is not a second note
                Assert.Contains("Already saved", MemoryTestEnv.Text(env.Call("remember", new JObject { ["scope"] = "general", ["text"] = "always answer in inches." })));
                Assert.Single(env.Store.Notes(env.Store.General()));

                Assert.Contains(id, MemoryTestEnv.Text(env.Call("list_memory", new JObject())));
                Assert.False(env.Call("forget", new JObject { ["id"] = id }).IsError);
                Assert.Empty(env.Store.Notes(env.Store.General()));
                Assert.False(File.Exists(env.Store.GeneralFile));
                Assert.True(env.Call("forget", new JObject { ["id"] = id }).IsError);
            }
        }

        [Fact]
        public void A_built_in_tool_gets_an_overlay_that_reaches_read_user_tool_and_the_run_staging()
        {
            using (var env = new MemoryTestEnv())
            {
                const string mold = "design-compression-mold";
                var shipped = File.ReadAllText(Path.Combine(UserTools.BuiltInRoot, mold, "TOOL.md"));
                var r = env.Call("remember", new JObject { ["scope"] = "tool:" + mold, ["text"] = "Hide leftover part copies outside the cavities when the mold is done." });
                Assert.False(r.IsError, MemoryTestEnv.Text(r));
                Assert.Contains("Saved to your notes for the Design compression mold for plastics and resins tool", MemoryTestEnv.Text(r));
                Assert.StartsWith("t", MemoryTestEnv.IdIn(MemoryTestEnv.Text(r)));

                // the shipped folder is untouched; the note is an overlay
                Assert.Equal(shipped, File.ReadAllText(Path.Combine(UserTools.BuiltInRoot, mold, "TOOL.md")));
                Assert.True(File.Exists(env.Store.ToolOverlayPath(mold)));

                // read_user_tool and the [Run tool:] staging (ToolRunner's payload "notes") carry it
                var read = MemoryTestEnv.Text(env.Call("read_user_tool", new JObject { ["name"] = mold }));
                Assert.Contains("Standing instructions from the user (they win over the text above):", read);
                Assert.Contains("Hide leftover part copies outside the cavities when the mold is done.", read);
                Assert.Contains("fork_tool", read); // read-only is never a dead end
                Assert.Contains("Hide leftover part copies", env.Store.OverlayText(mold));

                // the tool list marks it
                Assert.True((bool)UserTools.ListTools().Single(t => (string)t["name"] == mold)["hasNotes"]);
                Assert.False((bool)UserTools.ListTools().Single(t => (string)t["name"] == "smart-rebuild")["hasNotes"]);
            }
        }

        [Fact]
        public void A_user_tools_notes_go_into_its_TOOL_md_and_survive_a_rewrite()
        {
            using (var env = new MemoryTestEnv())
            {
                var tool = env.UserTool();
                var r = env.Call("remember", new JObject { ["scope"] = "tool:" + tool, ["text"] = "Default the groove depth to 0.5 mm." });
                Assert.False(r.IsError, MemoryTestEnv.Text(r));
                Assert.Contains("the Memory test " + tool + " tool's instructions", MemoryTestEnv.Text(r));
                var md = Path.Combine(UserTools.Root, tool, "TOOL.md");
                var text = File.ReadAllText(md);
                Assert.Contains("## Standing instructions", text);
                Assert.Contains(": Default the groove depth to 0.5 mm.", text);
                Assert.Contains("1. Do it.", text);
                Assert.Null(env.Store.OverlayText(tool)); // in TOOL.md, not an overlay
                Assert.True((bool)UserTools.ListTools().Single(t => (string)t["name"] == tool)["hasNotes"]);

                // save_user_tool rewrites the instructions but keeps the user's standing instructions
                env.Call("save_user_tool", new JObject { ["name"] = tool, ["title"] = "T", ["description"] = "D", ["instructions"] = "# New steps\n\n1. Other." });
                text = File.ReadAllText(md);
                Assert.Contains("1. Other.", text);
                Assert.Contains("Default the groove depth to 0.5 mm.", text);

                // a second note goes in the same section; forgetting the last removes the section
                var second = MemoryTestEnv.IdIn(MemoryTestEnv.Text(env.Call("remember", new JObject { ["scope"] = "tool:" + tool, ["text"] = "Fillet every groove corner." })));
                Assert.Single(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(md), "## Standing instructions").Cast<System.Text.RegularExpressions.Match>());
                var first = env.Store.ToolNotes(tool).First().Id;
                env.Call("forget", new JObject { ["id"] = first });
                env.Call("forget", new JObject { ["id"] = second });
                Assert.DoesNotContain("Standing instructions", File.ReadAllText(md));
                Assert.Contains("1. Other.", File.ReadAllText(md));
            }
        }

        [Fact]
        public void Replaces_removes_the_old_note_and_undo_restores_it()
        {
            using (var env = new MemoryTestEnv())
            {
                var old = MemoryTestEnv.IdIn(MemoryTestEnv.Text(env.Call("remember", new JObject { ["scope"] = "general", ["text"] = "Answer in metric." })));
                var r = env.Call("remember", new JObject { ["scope"] = "general", ["text"] = "Answer in inches.", ["replaces"] = new JArray(old) });
                Assert.Contains("Replaced: Answer in metric.", MemoryTestEnv.Text(r));
                var id = MemoryTestEnv.IdIn(MemoryTestEnv.Text(r));
                Assert.Equal(new[] { "Answer in inches." }, env.Store.Notes(env.Store.General()).Select(n => n.Text));

                Assert.True(env.Store.Undo(id)); // the chat's Undo
                Assert.Equal(new[] { "Answer in metric." }, env.Store.Notes(env.Store.General()).Select(n => n.Text));

                // an unknown id changes nothing
                Assert.True(env.Call("remember", new JObject { ["scope"] = "general", ["text"] = "X", ["replaces"] = new JArray("g000000") }).IsError);
                Assert.Single(env.Store.Notes(env.Store.General()));
            }
        }

        [Fact]
        public void Caps_scopes_and_the_saving_switch_are_enforced()
        {
            using (var env = new MemoryTestEnv())
            {
                Assert.True(env.Call("remember", new JObject { ["scope"] = "tool:no-such-tool", ["text"] = "x" }).IsError);
                Assert.True(env.Call("remember", new JObject { ["scope"] = "skill:nothing", ["text"] = "x" }).IsError);
                Assert.True(env.Call("remember", new JObject { ["scope"] = "general", ["text"] = new string('a', MemoryStore.NoteMax + 1) }).IsError);

                // general memory is capped (it is in every system prompt): over it nothing is saved and the agent is told to merge
                ToolResult last = null;
                for (var i = 0; i < 40 && (last == null || !last.IsError); i++)
                    last = env.Call("remember", new JObject { ["scope"] = "general", ["text"] = $"Rule {i}: " + new string('x', 200) });
                Assert.True(last.IsError);
                Assert.Contains("is full", MemoryTestEnv.Text(last));
                Assert.True(env.Store.Index()["used"].Value<int>() <= MemoryStore.GeneralCap);

                // Options switch: saving off refuses
                var off = new MemoryTools(() => env.Store) { Saving = () => false };
                var reg = new ToolRegistry();
                reg.RegisterAll(off);
                var r = reg.InvokeAsync("remember", new JObject { ["scope"] = "general", ["text"] = "y" }, env.Ctx).GetAwaiter().GetResult();
                Assert.True(r.IsError);
                Assert.Contains("turned off", MemoryTestEnv.Text(r));
            }
        }

        [Fact]
        public void Skill_notes_are_an_overlay_for_installed_plugins_and_a_section_for_local_ones()
        {
            using (var env = new MemoryTestEnv())
            {
                var local = Path.Combine(env.Dir, "local-skill");
                Directory.CreateDirectory(local);
                File.WriteAllText(Path.Combine(local, "SKILL.md"), "---\ndescription: x\n---\n\nDo the thing.\n");
                env.Store.SkillLookup = id => id == "bridge:connect" ? (Path.Combine(env.Dir, "cache-skill"), false)
                    : id == "mine:check" ? (local, true) : ((string, bool)?)null;

                var r = env.Call("remember", new JObject { ["scope"] = "skill:bridge:connect", ["text"] = "Summarise first." });
                Assert.False(r.IsError, MemoryTestEnv.Text(r));
                Assert.True(File.Exists(Path.Combine(env.Store.SkillNotesDir, "bridge.connect.md")));
                Assert.Contains("Summarise first.", env.Store.SkillOverlayText("bridge:connect"));
                Assert.Equal("bridge:connect", (string)env.Store.Index()["skills"][0]["id"]);

                Assert.False(env.Call("remember", new JObject { ["scope"] = "skill:mine:check", ["text"] = "Check twice." }).IsError);
                Assert.Contains("## Standing instructions", File.ReadAllText(Path.Combine(local, "SKILL.md")));
                Assert.Contains("Do the thing.", File.ReadAllText(Path.Combine(local, "SKILL.md")));
            }
        }

        [Fact]
        public void Options_can_edit_a_note_and_list_every_scope()
        {
            using (var env = new MemoryTestEnv())
            {
                var id = MemoryTestEnv.IdIn(MemoryTestEnv.Text(env.Call("remember", new JObject { ["scope"] = "general", ["text"] = "Never close my documents." })));
                var edited = env.Store.Edit(id, "Never close or save my documents.");
                Assert.NotEqual(id, edited.Id);
                Assert.Equal("Never close or save my documents.", env.Store.Notes(env.Store.General()).Single().Text);
                var list = env.Store.List();
                Assert.Equal("general", (string)list[0]["scope"]);
                Assert.Equal("Never close or save my documents.", (string)list[0]["notes"][0]["text"]);
            }
        }
    }
}

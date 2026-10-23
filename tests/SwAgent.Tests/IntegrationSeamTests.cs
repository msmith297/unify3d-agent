using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Memory;
using SwAgent.Plugins;
using SwAgent.Tests.Plugins;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// Where standing instructions, tool copies and tool profiles meet (the v3 integration): a copy of the mold tool carries
    /// the user's notes and profiles and opens with the same default; "always use profile X" makes X the default (no note);
    /// a run of the copy stages its notes. The user's profiles of the mold tool live in the test data folder for the test's length.
    /// </summary>
    [Collection("Memory")]
    public class ForkProfileSeamTests
    {
        private const string Mold = "design-compression-mold";

        private static void RemoveProfiles(params string[] tools)
        {
            foreach (var t in tools)
                try
                {
                    var dir = ToolProfiles.UserDir(t);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
                catch
                {
                    // best effort
                }
        }

        [Fact]
        public void A_copy_of_the_mold_tool_keeps_the_users_notes_and_profiles_and_opens_with_the_same_default()
        {
            using (var env = new MemoryTestEnv())
            {
                env.Registry.RegisterAll(new ProfileTools());
                var name = "mold-seam-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                var shop = "Seam shop " + Guid.NewGuid().ToString("N").Substring(0, 4);
                env.Track(name);
                RemoveProfiles(Mold);
                try
                {
                    ToolProfiles.Save(Mold, shop, new JObject { ["cycle_life"] = "20000", ["mold_material"] = "Tool steel" });

                    // "always use <shop> for molds": an unknown profile saves nothing; a real one becomes the default, not a note
                    var wrong = env.Call("remember", new JObject { ["scope"] = "tool:" + Mold, ["text"] = "Always use the No such shop profile.", ["profile"] = "No such shop" });
                    Assert.True(wrong.IsError);
                    Assert.Contains("PU shop starter", MemoryTestEnv.Text(wrong)); // it names the profiles there are
                    Assert.DoesNotContain("No such shop", MemoryTestEnv.Text(env.Call("list_memory", new JObject())));
                    var r = env.Call("remember", new JObject { ["scope"] = "tool:" + Mold, ["text"] = $"Always use the {shop} profile.", ["profile"] = shop.ToUpperInvariant() });
                    Assert.False(r.IsError, MemoryTestEnv.Text(r));
                    Assert.Contains($"\"{shop}\" profile is now the default", MemoryTestEnv.Text(r)); // its own spelling
                    Assert.Equal(shop, ToolProfiles.DefaultName(Mold));
                    Assert.Empty(env.Store.ToolNotes(Mold));
                    Assert.True(env.Call("remember", new JObject { ["scope"] = "general", ["text"] = "Use inches.", ["profile"] = shop }).IsError); // tools only
                    env.Call("remember", new JObject { ["scope"] = "tool:" + Mold, ["text"] = "Hide leftover part copies when done." });

                    var fork = env.Call("fork_tool", new JObject { ["source"] = Mold, ["name"] = name });
                    Assert.False(fork.IsError, MemoryTestEnv.Text(fork));
                    var text = MemoryTestEnv.Text(fork);
                    Assert.Contains("1 note on the original is copied", text);
                    Assert.Contains("profiles of the original are copied too: " + shop, text);

                    // the notes: copied, so the original keeps them; a run of either stages them for the agent
                    Assert.Single(env.Store.ToolNotes(Mold));
                    Assert.Single(env.Store.ToolNotes(name));
                    Assert.Contains("Hide leftover part copies when done.", UserTools.StandingSection(File.ReadAllText(Path.Combine(UserTools.Root, name, "TOOL.md"))));
                    foreach (var tool in new[] { Mold, name })
                    {
                        var staged = env.Store.RunNotes(tool);
                        Assert.StartsWith("Standing instructions from the user", staged);
                        Assert.Contains("Hide leftover part copies when done.", staged);
                    }
                    Assert.Null(env.Store.RunNotes("smart-rebuild"));

                    // the profiles: the user's with their default, and the shipped ones, which now name the copy
                    Assert.Equal(shop, ToolProfiles.DefaultName(name));
                    var list = ToolProfiles.List(name);
                    Assert.Contains(list, p => p.Name == shop && !p.Locked);
                    Assert.Contains(list, p => p.Name == "PU shop starter" && p.Locked);
                    Assert.All(list, p => Assert.Equal(name, p.Tool));
                    Assert.Contains(shop, UserTools.ListTools().Single(t => (string)t["name"] == name)["profiles"].Select(x => (string)x));
                    Assert.Contains("hasn't changed", MemoryTestEnv.Text(env.Call("compare_fork", new JObject { ["name"] = name }))); // retargeting isn't a change

                    // the copy's form opens with the default profile, read against the copy's own inputs and data
                    var spec = ToolSpec.Load(name);
                    var form = ProfileSession.OpenForm(spec, false, null, out var profiles, PageFixture.Temp(), documentInputs: true);
                    var s = new ToolFormSession(spec, form, false) { Profiles = profiles };
                    profiles.Attach(s.DocumentInputs());
                    var state = s.State();
                    Assert.Equal($"Loaded your default profile: {shop}.", (string)state["profile"]["note"]);
                    Assert.Empty(profiles.Warnings);
                    Assert.Equal("Tool steel", (string)state["groups"].SelectMany(g => g["inputs"]).Single(i => (string)i["id"] == "mold_material")["value"]);
                    var inputs = s.Finish(PageFixture.Temp());
                    Assert.Equal(shop, (string)inputs.Profile["name"]);
                    Assert.Equal(shop, ToolProfiles.LastUsed(name));

                    // save_tool_profile on the copy changes only the copy
                    var saved = env.Call("save_tool_profile", new JObject { ["tool"] = name, ["name"] = "Copy only", ["values"] = new JObject { ["cycle_life"] = "30000" }, ["make_default"] = true });
                    Assert.False(saved.IsError, MemoryTestEnv.Text(saved));
                    Assert.Equal("Copy only", ToolProfiles.DefaultName(name));
                    Assert.Equal(shop, ToolProfiles.DefaultName(Mold));
                    Assert.Null(ToolProfiles.Find(Mold, "Copy only"));
                }
                finally
                {
                    RemoveProfiles(Mold, name);
                }
            }
        }
    }

    /// <summary>
    /// The plugin creator bundles a copy of the mold tool with the user's profile and ships a helper: the plugin validates,
    /// its tool offers the profiles locked and opens with them, and the helper reaches code cells once the user approves.
    /// </summary>
    [Collection("Plugins")]
    public class CreatorSeamTests
    {
        private const string Mold = "design-compression-mold";

        private const string SeamMath = @"// summary: halves a length the shop's way
namespace SwAgent.UserHelpers
{
    public static class SeamMath
    {
        public static double Half(double x) => x / 2;
    }
}";

        private static string Text(ToolResult r) => string.Join("\n", r.Content.Select(c => (string)c["text"]));

        [Fact]
        public void A_plugin_made_by_the_creator_ships_a_tool_with_profiles_and_a_helper()
        {
            var tool = "mold-ship-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            const string plugin = "seam-shop";
            var id = plugin + ":" + tool;
            using (var env = new PluginTestEnv())
            using (var k = new KnowledgeTestEnv(() => env.Pm))
                try
                {
                    // the user's copy of the mold tool, with a profile of their own
                    Assert.False(new ForkTools().Fork(null, new ForkTools.ForkInput { Source = Mold, Name = tool }).IsError);
                    ToolProfiles.Save(tool, "Shop A", new JObject { ["cycle_life"] = "20000", ["mold_material"] = "Tool steel" });

                    var tools = new PluginTools { Manager = () => env.Pm };
                    Assert.False(tools.Create(null, new PluginTools.CreateInput { Name = plugin, Description = "Our mold shop" }).IsError);
                    // without profiles: the creator is told the user has some, to ask
                    var asked = tools.AddTool(null, new PluginTools.AddToolInput { Plugin = plugin, Tool = tool });
                    Assert.Contains("profiles: true ships them", Text(asked));
                    Directory.Delete(Path.Combine(env.Pm.Find(plugin).Root, "tools", tool), true);
                    var added = tools.AddTool(null, new PluginTools.AddToolInput { Plugin = plugin, Tool = tool, Profiles = true });
                    Assert.False(added.IsError, Text(added));
                    Assert.Contains("ships the profile Shop A", Text(added));
                    Assert.False(tools.WriteFile(null, new PluginTools.WriteInput { Plugin = plugin, Path = "helpers/SeamMath.cs", Content = SeamMath }).IsError);

                    var p = env.Pm.Find(plugin);
                    var folder = Path.Combine(p.Root, "tools", tool, "profiles");
                    foreach (var f in Directory.GetFiles(folder, "*.json"))
                        Assert.Equal(id, (string)JObject.Parse(File.ReadAllText(f))["tool"]);
                    var validate = Text(tools.Validate(null, new PluginTools.PluginInput { Plugin = plugin }));
                    Assert.DoesNotContain("error", validate);
                    Assert.DoesNotContain("profiles/", validate);

                    // the user approves the code (macro and helper) in the trust dialog
                    Assert.True(env.Pm.Enable(p.Id, true, env.Pm.CurrentHash(p)).Ok);
                    Assert.NotNull(UserTools.DirOf(id));
                    var list = ToolProfiles.List(id);
                    Assert.Contains(list, x => x.Name == "Shop A" && x.Locked);
                    Assert.Contains(list, x => x.Name == "PU shop starter" && x.Locked);
                    Assert.Contains("Shop A", UserTools.ListTools().Single(t => (string)t["name"] == id)["profiles"].Select(x => (string)x));

                    // whoever installs it opens the tool with a shipped profile as their default
                    ToolProfiles.SetDefault(id, "Shop A");
                    var spec = ToolSpec.Load(id);
                    var form = ProfileSession.OpenForm(spec, false, null, out var profiles, PageFixture.Temp(), documentInputs: true);
                    Assert.Equal("Shop A", profiles.Loaded?.Name);
                    Assert.Empty(profiles.Warnings);

                    k.Helpers.Invalidate();
                    var built = k.Helpers.Current().Find("SeamMath");
                    Assert.NotNull(built);
                    Assert.Equal("plugin:" + plugin, built.Source);
                    var cell = k.Cell("SeamMath.Half(5)");
                    Assert.False(cell.IsError, KnowledgeTestEnv.Text(cell));
                    Assert.Contains("2.5", KnowledgeTestEnv.Text(cell));
                }
                finally
                {
                    UserTools.Delete(tool);
                    foreach (var t in new[] { tool, id })
                    {
                        var dir = ToolProfiles.UserDir(t);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    }
                }
        }
    }
}

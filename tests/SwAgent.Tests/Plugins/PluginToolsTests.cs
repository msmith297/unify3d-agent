using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>The agent's plugin tools (use_skill, read_plugin_file, the plugin creator's tools) and command expansion.</summary>
    [Collection("Plugins")]
    public class PluginToolsTests
    {
        private static string Text(ToolResult r) => string.Join("\n", r.Content.Select(c => (string)c["text"]));

        [Fact]
        public void The_registry_offers_the_plugin_tools_with_buildable_schemas()
        {
            var registry = AgentAddIn.BuildTools(out _);
            var names = new HashSet<string>(registry.Definitions().Select(d => (string)d["name"]));
            foreach (var n in new[] { "use_skill", "read_plugin_file", "list_plugins", "read_plugin", "plugin_create", "plugin_write_file", "plugin_add_tool", "plugin_validate", "plugin_enable", "plugin_package" })
                Assert.Contains(n, names);
            var create = registry.Definitions().Single(d => (string)d["name"] == "plugin_create");
            Assert.NotNull(create["input_schema"]["properties"]["display_name"]);
        }

        [Fact]
        public void Create_write_validate_enable_and_package_a_local_plugin_then_install_the_package_elsewhere()
        {
            using (var env = new PluginTestEnv())
            {
                var tools = new PluginTools { Manager = () => env.Pm };
                Assert.False(tools.Create(null, new PluginTools.CreateInput { Name = "mold-shop", Description = "Shop rules", DisplayName = "Mold Shop", Category = "Molding & Toolmaking" }).IsError);
                Assert.True(tools.Create(null, new PluginTools.CreateInput { Name = "mold-shop", Description = "again" }).IsError);
                Assert.True(tools.Create(null, new PluginTools.CreateInput { Name = "Bad Name", Description = "x" }).IsError);
                var p = env.Pm.Find("mold-shop");
                Assert.True(p.Local);
                Assert.False(p.Enabled);
                Assert.Equal("Mold Shop", p.Manifest.DisplayName);

                // writes are checked first, and stay inside the plugin
                Assert.True(tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "mold-shop", Path = "hooks/hooks.json", Content = "{ not json" }).IsError);
                Assert.True(tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "mold-shop", Path = "commands/x.md", Content = "no front matter" }).IsError);
                Assert.Throws<ToolException>(() => tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "mold-shop", Path = "../escape.md", Content = "x" }));
                Assert.Throws<ToolException>(() => tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "mold-shop", Path = "C:\\Windows\\x.md", Content = "x" }));

                Assert.False(tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "mold-shop", Path = "commands/check.md", Content = "---\ndescription: Check a mold\nargument-hint: <part>\n---\nCheck $ARGUMENTS against @rules.md" }).IsError);
                Assert.False(tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "mold-shop", Path = "rules.md", Content = "Walls >= 12 mm." }).IsError);
                Assert.False(tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "mold-shop", Path = "skills/shop-rules/SKILL.md", Content = "---\nname: shop-rules\ndescription: The shop's mold rules\n---\nFollow ${CLAUDE_PLUGIN_ROOT}/rules.md" }).IsError);
                Assert.False(tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "mold-shop", Path = "skills/shop-rules/table.md", Content = "| a | b |" }).IsError);
                Assert.False(tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "mold-shop", Path = "agents/reviewer.md", Content = "---\nname: reviewer\ndescription: Reviews molds\ntools: Read, mold_calc\nmodel: sonnet\n---\nYou review molds." }).IsError);

                // a hook: written, but it runs only after the user approves it
                var hook = tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "mold-shop", Path = "hooks/hooks.json", Content = PluginTestEnv.Hooks("echo hi") });
                Assert.Contains("approve", Text(hook));

                // a user tool goes in as "mold-shop:<tool>"
                var userTool = "plugin-test-tool-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                new UserTools().Save(null, new UserTools.SaveInput { Name = userTool, Title = "T", Description = "D", Instructions = "Do it.", Kind = "workflow" });
                try
                {
                    Assert.False(tools.AddTool(null, new PluginTools.AddToolInput { Plugin = "mold-shop", Tool = userTool }).IsError);
                    Assert.True(tools.AddTool(null, new PluginTools.AddToolInput { Plugin = "mold-shop", Tool = userTool }).IsError); // already there
                    Assert.True(tools.AddTool(null, new PluginTools.AddToolInput { Plugin = "mold-shop", Tool = "smart-rebuild" }).IsError); // built-ins can't
                    Assert.True(File.Exists(Path.Combine(p.Root, "tools", userTool, "TOOL.md")));
                    Assert.NotNull(UserTools.DirOf(userTool)); // copied, not moved
                }
                finally
                {
                    UserTools.Delete(userTool);
                }

                var validate = Text(tools.Validate(null, new PluginTools.PluginInput { Plugin = "mold-shop" }));
                Assert.DoesNotContain("error", validate);

                var enable = Text(tools.Enable(null, new PluginTools.EnableInput { Plugin = "mold-shop", Enabled = true }));
                Assert.Contains("approve", enable);
                Assert.True(env.Pm.Find("mold-shop").Enabled);
                Assert.Empty(env.Pm.Runnable()); // the agent can't grant trust
                Assert.Equal(UserTools.DirOf("mold-shop:" + userTool), Path.Combine(p.Root, "tools", userTool));

                // components of an enabled plugin
                Assert.Equal("mold-shop:check", CommandIndex.All(env.Pm).Single().Id);
                Assert.Equal("<part>", (string)CommandIndex.All(env.Pm).Single().ToJson()["argumentHint"]);
                var agent = AgentIndex.All(env.Pm).Single().ToJson();
                Assert.Equal(new[] { "Read", "mold_calc" }, agent["tools"].Select(t => (string)t).ToArray());
                Assert.Equal("sonnet", (string)agent["model"]);
                Assert.Equal("You review molds.", (string)agent["prompt"]);

                var skill = Text(tools.UseSkill(null, new PluginTools.SkillInput { Name = "shop-rules" }));
                Assert.Contains("Follow " + p.Root + "/rules.md", skill);
                Assert.Contains("skills/shop-rules/table.md", skill);
                Assert.Equal("| a | b |", Text(tools.ReadFile(null, new PluginTools.FileInput { Plugin = "mold-shop", Path = "skills/shop-rules/table.md" })));
                Assert.Throws<ToolException>(() => tools.ReadFile(null, new PluginTools.FileInput { Plugin = "mold-shop", Path = "../../installed.json" }));

                var listed = Text(tools.List(null, new PluginTools.NoInput()));
                Assert.Contains("mold-shop (mold-shop@local, enabled, local: editable - hooks/MCP not approved", listed);
                Assert.Contains("Reviews molds", Text(tools.Read(null, new PluginTools.PluginInput { Plugin = "mold-shop" })));

                // package: a zip, a copy and marketplace.json, which installs on "another PC"
                var (zip, folder) = PluginTools.Package(env.Pm.Find("mold-shop"), Path.Combine(env.Dir, "packages"));
                Assert.EndsWith("mold-shop-0.1.0.zip", zip);
                using (var z = ZipFile.OpenRead(zip)) Assert.Contains(z.Entries, e => e.FullName.Replace('\\', '/') == ".claude-plugin/plugin.json");
                var market = MarketplaceManifest.Load(folder);
                Assert.Equal("./mold-shop", market.Plugins.Single().Source.Path);

                using (var other = new PluginTestEnv())
                {
                    var m = other.Pm.AddMarketplace(folder);
                    var ask = other.Pm.Install("mold-shop@" + m.Name);
                    Assert.NotNull(ask.NeedsTrust); // the hook travels with it and is asked about again
                    Assert.True(other.Pm.Install("mold-shop@" + m.Name, ask.Hash).Ok);
                    Assert.NotNull(UserTools.DirOf("mold-shop:" + userTool));
                }
            }
        }

        [Fact]
        public void Installed_plugins_are_read_only_to_the_creator_tools()
        {
            using (var env = new PluginTestEnv())
            {
                env.AddPlugin("bought");
                env.Pm.AddMarketplace(env.Market);
                env.Pm.Install("bought@test-market");
                var tools = new PluginTools { Manager = () => env.Pm };
                Assert.Contains("read-only", Assert.Throws<ToolException>(() => tools.WriteFile(null, new PluginTools.WriteInput { Plugin = "bought", Path = "x.md", Content = "x" })).Message);
                Assert.Throws<ToolException>(() => tools.Package(null, new PluginTools.PluginInput { Plugin = "bought" }));
                Assert.Throws<ToolException>(() => tools.ReadFile(null, new PluginTools.FileInput { Plugin = "nothing", Path = "x" }));
            }
        }

        [Fact]
        public void Validate_reports_what_would_break()
        {
            using (var env = new PluginTestEnv())
            {
                var dir = Path.Combine(env.Pm.LocalDir, "broken");
                PluginTestEnv.WriteJson(Path.Combine(dir, ".claude-plugin", "plugin.json"), new JObject
                {
                    ["name"] = "not-the-folder",
                    ["version"] = "one",
                    ["userConfig"] = new JObject { ["key"] = new JObject { ["type"] = "secret", ["sensitive"] = true } },
                });
                PluginTestEnv.Write(Path.Combine(dir, "hooks", "hooks.json"), PluginTestEnv.Hooks("tool.exe --key ${user_config.key}"));
                PluginTestEnv.Write(Path.Combine(dir, "skills", "s", "SKILL.md"), "no front matter");
                PluginTestEnv.Write(Path.Combine(dir, "agents", "rev.md"), "---\nname: rev\n---\n");
                PluginTestEnv.Write(Path.Combine(dir, "agents", "x.md"), "---\nname: x\ndescription: too short a name\n---\nPrompt");
                PluginTestEnv.Write(Path.Combine(dir, "tools", "Bad_Tool", "TOOL.md"), "x");
                PluginTestEnv.Write(Path.Combine(dir, "tools", "no-md", "readme.txt"), "x");
                var f = PluginTools.Validate(env.Pm.Find("broken"));
                Assert.Contains(f, x => x.StartsWith("error") && x.Contains("must match the folder name"));
                Assert.Contains(f, x => x.StartsWith("warning") && x.Contains("not semantic"));
                Assert.Contains(f, x => x.StartsWith("error") && x.Contains("userConfig.key"));
                Assert.Contains(f, x => x.StartsWith("error") && x.Contains("sensitive option on its command line"));
                Assert.Contains(f, x => x.StartsWith("error") && x.Contains("skills/s/SKILL.md needs front matter"));
                Assert.Contains(f, x => x.StartsWith("error") && x.Contains("agent rev needs front matter with a description"));
                Assert.Contains(f, x => x.StartsWith("error") && x.Contains("agents/x.md: its name"));
                Assert.Contains(f, x => x.StartsWith("error") && x.Contains("tools/Bad_Tool"));
                Assert.Contains(f, x => x.StartsWith("error") && x.Contains("tools/no-md has no TOOL.md"));

                var tools = new PluginTools { Manager = () => env.Pm };
                Assert.True(tools.Enable(null, new PluginTools.EnableInput { Plugin = "broken", Enabled = true }).IsError);
                Assert.False(env.Pm.Find("broken").Enabled);
            }
        }

        [Fact]
        public void Commands_expand_arguments_plugin_files_and_never_run_shell_lines()
        {
            using (var env = new PluginTestEnv())
            {
                var dir = Path.Combine(env.Pm.LocalDir, "cmds");
                PluginTestEnv.WriteJson(Path.Combine(dir, ".claude-plugin", "plugin.json"), new JObject { ["name"] = "cmds" });
                PluginTestEnv.Write(Path.Combine(dir, "commands", "fix.md"), "---\ndescription: Fix\nmodel: claude-haiku-5-5\n---\nFix $1 with depth $2 ($ARGUMENTS). See @docs/rules.md and @someone. Root ${CLAUDE_PLUGIN_ROOT}. !`git status` $9.");
                PluginTestEnv.Write(Path.Combine(dir, "docs", "rules.md"), "RULE TEXT");
                PluginTestEnv.Write(Path.Combine(dir, "commands", "inherit.md"), "---\ndescription: d\nmodel: inherit\n---\nx");
                var other = Path.Combine(env.Pm.LocalDir, "other");
                PluginTestEnv.WriteJson(Path.Combine(other, ".claude-plugin", "plugin.json"), new JObject { ["name"] = "other" });
                PluginTestEnv.Write(Path.Combine(other, "commands", "fix.md"), "other fix");
                env.Pm.Enable("cmds@local", true);

                var r = CommandIndex.Expand(env.Pm, "fix", "\"flash groove\" 2");
                Assert.Equal("Fix flash groove with depth 2 (\"flash groove\" 2). See \n<file path=\"docs/rules.md\">\nRULE TEXT\n</file>\n and @someone. Root " + dir + ". !`git status` .", (string)r["text"]);
                Assert.Equal("claude-haiku-5-5", (string)r["model"]);
                Assert.Contains("not run", (string)r["note"]);
                Assert.Null(CommandIndex.Expand(env.Pm, "/cmds:inherit", "")["model"]);

                env.Pm.Enable("other@local", true);
                Assert.Contains("several plugins", Assert.Throws<InvalidOperationException>(() => CommandIndex.Expand(env.Pm, "fix", "")).Message);
                Assert.Equal("other fix", (string)CommandIndex.Expand(env.Pm, "other:fix", "")["text"]);
                Assert.Throws<InvalidOperationException>(() => CommandIndex.Expand(env.Pm, "nope", ""));

                // @ paths stay inside the plugin
                var notes = new List<string>();
                Assert.Equal("see @../other/commands/fix.md", CommandIndex.ExpandBody("see @../other/commands/fix.md", "", dir, null, notes));
                Assert.Equal(new[] { "a b", "c", "" }, CommandIndex.SplitArgs("'a b' c \"\"").ToArray());
            }
        }
    }
}

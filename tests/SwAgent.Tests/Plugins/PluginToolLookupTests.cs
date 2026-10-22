using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>Plugin tools as "plugin:tool": one lookup (UserTools.DirOf), read-only unless local, Windows-safe per-tool files.</summary>
    [Collection("Plugins")]
    public class PluginToolLookupTests
    {
        [Fact]
        public void Installed_plugin_tools_resolve_by_namespaced_name_and_are_read_only()
        {
            using (var env = new PluginTestEnv())
            {
                var src = env.AddPlugin("mold-kit");
                PluginTestEnv.AddTool(src, "vent-pins");
                env.Pm.AddMarketplace(env.Market);
                Assert.Null(UserTools.DirOf("mold-kit:vent-pins")); // not installed
                env.Pm.Install("mold-kit@test-market");

                var dir = UserTools.DirOf("mold-kit:vent-pins");
                Assert.Equal(Path.Combine(env.Pm.CacheDir, "mold-kit", "1.0.0", "tools", "vent-pins"), dir);
                Assert.Equal(dir, UserTools.DirOf("  Mold-Kit:Vent-Pins ")); // same normalising as user tools
                Assert.True(UserTools.IsReadOnly("mold-kit:vent-pins"));
                Assert.True(UserTools.IsBuiltIn("mold-kit:vent-pins")); // the existing read-only callers treat it like a built-in
                Assert.True(UserTools.IsPluginTool("mold-kit:vent-pins"));
                Assert.Null(UserTools.DirOf("mold-kit:nope"));
                Assert.Null(UserTools.DirOf("mold-kit:vent-pins:x"));
                Assert.Null(UserTools.DirOf("mold kit:vent-pins"));

                var spec = ToolSpec.Load("mold-kit:vent-pins");
                Assert.NotNull(spec);
                Assert.Equal("vent-pins title", spec.Title);
                Assert.Equal("tool", spec.Kind);

                var listed = UserTools.ListTools().Single(t => (string)t["name"] == "mold-kit:vent-pins");
                Assert.Equal("mold-kit", (string)listed["plugin"]);
                Assert.True((bool)listed["readOnly"]);
                Assert.False((bool)listed["builtIn"]);
                Assert.Equal("vent-pins title", (string)listed["title"]);

                var tools = new UserTools();
                var read = tools.Read(null, new UserTools.ReadInput { Name = "mold-kit:vent-pins" });
                Assert.Contains("Do vent-pins.", (string)read.Content[0]["text"]);
                Assert.Contains("installed plugin: read-only", (string)read.Content[0]["text"]);
                var edit = Assert.Throws<ToolException>(() => tools.Edit(null, new UserTools.EditInput { Name = "mold-kit:vent-pins", Append = "x" }));
                Assert.Contains("installed plugin", edit.Message);
                Assert.Throws<ToolException>(() => UserTools.Delete("mold-kit:vent-pins"));
                Assert.True(tools.Save(null, new UserTools.SaveInput { Name = "mold-kit:vent-pins", Title = "x", Description = "x", Instructions = "x" }).IsError);

                // per-PC settings of a read-only plugin tool go to builtin-settings.json under a file-safe key
                var settings = Path.Combine(UserTools.Root, "builtin-settings.json");
                var before = File.Exists(settings) ? File.ReadAllText(settings) : null;
                try
                {
                    UserTools.SetValue("mold-kit:vent-pins", "intelligence", "high");
                    Assert.NotNull(JObject.Parse(File.ReadAllText(settings))["mold-kit+vent-pins"]);
                    Assert.Equal("high", ToolSpec.Load("mold-kit:vent-pins").Intelligence);
                    UserTools.SetValue("mold-kit:vent-pins", "intelligence", null);
                }
                finally
                {
                    if (before == null) File.Delete(settings);
                    else File.WriteAllText(settings, before);
                }

                // disabled plugins contribute nothing
                env.Pm.Enable("mold-kit@test-market", false);
                Assert.Null(UserTools.DirOf("mold-kit:vent-pins"));
                Assert.DoesNotContain(UserTools.ListTools(), t => (string)t["name"] == "mold-kit:vent-pins");
            }
        }

        [Fact]
        public void Local_plugin_tools_stay_editable()
        {
            using (var env = new PluginTestEnv())
            {
                var local = Path.Combine(env.Pm.LocalDir, "my-kit");
                PluginTestEnv.WriteJson(Path.Combine(local, ".claude-plugin", "plugin.json"), new JObject { ["name"] = "my-kit" });
                PluginTestEnv.AddTool(local, "groove");
                env.Pm.Enable("my-kit@local", true);

                Assert.False(UserTools.IsReadOnly("my-kit:groove"));
                Assert.False((bool)UserTools.ListTools().Single(t => (string)t["name"] == "my-kit:groove")["readOnly"]);
                var r = new UserTools().Edit(null, new UserTools.EditInput { Name = "my-kit:groove", Append = "## Rule\nKeep 2 mm." });
                Assert.False(r.IsError);
                Assert.Contains("Keep 2 mm.", File.ReadAllText(Path.Combine(local, "tools", "groove", "TOOL.md")));
            }
        }

        [Fact]
        public void Per_tool_files_use_a_windows_safe_storage_key()
        {
            Assert.Equal("plugin+tool", UserTools.StorageKey("Plugin:Tool "));
            Assert.Equal("flash-grooves", UserTools.StorageKey("flash-grooves"));
            var dir = Path.Combine(Path.GetTempPath(), "swagent-remember-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                RememberStore.Save("kit:tool", new JObject { ["depth"] = 2 }, dir);
                Assert.True(File.Exists(Path.Combine(dir, "kit+tool.json")));
                Assert.Equal(2, (int)RememberStore.Load("kit:tool", dir)["depth"]);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}

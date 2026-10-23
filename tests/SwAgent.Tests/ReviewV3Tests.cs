using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Mcp;
using SwAgent.Plugins;
using SwAgent.Tests.Plugins;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// The 2026-10-22 review of creator v2, standing instructions and tool profiles: check_prerequisites runs no path, MCP
    /// headers never follow a redirect to another host, a server's answer is capped, and the profile and memory stores keep
    /// the user's data (reserved "profiles", a deleted tool's profiles, damaged files kept as .bad, Undo in place).
    /// </summary>
    [Collection("Plugins")]
    public class ReviewV3PluginTests
    {
        [Fact]
        public void Check_prerequisites_runs_a_program_from_the_path_never_a_file_by_path()
        {
            var dir = Path.Combine(Paths.LocalDir, "review-v3", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(dir, "scripts"));
            var marker = Path.Combine(dir, "marker.txt");
            var probe = Path.Combine(dir, "scripts", "probe.cmd");
            File.WriteAllText(probe, $"@echo ran %1> \"{marker}\"\r\n@echo 1.2.3\r\n");
            try
            {
                foreach (var asked in new[] { probe, @"scripts\probe.cmd", "scripts/probe.cmd", @".\probe.cmd", @"..\probe", "C:probe.cmd", ".probe" })
                {
                    var r = CreatorTools.Check(asked);
                    Assert.False(r.Ok, asked);
                    Assert.Contains("not a path", r.Line);
                }
                Assert.False(File.Exists(marker), "a path was run");

                // A program "on the PATH" that is really in the add-in's data folders (a plugin's script) isn't run either.
                var ran = new List<string>();
                CreatorTools.FindOverride = n => n == "probe" ? probe : null;
                CreatorTools.VersionOverride = p =>
                {
                    ran.Add(p);
                    return "1.2.3";
                };
                try
                {
                    var r = CreatorTools.Check("probe");
                    Assert.False(r.Ok);
                    Assert.Contains("probe: not found", r.Line);
                    Assert.Empty(ran);
                }
                finally
                {
                    CreatorTools.FindOverride = null;
                    CreatorTools.VersionOverride = null;
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task Mcp_headers_never_follow_a_redirect_to_another_host()
        {
            var headers = new Dictionary<string, string> { ["X-Api-Key"] = "SECRET-PROBE-123" };
            using (var other = new FakeHttpMcp(FakeHttpMcp.Kind.Json))
            using (var server = new FakeHttpMcp(FakeHttpMcp.Kind.Json) { Redirect = other.Url })
            {
                var c = new McpHttpClient("moved", $"http://127.0.0.1:{server.Port}/old", headers);
                var ex = await Assert.ThrowsAnyAsync<McpException>(() => c.StartAsync());
                Assert.Contains($"redirects to http://127.0.0.1:{other.Port}/mcp, another host", ex.Message);
                Assert.Contains(".mcp.json", ex.Message);
                Assert.Empty(other.Requests); // the key never reached it
            }

            // the same server under another path: followed, with the body
            using (var server = new FakeHttpMcp(FakeHttpMcp.Kind.Json))
            {
                server.Redirect = server.Url;
                using (var c = new McpHttpClient("trailing", $"http://127.0.0.1:{server.Port}/old", headers))
                {
                    await c.StartAsync();
                    var r = await c.CallToolAsync("echo", new JObject { ["text"] = "moved" }, TimeSpan.FromSeconds(10), default);
                    Assert.Equal("moved", (string)r["content"][0]["text"]);
                }
                Assert.Contains(server.Requests, x => x.StartsWith("POST /old"));
                Assert.Contains(server.Requests, x => x.StartsWith("POST /mcp"));
            }
        }

        private static Stream Bytes(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

        [Fact]
        public async Task A_servers_answers_are_capped()
        {
            var cap = McpHttpClient.MaxAnswerChars;
            McpHttpClient.MaxAnswerChars = 2048;
            try
            {
                var big = new string('x', 3000);
                var tooBig = await Assert.ThrowsAnyAsync<McpException>(() => McpHttpClient.ReadEventsAsync(new MemoryStream(Encoding.UTF8.GetBytes("data: " + big + "\n\n")), (e, d) => true));
                Assert.Contains("over 2 KB", tooBig.Message);
                // many data lines that add up are refused too
                var lines = string.Concat(Enumerable.Repeat("data: " + new string('y', 500) + "\n", 6)) + "\n";
                await Assert.ThrowsAnyAsync<McpException>(() => McpHttpClient.ReadEventsAsync(new MemoryStream(Encoding.UTF8.GetBytes(lines)), (e, d) => true));

                await Assert.ThrowsAnyAsync<McpException>(() => McpHttpClient.ReadCappedAsync(Bytes(big), McpHttpClient.MaxAnswerChars, true));
                Assert.Equal(100, (await McpHttpClient.ReadCappedAsync(Bytes(big), 100, false)).Length); // an error body: cut, not refused
                Assert.Equal("{\"a\":1}", await McpHttpClient.ReadCappedAsync(Bytes("{\"a\":1}"), McpHttpClient.MaxAnswerChars, true));
            }
            finally
            {
                McpHttpClient.MaxAnswerChars = cap;
            }

            // lines end in \n, \r\n or \r, as in a browser
            var events = new List<(string, string)>();
            await McpHttpClient.ReadEventsAsync(new MemoryStream(Encoding.UTF8.GetBytes("data: a\r\ndata: b\r\rdata: c\n\nevent: x\rdata: d")), (e, d) =>
            {
                events.Add((e, d));
                return true;
            });
            Assert.Equal(new[] { ("message", "a\nb"), ("message", "c"), ("x", "d") }, events);
        }
    }

    [Collection("Memory")]
    public class ReviewV3StoreTests
    {
        private static string Unique(string stem) => stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);

        [Fact]
        public void The_profiles_folder_is_never_a_tool_and_deleting_a_tool_takes_only_its_own_profiles()
        {
            using (var env = new MemoryTestEnv())
            {
                var other = Unique("rv3-other");
                try
                {
                    ToolProfiles.Save(other, "Shop A", new JObject { ["cycle_life"] = "20000" });
                    var r = env.Call("save_user_tool", new JObject { ["name"] = "profiles", ["title"] = "P", ["description"] = "D", ["instructions"] = "1. x" });
                    Assert.True(r.IsError);
                    Assert.Contains("reserved", MemoryTestEnv.Text(r));
                    Assert.Throws<ToolException>(() => UserTools.Delete("profiles"));
                    Assert.NotNull(ToolProfiles.Find(other, "Shop A"));

                    // a deleted tool's profiles go with it: a new tool of that name starts clean
                    var tool = env.UserTool();
                    ToolProfiles.Save(tool, "Mine", new JObject { ["x"] = "1" });
                    ToolProfiles.SetDefault(tool, "Mine", toolDir: Path.Combine(UserTools.Root, tool));
                    UserTools.Delete(tool);
                    Assert.False(Directory.Exists(ToolProfiles.UserDir(tool)));
                    Assert.NotNull(ToolProfiles.Find(other, "Shop A")); // others untouched
                }
                finally
                {
                    ToolProfiles.RemoveAll(other);
                }
            }
        }

        [Fact]
        public void A_fork_gets_the_originals_profiles_not_a_deleted_tools_leftovers()
        {
            var root = PageFixture.Temp();
            ToolProfiles.Save("src-tool", "Shop A", new JObject { ["press"] = "3000 psi" }, root: root);
            ToolProfiles.SetDefault("src-tool", "Shop A", root, toolDir: PageFixture.Temp());
            // left over from a deleted "src-tool-custom" of an older version
            ToolProfiles.Save("src-tool-custom", "Shop A", new JObject { ["press"] = "1000 psi" }, root: root);
            ToolProfiles.Save("src-tool-custom", "Old", new JObject(), root: root);
            ToolProfiles.SetDefault("src-tool-custom", "Old", root, toolDir: PageFixture.Temp());

            Assert.Equal(2, ToolProfiles.CopyAll("src-tool", "src-tool-custom", root, fresh: true));
            Assert.Equal("3000 psi", (string)ToolProfiles.Find("src-tool-custom", "Shop A", root).Values["press"]);
            Assert.Null(ToolProfiles.Find("src-tool-custom", "Old", root));
            Assert.Equal("Shop A", ToolProfiles.DefaultName("src-tool-custom", root));
        }

        [Fact]
        public void A_moved_tool_keeps_the_users_default_and_the_profiles_that_did_not_ship()
        {
            var root = PageFixture.Temp();
            ToolProfiles.Save("mine", "Shop A", new JObject { ["press"] = "3000 psi" }, root: root);
            ToolProfiles.Save("mine", "Shop B", new JObject { ["press"] = "2000 psi" }, root: root);
            ToolProfiles.SetDefault("mine", "Shop A", root, toolDir: PageFixture.Temp());
            ToolProfiles.MarkUsed("mine", "Shop B", root);

            ToolProfiles.CopyAll("mine", "shop:mine", root, except: new[] { "Shop A" }); // Shop A shipped with the plugin
            Assert.Equal("Shop A", ToolProfiles.DefaultName("shop:mine", root)); // now opens the shipped (locked) one
            Assert.Equal("Shop B", ToolProfiles.LastUsed("shop:mine", root));
            var dir = PageFixture.Temp();
            Assert.Equal(new[] { "Shop B" }, ToolProfiles.List("shop:mine", root, dir).Select(p => p.Name));
        }

        [Fact]
        public void Damaged_profile_settings_and_undo_journal_are_kept_as_bad()
        {
            var root = PageFixture.Temp();
            var settings = Path.Combine(ToolProfiles.UserDir("bad-tool", root), ToolProfiles.SettingsFile);
            Directory.CreateDirectory(Path.GetDirectoryName(settings));
            const string damaged = "{\"default\": \"Shop A\", ";
            File.WriteAllText(settings, damaged);
            ToolProfiles.MarkUsed("bad-tool", "Shop B", root);
            Assert.Equal(damaged, File.ReadAllText(settings + ".bad"));
            Assert.Equal("Shop B", ToolProfiles.LastUsed("bad-tool", root));

            using (var env = new MemoryTestEnv())
            {
                var journal = Path.Combine(env.Store.Root, "memory-undo.json");
                Directory.CreateDirectory(env.Store.Root);
                File.WriteAllText(journal, "[{\"id\": ");
                Assert.False(env.Call("remember", new JObject { ["scope"] = "general", ["text"] = "Answer in metric." }).IsError);
                Assert.Equal("[{\"id\": ", File.ReadAllText(journal + ".bad"));
            }
        }

        [Fact]
        public void Undo_puts_replaced_notes_back_where_they_were()
        {
            using (var env = new MemoryTestEnv())
            {
                string Remember(string text, params string[] replaces)
                {
                    var input = new JObject { ["scope"] = "general", ["text"] = text };
                    if (replaces.Length > 0) input["replaces"] = new JArray(replaces);
                    var r = env.Call("remember", input);
                    Assert.False(r.IsError, MemoryTestEnv.Text(r));
                    return MemoryTestEnv.IdIn(MemoryTestEnv.Text(r));
                }
                string[] General() => env.Store.Notes(env.Store.General()).Select(n => n.Text).ToArray();

                Remember("Rule A.");
                var b = Remember("Rule B.");
                Remember("Rule C.");
                var d = Remember("Rule D.");
                var e = Remember("Rule E.", b, d);
                Assert.Equal(new[] { "Rule A.", "Rule C.", "Rule E." }, General());
                Assert.True(env.Store.Undo(e));
                Assert.Equal(new[] { "Rule A.", "Rule B.", "Rule C.", "Rule D." }, General());
            }
        }

        [Fact]
        public void A_tool_runs_notes_say_they_win_over_its_TOOL_md()
        {
            using (var env = new MemoryTestEnv())
            {
                var tool = env.UserTool();
                Assert.False(env.Call("remember", new JObject { ["scope"] = "tool:" + tool, ["text"] = "Hide leftover copies." }).IsError);
                Assert.StartsWith("Standing instructions from the user for this tool (they win over its TOOL.md):\n", env.Store.RunNotes(tool));
            }
        }
    }
}

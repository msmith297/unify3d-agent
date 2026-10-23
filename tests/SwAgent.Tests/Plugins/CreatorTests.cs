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
using SwAgent.Tools;
using Xunit;
using static SwAgent.Tests.Plugins.RuntimeTestKit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>
    /// Plugin creator v2 (docs/plugins/creator-v2.md): MCP over HTTP (Streamable, SSE answers, the old SSE transport) with a
    /// bearer token from a sensitive option, plugin_try_mcp over stdio and HTTP, check_prerequisites, and the rule that a
    /// secret never reaches the agent, the log or a plugin file. Remote servers are FakeHttpMcp on 127.0.0.1.
    /// </summary>
    [Collection("Plugins")]
    public class CreatorTests
    {
        private const string Secret = "sk-test-7f3a9c1e5b2d";

        private static string Text(ToolResult r) => string.Join("\n", r.Content.Select(c => (string)c["text"]));

        private static string Text(JObject result) => string.Join("\n", ((JArray)result["content"]).Where(c => (string)c["type"] == "text").Select(c => (string)c["text"]));

        /// <summary>A local plugin with a sensitive "token" option and this .mcp.json; the token set as the panel's ask_secret card does.</summary>
        private static PluginManager.LoadedPlugin LocalPlugin(PluginTestEnv env, string name, JObject mcp, bool setToken = true)
        {
            var tools = new PluginTools { Manager = () => env.Pm };
            Assert.False(tools.Create(null, new PluginTools.CreateInput { Name = name, Description = "Remote things" }).IsError);
            var manifest = new JObject
            {
                ["name"] = name,
                ["version"] = "0.1.0",
                ["description"] = "Remote things",
                ["userConfig"] = new JObject { ["token"] = new JObject { ["type"] = "string", ["title"] = "API token", ["sensitive"] = true } },
            };
            Assert.False(tools.WriteFile(null, new PluginTools.WriteInput { Plugin = name, Path = ".claude-plugin/plugin.json", Content = manifest.ToString() }).IsError);
            Assert.False(tools.WriteFile(null, new PluginTools.WriteInput { Plugin = name, Path = ".mcp.json", Content = new JObject { ["mcpServers"] = mcp }.ToString() }).IsError);
            var p = env.Pm.Find(name);
            if (setToken) env.Pm.SetConfig(p.Id, new JObject { ["token"] = Secret }); // plugins.setConfig, as SecretCard calls it
            return env.Pm.Find(name);
        }

        private static JObject HttpServer(string url) => new JObject
        {
            ["type"] = "http",
            ["url"] = url,
            ["headers"] = new JObject { ["Authorization"] = "Bearer ${user_config.token}" },
        };

        /// <summary>No file under the folder holds the secret, in UTF-8 or UTF-16 (the DPAPI file is encrypted).</summary>
        private static void NoSecretIn(string dir)
        {
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(f);
                Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(Secret)), $"The secret is in {f}");
                Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(Secret)), $"The secret is in {f} (UTF-16)");
            }
        }

        private static bool Contains(byte[] hay, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= hay.Length; i++)
            {
                var j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        private static string LogText() => File.Exists(Log.CurrentFile) ? File.ReadAllText(Log.CurrentFile) : "";

        // ---------------------------------------------------------------- MCP over HTTP

        [Fact]
        public async Task A_remote_http_server_with_a_bearer_token_works_end_to_end_and_the_token_stays_secret()
        {
            using (var server = new FakeHttpMcp(FakeHttpMcp.Kind.Json, Secret))
            using (var env = new PluginTestEnv())
            {
                var p = LocalPlugin(env, "remote-things", new JObject { ["remote"] = HttpServer(server.Url) });
                Assert.DoesNotContain(PluginTools.Validate(p), f => f.StartsWith("error"));

                // The trust dialog shows what runs: the URL and which headers go with it, never their values.
                var outcome = env.Pm.Enable(p.Id, true);
                var trust = string.Join("\n", outcome.NeedsTrust["mcp"].Select(x => (string)x));
                Assert.Contains("remote: http " + server.Url + " (sends Authorization)", trust);
                Assert.DoesNotContain(Secret, outcome.ToJson(env.Pm).ToString());

                var runtime = env.Pm.BuildRuntime(env.Pm.Find(p.Id));
                using (var host = Runtime(out _, runtime))
                {
                    host.Start();
                    await host.SyncAsync();
                    await host.Mcp.WhenStarted(TimeSpan.FromSeconds(30));
                    var status = (JObject)host.Mcp.Status().Single();
                    Assert.True((string)status["state"] == "running", (string)status["error"]);
                    Assert.Equal(2, (int)status["toolCount"]);
                    Assert.Contains(host.Mcp.Definitions(), t => (string)t["name"] == "mcp__plugin_remote-things_remote__echo");
                    Assert.Equal("Fake remote server.", (string)((JArray)host.Info()["mcpInstructions"]).Single()["text"]);

                    var echo = await host.CallAsync("mcp__plugin_remote-things_remote__echo", new JObject { ["text"] = "over http ✓" }, "t1", "tu1");
                    Assert.False((bool)echo["isError"]);
                    Assert.Equal("over http ✓", Text(echo));

                    // The key went as a bearer header; the session id and protocol version came back on later requests.
                    var who = Text(await host.CallAsync("mcp__plugin_remote-things_remote__whoami", null, "t1", "tu2"));
                    Assert.Equal("auth=Bearer " + Secret + " session=sess-42", who); // (the fake echoes it on purpose; real servers don't)
                    Assert.Contains(server.Requests, r => r.EndsWith("| sess-42 | " + McpClient.ProtocolVersion));
                    Assert.Contains("notifications/initialized", server.Notifications);

                    Assert.DoesNotContain(Secret, host.Mcp.Status().ToString());
                    Assert.DoesNotContain(Secret, host.Info().ToString());
                }
                Assert.Contains(server.Requests, r => r.StartsWith("DELETE /mcp")); // the session ended politely
                Assert.DoesNotContain(Secret, LogText());
                NoSecretIn(env.Dir);
            }
        }

        [Fact]
        public async Task Sse_answers_and_the_older_sse_transport_both_work()
        {
            using (var sse = new FakeHttpMcp(FakeHttpMcp.Kind.Sse))
            {
                var c = new McpHttpClient("sse-answers", sse.Url, null);
                using (c)
                {
                    await c.StartAsync();
                    Assert.False(c.Legacy);
                    Assert.Equal("fake-remote", (string)c.ServerInfo["name"]);
                    var tools = await c.ListToolsAsync(default);
                    Assert.Equal(new[] { "echo", "whoami" }, tools.Select(t => (string)t["name"]));
                    var r = await c.CallToolAsync("echo", new JObject { ["text"] = "streamed" }, TimeSpan.FromSeconds(10), default);
                    Assert.Equal("streamed", (string)r["content"][0]["text"]);
                    // The server's ping on the stream was answered.
                    Assert.True(await Until(() => sse.Replies.Any(x => (string)x["id"] == "srv-ping" && x["result"] != null), TimeSpan.FromSeconds(5)));
                }
            }

            using (var legacy = new FakeHttpMcp(FakeHttpMcp.Kind.Legacy))
            {
                var c = new McpHttpClient("old-sse", legacy.Url, null);
                using (c)
                {
                    await c.StartAsync();
                    Assert.True(c.Legacy); // POST /mcp was 405: fell back to GET + endpoint event
                    var tools = await c.ListToolsAsync(default);
                    Assert.Equal(2, tools.Count);
                    var r = await c.CallToolAsync("echo", new JObject { ["text"] = "old" }, TimeSpan.FromSeconds(10), default);
                    Assert.Equal("old", (string)r["content"][0]["text"]);
                    Assert.Contains(legacy.Requests, x => x.StartsWith("POST /messages?session=abc"));
                }
                var closed = false;
                var gone = new McpHttpClient("gone", legacy.Url, null, sseOnly: true);
                gone.Closed += _ => closed = true;
                await gone.StartAsync();
                legacy.Dispose(); // the server goes away: its event stream ends
                Assert.True(await Until(() => closed, TimeSpan.FromSeconds(10)));
                gone.Dispose();
            }
        }

        [Fact]
        public async Task Without_a_key_a_401_says_oauth_isnt_supported_and_a_wrong_key_is_refused()
        {
            using (var server = new FakeHttpMcp(FakeHttpMcp.Kind.Json, Secret))
            {
                var none = await Assert.ThrowsAnyAsync<McpException>(() => new McpHttpClient("x", server.Url, null).StartAsync());
                Assert.Contains("needs OAuth - not supported yet", none.Message);
                var wrong = await Assert.ThrowsAnyAsync<McpException>(() => new McpHttpClient("x", server.Url, new Dictionary<string, string> { ["Authorization"] = "Bearer nope" }).StartAsync());
                Assert.Contains("refused the request (401", wrong.Message);
                Assert.DoesNotContain("nope", wrong.Message);
            }
            Assert.Throws<McpException>(() => new McpHttpClient("x", "ftp://example.com/mcp", null));
            Assert.Equal("https://example.com:8443/mcp", McpHttpClient.SafeUrl("https://user:pw@example.com:8443/mcp?api_key=SECRET"));
        }

        [Fact]
        public async Task Server_sent_events_are_parsed_like_a_browser_does()
        {
            var events = new List<(string, string)>();
            var text = ": hello\n\nevent: endpoint\ndata: /m?x=1\n\ndata: {\"a\":\ndata: 1}\n\nevent: last\ndata: tail";
            await McpHttpClient.ReadEventsAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), (e, d) =>
            {
                events.Add((e, d));
                return true;
            });
            Assert.Equal(new[] { ("endpoint", "/m?x=1"), ("message", "{\"a\":\n1}"), ("last", "tail") }, events);
        }

        // ---------------------------------------------------------------- plugin_try_mcp

        [NodeFact]
        public async Task Try_mcp_starts_a_stdio_server_lists_its_tools_calls_a_read_only_one_and_stops_it()
        {
            using (var env = new PluginTestEnv())
            {
                var fixture = Path.Combine(FixtureDir, "mcp-echo.mjs");
                var p = LocalPlugin(env, "echo-plugin", new JObject
                {
                    ["echo"] = new JObject { ["command"] = "node", ["args"] = new JArray("${CLAUDE_PLUGIN_ROOT}/server.mjs"), ["env"] = new JObject { ["TOKEN"] = "${user_config.token}" } },
                });
                File.Copy(fixture, Path.Combine(p.Root, "server.mjs"));
                var tools = new CreatorTools { Manager = () => env.Pm };

                var r = await tools.TryMcp(null, new CreatorTools.TryInput { Plugin = "echo-plugin", Call = "echo", Arguments = new JObject { ["text"] = "tried" } });
                var text = Text(r);
                Assert.False(r.IsError, text);
                Assert.Contains("Server \"echo\": node ${CLAUDE_PLUGIN_ROOT}/server.mjs", text); // as written: no expanded values
                Assert.Contains("Started in", text);
                Assert.Contains("echo 1.0.0", text);
                Assert.Contains("7 tool(s):", text);
                Assert.Contains("- echo [read-only]: Echoes its text.", text);
                Assert.Contains("inputs: text (string)", text);
                Assert.Contains("Called echo:\r\ntried", text);
                Assert.Contains("Stopped.", text);
                // Not registered: the agent's tools don't change, and nothing is left running.
                Assert.Empty(env.Pm.Runnable());

                // A tool that may change something needs the user's OK first.
                var fail = Text(await tools.TryMcp(null, new CreatorTools.TryInput { Plugin = "echo-plugin", Call = "fail" }));
                Assert.Contains("Not called: fail isn't marked read-only", fail);
                var ok = Text(await tools.TryMcp(null, new CreatorTools.TryInput { Plugin = "echo-plugin", Call = "fail", UserOk = true }));
                Assert.Contains("Called fail - it returned an error:", ok);

                // A broken command line fails with what to do, and after 5 failed tries says to stop.
                new PluginTools { Manager = () => env.Pm }.WriteFile(null, new PluginTools.WriteInput
                {
                    Plugin = "echo-plugin", Path = ".mcp.json",
                    Content = new JObject { ["mcpServers"] = new JObject { ["echo"] = new JObject { ["command"] = "node", ["args"] = new JArray("${CLAUDE_PLUGIN_ROOT}/missing.mjs") } } }.ToString(),
                });
                ToolResult broken = null;
                for (var i = 1; i <= CreatorTools.MaxTries; i++)
                {
                    broken = await tools.TryMcp(null, new CreatorTools.TryInput { Plugin = "echo-plugin" });
                    Assert.True(broken.IsError);
                    if (i < CreatorTools.MaxTries) Assert.Contains($"Failed try {i} of 5.", Text(broken));
                }
                Assert.Contains("Failed after", Text(broken));
                Assert.Contains("Its error output (last lines):", Text(broken)); // node: Cannot find module ...missing.mjs
                Assert.Contains("stop changing it and tell the user what fails", Text(broken));
                Assert.DoesNotContain(Secret, LogText());
            }
        }

        [Fact]
        public async Task Try_mcp_over_http_hides_the_secret_even_when_the_server_prints_it()
        {
            using (var server = new FakeHttpMcp(FakeHttpMcp.Kind.Sse, Secret))
            using (var env = new PluginTestEnv())
            {
                LocalPlugin(env, "remote-try", new JObject { ["remote"] = HttpServer(server.Url) });
                var tools = new CreatorTools { Manager = () => env.Pm };
                var r = await tools.TryMcp(null, new CreatorTools.TryInput { Plugin = "remote-try", Call = "whoami", UserOk = true });
                var text = Text(r);
                Assert.False(r.IsError, text);
                Assert.Contains("Server \"remote\": http " + server.Url, text);
                Assert.Contains("fake-remote 2.0.0", text);
                Assert.Contains("auth=Bearer [hidden]", text);
                Assert.DoesNotContain(Secret, text);
                Assert.DoesNotContain(Secret, LogText());

                // No key entered yet: the try says so, and the server's refusal explains itself.
                LocalPlugin(env, "remote-unset", new JObject { ["remote"] = HttpServer(server.Url) }, setToken: false);
                var unset = Text(await tools.TryMcp(null, new CreatorTools.TryInput { Plugin = "remote-unset" }));
                Assert.Contains("token isn't set yet; ask the user with ask_secret first", unset);
                Assert.Contains("refused the request (401", unset);
                NoSecretIn(env.Dir);
            }
        }

        [Fact]
        public async Task Try_mcp_refuses_unknown_servers_and_plugins_without_servers()
        {
            using (var env = new PluginTestEnv())
            {
                var tools = new CreatorTools { Manager = () => env.Pm };
                LocalPlugin(env, "no-servers", new JObject(), setToken: false);
                Assert.Contains("no MCP servers yet", Text(await tools.TryMcp(null, new CreatorTools.TryInput { Plugin = "no-servers" })));
                LocalPlugin(env, "one-server", new JObject { ["a"] = HttpServer("http://127.0.0.1:9/mcp") }, setToken: false);
                Assert.Contains("has no MCP server 'b'", Text(await tools.TryMcp(null, new CreatorTools.TryInput { Plugin = "one-server", Server = "b" })));
                await Assert.ThrowsAsync<ToolException>(() => tools.TryMcp(null, new CreatorTools.TryInput { Plugin = "nothing-here" }));
                PluginSettings.Apply(new JObject { ["pluginMcp"] = false });
                Assert.Contains("turned off", Text(await tools.TryMcp(null, new CreatorTools.TryInput { Plugin = "one-server" })));
            }
        }

        [Fact]
        public void Validation_knows_http_servers_and_warns_about_secrets_in_urls()
        {
            using (var env = new PluginTestEnv())
            {
                var p = LocalPlugin(env, "url-key", new JObject
                {
                    ["inurl"] = new JObject { ["type"] = "http", ["url"] = "https://example.invalid/mcp?key=${user_config.token}" },
                    ["socket"] = new JObject { ["type"] = "ws", ["url"] = "wss://example.invalid/mcp" },
                    ["badheaders"] = new JObject { ["type"] = "sse", ["url"] = "https://example.invalid/sse", ["headers"] = "Authorization: x" },
                }, setToken: false);
                var findings = PluginTools.Validate(p);
                Assert.Contains(findings, f => f.StartsWith("warning: MCP server \"inurl\" puts a sensitive option in its url"));
                Assert.Contains(findings, f => f.StartsWith("error:") && f.Contains("type \"ws\" (use stdio, http or sse)"));
                Assert.Contains(findings, f => f.StartsWith("error:") && f.Contains("\"badheaders\": headers must be an object"));
            }
        }

        // ---------------------------------------------------------------- check_prerequisites

        [Fact]
        public async Task Check_prerequisites_reports_versions_and_install_lines_and_installs_nothing()
        {
            var found = new Dictionary<string, string>
            {
                ["node"] = @"C:\Program Files\nodejs\node.exe",
                ["python"] = @"C:\Users\u\AppData\Local\Microsoft\WindowsApps\python.exe", // the Store shortcut
                ["py"] = @"C:\Windows\py.exe",
                ["git"] = @"C:\Program Files\Git\cmd\git.exe",
            };
            var output = new Dictionary<string, string>
            {
                [found["node"]] = "v20.11.0\n",
                [found["python"]] = "",
                [found["py"]] = "Python 3.8.10\n",
                [found["git"]] = "git version 2.45.1.windows.1\n",
            };
            CreatorTools.FindOverride = n => found.TryGetValue(n, out var f) ? f : null;
            CreatorTools.VersionOverride = path => output[path];
            try
            {
                var r = await new CreatorTools().CheckPrerequisites(null, new CreatorTools.PrerequisitesInput { Programs = new[] { "node>=18", "python>=3.10", "uv", "git", "node>=22", "foo" } });
                var text = Text(r);
                Assert.Contains(@"- node: 20.11.0 (C:\Program Files\nodejs\node.exe) - OK, needs 18 or newer", text);
                Assert.Contains(@"- python: 3.8.10 (C:\Windows\py.exe) - older than 3.10. To update: winget install Python.Python.3.12", text);
                Assert.Contains("- uv: not found. To install: winget install astral-sh.uv", text);
                Assert.Contains("- git: 2.45.1 (", text);
                Assert.Contains("- node: 20.11.0 (C:\\Program Files\\nodejs\\node.exe) - older than 22.", text);
                Assert.Contains("- foo: not found. To install: install foo and make sure it is on the PATH", text);
                Assert.Contains("Missing or too old: python, uv, node, foo. Nothing was installed", text);

                found.Remove("py");
                Assert.Contains("python: not found (only the Microsoft Store shortcut)", CreatorTools.Check("python").Line);
                Assert.Contains("write it as a program name", CreatorTools.Check("node >= x").Line);
            }
            finally
            {
                CreatorTools.FindOverride = null;
                CreatorTools.VersionOverride = null;
            }
            Assert.Equal(new Version(3, 12, 1), CreatorTools.ParseVersion("Python 3.12.1"));
            Assert.Equal(new Version(27, 0, 3), CreatorTools.ParseVersion("Docker version 27.0.3, build 7d4bcd8"));
            Assert.Equal(new Version(0, 4, 0), CreatorTools.ParseVersion("uv 0.4.0 (a1b2c3 2024-08-01)"));
            Assert.Null(CreatorTools.ParseVersion("command not found"));
        }

        [NodeFact]
        public void Check_prerequisites_finds_the_real_node()
        {
            var r = CreatorTools.Check("node>=18");
            Assert.True(r.Ok, r.Line);
            Assert.True(r.Version.Major >= 18);
            Assert.EndsWith("node.exe", r.Path, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_registry_offers_the_creator_tools()
        {
            var registry = AgentAddIn.BuildTools(out _);
            var defs = registry.Definitions().ToDictionary(d => (string)d["name"]);
            Assert.NotNull(defs["check_prerequisites"]["input_schema"]["properties"]["programs"]);
            Assert.NotNull(defs["plugin_try_mcp"]["input_schema"]["properties"]["user_ok"]);
            Assert.True(SwBusyGate.ModelFree("check_prerequisites"));
            Assert.True(SwBusyGate.ModelFree("plugin_try_mcp"));
        }
    }
}

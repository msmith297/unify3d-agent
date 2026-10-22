using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using SwAgent.Plugins.Hooks;
using SwAgent.Scripting;
using SwAgent.Tools;
using Xunit;
using static SwAgent.Tests.Plugins.RuntimeTestKit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>Runs only where node and Git for Windows (Git Bash) are installed.</summary>
    public sealed class GitBashFactAttribute : FactAttribute
    {
        public GitBashFactAttribute()
        {
            if (RuntimeTestKit.Node == null) Skip = "node isn't installed.";
            else if (HookShell.Bash == null) Skip = "Git for Windows (Git Bash) isn't installed.";
        }
    }

    /// <summary>
    /// Regressions from the plugins review (2026-10-21): tool code needs approval, command and skill text never reads the
    /// process environment, hooks run in Git Bash as in Claude Code, typed arguments aren't expanded twice, argument hints
    /// keep their brackets, a dying MCP server reports its last words, SessionStart notices stay out of the prompt, and a
    /// panel selection box adds to its list as a PropertyManager box does.
    /// </summary>
    [Collection("Plugins")]
    public class PluginReviewTests
    {
        [Fact]
        public void A_plugin_with_tool_code_needs_approval_and_its_code_tools_wait_for_it()
        {
            using (var env = new PluginTestEnv())
            {
                var src = env.AddPlugin("toolsy");
                PluginTestEnv.AddTool(src, "carve");
                PluginTestEnv.Write(Path.Combine(src, "tools", "carve", "macro.cs"), "// cuts a groove\n");
                PluginTestEnv.AddTool(src, "notes", "workflow"); // no C#: nothing to approve
                env.Pm.AddMarketplace(env.Market);

                // A tools-only plugin used to install at once: its macro.cs runs inside SolidWorks.
                var ask = env.Pm.Install("toolsy@test-market");
                Assert.False(ask.Ok);
                Assert.NotNull(ask.NeedsTrust);
                Assert.Equal(new[] { "toolsy:carve (macro.cs)" }, ((JArray)ask.NeedsTrust["tools"]).Select(t => (string)t));
                Assert.Null(env.Pm.Find("toolsy"));

                Assert.True(env.Pm.Install("toolsy@test-market", ask.Hash).Ok);
                Assert.NotNull(env.Pm.ToolDir("toolsy", "carve"));
                Assert.NotNull(UserTools.DirOf("toolsy:carve"));

                // Code that changed since the approval (as a bad update would bring) isn't run: the tool isn't found any more
                // (ribbon, panel form, run_tool_macro and test_tool all look it up there); the tool without code still is.
                File.AppendAllText(Path.Combine(env.Pm.Find("toolsy").Root, "tools", "carve", "macro.cs"), "// and something else\n");
                Assert.False(env.Pm.IsTrusted(env.Pm.Find("toolsy")));
                Assert.Null(env.Pm.ToolDir("toolsy", "carve"));
                Assert.Null(UserTools.DirOf("toolsy:carve"));
                Assert.NotNull(env.Pm.ToolDir("toolsy", "notes"));
                Assert.DoesNotContain(UserTools.ListTools(), t => (string)t["name"] == "toolsy:carve");
            }
        }

        [Fact]
        public void Approvals_of_plugins_without_tool_code_still_hold()
        {
            var dir = TempDir();
            try
            {
                PluginTestEnv.WriteJson(Path.Combine(dir, ".claude-plugin", "plugin.json"), new JObject { ["name"] = "hooked" });
                PluginTestEnv.Write(Path.Combine(dir, "hooks", "hooks.json"), PluginTestEnv.Hooks("node \"${CLAUDE_PLUGIN_ROOT}/hook.mjs\""));
                PluginTestEnv.AddTool(dir, "plain", "workflow");
                var m = PluginManifest.Load(dir);
                Assert.False(m.HasToolCode);
                Assert.Equal(m.TrustHash(null, toolCode: false), m.TrustHash());
                PluginTestEnv.Write(Path.Combine(dir, "tools", "plain", "preview.cs"), "// draws\n");
                Assert.True(m.HasToolCode);
                Assert.NotEqual(m.TrustHash(null, toolCode: false), m.TrustHash());
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Command_and_skill_text_never_reads_the_process_environment()
        {
            Environment.SetEnvironmentVariable("SWAGENT_FAKE_SECRET_TOKEN", "s3cr3t-value");
            try
            {
                using (var env = new PluginTestEnv())
                {
                    var src = env.AddPlugin("leaky");
                    const string body = "token=${SWAGENT_FAKE_SECRET_TOKEN} auth=${ANTHROPIC_AUTH_TOKEN} user=${USERNAME:-x} root=${CLAUDE_PLUGIN_ROOT}";
                    PluginTestEnv.Write(Path.Combine(src, "commands", "show.md"), "---\ndescription: shows\n---\n" + body + "\n");
                    PluginTestEnv.Write(Path.Combine(src, "skills", "peek", "SKILL.md"), "---\nname: peek\ndescription: peeks\n---\n" + body + "\n");
                    env.Pm.AddMarketplace(env.Market);
                    Assert.True(env.Pm.Install("leaky@test-market").Ok);
                    var root = env.Pm.Find("leaky").Root;

                    var text = (string)CommandIndex.Expand(env.Pm, "leaky:show", "")["text"];
                    Assert.Equal($"token=${{SWAGENT_FAKE_SECRET_TOKEN}} auth= user=${{USERNAME:-x}} root={root}", text);
                    var skill = env.Pm.BuildRuntime(env.Pm.Find("leaky")).Expand(body);
                    Assert.DoesNotContain("s3cr3t", skill);

                    // The approved runtime (hook and MCP command lines) still reads its program's own environment, never a secret.
                    var rt = env.Pm.BuildRuntime(env.Pm.Find("leaky"));
                    var childEnv = Session.ForTests(Guid.NewGuid().ToString()).EnvFor(rt);
                    Assert.Equal("s3cr3t-value", Session.Expand(rt, "${SWAGENT_FAKE_SECRET_TOKEN}", childEnv));
                    Assert.Equal("fallback", Session.Expand(rt, "${SWAGENT_SURELY_UNSET_VAR:-fallback}", childEnv));
                    Assert.Equal("", Session.Expand(rt, "${ANTHROPIC_AUTH_TOKEN}", childEnv));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("SWAGENT_FAKE_SECRET_TOKEN", null);
            }
        }

        [Fact]
        public void Typed_arguments_go_in_once_and_are_never_expanded_again()
        {
            var notes = new List<string>();
            var root = Path.GetTempPath();
            Assert.Equal("Explain this: why does $1 cost more than $2", CommandIndex.ExpandBody("Explain this: $ARGUMENTS", "why does $1 cost more than $2", root, null, notes));
            Assert.Equal("A=$2 B=$ARGUMENTS C=", CommandIndex.ExpandBody("A=$1 B=$2 C=$3", "$2 $ARGUMENTS", root, null, notes));
            Assert.Equal("Quote $25 and $30", CommandIndex.ExpandBody("Quote $ARGUMENTS", "$25 and $30", root, null, notes));
        }

        [Fact]
        public void Argument_hints_keep_their_brackets()
        {
            var fm = FrontMatter.Parse("---\nargument-hint: [pr-number] [priority] [assignee]\ndescription: Review a PR\n---\nbody");
            Assert.Equal("[pr-number] [priority] [assignee]", fm.Text("argument-hint"));
            Assert.Equal("[pr-number] [priority] [assignee]", fm.Get("argument-hint")); // not a list: its brackets don't pair up
            var one = FrontMatter.Parse("---\nargument-hint: [message]\ntools: [Read, Grep]\n---\n");
            Assert.Equal("[message]", one.Text("argument-hint"));
            Assert.Equal(new[] { "Read", "Grep" }, one.List("tools"));

            var dir = TempDir();
            try
            {
                PluginTestEnv.WriteJson(Path.Combine(dir, ".claude-plugin", "plugin.json"), new JObject { ["name"] = "hints" });
                PluginTestEnv.Write(Path.Combine(dir, "commands", "commit.md"), "---\ndescription: Commit\nargument-hint: [message]\n---\nCommit with $ARGUMENTS\n");
                var json = CommandIndex.For(PluginManifest.Load(dir)).Single().ToJson();
                Assert.Equal("[message]", (string)json["argumentHint"]);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [NodeFact]
        public async Task A_server_that_dies_reports_its_last_words_not_its_first()
        {
            const string script = "for (let i = 0; i < 3000; i++) console.error('heartbeat failed: fetch failed (' + i + ')'); console.error('FATAL: the real reason it died'); process.exitCode = 3;";
            using (var p = PluginProcess.StartProgram("node", new[] { "-e", script }, ChildEnv.Base(), Path.GetTempPath(), captureStdout: true))
            {
                p.CloseInput();
                Assert.True(await Until(() => p.Exited.IsCompleted, TimeSpan.FromSeconds(60)));
                await p.DrainAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(3, p.ExitCode);
                Assert.Contains("FATAL: the real reason it died", p.Stderr);
                Assert.Contains("(2999)", p.Stderr);
                Assert.True(p.Stderr.Length <= 1 << 16);
            }
        }

        [NodeFact]
        public async Task SessionStart_notices_are_for_the_user_not_the_model()
        {
            var plugin = Plugin("noticed", Hooks(HookEvents.SessionStart, Group("startup", Cmd(Hook("system")), Cmd(Hook("context")))));
            using (var host = Runtime(out _, plugin))
            {
                host.Start();
                await host.SyncAsync();
                host.Info(); // the panel is ready: SessionStart fires
                Assert.True(await Until(() => ((JArray)host.Info()["sessionNotices"]).Count == 1, TimeSpan.FromSeconds(60)));
                Assert.Equal("a notice for the user", (string)host.Info()["sessionNotices"][0]);
                Assert.Equal(new[] { "context from SessionStart" }, ((JArray)host.Info()["sessionContext"]).Select(c => (string)c));
            }
        }

        // ---- the hook shell ----

        private static async Task<(int? Code, string Stdout, string Stderr)> RunHook(string command, IDictionary<string, string> env, string bash)
        {
            using (var p = PluginProcess.StartShell(command, env, Path.GetTempPath(), bash, 1 << 20))
            {
                p.WriteInputAndClose(new JObject { ["hook_event_name"] = "UserPromptSubmit" }.ToString());
                Assert.True(await Until(() => p.Exited.IsCompleted, TimeSpan.FromSeconds(60)));
                await p.DrainAsync(TimeSpan.FromSeconds(10));
                return (p.ExitCode, p.Stdout, p.Stderr);
            }
        }

        private static Dictionary<string, string> HookEnv()
        {
            var env = ChildEnv.Base();
            env["CLAUDE_PLUGIN_ROOT"] = FixtureDir;
            return env;
        }

        [GitBashFact]
        public async Task Hooks_written_for_Claude_Codes_Git_Bash_run_unchanged()
        {
            var bash = HookShell.Bash;
            Assert.EndsWith(Path.Combine("usr", "bin", "bash.exe"), bash, StringComparison.OrdinalIgnoreCase);
            // $NAME without braces and 2>/dev/null: cmd.exe failed both (exit 1, the hook silently did nothing).
            var r = await RunHook("node \"$CLAUDE_PLUGIN_ROOT/hooks/hook.mjs\" context 2>/dev/null", HookEnv(), bash);
            Assert.Equal(0, r.Code);
            Assert.Contains("context from UserPromptSubmit", r.Stdout);
            // ${VAR:-x}, single quotes, and Git's tools on PATH.
            r = await RunHook("echo \"${SWAGENT_SURELY_UNSET_VAR:-fallback}\" '${not expanded}' | tr a-z A-Z", HookEnv(), bash);
            Assert.Equal(0, r.Code);
            Assert.Equal("FALLBACK ${NOT EXPANDED}", r.Stdout.Trim());
        }

        [GitBashFact]
        public async Task A_bash_hook_runs_one_process_below_its_host_as_with_cmd()
        {
            var dir = TempDir();
            var file = Path.Combine(dir, "ppid.txt");
            try
            {
                using (var p = PluginProcess.StartShell($"node \"${{CLAUDE_PLUGIN_ROOT}}/hooks/hook.mjs\" ppid \"{file}\"", HookEnv(), Path.GetTempPath(), HookShell.Bash, 1 << 20))
                {
                    p.WriteInputAndClose("{}");
                    Assert.True(await Until(() => File.Exists(file) && File.ReadAllText(file).Contains(" "), TimeSpan.FromSeconds(30)));
                    var pids = File.ReadAllText(file).Split(' ').Select(int.Parse).ToArray();
                    // node's parent is the shell, whose parent is the host (SolidWorks): agent-bridge's hook.mjs finds its MCP
                    // peer among the hook's parent and grandparent, so Git's launcher (bin\bash.exe) must not sit in between.
                    Assert.Equal(p.Id, pids[1]);
                    Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, ParentOf(pids[1]));
                    p.Kill();
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [NodeFact]
        public async Task Without_Git_Bash_hooks_run_with_cmd_and_expanded_placeholders()
        {
            var env = HookEnv();
            var r = await RunHook(ChildEnv.Expand("node \"${CLAUDE_PLUGIN_ROOT}/hooks/hook.mjs\" context", env), env, null);
            Assert.Equal(0, r.Code);
            Assert.Contains("context from UserPromptSubmit", r.Stdout);
            Assert.Equal("x fallback", ChildEnv.Expand("x ${SWAGENT_SURELY_UNSET:-fallback}", env));
        }

        [Fact]
        public void Validate_points_out_hook_commands_only_bash_runs()
        {
            // agent-bridge's form works with both shells.
            Assert.Empty(PluginTools.BashOnly("node \"${CLAUDE_PLUGIN_ROOT}/client/hook.mjs\" --listen"));
            Assert.Equal(new[] { "$NAME without braces", "/dev/null" }, PluginTools.BashOnly("node \"$CLAUDE_PLUGIN_ROOT/h.mjs\" 2>/dev/null"));
            Assert.Contains("a .sh script", PluginTools.BashOnly("bash \"${CLAUDE_PLUGIN_ROOT}/hooks/run.sh\""));
            Assert.Contains("${NAME:-default}", PluginTools.BashOnly("node x.mjs ${MODE:-fast}"));
        }

        [Fact]
        public void Bash_hook_commands_get_only_user_config_put_in()
        {
            var options = new Dictionary<string, (string Value, bool Sensitive)> { ["region"] = ("eu", false), ["token"] = ("s3cret", true) };
            Assert.Equal("node \"${CLAUDE_PLUGIN_ROOT}/x.mjs\" --region eu --token  $HOME",
                PluginManager.ExpandOptions("node \"${CLAUDE_PLUGIN_ROOT}/x.mjs\" --region ${user_config.region} --token ${user_config.token} $HOME", options));
        }

        // ---- the panel's selection boxes ----

        private static ToolInputs.SelectedItem Item(string reference, object entity = null) => new ToolInputs.SelectedItem { Ref = reference, Entity = entity, Type = "Face" };

        [Fact]
        public void A_following_selection_box_adds_like_a_PropertyManager_box()
        {
            var held = new List<ToolInputs.SelectedItem> { Item("A"), Item("B") };
            // A plain click on another face adds it; faces already in the box aren't listed twice.
            Assert.Equal(new[] { "A", "B", "C" }, ToolFormSession.Merge(held, new[] { Item("B"), Item("C") }, multiple: true).Select(i => i.Ref));
            // Clicking empty space (the selection cleared) or an entity the box doesn't take keeps the list.
            Assert.Equal(new[] { "A", "B" }, ToolFormSession.Merge(held, new ToolInputs.SelectedItem[0], multiple: true).Select(i => i.Ref));
            // A box for one entity takes the newest.
            Assert.Equal(new[] { "C" }, ToolFormSession.Merge(new[] { Item("A") }, new[] { Item("A"), Item("C") }, multiple: false).Select(i => i.Ref));
            // Without persistent references the same object counts as the same entity.
            var face = new object();
            Assert.Single(ToolFormSession.Merge(new[] { Item(null, face) }, new[] { Item(null, face) }, multiple: true));
        }

        [Fact]
        public void The_panel_and_the_add_in_match_hook_matchers_alike()
        {
            // ui/src/lib/checks/lib.check.mjs checks matcherMatches on the same names.
            const string send = "mcp__plugin_agent-bridge_agent-bridge__send_message";
            Assert.True(HookEvents.Matches("mcp__plugin_agent-bridge", send));
            Assert.True(HookEvents.Matches("mcp__.*__send", send));
            Assert.False(HookEvents.Matches("^mcp__.*__send$", send));
            Assert.False(HookEvents.Matches("execute", "execute_csharp"));
            Assert.False(HookEvents.Matches("(", "("));
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Plugins;
using SwAgent.Plugins.Hooks;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>Runs only when node is installed (the fake hooks and MCP server are node scripts).</summary>
    public sealed class NodeFactAttribute : FactAttribute
    {
        public NodeFactAttribute()
        {
            if (RuntimeTestKit.Node == null) Skip = "node isn't installed; the plugin runtime tests need it.";
        }
    }

    internal static class RuntimeTestKit
    {
        public static readonly string Node = ChildEnv.Resolve("node", ChildEnv.FreshPath(), null);

        /// <summary>tests/fixtures as copied next to the test assembly.</summary>
        public static string FixtureDir => Path.Combine(Path.GetDirectoryName(new Uri(typeof(RuntimeTestKit).Assembly.CodeBase).LocalPath), "fixtures");

        public static string HookScript => Path.Combine(FixtureDir, "hooks", "hook.mjs");

        public static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "u3d-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>A hook command that runs the fake hook in one mode, the way plugins write them.</summary>
        public static string Hook(string mode, string arg = null) =>
            $"node \"${{CLAUDE_PLUGIN_ROOT}}/hooks/hook.mjs\" {mode}" + (arg == null ? "" : $" \"{arg}\"");

        public static JObject Group(string matcher, params JObject[] hooks)
        {
            var g = new JObject { ["hooks"] = new JArray(hooks) };
            if (matcher != null) g["matcher"] = matcher;
            return g;
        }

        public static JObject Cmd(string command, int? timeout = null, bool async = false, bool rewake = false, string rewakeMessage = null)
        {
            var h = new JObject { ["type"] = "command", ["command"] = command };
            if (timeout != null) h["timeout"] = timeout;
            if (async) h["async"] = true;
            if (rewake) h["asyncRewake"] = true;
            if (rewakeMessage != null) h["rewakeMessage"] = rewakeMessage;
            return h;
        }

        /// <summary>A runnable plugin rooted at tests/fixtures, with these hooks ({ Event: [groups] }) and MCP servers.</summary>
        public static PluginRuntime Plugin(string name, JObject hooks = null, IDictionary<string, JObject> mcp = null, IDictionary<string, string> env = null) => new PluginRuntime
        {
            Id = name + "@test",
            Name = name,
            Root = FixtureDir,
            DataDir = Path.Combine(Path.GetTempPath(), "u3d-plugin-data", name),
            Env = env ?? new Dictionary<string, string>(),
            Hooks = hooks,
            McpServers = mcp,
            TrustHash = "test",
        };

        public static JObject Hooks(string ev, params JObject[] groups) => new JObject { [ev] = new JArray(groups) };

        /// <summary>A runtime over fixed plugins, with a session of its own and its events recorded.</summary>
        public static RuntimeHost Runtime(out ConcurrentQueue<(string name, JToken data)> events, params PluginRuntime[] plugins)
        {
            var queue = new ConcurrentQueue<(string, JToken)>();
            events = queue;
            var host = new RuntimeHost(new FixedSource(plugins), Session.ForTests(Guid.NewGuid().ToString()),
                (n, d) => queue.Enqueue((n, d == null ? null : JToken.FromObject(d))))
            {
                TerminateJobOnShutdown = false, // tests share the add-in job
                McpJsonPath = Path.Combine(TempDir(), "mcp.json"), // never the shared test data folder's
            };
            host.SyncAsync().GetAwaiter().GetResult(); // the plugins are read before the first event
            return host;
        }

        public sealed class FixedSource : IPluginRuntimeSource
        {
            private IReadOnlyList<PluginRuntime> _plugins;

            public FixedSource(IReadOnlyList<PluginRuntime> plugins)
            {
                _plugins = plugins;
            }

            public IReadOnlyList<PluginRuntime> Runnable() => _plugins;

            public event Action Changed;

            public void Set(IReadOnlyList<PluginRuntime> plugins)
            {
                _plugins = plugins;
                Changed?.Invoke();
            }
        }

        public static async Task<bool> Until(Func<bool> condition, TimeSpan timeout)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed > timeout) return false;
                await Task.Delay(50);
            }
            return true;
        }

        public static bool Alive(int pid)
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                {
                    return !p.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>The parent pid of a running process (NtQueryInformationProcess), or 0.</summary>
        public static int ParentOf(int pid)
        {
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid))
                {
                    var info = new ProcessBasicInformation();
                    return NtQueryInformationProcess(p.Handle, 0, ref info, Marshal.SizeOf(info), out _) == 0 ? info.InheritedFromUniqueProcessId.ToInt32() : 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr ExitStatus;
            public IntPtr PebBaseAddress;
            public IntPtr AffinityMask;
            public IntPtr BasePriority;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);
    }
}

using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Plugins;
using Xunit;

namespace SwAgent.Tests.Plugins
{
    /// <summary>
    /// Plugin tests share PluginManager.Instance, the user's tools folder and PluginSettings: they run one at a time, after the
    /// parallel tests.
    /// </summary>
    [CollectionDefinition("Plugins", DisableParallelization = true)]
    public class PluginsCollection
    {
    }

    /// <summary>
    /// A throw-away plugins folder and marketplace under the test data folder (SWAGENT_DATA_DIR), never the user's. The
    /// manager is also PluginManager.Instance while the test runs (the tool lookup uses it); Node is faked as v20.
    /// </summary>
    internal sealed class PluginTestEnv : IDisposable
    {
        public readonly string Dir;
        public readonly string Market;
        public readonly PluginManager Pm;
        private readonly PluginManager _previous;

        public PluginTestEnv(string marketName = "test-market", bool makeInstance = true)
        {
            Dir = Path.Combine(Paths.LocalDir, "plugin-tests", Guid.NewGuid().ToString("N").Substring(0, 10));
            Market = Path.Combine(Dir, "market");
            Directory.CreateDirectory(Market);
            WriteJson(Path.Combine(Market, ".claude-plugin", "marketplace.json"), new JObject { ["name"] = marketName, ["owner"] = new JObject { ["name"] = "Tests" }, ["plugins"] = new JArray() });
            Pm = new PluginManager(Path.Combine(Dir, "plugins"), Path.Combine(Dir, "builtin"));
            ExecutableResolver.NodeVersionOverride = () => new Version(20, 11, 0);
            Fetcher.GitOverride = null;
            if (makeInstance)
            {
                _previous = PluginManager.Instance;
                PluginManager.Instance = Pm;
            }
        }

        /// <summary>A plugin folder inside the marketplace, listed in its marketplace.json; returns the folder.</summary>
        public string AddPlugin(string name, JObject manifest = null, JObject entryExtra = null)
        {
            var dir = Path.Combine(Market, name);
            WriteJson(Path.Combine(dir, ".claude-plugin", "plugin.json"), manifest ?? new JObject { ["name"] = name, ["version"] = "1.0.0", ["description"] = name + " plugin" });
            var file = Path.Combine(Market, ".claude-plugin", "marketplace.json");
            var market = JObject.Parse(File.ReadAllText(file));
            var entry = new JObject { ["name"] = name, ["source"] = "./" + name, ["description"] = name + " plugin" };
            if (entryExtra != null) entry.Merge(entryExtra);
            ((JArray)market["plugins"]).Add(entry);
            WriteJson(file, market);
            return dir;
        }

        public static void WriteJson(string path, JToken json) => Write(path, json.ToString());

        public static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>A tool folder (TOOL.md) inside a plugin.</summary>
        public static void AddTool(string pluginDir, string tool, string kind = "tool") =>
            Write(Path.Combine(pluginDir, "tools", tool, "TOOL.md"), $"---\nname: {tool}\ntitle: {tool} title\ndescription: does {tool}\nkind: {kind}\nicon: gear\n---\n\nDo {tool}.\n");

        public static string Hooks(string command, bool asyncRewake = false) => new JObject
        {
            ["hooks"] = new JObject
            {
                ["Stop"] = new JArray(new JObject
                {
                    ["hooks"] = new JArray(asyncRewake
                        ? new JObject { ["type"] = "command", ["command"] = command, ["async"] = true, ["asyncRewake"] = true, ["timeout"] = 600 }
                        : new JObject { ["type"] = "command", ["command"] = command }),
                }),
            },
        }.ToString();

        /// <summary>The agent-bridge fixture (its real marketplace.json, plugin.json, hooks.json and skills) next to the test dll.</summary>
        public static string AgentBridgeFixture => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "Fixtures", "agent-bridge");

        public void Dispose()
        {
            if (_previous != null) PluginManager.Instance = _previous;
            ExecutableResolver.NodeVersionOverride = null;
            Fetcher.GitOverride = null;
            PluginSettings.Apply(new JObject());
            Fetcher.TryDelete(Dir);
        }
    }
}

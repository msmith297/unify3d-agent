using System;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Plugins
{
    /// <summary>
    /// The plugin settings in settings.json (flat keys owned by the panel, design critique 19), with the defaults the C#
    /// side uses while a key is missing: settings.json is read before the panel has loaded once.
    /// <list type="bullet">
    /// <item>pluginAutoUpdate (true): update plugins of auto-updating marketplaces at start-up, files only;</item>
    /// <item>pluginHooks (true) / pluginMcp (true): run trusted plugins' hooks / MCP servers at all;</item>
    /// <item>pluginWakesPerHour (20): the wake loop guard;</item>
    /// <item>pluginEventsInChat (true): show hook notices and wakes in the chat;</item>
    /// <item>toolInputs ("panel" | "page"): where tool inputs are asked for.</item>
    /// </list>
    /// </summary>
    internal static class PluginSettings
    {
        private static JObject _current;
        private static readonly object Lock = new object();

        /// <summary>Raised after Apply (from settings.save): the runtime starts or stops hooks and MCP servers.</summary>
        public static event Action Changed;

        /// <summary>The settings as last saved (read from settings.json the first time).</summary>
        public static JObject Current
        {
            get
            {
                lock (Lock) return _current ?? (_current = SettingsStore.LoadSettings());
            }
        }

        /// <summary>The panel saved its settings (AgentController's settings.save calls this).</summary>
        public static void Apply(JObject settings)
        {
            lock (Lock) _current = (JObject)settings?.DeepClone() ?? new JObject();
            Changed?.Invoke();
        }

        public static bool AutoUpdate => Flag("pluginAutoUpdate", true);
        public static bool Hooks => Flag("pluginHooks", true);
        public static bool Mcp => Flag("pluginMcp", true);
        public static bool EventsInChat => Flag("pluginEventsInChat", true);

        public static int WakesPerHour
        {
            get
            {
                var v = Current["pluginWakesPerHour"];
                return v != null && (v.Type == JTokenType.Integer || v.Type == JTokenType.Float) ? Math.Max(0, (int)v) : 20;
            }
        }

        /// <summary>"panel" (default) or "page".</summary>
        public static string ToolInputs => (string)Current["toolInputs"] == "page" ? "page" : "panel";

        private static bool Flag(string key, bool fallback) => Current[key]?.Type == JTokenType.Boolean ? (bool)Current[key] : fallback;
    }
}

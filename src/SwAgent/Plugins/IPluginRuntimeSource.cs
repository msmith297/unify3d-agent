using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SwAgent.Plugins
{
    /// <summary>
    /// The seam between the plugin core (installs, marketplaces, trust) and the runtime that runs plugin hooks and MCP
    /// servers (Plugins/Hooks, Mcp). The core decides what may run; the runtime only runs what it is given.
    /// </summary>
    internal interface IPluginRuntimeSource
    {
        /// <summary>Plugins whose hooks and MCP servers may run: enabled, trusted (the approved hash matches) and allowed by settings.</summary>
        IReadOnlyList<PluginRuntime> Runnable();

        /// <summary>Raised when that list may have changed. Raised off the UI thread; the runtime marshals.</summary>
        event Action Changed;
    }

    /// <summary>One runnable plugin, as the runtime needs it.</summary>
    internal sealed class PluginRuntime
    {
        /// <summary>"name@marketplace".</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>The installed plugin folder (CLAUDE_PLUGIN_ROOT).</summary>
        public string Root { get; set; }

        /// <summary>The plugin's writable folder that survives updates (CLAUDE_PLUGIN_DATA).</summary>
        public string DataDir { get; set; }

        /// <summary>
        /// Extra environment for its hooks and MCP servers: the marketplace entry's unify3d.env, then plugin.json's
        /// unify3d.env, then userConfig as CLAUDE_PLUGIN_OPTION_&lt;KEY&gt;.
        /// </summary>
        public IDictionary<string, string> Env { get; set; }

        /// <summary>Expands ${CLAUDE_PLUGIN_ROOT}, ${CLAUDE_PLUGIN_DATA}, ${UNIFY3D_*}, ${user_config.*} and environment variables.</summary>
        public Func<string, string> Expand { get; set; }

        /// <summary>Normalized hooks: { Event: [{ matcher, hooks: [{ type, command, timeout, async, asyncRewake, rewakeMessage }] }] }, or null.</summary>
        public JObject Hooks { get; set; }

        /// <summary>MCP server configs by server name ({ command, args, env, ... }), or null.</summary>
        public IDictionary<string, JObject> McpServers { get; set; }

        /// <summary>Hash of the plugin's hooks and MCP servers that the user approved.</summary>
        public string TrustHash { get; set; }
    }
}

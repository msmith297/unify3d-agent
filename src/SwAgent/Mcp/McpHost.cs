using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Plugins;
using SwAgent.Plugins.Hooks;
using SwAgent.Tools;

namespace SwAgent.Mcp
{
    /// <summary>
    /// The MCP servers of the runnable plugins and of the user's mcp.json, and their tools as agent tools.
    ///
    /// Tool names follow Claude Code: mcp__plugin_&lt;plugin&gt;_&lt;server&gt;__&lt;tool&gt; for a plugin's server (no collisions
    /// between plugins), mcp__&lt;server&gt;__&lt;tool&gt; for mcp.json. The API allows 64 characters of [a-zA-Z0-9_-]: other
    /// characters become _, and a longer name is cut and given a hash suffix; the map leads back to the real name.
    /// A server that dies is restarted with backoff (1, 2, 4, 8, 16 s), then left in the error state until restarted by
    /// hand. Thread-safe; nothing here touches SolidWorks.
    /// </summary>
    internal sealed class McpHost : IDisposable
    {
        public const string Prefix = "mcp__";

        /// <summary>How long one tools/call may take.</summary>
        public TimeSpan CallTimeout { get; set; } = TimeSpan.FromMinutes(10);

        private static readonly int[] BackoffSeconds = { 1, 2, 4, 8, 16 };

        private readonly Session _session;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Server> _servers = new Dictionary<string, Server>(StringComparer.Ordinal);
        private Dictionary<string, (Server server, string tool)> _byApiName = new Dictionary<string, (Server, string)>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CancellationTokenSource>> _inFlight = new Dictionary<string, List<CancellationTokenSource>>(StringComparer.Ordinal);
        private bool _disposed;

        public McpHost(Session session)
        {
            _session = session;
        }

        /// <summary>Raised (off the UI thread) when the tools or a server's state changed.</summary>
        public event Action Changed;

        public static bool IsMcp(string name) => name != null && name.StartsWith(Prefix, StringComparison.Ordinal);

        private sealed class Server
        {
            public string Key;
            public PluginRuntime Plugin;
            public string Name;
            public JObject Config;
            public string Fingerprint;
            public string State = "starting";
            public string Error;
            public List<JObject> Tools = new List<JObject>();
            public McpClient Client;
            public int Generation;
            public int Failures;
            public DateTime StartedAt;
            public readonly TaskCompletionSource<bool> FirstAttempt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public string Label => Plugin == null ? Name : Plugin.Name + ":" + Name;
        }

        /// <summary>
        /// Makes the running servers match `plugins` (their McpServers) plus `userServers` (mcp.json's mcpServers): new ones
        /// start, removed ones stop, changed ones restart.
        /// </summary>
        public void Sync(IReadOnlyList<PluginRuntime> plugins, JObject userServers)
        {
            var wanted = new Dictionary<string, Server>(StringComparer.Ordinal);
            foreach (var p in plugins ?? new PluginRuntime[0])
            {
                if (p?.McpServers == null) continue;
                foreach (var kv in p.McpServers)
                {
                    if (kv.Value == null) continue;
                    var s = new Server { Key = p.Id + "/" + kv.Key, Plugin = p, Name = kv.Key, Config = kv.Value };
                    s.Fingerprint = Fingerprint(s);
                    wanted[s.Key] = s;
                }
            }
            foreach (var prop in userServers?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                if (!(prop.Value is JObject config)) continue;
                var s = new Server { Key = "user/" + prop.Name, Name = prop.Name, Config = config };
                s.Fingerprint = Fingerprint(s);
                wanted[s.Key] = s;
            }

            var stop = new List<Server>();
            var start = new List<Server>();
            lock (_sync)
            {
                if (_disposed) return;
                foreach (var old in _servers.Values.ToList())
                {
                    if (wanted.TryGetValue(old.Key, out var now) && now.Fingerprint == old.Fingerprint)
                    {
                        old.Plugin = now.Plugin; // same config; keep the running server
                        continue;
                    }
                    _servers.Remove(old.Key);
                    old.Generation++;
                    stop.Add(old);
                }
                foreach (var s in wanted.Values)
                {
                    if (_servers.ContainsKey(s.Key)) continue;
                    _servers[s.Key] = s;
                    start.Add(s);
                }
                if (stop.Count > 0) RebuildMap();
            }
            foreach (var s in stop) _ = StopServerAsync(s, TimeSpan.FromSeconds(2));
            foreach (var s in start) _ = StartServerAsync(s, s.Generation);
            if (stop.Count > 0 || start.Count > 0) Changed?.Invoke();
        }

        private static string Fingerprint(Server s) => (s.Plugin?.Root ?? "") + "|" + (s.Plugin?.TrustHash ?? "") + "|" +
            s.Config.ToString(Formatting.None) + "|" + (s.Plugin?.Env == null ? "" : string.Join(";", s.Plugin.Env.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value)));

        private async Task StartServerAsync(Server s, int generation)
        {
            var type = (string)s.Config["type"] ?? "stdio";
            if (type != "stdio" || string.IsNullOrWhiteSpace((string)s.Config["command"]))
            {
                // Retrying can't help: show the error until the config changes.
                lock (_sync)
                {
                    if (s.Generation != generation) return;
                    s.State = "error";
                    s.Error = type != "stdio" ? $"'{type}' MCP servers aren't supported yet (only stdio)." : "The server has no command.";
                }
                Log.Warn($"MCP {s.Label}: {s.Error}");
                s.FirstAttempt.TrySetResult(true);
                Changed?.Invoke();
                return;
            }
            McpClient client = null;
            try
            {
                var env = _session.EnvFor(s.Plugin);
                if (s.Config["env"] is JObject extra)
                {
                    foreach (var kv in extra.Properties()) env[kv.Name] = (string)kv.Value;
                    foreach (var kv in extra.Properties()) env[kv.Name] = Session.Expand(s.Plugin, env[kv.Name], env);
                }
                var command = Session.Expand(s.Plugin, (string)s.Config["command"], env);
                if (string.IsNullOrWhiteSpace(command)) throw new McpException("No command.");
                var args = (s.Config["args"] as JArray ?? new JArray()).Select(a => Session.Expand(s.Plugin, (string)a, env)).ToList();

                client = new McpClient(s.Label);
                var c = client;
                client.Closed += why => OnClosed(s, generation, c, why);
                client.ToolsChanged += () => _ = RefreshToolsAsync(s, generation, c);
                lock (_sync)
                {
                    if (s.Generation != generation || _disposed) throw new OperationCanceledException();
                    s.Client = client;
                    s.State = "starting";
                    s.Error = null;
                }
                Log.Info($"MCP {s.Label}: starting {(string)s.Config["command"]}"); // as configured: expanded values stay out of the log
                await client.StartAsync(command, args, env, Session.ProjectDir).ConfigureAwait(false);
                var tools = await client.ListToolsAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    if (s.Generation != generation || _disposed) throw new OperationCanceledException();
                    s.Tools = tools;
                    s.State = "running";
                    s.StartedAt = DateTime.UtcNow;
                    RebuildMap();
                }
                Log.Info($"MCP {s.Label}: running, {tools.Count} tool(s)");
                Changed?.Invoke();
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
            }
            catch (Exception ex)
            {
                client?.Dispose();
                Log.Warn($"MCP {s.Label} failed to start: {ex.Message}");
                Failed(s, generation, ex.Message);
            }
            finally
            {
                s.FirstAttempt.TrySetResult(true);
            }
        }

        private async Task RefreshToolsAsync(Server s, int generation, McpClient client)
        {
            try
            {
                var tools = await client.ListToolsAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    if (s.Generation != generation || s.Client != client) return;
                    s.Tools = tools;
                    RebuildMap();
                }
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Warn($"MCP {s.Label}: refreshing its tools failed: {ex.Message}");
            }
        }

        private void OnClosed(Server s, int generation, McpClient client, string why)
        {
            lock (_sync)
            {
                if (s.Generation != generation || s.Client != client || _disposed) return; // stopped on purpose, or replaced
                if (s.State != "running") return; // still starting: StartServerAsync reports that failure itself
            }
            Log.Warn($"MCP {s.Label} stopped: {why}");
            Failed(s, generation, "The server stopped: " + why);
        }

        /// <summary>A start failed or the server died: try again after a pause, a few times, then stay in the error state.</summary>
        private void Failed(Server s, int generation, string error)
        {
            int delay;
            lock (_sync)
            {
                if (s.Generation != generation || _disposed) return;
                // A server that ran for a while before dying gets a fresh set of retries.
                if (s.State == "running" && DateTime.UtcNow - s.StartedAt > TimeSpan.FromMinutes(2)) s.Failures = 0;
                s.Client = null;
                s.Tools = new List<JObject>();
                s.Error = error;
                RebuildMap();
                if (s.Failures >= BackoffSeconds.Length)
                {
                    s.State = "error";
                    delay = -1;
                }
                else
                {
                    s.State = "restarting";
                    delay = BackoffSeconds[s.Failures++];
                }
            }
            Changed?.Invoke();
            if (delay < 0) return;
            _ = Task.Delay(TimeSpan.FromSeconds(delay)).ContinueWith(_ =>
            {
                lock (_sync)
                {
                    if (s.Generation != generation || _disposed || !_servers.ContainsKey(s.Key)) return;
                }
                _ = StartServerAsync(s, generation);
            }, TaskScheduler.Default);
        }

        private async Task StopServerAsync(Server s, TimeSpan grace)
        {
            McpClient client;
            lock (_sync)
            {
                client = s.Client;
                s.Client = null;
                s.State = "stopped";
            }
            if (client == null) return;
            try
            {
                await client.StopAsync(grace).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"MCP {s.Label}: stopping failed: {ex.Message}");
            }
            client.Dispose();
        }

        /// <summary>Restarts one server by hand (the Plugins screen's restart button); plugin null for an mcp.json server.</summary>
        public bool Restart(string plugin, string server)
        {
            Server s;
            int generation;
            lock (_sync)
            {
                s = _servers.Values.FirstOrDefault(x => x.Name == server && (string.IsNullOrEmpty(plugin) ? x.Plugin == null : x.Plugin != null && (x.Plugin.Name == plugin || x.Plugin.Id == plugin)));
                if (s == null || _disposed) return false;
                generation = ++s.Generation;
                s.Failures = 0;
            }
            _ = Task.Run(async () =>
            {
                await StopServerAsync(s, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await StartServerAsync(s, generation).ConfigureAwait(false);
            });
            return true;
        }

        /// <summary>Completes when every server has answered initialize (or failed), or after `timeout`.</summary>
        public Task WhenStarted(TimeSpan timeout)
        {
            List<Task> first;
            lock (_sync)
            {
                first = _servers.Values.Select(s => (Task)s.FirstAttempt.Task).ToList();
            }
            return Task.WhenAny(Task.WhenAll(first), Task.Delay(timeout));
        }

        // ---- tools ----

        private void RebuildMap()
        {
            var map = new Dictionary<string, (Server, string)>(StringComparer.Ordinal);
            foreach (var s in _servers.Values.Where(x => x.State == "running").OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                foreach (var t in s.Tools)
                {
                    var tool = (string)t["name"];
                    if (string.IsNullOrEmpty(tool)) continue;
                    var name = ApiName(s.Plugin?.Name, s.Name, tool);
                    if (map.ContainsKey(name)) name = WithHash(name, s.Key + "/" + tool);
                    map[name] = (s, tool);
                }
            }
            _byApiName = map;
        }

        /// <summary>The agent's name for a server's tool (Claude Code's form, made valid for the API).</summary>
        internal static string ApiName(string plugin, string server, string tool)
        {
            var raw = plugin == null ? $"{Prefix}{server}__{tool}" : $"{Prefix}plugin_{plugin}_{server}__{tool}";
            var name = Regex.Replace(raw, "[^a-zA-Z0-9_-]", "_");
            return name.Length <= 64 ? name : WithHash(name, raw);
        }

        private static string WithHash(string name, string seed)
        {
            using (var sha = SHA1.Create())
            {
                var hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(seed)).Take(4).Select(b => b.ToString("x2")));
                return name.Substring(0, Math.Min(name.Length, 64 - 9)) + "_" + hash;
            }
        }

        /// <summary>Tool definitions in Claude API shape, for tools.list.</summary>
        public JArray Definitions()
        {
            Dictionary<string, (Server server, string tool)> map;
            lock (_sync) map = _byApiName;
            var list = new JArray();
            foreach (var kv in map.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var def = kv.Value.server.Tools.FirstOrDefault(t => (string)t["name"] == kv.Value.tool);
                if (def == null) continue;
                list.Add(new JObject
                {
                    ["name"] = kv.Key,
                    ["description"] = (string)def["description"] ?? (string)def["title"] ?? kv.Value.tool,
                    ["input_schema"] = SanitizeSchema(def["inputSchema"]),
                });
            }
            return list;
        }

        /// <summary>A schema the API accepts: an object type, no $schema, no top-level anyOf/oneOf/allOf.</summary>
        internal static JObject SanitizeSchema(JToken schema)
        {
            var s = schema is JObject o ? (JObject)o.DeepClone() : new JObject();
            s.Remove("$schema");
            s.Remove("anyOf");
            s.Remove("oneOf");
            s.Remove("allOf");
            s["type"] = "object";
            if (!(s["properties"] is JObject)) s["properties"] = new JObject();
            return s;
        }

        /// <summary>Runs an MCP tool for the agent; returns a tool result ({ content, isError }).</summary>
        public async Task<ToolResult> CallAsync(string apiName, JObject input, string threadId)
        {
            (Server server, string tool) target;
            McpClient client;
            lock (_sync)
            {
                if (!_byApiName.TryGetValue(apiName ?? "", out target)) return ToolResult.Error($"The MCP tool '{apiName}' isn't available (its server isn't running).");
                client = target.server.Client;
            }
            if (client == null) return ToolResult.Error($"The MCP server '{target.server.Label}' isn't running ({target.server.State}).");
            var cts = new CancellationTokenSource();
            var key = threadId ?? "";
            lock (_sync)
            {
                if (!_inFlight.TryGetValue(key, out var list)) _inFlight[key] = list = new List<CancellationTokenSource>();
                list.Add(cts);
            }
            var started = DateTime.UtcNow;
            try
            {
                var result = await client.CallToolAsync(target.tool, input, CallTimeout, cts.Token).ConfigureAwait(false);
                var mapped = MapResult(result);
                Log.Info($"MCP tool {apiName} finished in {(DateTime.UtcNow - started).TotalMilliseconds:N0} ms{(mapped.IsError ? " (error)" : "")}");
                return mapped;
            }
            catch (OperationCanceledException)
            {
                return ToolResult.Error("Cancelled.");
            }
            catch (Exception ex)
            {
                Log.Warn($"MCP tool {apiName} failed: {ex.Message}");
                return ToolResult.Error(ex.Message);
            }
            finally
            {
                lock (_sync)
                {
                    if (_inFlight.TryGetValue(key, out var list) && list.Remove(cts) && list.Count == 0) _inFlight.Remove(key);
                }
                cts.Dispose();
            }
        }

        /// <summary>Cancels a thread's running MCP calls (tools.cancel); each server gets notifications/cancelled.</summary>
        public void Cancel(string threadId)
        {
            List<CancellationTokenSource> list;
            lock (_sync)
            {
                if (!_inFlight.TryGetValue(threadId ?? "", out list)) return;
                list = list.ToList();
            }
            foreach (var cts in list)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Finished meanwhile.
                }
            }
        }

        private static readonly HashSet<string> ImageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp" };

        /// <summary>An MCP tools/call result as a tool result: text stays text, images become image blocks, the rest is described.</summary>
        internal static ToolResult MapResult(JObject result)
        {
            var r = new ToolResult { IsError = (bool?)result["isError"] == true };
            foreach (var c in (result["content"] as JArray ?? new JArray()).OfType<JObject>())
            {
                switch ((string)c["type"])
                {
                    case "text":
                        r.Text((string)c["text"]);
                        break;
                    case "image":
                        var mime = (string)c["mimeType"];
                        if (ImageTypes.Contains(mime ?? "") && !string.IsNullOrEmpty((string)c["data"])) r.Image((string)c["data"], mime.ToLowerInvariant());
                        else r.Text($"[image {mime}]");
                        break;
                    case "audio":
                        r.Text($"[audio {(string)c["mimeType"]}]");
                        break;
                    case "resource_link":
                        r.Text($"[resource {(string)c["name"]}: {(string)c["uri"]}]");
                        break;
                    case "resource":
                        var res = c["resource"] as JObject;
                        r.Text(res?["text"] != null ? (string)res["text"] : $"[resource {(string)res?["uri"]}]");
                        break;
                    default:
                        r.Text(c.ToString(Formatting.None));
                        break;
                }
            }
            if (r.Content.Count == 0 && result["structuredContent"] != null) r.Text(result["structuredContent"].ToString(Formatting.None));
            if (r.Content.Count == 0) r.Text("(empty)");
            return r;
        }

        // ---- status ----

        /// <summary>mcp.status: [{ plugin, server, state, error, toolCount }] (plugin null for mcp.json servers).</summary>
        public JArray Status()
        {
            lock (_sync)
            {
                return new JArray(_servers.Values.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new JObject
                {
                    ["plugin"] = s.Plugin?.Name,
                    ["server"] = s.Name,
                    ["state"] = s.State,
                    ["error"] = s.Error,
                    ["toolCount"] = s.State == "running" ? s.Tools.Count : 0,
                }));
            }
        }

        /// <summary>The servers' instructions from initialize, for the system prompt.</summary>
        public JArray Instructions()
        {
            lock (_sync)
            {
                return new JArray(_servers.Values.Where(s => s.State == "running" && !string.IsNullOrWhiteSpace(s.Client?.Instructions))
                    .OrderBy(s => s.Key, StringComparer.Ordinal)
                    .Select(s => new JObject { ["server"] = s.Name, ["plugin"] = s.Plugin?.Name, ["text"] = s.Client.Instructions }));
            }
        }

        /// <summary>The pid of a running server's process (tests).</summary>
        internal int? ProcessIdOf(string server)
        {
            lock (_sync) return _servers.Values.FirstOrDefault(s => s.Name == server)?.Client?.ProcessId;
        }

        /// <summary>Stops every server: stdin closes first so each can unregister, then whatever is left is killed.</summary>
        public Task StopAllAsync(TimeSpan grace)
        {
            List<Server> all;
            lock (_sync)
            {
                all = _servers.Values.ToList();
                _servers.Clear();
                _byApiName = new Dictionary<string, (Server, string)>(StringComparer.Ordinal);
                foreach (var s in all) s.Generation++;
            }
            return Task.WhenAll(all.Select(s => StopServerAsync(s, grace)));
        }

        public void Dispose()
        {
            lock (_sync) _disposed = true;
        }
    }
}

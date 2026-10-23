using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mcp
{
    /// <summary>
    /// One connection to an MCP server, whatever carries it: a local program over stdio (McpClient) or a remote server
    /// over Streamable HTTP, with the older HTTP+SSE transport as a fallback (McpHttpClient). Off the UI thread; never
    /// touches SolidWorks.
    /// </summary>
    internal interface IMcpConnection : IDisposable
    {
        string Name { get; }

        /// <summary>The server's instructions from initialize (go into the system prompt, as Claude Code does).</summary>
        string Instructions { get; }

        JObject ServerInfo { get; }

        /// <summary>The server's process (stdio); null for a remote server.</summary>
        int? ProcessId { get; }

        /// <summary>What the server wrote to stderr so far (stdio; the last 64 KB); null for a remote server.</summary>
        string Stderr { get; }

        /// <summary>Raised (off the UI thread) when the server says its tool list changed.</summary>
        event Action ToolsChanged;

        /// <summary>Raised once when the connection ends (exited, stopped, or the stream closed); the text says why.</summary>
        event Action<string> Closed;

        /// <summary>Connects and completes the initialize handshake. Throws (and stops it) on failure.</summary>
        Task StartAsync();

        /// <summary>tools/list, every page.</summary>
        Task<List<JObject>> ListToolsAsync(CancellationToken ct);

        /// <summary>tools/call. On cancel or timeout the server gets notifications/cancelled and the call throws.</summary>
        Task<JObject> CallToolAsync(string tool, JObject arguments, TimeSpan timeout, CancellationToken ct);

        /// <summary>Ends the connection politely (stdin closed / session deleted), then for good after `grace`.</summary>
        Task StopAsync(TimeSpan grace);
    }
}

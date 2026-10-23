using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwAgent.Tests.Plugins
{
    /// <summary>
    /// A remote MCP server for the tests, on 127.0.0.1 (no network): plain HTTP/1.1 over a TcpListener, one request per
    /// connection. Modes: Streamable HTTP answering with JSON, Streamable HTTP answering with an SSE stream (with a
    /// notification and a ping before the answer), and the older HTTP+SSE transport (POST /mcp is 405; GET /mcp opens the
    /// event stream; answers to POST /messages arrive there). With Token set, every request needs "Authorization: Bearer
    /// &lt;token&gt;" and a missing one gets 401 with an OAuth challenge.
    /// Tools: echo (read-only) and whoami (says which Authorization header it got).
    /// </summary>
    internal sealed class FakeHttpMcp : IDisposable
    {
        public enum Kind
        {
            Json,
            Sse,
            Legacy,
        }

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _sseLock = new object();
        private Stream _sse;

        public FakeHttpMcp(Kind kind, string token = null)
        {
            Mode = kind;
            Token = token;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(AcceptLoop);
        }

        public Kind Mode { get; }

        public string Token { get; }

        public int Port { get; }

        public string Url => $"http://127.0.0.1:{Port}/mcp";

        /// <summary>Every request: "METHOD path | Authorization | Mcp-Session-Id | MCP-Protocol-Version".</summary>
        public readonly ConcurrentQueue<string> Requests = new ConcurrentQueue<string>();

        /// <summary>Answers to the server's own requests (the ping on an SSE answer).</summary>
        public readonly ConcurrentQueue<JObject> Replies = new ConcurrentQueue<JObject>();

        public readonly ConcurrentQueue<string> Notifications = new ConcurrentQueue<string>();

        /// <summary>Set: any request to /old gets "307 Temporary Redirect" to this url.</summary>
        public string Redirect;

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            var keepOpen = false;
            try
            {
                var stream = client.GetStream();
                var (method, path, headers, body) = await ReadRequest(stream).ConfigureAwait(false);
                headers.TryGetValue("authorization", out var auth);
                headers.TryGetValue("mcp-session-id", out var session);
                headers.TryGetValue("mcp-protocol-version", out var protocol);
                Requests.Enqueue($"{method} {path} | {auth} | {session} | {protocol}");
                if (Redirect != null && path == "/old")
                {
                    await Write(stream, 307, "Temporary Redirect", "text/plain", "", "Location: " + Redirect).ConfigureAwait(false);
                    return;
                }
                if (Token != null && auth != "Bearer " + Token)
                {
                    await Write(stream, 401, "Unauthorized", "application/json", "{\"error\":\"unauthorized\"}",
                        "WWW-Authenticate: Bearer resource_metadata=\"http://127.0.0.1/.well-known/oauth-protected-resource\"").ConfigureAwait(false);
                    return;
                }
                if (method == "DELETE")
                {
                    await Write(stream, 200, "OK", "text/plain", "").ConfigureAwait(false);
                    return;
                }
                if (Mode == Kind.Legacy)
                {
                    if (method == "GET" && path == "/mcp")
                    {
                        // A stream that stays open is chunked, as real servers send it.
                        var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nTransfer-Encoding: chunked\r\n\r\n");
                        await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
                        lock (_sseLock)
                        {
                            _sse = stream;
                            WriteChunk("event: endpoint\ndata: /messages?session=abc\n\n");
                        }
                        keepOpen = true;
                        return;
                    }
                    if (method == "POST" && path.StartsWith("/messages"))
                    {
                        var msg = JObject.Parse(body);
                        var answer = Answer(msg, null);
                        await Write(stream, 202, "Accepted", "text/plain", "").ConfigureAwait(false);
                        if (answer != null) PushLegacy(answer);
                        return;
                    }
                    await Write(stream, 405, "Method Not Allowed", "text/plain", "").ConfigureAwait(false);
                    return;
                }
                if (method != "POST")
                {
                    await Write(stream, 405, "Method Not Allowed", "text/plain", "").ConfigureAwait(false);
                    return;
                }
                var request = JObject.Parse(body);
                var extra = (string)request["method"] == "initialize" ? "Mcp-Session-Id: sess-42" : null;
                var reply = Answer(request, session);
                if (reply == null)
                {
                    await Write(stream, 202, "Accepted", "text/plain", "").ConfigureAwait(false);
                    return;
                }
                if (Mode == Kind.Json)
                {
                    await Write(stream, 200, "OK", "application/json", reply.ToString(Formatting.None), extra).ConfigureAwait(false);
                    return;
                }
                // An SSE answer: a log notification and a ping first, then the answer, then the stream ends.
                var sse = new StringBuilder();
                sse.Append("event: message\ndata: ").Append(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/message", ["params"] = new JObject { ["level"] = "info", ["data"] = "working" } }.ToString(Formatting.None)).Append("\n\n");
                sse.Append(": a comment line\n\n");
                sse.Append("data: ").Append(new JObject { ["jsonrpc"] = "2.0", ["id"] = "srv-ping", ["method"] = "ping" }.ToString(Formatting.None)).Append("\n\n");
                sse.Append("event: message\ndata: ").Append(reply.ToString(Formatting.None)).Append("\n\n");
                var h = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n" + (extra != null ? extra + "\r\n" : "") + "\r\n";
                var all = Encoding.UTF8.GetBytes(h + sse);
                await stream.WriteAsync(all, 0, all.Length).ConfigureAwait(false);
            }
            catch
            {
                // A test ended mid-request.
            }
            finally
            {
                if (!keepOpen) client.Close();
            }
        }

        /// <summary>The JSON-RPC answer to a request (null for a notification or for our answer to the server's ping).</summary>
        private JObject Answer(JObject msg, string session)
        {
            var method = (string)msg["method"];
            var id = msg["id"];
            if (method == null)
            {
                Replies.Enqueue(msg);
                return null;
            }
            if (id == null)
            {
                Notifications.Enqueue(method);
                return null;
            }
            JObject result;
            switch (method)
            {
                case "initialize":
                    result = new JObject
                    {
                        ["protocolVersion"] = (string)msg["params"]?["protocolVersion"],
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "fake-remote", ["version"] = "2.0.0" },
                        ["instructions"] = "Fake remote server.",
                    };
                    break;
                case "tools/list":
                    result = new JObject
                    {
                        ["tools"] = new JArray(
                            new JObject { ["name"] = "echo", ["description"] = "Echoes its text.", ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = new JObject { ["text"] = new JObject { ["type"] = "string" } }, ["required"] = new JArray("text") }, ["annotations"] = new JObject { ["readOnlyHint"] = true } },
                            new JObject { ["name"] = "whoami", ["description"] = "Which Authorization header it got.", ["inputSchema"] = new JObject { ["type"] = "object" } }),
                    };
                    break;
                case "tools/call":
                    var name = (string)msg["params"]?["name"];
                    var text = name == "echo" ? (string)msg["params"]?["arguments"]?["text"] : name == "whoami" ? "auth=" + LastAuth() + " session=" + session : "unknown";
                    result = new JObject { ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }) };
                    break;
                default:
                    return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not found" } };
            }
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }

        private string LastAuth() => Requests.Reverse().Select(r => r.Split('|')[1].Trim()).FirstOrDefault();

        private void PushLegacy(JObject msg)
        {
            lock (_sseLock)
            {
                if (_sse == null) return;
                WriteChunk("event: message\ndata: " + msg.ToString(Formatting.None) + "\n\n");
            }
        }

        private void WriteChunk(string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            var size = Encoding.ASCII.GetBytes(data.Length.ToString("x") + "\r\n");
            _sse.Write(size, 0, size.Length);
            _sse.Write(data, 0, data.Length);
            _sse.Write(new byte[] { 13, 10 }, 0, 2);
            _sse.Flush();
        }

        private static async Task<(string method, string path, Dictionary<string, string> headers, string body)> ReadRequest(Stream stream)
        {
            var buffer = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                var n = await stream.ReadAsync(one, 0, 1).ConfigureAwait(false);
                if (n == 0) throw new IOException("closed");
                buffer.Add(one[0]);
                var c = buffer.Count;
                if (c >= 4 && buffer[c - 4] == '\r' && buffer[c - 3] == '\n' && buffer[c - 2] == '\r' && buffer[c - 1] == '\n') break;
            }
            var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var first = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                var i = l.IndexOf(':');
                if (i > 0) headers[l.Substring(0, i).Trim().ToLowerInvariant()] = l.Substring(i + 1).Trim();
            }
            var length = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
            var body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await stream.ReadAsync(body, read, length - read).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            return (first[0], first[1], headers, Encoding.UTF8.GetString(body, 0, read));
        }

        private static async Task Write(Stream stream, int status, string reason, string type, string body, string extraHeader = null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n" + (extraHeader != null ? extraHeader + "\r\n" : "") + "\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(h, 0, h.Length).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            lock (_sseLock)
            {
                try
                {
                    _sse?.Dispose();
                }
                catch
                {
                    // Closed.
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.UI
{
    /// <summary>
    /// JSON-RPC between the React UI and the add-in over WebView2 messaging.
    ///   UI  -> host: { kind: "rpc", id, method, params }
    ///   host -> UI : { kind: "rpc-result", id, ok, result | error }
    ///   host -> UI : { kind: "event", name, data }
    /// Handlers run on the SolidWorks UI thread, which is where every COM call must happen.
    /// </summary>
    internal sealed class WebBridge
    {
        private readonly Dictionary<string, Func<JToken, Task<object>>> _methods =
            new Dictionary<string, Func<JToken, Task<object>>>(StringComparer.Ordinal);

        private readonly Control _uiThread;
        private CoreWebView2 _core;

        public WebBridge(Control uiThread)
        {
            _uiThread = uiThread;
        }

        public bool IsReady => _core != null;

        /// <summary>
        /// Files dropped on the panel. The page can't see paths, so it forwards the File objects with
        /// chrome.webview.postMessageWithAdditionalObjects({ kind: "files" }, files) and WebView2 hands us their paths.
        /// </summary>
        public event Action<string[]> FilesDropped;

        public void Attach(CoreWebView2 core)
        {
            _core = core;
            _core.WebMessageReceived += OnMessage;
        }

        public void RegisterAsync(string method, Func<JToken, Task<object>> handler) => _methods[method] = handler;

        public void Register(string method, Func<JToken, object> handler) =>
            _methods[method] = p => Task.FromResult(handler(p));

        /// <summary>Push an event to the UI. Safe to call from any thread; dropped if the UI isn't loaded yet.</summary>
        public void Emit(string name, object data)
        {
            Post(new JObject { ["kind"] = "event", ["name"] = name, ["data"] = data == null ? null : JToken.FromObject(data) });
        }

        private async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject msg;
            try
            {
                msg = JObject.Parse(e.WebMessageAsJson);
            }
            catch (Exception ex)
            {
                Log.Error("Malformed message from UI", ex);
                return;
            }
            if ((string)msg["kind"] == "files")
            {
                var paths = (e.AdditionalObjects ?? new object[0]).OfType<CoreWebView2File>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
                if (paths.Length > 0) FilesDropped?.Invoke(paths);
                return;
            }
            if ((string)msg["kind"] != "rpc") return;

            var id = msg["id"];
            var method = (string)msg["method"];
            try
            {
                if (!_methods.TryGetValue(method ?? "", out var handler)) throw new InvalidOperationException($"Unknown method '{method}'");
                var result = await handler(msg["params"] ?? JValue.CreateNull());
                Post(new JObject
                {
                    ["kind"] = "rpc-result", ["id"] = id, ["ok"] = true,
                    ["result"] = result == null ? JValue.CreateNull() : result as JToken ?? JToken.FromObject(result),
                });
            }
            catch (Exception ex)
            {
                Log.Error($"RPC {method} failed", ex);
                Post(new JObject { ["kind"] = "rpc-result", ["id"] = id, ["ok"] = false, ["error"] = ex.Message });
            }
        }

        private void Post(JObject message)
        {
            if (_core == null) return;
            var json = message.ToString(Formatting.None);
            if (_uiThread.InvokeRequired)
            {
                _uiThread.BeginInvoke((Action)(() => _core?.PostWebMessageAsJson(json)));
            }
            else
            {
                _core.PostWebMessageAsJson(json);
            }
        }
    }
}

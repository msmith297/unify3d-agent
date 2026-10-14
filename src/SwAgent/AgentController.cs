using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Chat;
using SwAgent.Infrastructure;
using SwAgent.ModelFiles;
using SwAgent.Scripting;
using SwAgent.Tools;
using SwAgent.UI;
using SwAgent.Watch;
using Xarial.XCad.Documents;
using Xarial.XCad.SolidWorks;
using Xarial.XCad.SolidWorks.Documents;

namespace SwAgent
{
    /// <summary>
    /// Glue between SolidWorks documents, their chats, the tool registry and the web UI.
    /// Everything here runs on the SolidWorks UI thread.
    /// </summary>
    internal sealed class AgentController : IDisposable
    {
        private readonly ISwApplication _app;
        private readonly ToolRegistry _tools;
        private readonly ScriptHost _scripts;
        private readonly Dictionary<string, ChatDocumentHandler> _docs = new Dictionary<string, ChatDocumentHandler>();
        private readonly ChatDocument _scratch = ChatDocument.CreateScratch();
        private readonly Timer _selectionTimer = new Timer { Interval = 150 };
        private readonly Timer _themeTimer = new Timer { Interval = 2500 };
        private string _lastTheme;
        private readonly Timer _activeTimer = new Timer { Interval = 1000 };
        private readonly Timer _updateTimer = new Timer { Interval = 6 * 60 * 60 * 1000 };

        /// <summary>Update feed (GitHub Releases); checked at start-up and every few hours.</summary>
        internal Updater Updates { get; } = new Updater();

        /// <summary>Set once the install-after-exit helper has been started, so shutdown doesn't start it twice.</summary>
        internal bool UpdateHelperStarted { get; private set; }

        /// <summary>Auto-update: on unless the user turned it off (null = not asked yet, treated as off).</summary>
        internal bool AutoUpdate => (bool?)_settings["autoUpdate"] == true;
        private string _lastActiveKey;
        private ChatDocumentHandler _selectionSource;
        private WebBridge _bridge;
        private JObject _settings = SettingsStore.LoadSettings();
        private MarkupOverlay _markup;
        private IntPtr _markupView;
        private WatchRecorder _watch;

        /// <summary>What SolidWorks was running when it closed unexpectedly last time (CrashGuard), until dismissed.</summary>
        internal JObject Crash { get; set; }

        /// <summary>True when tools changed since SolidWorks started: the ribbon only picks them up after a restart.</summary>
        internal Func<bool> RibbonStale { get; set; }
        private bool _agentRunning;

        public AgentController(ISwApplication app, ToolRegistry tools, ScriptHost scripts)
        {
            _app = app;
            _tools = tools;
            _scripts = scripts;
            _selectionTimer.Tick += (s, e) =>
            {
                _selectionTimer.Stop();
                if (_selectionSource != null) EmitSelection(_selectionSource);
            };

            // SolidWorks raises no event when the interface theme changes, so poll the (cheap) preferences.
            _themeTimer.Tick += (s, e) =>
            {
                var theme = ReadTheme();
                var json = theme?.ToString(Newtonsoft.Json.Formatting.None);
                if (json == null || json == _lastTheme) return;
                _lastTheme = json;
                _bridge?.Emit("theme.changed", theme);
                ThemeChanged?.Invoke(theme);
            };
            _themeTimer.Start();

            Updates.Changed += () =>
            {
                _bridge?.Emit("update.changed", Updates.ToJson());
                // Auto-update: fetch new versions in the background; they install when SolidWorks closes.
                if (AutoUpdate && Updates.Status == Updater.State.Available) _ = Updates.DownloadAsync();
            };
            _updateTimer.Tick += (s, e) => _ = Updates.CheckAsync();
            _updateTimer.Start();
            var firstCheck = new Timer { Interval = 15000 }; // after start-up has settled
            firstCheck.Tick += (s, e) =>
            {
                firstCheck.Stop();
                firstCheck.Dispose();
                _ = Updates.CheckAsync();
            };
            firstCheck.Start();

            // DocumentActivated isn't raised for File > New (and can be missed during start-up): follow the
            // active document by polling too.
            _activeTimer.Tick += (s, e) =>
            {
                try
                {
                    var handler = HandlerFor(_app.Documents.Active);
                    var key = handler?.Chat.Key;
                    if (key == null || key == _lastActiveKey) return;
                    _lastActiveKey = key;
                    _bridge?.Emit("doc.activated", DocInfo(handler));
                    EmitSelection(handler);
                }
                catch (Exception ex)
                {
                    Log.Warn("Active document poll failed: " + ex.Message);
                }
            };
            _activeTimer.Start();
        }

        public void AttachBridge(WebBridge bridge)
        {
            _bridge = bridge;

            bridge.Register("app.getState", p => GetState());
            bridge.Register("settings.save", p =>
            {
                _settings = (JObject)p;
                SettingsStore.SaveSettings(_settings);
                return null;
            });
            bridge.Register("crash.dismiss", p =>
            {
                Crash = null;
                return null;
            });
            bridge.RegisterAsync("app.saveAndRestart", async p => await SaveAndRestart((bool?)p?["saveDocuments"] == true));
            bridge.Register("userTools.run", p =>
            {
                // From the panel (e.g. right after creating a tool, before the ribbon has its button).
                _ = RunToolAsync((string)p["name"], null);
                return null;
            });
            bridge.Register("update.status", p => Updates.ToJson());
            bridge.RegisterAsync("update.check", async p =>
            {
                await Updates.CheckAsync();
                return Updates.ToJson();
            });
            bridge.Register("update.unsavedDocuments", p => UnsavedDocuments());
            bridge.RegisterAsync("update.restartAndInstall", async p => await RestartAndInstall((bool?)p?["saveDocuments"] == true));
            bridge.Register("apiKey.get", p => new JObject { ["key"] = SettingsStore.GetApiKey() });
            bridge.Register("apiKey.set", p =>
            {
                SettingsStore.SetApiKey((string)p["key"]);
                return new JObject { ["source"] = SettingsStore.ApiKeySource() };
            });

            bridge.Register("chat.load", p =>
            {
                var chat = ResolveChat((string)p["docKey"]);
                return new JObject
                {
                    ["docKey"] = chat.Key,
                    ["docId"] = chat.DocId,
                    ["recovered"] = chat.Recovered,
                    ["data"] = chat.Data,
                };
            });
            bridge.Register("chat.save", p =>
            {
                var chat = ResolveChat((string)p["docKey"]);
                chat.Update((JObject)p["data"]);
                if (!chat.IsScratch && ((bool?)_settings["markDocumentDirty"] ?? true))
                {
                    try
                    {
                        chat.Doc?.Model?.SetSaveFlag(); // so "save changes?" also protects the conversation
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("SetSaveFlag failed: " + ex.Message);
                    }
                }
                return new JObject { ["revision"] = chat.Revision };
            });

            bridge.Register("tools.list", p => _tools.Definitions());
            bridge.RegisterAsync("tools.run", async p =>
            {
                var doc = DocumentFor((string)p["docKey"]);
                var refs = (p["refs"] as JObject)?.Properties().ToDictionary(x => x.Name, x => (string)x.Value);
                var ctx = new ToolContext(_app, doc, (string)p["threadId"], _tools, refs);
                var result = await _tools.InvokeAsync((string)p["name"], p["input"] as JObject, ctx);
                return (object)result.ToJson();
            });
            bridge.Register("tools.cancel", p =>
            {
                _scripts.Cancel((string)p["threadId"]);
                return null;
            });
            bridge.Register("session.reset", p =>
            {
                _scripts.Reset((string)p["threadId"]);
                return null;
            });

            bridge.Register("selection.get", p =>
            {
                var key = (string)p["docKey"];
                return _docs.TryGetValue(key ?? "", out var h) ? SelectionPayload(h) : null;
            });
            bridge.Register("selection.snapshot", p =>
            {
                var doc = DocumentFor((string)p["docKey"]);
                return doc?.Model == null ? null : SelectionTools.Snapshot(_app.Sw, doc.Model);
            });
            bridge.Register("markup.start", p => StartMarkup((string)p["docKey"], (string)p["tool"], (string)p["color"]));
            bridge.Register("markup.set", p =>
            {
                if (_markup == null) return null;
                if (Enum.TryParse((string)p["tool"], out MarkupOverlay.Tool tool)) _markup.CurrentTool = tool;
                if (!string.IsNullOrEmpty((string)p["color"])) _markup.CurrentColor = System.Drawing.ColorTranslator.FromHtml((string)p["color"]);
                return null;
            });
            bridge.Register("markup.undo", p =>
            {
                _markup?.Undo();
                return null;
            });
            bridge.Register("markup.clear", p =>
            {
                _markup?.ClearStrokes();
                return null;
            });
            bridge.Register("markup.finish", p => FinishMarkup());
            bridge.Register("markup.cancel", p =>
            {
                CloseMarkup();
                return null;
            });
            bridge.Register("agent.activity", p =>
            {
                // The UI reports when a run starts and ends; silence SolidWorks' rebuild-error beeps in between.
                _agentRunning = (bool?)p["running"] == true;
                if (_watch != null) _watch.AgentBusy = _agentRunning;
                if ((bool?)p["running"] == true)
                {
                    if ((bool?)_settings["muteErrorSounds"] ?? true) SoundMuter.Begin();
                }
                else
                {
                    SoundMuter.End();
                }
                return null;
            });
            // Files for the chat: picked, dropped on the panel, or a tool's files input. 3D models are described
            // (views + measurements) separately, so the composer can show the file at once and fill it in when ready.
            bridge.RegisterAsync("files.pick", async p => await PickFiles());
            bridge.RegisterAsync("files.describe", async p => (object)await FileAttachments.EntryAsync(_app.Sw, (string)p["path"]));
            bridge.RegisterAsync("files.entries", async p =>
            {
                var list = new JArray();
                foreach (var path in (p["paths"] as JArray ?? new JArray()).Select(x => (string)x))
                {
                    list.Add(await FileAttachments.EntryAsync(_app.Sw, path, describeModels: false));
                }
                return list;
            });
            bridge.FilesDropped += paths => bridge.Emit("files.dropped", new JObject { ["paths"] = new JArray(paths) });

            // "Watch me": record what the user does so the agent can turn it into a tool or workflow.
            bridge.RegisterAsync("watch.start", async p =>
            {
                var doc = DocumentFor((string)p["docKey"]);
                var model = doc?.Model ?? throw new InvalidOperationException("Open a part or assembly to record what you do in it.");
                StopWatch();
                var key = HandlerFor(doc)?.Chat.Key ?? (string)p["docKey"];
                _watch = await WatchRecorder.StartAsync(_app.Sw, model, key);
                _watch.AgentBusy = _agentRunning;
                _watch.Changed += status => _bridge?.Emit("watch.log", status);
                return _watch.Status();
            });
            bridge.RegisterAsync("watch.stop", async p =>
            {
                var w = _watch ?? throw new InvalidOperationException("Watch me isn't recording.");
                _watch = null;
                return await w.StopAsync();
            });
            bridge.Register("watch.cancel", p =>
            {
                StopWatch();
                return null;
            });
            bridge.Register("watch.status", p => _watch?.Status() ?? new JObject { ["active"] = false });

            bridge.Register("userTools.list", p => new JArray(UserTools.ListTools()));
            bridge.Register("userTools.delete", p =>
            {
                UserTools.Delete((string)p["name"]);
                return null;
            });
            bridge.Register("userTools.openFolder", p =>
            {
                Process.Start(new ProcessStartInfo(UserTools.Root) { UseShellExecute = true });
                return null;
            });
            bridge.Register("userTools.setValue", p =>
            {
                // Only the settings the user may change from the panel.
                var key = (string)p["key"];
                if (key != "intelligence" && key != "confirm") throw new InvalidOperationException("Not a tool setting: " + key);
                UserTools.SetValue((string)p["name"], key, (string)p["value"]);
                return null;
            });
            bridge.Register("userTools.setIcon", p =>
            {
                UserTools.SetIconPng((string)p["name"], Convert.FromBase64String((string)p["png"]));
                return null;
            });
            UserTools.Changed -= OnUserToolsChanged;
            UserTools.Changed += OnUserToolsChanged;
            bridge.Register("doc.activate", p =>
            {
                var model = DocumentFor((string)p["docKey"])?.Model;
                if (model != null)
                {
                    var errors = 0;
                    _app.Sw.ActivateDoc3(model.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                }
                return null;
            });
            bridge.Register("app.openExternal", p =>
            {
                ChatPaneControl.OpenExternal((string)p["url"]);
                return null;
            });
            bridge.Register("app.openLogs", p =>
            {
                Process.Start(new ProcessStartInfo(Paths.LogDir) { UseShellExecute = true });
                return null;
            });
        }

        public void SendCommand(string name, object args = null) => _bridge?.Emit("command", new JObject
        {
            ["name"] = name,
            ["args"] = args == null ? null : JToken.FromObject(args),
        });

        // ---- document lifecycle (called by ChatDocumentHandler / xCAD events) ----

        public void OnChatReadFromFile(ChatDocumentHandler handler) => _bridge?.Emit("doc.chatChanged", new JObject { ["key"] = handler.Chat.Key });

        public void OnDocumentOpened(ChatDocumentHandler handler) => _docs[handler.Chat.Key] = handler;

        public void OnDocumentClosed(ChatDocumentHandler handler)
        {
            if (handler.Chat == null) return;
            _docs.Remove(handler.Chat.Key);
            if (_selectionSource == handler) _selectionSource = null;
            if (_watch?.DocKey == handler.Chat.Key)
            {
                StopWatch();
                _bridge?.Emit("watch.ended", new JObject { ["reason"] = "The document was closed, so Watch me stopped recording." });
            }
            _bridge?.Emit("doc.closed", new JObject { ["key"] = handler.Chat.Key });
        }

        public void OnDocumentActivated(IXDocument doc)
        {
            var handler = HandlerFor(doc);
            if (handler == null) return;
            _bridge?.Emit("doc.activated", DocInfo(handler));
            EmitSelection(handler);
        }

        public void OnDocumentSaved(ChatDocumentHandler handler)
        {
            // Title/path change after Save As; refresh once SolidWorks has finished the save.
            var t = new Timer { Interval = 700 };
            t.Tick += (s, e) =>
            {
                t.Stop();
                t.Dispose();
                if (_docs.ContainsKey(handler.Chat.Key)) _bridge?.Emit("doc.updated", DocInfo(handler));
            };
            t.Start();
        }

        public void OnSelectionChanged(ChatDocumentHandler handler)
        {
            _selectionSource = handler;
            _selectionTimer.Stop();
            _selectionTimer.Start();
        }

        // ---- helpers ----

        // ---- tools (ribbon buttons / hotkeys) ----

        /// <summary>Run one of the user's Tools (ribbon button / hotkey); see ToolRunner.</summary>
        public Task RunToolAsync(string name, Action showPane) =>
            (_toolRunner ?? (_toolRunner = new ToolRunner(_app, _tools, _scripts, SendCommand, Status))).RunAsync(name, showPane);

        private ToolRunner _toolRunner;

        private void Status(string text)
        {
            try
            {
                ((IFrame)_app.Sw.Frame())?.SetStatusBarText(text);
            }
            catch
            {
                // Cosmetic only.
            }
        }

        private void StopWatch()
        {
            _watch?.Dispose();
            _watch = null;
        }

        /// <summary>Open-file dialog for chat attachments; returns entries (3D models still to be described).</summary>
        private async Task<object> PickFiles()
        {
            string[] paths;
            using (var dlg = new OpenFileDialog
            {
                Title = "Attach files for the agent",
                Multiselect = true,
                Filter = "Images, PDFs and 3D models|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf;*.stl;*.3mf;*.obj;*.step;*.stp;*.iges;*.igs;*.x_t;*.x_b;*.sldprt;*.sldasm|" +
                         "3D models (*.stl;*.3mf;*.obj;*.step;*.stp;*.iges;*.igs;*.x_t;*.x_b;*.sldprt;*.sldasm)|*.stl;*.3mf;*.obj;*.step;*.stp;*.iges;*.igs;*.x_t;*.x_b;*.sldprt;*.sldasm|" +
                         "Images (*.png;*.jpg;*.jpeg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp|PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            })
            {
                var owner = new Win32Window(new IntPtr(((IFrame)_app.Sw.Frame()).GetHWndx64()));
                if (dlg.ShowDialog(owner) != DialogResult.OK) return new JArray();
                paths = dlg.FileNames;
            }
            var list = new JArray();
            foreach (var path in paths) list.Add(await FileAttachments.EntryAsync(_app.Sw, path, describeModels: false));
            return list;
        }

        private sealed class Win32Window : IWin32Window
        {
            public Win32Window(IntPtr handle) => Handle = handle;

            public IntPtr Handle { get; }
        }

        // ---- updates ----

        /// <summary>Open documents with unsaved changes; those never saved can't be saved without asking for a name.</summary>
        private JArray UnsavedDocuments()
        {
            var list = new JArray();
            foreach (var doc in _app.Documents)
            {
                try
                {
                    var model = ((Xarial.XCad.SolidWorks.Documents.ISwDocument)doc).Model;
                    if (model == null || !model.GetSaveFlag()) continue;
                    var path = model.GetPathName();
                    list.Add(new JObject { ["title"] = model.GetTitle(), ["path"] = path, ["untitled"] = string.IsNullOrEmpty(path) });
                }
                catch
                {
                    // A document closing mid-enumeration; ignore.
                }
            }
            return list;
        }

        /// <summary>
        /// Download (if needed), save open documents, start the install helper and close SolidWorks; the helper
        /// installs and starts SolidWorks again. Returns { ok } or { ok: false, reason, unsaved }.
        /// </summary>
        private async Task<JObject> RestartAndInstall(bool saveDocuments)
        {
            if (Updates.Status == Updater.State.Available && !await Updates.DownloadAsync())
                return new JObject { ["ok"] = false, ["reason"] = Updates.Error };
            if (Updates.Status != Updater.State.Ready)
                return new JObject { ["ok"] = false, ["reason"] = Updates.Error ?? "No update is ready to install." };

            var unsaved = UnsavedDocuments();
            if (unsaved.Any(d => (bool)d["untitled"]))
                return new JObject { ["ok"] = false, ["reason"] = "Some documents have never been saved. Save them (File > Save As) first.", ["unsaved"] = unsaved };
            if (unsaved.Count > 0 && !saveDocuments)
                return new JObject { ["ok"] = false, ["reason"] = "unsaved", ["unsaved"] = unsaved };

            foreach (var d in unsaved)
            {
                var model = (IModelDoc2)_app.Sw.GetOpenDocumentByName((string)d["path"]);
                int errors = 0, warnings = 0;
                if (model != null && !model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                    return new JObject { ["ok"] = false, ["reason"] = $"Couldn't save {d["title"]} ({(swFileSaveError_e)errors}). Save it yourself, then try again." };
            }

            if (!Updates.LaunchInstaller(restartSolidWorks: true))
                return new JObject { ["ok"] = false, ["reason"] = Updates.Error };
            UpdateHelperStarted = true;

            // Close SolidWorks after this reply has reached the panel.
            var exit = new Timer { Interval = 500 };
            exit.Tick += (s, e) =>
            {
                exit.Stop();
                Log.Info("Closing SolidWorks to install the update");
                _app.Sw.ExitApp();
            };
            exit.Start();
            return new JObject { ["ok"] = true };
        }

        /// <summary>
        /// Save open documents and restart SolidWorks (new tools only reach the ribbon at start-up). The active
        /// document is reopened. Returns { ok } or { ok: false, reason, unsaved } like RestartAndInstall.
        /// </summary>
        private Task<JObject> SaveAndRestart(bool saveDocuments)
        {
            var unsaved = UnsavedDocuments();
            if (unsaved.Any(d => (bool)d["untitled"]))
                return Task.FromResult(new JObject { ["ok"] = false, ["reason"] = "Some documents have never been saved. Save them (File > Save As) first.", ["unsaved"] = unsaved });
            if (unsaved.Count > 0 && !saveDocuments)
                return Task.FromResult(new JObject { ["ok"] = false, ["reason"] = "unsaved", ["unsaved"] = unsaved });

            foreach (var d in unsaved)
            {
                var model = (IModelDoc2)_app.Sw.GetOpenDocumentByName((string)d["path"]);
                int errors = 0, warnings = 0;
                if (model != null && !model.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                    return Task.FromResult(new JObject { ["ok"] = false, ["reason"] = $"Couldn't save {d["title"]} ({(swFileSaveError_e)errors}). Save it yourself, then try again." });
            }

            string reopen = null;
            try
            {
                reopen = ((IModelDoc2)_app.Sw.ActiveDoc)?.GetPathName();
            }
            catch
            {
                // Nothing to reopen.
            }
            if (!RestartHelper.Launch(reopen)) return Task.FromResult(new JObject { ["ok"] = false, ["reason"] = "Couldn't start the restart helper." });

            var exit = new Timer { Interval = 500 };
            exit.Tick += (s, e) =>
            {
                exit.Stop();
                Log.Info("Restarting SolidWorks");
                _app.Sw.ExitApp();
            };
            exit.Start();
            return Task.FromResult(new JObject { ["ok"] = true });
        }

        /// <summary>At shutdown: with auto-update on, a downloaded update installs now (without restarting SolidWorks).</summary>
        public void InstallPendingUpdateOnExit()
        {
            if (UpdateHelperStarted || !AutoUpdate || Updates.Status != Updater.State.Ready) return;
            UpdateHelperStarted = Updates.LaunchInstaller(restartSolidWorks: false);
        }

        private JObject GetState()
        {
            var active = HandlerFor(_app.Documents.Active);
            return new JObject
            {
                ["version"] = Updates.CurrentVersion.ToString(3),
                ["update"] = Updates.ToJson(),
                ["crash"] = Crash,
                ["ribbonStale"] = RibbonStale?.Invoke() ?? false,
                ["solidworksVersion"] = SafeRevision(),
                ["apiKeySource"] = SettingsStore.ApiKeySource(),
                ["settings"] = _settings,
                ["scratchKey"] = ChatDocument.ScratchKey,
                ["activeDoc"] = active == null ? null : DocInfo(active),
                ["docs"] = new JArray(_docs.Values.Where(IsVisible).Select(DocInfo)),
                ["logFile"] = Log.CurrentFile,
                ["swTheme"] = ReadTheme(),
            };
        }

        private string SafeRevision()
        {
            try
            {
                return _app.Sw.RevisionNumber();
            }
            catch
            {
                return null;
            }
        }

        private ChatDocument ResolveChat(string docKey)
        {
            if (string.IsNullOrEmpty(docKey) || docKey == ChatDocument.ScratchKey) return _scratch;
            if (_docs.TryGetValue(docKey, out var h)) return h.Chat;
            throw new InvalidOperationException("That document is no longer open.");
        }

        /// <summary>The document tools act on: the chat's own document, or the active one for a scratch chat.</summary>
        private ISwDocument DocumentFor(string docKey)
        {
            if (string.IsNullOrEmpty(docKey) || docKey == ChatDocument.ScratchKey) return _app.Documents.Active;
            if (_docs.TryGetValue(docKey, out var h)) return h.Chat.Doc;
            throw new InvalidOperationException("The document for this chat has been closed.");
        }

        private ChatDocumentHandler HandlerFor(IXDocument doc)
        {
            if (doc == null) return null;
            try
            {
                return _app.Documents.GetHandler<ChatDocumentHandler>(doc);
            }
            catch
            {
                return _docs.Values.FirstOrDefault(h => h.Chat.Doc.Equals(doc));
            }
        }

        private static bool IsVisible(ChatDocumentHandler h)
        {
            try
            {
                return h.Chat.Doc.Model.Visible;
            }
            catch
            {
                return false;
            }
        }

        private static JObject DocInfo(ChatDocumentHandler h)
        {
            var info = new JObject { ["key"] = h.Chat.Key };
            try
            {
                var model = h.Chat.Doc.Model;
                info["title"] = model.GetTitle();
                info["path"] = model.GetPathName();
                info["type"] = (swDocumentTypes_e)model.GetType() == swDocumentTypes_e.swDocPART ? "part"
                    : (swDocumentTypes_e)model.GetType() == swDocumentTypes_e.swDocASSEMBLY ? "assembly"
                    : (swDocumentTypes_e)model.GetType() == swDocumentTypes_e.swDocDRAWING ? "drawing" : "other";
            }
            catch (Exception ex)
            {
                info["title"] = "(unavailable)";
                Log.Warn("DocInfo failed: " + ex.Message);
            }
            return info;
        }

        private void EmitSelection(ChatDocumentHandler handler) => _bridge?.Emit("selection.changed", SelectionPayload(handler));

        private static JObject SelectionPayload(ChatDocumentHandler h)
        {
            var items = new JArray();
            try
            {
                foreach (var (type, label) in ModelTools.SelectionChips(h.Chat.Doc.Model, 30))
                {
                    items.Add(new JObject { ["type"] = type, ["label"] = label });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Selection summary failed: " + ex.Message);
            }
            return new JObject { ["docKey"] = h.Chat.Key, ["items"] = items };
        }

        /// <summary>Raised when the SolidWorks interface theme or panel colour changes.</summary>
        public event Action<JObject> ThemeChanged;

        /// <summary>
        /// The SolidWorks interface theme (swInterfaceBrightnessTheme_e: 0 Light, 1 Medium, 2 Dark, 3 Medium Light)
        /// and the FeatureManager / PropertyManager background colours as #RRGGBB.
        /// </summary>
        public JObject ReadTheme()
        {
            try
            {
                var sw = _app.Sw;
                return new JObject
                {
                    ["theme"] = sw.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swSystemColorsBackground),
                    ["treeBg"] = ToHex(sw.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swSystemColorsTreeViewBackground)),
                    ["pmBg"] = ToHex(sw.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swPropertyManagerColorBackground)),
                };
            }
            catch (Exception ex)
            {
                Log.Warn("Reading the SolidWorks theme failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>COLORREF (0x00BBGGRR) to #RRGGBB.</summary>
        internal static string ToHex(int colorRef) => $"#{colorRef & 0xFF:X2}{(colorRef >> 8) & 0xFF:X2}{(colorRef >> 16) & 0xFF:X2}";

        private void OnUserToolsChanged()
        {
            _bridge?.Emit("userTools.changed", new JArray(UserTools.ListTools()));
            _bridge?.Emit("ribbon.stale", new JObject { ["stale"] = RibbonStale?.Invoke() ?? false });
        }

        private object StartMarkup(string docKey, string tool, string color)
        {
            var model = DocumentFor(docKey)?.Model ?? throw new InvalidOperationException("Open a document to draw on its view.");
            if (!ReferenceEquals(_app.Sw.ActiveDoc, model))
            {
                var errors = 0;
                _app.Sw.ActivateDoc3(model.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
            }
            var view = model.ActiveView as IModelView ?? throw new InvalidOperationException("The document has no view to draw on.");
            CloseMarkup();
            _markupView = new IntPtr(view.GetViewHWndx64());
            var owner = new IntPtr(((IFrame)_app.Sw.Frame()).GetHWndx64());
            _markup = new MarkupOverlay(_markupView, owner);
            if (Enum.TryParse(tool, out MarkupOverlay.Tool t)) _markup.CurrentTool = t;
            if (!string.IsNullOrEmpty(color)) _markup.CurrentColor = System.Drawing.ColorTranslator.FromHtml(color);
            _markup.KeyCommand += cmd => _bridge?.Emit("markup.key", new JObject { ["command"] = cmd });
            _markup.StrokesChanged += () => _bridge?.Emit("markup.strokes", new JObject { ["count"] = _markup?.StrokeCount ?? 0 });
            _markup.Open();
            return new JObject { ["active"] = true };
        }

        /// <summary>Capture the view, paint the user's strokes on top, return it as a JPEG for the chat.</summary>
        private object FinishMarkup()
        {
            if (_markup == null) return null;
            try
            {
                _markup.Hide(); // so a screen-copy fallback never picks up the overlay's own hint/frame
                using (var shot = ViewTools.CaptureWindow(_markupView))
                {
                    using (var g = System.Drawing.Graphics.FromImage(shot))
                    {
                        var scale = _markup.ViewSize.Width > 0 ? shot.Width / (float)_markup.ViewSize.Width : 1f;
                        _markup.PaintStrokes(g, scale);
                    }
                    using (var fitted = ViewTools.Fit(shot, 1280))
                    {
                        return new JObject
                        {
                            ["mediaType"] = "image/jpeg",
                            ["data"] = Convert.ToBase64String(ViewTools.EncodeJpeg(fitted, 85)),
                            ["strokes"] = _markup.StrokeCount,
                        };
                    }
                }
            }
            finally
            {
                CloseMarkup();
            }
        }

        private void CloseMarkup()
        {
            if (_markup == null) return;
            try
            {
                _markup.Close();
                _markup.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Closing markup failed: " + ex.Message);
            }
            _markup = null;
        }

        public void Dispose()
        {
            StopWatch();
            CloseMarkup();
            _themeTimer.Dispose();
            _activeTimer.Dispose();
            _updateTimer.Dispose();
            _selectionTimer.Dispose();
        }
    }
}

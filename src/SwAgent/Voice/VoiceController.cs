using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.Tools;
using SwAgent.UI;
using Xarial.XCad.SolidWorks;
using Timer = System.Windows.Forms.Timer;

namespace SwAgent.Voice
{
    /// <summary>
    /// Voice messages and the markup pen.
    ///
    /// Push-to-talk: hold the talk key (default `) while SolidWorks is in front and speak. The words appear in the panel
    /// as they are recognised (local Whisper, see SpeechEngine), and everything the user does meanwhile is put on the
    /// same clock: what they select, features they add, the view each time it settles, markup frames. Letting go sends
    /// it all as one message. A short tap of the key still types it.
    ///
    /// The pen (MarkupOverlay) stays on while the user turns the model; each view they mark becomes a frame. Esc ends
    /// it. If the user is talking, the frames go into the voice message, and the message waits for Esc.
    ///
    /// Runs on the SolidWorks UI thread; the input hooks only record and post. No geometry is read from timers: the
    /// view is watched through window captures (PrintWindow), never through the SolidWorks API.
    /// </summary>
    internal sealed class VoiceController : IDisposable
    {
        private const int TapMs = 220;
        private const int VkEscape = 0x1B, VkReturn = 0x0D, VkZ = 0x5A, VkControl = 0x11, VkMenu = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;

        private readonly ISwApplication _app;
        private readonly Func<JObject> _settings;
        private readonly Func<IModelDoc2, string> _chatKey;
        private readonly Control _ui = new Control();
        private readonly InputHooks _hooks = new InputHooks();
        private readonly SpeechEngine _engine = new SpeechEngine();
        private readonly Speaker _speaker = new Speaker();
        private readonly Timer _holdTimer = new Timer { Interval = TapMs };
        private readonly Timer _viewTimer = new Timer { Interval = 800 };
        private Action<string, JObject> _emit = (n, d) => { };

        // Push-to-talk state. _keyHeld is set by the hook (at once); the rest happens in posted calls.
        private string _talkKey = "vk:192";
        private volatile bool _keyHeld;
        private DateTime _pressedAt;
        private bool _holdConfirmed;
        private int _recording; // id of the recording for the current press, 0 if none
        private int _nextId;
        private VoiceSession _session;
        private object _sessionModel; // the model whose events the session listens to
        private byte[] _pendingThumb;

        // Download of a speech model.
        private CancellationTokenSource _download;
        private string _downloadId;
        private double _downloadProgress;
        private string _downloadError;

        // The markup pen.
        private MarkupOverlay _markup;
        private IntPtr _markupView;
        private readonly List<MarkupOverlay.Frame> _framesInVoice = new List<MarkupOverlay.Frame>();

        public VoiceController(ISwApplication app, Func<JObject> settings, Func<IModelDoc2, string> chatKey)
        {
            _app = app;
            _settings = settings;
            _chatKey = chatKey;
            _ui.CreateControl();
            var _ = _ui.Handle;
            _hooks.Key = OnKey;
            _hooks.Mouse = OnMouse;
            _holdTimer.Tick += Safe.Tick("Talk key hold", OnHoldConfirmed);
            _viewTimer.Tick += Safe.Tick("Voice view watch", WatchView);
            _engine.Changed += () => EmitStatus();
            _engine.Level += (id, v) =>
            {
                if (id == _recording) _emit("voice.level", new JObject { ["v"] = v });
            };
            _engine.Partial += OnPartial;
            _engine.Final += OnFinal;
            _engine.Failed += OnFailed;
            ApplySettings();
        }

        /// <summary>The user turned voice on (the panel only allows that once a speech model is downloaded).</summary>
        public bool Enabled => (bool?)_settings()["voiceEnabled"] == true;

        /// <summary>On and a speech model is downloaded: only then does the talk key stop typing. Cached for the hooks.</summary>
        private volatile bool _ready;
        private string ModelId => (string)_settings()["voiceModel"] ?? SpeechModels.DefaultId;

        public void AttachBridge(WebBridge bridge)
        {
            _emit = (name, data) => bridge.Emit(name, data);
            bridge.Register("voice.status", p => Status());
            bridge.Register("voice.download", p =>
            {
                StartDownload((string)p["id"] ?? SpeechModels.DefaultId);
                return Status();
            });
            bridge.Register("voice.cancelDownload", p =>
            {
                _download?.Cancel();
                return null;
            });
            bridge.Register("voice.deleteModel", p =>
            {
                var id = (string)p["id"];
                if (id == ModelId) _engine.Shutdown(); // a loaded model can't be deleted; the helper restarts on the next press
                SpeechModels.Delete(id);
                ApplySettings();
                return Status();
            });
            bridge.Register("voice.cancel", p =>
            {
                CancelSession("Voice message discarded.");
                return null;
            });
            bridge.Register("voice.speak", p =>
            {
                var s = _settings();
                _speaker.Speak((string)p["text"], (string)s["speakVoice"], (double?)s["speakRate"] ?? 0);
                return null;
            });
            bridge.Register("voice.stopSpeaking", p =>
            {
                _speaker.Stop();
                return null;
            });
            bridge.Register("voice.voices", p => Speaker.Voices());
            bridge.Register("markup.start", p => StartMarkup((string)p["tool"], (string)p["color"]));
            bridge.Register("markup.set", p =>
            {
                if (_markup == null) return null;
                if (Enum.TryParse((string)p["tool"], out MarkupOverlay.Tool tool)) _markup.CurrentTool = tool;
                if (!string.IsNullOrEmpty((string)p["color"])) _markup.CurrentColor = ColorTranslator.FromHtml((string)p["color"]);
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
            EmitStatus();
        }

        /// <summary>Settings changed (or start-up): the talk key, and warm up the speech model if voice is on.</summary>
        public void ApplySettings()
        {
            _talkKey = (string)_settings()["voiceKey"] ?? "vk:192";
            _ready = Enabled && SpeechModels.Installed(ModelId) && System.IO.File.Exists(SpeechEngine.ExePath);
            UpdateHooks();
            if (Enabled && SpeechModels.Installed(ModelId))
            {
                // Load the model in the background now, so the first press doesn't wait for it.
                var warm = new Timer { Interval = 4000 };
                warm.Tick += Safe.Tick("Warm up speech", () =>
                {
                    warm.Stop();
                    warm.Dispose();
                    if (Enabled) _engine.Ensure(ModelId);
                });
                warm.Start();
            }
            EmitStatus();
        }

        private void UpdateHooks()
        {
            var mouseKey = _ready && _talkKey.StartsWith("mouse:");
            _hooks.EnableKeyboard(_ready || _markup != null);
            _hooks.EnableMouse(mouseKey || _markup != null);
        }

        // ---------------------------------------------------------------- input (hook callbacks: decide and post)

        private bool OnKey(int vk, bool down, bool injected)
        {
            if (injected) return false;

            // The pen: Esc / Enter finish, Ctrl+Z undoes a stroke, arrow keys turn the view (end the page).
            if (_markup != null && InputHooks.SolidWorksInFront())
            {
                if (vk == VkEscape || vk == VkReturn)
                {
                    if (down && !_keyHeld) Post(() => _emit("markup.key", new JObject { ["command"] = "finish" }));
                    else if (down) Post(() => CancelSession("Voice message discarded.")); // Esc while talking: cancel the message
                    return true;
                }
                if (vk == VkZ && InputHooks.IsDown(VkControl))
                {
                    if (down) Post(() => _markup?.Undo());
                    return true;
                }
                if (down && vk >= 0x25 && vk <= 0x28) Post(() => _markup?.ViewMayChange());
            }

            if (!_ready) return false;
            if (vk == VkEscape && down && _keyHeld)
            {
                Post(() => CancelSession("Voice message discarded."));
                return true;
            }
            if (!_talkKey.StartsWith("vk:") || vk != ParseVk(_talkKey)) return false;
            return TalkKey(down, vk);
        }

        private bool OnMouse(int msg, Point pt, int button)
        {
            var markup = _markup; // read once: this runs on the hook thread
            if (markup != null && InputHooks.SolidWorksInFront() && markup.Hook(msg, pt)) return true;
            if (!_ready || !_talkKey.StartsWith("mouse:")) return false;
            if (msg != InputHooks.WmXButtonDown && msg != InputHooks.WmXButtonUp) return false;
            if (button != (_talkKey == "mouse:x2" ? 2 : 1)) return false;
            return TalkKey(msg == InputHooks.WmXButtonDown, 0);
        }

        private bool TalkKey(bool down, int vk)
        {
            if (down)
            {
                if (_keyHeld) return true; // auto-repeat
                if (!InputHooks.SolidWorksInFront()) return false;
                if (vk != 0 && !IsModifierKey(vk) && (InputHooks.IsDown(VkControl) || InputHooks.IsDown(VkMenu) || InputHooks.IsDown(VkLWin) || InputHooks.IsDown(VkRWin)))
                    return false; // a shortcut with the key, not push-to-talk
                _keyHeld = true;
                var at = DateTime.Now;
                Post(() => TalkDown(at));
                return true;
            }
            if (!_keyHeld) return false;
            _keyHeld = false;
            var released = DateTime.Now;
            Post(() => TalkUp(released));
            return true;
        }

        private static bool IsModifierKey(int vk) => vk == 0xA2 || vk == 0xA3 || vk == 0xA4 || vk == 0xA5 || vk == VkControl || vk == VkMenu;

        private static int ParseVk(string key) => int.TryParse(key.Substring(3), out var v) ? v : 192;

        private void Post(Action a)
        {
            try
            {
                _ui.BeginInvoke((MethodInvoker)(() => Safe.Run("Voice input", a)));
            }
            catch (InvalidOperationException)
            {
                // Shutting down.
            }
        }

        // ---------------------------------------------------------------- push-to-talk

        private void TalkDown(DateTime pressed)
        {
            _speaker.Stop(); // talking over a spoken reply stops it
            _pressedAt = pressed;
            _holdConfirmed = false;
            if (!SpeechModels.Installed(ModelId))
            {
                _emit("voice.notice", new JObject { ["text"] = "Push-to-talk needs a speech model: download one in Settings > Voice." });
                _recording = 0;
                return;
            }
            _engine.Ensure(ModelId);
            _recording = ++_nextId;
            _engine.Start(_recording, Vocabulary());
            _holdTimer.Stop();
            _holdTimer.Start();
        }

        /// <summary>The key is still down after a tap's length: this is talking, not typing.</summary>
        private void OnHoldConfirmed()
        {
            _holdTimer.Stop();
            if (!_keyHeld || _recording == 0 || _holdConfirmed) return;
            _holdConfirmed = true;
            if (_session == null) BeginSession();
            _session.AddRecording(_recording, _pressedAt);
            EmitState("listening");
        }

        private void TalkUp(DateTime released)
        {
            _holdTimer.Stop();
            var id = _recording;
            _recording = 0;
            if (id == 0) return;
            if (!_holdConfirmed)
            {
                if ((released - _pressedAt).TotalMilliseconds < TapMs)
                {
                    // A tap: give the key back to whatever has focus.
                    _engine.Cancel(id);
                    if (_talkKey.StartsWith("vk:")) InputHooks.Replay(ParseVk(_talkKey));
                    return;
                }
                // Held long enough, but SolidWorks was busy and the hold timer hadn't run yet.
                _holdConfirmed = true;
                if (_session == null) BeginSession();
                _session.AddRecording(id, _pressedAt);
            }
            _engine.Stop(id);
            EmitState("transcribing");
        }

        private void OnPartial(int id, JArray segments)
        {
            if (_session == null || !_session.Owns(id)) return;
            _session.SetPartial(id, segments);
            _emit("voice.partial", new JObject { ["text"] = _session.Text });
        }

        private void OnFinal(int id, JArray segments)
        {
            if (_session == null || !_session.Owns(id)) return;
            _session.SetFinal(id, segments);
            _emit("voice.partial", new JObject { ["text"] = _session.Text });
            MaybeCommit();
        }

        private void OnFailed(int id, string message)
        {
            _emit("voice.notice", new JObject { ["text"] = message });
            if (_session == null || !_session.Owns(id)) return;
            _session.SetFinal(id, new JArray());
            MaybeCommit();
        }

        /// <summary>Send once nothing is still being said, transcribed or drawn.</summary>
        private void MaybeCommit()
        {
            if (_session == null || _keyHeld || _recording != 0 || _session.Pending.Count > 0) return;
            if (_markup != null)
            {
                EmitState("pen"); // the message waits for the markup (Esc)
                return;
            }
            Commit();
        }

        // ---------------------------------------------------------------- the session (what happens while talking)

        private void BeginSession()
        {
            IModelDoc2 model = null;
            IntPtr hwnd = IntPtr.Zero;
            try
            {
                model = _app.Sw.ActiveDoc as IModelDoc2;
                if (model?.ActiveView is IModelView view) hwnd = new IntPtr(view.GetViewHWndx64());
            }
            catch (Exception ex)
            {
                Log.Warn("Voice: reading the active document failed: " + ex.Message);
            }
            _session = new VoiceSession(model == null ? null : _chatKey(model), hwnd, _pressedAt);
            _pendingThumb = null;
            if (model != null)
            {
                Attach(model);
                RecordSelection(model, initial: true);
                RecordSketch(model);
            }
            // Markup frames drawn before the user started talking belong to this message too.
            if (_markup != null)
            {
                foreach (var f in _markup.Frames.Where(f => !_framesInVoice.Contains(f))) AddFrame(f);
            }
            if (hwnd != IntPtr.Zero && (bool?)_settings()["voiceViews"] != false)
            {
                CaptureView("view", force: true);
                _viewTimer.Start();
            }
        }

        private void Commit()
        {
            var s = _session;
            EndSession();
            if (s == null) return;
            if (!s.HasSpeech && !s.HasActions)
            {
                EmitState("idle");
                _emit("voice.notice", new JObject { ["text"] = "Didn't catch any words - hold the key a little longer while you speak." });
                return;
            }
            var json = s.ToJson();
            Log.Info($"Voice message: {json["durationMs"]} ms, {((JArray)json["segments"]).Count} segments, {((JArray)json["events"]).Count} events");
            _emit("voice.final", json);
            EmitState("idle");
        }

        private void CancelSession(string notice)
        {
            if (_recording != 0) _engine.Cancel(_recording);
            _recording = 0;
            _keyHeld = false;
            _holdTimer.Stop();
            if (_session == null) return;
            foreach (var id in _session.Pending.ToList()) _engine.Cancel(id);
            EndSession();
            EmitState("idle");
            if (notice != null) _emit("voice.notice", new JObject { ["text"] = notice });
        }

        private void EndSession()
        {
            _viewTimer.Stop();
            Detach();
            _session = null;
        }

        /// <summary>
        /// The view, captured from the window (no SolidWorks calls): kept once it has stopped changing for two checks and
        /// looks different from the last one kept - a turned model, a new feature, a sketch.
        /// </summary>
        private void WatchView()
        {
            if (_session == null || _session.ViewHwnd == IntPtr.Zero) return;
            CaptureView("view", force: false);
        }

        private void CaptureView(string kind, bool force)
        {
            var s = _session;
            if (s == null) return;
            // While the pen is on and the user is drawing, the markup frames show the view.
            if (!force && _markup != null && _markup.StrokeCount > 0) return;
            Bitmap shot;
            try
            {
                shot = ViewTools.CaptureWindow(s.ViewHwnd);
            }
            catch
            {
                return; // minimized / too small
            }
            using (shot)
            {
                var thumb = Thumb(shot);
                if (!force)
                {
                    // Wait for the view to settle: the same picture twice in a row.
                    var settled = _pendingThumb != null && Diff(_pendingThumb, thumb) < 2.5;
                    _pendingThumb = thumb;
                    if (!settled) return;
                    if (s.LastViewThumb != null && Diff(s.LastViewThumb, thumb) < 6 && !s.ChangedSinceView) return;
                    if (s.LastViewThumb != null && Diff(s.LastViewThumb, thumb) < 1.5) return;
                    if (s.Now - s.LastViewAt < 1200) return;
                }
                s.LastViewThumb = thumb;
                _pendingThumb = thumb;
                using (var fitted = ViewTools.Fit(shot, 1024))
                {
                    var n = s.ImageCount + 1;
                    var e = s.Add(kind, n == 1 ? "View" : $"View {n}", new JObject
                    {
                        ["image"] = Convert.ToBase64String(ViewTools.EncodeJpeg(fitted, 80)),
                        ["mediaType"] = "image/jpeg",
                    });
                    EmitEvent(e);
                }
            }
        }

        private void AddFrame(MarkupOverlay.Frame f)
        {
            if (_session == null) return;
            _framesInVoice.Add(f);
            var count = _framesInVoice.Count;
            var e = _session.Add("markup", $"Markup {count}", new JObject { ["image"] = f.Data, ["mediaType"] = f.MediaType, ["strokes"] = f.Strokes }, _session.At(f.At));
            EmitEvent(e);
        }

        /// <summary>From AgentController when the selection changed (debounced, between commands).</summary>
        public void OnSelectionChanged(IModelDoc2 model)
        {
            if (_session == null || model == null || !ReferenceEquals(model, _sessionModel)) return;
            RecordSelection(model, initial: false);
            RecordSketch(model);
        }

        private void RecordSelection(IModelDoc2 model, bool initial)
        {
            JArray items;
            try
            {
                items = (JArray)SelectionTools.Snapshot(_app.Sw, model, 20)["items"];
            }
            catch (Exception ex)
            {
                Log.Warn("Voice: reading the selection failed: " + ex.Message);
                return;
            }
            string Sig(JToken i) => $"{i["type"]}|{i["label"]}|{i["detail"]}";
            var sig = string.Join(";", items.Select(Sig));
            if (sig == _session.LastSelectionSig) return;
            var before = new HashSet<string>(_session.LastSelectionSig.Split(';'));
            _session.LastSelectionSig = sig;
            if (items.Count == 0)
            {
                if (!initial) EmitEvent(_session.Add("deselect", "Cleared the selection"));
                return;
            }
            var added = initial ? items.ToList() : items.Where(i => !before.Contains(Sig(i))).ToList();
            if (added.Count == 0) return;
            var first = added[0];
            var summary = (initial ? "Already selected: " : "Selected ") + Describe(first) + (added.Count > 1 ? $" +{added.Count - 1}" : "");
            EmitEvent(_session.Add("select", summary, new JObject { ["items"] = new JArray(added), ["total"] = items.Count }, initial ? 0 : (int?)null));
        }

        private static string Describe(JToken item)
        {
            var type = (string)item["type"] ?? "Entity";
            var label = (string)item["label"];
            return string.IsNullOrEmpty(label) ? type : $"{type} of {label}";
        }

        private void RecordSketch(IModelDoc2 model)
        {
            string name = null;
            try
            {
                if (model.SketchManager?.ActiveSketch is IFeature f) name = f.Name;
                else if (model.SketchManager?.ActiveSketch != null) name = "a sketch";
            }
            catch
            {
                return;
            }
            if (name == _session.ActiveSketch) return;
            var was = _session.ActiveSketch;
            _session.ActiveSketch = name;
            if (name != null) EmitEvent(_session.Add("sketch", $"Editing {name}", new JObject { ["name"] = name }));
            else if (was != null) EmitEvent(_session.Add("sketch", $"Closed {was}", new JObject { ["closed"] = was }));
        }

        private void Attach(IModelDoc2 model)
        {
            Detach();
            try
            {
                switch (model)
                {
                    case PartDoc part:
                        part.AddItemNotify += OnAddItem;
                        part.DeleteItemNotify += OnDeleteItem;
                        part.RenameItemNotify += OnRenameItem;
                        break;
                    case AssemblyDoc asm:
                        asm.AddItemNotify += OnAddItem;
                        asm.DeleteItemNotify += OnDeleteItem;
                        asm.RenameItemNotify += OnRenameItem;
                        break;
                    default:
                        return;
                }
                _sessionModel = model;
            }
            catch (Exception ex)
            {
                Log.Warn("Voice: watching the document failed: " + ex.Message);
            }
        }

        private void Detach()
        {
            try
            {
                switch (_sessionModel)
                {
                    case PartDoc part:
                        part.AddItemNotify -= OnAddItem;
                        part.DeleteItemNotify -= OnDeleteItem;
                        part.RenameItemNotify -= OnRenameItem;
                        break;
                    case AssemblyDoc asm:
                        asm.AddItemNotify -= OnAddItem;
                        asm.DeleteItemNotify -= OnDeleteItem;
                        asm.RenameItemNotify -= OnRenameItem;
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Voice: detaching document events failed: " + ex.Message);
            }
            _sessionModel = null;
        }

        // SolidWorks raises these itself: record only (an exception escaping would close SolidWorks).
        private int OnAddItem(int type, string name)
        {
            Safe.Run("Voice: item added", () =>
            {
                if (_session == null || !Interesting(type)) return;
                EmitEvent(_session.Add("added", $"Added {name}", new JObject { ["name"] = name, ["what"] = What(type) }));
            });
            return 0;
        }

        private int OnDeleteItem(int type, string name)
        {
            Safe.Run("Voice: item deleted", () =>
            {
                if (_session == null || !Interesting(type)) return;
                EmitEvent(_session.Add("deleted", $"Deleted {name}", new JObject { ["name"] = name, ["what"] = What(type) }));
            });
            return 0;
        }

        private int OnRenameItem(int type, string oldName, string newName)
        {
            Safe.Run("Voice: item renamed", () =>
            {
                if (_session == null || !Interesting(type)) return;
                EmitEvent(_session.Add("renamed", $"Renamed {oldName} to {newName}", new JObject { ["name"] = newName, ["was"] = oldName }));
            });
            return 0;
        }

        private static bool Interesting(int type) =>
            type == (int)swNotifyEntityType_e.swNotifyFeature || type == (int)swNotifyEntityType_e.swNotifyComponent ||
            type == (int)swNotifyEntityType_e.swNotifyConfiguration;

        private static string What(int type) =>
            type == (int)swNotifyEntityType_e.swNotifyComponent ? "component" : type == (int)swNotifyEntityType_e.swNotifyConfiguration ? "configuration" : "feature";

        /// <summary>
        /// Words Whisper should expect: CAD vocabulary plus the names in the active document's feature tree, so "make
        /// Cut-Extrude3 deeper" comes out right.
        /// </summary>
        private string Vocabulary()
        {
            var names = new List<string>();
            try
            {
                if (_app.Sw.ActiveDoc is IModelDoc2 model)
                {
                    var f = model.FirstFeature() as IFeature;
                    var guard = 0;
                    while (f != null && guard++ < 2000 && names.Count < 60)
                    {
                        var type = f.GetTypeName2();
                        if (!type.EndsWith("Folder") && type != "MaterialFolder" && type != "HistoryFolder" && type != "OriginProfileFeature")
                            names.Add(f.Name);
                        f = f.GetNextFeature() as IFeature;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Voice: reading feature names failed: " + ex.Message);
            }
            var text = "SolidWorks CAD. Extrude, boss, cut, revolve, sweep, loft, fillet, chamfer, draft, shell, rib, hole wizard, " +
                       "mirror, linear pattern, sketch, plane, mate, assembly, part, dimension, face, edge, vertex, body, parting line, " +
                       "core, cavity, ejector, gate, runner." + (names.Count > 0 ? " Features: " + string.Join(", ", names.Distinct()) + "." : "");
            return text.Length > 800 ? text.Substring(0, 800) : text;
        }

        private static byte[] Thumb(Bitmap bmp)
        {
            const int w = 40, h = 30;
            var result = new byte[w * h];
            using (var small = new Bitmap(w, h))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.DrawImage(bmp, 0, 0, w, h);
                }
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var c = small.GetPixel(x, y);
                    result[y * w + x] = (byte)((c.R * 3 + c.G * 6 + c.B) / 10);
                }
            }
            return result;
        }

        private static double Diff(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 255;
            double sum = 0;
            for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
            return sum / a.Length;
        }

        // ---------------------------------------------------------------- the pen

        private object StartMarkup(string tool, string color)
        {
            var model = _app.Sw.ActiveDoc as IModelDoc2 ?? throw new InvalidOperationException("Open a document to draw on its view.");
            var view = model.ActiveView as IModelView ?? throw new InvalidOperationException("The document has no view to draw on.");
            CloseMarkup();
            _markupView = new IntPtr(view.GetViewHWndx64());
            var owner = new IntPtr(((IFrame)_app.Sw.Frame()).GetHWndx64());
            _markup = new MarkupOverlay(_markupView, owner);
            if (Enum.TryParse(tool, out MarkupOverlay.Tool t)) _markup.CurrentTool = t;
            if (!string.IsNullOrEmpty(color)) _markup.CurrentColor = ColorTranslator.FromHtml(color);
            _markup.StrokesChanged += () => _emit("markup.strokes", new JObject { ["count"] = _markup?.StrokeCount ?? 0, ["frames"] = _markup?.FrameCount ?? 0 });
            _markup.FrameCaptured += f =>
            {
                if (_session != null) AddFrame(f);
                _emit("markup.strokes", new JObject { ["count"] = _markup?.StrokeCount ?? 0, ["frames"] = _markup?.FrameCount ?? 0 });
            };
            _framesInVoice.Clear();
            _markup.Open();
            UpdateHooks();
            return new JObject { ["active"] = true };
        }

        /// <summary>
        /// End the markup. Frames that went into a voice message stay there (and the message is sent now if it was only
        /// waiting for this); the others are returned for the composer.
        /// </summary>
        private object FinishMarkup()
        {
            if (_markup == null) return null;
            List<MarkupOverlay.Frame> frames;
            try
            {
                frames = _markup.Finish();
            }
            finally
            {
                CloseMarkup(commit: false);
            }
            var free = frames.Where(f => !_framesInVoice.Contains(f)).ToList();
            if (_session != null)
            {
                foreach (var f in free) AddFrame(f);
                free.Clear();
            }
            var inVoice = frames.Count - free.Count;
            _framesInVoice.Clear();
            if (_session != null) MaybeCommit();
            return new JObject
            {
                ["frames"] = new JArray(free.Select(f => new JObject { ["mediaType"] = f.MediaType, ["data"] = f.Data, ["strokes"] = f.Strokes })),
                ["inVoice"] = inVoice,
            };
        }

        private void CloseMarkup(bool commit = true)
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
            UpdateHooks();
            // A voice message that was only waiting for the pen goes now.
            if (commit && _session != null) MaybeCommit();
        }

        // ---------------------------------------------------------------- models and status

        private void StartDownload(string id)
        {
            if (_download != null) return;
            _download = new CancellationTokenSource();
            _downloadId = id;
            _downloadProgress = 0;
            _downloadError = null;
            EmitStatus();
            var lastEmit = DateTime.MinValue;
            var ct = _download.Token;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await SpeechModels.DownloadAsync(id, p =>
                    {
                        _downloadProgress = p;
                        if ((DateTime.UtcNow - lastEmit).TotalMilliseconds < 400) return;
                        lastEmit = DateTime.UtcNow;
                        Post(EmitStatus);
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _downloadError = null;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Downloading speech model {id} failed: {ex.Message}");
                    _downloadError = "Download failed: " + ex.Message;
                }
                Post(() =>
                {
                    _download = null;
                    _downloadId = null;
                    ApplySettings(); // the talk key works from now on, and the model warms up
                });
            });
        }

        private JObject Status() => new JObject
        {
            ["enabled"] = Enabled,
            ["model"] = ModelId,
            ["models"] = SpeechModels.ToJson(),
            ["engine"] = _engine.Status.ToString().ToLowerInvariant(),
            ["engineError"] = _engine.Error,
            ["download"] = _downloadId == null ? null : new JObject { ["id"] = _downloadId, ["progress"] = Math.Round(_downloadProgress, 3) },
            ["downloadError"] = _downloadError,
            ["available"] = System.IO.File.Exists(SpeechEngine.ExePath),
        };

        private void EmitStatus() => _emit("voice.status", Status());

        private void EmitState(string phase) => _emit("voice.state", new JObject
        {
            ["phase"] = phase,
            ["docKey"] = _session?.DocKey,
            ["text"] = _session?.Text ?? "",
            ["events"] = _session?.Summaries() ?? new JArray(),
            ["startedAt"] = _session?.Started.ToUniversalTime().ToString("o"),
        });

        private void EmitEvent(JObject e) => _emit("voice.event", new JObject { ["t"] = e["t"], ["kind"] = e["kind"], ["summary"] = e["summary"] });

        public void Dispose()
        {
            _hooks.Dispose();
            CloseMarkup();
            EndSession();
            _download?.Cancel();
            _holdTimer.Dispose();
            _viewTimer.Dispose();
            _engine.Dispose();
            _speaker.Dispose();
            _ui.Dispose();
        }
    }
}

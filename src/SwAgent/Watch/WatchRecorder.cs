using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.ModelFiles;
using SwAgent.Tools;
using Timer = System.Windows.Forms.Timer;

namespace SwAgent.Watch
{
    /// <summary>
    /// "Watch me": records what the user does in one document so the agent can turn it into a tool or workflow.
    /// Document events (rebuild, item added/deleted/renamed, undo/redo, user selection) schedule a debounced check
    /// that snapshots the feature tree (FeatureReader) and diffs it against the previous one (WatchDiff); the changes
    /// go into an ActivityLog, each with a thumbnail of the view as it is (the camera is never moved). A slow poll of a
    /// cheap tree fingerprint catches anything the events miss. Runs on the SolidWorks UI thread.
    /// </summary>
    internal sealed class WatchRecorder : IDisposable
    {
        private const int ThumbSize = 320;

        private readonly ISldWorks _sw;
        private readonly IModelDoc2 _model;
        private readonly ActivityLog _log = new ActivityLog();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Timer _changeTimer = new Timer { Interval = 450 };
        private readonly Timer _selectionTimer = new Timer { Interval = 400 };
        private readonly Timer _pollTimer = new Timer { Interval = 2000 };
        private List<FeatureState> _features;
        private int _featuresBefore;
        private string _signature;
        private string _beforeView;
        private Task _check;
        private bool _recheck;
        private bool _attached;
        private DateTime _startedAt;

        public string DocKey { get; }
        public string Title { get; }
        public string DocType { get; }

        /// <summary>Set while the agent is running, so its own changes are marked as such.</summary>
        public bool AgentBusy { get; set; }

        /// <summary>The log changed (entries without thumbnails, for the panel's live view).</summary>
        public event Action<JObject> Changed;

        private WatchRecorder(ISldWorks sw, IModelDoc2 model, string docKey)
        {
            _sw = sw;
            _model = model;
            DocKey = docKey;
            Title = model.GetTitle();
            var type = (swDocumentTypes_e)model.GetType();
            DocType = type == swDocumentTypes_e.swDocPART ? "part" : type == swDocumentTypes_e.swDocASSEMBLY ? "assembly" : "other";
        }

        public static async Task<WatchRecorder> StartAsync(ISldWorks sw, IModelDoc2 model, string docKey)
        {
            var type = (swDocumentTypes_e)model.GetType();
            if (type != swDocumentTypes_e.swDocPART && type != swDocumentTypes_e.swDocASSEMBLY)
                throw new InvalidOperationException("Watch me records parts and assemblies.");
            var r = new WatchRecorder(sw, model, docKey);
            r._features = await FeatureReader.SnapshotAsync(model, new Slicer(), r._cts.Token);
            r._featuresBefore = r._features.Count;
            r._signature = FeatureReader.Signature(model);
            r._beforeView = r.Capture(960, 82);
            r._startedAt = DateTime.UtcNow;
            r._clock.Restart();
            r.Attach();
            Log.Info($"Watch me: recording {r.Title} ({r._featuresBefore} features)");
            return r;
        }

        public JObject Status() => new JObject
        {
            ["active"] = true,
            ["docKey"] = DocKey,
            ["doc"] = Title,
            ["startedAt"] = _startedAt.ToString("o"),
            ["entries"] = new JArray(_log.Entries.Select(e => e.ToJson())),
        };

        /// <summary>Stop and hand over the log: text for the agent, the views before and after, and a strip of step thumbnails.</summary>
        public async Task<JObject> StopAsync()
        {
            Detach();
            try
            {
                await CheckNowAsync(); // whatever happened since the last event
            }
            catch (Exception ex)
            {
                Log.Warn("Watch me: final check failed: " + ex.Message);
            }
            var seconds = _clock.Elapsed.TotalSeconds;
            var text = WatchReport.Compose(Title, DocType, seconds, _log, _features, _featuresBefore);
            var result = new JObject
            {
                ["docKey"] = DocKey,
                ["doc"] = Title,
                ["docType"] = DocType,
                ["seconds"] = Math.Round(seconds),
                ["steps"] = _log.Entries.Count,
                ["text"] = text,
                ["entries"] = new JArray(_log.Entries.Select(e => e.ToJson())),
                ["stats"] = new JObject
                {
                    ["added"] = _log.Count("add"),
                    ["edited"] = _log.Count("edit"),
                    ["deleted"] = _log.Count("delete"),
                    ["renamed"] = _log.Count("rename"),
                    ["selections"] = _log.Count("select"),
                },
                ["before"] = _beforeView,
                ["after"] = _log.Entries.Count > 0 ? Capture(960, 82) : null,
                ["film"] = Filmstrip(),
            };
            Log.Info($"Watch me: stopped after {WatchFormat.Duration(seconds)}, {_log.Entries.Count} steps");
            Dispose();
            return result;
        }

        public void Dispose()
        {
            Detach();
            _cts.Cancel();
            _changeTimer.Dispose();
            _selectionTimer.Dispose();
            _pollTimer.Dispose();
        }

        // ---- events ----

        private void Attach()
        {
            _changeTimer.Tick += Safe.Tick("Watch: change check", () =>
            {
                if (SwBusy.InCommand(_sw)) return; // keep ticking until the command is over
                _changeTimer.Stop();
                _ = CheckNowAsync();
            });
            _selectionTimer.Tick += Safe.Tick("Watch: selection", () =>
            {
                _selectionTimer.Stop();
                RecordSelection();
            });
            _pollTimer.Tick += Safe.Tick("Watch: poll", Poll);
            switch (_model)
            {
                case PartDoc part:
                    part.RegenPostNotify2 += OnRegen;
                    part.AddItemNotify += OnItem;
                    part.DeleteItemNotify += OnItem;
                    part.RenameItemNotify += OnRename;
                    part.UndoPostNotify += OnChange;
                    part.RedoPostNotify += OnChange;
                    part.UserSelectionPostNotify += OnUserSelection;
                    break;
                case AssemblyDoc asm:
                    asm.RegenPostNotify2 += OnRegen;
                    asm.AddItemNotify += OnItem;
                    asm.DeleteItemNotify += OnItem;
                    asm.RenameItemNotify += OnRename;
                    asm.UndoPostNotify += OnChange;
                    asm.RedoPostNotify += OnChange;
                    asm.UserSelectionPostNotify += OnUserSelection;
                    break;
            }
            _attached = true;
            _pollTimer.Start();
        }

        private void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _pollTimer.Stop();
            _changeTimer.Stop();
            _selectionTimer.Stop();
            try
            {
                switch (_model)
                {
                    case PartDoc part:
                        part.RegenPostNotify2 -= OnRegen;
                        part.AddItemNotify -= OnItem;
                        part.DeleteItemNotify -= OnItem;
                        part.RenameItemNotify -= OnRename;
                        part.UndoPostNotify -= OnChange;
                        part.RedoPostNotify -= OnChange;
                        part.UserSelectionPostNotify -= OnUserSelection;
                        break;
                    case AssemblyDoc asm:
                        asm.RegenPostNotify2 -= OnRegen;
                        asm.AddItemNotify -= OnItem;
                        asm.DeleteItemNotify -= OnItem;
                        asm.RenameItemNotify -= OnRename;
                        asm.UndoPostNotify -= OnChange;
                        asm.RedoPostNotify -= OnChange;
                        asm.UserSelectionPostNotify -= OnUserSelection;
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Watch me: detaching events failed: " + ex.Message);
            }
        }

        private int OnRegen(object stopFeature) => OnChange();

        private int OnItem(int entityType, string itemName) => OnChange();

        private int OnRename(int entityType, string oldName, string newName) => OnChange();

        private int OnChange()
        {
            // Debounced: one rebuild raises several events, and dragging a value raises many.
            _changeTimer.Stop();
            _changeTimer.Start();
            return 0;
        }

        private int OnUserSelection()
        {
            _selectionTimer.Stop();
            _selectionTimer.Start();
            return 0;
        }

        private void Poll()
        {
            if (_check != null && !_check.IsCompleted) return;
            if (SwBusy.InCommand(_sw)) return; // read the model between commands, never in the middle of one
            try
            {
                if (FeatureReader.Signature(_model) != _signature) _ = CheckNowAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Watch me: poll failed: " + ex.Message);
            }
        }

        // ---- recording ----

        /// <summary>Snapshot, diff, log. Overlapping requests fold into one more pass after the running one.</summary>
        private async Task CheckNowAsync()
        {
            if (_check != null && !_check.IsCompleted)
            {
                _recheck = true;
                await _check;
                return;
            }
            _check = RunChecksAsync();
            await _check;
        }

        private async Task RunChecksAsync()
        {
            do
            {
                _recheck = false;
                try
                {
                    var t = _clock.Elapsed.TotalSeconds;
                    var after = await FeatureReader.SnapshotAsync(_model, new Slicer(), _cts.Token);
                    var changes = WatchDiff.Diff(_features, after);
                    _features = after;
                    _signature = FeatureReader.Signature(_model);
                    if (changes.Count == 0) continue;
                    var thumb = changes.Any(c => c.Kind == "add" || c.Kind == "edit") ? Capture(ThumbSize, 70) : null;
                    foreach (var c in changes)
                    {
                        var entry = _log.AddChange(c, t, AgentBusy);
                        if (entry != null && thumb != null && entry.Kind != "delete") entry.Thumb = thumb;
                    }
                    Changed?.Invoke(Status());
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn("Watch me: reading the feature tree failed: " + ex.Message);
                }
            } while (_recheck && !_cts.IsCancellationRequested);
        }

        private void RecordSelection()
        {
            try
            {
                if (!(SelectionTools.Snapshot(_sw, _model, 30)["items"] is JArray items) || items.Count == 0) return;
                var summary = SelectionSummary.Summarize(items.Select(i => ((string)i["type"], (string)i["label"], (string)i["detail"])));
                if (_log.AddSelection(summary, _clock.Elapsed.TotalSeconds) != null) Changed?.Invoke(Status());
            }
            catch (Exception ex)
            {
                Log.Warn("Watch me: reading the selection failed: " + ex.Message);
            }
        }

        /// <summary>The document's view exactly as it is (JPEG base64), or null when it isn't on screen.</summary>
        private string Capture(int maxSize, long quality)
        {
            try
            {
                if (!ReferenceEquals(_sw.ActiveDoc, _model) || !(_model.ActiveView is IModelView view)) return null;
                using (var shot = ViewTools.CaptureWindow(new IntPtr(view.GetViewHWndx64())))
                using (var fitted = ViewTools.Fit(shot, maxSize))
                {
                    return Convert.ToBase64String(ViewTools.EncodeJpeg(fitted, quality));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Watch me: view capture failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Up to six step thumbnails, numbered like the log, in one image.</summary>
        private string Filmstrip()
        {
            var steps = _log.Entries.Select((e, i) => (Entry: e, Number: i + 1)).Where(x => x.Entry.Thumb != null).ToList();
            if (steps.Count == 0) return null;
            if (steps.Count > 6) steps = Enumerable.Range(0, 6).Select(k => steps[(int)Math.Round(k * (steps.Count - 1) / 5.0)]).Distinct().ToList();
            var images = new List<Bitmap>();
            try
            {
                foreach (var s in steps)
                {
                    using (var ms = new System.IO.MemoryStream(Convert.FromBase64String(s.Entry.Thumb))) images.Add(new Bitmap(ms));
                }
                var cols = Math.Min(3, images.Count);
                var rows = (images.Count + cols - 1) / cols;
                int tileW = ThumbSize, tileH = (int)(ThumbSize * images[0].Height / (double)images[0].Width);
                using (var sheet = new Bitmap(tileW * cols, tileH * rows))
                {
                    using (var g = Graphics.FromImage(sheet))
                    using (var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var labelBg = new SolidBrush(Color.FromArgb(200, 32, 38, 46)))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        for (var i = 0; i < images.Count; i++)
                        {
                            int x = i % cols * tileW, y = i / cols * tileH;
                            g.DrawImage(images[i], x, y, tileW, tileH);
                            g.DrawRectangle(Pens.Silver, x, y, tileW - 1, tileH - 1);
                            var label = $"STEP {steps[i].Number}";
                            var size = g.MeasureString(label, font);
                            g.FillRectangle(labelBg, x + 5, y + 5, size.Width + 8, size.Height + 2);
                            g.DrawString(label, font, Brushes.White, x + 9, y + 6);
                        }
                    }
                    return Convert.ToBase64String(ViewTools.EncodeJpeg(sheet, 78));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Watch me: filmstrip failed: " + ex.Message);
                return null;
            }
            finally
            {
                foreach (var b in images) b.Dispose();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// "Save and restart" picks up exactly where the user left off: before SolidWorks closes, the open document windows
    /// (in order, with the active one), each window's camera, and the panel's unsent drafts are written to
    /// resume.json; the next start reopens the documents, puts the cameras back and hands the drafts to the panel.
    /// </summary>
    internal static class ResumeSession
    {
        private static string FilePath => Path.Combine(Paths.LocalDir, "resume.json");

        /// <summary>A resume older than this is stale (e.g. SolidWorks was started again much later by hand).</summary>
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

        /// <summary>The panel's part, handed over at the next start (see TakePanel).</summary>
        private static JObject _panel;

        public static void Save(ISldWorks sw, JObject panel)
        {
            try
            {
                var docs = new JArray();
                var active = (IModelDoc2)sw.ActiveDoc;
                var activePath = active?.GetPathName();
                foreach (var model in OpenWindows(sw))
                {
                    var path = model.GetPathName();
                    if (string.IsNullOrEmpty(path)) continue;
                    docs.Add(new JObject
                    {
                        ["path"] = path,
                        ["active"] = string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase),
                        ["view"] = Camera(model),
                    });
                }
                File.WriteAllText(FilePath, new JObject
                {
                    ["at"] = DateTime.UtcNow.ToString("o"),
                    ["docs"] = docs,
                    ["panel"] = panel,
                }.ToString());
                Log.Info($"Resume: saved {docs.Count} document(s)");
            }
            catch (Exception ex)
            {
                Log.Warn("Resume: couldn't save the session: " + ex.Message);
            }
        }

        /// <summary>
        /// At start-up: if SolidWorks was restarted by the add-in, reopen the documents once SolidWorks has finished
        /// starting. The file is removed first, so a problem while reopening can't repeat on every start.
        /// </summary>
        public static void RestoreWhenReady(ISldWorks sw)
        {
            JObject state;
            try
            {
                if (!File.Exists(FilePath)) return;
                state = JObject.Parse(File.ReadAllText(FilePath));
                File.Delete(FilePath);
                if (DateTime.TryParse((string)state["at"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) &&
                    DateTime.UtcNow - at > MaxAge)
                {
                    Log.Info("Resume: ignored a stale session file");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Resume: couldn't read the session: " + ex.Message);
                return;
            }

            _panel = state["panel"] as JObject;
            var docs = (state["docs"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            if (docs.Count == 0) return;

            var started = DateTime.UtcNow;
            var timer = new Timer { Interval = 400 };
            timer.Tick += Safe.Tick("Resume: reopening documents", () =>
            {
                if (!StartupDone(sw) && DateTime.UtcNow - started < TimeSpan.FromSeconds(60)) return;
                timer.Stop();
                timer.Dispose();
                Reopen(sw, docs);
            });
            timer.Start();
        }

        /// <summary>The panel's drafts etc. from before the restart, once (the panel asks for it when it starts).</summary>
        public static JObject TakePanel()
        {
            var p = _panel;
            _panel = null;
            return p;
        }

        private static void Reopen(ISldWorks sw, List<JObject> docs)
        {
            // The active document last, so it ends up in front.
            foreach (var d in docs.OrderBy(d => (bool?)d["active"] == true))
            {
                var path = (string)d["path"];
                try
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    var model = (IModelDoc2)sw.GetOpenDocumentByName(path);
                    if (model == null)
                    {
                        int errors = 0, warnings = 0;
                        model = sw.OpenDoc6(path, (int)TypeOf(path), (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                    }
                    if (model == null) continue;
                    int activateErrors = 0;
                    sw.ActivateDoc3(model.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
                    SetCamera(sw, model, d["view"] as JObject);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Resume: couldn't reopen {Path.GetFileName(path)}: {ex.Message}");
                }
            }
            Log.Info($"Resume: reopened {docs.Count} document(s)");
        }

        /// <summary>Documents that have their own window (not components loaded only inside an assembly).</summary>
        private static IEnumerable<IModelDoc2> OpenWindows(ISldWorks sw)
        {
            var all = (object[])sw.GetDocuments() ?? new object[0];
            foreach (IModelDoc2 m in all)
            {
                bool visible;
                try
                {
                    visible = m.Visible && m.GetFirstModelView() != null;
                }
                catch
                {
                    visible = false;
                }
                if (visible) yield return m;
            }
        }

        private static JObject Camera(IModelDoc2 model)
        {
            try
            {
                var view = (IModelView)model.ActiveView;
                if (view == null) return null;
                var orientation = (double[])((IMathTransform)view.Orientation3).ArrayData;
                var translation = (double[])((IMathVector)view.Translation3).ArrayData;
                return new JObject
                {
                    ["orientation"] = new JArray(orientation),
                    ["translation"] = new JArray(translation),
                    ["scale"] = view.Scale2,
                };
            }
            catch
            {
                return null;
            }
        }

        private static void SetCamera(ISldWorks sw, IModelDoc2 model, JObject cam)
        {
            if (cam == null) return;
            try
            {
                var view = (IModelView)model.ActiveView;
                if (view == null) return;
                var math = (IMathUtility)sw.GetMathUtility();
                var orientation = cam["orientation"]?.ToObject<double[]>();
                var translation = cam["translation"]?.ToObject<double[]>();
                if (orientation?.Length == 16) view.Orientation3 = (MathTransform)math.CreateTransform(orientation);
                if (translation?.Length == 3) view.Translation3 = (MathVector)math.CreateVector(translation);
                var scale = (double?)cam["scale"];
                if (scale > 0) view.Scale2 = scale.Value;
                view.GraphicsRedraw(null);
            }
            catch (Exception ex)
            {
                Log.Warn("Resume: couldn't restore the view: " + ex.Message);
            }
        }

        private static bool StartupDone(ISldWorks sw)
        {
            try
            {
                return sw.StartupProcessCompleted;
            }
            catch
            {
                return true; // older SolidWorks: the timer has already given it a moment
            }
        }

        private static swDocumentTypes_e TypeOf(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".sldasm":
                    return swDocumentTypes_e.swDocASSEMBLY;
                case ".slddrw":
                    return swDocumentTypes_e.swDocDRAWING;
                default:
                    return swDocumentTypes_e.swDocPART;
            }
        }
    }
}

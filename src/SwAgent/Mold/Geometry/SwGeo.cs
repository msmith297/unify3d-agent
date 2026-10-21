using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;

namespace SwAgent.Mold.Geometry
{
    /// <summary>A failed mold operation or self-check: the message says what was measured and what to do.</summary>
    public sealed class MoldApiException : Exception
    {
        public MoldApiException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// SolidWorks helpers shared by the mold tools and the Mold object [SolidWorks]. Every call here runs on the SolidWorks
    /// UI thread from a tool call or a code cell - never from a timer or a background thread - and null-checks what the
    /// API returns.
    /// </summary>
    internal static class SwGeo
    {
        public const double Mm = 0.001;

        public static IPartDoc RequirePart(IModelDoc2 model)
        {
            if (model == null) throw new MoldApiException("No document is open for this chat. Open the mold part first.");
            if (!(model is IPartDoc part) || model.GetType() != (int)swDocumentTypes_e.swDocPART)
                throw new MoldApiException("The mold tools work in a part document (one multi-body part holds the whole mold).");
            return part;
        }

        public static List<IBody2> Solids(IModelDoc2 model, bool visibleOnly = false) =>
            model is IPartDoc part ? ((part.GetBodies2((int)swBodyType_e.swSolidBody, visibleOnly) as object[]) ?? new object[0]).OfType<IBody2>().ToList() : new List<IBody2>();

        public static List<IBody2> Sheets(IModelDoc2 model) =>
            model is IPartDoc part ? ((part.GetBodies2((int)swBodyType_e.swSheetBody, false) as object[]) ?? new object[0]).OfType<IBody2>().ToList() : new List<IBody2>();

        /// <summary>Volume in m^3 (mass properties at density 1).</summary>
        /// <summary>A finite measurement (mass properties that fail give NaN, and NaN passes every "> tol" test).</summary>
        public static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        /// <summary>A self-check deviation that fails: off by more than tol, or not measurable at all.</summary>
        public static bool Off(double deviation, double tol) => !Finite(deviation) || Math.Abs(deviation) > tol;

        public static double Volume(IBody2 b) => b?.GetMassProperties(1.0) is double[] mp && mp.Length > 3 ? mp[3] : double.NaN;

        /// <summary>Centre of mass in model meters.</summary>
        public static double[] Centroid(IBody2 b) => b?.GetMassProperties(1.0) is double[] mp && mp.Length > 3 ? new[] { mp[0], mp[1], mp[2] } : null;

        /// <summary>
        /// Tight box in the frame: [u0, v0, w0, u1, v1, w1] (meters, relative to the frame origin) from six extreme points
        /// - GetBodyBox isn't tight on curved bodies.
        /// </summary>
        public static double[] Box(IBody2 b, MoldFrame f)
        {
            var r = new double[6];
            var axes = new[] { f.U, f.V, f.W };
            for (var k = 0; k < 3; k++)
            {
                foreach (var sign in new[] { -1, 1 })
                {
                    var d = V3.Mul(axes[k], sign);
                    if (!b.GetExtremePoint(d[0], d[1], d[2], out var x, out var y, out var z))
                        throw new MoldApiException("Couldn't measure a body's extent (GetExtremePoint failed).");
                    var p = f.ToFrame(new[] { x, y, z });
                    r[sign < 0 ? k : k + 3] = p[k];
                }
            }
            return r;
        }

        public static string BoxText(double[] box) =>
            $"u {box[0] / Mm:0.###}..{box[3] / Mm:0.###}, v {box[1] / Mm:0.###}..{box[4] / Mm:0.###}, w {box[2] / Mm:0.###}..{box[5] / Mm:0.###} mm";

        /// <summary>The Top Plane: the 2nd RefPlane feature (never by its name, which depends on the language).</summary>
        public static IFeature TopPlane(IModelDoc2 model)
        {
            var n = 0;
            for (var f = model.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
                if (f.GetTypeName2() == "RefPlane" && ++n == 2) return f;
            throw new MoldApiException("This part has no Top Plane (the 2nd reference plane) to take the press axis from.");
        }

        /// <summary>The mold frame: W = the Top Plane's normal (Transform.ArrayData[6..8]), origin at the model origin.</summary>
        public static MoldFrame Frame(IModelDoc2 model)
        {
            var plane = TopPlane(model).GetSpecificFeature2() as IRefPlane ?? throw new MoldApiException("The Top Plane has no plane data.");
            var a = plane.Transform?.ArrayData as double[] ?? throw new MoldApiException("The Top Plane has no transform.");
            var w = new[] { a[6], a[7], a[8] };
            var len = V3.Len(w);
            if (Math.Abs(len - 1) > 1e-6) throw new MoldApiException($"The Top Plane's normal has length {len:0.######}, not 1.");
            return MoldFrame.FromNormal(w);
        }

        public static SelectData Mark(IModelDoc2 model, int mark)
        {
            var data = ((ISelectionMgr)model.SelectionManager).CreateSelectData();
            data.Mark = mark;
            return data;
        }

        public static void SelectBody(IModelDoc2 model, IBody2 body, bool append, int mark)
        {
            if (body == null) throw new MoldApiException("A body to select is null (it may have been consumed by a later feature - get bodies fresh with Mold.Bodies()).");
            if (!body.Select2(append, Mark(model, mark))) throw new MoldApiException($"Couldn't select body '{SafeName(body)}'.");
        }

        public static int SelectedCount(IModelDoc2 model) => ((ISelectionMgr)model.SelectionManager).GetSelectedObjectCount2(-1);

        public static string SafeName(IBody2 b)
        {
            try
            {
                return b?.Name ?? "?";
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>Every feature, top level and sub-features (body folders live under the Solid Bodies folder).</summary>
        public static IEnumerable<IFeature> AllFeatures(IModelDoc2 model)
        {
            var stack = new Stack<IFeature>();
            var top = new List<IFeature>();
            for (var f = model.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature) top.Add(f);
            for (var i = top.Count - 1; i >= 0; i--) stack.Push(top[i]);
            var depth = 0;
            while (stack.Count > 0 && depth++ < 200000)
            {
                var f = stack.Pop();
                yield return f;
                var subs = new List<IFeature>();
                for (var s = f.GetFirstSubFeature() as IFeature; s != null; s = s.GetNextSubFeature() as IFeature) subs.Add(s);
                for (var i = subs.Count - 1; i >= 0; i--) stack.Push(subs[i]);
            }
        }

        public static IFeature FeatureByName(IModelDoc2 model, string name) => (model as IPartDoc)?.FeatureByName(name) as IFeature;

        /// <summary>Solid-body folders ("SubAtomFolder") with their bodies.</summary>
        public static List<(IFeature Feature, string Name, List<IBody2> Bodies)> BodyFolders(IModelDoc2 model)
        {
            var result = new List<(IFeature, string, List<IBody2>)>();
            var seen = new HashSet<string>();
            foreach (var f in AllFeatures(model))
            {
                if (f.GetTypeName2() != "SubAtomFolder") continue;
                if (!seen.Add(f.Name)) continue;
                var folder = f.GetSpecificFeature2() as IBodyFolder;
                var bodies = ((folder?.GetBodies() as object[]) ?? new object[0]).OfType<IBody2>().ToList();
                result.Add((f, f.Name, bodies));
            }
            return result;
        }

        /// <summary>The name a new feature can take: `name`, or `name (2)`, `name (3)`... if taken.</summary>
        public static string UniqueName(IModelDoc2 model, string name)
        {
            if (FeatureByName(model, name) == null) return name;
            for (var k = 2; k < 1000; k++)
                if (FeatureByName(model, $"{name} ({k})") == null) return $"{name} ({k})";
            return name + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        public static void Rename(IModelDoc2 model, IFeature f, string name)
        {
            if (f == null || string.IsNullOrEmpty(name)) return;
            try
            {
                f.Name = UniqueName(model, name);
            }
            catch (Exception ex)
            {
                Log.Warn($"Renaming a feature to '{name}' failed: {ex.Message}");
            }
        }

        /// <summary>Bodies that carry the feature's faces (what a feature made or changed).</summary>
        public static List<IBody2> BodiesOf(IFeature f)
        {
            var result = new List<IBody2>();
            var names = new HashSet<string>();
            foreach (var face in ((f?.GetFaces() as object[]) ?? new object[0]).OfType<IFace2>())
            {
                if (!(face.GetBody() is IBody2 b)) continue;
                if (names.Add(SafeName(b))) result.Add(b);
            }
            return result;
        }

        /// <summary>A body's identity across a feature that doesn't touch it: volume and centroid.</summary>
        public static (double V, double X, double Y, double Z) Signature(IBody2 b)
        {
            var mp = b.GetMassProperties(1.0) as double[];
            return mp == null || mp.Length < 4 ? (double.NaN, 0, 0, 0) : (mp[3], mp[0], mp[1], mp[2]);
        }

        public static bool SameSignature((double V, double X, double Y, double Z) a, (double V, double X, double Y, double Z) b) =>
            Math.Abs(a.V - b.V) <= 1e-9 * Math.Max(1e-12, Math.Abs(a.V)) + 1e-18 && Math.Abs(a.X - b.X) < 1e-8 && Math.Abs(a.Y - b.Y) < 1e-8 && Math.Abs(a.Z - b.Z) < 1e-8;

        /// <summary>The solids now that weren't there before (new or changed), matched by signature.</summary>
        public static List<IBody2> NewBodies(IModelDoc2 model, IList<(double V, double X, double Y, double Z)> before)
        {
            var pool = before.ToList();
            var result = new List<IBody2>();
            foreach (var b in Solids(model))
            {
                var s = Signature(b);
                var k = pool.FindIndex(p => SameSignature(p, s));
                if (k >= 0) pool.RemoveAt(k);
                else result.Add(b);
            }
            return result;
        }

        public static string PersistRef(IModelDoc2 model, object entity)
        {
            try
            {
                return model.Extension.GetPersistReference3(entity) is byte[] bytes ? Convert.ToBase64String(bytes) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Errors (not warnings) after a forced rebuild: feature name and error code.</summary>
        public static List<string> RebuildErrors(IModelDoc2 model, bool rebuild = true)
        {
            if (rebuild) UI.SceneOverlay.QuietRebuild(() => model.ForceRebuild3(false)); // a check: what's drawn stays
            var r = new List<string>();
            for (var f = model.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
            {
                var code = f.GetErrorCode2(out var warning);
                if (code != 0 && !warning) r.Add($"{f.Name} ({(swFeatureError_e)code})");
            }
            return r;
        }

        /// <summary>Delete features by name (with their absorbed sketches) - undoing a failed step before reporting it.</summary>
        public static bool DeleteFeatures(IModelDoc2 model, params IFeature[] features)
        {
            try
            {
                model.ClearSelection2(true);
                var any = false;
                foreach (var f in features)
                    if (f != null && f.Select2(true, 0)) any = true;
                return any && model.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
            }
            catch (Exception ex)
            {
                Log.Warn("Deleting a failed mold feature: " + ex.Message);
                return false;
            }
            finally
            {
                model.ClearSelection2(true);
            }
        }

        /// <summary>
        /// For long batches of features: the feature tree and graphics stop redrawing until disposed, then both come back
        /// whatever happens (try/finally).
        /// </summary>
        public static IDisposable Quiet(IModelDoc2 model)
        {
            var fm = model?.FeatureManager;
            var view = model?.ActiveView as IModelView;
            bool tree = true, graphics = true;
            try
            {
                if (fm != null)
                {
                    tree = fm.EnableFeatureTree;
                    fm.EnableFeatureTree = false;
                }
                if (view != null)
                {
                    graphics = view.EnableGraphicsUpdate;
                    view.EnableGraphicsUpdate = false;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Pausing the feature tree / graphics: " + ex.Message);
            }
            return new Restore(() =>
            {
                try
                {
                    if (fm != null) fm.EnableFeatureTree = tree;
                }
                catch (Exception ex)
                {
                    Log.Warn("Restoring the feature tree: " + ex.Message);
                }
                try
                {
                    if (view != null) view.EnableGraphicsUpdate = graphics;
                }
                catch (Exception ex)
                {
                    Log.Warn("Restoring graphics updates: " + ex.Message);
                }
            });
        }

        private sealed class Restore : IDisposable
        {
            private Action _a;

            public Restore(Action a)
            {
                _a = a;
            }

            public void Dispose()
            {
                var a = _a;
                _a = null;
                a?.Invoke();
            }
        }

        // ---------- the mold record's folder ----------

        public const string RecordProperty = "Unify3D.MoldRecord";
        public const string IdProperty = "Unify3D.MoldId";

        public static string GetProperty(IModelDoc2 model, string name)
        {
            try
            {
                var cpm = model?.Extension?.CustomPropertyManager[""];
                if (cpm == null) return null;
                cpm.Get6(name, false, out var raw, out var resolved, out _, out _);
                var v = string.IsNullOrWhiteSpace(resolved) ? raw : resolved;
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch
            {
                return null;
            }
        }

        public static void SetProperty(IModelDoc2 model, string name, string value)
        {
            var cpm = model?.Extension?.CustomPropertyManager[""] ?? throw new MoldApiException("The part has no custom property manager.");
            var r = cpm.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            if (r != (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged)
                throw new MoldApiException($"Couldn't set the custom property {name} ({(swCustomInfoAddResult_e)r}).");
        }

        /// <summary>
        /// The record folder `<mold folder>\<mold file name>.mold\`: the custom property holds the folder name, so a renamed
        /// or moved part still finds it next to itself - unless that record names another part that still exists (this
        /// file is a Save As or working copy of it, RecordOwner): then the copy's own `<file name>.mold`. Null for a part
        /// never saved.
        /// </summary>
        public static string RecordFolder(IModelDoc2 model)
        {
            var path = model?.GetPathName();
            if (string.IsNullOrEmpty(path)) return null;
            var stored = StoredRecordFolder(model);
            if (stored != null && RecordOwner.Owns(stored, path)) return stored;
            return Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".mold");
        }

        /// <summary>The folder the custom property Unify3D.MoldRecord names, next to the part, if it exists (no ownership check).</summary>
        public static string StoredRecordFolder(IModelDoc2 model)
        {
            var path = model?.GetPathName();
            if (string.IsNullOrEmpty(path)) return null;
            var stored = GetProperty(model, RecordProperty);
            if (string.IsNullOrEmpty(stored)) return null;
            var candidate = Path.Combine(Path.GetDirectoryName(path), Path.GetFileName(stored.TrimEnd('\\', '/')));
            return Directory.Exists(candidate) ? candidate : null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;

namespace SwAgent.ModelFiles
{
    /// <summary>
    /// Loads CAD files SolidWorks has to read (STEP, IGES, Parasolid, SLDPRT, SLDASM) as invisible documents, reads
    /// their mass properties and a triangle mesh, and closes everything it opened. A file the user already has open is
    /// read in place and left alone. Runs on the SolidWorks UI thread (every COM call must), in slices that hand the
    /// message loop back every few tens of milliseconds so SolidWorks stays responsive.
    /// </summary>
    internal static class SwModelLoader
    {
        public const int MaxTriangles = 2_500_000;

        public sealed class Loaded
        {
            public Mesh Mesh;
            public SolidWorksFacts Facts;
        }

        public static async Task<Loaded> LoadAsync(ISldWorks sw, string path, CancellationToken ct)
        {
            // One hidden open at a time, shared with every other caller (HiddenDocuments): DocumentVisible is a global switch.
            using (await HiddenDocuments.LockAsync(ct))
            {
                var slicer = new Slicer();
                var activeBefore = sw.ActiveDoc as IModelDoc2;
                var openBefore = HiddenDocuments.OpenTitles(sw);
                var model = sw.GetOpenDocumentByName(path) as IModelDoc2;
                var opened = false;
                if (model == null)
                {
                    // its own synchronous hidden open counts as busy: a synchronous Lock() re-entered from OpenDoc6's message
                    // pump fails fast instead of opening a second hidden document in the middle of this one
                    using (HiddenDocuments.SyncSection()) model = Open(sw, path);
                    opened = true;
                }
                try
                {
                    SolidWorksFacts facts;
                    using (HiddenDocuments.SyncSection()) facts = ReadFacts(model);
                    facts.How = opened ? "SolidWorks (opened hidden in the background, then closed - the user's documents and view were not touched)"
                        : "SolidWorks (the file is already open there - read in place, nothing changed)";
                    await slicer.TickAsync(ct);
                    var mesh = await TessellateAsync(model, facts, slicer, ct);
                    return new Loaded { Mesh = mesh, Facts = facts };
                }
                finally
                {
                    if (opened)
                        using (HiddenDocuments.SyncSection()) HiddenDocuments.Close(sw, model, openBefore, activeBefore);
                }
            }
        }

        private static IModelDoc2 Open(ISldWorks sw, string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var type = ext == ".sldprt" ? swDocumentTypes_e.swDocPART : ext == ".sldasm" ? swDocumentTypes_e.swDocASSEMBLY : swDocumentTypes_e.swDocNONE;
            using (HiddenDocuments.Hidden(sw))
            {
                var watch = Stopwatch.StartNew();
                IModelDoc2 model;
                if (type != swDocumentTypes_e.swDocNONE)
                {
                    int errors = 0, warnings = 0;
                    model = sw.OpenDoc6(path, (int)type, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
                    if (model == null) throw new InvalidOperationException($"SolidWorks couldn't open the file ({(swFileLoadError_e)errors}).");
                }
                else
                {
                    var errors = 0;
                    model = sw.LoadFile4(path, "r", sw.GetImportFileData(path), ref errors);
                    if (model == null) throw new InvalidOperationException($"SolidWorks couldn't import the file ({(swFileLoadError_e)errors}).");
                }
                Log.Info($"Model context: loaded {Path.GetFileName(path)} hidden in {watch.ElapsedMilliseconds} ms");
                return model;
            }
        }

        private static SolidWorksFacts ReadFacts(IModelDoc2 model)
        {
            var facts = new SolidWorksFacts();
            var type = (swDocumentTypes_e)model.GetType();
            facts.DocumentType = type == swDocumentTypes_e.swDocASSEMBLY ? "assembly" : type == swDocumentTypes_e.swDocPART ? "part" : type.ToString();
            Try(() =>
            {
                var unit = (swLengthUnit_e)model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
                facts.LengthUnit = unit.ToString().Replace("sw", "").ToLowerInvariant();
            });
            Try(() => facts.Features = model.GetFeatureCount());
            Try(() => facts.Configurations = (model.GetConfigurationNames() as string[])?.Length ?? 0);
            if (model is IPartDoc part)
            {
                Try(() => facts.SolidBodies = (part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[])?.Length ?? 0);
                Try(() => facts.SurfaceBodies = (part.GetBodies2((int)swBodyType_e.swSheetBody, true) as object[])?.Length ?? 0);
                Try(() =>
                {
                    var m = part.GetMaterialPropertyName2("", out _);
                    if (!string.IsNullOrEmpty(m)) facts.Material = m;
                });
            }
            if (model is IAssemblyDoc asm) Try(() => facts.Components = asm.GetComponentCount(false));
            Try(() =>
            {
                if (model.Extension.GetMassProperties2(1, out var status, false) is double[] mp && mp.Length >= 6 && status == 0)
                {
                    facts.Volume = mp[3] * 1e9;
                    facts.Area = mp[4] * 1e6;
                    facts.Mass = mp[5];
                }
            });
            return facts;
        }

        private static async Task<Mesh> TessellateAsync(IModelDoc2 model, SolidWorksFacts facts, Slicer slicer, CancellationToken ct)
        {
            var mesh = new Mesh();
            var bodies = new List<(IBody2 Body, double[] Transform)>();
            if (model is IPartDoc part)
            {
                var solids = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[] ?? new object[0];
                var sheets = part.GetBodies2((int)swBodyType_e.swSheetBody, true) as object[] ?? new object[0];
                bodies.AddRange(solids.Concat(sheets).OfType<IBody2>().Select(b => (b, (double[])null)));
            }
            else if (model is IAssemblyDoc asm)
            {
                foreach (var comp in (asm.GetComponents(false) as object[] ?? new object[0]).OfType<IComponent2>())
                {
                    await slicer.TickAsync(ct);
                    try
                    {
                        if (comp.IsSuppressed() || comp.IsHidden(true)) continue;
                        var compBodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out _) as object[];
                        if (compBodies == null || compBodies.Length == 0) continue;
                        var xf = comp.Transform2?.ArrayData as double[];
                        bodies.AddRange(compBodies.OfType<IBody2>().Select(b => (b, xf)));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Model context: component skipped ({ex.Message})");
                    }
                }
            }

            var fallbacks = 0;
            foreach (var (body, xf) in bodies)
            {
                if (mesh.TriangleCount >= MaxTriangles)
                {
                    facts.Notes.Add($"The mesh was capped at {MaxTriangles:N0} triangles; some bodies are missing from the views.");
                    break;
                }
                await slicer.TickAsync(ct);
                if (!await AddFaceTrianglesAsync(mesh, body, xf, slicer, ct))
                {
                    fallbacks++;
                    await AddTessellationAsync(mesh, body, xf, slicer, ct);
                }
            }
            if (fallbacks > 0) Log.Info($"Model context: {fallbacks} bodies tessellated with ITessellation");
            mesh.Seal();
            return mesh;
        }

        /// <summary>The display tessellation, one array per face. False (nothing added) when a face has none, as can happen for hidden documents.</summary>
        private static async Task<bool> AddFaceTrianglesAsync(Mesh mesh, IBody2 body, double[] xf, Slicer slicer, CancellationToken ct)
        {
            var arrays = new List<float[]>();
            foreach (var face in (body.GetFaces() as object[] ?? new object[0]).OfType<IFace2>())
            {
                await slicer.TickAsync(ct);
                if (!(face.GetTessTriangles(true) is float[] tris) || tris.Length < 9) return false;
                arrays.Add(tris);
            }
            foreach (var tris in arrays)
            {
                for (var i = 0; i + 8 < tris.Length; i += 9)
                {
                    mesh.AddTriangle(V(mesh, tris, i, xf), V(mesh, tris, i + 3, xf), V(mesh, tris, i + 6, xf));
                }
            }
            return arrays.Count > 0;
        }

        /// <summary>Tessellate a body with the modeller (independent of graphics), for bodies without display data.</summary>
        private static async Task AddTessellationAsync(Mesh mesh, IBody2 body, double[] xf, Slicer slicer, CancellationToken ct)
        {
            if (!(body.GetTessellation(null) is ITessellation tess)) return;
            tess.NeedFaceFacetMap = false;
            tess.NeedVertexNormal = false;
            tess.NeedEdgeFinMap = false;
            if (!tess.Tessellate()) return;
            var vertexCount = tess.GetVertexCount();
            var map = new int[vertexCount];
            var pt = new float[3];
            for (var v = 0; v < vertexCount; v++)
            {
                if ((v & 0xFF) == 0) await slicer.TickAsync(ct);
                var p = (double[])tess.GetVertexPoint(v);
                pt[0] = (float)p[0];
                pt[1] = (float)p[1];
                pt[2] = (float)p[2];
                map[v] = V(mesh, pt, 0, xf);
            }
            var facets = tess.GetFacetCount();
            for (var f = 0; f < facets; f++)
            {
                if ((f & 0xFF) == 0) await slicer.TickAsync(ct);
                if (!(tess.GetFacetFins(f) is int[] fins) || fins.Length < 3) continue;
                var corners = fins.Select(fin => ((int[])tess.GetFinVertices(fin))[0]).ToArray();
                for (var k = 1; k + 1 < corners.Length; k++) mesh.AddTriangle(map[corners[0]], map[corners[k]], map[corners[k + 1]]);
            }
        }

        /// <summary>
        /// One vertex (metres, component space) into the mesh (mm, assembly space). SolidWorks transforms are row-vector:
        /// ArrayData = 3x3 rotation, translation, scale.
        /// </summary>
        private static int V(Mesh mesh, float[] a, int i, double[] xf)
        {
            double x = a[i], y = a[i + 1], z = a[i + 2];
            if (xf != null && xf.Length >= 13)
            {
                var s = xf[12] == 0 ? 1 : xf[12];
                var tx = (x * xf[0] + y * xf[3] + z * xf[6]) * s + xf[9];
                var ty = (x * xf[1] + y * xf[4] + z * xf[7]) * s + xf[10];
                var tz = (x * xf[2] + y * xf[5] + z * xf[8]) * s + xf[11];
                x = tx;
                y = ty;
                z = tz;
            }
            return mesh.AddVertex((float)(x * 1000), (float)(y * 1000), (float)(z * 1000));
        }

        private static void Try(Action a)
        {
            try
            {
                a();
            }
            catch
            {
                // Each fact is optional.
            }
        }
    }

    /// <summary>
    /// Every document the add-in opens in the background goes through here (critique B6): attachments read for the agent
    /// (SwModelLoader), the mold tool's part import and STEP export, drawings converted to PDF. DocumentVisible is a global
    /// SolidWorks switch, so:
    ///   - one gate: LockAsync for work that awaits (it is held across the awaits), Lock for synchronous work;
    ///   - Hidden turns visibility off for parts, assemblies and drawings and restores exactly what was set - only ever
    ///     inside synchronous code, never across an await (a document the user opens meanwhile would stay hidden);
    ///   - Close quits what we opened plus any hidden documents it pulled in, and re-activates the user's document.
    /// Everything runs on the SolidWorks UI thread.
    /// </summary>
    internal static class HiddenDocuments
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private static readonly int[] Types =
        {
            (int)swDocumentTypes_e.swDocPART, (int)swDocumentTypes_e.swDocASSEMBLY, (int)swDocumentTypes_e.swDocDRAWING,
        };

        public static async Task<IDisposable> LockAsync(CancellationToken ct)
        {
            await Gate.WaitAsync(ct);
            try
            {
                // never start inside a synchronous hidden open's message pump (its own Lock() went ahead without the gate)
                await WhileSyncBusyAsync(ct);
            }
            catch
            {
                Gate.Release();
                throw;
            }
            return new Releaser(() => Gate.Release());
        }

        /// <summary>Synchronous hidden-document work on the stack right now (UI thread only): Lock holders and SyncSection.</summary>
        private static int _syncDepth;

        /// <summary>True while synchronous hidden-document work is on the UI thread's stack (an async loader must not resume).</summary>
        public static bool SyncBusy => _syncDepth > 0;

        /// <summary>
        /// Marks a synchronous section of an async holder (SwModelLoader's OpenDoc6/LoadFile4, its facts and its Close
        /// sweep) as busy, so a synchronous Lock() re-entered from a message pump inside it fails fast with "busy".
        /// </summary>
        public static IDisposable SyncSection()
        {
            _syncDepth++;
            return new Releaser(() => _syncDepth--);
        }

        /// <summary>Waits (yielding the UI thread) until no synchronous hidden-document work is on the stack.</summary>
        public static async Task WhileSyncBusyAsync(CancellationToken ct)
        {
            while (SyncBusy) await Task.Delay(50, ct);
        }

        /// <summary>
        /// The gate for synchronous work. If an async load holds it and is paused between two of its slices (visibility
        /// restored, Hidden is never held across an await), synchronous work goes ahead without owning the gate: the loader
        /// does not resume while it runs (Slicer waits on SyncBusy). If synchronous work is on the stack - a Lock holder,
        /// or the loader's own synchronous open, facts or Close (SyncSection) - this is a re-entry from a message pump inside
        /// it (OpenDoc6 or LoadFile4 of a big file pumps messages: a second drawing dropped meanwhile): two hidden opens
        /// would interleave, so it fails fast with a "busy, try again" error instead.
        /// </summary>
        public static IDisposable Lock()
        {
            var owned = Gate.Wait(0);
            if (!owned && _syncDepth > 0)
                throw new InvalidOperationException("SolidWorks is busy opening another file hidden; try again in a moment.");
            _syncDepth++;
            return new Releaser(() =>
            {
                _syncDepth--;
                if (owned) Gate.Release();
            });
        }

        /// <summary>New part, assembly and drawing documents open hidden until this is disposed.</summary>
        public static IDisposable Hidden(ISldWorks sw)
        {
            var before = Types.Select(t => (Type: t, Visible: sw.GetDocumentVisible(t))).ToList();
            foreach (var t in Types) sw.DocumentVisible(false, t);
            return new Releaser(() =>
            {
                foreach (var (t, visible) in before)
                {
                    try
                    {
                        sw.DocumentVisible(visible, t);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Restoring document visibility failed: " + ex.Message);
                    }
                }
            });
        }

        /// <summary>Lock and Hidden together, for a synchronous open-use-close.</summary>
        public static IDisposable Scope(ISldWorks sw)
        {
            var gate = Lock();
            try
            {
                var hidden = Hidden(sw);
                return new Releaser(() =>
                {
                    try
                    {
                        hidden.Dispose();
                    }
                    finally
                    {
                        gate.Dispose();
                    }
                });
            }
            catch
            {
                gate.Dispose();
                throw;
            }
        }

        public static HashSet<string> OpenTitles(ISldWorks sw) =>
            new HashSet<string>((sw.GetDocuments() as object[] ?? new object[0]).OfType<IModelDoc2>().Select(d => d.GetTitle()), StringComparer.OrdinalIgnoreCase);

        /// <summary>Close our document, any hidden documents it pulled in (imported assembly parts, a drawing's models), and restore the active document.</summary>
        public static void Close(ISldWorks sw, IModelDoc2 model, HashSet<string> openBefore, IModelDoc2 activeBefore)
        {
            try
            {
                if (model != null) sw.QuitDoc(model.GetTitle());
                foreach (var doc in (sw.GetDocuments() as object[] ?? new object[0]).OfType<IModelDoc2>().ToList())
                {
                    var title = doc.GetTitle();
                    if (!openBefore.Contains(title) && !doc.Visible) sw.QuitDoc(title);
                }
                if (activeBefore != null && !ReferenceEquals(sw.ActiveDoc, activeBefore))
                {
                    var errors = 0;
                    sw.ActivateDoc3(activeBefore.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Closing the hidden document failed: " + ex.Message);
            }
        }

        private sealed class Releaser : IDisposable
        {
            private Action _release;

            public Releaser(Action release)
            {
                _release = release;
            }

            public void Dispose()
            {
                var r = Interlocked.Exchange(ref _release, null);
                r?.Invoke();
            }
        }
    }

    /// <summary>Gives the SolidWorks message loop a turn every ~30 ms of work on the UI thread.</summary>
    internal sealed class Slicer
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public async Task TickAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_watch.ElapsedMilliseconds < 30) return;
            // A timer continuation (not Task.Yield, which is posted ahead of input) lets mouse and keyboard through.
            await Task.Delay(1, ct);
            // The continuation can be dispatched from a message pump inside synchronous hidden-document work (a drawing's
            // OpenDoc6 / SaveAs3): don't resume - and never run the Close sweep - inside it.
            await HiddenDocuments.WhileSyncBusyAsync(ct);
            _watch.Restart();
        }
    }
}

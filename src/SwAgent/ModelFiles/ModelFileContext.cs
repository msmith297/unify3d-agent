using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SwAgent.Infrastructure;
using SwAgent.Tools;

namespace SwAgent.ModelFiles
{
    /// <summary>
    /// Turns a 3D model file (from a tool's files input or a chat attachment) into context the agent can use: six
    /// rendered views in one image plus measurements (size, volume, area, bodies, units).
    ///   - Meshes (STL, 3MF, OBJ) are read, measured and rendered entirely on a background thread.
    ///   - CAD files (STEP, IGES, Parasolid, SLDPRT, SLDASM) are loaded by SolidWorks as hidden documents
    ///     (SwModelLoader), measured with SolidWorks' mass properties, tessellated, and closed again; the rendering
    ///     happens off the UI thread. The user's documents, camera and view are never touched.
    /// Each file has a size and time budget; results are cached by path, size and timestamp.
    /// </summary>
    internal static class ModelFileContext
    {
        public static readonly string[] MeshExtensions = { ".stl", ".3mf", ".obj" };
        public static readonly string[] CadExtensions = { ".step", ".stp", ".iges", ".igs", ".x_t", ".x_b", ".sldprt", ".sldasm" };

        public const long MaxMeshBytes = 300L << 20;
        public const long MaxCadBytes = 400L << 20;
        public static readonly TimeSpan MeshBudget = TimeSpan.FromSeconds(40);
        public static readonly TimeSpan CadBudget = TimeSpan.FromSeconds(90);

        private const int CacheSize = 8;
        private static readonly LinkedList<(string Key, ModelReport Report)> Cache = new LinkedList<(string, ModelReport)>();

        public static bool IsModelFile(string path)
        {
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return MeshExtensions.Contains(ext) || CadExtensions.Contains(ext);
        }

        /// <summary>Describe a model file. Call on the SolidWorks UI thread (CAD formats need it). Failures come back in the report; only cancellation throws.</summary>
        public static async Task<ModelReport> DescribeAsync(ISldWorks sw, string path, CancellationToken cancel = default)
        {
            var watch = Stopwatch.StartNew();
            var report = new ModelReport { Name = Path.GetFileName(path), Path = path, Format = ModelReport.FormatName(path) };
            string key = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) throw new FileNotFoundException("The file doesn't exist (any more).");
                report.Bytes = info.Length;
                key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                lock (Cache)
                {
                    var hit = Cache.FirstOrDefault(c => c.Key == key);
                    if (hit.Report != null) return hit.Report;
                }

                var isMesh = MeshReader.IsMeshFile(path);
                var limit = isMesh ? MaxMeshBytes : MaxCadBytes;
                if (info.Length > limit)
                {
                    throw new InvalidOperationException($"The file is {ModelReport.Size(info.Length)}; files over {ModelReport.Size(limit)} aren't read automatically - open it in SolidWorks if you need it.");
                }

                using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    var limitTime = isMesh ? MeshBudget : CadBudget;
                    budget.CancelAfter(limitTime);
                    var ct = budget.Token;
                    try
                    {
                        Mesh mesh;
                        string units;
                        var up = 'Y';
                        SolidWorksFacts facts = null;
                        if (isMesh)
                        {
                            var read = await Task.Run(() => MeshReader.Read(path, ct), ct);
                            mesh = read.Mesh;
                            units = read.Units;
                            up = read.Up;
                        }
                        else
                        {
                            if (sw == null) throw new InvalidOperationException("SolidWorks is needed to read this format.");
                            var loaded = await SwModelLoader.LoadAsync(sw, path, ct);
                            mesh = loaded.Mesh;
                            facts = loaded.Facts;
                            units = $"mm (document unit: {facts.LengthUnit ?? "unknown"})";
                        }

                        var (stats, views) = await Task.Run(() => MeasureAndRender(mesh, up, ct), ct);
                        report.Views = views;
                        var notes = new List<string>();
                        if (mesh.TriangleCount == 0) notes.Add("No geometry could be tessellated, so there are no views.");
                        report.Summary = ModelReport.Summarize(report.Name, report.Format, report.Bytes, units, stats, facts, up, views != null, notes);
                    }
                    catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Reading the model took longer than {limitTime.TotalSeconds:0} s and was stopped.");
                    }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                report.Error = ex.Message;
                report.Summary = $"3D model: {report.Name} ({report.Format}{(report.Bytes > 0 ? ", " + ModelReport.Size(report.Bytes) : "")}) - " +
                                 $"couldn't be read automatically: {ex.Message} Path: {path}";
                Log.Warn($"Model context for {path} failed: {ex.Message}");
                key = null; // don't cache failures
            }
            report.Seconds = watch.Elapsed.TotalSeconds;
            Log.Info($"Model context: {report.Name} in {watch.ElapsedMilliseconds} ms{(report.Error != null ? " (failed)" : "")}");
            if (key != null)
            {
                lock (Cache)
                {
                    Cache.AddFirst((key, report));
                    while (Cache.Count > CacheSize) Cache.RemoveLast();
                }
            }
            return report;
        }

        /// <summary>Measurements and the six-view sheet (JPEG, base64). Background thread.</summary>
        internal static (MeshStats Stats, string Views) MeasureAndRender(Mesh mesh, char up, CancellationToken ct)
        {
            var stats = MeshStats.Compute(mesh, ct);
            if (mesh.TriangleCount == 0) return (stats, null);
            using (var sheet = MeshRenderer.RenderSheet(mesh, stats.TriangleShell, up, 400, 300, ct))
            {
                return (stats, Convert.ToBase64String(ViewTools.EncodeJpeg(sheet, 82)));
            }
        }
    }
}

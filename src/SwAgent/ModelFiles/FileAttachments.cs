using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;

namespace SwAgent.ModelFiles
{
    /// <summary>
    /// Files the user hands the agent (a tool's files input, or a chat attachment), as the panel receives them:
    /// images and PDFs travel as data (the model reads them directly); SolidWorks drawings as a PDF SolidWorks exports;
    /// 3D models as rendered views plus a measurement summary (ModelFileContext); anything else as its path.
    /// </summary>
    internal static class FileAttachments
    {
        public const long MaxImageBytes = 5_000_000;

        /// <summary>
        /// Base64 makes a PDF 4/3 larger and requests are capped at 32 MB: 20 MB becomes 26.7 MB and fits, 30 MB became 40 MB
        /// and failed. The staged PDF rides along on every request of the chat (as a cached read), so smaller is better.
        /// </summary>
        public const long MaxPdfBytes = 20_000_000;

        public static string MediaType(string path)
        {
            switch (Path.GetExtension(path ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".pdf": return "application/pdf";
                default: return null;
            }
        }

        public static bool IsDrawing(string path) => string.Equals(Path.GetExtension(path ?? ""), ".slddrw", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// { name, path, kind: image|pdf|model|drawing|file|too_large, mediaType?, data?, summary?, views?, error? }.
        /// With describeModels false (the composer's drop/pick quick path, which must answer at once) a model is path-only
        /// and so is a drawing: kind "drawing", converted to its PDF entry by a later call with describeModels true
        /// (files.describe) - opening a drawing and its models and exporting every sheet can take tens of seconds.
        /// </summary>
        public static async Task<JObject> EntryAsync(ISldWorks sw, string path, bool describeModels = true, CancellationToken ct = default)
        {
            var entry = new JObject { ["name"] = Path.GetFileName(path), ["path"] = path };
            try
            {
                if (ModelFileContext.IsModelFile(path))
                {
                    entry["kind"] = "model";
                    entry["format"] = ModelReport.FormatName(path);
                    if (!describeModels) return entry;
                    var report = await ModelFileContext.DescribeAsync(sw, path, ct);
                    entry["summary"] = report.Summary;
                    if (report.Views != null)
                    {
                        entry["views"] = report.Views;
                        entry["viewsMediaType"] = "image/jpeg";
                    }
                    if (report.Error != null) entry["error"] = report.Error;
                    entry["seconds"] = Math.Round(report.Seconds, 1);
                    return entry;
                }
                if (IsDrawing(path) && !describeModels)
                {
                    entry["kind"] = "drawing";
                    entry["format"] = "SolidWorks drawing";
                    return entry;
                }
                if (IsDrawing(path) && sw != null)
                {
                    // The model reads PDFs, not .slddrw: SolidWorks exports every sheet, and that PDF is what's attached.
                    var pdf = DrawingToPdf(sw, path);
                    var pdfSize = new FileInfo(pdf).Length;
                    entry["name"] = Path.GetFileName(path) + " (as PDF)";
                    entry["pdfPath"] = pdf;
                    if (pdfSize > MaxPdfBytes)
                    {
                        entry["kind"] = "too_large";
                        entry["error"] = $"the drawing's PDF is {pdfSize / 1e6:0.#} MB (limit {MaxPdfBytes / 1e6:0} MB) - export fewer sheets or a lighter PDF";
                        return entry;
                    }
                    entry["kind"] = "pdf";
                    entry["mediaType"] = "application/pdf";
                    entry["data"] = Convert.ToBase64String(File.ReadAllBytes(pdf));
                    return entry;
                }
                var media = MediaType(path);
                var size = new FileInfo(path).Length;
                if (media == null)
                {
                    entry["kind"] = "file";
                }
                else if (size > (media == "application/pdf" ? MaxPdfBytes : MaxImageBytes))
                {
                    entry["kind"] = "too_large";
                }
                else
                {
                    entry["kind"] = media == "application/pdf" ? "pdf" : "image";
                    entry["mediaType"] = media;
                    entry["data"] = Convert.ToBase64String(File.ReadAllBytes(path));
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                entry["error"] = ex.Message;
            }
            return entry;
        }

        /// <summary>Converted drawings by path, size and time stamp (the panel describes a file more than once).</summary>
        private static readonly Dictionary<string, string> Converted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Export every sheet of a drawing to PDF. A drawing the user has open is exported in place; otherwise it is opened
        /// read-only and hidden (HiddenDocuments: the shared gate, visibility restored), exported, and closed together with
        /// the models it loaded. Runs on the SolidWorks UI thread.
        /// </summary>
        internal static string DrawingToPdf(ISldWorks sw, string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The drawing doesn't exist (any more).", path);
            var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            lock (Converted)
            {
                if (Converted.TryGetValue(key, out var cached) && File.Exists(cached)) return cached;
            }
            var dir = Directory.CreateDirectory(Path.Combine(Paths.LocalDir, "drawings")).FullName;
            var pdf = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}-{(uint)key.GetHashCode():x8}.pdf");
            var watch = Stopwatch.StartNew();
            using (HiddenDocuments.Lock())
            {
                var activeBefore = sw.ActiveDoc as IModelDoc2;
                var openBefore = HiddenDocuments.OpenTitles(sw);
                var model = sw.GetOpenDocumentByName(path) as IModelDoc2;
                var opened = false;
                try
                {
                    if (model == null)
                    {
                        int errors = 0, warnings = 0;
                        using (HiddenDocuments.Hidden(sw))
                        {
                            model = sw.OpenDoc6(path, (int)swDocumentTypes_e.swDocDRAWING,
                                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
                        }
                        if (model == null) throw new InvalidOperationException($"SolidWorks couldn't open the drawing ({(swFileLoadError_e)errors}).");
                        opened = true;
                    }
                    var data = sw.GetExportFileData((int)swExportDataFileType_e.swExportPdfData) as IExportPdfData;
                    data?.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportAllSheets, null);
                    int saveErrors = 0, saveWarnings = 0;
                    if (File.Exists(pdf)) File.Delete(pdf);
                    var ok = model.Extension.SaveAs3(pdf, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                        data, null, ref saveErrors, ref saveWarnings);
                    if (!ok || !File.Exists(pdf)) throw new InvalidOperationException($"SolidWorks couldn't export the drawing to PDF ({(swFileSaveError_e)saveErrors}).");
                }
                finally
                {
                    if (opened) HiddenDocuments.Close(sw, model, openBefore, activeBefore);
                }
            }
            Log.Info($"Drawing {Path.GetFileName(path)} exported to PDF in {watch.ElapsedMilliseconds} ms");
            if (watch.Elapsed > TimeSpan.FromSeconds(60)) Log.Warn($"Converting {Path.GetFileName(path)} took over the 60 s budget");
            lock (Converted) Converted[key] = pdf;
            return pdf;
        }
    }
}

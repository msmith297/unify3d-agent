using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;

namespace SwAgent.ModelFiles
{
    /// <summary>
    /// Files the user hands the agent (a tool's files input, or a chat attachment), as the panel receives them:
    /// images and PDFs travel as data (the model reads them directly); 3D models as rendered views plus a measurement
    /// summary (ModelFileContext); anything else as its path.
    /// </summary>
    internal static class FileAttachments
    {
        public const long MaxImageBytes = 5_000_000;
        public const long MaxPdfBytes = 30_000_000;

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

        /// <summary>{ name, path, kind: image|pdf|model|file|too_large, mediaType?, data?, summary?, views?, error? }.</summary>
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
    }
}

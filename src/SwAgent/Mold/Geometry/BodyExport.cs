using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.ModelFiles;

namespace SwAgent.Mold.Geometry
{
    /// <summary>
    /// mold_bodies export [SolidWorks] (design 2.3.4): one STEP (or Parasolid) file per body, written from a copy of the
    /// body in a hidden new part - nothing is added to the mold's feature tree. Per body: IBody2.Copy2, a hidden
    /// NewDocument, CreateFeatureFromBody3, the body's material, swStepAP set for the save, SaveAs3, QuitDoc. The STEP
    /// application protocol and document visibility are restored in finally, and the mold document is active again.
    /// One body at a time inside the shared gate (HiddenDocuments): visibility is off only during each body's open-save-
    /// close, and SolidWorks gets a turn between bodies.
    /// </summary>
    public static class BodyExport
    {
        public sealed class Result
        {
            public List<(string Id, string Path)> Written = new List<(string, string)>();
            public List<string> Problems = new List<string>();
        }

        /// <summary>Export `items` (file stem, body, SolidWorks material) into `folder` as .step (AP 214 or 242) or .x_t.</summary>
        public static async Task<Result> ExportAsync(ISldWorks sw, IModelDoc2 mold, IList<(string Stem, IBody2 Body, string Material)> items, string folder,
            string format, int stepAp, CancellationToken ct)
        {
            var result = new Result();
            if (items.Count == 0) return result;
            Directory.CreateDirectory(folder);
            var ext = string.Equals(format, "x_t", StringComparison.OrdinalIgnoreCase) ? ".x_t" : ".step";
            var template = sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
                throw new MoldApiException("SolidWorks has no default part template (Tools > Options > Default Templates); exports need one.");
            using (await HiddenDocuments.LockAsync(ct))
            {
                var oldAp = sw.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP);
                try
                {
                    if (ext == ".step") sw.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP, stepAp == 242 ? 242 : 214);
                    foreach (var (stem, body, material) in items)
                    {
                        ct.ThrowIfCancellationRequested();
                        var path = Path.Combine(folder, stem + ext);
                        try
                        {
                            ExportOne(sw, mold, body, material, template, path);
                            result.Written.Add((stem, path));
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            result.Problems.Add($"{stem}: {ex.Message}");
                            Log.Warn($"Export of {stem} failed: {ex.Message}");
                        }
                        await Task.Delay(1, ct); // a turn for SolidWorks' message loop between bodies (visibility is restored here)
                    }
                }
                finally
                {
                    try
                    {
                        sw.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swStepAP, oldAp);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Restoring the STEP AP preference: " + ex.Message);
                    }
                }
            }
            return result;
        }

        private static void ExportOne(ISldWorks sw, IModelDoc2 mold, IBody2 body, string material, string template, string path)
        {
            if (body == null) throw new MoldApiException("the body is gone (consumed by a later feature?)");
            var copy = body.Copy2(true) as IBody2 ?? throw new MoldApiException("couldn't copy the body");
            var activeBefore = sw.ActiveDoc as IModelDoc2 ?? mold;
            var openBefore = HiddenDocuments.OpenTitles(sw);
            IModelDoc2 doc = null;
            try
            {
                using (HiddenDocuments.Hidden(sw))
                {
                    doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2 ?? throw new MoldApiException("couldn't create a hidden part for the export");
                }
                var part = doc as IPartDoc ?? throw new MoldApiException("the export document isn't a part (check the default part template)");
                var feature = part.CreateFeatureFromBody3(copy, false, (int)swCreateFeatureBodyOpts_e.swCreateFeatureBodyCheck) as IFeature;
                if (feature == null) throw new MoldApiException("the body copy didn't make a valid solid (CreateFeatureFromBody3 failed its check)");
                if (!string.IsNullOrWhiteSpace(material))
                {
                    try
                    {
                        part.SetMaterialPropertyName2(BodyReport.ActiveConfig(doc), "SOLIDWORKS Materials", material);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Export material: " + ex.Message);
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                int errors = 0, warnings = 0;
                var ok = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings);
                if (!ok || !File.Exists(path)) throw new MoldApiException($"SaveAs failed ({(swFileSaveError_e)errors})");
            }
            finally
            {
                HiddenDocuments.Close(sw, doc, openBefore, activeBefore);
            }
        }

        /// <summary>
        /// The molded part as received, as STEP: a STEP file is copied; any other CAD file is opened hidden (read-only) and
        /// saved as STEP. Returns the written path.
        /// </summary>
        public static string ExportSourcePart(ISldWorks sw, string source, string path)
        {
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) throw new MoldApiException($"the molded part's file isn't there: {source}");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var ext = Path.GetExtension(source).ToLowerInvariant();
            if (ext == ".step" || ext == ".stp")
            {
                File.Copy(source, path, true);
                return path;
            }
            using (HiddenDocuments.Lock())
            {
                var activeBefore = sw.ActiveDoc as IModelDoc2;
                var openBefore = HiddenDocuments.OpenTitles(sw);
                var already = sw.GetOpenDocumentByName(source) as IModelDoc2;
                IModelDoc2 doc = already;
                try
                {
                    if (doc == null)
                    {
                        using (HiddenDocuments.Hidden(sw))
                        {
                            if (ext == ".sldprt")
                            {
                                int e = 0, w = 0;
                                doc = sw.OpenDoc6(source, (int)swDocumentTypes_e.swDocPART, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref e, ref w);
                            }
                            else
                            {
                                var e = 0;
                                doc = sw.LoadFile4(source, "r", sw.GetImportFileData(source), ref e);
                            }
                        }
                        if (doc == null) throw new MoldApiException($"SolidWorks couldn't open {Path.GetFileName(source)}");
                    }
                    int errors = 0, warnings = 0;
                    if (File.Exists(path)) File.Delete(path);
                    var ok = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, null, ref errors, ref warnings);
                    if (!ok || !File.Exists(path)) throw new MoldApiException($"SaveAs STEP failed ({(swFileSaveError_e)errors})");
                    return path;
                }
                finally
                {
                    if (already == null && doc != null) HiddenDocuments.Close(sw, doc, openBefore, activeBefore);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.ModelFiles;
using SwAgent.Scripting;

namespace SwAgent.Mold.Geometry
{
    /// <summary>
    /// Mold.* in execute_csharp cells and tool macros [SolidWorks] (design 2.3.6): the tested building blocks of the
    /// design-compression-mold tool. Lengths in meters, angles in radians, like the rest of the API. Every method checks
    /// its result by measurement (volumes, boxes, counts) and throws a MoldApiException saying what it measured when the
    /// check fails, so a wrong cut never passes silently. Bodies change identity with every feature: methods put the
    /// bodies they made or changed in LastBodies, and Mold.Bodies() finds them by role and geometry.
    /// </summary>
    public sealed class MoldApi
    {
        private readonly ScriptGlobals _g;

        internal MoldApi(ScriptGlobals g)
        {
            _g = g;
        }

        private IModelDoc2 Model => _g.Model ?? throw new MoldApiException("No document is open for this chat. Open the mold part first.");
        private ISldWorks Sw => _g.swApp;

        /// <summary>The bodies the last Mold.* operation made or changed (e.g. the copies of CopyBody, the cut plate of Subtract).</summary>
        public IList<IBody2> LastBodies { get; private set; } = new List<IBody2>();

        // ---------- frame, bodies ----------

        /// <summary>The mold frame: W = the Top Plane's normal (press and pull axis, +W toward the lid), U and V in the plane.</summary>
        public MoldFrame Frame() => SwGeo.Frame(Model);

        /// <summary>Every solid body with its role ("Mold - *" folder), id (BASE-1, LID-1 ... by geometry), volume and box.</summary>
        public IList<MoldBody> Bodies()
        {
            SwGeo.RequirePart(Model);
            return BodyReport.List(Model, Frame());
        }

        /// <summary>A body by mold id ("BASE-1", "PART-REF"), by SolidWorks body name, or null.</summary>
        public IBody2 Body(string idOrName)
        {
            var all = Bodies();
            return (all.FirstOrDefault(b => string.Equals(b.Id, idOrName, StringComparison.OrdinalIgnoreCase)) ??
                    all.FirstOrDefault(b => string.Equals(b.Name, idOrName, StringComparison.Ordinal)))?.Body;
        }

        /// <summary>A body's box in the mold frame [u0, v0, w0, u1, v1, w1] (meters), from six extreme points.</summary>
        public double[] Box(IBody2 b) => SwGeo.Box(b ?? throw new MoldApiException("Box: the body is null."), Frame());

        /// <summary>A body's volume in m^3.</summary>
        public double Volume(IBody2 b) => SwGeo.Volume(b ?? throw new MoldApiException("Volume: the body is null."));

        // ---------- the molded part ----------

        private static readonly string[] Neutral = { ".step", ".stp", ".iges", ".igs", ".x_t", ".x_b", ".sat", ".jt", ".parasolid" };
        private static readonly string[] Meshes = { ".stl", ".3mf", ".obj", ".ply", ".wrl" };

        /// <summary>
        /// Insert the molded part as an Insert Part feature "MOLD Part &lt;name&gt;" (kept linked, so Smart Rebuild can swap a
        /// revised part). STEP, IGES and Parasolid are first converted to SLDPRT in the record's part\ folder (opened hidden).
        /// Checks (critique B8): the import is exactly one solid with no surface bodies, IBody2.Check3 finds no faults, and the
        /// inserted body's volume is within 0.5% of the converted part's (or of expectedVolumeMm3, e.g. from the attachment
        /// summary). On a failed check the feature is removed again and the message says what was found - ask the user.
        /// </summary>
        public IFeature InsertPart(string path, string config = "", double expectedVolumeMm3 = 0)
        {
            var model = Model;
            var part = SwGeo.RequirePart(model);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new MoldApiException($"InsertPart: the file isn't there: {path}");
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (Meshes.Contains(ext)) throw new MoldApiException("InsertPart: a mesh (STL, 3MF, OBJ) can't become a reliable solid for a cavity. Ask the user for STEP or SLDPRT.");
            var name = Path.GetFileNameWithoutExtension(path);
            var sldprt = path;
            var sourceVolume = expectedVolumeMm3 > 0 ? expectedVolumeMm3 * 1e-9 : 0;
            if (ext != ".sldprt")
            {
                if (!Neutral.Contains(ext)) throw new MoldApiException($"InsertPart: {ext} files aren't a CAD format SolidWorks imports as a solid. Ask for STEP or SLDPRT.");
                var folder = Directory.CreateDirectory(Path.Combine(SwGeo.RecordFolder(model) ?? _g.WorkDir ?? Path.GetTempPath(), "part")).FullName;
                sldprt = Path.Combine(folder, name + ".SLDPRT");
                for (var k = 2; File.Exists(sldprt); k++) sldprt = Path.Combine(folder, $"{name}-{k}.SLDPRT"); // an earlier import stays as it was
                var converted = ConvertNeutral(path, sldprt);
                if (sourceVolume <= 0) sourceVolume = converted;
            }

            var before = SwGeo.Solids(model).Select(SwGeo.Signature).ToList();
            var sheetsBefore = SwGeo.Sheets(model).Count;
            model.ClearSelection2(true);
            var feature = part.InsertPart3(sldprt, (int)(swInsertPartOptions_e.swInsertPartImportSolids | swInsertPartOptions_e.swInsertPartDontZoomAll), config ?? "") as IFeature;
            model.ClearSelection2(true);
            if (feature == null) throw new MoldApiException($"InsertPart: SolidWorks didn't insert {Path.GetFileName(sldprt)} (InsertPart3 returned nothing). Is the configuration name right?");
            var added = SwGeo.NewBodies(model, before);
            var sheets = SwGeo.Sheets(model).Count - sheetsBefore;
            string problem = null;
            if (added.Count != 1 || sheets != 0)
                problem = $"the insert brought {added.Count} solid and {sheets} surface bodies; the cavity needs exactly one solid";
            else if (added[0].Check3 is IFaultEntity fault && fault.Count > 0)
                problem = $"the inserted solid has {fault.Count} faulty face(s)/edge(s) (Check3)";
            else if (sourceVolume > 0 && Math.Abs(SwGeo.Volume(added[0]) / sourceVolume - 1) > 0.005)
                problem = $"the inserted solid's volume {SwGeo.Volume(added[0]) / 1e-9:0.#} mm3 differs from the source's {sourceVolume / 1e-9:0.#} mm3 by more than 0.5%";
            if (problem != null)
            {
                SwGeo.DeleteFeatures(model, feature);
                throw new MoldApiException($"InsertPart: {problem}. The insert was removed again; ask the user for a clean single-solid STEP or SLDPRT.");
            }
            SwGeo.Rename(model, feature, "MOLD Part " + name);
            LastBodies = added;
            Log.Info($"Mold: inserted {path} as {feature.Name} ({SwGeo.Volume(added[0]) / 1e-9:0.#} mm3)");
            return feature;
        }

        /// <summary>Import a neutral file hidden, check it is one clean solid, save it as SLDPRT, close it. Returns its volume (m^3).</summary>
        private double ConvertNeutral(string path, string sldprt)
        {
            var sw = Sw;
            using (HiddenDocuments.Lock())
            {
                var activeBefore = sw.ActiveDoc as IModelDoc2;
                var openBefore = HiddenDocuments.OpenTitles(sw);
                IModelDoc2 doc = null;
                try
                {
                    using (HiddenDocuments.Hidden(sw))
                    {
                        var err = 0;
                        doc = sw.LoadFile4(path, "r", sw.GetImportFileData(path), ref err);
                        if (doc == null) throw new MoldApiException($"InsertPart: SolidWorks couldn't import {Path.GetFileName(path)} ({(swFileLoadError_e)err}).");
                    }
                    if (doc.GetType() != (int)swDocumentTypes_e.swDocPART)
                        throw new MoldApiException($"InsertPart: {Path.GetFileName(path)} imports as an assembly (several parts). Ask which part is molded, or for a single-part STEP.");
                    var solids = SwGeo.Solids(doc);
                    var sheets = SwGeo.Sheets(doc);
                    if (solids.Count != 1 || sheets.Count > 0)
                        throw new MoldApiException($"InsertPart: {Path.GetFileName(path)} imports as {solids.Count} solid and {sheets.Count} surface bodies; the mold needs exactly one solid. Ask the user for a clean solid (or knit/fix it in SolidWorks first).");
                    if (solids[0].Check3 is IFaultEntity fault && fault.Count > 0)
                        throw new MoldApiException($"InsertPart: the imported solid has {fault.Count} faulty face(s)/edge(s) (Check3). Ask the user for a clean export, or run Import Diagnostics.");
                    var volume = SwGeo.Volume(solids[0]);
                    int e = 0, w = 0;
                    var ok = doc.Extension.SaveAs3(sldprt, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref e, ref w);
                    if (!ok || !File.Exists(sldprt)) throw new MoldApiException($"InsertPart: saving the converted part failed ({(swFileSaveError_e)e}).");
                    return volume;
                }
                finally
                {
                    if (doc != null) HiddenDocuments.Close(sw, doc, openBefore, activeBefore);
                }
            }
        }

        /// <summary>The molded part's body: the Insert Part feature's body ("MOLD Part ..."), else PART-REF, else the largest solid.</summary>
        public IBody2 PartBody()
        {
            var model = Model;
            foreach (var f in SwGeo.AllFeatures(model).Where(f => f.Name.StartsWith("MOLD Part ", StringComparison.Ordinal)))
            {
                var b = SwGeo.BodiesOf(f).FirstOrDefault();
                if (b != null) return b;
            }
            return Body("PART-REF") ?? SwGeo.Solids(model).OrderByDescending(SwGeo.Volume).FirstOrDefault();
        }

        // ---------- moving and scaling ----------

        /// <summary>
        /// Turn and/or move a body (no copy): axisAngle = {ax, ay, az, angle} about a model axis through the origin (right-
        /// hand rule), then move = {dx, dy, dz}. Rotation and move are separate Move/Copy features ("MOLD Orient", "MOLD
        /// Orient move") so the order is unambiguous. Checked: volume unchanged, centroid where the transform puts it.
        /// </summary>
        public IFeature Orient(IBody2 b, double[] axisAngle, double[] move = null)
        {
            var model = Model;
            SwGeo.RequirePart(model);
            if (b == null) throw new MoldApiException("Orient: the body is null.");
            IFeature last = null;
            var body = b;
            if (axisAngle != null && axisAngle.Length >= 4 && Math.Abs(axisAngle[3]) > 1e-12)
            {
                var axis = V3.Unit(new[] { axisAngle[0], axisAngle[1], axisAngle[2] });
                var k = MoldFrame.AxisIndex(axis);
                if (k < 0) throw new MoldApiException("Orient: rotate about a model axis (X, Y or Z); chain two calls for other turns.");
                var angle = axisAngle[3] * Math.Sign(axis[k]);
                // How the turn is checked: the body's support (its extent along a direction d) must equal the turned body's
                // extent along the turned direction, for three directions - true only for the right turn (or a body
                // symmetric enough that both turns give the same shape).
                var dirs = new[] { new[] { 1, 0.3, 0.1 }, new[] { 0.2, 1, 0.5 }, new[] { 0.4, 0.1, 1 } }.Select(V3.Unit).ToList();
                var support0 = dirs.Select(d => Support(body, d)).ToList();
                var othersSig = SwGeo.Solids(model).Where(x => !SameBody(x, body)).Select(SwGeo.Signature).ToList();
                var done = false;
                var worst = 0.0;
                foreach (var sign in new[] { 1, -1 })
                {
                    var r = new double[3];
                    r[k] = sign * angle;
                    var (f, moved) = MoveCopy(body, new double[3], r, false, 1);
                    worst = Enumerable.Range(0, 3).Max(i => Math.Abs(Support(moved[0], Rotate(dirs[i], k, angle)) - support0[i]));
                    if (worst < 1e-5)
                    {
                        SwGeo.Rename(model, f, "MOLD Orient");
                        last = f;
                        body = moved[0];
                        done = true;
                        break;
                    }
                    // SolidWorks turned the other way round: undo and turn by the opposite angle.
                    SwGeo.DeleteFeatures(model, f);
                    body = SwGeo.NewBodies(model, othersSig).FirstOrDefault() ?? throw new MoldApiException("Orient: the body wasn't found after undoing a turn.");
                }
                if (!done) throw new MoldApiException($"Orient: neither turn direction put the body where a {axisAngle[3] * 180 / Math.PI:0.##} deg turn about {"XYZ"[k]} does ({worst * 1000:0.###} mm off); nothing was changed.");
            }
            if (move != null && move.Length >= 3 && V3.Len(move) > 1e-12)
            {
                var c0 = SwGeo.Centroid(body);
                var (f, moved) = MoveCopy(body, move, new double[3], false, 1);
                var c1 = SwGeo.Centroid(moved[0]);
                var off = V3.Dist(c1, V3.Add(c0, move));
                if (off > 1e-5)
                {
                    SwGeo.DeleteFeatures(model, f);
                    throw new MoldApiException($"Orient: the move landed {off * 1000:0.###} mm from where it should (the feature was removed).");
                }
                SwGeo.Rename(model, f, "MOLD Orient move");
                last = f;
                body = moved[0];
            }
            LastBodies = new List<IBody2> { body };
            return last ?? throw new MoldApiException("Orient: nothing to do (no angle and no move).");
        }

        private static bool SameBody(IBody2 a, IBody2 b) => SwGeo.SafeName(a) == SwGeo.SafeName(b);

        /// <summary>The body's extent along a unit direction: max over the body of p . d (from its extreme point).</summary>
        private static double Support(IBody2 b, double[] d)
        {
            if (!b.GetExtremePoint(d[0], d[1], d[2], out var x, out var y, out var z)) throw new MoldApiException("Couldn't measure a body's extent.");
            return x * d[0] + y * d[1] + z * d[2];
        }

        private static double[] Rotate(double[] p, int axis, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            switch (axis)
            {
                case 0: return new[] { p[0], c * p[1] - s * p[2], s * p[1] + c * p[2] };
                case 1: return new[] { c * p[0] + s * p[2], p[1], -s * p[0] + c * p[2] };
                default: return new[] { c * p[0] - s * p[1], s * p[0] + c * p[1], p[2] };
            }
        }

        /// <summary>Move/Copy with the body at Mark 1 (Mark 0 fails, v1). Checks the volume is kept. Returns the feature and the bodies it made or moved.</summary>
        private (IFeature Feature, List<IBody2> Bodies) MoveCopy(IBody2 body, double[] t, double[] r, bool copy, int n)
        {
            var model = Model;
            var v0 = SwGeo.Volume(body);
            var before = SwGeo.Solids(model).Where(x => copy || !SameBody(x, body)).Select(SwGeo.Signature).ToList();
            model.ClearSelection2(true);
            SwGeo.SelectBody(model, body, false, 1);
            var f = model.FeatureManager.InsertMoveCopyBody2(t[0], t[1], t[2], 0, 0, 0, 0, r[0], r[1], r[2], copy, n);
            model.ClearSelection2(true);
            if (f == null) throw new MoldApiException($"Move/Copy of '{SwGeo.SafeName(body)}' failed (InsertMoveCopyBody2 returned nothing).");
            var made = SwGeo.NewBodies(model, before);
            var want = copy ? n : 1;
            if (made.Count != want)
            {
                SwGeo.DeleteFeatures(model, f);
                throw new MoldApiException($"Move/Copy made {made.Count} bodies, expected {want} (the feature was removed).");
            }
            foreach (var m in made)
            {
                var v = SwGeo.Volume(m);
                if (Math.Abs(v / v0 - 1) > 1e-4)
                {
                    SwGeo.DeleteFeatures(model, f);
                    throw new MoldApiException($"Move/Copy changed a body's volume ({v0 / 1e-9:0.#} -> {v / 1e-9:0.#} mm3); the feature was removed.");
                }
            }
            return (f, made);
        }

        /// <summary>
        /// Copy a body n times, each copy offset by d from the previous (d = 0: copies in place). Body at Mark 1 (v1).
        /// Checked: n new bodies, each with the original's volume, copy k's centroid at the original's + k d within 0.01 mm.
        /// </summary>
        public IFeature CopyBody(IBody2 b, double[] d, int n = 1)
        {
            var model = Model;
            SwGeo.RequirePart(model);
            if (b == null) throw new MoldApiException("CopyBody: the body is null.");
            if (n < 1) throw new MoldApiException("CopyBody: n must be at least 1.");
            d = d ?? new double[3];
            var c0 = SwGeo.Centroid(b);
            var (f, made) = MoveCopy(b, d, new double[3], true, n);
            var expected = Enumerable.Range(1, n).Select(k => V3.Add(c0, V3.Mul(d, k))).ToList();
            foreach (var m in made)
            {
                var c = SwGeo.Centroid(m);
                if (expected.Min(e => V3.Dist(e, c)) > 1e-5)
                {
                    SwGeo.DeleteFeatures(model, f);
                    throw new MoldApiException($"CopyBody: a copy's centroid is not at original + k*d (nearest {expected.Min(e => V3.Dist(e, c)) * 1000:0.###} mm off); the feature was removed.");
                }
            }
            SwGeo.Rename(model, f, "MOLD Copy " + SwGeo.SafeName(b));
            LastBodies = made.OrderBy(m => expected.FindIndex(e => V3.Dist(e, SwGeo.Centroid(m)) < 1e-5)).ToList();
            return f;
        }

        /// <summary>
        /// Shrink allowance on the part body (critique A6): scale about a coordinate system "MOLD CS" at the parting-plane
        /// centroid (the body's U/V box centre at W = plW, default the body's centroid height), so the parting plane stays
        /// where the analysis put it and per-axis factors act along U, V, W. Only the body is selected - with nothing
        /// selected, Scale scales every body. Checked: V_after/V_before = su sv sw within 0.1%, and the box scaled about the
        /// fixed point within 0.01 mm. Fallback (documented): scale about the origin, then move the body back by
        /// (1 - s) * fixed point ("MOLD Shrink re-center") - the same result as two features.
        /// </summary>
        public IFeature ScaleForShrink(IBody2 b, double su, double sv, double sw, double? plW = null)
        {
            var model = Model;
            SwGeo.RequirePart(model);
            if (b == null) throw new MoldApiException("ScaleForShrink: the body is null.");
            if (su <= 0.5 || sv <= 0.5 || sw <= 0.5 || su > 2 || sv > 2 || sw > 2)
                throw new MoldApiException("ScaleForShrink: factors are scale factors (e.g. 1.0154 for 1.54% shrink), between 0.5 and 2.");
            var frame = Frame();
            var s = frame.ModelScaleFactors(su, sv, sw);
            var uniform = Math.Abs(su - sv) < 1e-12 && Math.Abs(sv - sw) < 1e-12;
            var box0 = SwGeo.Box(b, frame);
            var v0 = SwGeo.Volume(b);
            var cw = plW ?? frame.ToFrame(SwGeo.Centroid(b))[2];
            var fixedUvw = new[] { (box0[0] + box0[3]) / 2, (box0[1] + box0[4]) / 2, cw };
            var fixedModel = frame.ToModel(fixedUvw);
            var others = SwGeo.Solids(model).Where(x => !SameBody(x, b)).Select(SwGeo.Signature).ToList();
            var label = uniform ? $"MOLD Shrink {(su - 1) * 100:0.00}%" : $"MOLD Shrink U{(su - 1) * 100:0.00}% V{(sv - 1) * 100:0.00}% W{(sw - 1) * 100:0.00}%";

            string Check(IBody2 scaled)
            {
                var v = SwGeo.Volume(scaled);
                var ratio = v / v0 / (su * sv * sw);
                if (Math.Abs(ratio - 1) > 0.001) return $"volume ratio {v / v0:0.######}, expected {su * sv * sw:0.######}";
                var box = SwGeo.Box(scaled, frame);
                var f = new[] { su, sv, sw };
                for (var k = 0; k < 3; k++)
                {
                    var lo = fixedUvw[k] + f[k] * (box0[k] - fixedUvw[k]);
                    var hi = fixedUvw[k] + f[k] * (box0[k + 3] - fixedUvw[k]);
                    if (Math.Abs(box[k] - lo) > 1e-5 || Math.Abs(box[k + 3] - hi) > 1e-5)
                        return $"the scaled box ({SwGeo.BoxText(box)}) is not the original scaled about the parting-plane centroid (expected {"uvw"[k]} {lo * 1000:0.###}..{hi * 1000:0.###} mm)";
                }
                return null;
            }

            // 1. About MOLD CS.
            var cs = CoordinateSystemAt(fixedModel);
            var tried = new List<string>();
            if (cs != null)
            {
                foreach (var (bodyMark, csMark) in new[] { (0, 0), (1, 2), (2, 1) })
                {
                    model.ClearSelection2(true);
                    SwGeo.SelectBody(model, b, false, bodyMark);
                    if (!cs.Select2(true, csMark) || SwGeo.SelectedCount(model) != 2)
                    {
                        model.ClearSelection2(true);
                        continue;
                    }
                    var f = model.FeatureManager.InsertScale((short)swScaleType_e.swScaleAboutCoordinateSystem, uniform, s[0], s[1], s[2]);
                    model.ClearSelection2(true);
                    if (f == null)
                    {
                        tried.Add($"marks {bodyMark}/{csMark}: no feature");
                        continue;
                    }
                    var scaled = SwGeo.NewBodies(model, others);
                    var problem = scaled.Count != 1 ? $"{scaled.Count} bodies changed" : Check(scaled[0]);
                    if (problem == null)
                    {
                        SwGeo.Rename(model, f, label);
                        LastBodies = scaled;
                        return f;
                    }
                    tried.Add($"marks {bodyMark}/{csMark}: {problem}");
                    SwGeo.DeleteFeatures(model, f);
                }
            }
            else tried.Add("MOLD CS couldn't be created");
            Log.Warn("Mold: scale about MOLD CS failed (" + string.Join("; ", tried) + "); using origin + re-center");

            // 2. Fallback: about the origin, then move the fixed point back.
            model.ClearSelection2(true);
            SwGeo.SelectBody(model, b, false, 0);
            if (SwGeo.SelectedCount(model) != 1) throw new MoldApiException("ScaleForShrink: couldn't select only the part body (Scale would scale every body).");
            var fs = model.FeatureManager.InsertScale((short)swScaleType_e.swScaleAboutOrigin, uniform, s[0], s[1], s[2]);
            model.ClearSelection2(true);
            if (fs == null) throw new MoldApiException("ScaleForShrink: SolidWorks made no Scale feature (" + string.Join("; ", tried) + ").");
            var afterScale = SwGeo.NewBodies(model, others);
            if (afterScale.Count != 1 || Math.Abs(SwGeo.Volume(afterScale[0]) / v0 / (su * sv * sw) - 1) > 0.001)
            {
                SwGeo.DeleteFeatures(model, fs);
                throw new MoldApiException("ScaleForShrink: the scale about the origin didn't give the expected volume; it was removed.");
            }
            SwGeo.Rename(model, fs, label);
            var t = new[] { (1 - s[0]) * fixedModel[0], (1 - s[1]) * fixedModel[1], (1 - s[2]) * fixedModel[2] };
            if (V3.Len(t) > 1e-9)
            {
                var (fm, moved) = MoveCopy(afterScale[0], t, new double[3], false, 1);
                SwGeo.Rename(model, fm, "MOLD Shrink re-center");
                afterScale = moved;
            }
            var check = Check(afterScale[0]);
            if (check != null) throw new MoldApiException("ScaleForShrink (origin + re-center): " + check + ". Check the features MOLD Shrink and MOLD Shrink re-center.");
            LastBodies = afterScale;
            return fs;
        }

        /// <summary>"MOLD CS" at a point (axes = model axes): reuses one already there, else creates it. Null if SolidWorks refuses.</summary>
        private IFeature CoordinateSystemAt(double[] p)
        {
            var model = Model;
            foreach (var f in SwGeo.AllFeatures(model).Where(x => x.Name.StartsWith("MOLD CS", StringComparison.Ordinal)))
            {
                try
                {
                    if (f.GetDefinition() is ICoordinateSystemFeatureData data && data.Transform?.ArrayData is double[] a && a.Length >= 12 &&
                        V3.Dist(new[] { a[9], a[10], a[11] }, p) < 1e-9 && Math.Abs(a[0] - 1) < 1e-9 && Math.Abs(a[4] - 1) < 1e-9 && Math.Abs(a[8] - 1) < 1e-9)
                        return f;
                }
                catch
                {
                    // Not readable: make a new one.
                }
            }
            try
            {
                model.ClearSelection2(true);
                var cs = model.FeatureManager.CreateCoordinateSystemUsingNumericalValues(true, p[0], p[1], p[2], false, 0, 0, 0);
                model.ClearSelection2(true);
                if (cs == null) return null;
                SwGeo.Rename(model, cs, "MOLD CS");
                return cs;
            }
            catch (Exception ex)
            {
                Log.Warn("Mold: MOLD CS: " + ex.Message);
                return null;
            }
        }

        // ---------- plates, cavities, splits ----------

        /// <summary>
        /// A plate body "MOLD Plate &lt;id&gt;" covering u0..u1, v0..v1 (frame, meters) from w0 to w1: a rectangle on the Top
        /// Plane extruded with a start offset of w0 (the verified v1 pattern: Merge off makes a new body; every plate is
        /// referenced to the datum plane, not to faces that change). Checked: one new body with that box within 0.01 mm.
        /// </summary>
        public IFeature Plate(string id, double u0, double v0, double u1, double v1, double w0, double w1)
        {
            var model = Model;
            SwGeo.RequirePart(model);
            if (u1 <= u0 || v1 <= v0 || w1 <= w0) throw new MoldApiException("Plate: give u0 < u1, v0 < v1, w0 < w1 (meters, mold frame).");
            var frame = Frame();
            var expected = new[] { u0, v0, w0, u1, v1, w1 };
            var before = SwGeo.Solids(model).Select(SwGeo.Signature).ToList();
            foreach (var reverse in new[] { false, true }) // the verified v1 direction first; reversed (Dir) if the box says otherwise
            {
                var sketch = SketchRectangle(frame, u0, v0, u1, v1);
                model.ClearSelection2(true);
                sketch.Select2(false, 0);
                // Forward: start at w0 and extrude +W. Reversed: start at w1 and extrude back down to w0.
                var start = reverse ? w1 : w0;
                var off = Math.Abs(start) > 1e-9;
                var f = model.FeatureManager.FeatureExtrusion3(true, false, reverse, (int)swEndConditions_e.swEndCondBlind, 0, w1 - w0, 0, false, false, false, false, 0, 0,
                    false, false, false, false, false, true, true,
                    off ? (int)swStartConditions_e.swStartOffset : (int)swStartConditions_e.swStartSketchPlane, Math.Abs(start), start < 0);
                model.ClearSelection2(true);
                if (f == null)
                {
                    SwGeo.DeleteFeatures(model, sketch);
                    throw new MoldApiException($"Plate {id}: the extrusion failed (FeatureExtrusion3 returned nothing).");
                }
                var made = SwGeo.NewBodies(model, before);
                var box = made.Count == 1 ? SwGeo.Box(made[0], frame) : null;
                if (box != null && Enumerable.Range(0, 6).All(k => Math.Abs(box[k] - expected[k]) < 1e-5))
                {
                    SwGeo.Rename(model, f, "MOLD Plate " + id);
                    SwGeo.Rename(model, sketch, "MOLD Plate " + id + " sketch");
                    LastBodies = made;
                    return f;
                }
                SwGeo.DeleteFeatures(model, f, sketch);
                if (reverse) throw new MoldApiException($"Plate {id}: made {made.Count} bodies" + (box != null ? $" at {SwGeo.BoxText(box)}, expected {SwGeo.BoxText(expected)}" : "") + "; removed.");
            }
            throw new MoldApiException($"Plate {id}: not built.");
        }

        /// <summary>A closed rectangle u0..u1 x v0..v1 sketched on the Top Plane; returns the sketch feature.</summary>
        private IFeature SketchRectangle(MoldFrame frame, double u0, double v0, double u1, double v1)
        {
            var model = Model;
            var sm = model.SketchManager;
            var mu = (IMathUtility)Sw.GetMathUtility();
            model.ClearSelection2(true);
            if (!SwGeo.TopPlane(model).Select2(false, 0)) throw new MoldApiException("Couldn't select the Top Plane to sketch on.");
            var adb = sm.AddToDB;
            sm.InsertSketch(true);
            try
            {
                sm.AddToDB = true;
                var sk = model.GetActiveSketch2() as ISketch ?? throw new MoldApiException("No sketch opened on the Top Plane.");
                var t = (IMathTransform)sk.ModelToSketchTransform;
                double[] S(double u, double v)
                {
                    var p = (IMathPoint)mu.CreatePoint(frame.ToModel(u, v, 0));
                    var r = (double[])((IMathPoint)p.MultiplyTransform(t)).ArrayData;
                    return r;
                }
                var c = new[] { S(u0, v0), S(u1, v0), S(u1, v1), S(u0, v1) };
                for (var k = 0; k < 4; k++) sm.CreateLine(c[k][0], c[k][1], 0, c[(k + 1) % 4][0], c[(k + 1) % 4][1], 0);
            }
            finally
            {
                sm.AddToDB = adb;
                sm.InsertSketch(true);
            }
            return model.Extension.GetLastFeatureAdded() as IFeature ?? throw new MoldApiException("The rectangle sketch wasn't created.");
        }

        /// <summary>
        /// Combine-subtract: target at Mark 1, tools at Mark 2, InsertCombineFeature(SWBODYCUT, null, null) (passing the
        /// bodies as arguments returns nothing, v1). The tool bodies are consumed - copy them first (CopyBody) to keep them.
        /// Checked: the volume removed equals the tools' volume inside the target within 0.1%.
        /// </summary>
        public IFeature Subtract(IBody2 target, IEnumerable<IBody2> tools, string name)
        {
            var model = Model;
            SwGeo.RequirePart(model);
            var toolList = (tools ?? Enumerable.Empty<IBody2>()).Where(t => t != null).ToList();
            if (target == null || toolList.Count == 0) throw new MoldApiException("Subtract: give the target body and at least one tool body.");
            var vTarget = SwGeo.Volume(target);
            double? inside = 0;
            foreach (var t in toolList)
            {
                var v = IntersectionVolume(target, t);
                if (v == null)
                {
                    inside = null;
                    break;
                }
                inside += v;
            }
            var involved = new[] { target }.Concat(toolList).ToList();
            var others = SwGeo.Solids(model).Where(x => !involved.Any(i => SameBody(i, x))).Select(SwGeo.Signature).ToList();
            model.ClearSelection2(true);
            SwGeo.SelectBody(model, target, false, 1);
            foreach (var t in toolList) SwGeo.SelectBody(model, t, true, 2);
            var f = model.FeatureManager.InsertCombineFeature((int)swBodyOperationType_e.SWBODYCUT, null, null);
            model.ClearSelection2(true);
            if (f == null) throw new MoldApiException("Subtract: the Combine (subtract) failed - do the tools overlap the target?");
            var result = SwGeo.NewBodies(model, others);
            var vAfter = result.Sum(SwGeo.Volume);
            var removed = vTarget - vAfter;
            string problem = null;
            if (result.Count == 0) problem = "no body is left";
            else if (inside.HasValue && Math.Abs(removed - inside.Value) > 0.001 * Math.Max(inside.Value, 1e-12))
                problem = $"removed {removed / 1e-9:0.#} mm3 but the tools hold {inside.Value / 1e-9:0.#} mm3 inside the target";
            else if (!inside.HasValue && (removed <= 0 || removed > toolList.Sum(SwGeo.Volume) * 1.001))
                problem = $"removed {removed / 1e-9:0.#} mm3, more than the tools' volume or nothing";
            if (problem != null)
            {
                SwGeo.DeleteFeatures(model, f);
                throw new MoldApiException($"Subtract '{name}': {problem}; the feature was removed.");
            }
            SwGeo.Rename(model, f, string.IsNullOrWhiteSpace(name) ? "MOLD Cavity" : name);
            LastBodies = result;
            return f;
        }

        /// <summary>Volume of a ∩ b from temporary copies (nothing is added to the model), or null if the modeler refuses.</summary>
        private static double? IntersectionVolume(IBody2 a, IBody2 b)
        {
            try
            {
                var ca = a.Copy2(false) as IBody2;
                var cb = b.Copy2(false) as IBody2;
                if (ca == null || cb == null) return null;
                var parts = ca.Operations2((int)swBodyOperationType_e.SWBODYINTERSECT, cb, out var err) as object[];
                if (err != 0) return null;
                return (parts ?? new object[0]).OfType<IBody2>().Sum(SwGeo.Volume);
            }
            catch (Exception ex)
            {
                Log.Warn("Mold: intersection volume: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Split a body with a plane (Pre/PostSplitBody2): the plane at Mark 1 and the body at Mark 2, then every resulting
        /// body kept. Checked: at least two bodies, total volume kept within 0.1%. (Selection marks per spike S8/S11 notes:
        /// a second attempt uses Mark 0 for both.)
        /// </summary>
        public IFeature SplitByPlane(IBody2 b, IFeature plane, string name)
        {
            var model = Model;
            SwGeo.RequirePart(model);
            if (b == null || plane == null) throw new MoldApiException("SplitByPlane: give the body and the plane feature.");
            var v0 = SwGeo.Volume(b);
            var others = SwGeo.Solids(model).Where(x => !SameBody(x, b)).Select(SwGeo.Signature).ToList();
            foreach (var (planeMark, bodyMark) in new[] { (1, 2), (0, 0) })
            {
                model.ClearSelection2(true);
                plane.Select2(false, planeMark);
                SwGeo.SelectBody(model, b, true, bodyMark);
                var pieces = model.FeatureManager.PreSplitBody2() as object[];
                if (pieces == null || pieces.Length < 2)
                {
                    model.ClearSelection2(true);
                    continue;
                }
                var f = model.FeatureManager.PostSplitBody2(pieces, false, null, null, "") as IFeature;
                model.ClearSelection2(true);
                if (f == null) continue;
                var made = SwGeo.NewBodies(model, others);
                var v = made.Sum(SwGeo.Volume);
                if (made.Count >= 2 && Math.Abs(v / v0 - 1) <= 0.001)
                {
                    SwGeo.Rename(model, f, string.IsNullOrWhiteSpace(name) ? "MOLD Split" : name);
                    LastBodies = made;
                    return f;
                }
                SwGeo.DeleteFeatures(model, f);
                throw new MoldApiException($"SplitByPlane '{name}': {made.Count} bodies with {v / 1e-9:0.#} of {v0 / 1e-9:0.#} mm3; removed.");
            }
            throw new MoldApiException($"SplitByPlane '{name}': SolidWorks found nothing to split (does the plane cross the body?).");
        }

        // ---------- folders and materials ----------

        /// <summary>
        /// Put bodies in the solid-body folder `name` ("Mold - Base", "Mold - Reference", "Garbage"...): select them, then
        /// FeatureManager.InsertSubFolder() and rename (as Smart Rebuild's cleanup). An existing folder of that name is
        /// merged: its bodies and the new ones go in a new folder, the emptied old one is deleted. Checked: the folder holds
        /// every body asked for.
        /// </summary>
        public IFeature Folder(string name, params IBody2[] bodies)
        {
            var model = Model;
            SwGeo.RequirePart(model);
            if (string.IsNullOrWhiteSpace(name)) throw new MoldApiException("Folder: give a folder name.");
            var list = (bodies ?? new IBody2[0]).Where(b => b != null).ToList();
            if (list.Count == 0) throw new MoldApiException("Folder: no bodies given.");
            var existing = SwGeo.BodyFolders(model).Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var all = list.Concat(existing.SelectMany(e => e.Bodies)).GroupBy(SwGeo.SafeName).Select(g => g.First()).ToList();
            model.ClearSelection2(true);
            foreach (var b in all)
                if (!b.Select2(true, null)) throw new MoldApiException($"Folder: couldn't select body '{SwGeo.SafeName(b)}'.");
            var folder = model.FeatureManager.InsertSubFolder();
            model.ClearSelection2(true);
            if (folder == null) throw new MoldApiException($"Folder '{name}': SolidWorks made no body folder (InsertSubFolder).");
            foreach (var old in existing)
            {
                var count = (old.Feature.GetSpecificFeature2() as IBodyFolder)?.GetBodyCount() ?? 0;
                if (count == 0) SwGeo.DeleteFeatures(model, old.Feature);
                else Log.Warn($"Mold: folder '{old.Name}' still holds {count} bodies after the merge; kept");
            }
            SwGeo.Rename(model, folder, name);
            var held = (folder.GetSpecificFeature2() as IBodyFolder)?.GetBodyCount() ?? -1;
            if (held != all.Count) throw new MoldApiException($"Folder '{folder.Name}' holds {held} bodies, expected {all.Count}.");
            return folder;
        }

        /// <summary>
        /// Material of one body (aluminium plates, steel inserts): IBody2.SetMaterialProperty in the active configuration,
        /// read back - SolidWorks ignores unknown names silently, so a name that doesn't read back throws.
        /// </summary>
        public bool SetBodyMaterial(IBody2 b, string name, string db = "SOLIDWORKS Materials")
        {
            var model = Model;
            SwGeo.RequirePart(model);
            if (b == null || string.IsNullOrWhiteSpace(name)) throw new MoldApiException("SetBodyMaterial: give a body and a material name.");
            var cfg = BodyReport.ActiveConfig(model);
            b.SetMaterialProperty(cfg, db, name);
            var back = b.GetMaterialPropertyName(cfg, out _);
            if (!string.Equals(back, name, StringComparison.OrdinalIgnoreCase))
                throw new MoldApiException($"SetBodyMaterial: SolidWorks didn't take '{name}' from '{db}' (reads back '{back}'). Use the exact library name, e.g. \"6061-T6 (SS)\".");
            return true;
        }

        // ---------- flash system, holes ----------

        /// <summary>A FlashOptions with the shop standard's defaults (lengths in meters): Mold.FlashGrooves(face, Mold.FlashOptions()).</summary>
        public FlashOptions FlashOptions() => new FlashOptions();

        /// <summary>
        /// The user's flash-groove standard on the base's parting face (null: auto-pick the top face of the bottom block):
        /// lip, moat and runners, R corners with one fillet feature per body, then the acceptance checks measured (lip and
        /// clearance). Throws with a geometry summary for cases the code doesn't build (combined moats, freeform outlines,
        /// unclear multi-plate bases) - build those in cells by the same rules.
        /// </summary>
        public FlashResult FlashGrooves(IFace2 face = null, FlashOptions options = null) => new FlashSystem(Sw, Model, options).Run(face);

        /// <summary>
        /// Counterbored clearance holes for socket head cap screws from a planar face (e.g. the lid's top), at model points on
        /// that face: a counterbore cboreDia x cboreDepth and a through hole holeDia x holeDepth, as cuts ("&lt;name&gt; cbore",
        /// "&lt;name&gt;"). Checked: one cylinder of each diameter at every point.
        /// </summary>
        public IFeature CboreHoles(string name, IFace2 face, IEnumerable<double[]> points, double holeDia, double holeDepth, double cboreDia, double cboreDepth) =>
            Holes.Cbore(Sw, Model, name, face, points?.ToList(), holeDia, holeDepth, cboreDia, cboreDepth);

        /// <summary>
        /// Tapped holes (or insert holes) from a planar face: tap drill tapDrillDia x drillDepth at model points on the face,
        /// as a cut "&lt;name&gt;"; the thread spec (e.g. "1/2-13 UNC x 25 mm") goes in the feature's comment-free name suffix and in
        /// the record's hardware list for the machining sheet. Checked: one cylinder of the drill diameter per point.
        /// </summary>
        public IFeature TappedHoles(string name, IFace2 face, IEnumerable<double[]> points, double tapDrillDia, double drillDepth, string thread = null) =>
            Holes.Tapped(Sw, Model, name, face, points?.ToList(), tapDrillDia, drillDepth, thread);

        /// <summary>
        /// Dog-bone reliefs for an insert pocket (critique B7): at every sharp inside corner of the pocket floor's outline
        /// (lines meeting with no arc, or an arc smaller than 1.1 x the cutter radius), a circle of radius 1.1 x cutterRadius
        /// through the corner, centred on the bisector out in the steel, cut from the opening face down to the floor
        /// ("MOLD Pocket reliefs"). The insert's sharp corners then seat without EDM. Checked: one cylinder per corner.
        /// Returns null when every corner is already rounded enough.
        /// </summary>
        public IFeature PocketCornerReliefs(IFace2 openingFace, IFace2 pocketFloor, double cutterRadius, string name = "MOLD Pocket reliefs")
        {
            var model = Model;
            SwGeo.RequirePart(model);
            if (cutterRadius <= 0) throw new MoldApiException("PocketCornerReliefs: give the radius of the cutter that machines the pocket (meters).");
            foreach (var f in new[] { openingFace, pocketFloor })
                if (f == null || !(f.GetSurface() is ISurface s) || !s.IsPlane()) throw new MoldApiException("PocketCornerReliefs: give the planar face around the pocket's opening and the pocket's flat floor.");
            var n = V3.Unit((double[])pocketFloor.Normal);
            var frame = MoldFrame.FromNormal(n);
            double[] To2(double[] p) => new[] { V3.Dot(p, frame.U), V3.Dot(p, frame.V) };
            var outer = ((object[])pocketFloor.GetLoops()).Cast<ILoop2>().FirstOrDefault(l => l.IsOuter()) ?? throw new MoldApiException("PocketCornerReliefs: the floor has no outline.");
            var segs = new List<FlashLayout.Segment>();
            var outline = new List<double[]>();
            foreach (ICoEdge ce in (object[])outer.GetCoEdges())
            {
                var e = (IEdge)ce.GetEdge();
                var c = (ICurve)e.GetCurve();
                var p = (double[])e.GetCurveParams2();
                var a = new[] { p[0], p[1], p[2] };
                var b = new[] { p[3], p[4], p[5] };
                if (!ce.GetSense())
                {
                    var t = a;
                    a = b;
                    b = t;
                }
                segs.Add(c.IsLine() ? new FlashLayout.Segment { IsLine = true, A = To2(a), B = To2(b) } : c.IsCircle() ? new FlashLayout.Segment { A = To2(a), B = To2(b), R = ((double[])c.CircleParams)[6] } : null);
                outline.Add(To2(a));
                outline.Add(To2(V3.Mul(V3.Add(a, b), 0.5)));
            }
            var poly = FlashLayout.FromSegments(segs, outline) ?? throw new MoldApiException("PocketCornerReliefs: the pocket outline isn't straight-sided (lines and corner arcs); relieve it by hand.");
            var r = PocketRelief.ReliefRadius(cutterRadius);
            // V[i] is where line i meets line i + 1; Rc[i] is the arc radius between them.
            var sharp = poly.Rc.Select(rc => rc < r - 1e-7).ToList();
            var centres = PocketRelief.DogBones(poly.V, r, sharp);
            if (centres.Count == 0) return null;
            var top = V3.Unit((double[])openingFace.Normal);
            var ppTop = (double[])((ISurface)openingFace.GetSurface()).PlaneParams;
            var hTop = ppTop[3] * top[0] + ppTop[4] * top[1] + ppTop[5] * top[2];
            var ppFloor = (double[])((ISurface)pocketFloor.GetSurface()).PlaneParams;
            var hFloorAlongTop = V3.Dot(new[] { ppFloor[3], ppFloor[4], ppFloor[5] }, top);
            var depth = Math.Abs(hTop - hFloorAlongTop);
            if (V3.Dot(top, n) < 0.9999 || depth < 1e-6) throw new MoldApiException("PocketCornerReliefs: the opening face must be parallel to the floor and above it.");
            var points = centres.Select(c2 =>
            {
                var onFloor = V3.Add(V3.Mul(frame.U, c2[0]), V3.Mul(frame.V, c2[1]));
                return V3.Add(onFloor, V3.Mul(top, hTop - V3.Dot(onFloor, top)));
            }).ToList();
            var f2 = Holes.Cut(Sw, model, name, openingFace, points, 2 * r, depth);
            return f2;
        }

        // ---------- analysis ----------

        /// <summary>The same analysis as the mold_part tool (draft, undercuts, outline, parting-line candidates, air traps, fragile steel).</summary>
        public PartReport Analyze(IBody2 body = null, PartAnalysisOptions options = null) =>
            PartAnalysis.Run(Sw, Model, body ?? PartBody() ?? throw new MoldApiException("Analyze: no body."), options, _g.Ct);

        /// <summary>The record folder of this mold (`&lt;mold file&gt;.mold\`), or null for a part never saved.</summary>
        public string RecordFolder() => SwGeo.RecordFolder(Model);
    }
}

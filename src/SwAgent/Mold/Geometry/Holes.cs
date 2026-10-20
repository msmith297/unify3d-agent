using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.Mold.Geometry
{
    /// <summary>
    /// Bolt holes [SolidWorks]: circles sketched on a planar face and cut blind - the pattern v1 verified - with the
    /// diameters from mold_calc (clearance, counterbore, tap drill or insert drill). The cut stops at its depth, so a
    /// counterbore through the lid doesn't reach the base: each piece gets its own cut from its own face. Hole Wizard
    /// (thread data in the model) waits for spike S11; until then the thread spec lives in the record's hardware list
    /// and the machining sheet. Each cut removes material from the face's own body only (measured), and each method
    /// measures the cylinders it made.
    /// </summary>
    internal static class Holes
    {
        public static IFeature Cbore(ISldWorks sw, IModelDoc2 model, string name, IFace2 face, List<double[]> points, double holeDia, double holeDepth, double cboreDia, double cboreDepth)
        {
            if (cboreDia <= holeDia) throw new MoldApiException("CboreHoles: the counterbore must be wider than the hole.");
            if (cboreDepth <= 0 || holeDepth <= cboreDepth) throw new MoldApiException("CboreHoles: give a counterbore depth > 0 and a hole depth (from the face) beyond it.");
            // The face object goes stale when the counterbore cuts it: keep its persistent reference to find it again.
            var faceRef = SwGeo.PersistRef(model, face);
            var cb = Cut(sw, model, (name ?? "MOLD Bolt holes") + " cbore", face, points, cboreDia, cboreDepth);
            IFace2 again = null;
            try
            {
                if (faceRef != null)
                {
                    var o = model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(faceRef), out var err);
                    if (err == (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok) again = o as IFace2;
                }
            }
            catch
            {
                again = null;
            }
            if (again == null) throw new MoldApiException("CboreHoles: the counterbores are cut, but the face wasn't found again for the through holes - cut them with TappedHoles-style holes from the face.");
            // The through hole starts at the same face (the original plane), so its depth is measured from there too.
            return Cut(sw, model, name ?? "MOLD Bolt holes", again, points, holeDia, holeDepth) ?? cb;
        }

        public static IFeature Tapped(ISldWorks sw, IModelDoc2 model, string name, IFace2 face, List<double[]> points, double drillDia, double depth, string thread)
        {
            var n = (name ?? "MOLD Tapped holes") + (string.IsNullOrWhiteSpace(thread) ? "" : $" {thread.Trim()}");
            return Cut(sw, model, n, face, points, drillDia, depth);
        }

        /// <summary>Circles of diameter d at the points (model meters, on the face's plane), cut blind `depth` into the face's side.</summary>
        /// <summary>The feature-scope selection mark that worked last (spike S11 settles it; until then each is measured).</summary>
        private static int? _scopeMark;

        /// <summary>
        /// Cut the sketch blind `depth` so that only `target` loses material, checked by measurement: every other body keeps
        /// its volume and centroid and the target loses some. First with auto-selected scope (right whenever the cut
        /// doesn't reach another body), then with feature scope on the target alone (UseAutoSelect=false, the body
        /// selected; the selection mark FeatureCut4 reads is unverified, so each candidate is tried and measured). A try
        /// that touches another body is deleted (the sketch is kept) and the next one runs; if none works, it throws.
        /// </summary>
        private static IFeature ScopedCut(IModelDoc2 model, string name, IFeature sketch, IBody2 target, List<(double V, double X, double Y, double Z)> others, double v0, double depth)
        {
            var targetSig = SwGeo.Signature(target);
            var tries = new List<int?> { null };
            if (_scopeMark.HasValue) tries.Add(_scopeMark);
            tries.AddRange(new int?[] { 8, 1, 2, 0 }.Where(m => m != _scopeMark));
            var tried = new List<string>();
            foreach (var mark in tries)
            {
                // The target as it is now (an undone try rebuilds the part; old body references may be stale).
                var body = SwGeo.Solids(model).FirstOrDefault(b => SwGeo.SameSignature(SwGeo.Signature(b), targetSig));
                if (body == null) throw new MoldApiException($"Holes '{name}': the target body wasn't found again after an undone try ({string.Join("; ", tried)}).");
                model.ClearSelection2(true);
                if (!sketch.Select2(false, 0)) throw new MoldApiException($"Holes '{name}': couldn't select the hole sketch.");
                if (mark.HasValue) SwGeo.SelectBody(model, body, true, mark.Value);
                var auto = !mark.HasValue;
                var f = model.FeatureManager.FeatureCut4(true, false, false, (int)swEndConditions_e.swEndCondBlind, 0, depth, 0, false, false, false, false, 0, 0,
                    false, false, false, false, false, true, auto, true, true, false, 0, 0, false, false);
                model.ClearSelection2(true);
                var label = auto ? "auto scope" : $"scope mark {mark}";
                if (f == null)
                {
                    tried.Add(label + ": no feature");
                    continue;
                }
                var changed = SwGeo.NewBodies(model, others);
                var kept = SwGeo.Solids(model).Count - changed.Count;
                var removed = v0 - changed.Sum(SwGeo.Volume);
                string problem = null;
                if (kept != others.Count) problem = $"it cut {others.Count - kept} other bod{(others.Count - kept == 1 ? "y" : "ies")}";
                else if (changed.Count == 0 || removed <= 1e-12) problem = "it removed nothing from the face's body";
                if (problem == null)
                {
                    if (mark.HasValue) _scopeMark = mark;
                    return f;
                }
                tried.Add(label + ": " + problem);
                // Undo the feature only; the sketch stays for the next try.
                model.ClearSelection2(true);
                if (f.Select2(false, 0)) model.Extension.DeleteSelection2(0);
                model.ClearSelection2(true);
            }
            SwGeo.DeleteFeatures(model, sketch);
            throw new MoldApiException($"Holes '{name}': no cut removed material from the face's body alone ({string.Join("; ", tried)}); nothing was changed. " +
                                       "Check the depth (it may run into the next body) or cut it by hand with Feature Scope set to that body.");
        }

        internal static IFeature Cut(ISldWorks sw, IModelDoc2 model, string name, IFace2 face, List<double[]> points, double d, double depth)
        {
            SwGeo.RequirePart(model);
            if (face == null || !(face.GetSurface() is ISurface s) || !s.IsPlane()) throw new MoldApiException("Holes: give the planar face the holes start from.");
            if (points == null || points.Count == 0) throw new MoldApiException("Holes: no hole positions.");
            if (d <= 0 || depth <= 0) throw new MoldApiException("Holes: diameter and depth must be positive.");
            var normal = V3.Unit((double[])face.Normal);
            var pp = (double[])s.PlaneParams;
            var h = pp[3] * normal[0] + pp[4] * normal[1] + pp[5] * normal[2];
            foreach (var p in points)
                if (p == null || p.Length < 3 || Math.Abs(V3.Dot(p, normal) - h) > 1e-6)
                    throw new MoldApiException("Holes: every position must lie on the face's plane (model meters).");
            // The cut is meant for the face's own body only (the design's multi-body rule): a blind cut through a lid must
            // not continue into the base, a pocket relief must not cut the insert sitting in the pocket.
            var target = face.GetBody() as IBody2 ?? throw new MoldApiException("Holes: the face isn't on a solid body.");
            var targetName = SwGeo.SafeName(target);
            var v0 = SwGeo.Volume(target);
            var others = SwGeo.Solids(model).Where(b => SwGeo.SafeName(b) != targetName).Select(SwGeo.Signature).ToList();
            var mu = (IMathUtility)sw.GetMathUtility();
            var sm = model.SketchManager;
            var adb = sm.AddToDB;
            model.ClearSelection2(true);
            if (!((IEntity)face).Select4(false, null)) throw new MoldApiException("Holes: couldn't select the face to sketch on.");
            sm.InsertSketch(true);
            IFeature sketch;
            try
            {
                sm.AddToDB = true;
                var sk = model.GetActiveSketch2() as ISketch ?? throw new MoldApiException("Holes: no sketch opened on the face.");
                var t = (IMathTransform)sk.ModelToSketchTransform;
                foreach (var p in points)
                {
                    var q = (double[])((IMathPoint)((IMathPoint)mu.CreatePoint(p)).MultiplyTransform(t)).ArrayData;
                    sm.CreateCircleByRadius(q[0], q[1], 0, d / 2);
                }
            }
            finally
            {
                sm.AddToDB = adb;
                sm.InsertSketch(true);
            }
            sketch = model.Extension.GetLastFeatureAdded() as IFeature ?? throw new MoldApiException("Holes: the sketch wasn't created.");
            var f = ScopedCut(model, name, sketch, target, others, v0, depth);
            // Measure: a cylinder of radius d/2 whose axis passes through each point.
            var cylinders = ((f.GetFaces() as object[]) ?? new object[0]).OfType<IFace2>()
                .Select(x => x.GetSurface() as ISurface).Where(x => x != null && x.IsCylinder())
                .Select(x => (double[])x.CylinderParams).Where(c => c != null && Math.Abs(c[6] - d / 2) < 1e-6).ToList();
            var missing = points.Count(p => !cylinders.Any(c =>
            {
                var axis = V3.Unit(new[] { c[3], c[4], c[5] });
                var v = V3.Sub(p, new[] { c[0], c[1], c[2] });
                return V3.Len(V3.Sub(v, V3.Mul(axis, V3.Dot(v, axis)))) < 1e-5;
            }));
            if (missing > 0)
            {
                SwGeo.DeleteFeatures(model, f, sketch);
                throw new MoldApiException($"Holes '{name}': {missing} of {points.Count} holes of Ø{d * 1000:0.###} mm weren't cut where asked (removed). Is the face on the body, and the depth into it?");
            }
            SwGeo.Rename(model, f, name);
            return f;
        }
    }
}

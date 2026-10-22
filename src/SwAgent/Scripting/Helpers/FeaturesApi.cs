using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.Mold.Geometry;
using SwAgent.Scripting.Extensions;

namespace SwAgent.Scripting.Helpers
{
    /// <summary>
    /// Features.* in code cells [SolidWorks]: extrude, cut, revolve and fillet from a sketch or edges with one call
    /// instead of FeatureExtrusion3's 23 and FeatureCut4's 27 positional arguments. Lengths in meters, angles in degrees.
    /// Each one checks its result by measurement (the end face lies at the asked depth on the asked side, material was
    /// added or removed, bodies outside the scope are untouched), tries the documented alternatives (the other
    /// direction, other scope selection marks) and keeps the sketch if it has to give up. "Along the sketch normal" is
    /// the sketch's +Z (for a sketch on a face: out of the face).
    /// </summary>
    public sealed class FeaturesApi
    {
        private readonly ScriptGlobals _g;

        // Learned this session: what Dir = false does for extrudes and cuts, the feature-scope body mark that works.
        private static bool? _extrudeFalseIsAlong;
        private static bool? _cutFalseIsAgainst;
        private static int? _scopeMark;

        /// <summary>Depth for Features.Cut: through all.</summary>
        public double ThroughAll => double.PositiveInfinity;

        internal FeaturesApi(ScriptGlobals g)
        {
            _g = g;
        }

        private IModelDoc2 Part(string what) => HelperOps.RequirePart(_g.Model, what);

        /// <summary>The feature the last Features.* call made.</summary>
        public IFeature LastFeature { get; private set; }

        // ---------- finding ----------

        [Helper("A feature by its tree name in a part, assembly or drawing, or null.", "IPartDoc.FeatureByName", "IAssemblyDoc.FeatureByName", "IDrawingDoc.FeatureByName")]
        public IFeature ByName(string name) => (_g.Model ?? throw new SwHelperException("Features.ByName: no document is open.")).FeatureByName(name);

        [Helper("The feature added last, or null.")]
        public IFeature Last() => _g.Model?.LastFeature();

        [Helper("Delete features with their absorbed sketches. Returns true if SolidWorks deleted them.")]
        public bool Delete(params IFeature[] features) => SwGeo.DeleteFeatures(_g.Model ?? throw new SwHelperException("Features.Delete: no document is open."), features);

        [Helper("Rebuild and list the features with errors (not warnings) as \"name (error code)\".")]
        public IList<string> Errors() => SwGeo.RebuildErrors(_g.Model ?? throw new SwHelperException("Features.Errors: no document is open."));

        // ---------- extrude, cut ----------

        [Helper("Extrude a closed sketch (IFeature, ISketch, name, or null = the last sketch) by depth meters along its normal (reverse: the other way; midPlane: half each way), merged into touching bodies unless merge is false. Checked: the end face lies at that depth on that side and material was added. Returns the feature.",
            "IFeatureManager.FeatureExtrusion3", "IFeatureManager.FeatureExtrusion2")]
        public IFeature Extrude(object sketch, double depth, bool reverse = false, bool merge = true, bool midPlane = false)
        {
            const string what = "Features.Extrude";
            var model = Part(what);
            if (!(depth > 0) || double.IsInfinity(depth)) throw new SwHelperException($"{what}: depth must be a positive length in meters (mm(10)).");
            var sk = Sketch(model, sketch, what);
            var (o, n) = SketchPlane(sk);
            var v0 = SwGeo.Solids(model).Sum(SwGeo.Volume);
            var count0 = SwGeo.Solids(model).Count;
            var falseIsAlong = _extrudeFalseIsAlong ?? true;
            var first = reverse == falseIsAlong; // the Dir flag that should give the asked side
            var tried = new List<string>();
            foreach (var dir in midPlane ? new[] { false } : new[] { first, !first })
            {
                var f = Select(model, sk, null, 0, what) ? model.FeatureManager.FeatureExtrusion3(Sd: true, Flip: false, Dir: dir,
                    T1: midPlane ? (int)swEndConditions_e.swEndCondMidPlane : (int)swEndConditions_e.swEndCondBlind, T2: 0, D1: depth, D2: 0,
                    Dchk1: false, Dchk2: false, Ddir1: false, Ddir2: false, Dang1: 0, Dang2: 0, OffsetReverse1: false, OffsetReverse2: false,
                    TranslateSurface1: false, TranslateSurface2: false, Merge: merge, UseFeatScope: true, UseAutoSelect: true,
                    T0: (int)swStartConditions_e.swStartSketchPlane, StartOffset: 0, FlipStartOffset: false) : null;
                model.ClearSelection2(true);
                var label = $"Dir={dir}";
                if (f == null)
                {
                    tried.Add(label + ": no feature");
                    continue;
                }
                var caps = n == null ? new List<double>() : PlanarOffsets(f, o, n);
                var tol = 1e-7 + 1e-6 * depth;
                bool Has(double h) => caps.Any(c => Math.Abs(c - h) < tol);
                var added = SwGeo.Solids(model).Sum(SwGeo.Volume) - v0;
                string problem = HelperOps.Error(f) is string e ? "rebuild error " + e
                    : !(added > 1e-15) ? "it added no material (is the sketch closed, and not inside a body already?)"
                    : !merge && SwGeo.Solids(model).Count <= count0 ? "merge is off but no new body appeared"
                    : null;
                if (problem == null && n != null)
                {
                    if (midPlane)
                    {
                        if (caps.Count > 0 && !(Has(depth / 2) && Has(-depth / 2))) problem = $"the end faces aren't at +-{HelperOps.Mm(depth / 2)} mm from the sketch";
                    }
                    else
                    {
                        var want = reverse ? -depth : depth;
                        if (Has(-want) && !Has(want)) problem = "it went the other way";
                        else if (caps.Count > 0 && !Has(want)) problem = $"no end face at {HelperOps.Mm(want)} mm from the sketch (found {string.Join(", ", caps.Select(HelperOps.Mm))} mm)";
                        if (Has(want) || Has(-want)) _extrudeFalseIsAlong = Has(depth) ^ dir; // Dir=false went along iff the face is at +depth
                    }
                }
                if (problem == null)
                {
                    LastFeature = f;
                    return f;
                }
                tried.Add(label + ": " + problem);
                DeleteOnly(model, f);
            }
            throw new SwHelperException($"{what} of '{sk.Name}': no extrusion came out right ({string.Join("; ", tried)}); the sketch is kept, nothing else changed.");
        }

        [Helper("Cut a closed sketch (IFeature, ISketch, name, or null = the last sketch) depth meters deep (Features.ThroughAll for through all) into the bodies in scope (null = all). reverse null = whichever side removes material (into the face first), false = against the sketch normal, true = along it; bothSides cuts both ways. Checked: material removed, bodies outside the scope untouched, the floor at that depth. Returns the feature.",
            "IFeatureManager.FeatureCut4", "IFeatureManager.FeatureCut3")]
        public IFeature Cut(object sketch, double depth, IEnumerable<IBody2> scope = null, bool? reverse = null, bool bothSides = false)
        {
            const string what = "Features.Cut";
            var model = Part(what);
            var through = double.IsPositiveInfinity(depth);
            if (!through && !(depth > 0)) throw new SwHelperException($"{what}: depth must be a positive length in meters (mm(5)) or Features.ThroughAll.");
            var sk = Sketch(model, sketch, what);
            var (o, n) = SketchPlane(sk);
            var scopeList = scope == null ? null : HelperOps.Distinct(scope, what);
            var falseIsAgainst = _cutFalseIsAgainst ?? true;
            // Dir flags in the order to try: the asked side, or (auto) into the face first, then out of it.
            var wantAgainst = reverse.HasValue ? !reverse.Value : true;
            var firstDir = wantAgainst != falseIsAgainst;
            var dirs = bothSides ? new[] { false } : reverse.HasValue ? new[] { firstDir, !firstDir } : new[] { firstDir, !firstDir };
            var tried = new List<string>();
            foreach (var dir in dirs)
            {
                foreach (var mark in scopeList == null ? new int?[] { null } : HelperOps.Marks(_scopeMark, 0, 8, 1, 2).Select(m => (int?)m))
                {
                    var r = Remove(model, sk, scopeList, mark, what, () => model.FeatureManager.FeatureCut4(Sd: !bothSides, Flip: false, Dir: dir,
                        T1: through ? (int)swEndConditions_e.swEndCondThroughAll : (int)swEndConditions_e.swEndCondBlind,
                        T2: bothSides ? (through ? (int)swEndConditions_e.swEndCondThroughAll : (int)swEndConditions_e.swEndCondBlind) : 0,
                        D1: through ? 0 : depth, D2: bothSides && !through ? depth : 0,
                        Dchk1: false, Dchk2: false, Ddir1: false, Ddir2: false, Dang1: 0, Dang2: 0, OffsetReverse1: false, OffsetReverse2: false,
                        TranslateSurface1: false, TranslateSurface2: false, NormalCut: false, UseFeatScope: true, UseAutoSelect: scopeList == null,
                        AssemblyFeatureScope: false, AutoSelectComponents: false, PropagateFeatureToParts: false,
                        T0: (int)swStartConditions_e.swStartSketchPlane, StartOffset: 0, FlipStartOffset: false, OptimizeGeometry: false));
                    var label = $"Dir={dir}" + (mark.HasValue ? $", scope mark {mark}" : "");
                    if (r.Feature == null)
                    {
                        tried.Add(label + ": " + r.Problem);
                        if (r.Problem.StartsWith("it removed nothing", StringComparison.Ordinal)) break; // the other side, not another mark
                        continue;
                    }
                    if (!bothSides && !through && n != null)
                    {
                        var floors = PlanarOffsets(r.Feature, o, n);
                        var tol = 1e-7 + 1e-6 * depth;
                        bool Has(double h) => floors.Any(c => Math.Abs(c - h) < tol);
                        if (Has(depth) || Has(-depth)) _cutFalseIsAgainst = Has(-depth) ^ dir; // Dir=false cut against iff the floor is at -depth
                        if (reverse.HasValue)
                        {
                            var want = wantAgainst ? -depth : depth;
                            string problem = Has(-want) && !Has(want) ? "it cut the other way"
                                : floors.Count > 0 && !Has(want) ? $"no floor at {HelperOps.Mm(want)} mm from the sketch (found {string.Join(", ", floors.Select(HelperOps.Mm))} mm)" : null;
                            if (problem != null)
                            {
                                tried.Add(label + ": " + problem);
                                DeleteOnly(model, r.Feature);
                                break;
                            }
                        }
                    }
                    if (mark.HasValue) _scopeMark = mark;
                    LastFeature = r.Feature;
                    return r.Feature;
                }
            }
            throw new SwHelperException($"{what} of '{sk.Name}': no cut came out right ({string.Join("; ", tried)}); the sketch is kept, nothing else changed." +
                                        (scopeList != null ? " Check the depth reaches into the scope bodies." : ""));
        }

        // ---------- revolve ----------

        [Helper("Revolve a sketch about its centerline (or `axis`: a sketch line, edge or axis feature) by degrees, merged unless merge is false. Checked: no rebuild error and material added. Returns the feature.",
            "IFeatureManager.FeatureRevolve2", "IFeatureManager.FeatureRevolve")]
        public IFeature Revolve(object sketch, double degrees = 360, object axis = null, bool merge = true) => RevolveCore("Features.Revolve", sketch, degrees, axis, false, merge, null);

        [Helper("Revolve-cut a sketch about its centerline (or `axis`) by degrees from the bodies in scope (null = all). Checked: material removed, bodies outside the scope untouched. Returns the feature.",
            "IFeatureManager.FeatureRevolve2", "IFeatureManager.FeatureRevolveCut")]
        public IFeature RevolveCut(object sketch, double degrees = 360, object axis = null, IEnumerable<IBody2> scope = null) => RevolveCore("Features.RevolveCut", sketch, degrees, axis, true, true, scope);

        private IFeature RevolveCore(string what, object sketch, double degrees, object axis, bool cut, bool merge, IEnumerable<IBody2> scope)
        {
            var model = Part(what);
            var angle = degrees * Math.PI / 180;
            if (!(angle > 1e-9) || angle > 2 * Math.PI + 1e-9) throw new SwHelperException($"{what}: degrees must be in 0..360.");
            angle = Math.Min(angle, 2 * Math.PI);
            var sk = Sketch(model, sketch, what);
            var centerlines = ((sk.GetSpecificFeature2() as ISketch)?.GetSketchSegments()).AsObjects<ISketchSegment>()
                .Where(s => s.ConstructionGeometry && s.GetType() == (int)swSketchSegments_e.swSketchLINE).ToList();
            if (axis == null && centerlines.Count == 0) throw new SwHelperException($"{what}: '{sk.Name}' has no centerline - add one (construction line) or pass `axis`.");
            var axisEntity = axis ?? (centerlines.Count == 1 ? centerlines[0] : null);
            var scopeList = scope == null ? null : HelperOps.Distinct(scope, what);
            // Sketch alone (SolidWorks takes a single centerline itself), then the axis selected with the marks revolves read.
            var axisMarks = new int?[] { null }.Concat(axisEntity != null ? new int?[] { 4, 16, 1, 0 } : new int?[0]).ToList();
            var tried = new List<string>();
            foreach (var axisMark in axisMarks)
            {
                foreach (var mark in scopeList == null ? new int?[] { null } : HelperOps.Marks(_scopeMark, 0, 8, 1, 2).Select(m => (int?)m))
                {
                    var label = (axisMark.HasValue ? $"axis mark {axisMark}" : "sketch only") + (mark.HasValue ? $", scope mark {mark}" : "");
                    Func<IFeature> make = () =>
                    {
                        if (axisMark.HasValue && !SelectAxis(model, axisEntity, axisMark.Value)) return null;
                        return model.FeatureManager.FeatureRevolve2(SingleDir: true, IsSolid: true, IsThin: false, IsCut: cut, ReverseDir: false,
                            BothDirectionUpToSameEntity: false, Dir1Type: (int)swEndConditions_e.swEndCondBlind, Dir2Type: 0, Dir1Angle: angle, Dir2Angle: 0,
                            OffsetReverse1: false, OffsetReverse2: false, OffsetDistance1: 0, OffsetDistance2: 0, ThinType: 0, ThinThickness1: 0, ThinThickness2: 0,
                            Merge: merge, UseFeatScope: true, UseAutoSelect: scopeList == null);
                    };
                    if (cut)
                    {
                        var r = Remove(model, sk, scopeList, mark, what, make);
                        if (r.Feature != null)
                        {
                            if (mark.HasValue) _scopeMark = mark;
                            LastFeature = r.Feature;
                            return r.Feature;
                        }
                        tried.Add(label + ": " + r.Problem);
                        continue;
                    }
                    var v0 = SwGeo.Solids(model).Sum(SwGeo.Volume);
                    var f = Select(model, sk, null, 0, what) ? make() : null;
                    model.ClearSelection2(true);
                    if (f == null)
                    {
                        tried.Add(label + ": no feature");
                        continue;
                    }
                    var added = SwGeo.Solids(model).Sum(SwGeo.Volume) - v0;
                    var problem = HelperOps.Error(f) is string e ? "rebuild error " + e : !(added > 1e-15) ? "it added no material" : null;
                    if (problem == null)
                    {
                        LastFeature = f;
                        return f;
                    }
                    tried.Add(label + ": " + problem);
                    DeleteOnly(model, f);
                }
            }
            throw new SwHelperException($"{what} of '{sk.Name}': no revolve came out right ({string.Join("; ", tried)}); the sketch is kept, nothing else changed. " +
                                        "The profile must be closed and stay on one side of the axis.");
        }

        private static bool SelectAxis(IModelDoc2 model, object axis, int mark)
        {
            var data = model.CreateSelectData(mark);
            switch (axis)
            {
                case ISketchSegment s:
                    return s.Select4(true, data);
                case IFeature f:
                    return f.Select2(true, mark);
                case IEntity e:
                    return e.Select4(true, data);
            }
            throw new SwHelperException($"The revolve axis must be a sketch line, an edge or an axis feature, not a {VariantExtensions.TypeName(axis)}.");
        }

        // ---------- fillet ----------

        [Helper("Round edges with radius meters, one fillet feature (one per body if SolidWorks refuses them together). Checked: no rebuild error, round faces of that radius, the bodies' volume changed. Returns the (last) feature.",
            "IFeatureManager.FeatureFillet3", "IModelDoc2.FeatureFillet3")]
        public IFeature Fillet(IEnumerable<IEdge> edges, double radius)
        {
            const string what = "Features.Fillet";
            var model = Part(what);
            var list = (edges ?? Enumerable.Empty<IEdge>()).Where(e => e != null).ToList();
            if (list.Count == 0) throw new SwHelperException($"{what}: no edges given.");
            if (!(radius > 0)) throw new SwHelperException($"{what}: the radius must be positive (meters, mm(1)).");
            var refs = list.Select(e => SwGeo.PersistRef(model, e)).ToList();
            var groups = list.Select((e, i) => (Edge: e, Ref: refs[i], Body: SwGeo.SafeName(e.GetBody()))).GroupBy(x => x.Body).ToList();
            var tried = new List<string>();
            // All edges in one feature; if SolidWorks refuses, one feature per body (the user's flash grooves do it that way).
            var f = FilletBatch(model, refs, list, radius, what, tried);
            if (f != null)
            {
                LastFeature = f;
                return f;
            }
            if (groups.Count > 1)
            {
                var made = new List<IFeature>();
                foreach (var g in groups)
                {
                    var fg = FilletBatch(model, g.Select(x => x.Ref).ToList(), g.Select(x => x.Edge).ToList(), radius, what, tried);
                    if (fg == null) break;
                    made.Add(fg);
                }
                if (made.Count == groups.Count)
                {
                    LastFeature = made.Last();
                    return LastFeature;
                }
                if (made.Count > 0) SwGeo.DeleteFeatures(model, made.ToArray());
            }
            throw new SwHelperException($"{what}: no fillet came out right ({string.Join("; ", tried.Distinct())}); nothing was changed. Is the radius too big for the faces next to the edges?");
        }

        public IFeature Fillet(IEdge edge, double radius) => Fillet(new[] { edge }, radius);

        private static IFeature FilletBatch(IModelDoc2 model, List<string> refs, List<IEdge> edges, double radius, string what, List<string> tried)
        {
            var bodyNames = new HashSet<string>(edges.Select(e => SwGeo.SafeName(e.GetBody())), StringComparer.Ordinal);
            foreach (var options in new[] { (int)swFeatureFilletOptions_e.swFeatureFilletUniformRadius,
                         (int)(swFeatureFilletOptions_e.swFeatureFilletUniformRadius | swFeatureFilletOptions_e.swFeatureFilletPropagate), 195 })
            {
                // Edges as they are now (an undone try rebuilds the part): by persistent reference, else the object given.
                var current = refs.Select((r, i) => Resolve(model, r) as IEdge ?? edges[i]).ToList();
                var v0 = SwGeo.Solids(model).Where(b => bodyNames.Contains(SwGeo.SafeName(b))).Sum(SwGeo.Volume);
                model.ClearSelection2(true);
                IFeature f;
                try
                {
                    if (current.Any(e => !((IEntity)e).Select4(true, null))) throw new SwHelperException($"{what}: couldn't select an edge (consumed by a later feature? get it again).");
                    f = model.FeatureManager.FeatureFillet3(Options: options, R1: radius, R2: 0, Rho: 0, Ftyp: 0, OverflowType: 0, ConicRhoType: 0,
                        Radii: null, Dist2Arr: null, RhoArr: null, SetBackDistances: null, PointRadiusArray: null, PointDist2Array: null, PointRhoArray: null) as IFeature;
                }
                finally
                {
                    model.ClearSelection2(true);
                }
                var label = $"options {options}";
                if (f == null)
                {
                    tried.Add(label + ": no feature");
                    continue;
                }
                var v1 = SwGeo.Solids(model).Where(b => bodyNames.Contains(SwGeo.SafeName(b))).Sum(SwGeo.Volume);
                var round = f.GetFaces().AsObjects<IFace2>().Count(face => HasRadius(face, radius));
                var problem = HelperOps.Error(f) is string e ? "rebuild error " + e
                    : round == 0 ? $"no round face of R{HelperOps.Mm(radius)} mm"
                    : !(Math.Abs(v1 - v0) > 1e-15) ? "the volume didn't change" : null;
                if (problem == null) return f;
                tried.Add(label + ": " + problem);
                SwGeo.DeleteFeatures(model, f);
            }
            return null;
        }

        private static bool HasRadius(IFace2 face, double r)
        {
            try
            {
                if (!(face.GetSurface() is ISurface s)) return false;
                if (s.IsCylinder()) return Math.Abs(s.CylinderParams.AsDoubles()[6] - r) < 1e-7 + 1e-6 * r;
                if (s.IsTorus())
                {
                    var t = s.TorusParams.AsDoubles();
                    return t.Length > 7 && Math.Abs(t[7] - r) < 1e-7 + 1e-6 * r;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Features.Fillet: reading a face: " + ex.Message);
            }
            return false;
        }

        private static object Resolve(IModelDoc2 model, string persistRef)
        {
            if (persistRef == null) return null;
            try
            {
                var o = model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(persistRef), out var err);
                return err == (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok ? o : null;
            }
            catch
            {
                return null;
            }
        }

        // ---------- shared ----------

        /// <summary>
        /// Run a material-removing feature from the sketch with an optional body scope (bodies at `mark`), and check it:
        /// material removed from the scope, everything outside it untouched. A failed try is deleted (the sketch kept).
        /// </summary>
        private (IFeature Feature, string Problem) Remove(IModelDoc2 model, IFeature sketch, List<IBody2> scope, int? mark, string what, Func<IFeature> make)
        {
            var scopeNames = scope == null ? null : new HashSet<string>(scope.Select(SwGeo.SafeName), StringComparer.Ordinal);
            var all = SwGeo.Solids(model);
            var inScope = all.Where(b => scopeNames == null || scopeNames.Contains(SwGeo.SafeName(b))).ToList();
            var outside = all.Where(b => scopeNames != null && !scopeNames.Contains(SwGeo.SafeName(b))).Select(SwGeo.Signature).ToList();
            if (scope != null && inScope.Count == 0) throw new SwHelperException($"{what}: none of the scope bodies is in the part any more (get them again after earlier features).");
            var v0 = inScope.Sum(SwGeo.Volume);
            IFeature f;
            try
            {
                f = Select(model, sketch, mark.HasValue ? inScope : null, mark ?? 0, what) ? make() : null;
            }
            finally
            {
                model.ClearSelection2(true);
            }
            if (f == null) return (null, "no feature");
            var changed = SwGeo.NewBodies(model, outside);
            var removed = v0 - changed.Sum(SwGeo.Volume);
            var problem = HelperOps.Error(f) is string e ? "rebuild error " + e
                : SwGeo.Solids(model).Count != outside.Count + changed.Count ? "it cut bodies outside the scope"
                : !(removed > 1e-15) ? "it removed nothing (wrong side, or not reaching the bodies)"
                : null;
            if (problem == null) return (f, null);
            DeleteOnly(model, f);
            return (null, problem);
        }

        /// <summary>The sketch, the scope bodies at `mark` (if any): the selection a feature call reads. False if the sketch can't be selected.</summary>
        private static bool Select(IModelDoc2 model, IFeature sketch, List<IBody2> scope, int mark, string what)
        {
            model.ClearSelection2(true);
            if (!sketch.Select2(false, 0)) return false;
            if (scope != null) HelperOps.SelectBodies(model, scope, mark, what);
            return true;
        }

        /// <summary>The sketch feature from what the cell passed; an open sketch that is this one is closed first.</summary>
        private static IFeature Sketch(IModelDoc2 model, object sketch, string what)
        {
            IFeature f;
            switch (sketch)
            {
                case null:
                    f = model.LastFeature();
                    break;
                case string name:
                    f = model.FeatureByName(name) ?? throw new SwHelperException($"{what}: there is no feature named '{name}'.");
                    break;
                case IFeature feature:
                    f = feature;
                    break;
                case ISketch s:
                    f = (IFeature)s;
                    break;
                default:
                    throw new SwHelperException($"{what}: give the sketch as its feature, ISketch or name, not a {VariantExtensions.TypeName(sketch)}.");
            }
            var type = f?.GetTypeName2();
            if (type != "ProfileFeature" && type != "3DProfileFeature")
                throw new SwHelperException(f == null ? $"{what}: no sketch given, and the last feature isn't one." : $"{what}: '{f.Name}' is a {type}, not a sketch.");
            if (model.GetActiveSketch2() is ISketch active)
            {
                if ((active as IFeature)?.Name == f.Name) model.SketchManager.InsertSketch(true);
                else throw new SwHelperException($"{what}: another sketch is open for editing - close it first (Model.SketchManager.InsertSketch(true)).");
            }
            return f;
        }

        /// <summary>The sketch plane's origin and normal (+Z of the sketch) in model meters; (null, null) for a 3D sketch.</summary>
        private (double[] O, double[] N) SketchPlane(IFeature sketch)
        {
            try
            {
                if (sketch.GetTypeName2() != "ProfileFeature" || !(sketch.GetSpecificFeature2() is ISketch sk) || _g.swApp == null) return (null, null);
                var mu = (IMathUtility)_g.swApp.GetMathUtility();
                var inv = (MathTransform)((IMathTransform)sk.ModelToSketchTransform).Inverse();
                double[] P(double z) => ((IMathPoint)((IMathPoint)mu.CreatePoint(new[] { 0.0, 0.0, z })).MultiplyTransform(inv)).ArrayData.AsDoubles();
                var o = P(0);
                return (o, V3.Unit(V3.Sub(P(1), o)));
            }
            catch (Exception ex)
            {
                Log.Warn("Features: the sketch plane: " + ex.Message);
                return (null, null);
            }
        }

        /// <summary>Signed offsets from the sketch plane of the feature's planar faces parallel to it (end faces, floors).</summary>
        private static List<double> PlanarOffsets(IFeature f, double[] o, double[] n)
        {
            var result = new List<double>();
            foreach (var face in f.GetFaces().AsObjects<IFace2>())
            {
                try
                {
                    if (!(face.GetSurface() is ISurface s) || !s.IsPlane()) continue;
                    var pp = s.PlaneParams.AsDoubles();
                    if (pp.Length < 6 || Math.Abs(Math.Abs(V3.Dot(V3.Unit(new[] { pp[0], pp[1], pp[2] }), n)) - 1) > 1e-9) continue;
                    result.Add(V3.Dot(V3.Sub(new[] { pp[3], pp[4], pp[5] }, o), n));
                }
                catch (Exception ex)
                {
                    Log.Warn("Features: reading a face: " + ex.Message);
                }
            }
            return result;
        }

        /// <summary>Delete a feature but keep the sketch it absorbed (for the next try).</summary>
        private static void DeleteOnly(IModelDoc2 model, IFeature f)
        {
            try
            {
                model.ClearSelection2(true);
                if (f.Select2(false, 0)) model.Extension.DeleteSelection2(0);
            }
            catch (Exception ex)
            {
                Log.Warn("Features: removing a failed try: " + ex.Message);
            }
            finally
            {
                model.ClearSelection2(true);
            }
        }
    }
}

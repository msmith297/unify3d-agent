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
    /// Bodies.* in code cells [SolidWorks]: move, turn, copy, combine, split and file solid bodies with one feature each,
    /// every result measured. Lengths in meters (mm(...)), angles in degrees. SolidWorks' own calls for these take a dozen
    /// positional arguments whose selection marks and rotation slots are undocumented or surprising (InsertMoveCopyBody2's
    /// RotAngleX does not simply turn about X): each helper tries the documented alternatives, keeps the one whose
    /// measured result is right, removes the others, and remembers what worked for the next call. Bodies change identity
    /// with every feature: each helper returns the bodies as they are now (Bodies.Last too), so don't reuse old references.
    /// </summary>
    public sealed class BodiesApi
    {
        private readonly ScriptGlobals _g;

        // What worked last time (this SolidWorks session): the Move/Copy selection mark, which rotation slot turns
        // about which model axis and with which sign, and whether the rotation point is honoured.
        private static int? _moveMark;
        private static readonly Dictionary<int, (int Axis, int Sign)> SlotMap = new Dictionary<int, (int Axis, int Sign)>();
        private static bool? _rotPointHonored;
        private static readonly object Gate = new object();

        internal BodiesApi(ScriptGlobals g)
        {
            _g = g;
        }

        private IModelDoc2 Part(string what) => HelperOps.RequirePart(_g.Model, what);

        /// <summary>The bodies the last Bodies.* call made or changed (copies, the cut target, split pieces...).</summary>
        public IList<IBody2> Last { get; private set; } = new List<IBody2>();

        /// <summary>The feature the last Bodies.* call made (rename it with LastFeature.Name = "...").</summary>
        public IFeature LastFeature { get; private set; }

        private T Done<T>(T result, IFeature feature, IEnumerable<IBody2> bodies)
        {
            LastFeature = feature;
            Last = bodies.ToList();
            return result;
        }

        // ---------- finding ----------

        [Helper("The part's solid bodies (visible only if asked).")]
        public IList<IBody2> All(bool visibleOnly = false) => SwGeo.Solids(Part("Bodies.All"), visibleOnly);

        [Helper("A solid body by its name (as in the Solid Bodies folder), or null.")]
        public IBody2 Named(string name) => SwGeo.Solids(Part("Bodies.Named")).FirstOrDefault(b => string.Equals(SwGeo.SafeName(b), name, StringComparison.Ordinal));

        // ---------- move, copy, turn ----------

        [Helper("Move a body (or a list) by dx, dy, dz meters with one Move/Copy Body feature; checked: each centroid moved by exactly that, volume kept. Returns the moved body.",
            "IFeatureManager.InsertMoveCopyBody2")]
        public IBody2 Move(IBody2 body, double dx, double dy, double dz) => Move(new[] { body }, dx, dy, dz)[0];

        public IList<IBody2> Move(IEnumerable<IBody2> bodies, double dx, double dy, double dz)
        {
            var model = Part("Bodies.Move");
            var list = HelperOps.Distinct(bodies, "Bodies.Move");
            var t = new[] { dx, dy, dz };
            if (V3.Len(t) < 1e-12) throw new SwHelperException("Bodies.Move: nothing to do (the move is zero).");
            var (f, moved) = Translate(model, list, t, "Bodies.Move");
            return Done(moved, f, moved);
        }

        [Helper("Copy a body (or a list) n times, copy k at k*(dx, dy, dz) meters (0, 0, 0: in place), one feature; checked: n new bodies per body with its volume, each centroid where it belongs. Returns the copies (originals unchanged).",
            "IFeatureManager.InsertMoveCopyBody2")]
        public IList<IBody2> Copy(IBody2 body, double dx, double dy, double dz, int n = 1) => Copy(new[] { body }, dx, dy, dz, n);

        public IList<IBody2> Copy(IEnumerable<IBody2> bodies, double dx, double dy, double dz, int n = 1)
        {
            var model = Part("Bodies.Copy");
            var list = HelperOps.Distinct(bodies, "Bodies.Copy");
            if (n < 1) throw new SwHelperException("Bodies.Copy: n (the number of copies) must be at least 1.");
            var t = new[] { dx, dy, dz };
            var shapes = list.Select(b => BodyShape.Take(b, false)).ToList();
            var tried = new List<string>();
            foreach (var mark in HelperOps.Marks(_moveMark, 1, 0))
            {
                if (tried.Count > 0) list = HelperOps.Refind(model, shapes, "Bodies.Copy");
                var namesBefore = new HashSet<string>(SwGeo.Solids(model).Select(SwGeo.SafeName), StringComparer.Ordinal);
                var sigBefore = SwGeo.Solids(model).Select(SwGeo.Signature).ToList();
                var f = MoveCopy(model, list, t, HelperOps.Zero, HelperOps.Zero, true, n, mark, "Bodies.Copy");
                if (f == null)
                {
                    tried.Add($"mark {mark}: no feature");
                    continue;
                }
                // copies in place share the original's volume and centroid: tell them apart by name
                var byName = SwGeo.Solids(model).Where(b => !namesBefore.Contains(SwGeo.SafeName(b))).ToList();
                var made = byName.Count == n * list.Count ? byName : SwGeo.NewBodies(model, sigBefore);
                var ordered = new List<IBody2>();
                string problem = made.Count != n * list.Count ? $"made {made.Count} bodies, expected {n * list.Count}" : null;
                var pool = made.ToList();
                for (var i = 0; i < shapes.Count && problem == null; i++)
                {
                    for (var k = 1; k <= n && problem == null; k++)
                    {
                        var want = V3.Add(shapes[i].C, V3.Mul(t, k));
                        var j = pool.FindIndex(b => Math.Abs(SwGeo.Volume(b) / shapes[i].V - 1) < 1e-4 && SwGeo.Centroid(b) is double[] c && V3.Dist(c, want) < 1e-5);
                        if (j < 0) problem = $"copy {k} of '{shapes[i].Name}' isn't at original + {k} x ({HelperOps.Mm(dx)}, {HelperOps.Mm(dy)}, {HelperOps.Mm(dz)}) mm or changed volume";
                        else
                        {
                            ordered.Add(pool[j]);
                            pool.RemoveAt(j);
                        }
                    }
                }
                if (problem == null)
                {
                    _moveMark = mark;
                    return Done(ordered, f, ordered);
                }
                tried.Add($"mark {mark}: {problem}");
                SwGeo.DeleteFeatures(model, f);
            }
            throw new SwHelperException($"Bodies.Copy: no copy came out right ({string.Join("; ", tried)}); nothing was changed.");
        }

        [Helper("Turn a body (or a list) by degrees (not deg()) about \"X\"/\"Y\"/\"Z\" (\"-Z\"...) or a vector, through the point `about` (null: the origin; body.Centroid(): in place). Right-hand rule; checked by measuring the turned shape; the Move/Copy rotation slot and sign that give the asked turn are found by measurement and remembered. Returns the turned body.",
            "IFeatureManager.InsertMoveCopyBody2")]
        public IBody2 Rotate(IBody2 body, string axis, double degrees, double[] about = null) => Rotate(new[] { body }, RotationMath.AxisVector(axis), degrees, about)[0];

        public IBody2 Rotate(IBody2 body, double[] axis, double degrees, double[] about = null) => Rotate(new[] { body }, axis, degrees, about)[0];

        public IList<IBody2> Rotate(IEnumerable<IBody2> bodies, string axis, double degrees, double[] about = null) => Rotate(bodies, RotationMath.AxisVector(axis), degrees, about);

        public IList<IBody2> Rotate(IEnumerable<IBody2> bodies, double[] axis, double degrees, double[] about = null)
        {
            var model = Part("Bodies.Rotate");
            var list = HelperOps.Distinct(bodies, "Bodies.Rotate");
            if (axis == null || axis.Length < 3 || V3.Len(axis) < 1e-12) throw new SwHelperException("Bodies.Rotate: give the axis as \"X\", \"Y\", \"Z\" or a vector new[]{x, y, z}.");
            if (about != null && about.Length < 3) throw new SwHelperException("Bodies.Rotate: `about` is a point new[]{x, y, z} in meters.");
            HelperOps.RequireDegrees(degrees, "Bodies.Rotate");
            var p = about == null ? HelperOps.Zero : new[] { about[0], about[1], about[2] };
            var angle = Math.IEEERemainder(degrees * Math.PI / 180, 2 * Math.PI);
            if (Math.Abs(angle) < 1e-12) throw new SwHelperException("Bodies.Rotate: nothing to do (the angle is a whole number of turns).");
            var r = RotationMath.About(axis, angle);
            var start = list.Select(b => BodyShape.Take(b, true)).ToList();
            var made = new List<IFeature>();
            try
            {
                var k = RotationMath.AxisIndex(axis);
                if (k >= 0) list = TurnAbout(model, list, k, angle * Math.Sign(V3.Unit(axis)[k]), p, made);
                else
                {
                    // Any other axis: three fixed-axis turns (X, then Y, then Z) that compose to it, then the whole checked.
                    var (ax, ay, az) = RotationMath.EulerXyz(r);
                    foreach (var (kk, a) in new[] { (0, ax), (1, ay), (2, az) })
                        if (Math.Abs(a) > 1e-12) list = TurnAbout(model, list, kk, a, p, made);
                    var (err, pairing) = RotationMath.Match(start, list, r, p);
                    if (!(err < Tol(start))) throw new SwHelperException($"Bodies.Rotate: the three axis turns don't add up to the asked turn ({HelperOps.Mm(err)} mm off).");
                    list = pairing.Select(j => list[j]).ToList();
                }
            }
            catch (Exception ex) when (made.Count > 0 && !(ex is OperationCanceledException))
            {
                var removed = SwGeo.DeleteFeatures(model, made.ToArray());
                throw new SwHelperException(ex.Message + (removed ? " The turns made before it were removed again." : " NOTE: earlier turns of this call are still in the tree: " + string.Join(", ", made.Select(x => x.Name))));
            }
            return Done(list, made.Last(), list);
        }

        private static double Tol(IEnumerable<BodyShape> shapes) => 1e-5 + 1e-6 * HelperOps.Size(shapes);

        /// <summary>One turn about model axis k by angle about p, found by measurement (see the class notes). Adds its features to `made`.</summary>
        private List<IBody2> TurnAbout(IModelDoc2 model, List<IBody2> list, int k, double angle, double[] p, List<IFeature> made)
        {
            var shapes = list.Select(b => BodyShape.Take(b, true)).ToList();
            var others = HelperOps.Others(model, list);
            var tol = Tol(shapes);
            var want = RotationMath.Axis(k, angle);
            var hasPoint = V3.Len(p) > 0;
            var tried = new List<string>();
            var triedSlots = new HashSet<(int Slot, double Value)>();
            for (var attempt = 0; attempt < 6; attempt++)
            {
                (int Slot, double Value)? next;
                lock (Gate)
                    next = RotationMath.NextSlot(k, angle, SlotMap, triedSlots);
                if (!next.HasValue) break;
                var (slot, value) = next.Value;
                triedSlots.Add((slot, value));
                if (attempt > 0) list = HelperOps.Refind(model, shapes, "Bodies.Rotate");
                var usePoint = hasPoint && _rotPointHonored != false;
                var rp = usePoint ? p : HelperOps.Zero;
                var rot = new double[3];
                rot[slot] = value;
                IFeature f = null;
                foreach (var mark in HelperOps.Marks(_moveMark, 1, 0))
                {
                    f = MoveCopy(model, list, HelperOps.Zero, rp, rot, false, 1, mark, "Bodies.Rotate");
                    if (f == null) continue;
                    _moveMark = mark;
                    break;
                }
                var label = $"RotAngle{"XYZ"[slot]} = {value * 180 / Math.PI:0.##} deg";
                if (f == null)
                {
                    tried.Add(label + ": no feature");
                    continue;
                }
                // What did SolidWorks do? Every axis-and-sign turn, about the rotation point and about the origin.
                var after = SwGeo.NewBodies(model, others);
                var centres = usePoint ? new[] { rp, HelperOps.Zero } : new[] { HelperOps.Zero };
                var hits = new List<(int Axis, int Sign, double[] C, int[] Pairing)>();
                for (var a = 0; a < 3; a++)
                foreach (var s in new[] { 1, -1 })
                foreach (var c in centres)
                {
                    var (err, pairing) = RotationMath.Match(shapes, after, RotationMath.Axis(a, s * value), c);
                    if (err < tol) hits.Add((a, s, c, pairing));
                }
                bool Wanted((int Axis, int Sign, double[] C, int[] Pairing) h) => RotationMath.Diff(RotationMath.Axis(h.Axis, h.Sign * value), want) < 1e-9;
                if (hits.Count == 1)
                    lock (Gate)
                        SlotMap[slot] = (hits[0].Axis, hits[0].Sign);
                var exact = hits.FirstOrDefault(h => Wanted(h) && V3.Dist(h.C, p) < 1e-12);
                if (exact.Pairing != null)
                {
                    if (usePoint) _rotPointHonored = true;
                    made.Add(f);
                    return exact.Pairing.Select(j => after[j]).ToList();
                }
                var aboutOrigin = hits.FirstOrDefault(h => Wanted(h) && V3.Len(h.C) == 0);
                if (aboutOrigin.Pairing != null && hasPoint)
                {
                    // The turn is right but about the origin (the rotation point was ignored): move the bodies back by p - R p.
                    if (usePoint) _rotPointHonored = false;
                    made.Add(f);
                    var turned = aboutOrigin.Pairing.Select(j => after[j]).ToList();
                    var (fm, moved) = Translate(model, turned, V3.Sub(p, RotationMath.Apply(want, p)), "Bodies.Rotate");
                    made.Add(fm);
                    return moved;
                }
                tried.Add(label + (hits.Count == 0 ? ": the result matches no turn about X, Y or Z"
                    : hits.Count == 1 ? $": turned about {"XYZ"[hits[0].Axis]} by {hits[0].Sign * value * 180 / Math.PI:0.##} deg" + (V3.Len(hits[0].C) == 0 && hasPoint ? " about the origin" : "")
                    : ": ambiguous (the bodies look the same after several turns)"));
                SwGeo.DeleteFeatures(model, f);
            }
            throw new SwHelperException($"Bodies.Rotate: no Move/Copy rotation gave the turn about {"XYZ"[k]} by {angle * 180 / Math.PI:0.##} deg ({string.Join("; ", tried)}); this step was removed.");
        }

        /// <summary>A verified translation (no copy) of bodies by t; returns the feature and the moved bodies in input order.</summary>
        private (IFeature Feature, List<IBody2> Moved) Translate(IModelDoc2 model, List<IBody2> list, double[] t, string what)
        {
            var shapes = list.Select(b => BodyShape.Take(b, false)).ToList();
            var others = HelperOps.Others(model, list);
            var tried = new List<string>();
            foreach (var mark in HelperOps.Marks(_moveMark, 1, 0))
            {
                if (tried.Count > 0) list = HelperOps.Refind(model, shapes, what);
                var f = MoveCopy(model, list, t, HelperOps.Zero, HelperOps.Zero, false, 1, mark, what);
                if (f == null)
                {
                    tried.Add($"mark {mark}: no feature");
                    continue;
                }
                var after = SwGeo.NewBodies(model, others);
                var (err, pairing) = RotationMath.Match(shapes, after, RotationMath.Identity, HelperOps.Zero, t);
                if (err < 1e-5)
                {
                    _moveMark = mark;
                    return (f, pairing.Select(j => after[j]).ToList());
                }
                tried.Add($"mark {mark}: " + (double.IsInfinity(err) ? $"{after.Count} bodies changed, expected {list.Count}" : $"landed {HelperOps.Mm(err)} mm from where asked"));
                SwGeo.DeleteFeatures(model, f);
            }
            throw new SwHelperException($"{what}: the move by ({HelperOps.Mm(t[0])}, {HelperOps.Mm(t[1])}, {HelperOps.Mm(t[2])}) mm didn't come out right ({string.Join("; ", tried)}); nothing was changed.");
        }

        private static IFeature MoveCopy(IModelDoc2 model, List<IBody2> bodies, double[] t, double[] p, double[] rot, bool copy, int n, int mark, string what)
        {
            model.ClearSelection2(true);
            try
            {
                HelperOps.SelectBodies(model, bodies, mark, what);
                return model.FeatureManager.InsertMoveCopyBody2(TransX: t[0], TransY: t[1], TransZ: t[2], TransDist: 0, RotPointX: p[0], RotPointY: p[1], RotPointZ: p[2],
                    RotAngleX: rot[0], RotAngleY: rot[1], RotAngleZ: rot[2], BCopy: copy, NumCopies: n);
            }
            finally
            {
                model.ClearSelection2(true);
            }
        }

        // ---------- combine ----------

        [Helper("Join tool bodies into the target (Combine Add), one feature; checked: the result's volume is the union's. Returns the joined body.", "IFeatureManager.InsertCombineFeature", "IPartDoc.InsertCombineFeature")]
        public IBody2 Combine(IBody2 target, params IBody2[] tools) => Boolean("Bodies.Combine", (int)swBodyOperationType_e.SWBODYADD, target, tools, false);

        public IBody2 Combine(IBody2 target, IEnumerable<IBody2> tools) => Boolean("Bodies.Combine", (int)swBodyOperationType_e.SWBODYADD, target, tools, false);

        [Helper("Cut tool bodies out of the target (Combine Subtract), one feature; keepTools: copies are cut, the tools stay; checked: the volume removed is the tools' volume inside the target. Returns the cut body (the largest piece; all in Bodies.Last).", "IFeatureManager.InsertCombineFeature", "IPartDoc.InsertCombineFeature")]
        public IBody2 Subtract(IBody2 target, IEnumerable<IBody2> tools, bool keepTools = false) => Boolean("Bodies.Subtract", (int)swBodyOperationType_e.SWBODYCUT, target, tools, keepTools);

        public IBody2 Subtract(IBody2 target, IBody2 tool, bool keepTools = false) => Boolean("Bodies.Subtract", (int)swBodyOperationType_e.SWBODYCUT, target, new[] { tool }, keepTools);

        [Helper("Keep only what the target and the tools share (Combine Common), one feature; checked: the result's volume is the intersection's. Returns the result body.", "IFeatureManager.InsertCombineFeature")]
        public IBody2 Intersect(IBody2 target, IEnumerable<IBody2> tools) => Boolean("Bodies.Intersect", (int)swBodyOperationType_e.SWBODYINTERSECT, target, tools, false);

        public IBody2 Intersect(IBody2 target, IBody2 tool) => Boolean("Bodies.Intersect", (int)swBodyOperationType_e.SWBODYINTERSECT, target, new[] { tool }, false);

        private IBody2 Boolean(string what, int op, IBody2 target, IEnumerable<IBody2> toolsIn, bool keepTools)
        {
            var model = Part(what);
            if (target == null) throw new SwHelperException($"{what}: the target body is null.");
            var targetName = SwGeo.SafeName(target);
            var tools = HelperOps.Distinct(toolsIn, what).Where(t => SwGeo.SafeName(t) != targetName).ToList();
            if (tools.Count == 0) throw new SwHelperException($"{what}: give at least one tool body other than the target.");
            IFeature copies = null;
            HashSet<string> originals = null;
            var ok = false;
            try
            {
                if (keepTools)
                {
                    // The copies are cut; on a retry Refind must find them, not the originals of the same shape.
                    originals = new HashSet<string>(tools.Select(SwGeo.SafeName), StringComparer.Ordinal);
                    tools = Copy(tools, 0, 0, 0).ToList();
                    copies = LastFeature;
                }
                var vT = SwGeo.Volume(target);
                var vTools = tools.Select(SwGeo.Volume).ToList();
                var expected = op == (int)swBodyOperationType_e.SWBODYADD ? UnionVolume(target, tools)
                    : op == (int)swBodyOperationType_e.SWBODYCUT ? CutVolume(target, tools)
                    : CommonVolume(target, tools);
                var shapes = new[] { target }.Concat(tools).Select(b => BodyShape.Take(b, false)).ToList();
                var others = HelperOps.Others(model, new[] { target }.Concat(tools));
                // (target mark, tools mark) or null = pass the bodies as arguments; from the user's working cells and v1.
                var attempts = op == (int)swBodyOperationType_e.SWBODYADD ? new (int, int)?[] { (2, 2), (0, 0), (1, 1), null }
                    : op == (int)swBodyOperationType_e.SWBODYCUT ? new (int, int)?[] { (1, 2), null, (0, 2) }
                    : new (int, int)?[] { (1, 2), (2, 2), null };
                var tried = new List<string>();
                foreach (var marks in attempts)
                {
                    if (tried.Count > 0)
                    {
                        var again = HelperOps.Refind(model, shapes, what, originals);
                        target = again[0];
                        tools = again.Skip(1).ToList();
                    }
                    var label = marks.HasValue ? $"marks {marks.Value.Item1}/{marks.Value.Item2}" : "bodies as arguments";
                    IFeature f;
                    model.ClearSelection2(true);
                    try
                    {
                        if (marks.HasValue)
                        {
                            HelperOps.SelectBodies(model, new[] { target }, marks.Value.Item1, what);
                            HelperOps.SelectBodies(model, tools, marks.Value.Item2, what);
                            f = model.FeatureManager.InsertCombineFeature(OperationType: op, MainBody: null, ToolVar: null);
                        }
                        else f = model.FeatureManager.InsertCombineFeature(OperationType: op, MainBody: (Body2)target, ToolVar: tools.Select(t => (Body2)t).ToArray());
                    }
                    finally
                    {
                        model.ClearSelection2(true);
                    }
                    if (f == null)
                    {
                        tried.Add(label + ": no feature");
                        continue;
                    }
                    var result = SwGeo.NewBodies(model, others);
                    var vAfter = result.Sum(SwGeo.Volume);
                    var problem = HelperOps.Error(f) is string e ? "rebuild error " + e
                        : result.Count == 0 ? "no body is left"
                        : SwGeo.Solids(model).Count != others.Count + result.Count ? "other bodies changed"
                        : CheckBoolean(op, vT, vTools, vAfter, expected);
                    if (problem == null)
                    {
                        ok = true;
                        var sorted = result.OrderByDescending(SwGeo.Volume).ToList();
                        return Done(sorted[0], f, sorted);
                    }
                    tried.Add(label + ": " + problem);
                    SwGeo.DeleteFeatures(model, f);
                }
                throw new SwHelperException($"{what}: no Combine came out right ({string.Join("; ", tried)}); nothing was changed" +
                                            (op == (int)swBodyOperationType_e.SWBODYADD ? ". Do the bodies touch or overlap?" : ". Do the tools overlap the target?"));
            }
            finally
            {
                if (!ok && copies != null) SwGeo.DeleteFeatures(model, copies);
            }
        }

        private static string CheckBoolean(int op, double vT, IList<double> vTools, double vAfter, double? expected)
        {
            string V(double v) => (v / 1e-9).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            if (!SwGeo.Finite(vAfter)) return "the result's volume couldn't be measured";
            if (op == (int)swBodyOperationType_e.SWBODYCUT)
            {
                var removed = vT - vAfter;
                if (removed <= 1e-15) return "it removed nothing";
                if (expected.HasValue && Math.Abs(vAfter - expected.Value) > 1e-3 * Math.Max(removed, 1e-15))
                    return $"removed {V(removed)} mm3 but the tools hold {V(vT - expected.Value)} mm3 inside the target";
                if (!expected.HasValue && removed > vTools.Sum() * 1.001) return $"removed {V(removed)} mm3, more than the tools' {V(vTools.Sum())} mm3";
                return null;
            }
            if (op == (int)swBodyOperationType_e.SWBODYADD)
            {
                if (expected.HasValue) return Math.Abs(vAfter - expected.Value) <= 1e-3 * expected.Value ? null : $"the result holds {V(vAfter)} mm3, the union is {V(expected.Value)} mm3";
                var max = Math.Max(vT, vTools.DefaultIfEmpty(0).Max());
                return vAfter <= (vT + vTools.Sum()) * (1 + 1e-6) && vAfter >= max * (1 - 1e-6) ? null : $"the result holds {V(vAfter)} mm3, outside {V(max)}..{V(vT + vTools.Sum())} mm3";
            }
            if (vAfter <= 1e-15) return "nothing is left (the bodies don't overlap)";
            if (expected.HasValue) return Math.Abs(vAfter - expected.Value) <= 1e-3 * Math.Max(expected.Value, 1e-15) ? null : $"the result holds {V(vAfter)} mm3, the intersection is {V(expected.Value)} mm3";
            return vAfter <= Math.Min(vT, vTools.Min()) * (1 + 1e-6) ? null : $"the result holds {V(vAfter)} mm3, more than the smallest body";
        }

        /// <summary>A temporary copy (nothing is added to the model).</summary>
        private static IBody2 Temp(IBody2 b) => b?.Copy2(false) as IBody2;

        /// <summary>Volume of a ∩ b from temporary copies, or null if the modeler refuses.</summary>
        private static double? IntersectionVolume(IBody2 a, IBody2 b)
        {
            try
            {
                var ca = Temp(a);
                var cb = Temp(b);
                if (ca == null || cb == null) return null;
                var parts = ca.Operations2((int)swBodyOperationType_e.SWBODYINTERSECT, cb, out var err);
                if (err != 0) return null;
                return parts.AsObjects<IBody2>().Sum(SwGeo.Volume);
            }
            catch (Exception ex)
            {
                Log.Warn("Bodies: intersection volume: " + ex.Message);
                return null;
            }
        }

        /// <summary>The target's volume after cutting the tools (tools assumed not to overlap each other inside it), or null.</summary>
        private static double? CutVolume(IBody2 target, IList<IBody2> tools)
        {
            var inside = 0.0;
            foreach (var t in tools)
            {
                var v = IntersectionVolume(target, t);
                if (v == null) return null;
                inside += v.Value;
            }
            // Tools overlapping each other inside the target would be counted twice: then only the bounds are checked.
            for (var i = 0; i < tools.Count; i++)
            for (var j = i + 1; j < tools.Count; j++)
                if (!(IntersectionVolume(tools[i], tools[j]) is double o) || o > 1e-15) return null;
            return SwGeo.Volume(target) - inside;
        }

        /// <summary>The union's volume by inclusion-exclusion when no two tools overlap, else null.</summary>
        private static double? UnionVolume(IBody2 target, IList<IBody2> tools)
        {
            var total = SwGeo.Volume(target);
            foreach (var t in tools)
            {
                var v = IntersectionVolume(target, t);
                if (v == null) return null;
                total += SwGeo.Volume(t) - v.Value;
            }
            for (var i = 0; i < tools.Count; i++)
            for (var j = i + 1; j < tools.Count; j++)
                if (!(IntersectionVolume(tools[i], tools[j]) is double o) || o > 1e-15) return null;
            return total;
        }

        /// <summary>The volume common to the target and every tool, from temporary copies, or null.</summary>
        private static double? CommonVolume(IBody2 target, IList<IBody2> tools)
        {
            try
            {
                var current = new List<IBody2> { Temp(target) };
                foreach (var t in tools)
                {
                    var next = new List<IBody2>();
                    foreach (var piece in current)
                    {
                        var tool = Temp(t);
                        if (piece == null || tool == null) return null;
                        var parts = piece.Operations2((int)swBodyOperationType_e.SWBODYINTERSECT, tool, out var err);
                        if (err != 0) return null;
                        next.AddRange(parts.AsObjects<IBody2>());
                    }
                    current = next;
                    if (current.Count == 0) return 0;
                }
                return current.Sum(SwGeo.Volume);
            }
            catch (Exception ex)
            {
                Log.Warn("Bodies: common volume: " + ex.Message);
                return null;
            }
        }

        // ---------- split ----------

        [Helper("Split a body by a plane feature, or Split(body, \"X\"|\"Y\"|\"Z\", at) by a plane it makes at that coordinate (meters), keeping every piece; checked: two or more pieces, volume kept, each piece on one side of the plane. Returns the pieces.",
            "IFeatureManager.PreSplitBody2", "IFeatureManager.PostSplitBody2", "IFeatureManager.InsertRefPlane")]
        public IList<IBody2> Split(IBody2 body, IFeature plane)
        {
            var model = Part("Bodies.Split");
            if (body == null || plane == null) throw new SwHelperException("Bodies.Split: give the body and the plane feature.");
            var pieces = SplitWith(model, body, plane, "Bodies.Split");
            return Done(pieces.Bodies, pieces.Feature, pieces.Bodies);
        }

        public IList<IBody2> Split(IBody2 body, string axis, double at)
        {
            var model = Part("Bodies.Split");
            if (body == null) throw new SwHelperException("Bodies.Split: the body is null.");
            var k = RotationMath.AxisIndex(RotationMath.AxisVector(axis));
            var basePlane = BasePlane(model, k);
            if (Math.Abs(at) < 1e-12)
            {
                var r0 = SplitWith(model, body, basePlane, "Bodies.Split");
                return Done(r0.Bodies, r0.Feature, r0.Bodies);
            }
            var plane = OffsetPlane(model, basePlane, k, at);
            try
            {
                var r = SplitWith(model, body, plane, "Bodies.Split");
                SwGeo.Rename(model, plane, $"Split plane {"XYZ"[k]}={HelperOps.Mm(at)}mm");
                return Done(r.Bodies, r.Feature, r.Bodies);
            }
            catch (SwHelperException)
            {
                SwGeo.DeleteFeatures(model, plane);
                throw;
            }
        }

        /// <summary>The default plane normal to model axis k (Front = Z, Top = Y, Right = X), found by its normal, not its name.</summary>
        private static IFeature BasePlane(IModelDoc2 model, int k)
        {
            var n = 0;
            for (var f = model.FirstFeature() as IFeature; f != null && n < 3; f = f.GetNextFeature() as IFeature)
            {
                if (f.GetTypeName2() != "RefPlane") continue;
                n++;
                if (PlaneOf(f) is var pl && pl.N != null && Math.Abs(Math.Abs(pl.N[k]) - 1) < 1e-9 && Math.Abs(V3.Dot(pl.O, pl.N)) < 1e-9) return f;
            }
            throw new SwHelperException($"Bodies.Split: this part has no default plane normal to {"XYZ"[k]} (Front/Top/Right) to offset from.");
        }

        /// <summary>A reference plane's origin and unit normal (model meters), or (null, null) for other features.</summary>
        private static (double[] O, double[] N) PlaneOf(IFeature f)
        {
            if (!(f?.GetSpecificFeature2() is IRefPlane rp)) return (null, null);
            var a = rp.Transform?.ArrayData.AsDoubles();
            if (a == null || a.Length < 12) return (null, null);
            return (new[] { a[9], a[10], a[11] }, V3.Unit(new[] { a[6], a[7], a[8] }));
        }

        /// <summary>A plane parallel to the base plane at model coordinate `at` along axis k, checked where it lands (flipped if it went the other way).</summary>
        private static IFeature OffsetPlane(IModelDoc2 model, IFeature basePlane, int k, double at)
        {
            var tried = new List<string>();
            foreach (var flip in new[] { false, true })
            {
                model.ClearSelection2(true);
                IFeature plane;
                try
                {
                    if (!basePlane.Select2(false, 0)) throw new SwHelperException("Bodies.Split: couldn't select the base plane.");
                    var c = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance | (flip ? (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip : 0);
                    plane = model.FeatureManager.InsertRefPlane(FirstConstraint: c, FirstConstraintAngleOrDistance: Math.Abs(at), SecondConstraint: 0, SecondConstraintAngleOrDistance: 0,
                        ThirdConstraint: 0, ThirdConstraintAngleOrDistance: 0) as IFeature ?? model.Extension.GetLastFeatureAdded() as IFeature;
                }
                finally
                {
                    model.ClearSelection2(true);
                }
                if (plane == null || plane.GetTypeName2() != "RefPlane")
                {
                    tried.Add((flip ? "flipped" : "plain") + ": no plane");
                    continue;
                }
                var (o, _) = PlaneOf(plane);
                var where = o == null ? double.NaN : o[k];
                if (Math.Abs(where - at) < 1e-8) return plane;
                tried.Add((flip ? "flipped" : "plain") + $": landed at {"XYZ"[k]} = {HelperOps.Mm(where)} mm");
                SwGeo.DeleteFeatures(model, plane);
            }
            throw new SwHelperException($"Bodies.Split: couldn't make a plane at {"XYZ"[k]} = {HelperOps.Mm(at)} mm ({string.Join("; ", tried)}).");
        }

        private (IFeature Feature, List<IBody2> Bodies) SplitWith(IModelDoc2 model, IBody2 body, IFeature plane, string what)
        {
            var v0 = SwGeo.Volume(body);
            var shape = BodyShape.Take(body, false);
            var others = HelperOps.Others(model, new[] { body });
            var (o, n) = PlaneOf(plane);
            var tried = new List<string>();
            foreach (var (planeMark, bodyMark) in new[] { (1, 2), (0, 0), (2, 1) })
            {
                if (tried.Count > 0) body = HelperOps.Refind(model, new[] { shape }, what)[0];
                IFeature f = null;
                model.ClearSelection2(true);
                try
                {
                    if (!plane.Select2(false, planeMark)) throw new SwHelperException($"{what}: couldn't select the plane '{plane.Name}'.");
                    HelperOps.SelectBodies(model, new[] { body }, bodyMark, what);
                    var pieces = model.FeatureManager.PreSplitBody2() as object[];
                    if (pieces == null || pieces.Length < 2)
                    {
                        tried.Add($"marks {planeMark}/{bodyMark}: nothing to split");
                        continue;
                    }
                    var lastBefore = (model.Extension.GetLastFeatureAdded() as IFeature)?.Name;
                    var posted = model.FeatureManager.PostSplitBody2(BodiesToMark: pieces, ConsumeCut: false, Origins: null, SavePaths: null, OverrideTemplateName: "");
                    // The split feature - or, should this version hand back the bodies or nothing, the feature just added.
                    var added = model.Extension.GetLastFeatureAdded() as IFeature;
                    f = posted as IFeature ?? (added != null && added.Name != lastBefore ? added : null);
                }
                finally
                {
                    model.ClearSelection2(true);
                }
                if (f == null)
                {
                    tried.Add($"marks {planeMark}/{bodyMark}: no feature");
                    continue;
                }
                var made = SwGeo.NewBodies(model, others);
                var v = made.Sum(SwGeo.Volume);
                var tol = 1e-6 + 1e-6 * Math.Abs(V3.Dot(o ?? HelperOps.Zero, n ?? HelperOps.Zero));
                string problem = HelperOps.Error(f) is string e ? "rebuild error " + e
                    : made.Count < 2 ? $"{made.Count} piece(s)"
                    : !(Math.Abs(v / v0 - 1) <= 0.001) ? $"the pieces hold {v / 1e-9:0.#} of {v0 / 1e-9:0.#} mm3"
                    : n != null && made.Any(b => !OneSide(b, o, n, tol)) ? "a piece lies on both sides of the plane"
                    : null;
                if (problem == null) return (f, made.OrderByDescending(SwGeo.Volume).ToList());
                tried.Add($"marks {planeMark}/{bodyMark}: {problem}");
                SwGeo.DeleteFeatures(model, f);
            }
            throw new SwHelperException($"{what}: the split didn't come out right ({string.Join("; ", tried)}); nothing was changed. Does the plane cross the body?");
        }

        private static bool OneSide(IBody2 b, double[] o, double[] n, double tol)
        {
            var h = V3.Dot(o, n);
            var up = RotationMath.Support(b, n) - h;
            var down = RotationMath.Support(b, V3.Mul(n, -1)) + h;
            return up <= tol || down <= tol;
        }

        // ---------- folders ----------

        [Helper("Put bodies in the solid-body folder `name` (merged with a folder of that name); checked: the folder holds them all. Returns the folder feature.", "IFeatureManager.InsertSubFolder", "IBodyFolder.GetBodies")]
        public IFeature ToFolder(IEnumerable<IBody2> bodies, string name)
        {
            Part("Bodies.ToFolder");
            var list = HelperOps.Distinct(bodies, "Bodies.ToFolder");
            try
            {
                var folder = _g.Mold.Folder(name, list.ToArray());
                return Done(folder, folder, list);
            }
            catch (MoldApiException ex)
            {
                throw new SwHelperException("Bodies.ToFolder: " + ex.Message);
            }
        }

        public IFeature ToFolder(IBody2 body, string name) => ToFolder(new[] { body }, name);
    }
}

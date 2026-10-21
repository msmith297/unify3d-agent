using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;

namespace SwAgent.Mold.Geometry
{
    public sealed class FlashOptions
    {
        public FlashRules Rules = new FlashRules();
        /// <summary>auto (ask when unsure) | one (touching coplanar plates are one base) | single (this face's body only).</summary>
        public string MultiBody = "auto";
        /// <summary>Remove this tool's earlier flash features (MOLD Flash ...) first.</summary>
        public bool ReplaceExisting = true;
    }

    public sealed class FlashResult
    {
        public int Moats, Runners, ShiftedRunners, DroppedRunners, FilletEdges;
        public List<string> Fillets = new List<string>();
        /// <summary>Measured: smallest steel between a cavity and a groove, and between a groove and any other feature (m).</summary>
        public double LipMin, ClearanceMin;
        public List<string> Problems = new List<string>();
        public string Log, Geometry;
        public string Summary;

        public override string ToString() => Summary;
    }

    /// <summary>
    /// The user's flash-groove standard on the base's parting face [SolidWorks]: a moat round each cavity with a lip of
    /// steel, runners out through the side of the block, R corners - ported from the user's verified flash-grooves macro
    /// (multi-body bases: touching coplanar plates are one parting face; seams are not edges; runners leave only through
    /// the assembled perimeter and never along a seam). Changes from the macro: one fillet feature per body (a single
    /// fillet across bodies loses its edges on the next rebuild - the macro's open bug), no Propagate flag (it failed on
    /// hc-64), MOLD names. Cases the code doesn't cover throw a MoldApiException with a geometry summary, as the macro's
    /// NeedsAgent did. The 2D layout is FlashLayout (pure, tested).
    /// </summary>
    internal sealed class FlashSystem
    {
        private const double In = 0.0254;
        private readonly ISldWorks _sw;
        private readonly IModelDoc2 _model;
        private readonly FlashOptions _o;
        private readonly StringBuilder _log = new StringBuilder();
        private string _summary = "";
        private readonly double TOUCH = 0.001 * In, SEAM = 1e-6, NEAR = 0.5 * In, STEP = 0.02 * In;

        public const string MoatName = "MOLD Flash moat", RunnersName = "MOLD Flash runners", FilletName = "MOLD Flash groove R";

        public FlashSystem(ISldWorks sw, IModelDoc2 model, FlashOptions o)
        {
            _sw = sw;
            _model = model;
            _o = o ?? new FlashOptions();
        }

        private MoldApiException Handoff(string why) =>
            new MoldApiException("Flash grooves need the agent: " + why + (_summary.Length > 0 ? "\nGEOMETRY:\n" + _summary : ""));

        private static double Dot3(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

        private static double[] UnitN(IFace2 f)
        {
            var n = (double[])f.Normal;
            var l = Math.Sqrt(Dot3(n, n));
            return new[] { n[0] / l, n[1] / l, n[2] / l };
        }

        private static double PlaneH(IFace2 f, double[] n)
        {
            var pp = (double[])((ISurface)f.GetSurface()).PlaneParams;
            return pp[3] * n[0] + pp[4] * n[1] + pp[5] * n[2];
        }

        private static bool IsPlane(IFace2 f) => f?.GetSurface() is ISurface s && s.IsPlane();

        private static string BodyName(IFace2 f) => f.GetBody() is IBody2 b ? b.Name : "?";

        /// <summary>Points along an edge that stay on it (tessellated, densified to 0.5 mm, snapped back onto the edge).</summary>
        private static List<double[]> SampE(IEdge e)
        {
            var raw = new List<double[]>();
            var c = (ICurve)e.GetCurve();
            var p = (double[])e.GetCurveParams2();
            var s = new[] { p[0], p[1], p[2] };
            var t = new[] { p[3], p[4], p[5] };
            if (Math.Sqrt(Math.Pow(s[0] - t[0], 2) + Math.Pow(s[1] - t[1], 2) + Math.Pow(s[2] - t[2], 2)) > 1e-9)
            {
                if (c.GetTessPts(1e-5, 1e-3, s, t) is double[] ts)
                    for (var i = 0; i + 2 < ts.Length; i += 3) raw.Add(new[] { ts[i], ts[i + 1], ts[i + 2] });
            }
            if (raw.Count < 2)
            {
                var cpd = e.GetCurveParams3();
                double t0 = cpd.UMinValue, t1 = cpd.UMaxValue;
                for (var k = 0; k <= 64; k++)
                {
                    var z = (double[])c.Evaluate2(t0 + (t1 - t0) * k / 64.0, 0);
                    raw.Add(new[] { z[0], z[1], z[2] });
                }
            }
            var outp = new List<double[]>();
            for (var i = 0; i < raw.Count; i++)
            {
                outp.Add(raw[i]);
                if (i + 1 < raw.Count)
                {
                    var a = raw[i];
                    var b = raw[i + 1];
                    var len = V3.Dist(a, b);
                    var n = (int)(len / 0.0005);
                    for (var k = 1; k < n; k++)
                    {
                        var f = (double)k / n;
                        outp.Add(new[] { a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f });
                    }
                }
            }
            return outp.Select(q => e.GetClosestPointOn(q[0], q[1], q[2]) is double[] o && o.Length >= 3 ? new[] { o[0], o[1], o[2] } : q).ToList();
        }

        /// <summary>The point half way along an edge by length, on the edge (not the chord midpoint).</summary>
        private static double[] MidAlong(IEdge e, List<double[]> mp)
        {
            double tot = 0;
            for (var i = 1; i < mp.Count; i++) tot += V3.Dist(mp[i], mp[i - 1]);
            double h = tot / 2, acc = 0;
            for (var i = 1; i < mp.Count; i++)
            {
                var s = V3.Dist(mp[i], mp[i - 1]);
                if (acc + s >= h && s > 0)
                {
                    var f = (h - acc) / s;
                    var q = V3.Add(mp[i - 1], V3.Mul(V3.Sub(mp[i], mp[i - 1]), f));
                    return e.GetClosestPointOn(q[0], q[1], q[2]) is double[] o ? new[] { o[0], o[1], o[2] } : q;
                }
                acc += s;
            }
            return mp[mp.Count / 2];
        }

        private static List<double[]> OuterPts(IFace2 f)
        {
            var r = new List<double[]>();
            foreach (ILoop2 l in (object[])f.GetLoops())
                if (l.IsOuter())
                    foreach (IEdge e in (object[])l.GetEdges()) r.AddRange(SampE(e));
            return r;
        }

        private static double DistTo(IFace2 f, double[] q) =>
            f.GetClosestPointOn(q[0], q[1], q[2]) is double[] o ? V3.Dist(new[] { o[0], o[1], o[2] }, q) : double.MaxValue;

        private static double Gap(IFace2 a, IFace2 b)
        {
            var best = double.MaxValue;
            foreach (var q in OuterPts(b)) best = Math.Min(best, DistTo(a, q));
            foreach (var q in OuterPts(a)) best = Math.Min(best, DistTo(b, q));
            return best;
        }

        private static bool BoxNear(IFace2 a, IFace2 b, double t)
        {
            var A = (double[])a.GetBox();
            var B = (double[])b.GetBox();
            for (var k = 0; k < 3; k++)
                if (A[k] - t > B[k + 3] || B[k] - t > A[k + 3]) return false;
            return true;
        }

        /// <summary>Multi-body base: planar faces of other bodies, coplanar and touching the parting face (transitively), plus unsure cases.</summary>
        private (List<IFace2> Group, List<string> Unsure) GroupOf(IFace2 f0)
        {
            var n0 = UnitN(f0);
            var h0 = PlaneH(f0, n0);
            var grp = new List<IFace2> { f0 };
            var uns = new List<string>();
            var b0 = BodyName(f0);
            var cand = new List<(IFace2 F, double Dh)>();
            foreach (var b in SwGeo.Solids(_model, true))
            {
                if (b.Name == b0) continue;
                foreach (IFace2 f in (object[])b.GetFaces())
                {
                    if (!IsPlane(f) || Dot3(UnitN(f), n0) < 0.9999) continue;
                    var dh = PlaneH(f, n0) - h0;
                    if (Math.Abs(dh) <= STEP) cand.Add((f, dh));
                }
            }
            var grew = true;
            while (grew)
            {
                grew = false;
                foreach (var c in cand)
                {
                    if (grp.Contains(c.F) || Math.Abs(c.Dh) > 1e-5) continue;
                    if (grp.Any(g => BoxNear(g, c.F, TOUCH) && Gap(g, c.F) < TOUCH))
                    {
                        grp.Add(c.F);
                        grew = true;
                    }
                }
            }
            var gb = new HashSet<string>(grp.Select(BodyName));
            foreach (var c in cand)
            {
                if (grp.Contains(c.F) || gb.Contains(BodyName(c.F))) continue;
                if (!grp.Any(g => BoxNear(g, c.F, NEAR))) continue;
                var gap = grp.Min(g => Gap(g, c.F));
                if (Math.Abs(c.Dh) <= 1e-5)
                {
                    if (gap < NEAR) uns.Add($"body '{BodyName(c.F)}' has a face coplanar with the base {gap / In:0.###} in away from it (a gap, not touching)");
                }
                else if (gap < Math.Abs(c.Dh) + TOUCH)
                {
                    uns.Add($"body '{BodyName(c.F)}' touches the base but its top face is {c.Dh / In:+0.####;-0.####} in off the parting plane (step between plates)");
                }
            }
            return (grp, uns);
        }

        /// <summary>Is a closed loop of edges a cavity (not a small round hole or a narrow slot)?</summary>
        private bool IsCav(List<IEdge> es, List<double[]> pts2)
        {
            var single = es.Count == 1 && ((ICurve)es[0].GetCurve()).IsCircle();
            var dia = single ? ((double[])((ICurve)es[0].GetCurve()).CircleParams)[6] * 2 : 0;
            return FlashLayout.IsCavity(pts2, single, dia, _o.Rules);
        }

        public FlashResult Run(IFace2 selected)
        {
            var model = _model;
            SwGeo.RequirePart(model);
            var R = _o.Rules;
            double W = R.Width, D = R.Depth, LIP = R.Lip, CLR = R.Clearance, RAD = R.Radius;
            if (W <= 0 || D <= 0 || R.Pitch <= 0) throw new MoldApiException("Flash grooves: width, depth and runner pitch must be positive.");
            var mb = (_o.MultiBody ?? "auto").Trim().ToLowerInvariant();
            bool mbOne = mb == "one", mbSingle = mb == "single";
            var how = "selected face";
            var pf = selected;
            if (pf != null && !IsPlane(pf)) throw new MoldApiException("Flash grooves: the face isn't flat. Give the flat parting face of the base plate (the face the cavity opens into).");
            var bodies = SwGeo.Solids(model, true);
            if (bodies.Count == 0) throw new MoldApiException("Flash grooves: this part has no solid body.");
            if (pf == null)
            {
                // Auto-pick: upward planar faces, ranked by (has a cavity, lowest body bottom, largest area).
                how = "top face of the bottom block";
                var up = SwGeo.Frame(model).W;
                var cands = new List<(IFace2 F, int Cav, double Bot, double Area)>();
                foreach (var b in bodies)
                {
                    var bb = (double[])b.GetBodyBox();
                    var bot = double.MaxValue;
                    for (var i = 0; i < 8; i++)
                    {
                        var c = new[] { (i & 1) == 0 ? bb[0] : bb[3], (i & 2) == 0 ? bb[1] : bb[4], (i & 4) == 0 ? bb[2] : bb[5] };
                        bot = Math.Min(bot, Dot3(c, up));
                    }
                    foreach (IFace2 f in (object[])b.GetFaces())
                    {
                        if (!IsPlane(f) || Dot3((double[])f.Normal, up) <= 0.999) continue;
                        cands.Add((f, InnerCavityCount(f), Math.Round(bot, 5), f.GetArea()));
                    }
                }
                if (cands.Count == 0) throw new MoldApiException("Flash grooves: no upward-facing flat face found. Give the parting face of the base plate.");
                pf = cands.OrderByDescending(c => c.Cav > 0 ? 1 : 0).ThenBy(c => c.Bot).ThenByDescending(c => c.Area).First().F;
                if (!mbSingle && bodies.Count > 1)
                {
                    var g0 = GroupOf(pf);
                    if (g0.Group.Count > 1)
                        throw Handoff($"no face was given and the part has {bodies.Count} bodies; the auto pick (face at height {PlaneH(pf, UnitN(pf)) / In:0.###} in on '{BodyName(pf)}') is part of a multi-plate base [{string.Join(", ", g0.Group.Select(BodyName))}]. Ask which plates / face form the base, then run on that face. Nothing was changed.");
                }
            }
            var G0 = mbSingle ? (new List<IFace2> { pf }, new List<string>()) : GroupOf(pf);
            if (!mbSingle && !mbOne && G0.Item2.Count > 0)
                throw Handoff($"multi-body base not clear-cut - touching plates [{string.Join(", ", G0.Item1.Select(BodyName))}], plus: {string.Join("; ", G0.Item2)}. Ask the user whether these belong to the base. Nothing was changed.");

            // A point on the face's largest triangle: the face is found again there after the old grooves are removed.
            var tr = (float[])pf.GetTessTriangles(true) ?? throw new MoldApiException("Flash grooves: the parting face has no tessellation (hidden body?).");
            int bi = 0;
            double ba = -1;
            for (var i = 0; i + 8 < tr.Length; i += 9)
            {
                double ax = tr[i + 3] - tr[i], ay = tr[i + 4] - tr[i + 1], az = tr[i + 5] - tr[i + 2], bx = tr[i + 6] - tr[i], by = tr[i + 7] - tr[i + 1], bz = tr[i + 8] - tr[i + 2];
                double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
                var ar = cx * cx + cy * cy + cz * cz;
                if (ar > ba)
                {
                    ba = ar;
                    bi = i;
                }
            }
            var pick = new[] { (tr[bi] + tr[bi + 3] + tr[bi + 6]) / 3.0, (tr[bi + 1] + tr[bi + 4] + tr[bi + 7]) / 3.0, (tr[bi + 2] + tr[bi + 5] + tr[bi + 8]) / 3.0 };
            var pickNormal = UnitN(pf);

            var sm = model.SketchManager;
            var fm = model.FeatureManager;
            var mu = (IMathUtility)_sw.GetMathUtility();
            // InsertSketch(true) is a toggle: with a sketch already open it would close that one instead of opening ours
            if (sm.ActiveSketch != null || model.GetActiveSketch2() != null)
                throw new MoldApiException("Flash grooves: a sketch is open for editing. Exit the sketch (or ask the user to finish it), then run again. Nothing was changed.");
            if (_o.ReplaceExisting)
            {
                var old = SwGeo.AllFeatures(model).Where(f => f.Name.StartsWith(MoatName, StringComparison.Ordinal) || f.Name.StartsWith(RunnersName, StringComparison.Ordinal) ||
                                                             f.Name.StartsWith(FilletName, StringComparison.Ordinal)).ToList();
                if (old.Count > 0)
                {
                    SwGeo.DeleteFeatures(model, old.ToArray());
                    _log.AppendLine($"removed {old.Count} earlier flash feature(s)");
                }
            }
            // Find the face again: the face of a body at the pick point (by ray, not by selecting coordinates where plates touch).
            var face = FaceAt(pick, pickNormal) ?? throw Handoff("couldn't find the parting face again at its pick point after removing the earlier grooves.");
            var N0 = UnitN(face);
            var H0 = Dot3(pick, N0);
            var grp = mbSingle ? new List<IFace2> { face } : GroupOf(face).Group;
            if (grp.Count > 1) _log.AppendLine($"multi-body base: {grp.Count} plates treated as one parting face ({string.Join(", ", grp.Select(BodyName))})");

            var adb = sm.AddToDB;
            IMathTransform T = null, Ti = null;
            var sketchOpen = false;
            IFeature openSketchFeature = null;   // the sketch this call opened (never "the last feature added")
            double[] S2(double[] p)
            {
                var mp = (IMathPoint)mu.CreatePoint(new[] { p[0], p[1], p[2] });
                var r = (double[])((IMathPoint)mp.MultiplyTransform(T)).ArrayData;
                return new[] { r[0], r[1] };
            }
            double[] M3(double[] p)
            {
                var mp = (IMathPoint)mu.CreatePoint(new[] { p[0], p[1], 0.0 });
                var r = (double[])((IMathPoint)mp.MultiplyTransform(Ti)).ArrayData;
                return new[] { r[0], r[1], r[2] };
            }
            // Opens a sketch on a face; sketchOpen and the feature are set only once a sketch really is open.
            ISketch OpenSketch(IFace2 on, string what)
            {
                model.ClearSelection2(true);
                if (!((IEntity)on).Select4(false, null)) throw Handoff("couldn't select the " + what + " to sketch on.");
                sm.InsertSketch(true);
                var opened = model.GetActiveSketch2() as ISketch ?? throw Handoff("no sketch opened on the " + what + ".");
                sketchOpen = true;
                openSketchFeature = opened as IFeature;
                return opened;
            }
            // Closes the sketch this call opened (toggles only while a sketch is active - it can only be ours: the run refuses
            // to start inside one) and returns its feature; deleteIfEmpty deletes exactly that feature.
            IFeature CloseSketch(bool deleteIfEmpty)
            {
                if (!sketchOpen) return null;
                sketchOpen = false;
                sm.AddToDB = adb;
                if (model.GetActiveSketch2() != null) sm.InsertSketch(true);
                var f = openSketchFeature;
                openSketchFeature = null;
                if (deleteIfEmpty && f != null) { SwGeo.DeleteFeatures(model, f); return null; }
                return f;
            }

            var result = new FlashResult();
            IFeature moatCut = null, runnerCut = null;
            try
            {
                using (SwGeo.Quiet(model))
                {
                    // ---- the moat sketch on the parting face; everything is measured in its coordinates ----
                    var sk = OpenSketch(face, "parting face");
                    sm.AddToDB = true;
                    T = (IMathTransform)sk.ModelToSketchTransform;
                    Ti = (IMathTransform)T.Inverse();

                    // Cavity outlines, feature points, the assembled outer perimeter, seams between plates.
                    var cavSrc = new List<(List<IEdge> Es, List<double[]> Mp, string Where)>();
                    var featPts = new List<double[]>();
                    var chkCav = new List<double[]>();
                    var chkFeat = new List<double[]>();
                    var outerModel = new List<double[]>();
                    var seamSegs = new List<(double[] A, double[] B)>();
                    var open = new List<(IEdge E, List<double[]> Mp, List<double[]> P2)>();
                    double orient = 0;
                    foreach (var gf in grp)
                    {
                        var bn = BodyName(gf);
                        bool OnOther(double[] q) => grp.Any(o => !ReferenceEquals(o, gf) && DistTo(o, q) < SEAM);
                        foreach (ILoop2 l in (object[])gf.GetLoops())
                        {
                            var ces = ((object[])l.GetCoEdges()).Cast<ICoEdge>().ToList();
                            var es = ces.Select(c => (IEdge)c.GetEdge()).ToList();
                            if (!l.IsOuter())
                            {
                                var mp = es.SelectMany(SampE).ToList();
                                var pts = mp.Select(S2).ToList();
                                if (IsCav(es, pts))
                                {
                                    cavSrc.Add((es, mp, bn));
                                    chkCav.AddRange(mp);
                                }
                                else
                                {
                                    featPts.AddRange(pts);
                                    chkFeat.AddRange(mp);
                                }
                                continue;
                            }
                            if (grp.Count == 1)
                            {
                                outerModel.AddRange(es.SelectMany(SampE));
                                continue;
                            }
                            var loopPts = new List<double[]>();
                            foreach (var ce in ces)
                            {
                                var e = (IEdge)ce.GetEdge();
                                var mp = SampE(e);
                                if (!ce.GetSense()) mp.Reverse();
                                loopPts.AddRange(mp.Select(S2));
                                var a = mp.First();
                                var b = mp.Last();
                                if (OnOther(MidAlong(e, mp)))
                                {
                                    if (OnOther(a) && OnOther(b)) seamSegs.Add((S2(a), S2(b)));
                                    else throw Handoff($"multi-body base: an edge of '{bn}' only partly touches the next plate (plates of different size along the seam). Build the grooves by hand by the rules, or ask the user. Nothing was changed except removing earlier flash grooves.");
                                }
                                else open.Add((e, mp, mp.Select(S2).ToList()));
                            }
                            if (orient == 0) orient = Math.Sign(FlashLayout.Area(loopPts));
                        }
                    }
                    if (grp.Count > 1)
                    {
                        // Chain the non-seam outer edges of all plates into closed loops. At a branch point take the sharpest
                        // turn away from the steel: that traces the boundary of the joined plates.
                        var splitLoops = new List<(List<IEdge> Es, List<double[]> Mp, List<double[]> P2, double Area)>();
                        var used = new bool[open.Count];
                        const double tol = 2e-5;
                        double[] DirAt(List<double[]> p2, bool atEnd)
                        {
                            if (atEnd)
                            {
                                var e = p2[p2.Count - 1];
                                for (var i = p2.Count - 2; i >= 0; i--)
                                    if (FlashLayout.Len(FlashLayout.Sub(e, p2[i])) > 1e-7) return FlashLayout.Unit(FlashLayout.Sub(e, p2[i]));
                            }
                            else
                            {
                                var s = p2[0];
                                for (var i = 1; i < p2.Count; i++)
                                    if (FlashLayout.Len(FlashLayout.Sub(p2[i], s)) > 1e-7) return FlashLayout.Unit(FlashLayout.Sub(p2[i], s));
                            }
                            return new[] { 1.0, 0 };
                        }
                        for (var i = 0; i < open.Count; i++)
                        {
                            if (used[i]) continue;
                            used[i] = true;
                            var chain = new List<IEdge> { open[i].E };
                            var pts = new List<double[]>(open[i].Mp);
                            var p2s = new List<double[]>(open[i].P2);
                            var start = open[i].P2[0];
                            var cur = open[i].P2[open[i].P2.Count - 1];
                            var din = DirAt(open[i].P2, true);
                            var guard = 0;
                            while (FlashLayout.Len(FlashLayout.Sub(cur, start)) > tol && guard++ < 10000)
                            {
                                var nx = -1;
                                var bestK = double.MaxValue;
                                for (var j = 0; j < open.Count; j++)
                                {
                                    if (used[j] || FlashLayout.Len(FlashLayout.Sub(open[j].P2[0], cur)) >= tol) continue;
                                    var dout = DirAt(open[j].P2, false);
                                    var turn = Math.Atan2(FlashLayout.Cross(din, dout), din[0] * dout[0] + din[1] * dout[1]);
                                    var key = orient * turn;
                                    if (key < bestK)
                                    {
                                        bestK = key;
                                        nx = j;
                                    }
                                }
                                if (nx < 0) throw Handoff($"multi-body base: the plate outlines don't join into closed loops at the seams (open end at sketch {cur[0] / In:0.###}, {cur[1] / In:0.###} in).");
                                used[nx] = true;
                                chain.Add(open[nx].E);
                                pts.AddRange(open[nx].Mp);
                                p2s.AddRange(open[nx].P2);
                                cur = open[nx].P2[open[nx].P2.Count - 1];
                                din = DirAt(open[nx].P2, true);
                            }
                            splitLoops.Add((chain, pts, p2s, Math.Abs(FlashLayout.Area(p2s))));
                        }
                        if (splitLoops.Count == 0) throw Handoff("multi-body base: no outer perimeter found.");
                        var perim = splitLoops.OrderByDescending(x => x.Area).First();
                        outerModel.AddRange(perim.Mp);
                        foreach (var x in splitLoops)
                        {
                            if (ReferenceEquals(x.Es, perim.Es)) continue;
                            if (IsCav(x.Es, x.P2))
                            {
                                cavSrc.Add((x.Es, x.Mp, "split across plates"));
                                chkCav.AddRange(x.Mp);
                                _log.AppendLine($"cavity split by a seam rebuilt from {x.Es.Count} edges");
                            }
                            else
                            {
                                featPts.AddRange(x.P2);
                                chkFeat.AddRange(x.Mp);
                                _log.AppendLine($"small feature across a seam ({x.Es.Count} edges) kept clear like a hole");
                            }
                        }
                    }
                    var outerPts = outerModel.Select(S2).ToList();
                    if (outerPts.Count == 0) throw Handoff("the parting face has no outer outline.");
                    double ox0 = outerPts.Min(p => p[0]), ox1 = outerPts.Max(p => p[0]), oy0 = outerPts.Min(p => p[1]), oy1 = outerPts.Max(p => p[1]);
                    for (var oi = 0; oi < outerPts.Count; oi++)
                    {
                        var p = outerPts[oi];
                        if (Math.Min(Math.Min(p[0] - ox0, ox1 - p[0]), Math.Min(p[1] - oy0, oy1 - p[1])) > 1e-5)
                        {
                            featPts.Add(p); // a notch in the outline (pry slot, lift notch): keep clear of it
                            chkFeat.Add(outerModel[oi]);
                        }
                    }
                    var seamPts = new List<(double[] P, double[] D)>();
                    foreach (var s in seamSegs)
                    {
                        var L = FlashLayout.Len(FlashLayout.Sub(s.B, s.A));
                        if (L < 1e-6) continue;
                        var d = FlashLayout.Unit(FlashLayout.Sub(s.B, s.A));
                        var n = Math.Max(1, (int)(L / 0.0005));
                        for (var k = 0; k <= n; k++) seamPts.Add((FlashLayout.Add(s.A, FlashLayout.Mul(d, L * k / n)), d));
                    }

                    // Geometry summary for a hand-off (model coordinates, inches).
                    string P3(double[] m) => $"({m[0] / In:0.###}, {m[1] / In:0.###}, {m[2] / In:0.###})";
                    string Box3(List<double[]> m) => $"x {m.Min(p => p[0]) / In:0.###}..{m.Max(p => p[0]) / In:0.###}, y {m.Min(p => p[1]) / In:0.###}..{m.Max(p => p[1]) / In:0.###}, z {m.Min(p => p[2]) / In:0.###}..{m.Max(p => p[2]) / In:0.###}";
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($"parting plane: normal ({N0[0]:0.###}, {N0[1]:0.###}, {N0[2]:0.###}), height {H0 / In:0.####} in; plates: {string.Join(", ", grp.Select(BodyName))}");
                        sb.AppendLine($"assembled outer perimeter: {Box3(outerModel)}");
                        foreach (var s in seamSegs) sb.AppendLine($"seam {P3(M3(s.A))} -> {P3(M3(s.B))}");
                        var k = 0;
                        foreach (var c in cavSrc)
                            sb.AppendLine($"cavity C{++k} ({c.Where}, {c.Es.Count} edges: {string.Join("", c.Es.Select(e => { var cv = (ICurve)e.GetCurve(); return cv.IsLine() ? "L" : cv.IsCircle() ? "A" : "S"; }))}): {Box3(c.Mp)}");
                        sb.AppendLine($"feature points kept {CLR / In:0.###} in clear (holes, vents, notches): {featPts.Count}");
                        _summary = sb.ToString();
                    }

                    // Cavity checks before anything is drawn.
                    var cav = cavSrc.Select(c => c.Mp.Select(S2).ToList()).ToList();
                    if (cav.Count == 0)
                        throw new MoldApiException($"Flash grooves: no cavity found on the {how}{(grp.Count > 1 ? $" (or the {grp.Count} plates around it)" : "")}. Grooves sit a lip away from a cavity, so the plate needs its cavity pocket first.");
                    for (var i = 0; i < cav.Count; i++)
                        for (var j = i + 1; j < cav.Count; j++)
                        {
                            var g = FlashLayout.Gap(cav[i], cav[j]);
                            if (g < 2 * LIP - 1e-6)
                                throw Handoff($"openings C{i + 1} ({cavSrc[i].Where}) and C{j + 1} ({cavSrc[j].Where}) are only {g / In:0.###} in apart - too close for a lip on each, so probably not both product cavities. Ask which openings are cavities. Nothing was changed except removing earlier flash grooves.");
                            if (FlashLayout.TooCloseForSeparateMoats(g, R.SeparateMoatGap))
                                throw Handoff($"cavities C{i + 1} and C{j + 1} are {g / In:0.###} in apart (separate moats need {R.SeparateMoatGap / In:0.###} in): build ONE combined moat around them by the rules, then runners and fillets, and verify the checks. Nothing was changed except removing earlier flash grooves.");
                        }

                    // ---- moats ----
                    var moats = new List<FlashLayout.Moat>();
                    var undrawn = new List<string>();
                    var ci = 0;
                    foreach (var cs in cavSrc)
                    {
                        ci++;
                        var segs = cs.Es.Select(e =>
                        {
                            var c = (ICurve)e.GetCurve();
                            var p = (double[])e.GetCurveParams2();
                            var a = S2(new[] { p[0], p[1], p[2] });
                            var b = S2(new[] { p[3], p[4], p[5] });
                            if (c.IsLine()) return new FlashLayout.Segment { IsLine = true, A = a, B = b };
                            if (c.IsCircle()) return new FlashLayout.Segment { A = a, B = b, R = ((double[])c.CircleParams)[6] };
                            return null; // spline: not a straight-sided outline
                        }).ToList();
                        var poly = FlashLayout.FromSegments(segs, cs.Mp.Select(S2).ToList());
                        if (poly == null)
                        {
                            undrawn.Add($"C{ci} ({cs.Where})");
                            _log.AppendLine($"C{ci} ({cs.Where}): not a straight-sided outline (round, slot or freeform) - NOT DRAWN");
                            continue;
                        }
                        var moat = FlashLayout.MoatFor($"C{ci}", poly, R);
                        DrawRounded(sm, moat.InnerCorners);
                        DrawRounded(sm, moat.OuterCorners);
                        moats.Add(moat);
                        _log.AppendLine($"C{ci} ({cs.Where}): moat {moat.Inner.Count} sides, lip corner R {string.Join("/", moat.InnerRadii.Select(x => (x / In).ToString("0.###")).Distinct())} in");
                    }
                    var moatSk = CloseSketch(moats.Count == 0);
                    if (moats.Count == 0)
                        throw Handoff("no cavity outline is straight-sided (lines and corner arcs), so no moat was drawn. Nothing was changed except removing earlier flash grooves. Draw the moat from the cavity edges (convert, offset lip and lip + width, cut). Log: " + _log);
                    if (moatSk == null) throw Handoff("the moat sketch was not created.");
                    model.ClearSelection2(true);
                    moatSk.Select2(false, 0);
                    moatCut = fm.FeatureCut4(true, false, false, (int)swEndConditions_e.swEndCondBlind, 0, D, 0, false, false, false, false, 0, 0,
                        false, false, false, false, false, true, true, true, true, false, 0, 0, false, false);
                    if (moatCut == null) throw Handoff("the moat sketch is drawn but its cut failed (FeatureCut4 returned null). Log: " + _log);
                    SwGeo.Rename(model, moatCut, MoatName);
                    result.Moats = moats.Count;

                    // ---- runners ----
                    var runners = new List<FlashLayout.Runner>();
                    foreach (var m in moats) runners.AddRange(FlashLayout.PlaceRunners(m, outerPts, featPts, seamPts, R, _log));
                    if (runners.Count > 0)
                    {
                        var topFace = TopFace(N0, H0) ?? throw Handoff("the parting face isn't there after the moat cut.");
                        var t2 = (IMathTransform)OpenSketch(topFace, "parting face (runners)").ModelToSketchTransform;
                        sm.AddToDB = true;
                        double[] R2(double[] p2)
                        {
                            var q = M3(p2);
                            var mp = (IMathPoint)mu.CreatePoint(q);
                            var r = (double[])((IMathPoint)mp.MultiplyTransform(t2)).ArrayData;
                            return new[] { r[0], r[1] };
                        }
                        foreach (var r in runners)
                        {
                            var c = r.Rectangle(W).Select(R2).ToArray();
                            for (var k = 0; k < 4; k++) sm.CreateLine(c[k][0], c[k][1], 0, c[(k + 1) % 4][0], c[(k + 1) % 4][1], 0);
                        }
                        var rs = CloseSketch(false);
                        model.ClearSelection2(true);
                        rs?.Select2(false, 0);
                        runnerCut = fm.FeatureCut4(true, false, false, (int)swEndConditions_e.swEndCondBlind, 0, D, 0, false, false, false, false, 0, 0,
                            false, false, false, false, false, true, true, true, true, false, 0, 0, false, false);
                        if (runnerCut == null) throw Handoff("the moat is cut; the runner sketch is drawn but its cut failed. Log: " + _log);
                        SwGeo.Rename(model, runnerCut, RunnersName);
                    }
                    result.Runners = runners.Count;
                    result.ShiftedRunners = runners.Count(r => r.Shifted);
                    result.DroppedRunners = _log.ToString().Split('\n').Count(x => x.Contains("dropped"));

                    // ---- fillets: one feature per body, no Propagate ----
                    var gfaces = new List<IFace2>();
                    foreach (var f in new[] { moatCut, runnerCut })
                        if (f != null) gfaces.AddRange(((f.GetFaces() as object[]) ?? new object[0]).OfType<IFace2>());
                    bool OnSeam(double[] m3)
                    {
                        var q = S2(m3);
                        foreach (var s in seamSegs)
                        {
                            var d = FlashLayout.Sub(s.B, s.A);
                            var L = FlashLayout.Len(d);
                            if (L < 1e-9) continue;
                            var u = FlashLayout.Mul(d, 1 / L);
                            var v = FlashLayout.Sub(q, s.A);
                            var t = v[0] * u[0] + v[1] * u[1];
                            if (t < -1e-5 || t > L + 1e-5) continue;
                            if (Math.Abs(FlashLayout.Cross(u, v)) < 2e-5) return true;
                        }
                        return false;
                    }
                    var fe = new List<(IEdge E, string Body)>();
                    var seamSkipped = 0;
                    foreach (var gf in gfaces)
                        foreach (IEdge e in (object[])gf.GetEdges())
                        {
                            if (fe.Any(x => _sw.IsSame(x.E, e) == (int)swObjectEquality.swObjectSame)) continue;
                            var c = (ICurve)e.GetCurve();
                            if (!c.IsLine()) continue;
                            var p = (double[])e.GetCurveParams2();
                            var dv = new[] { p[3] - p[0], p[4] - p[1], p[5] - p[2] };
                            var L = Math.Sqrt(Dot3(dv, dv));
                            if (Math.Abs(L - D) > 1e-6 || Math.Abs(Math.Abs(Dot3(dv, N0)) - D) > 1e-6) continue; // the groove's vertical corner edges
                            var af = ((object[])e.GetTwoAdjacentFaces2()).Cast<IFace2>().ToList();
                            if (af.Count != 2 || af.Any(x => x == null || !IsPlane(x))) continue;
                            if (Math.Abs(Dot3((double[])af[0].Normal, (double[])af[1].Normal)) > 0.999) continue;
                            if (seamSegs.Count > 0 && OnSeam(new[] { p[0], p[1], p[2] }))
                            {
                                seamSkipped++;
                                continue; // a groove wall meeting a plate-to-plate seam: not a corner
                            }
                            fe.Add((e, BodyName(af[0])));
                        }
                    result.FilletEdges = fe.Count;
                    var edgeRefs = fe.ToDictionary(x => x.E, x => SwGeo.PersistRef(model, x.E));
                    object Resolve(string b64)
                    {
                        if (b64 == null) return null;
                        try
                        {
                            var o = model.Extension.GetObjectByPersistReference3(Convert.FromBase64String(b64), out var err);
                            return err == (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok ? o : null;
                        }
                        catch
                        {
                            return null;
                        }
                    }
                    // Fillet names carry the body's mold id (BASE-1), not its auto-generated SolidWorks name.
                    var ids = new Dictionary<string, string>();
                    try
                    {
                        foreach (var b in BodyReport.List(model, SwGeo.Frame(model))) ids[b.Name] = b.Id;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Body ids for the fillet names: " + ex.Message);
                    }
                    foreach (var group in fe.GroupBy(x => x.Body))
                    {
                        model.ClearSelection2(true);
                        // Edges are found again by persistent reference: the previous body's fillet may have renewed them.
                        var picked = 0;
                        foreach (var (e, _) in group)
                        {
                            var edge = result.Fillets.Count == 0 ? e : Resolve(edgeRefs[e]) as IEdge;
                            if (edge != null && ((IEntity)edge).Select4(true, null)) picked++;
                        }
                        if (picked == 0)
                        {
                            result.Problems.Add($"the groove corners of '{group.Key}' couldn't be selected again for their fillet");
                            continue;
                        }
                        var fil = fm.FeatureFillet3((int)swFeatureFilletOptions_e.swFeatureFilletUniformRadius, RAD, 0, 0, 0, 0, 0,
                            null, null, null, null, null, null, null) as IFeature;
                        if (fil == null)
                        {
                            result.Problems.Add($"the R{RAD / In:0.###} in fillet on {group.Count()} groove corners of '{group.Key}' failed");
                            continue;
                        }
                        var name = $"{FilletName}{RAD / In:0.###} ({(ids.TryGetValue(group.Key, out var id) ? id : group.Key)})";
                        SwGeo.Rename(model, fil, name);
                        result.Fillets.Add(fil.Name);
                    }
                    model.ClearSelection2(true);
                    if (undrawn.Count > 0) result.Problems.Add($"{string.Join(", ", undrawn)} not grooved (not a straight-sided outline) - add their moats by hand");
                    if (seamSkipped > 0) _log.AppendLine($"{seamSkipped} groove edges on plate seams left sharp");

                    // ---- acceptance checks, measured ----
                    var errors = SwGeo.RebuildErrors(model).Where(x => x.StartsWith("MOLD Flash", StringComparison.Ordinal)).ToList();
                    foreach (var x in errors) result.Problems.Add(x + " has a rebuild error");
                    var gf2 = new List<IFace2>();
                    foreach (var f in SwGeo.AllFeatures(model).Where(f => f.Name.StartsWith("MOLD Flash", StringComparison.Ordinal)))
                        gf2.AddRange(((f.GetFaces() as object[]) ?? new object[0]).OfType<IFace2>());
                    double Near(double[] q)
                    {
                        var best = double.MaxValue;
                        foreach (var f in gf2) best = Math.Min(best, DistTo(f, q));
                        return best;
                    }
                    result.LipMin = chkCav.Count > 0 ? chkCav.Min(Near) : double.MaxValue;
                    result.ClearanceMin = chkFeat.Count > 0 ? chkFeat.Min(Near) : double.MaxValue;
                    if (result.LipMin < LIP - 1e-5) result.Problems.Add($"the lip between cavity and groove is only {result.LipMin / In:0.###} in somewhere (needs {LIP / In:0.###})");
                    if (result.ClearanceMin < CLR - 1e-5) result.Problems.Add($"only {result.ClearanceMin / In:0.###} in of steel between a groove and a hole, notch or other feature (needs {CLR / In:0.###})");
                    // No dead ends (A10): every moat has a runner, and every runner crosses the plate's outline.
                    result.Problems.AddRange(FlashLayout.DeadEnds(moats, runners, outerPts, W));
                }
            }
            finally
            {
                if (sketchOpen)
                {
                    try
                    {
                        CloseSketch(true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Closing the flash sketch: " + ex.Message);
                    }
                }
                sm.AddToDB = adb;
                model.ClearSelection2(true);
            }
            model.GraphicsRedraw2();
            result.Log = _log.ToString();
            result.Geometry = _summary;
            var mbNote = grp.Count > 1 ? $" - {grp.Count} plates treated as one base ({string.Join(", ", grp.Select(BodyName))})" : "";
            result.Summary = $"{result.Moats} moat{(result.Moats > 1 ? "s" : "")}, {result.Runners} runners" +
                             (result.ShiftedRunners > 0 ? $" ({result.ShiftedRunners} shifted for clearance)" : "") +
                             (result.DroppedRunners > 0 ? $", {result.DroppedRunners} dropped (no clear spot)" : "") +
                             $", {W / In:0.###} x {D / In:0.###} in, {result.FilletEdges} corners R{RAD / In:0.###} in {result.Fillets.Count} fillet feature(s){mbNote} - measured: lip " +
                             $"{(result.LipMin == double.MaxValue ? 0 : result.LipMin) / In:0.###} in, clearance {(result.ClearanceMin == double.MaxValue ? 0 : result.ClearanceMin) / In:0.###} in" +
                             (result.Problems.Count > 0 ? ". PROBLEMS: " + string.Join("; ", result.Problems) : "");
            if (result.Problems.Count > 0)
                throw new MoldApiException("The grooves were cut but these acceptance checks failed: " + string.Join("; ", result.Problems) +
                                           ". Fix them (undo or checkpoint restore first if needed), then verify again.\n" + result.Summary + "\nLog:\n" + result.Log);
            return result;
        }

        /// <summary>A rounded polygon in the open sketch: arc at each corner, line to the next corner.</summary>
        private static void DrawRounded(ISketchManager sm, List<FlashLayout.Corner> corners)
        {
            for (var i = 0; i < corners.Count; i++)
            {
                var cur = corners[i];
                var nx = corners[(i + 1) % corners.Count];
                if (cur.HasArc) sm.CreateArc(cur.C[0], cur.C[1], 0, cur.Ta[0], cur.Ta[1], 0, cur.Tb[0], cur.Tb[1], 0, (short)cur.Dir);
                sm.CreateLine(cur.Tb[0], cur.Tb[1], 0, nx.Ta[0], nx.Ta[1], 0);
            }
        }

        /// <summary>Inner loops of a face that look like cavities (for the auto-pick ranking).</summary>
        private int InnerCavityCount(IFace2 f)
        {
            var n = UnitN(f);
            var u = Math.Abs(n[0]) > 0.9 ? new double[] { 0, 1, 0 } : new double[] { 1, 0, 0 };
            var d = Dot3(u, n);
            u = V3.Unit(new[] { u[0] - d * n[0], u[1] - d * n[1], u[2] - d * n[2] });
            var v = V3.Cross(n, u);
            var count = 0;
            foreach (ILoop2 l in (object[])f.GetLoops())
            {
                if (l.IsOuter()) continue;
                var es = ((object[])l.GetEdges()).Cast<IEdge>().ToList();
                var uv = es.SelectMany(SampE).Select(p => new[] { Dot3(p, u), Dot3(p, v) }).ToList();
                if (uv.Count > 0 && IsCav(es, uv)) count++;
            }
            return count;
        }

        /// <summary>The planar face facing `normal` at a point: the face nearest the point among the solids' faces with that normal.</summary>
        private IFace2 FaceAt(double[] p, double[] normal)
        {
            IFace2 best = null;
            var bestD = 1e-6;
            foreach (var b in SwGeo.Solids(_model, true))
                foreach (IFace2 f in (object[])b.GetFaces())
                {
                    if (!IsPlane(f) || Dot3(UnitN(f), normal) < 0.9999) continue;
                    var d = DistTo(f, p);
                    if (d < bestD)
                    {
                        bestD = d;
                        best = f;
                    }
                }
            return best;
        }

        /// <summary>The largest planar face at the parting plane facing the same way (the runner sketch's face).</summary>
        private IFace2 TopFace(double[] n0, double h0)
        {
            IFace2 top = null;
            foreach (var b in SwGeo.Solids(_model, true))
                foreach (IFace2 f in (object[])b.GetFaces())
                {
                    if (!IsPlane(f) || Dot3(UnitN(f), n0) < 0.999 || Math.Abs(PlaneH(f, n0) - h0) > 1e-6) continue;
                    if (top == null || f.GetArea() > top.GetArea()) top = f;
                }
            return top;
        }
    }
}

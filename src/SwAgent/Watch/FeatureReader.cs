using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.ModelFiles;
using SwAgent.Tools;

namespace SwAgent.Watch
{
    /// <summary>
    /// Reads the feature tree into FeatureStates: each feature's type and the parameters a person would mention
    /// (fillet radius and edge count, extrusion end condition and depth, pattern counts, sketch contents...), plus
    /// every dimension that drives it. Read-only API calls on the UI thread, sliced so SolidWorks stays responsive.
    /// </summary>
    internal static class FeatureReader
    {
        private static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HistoryFolder", "SelectionSetFolder", "SensorFolder", "DocsFolder", "DetailCabinet", "CommentsFolder",
            "FavoriteFolder", "InkMarkupFolder", "EnvFolder", "MBD3DViewFolder", "LiveSectionFolder", "SurfaceBodyFolder",
            "SolidBodyFolder", "MaterialFolder", "EqnFolder", "OriginProfileFeature", "ProfileFeatureFolder", "MateGroup",
            "RefAxisFolder", "BlockFolder", "GridFeature", "FtrFolder", "CutListFolder", "SubWeldFolder", "SubAtomFolder",
        };

        private static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProfileFeature"] = "Sketch", ["3DProfileFeature"] = "3D sketch", ["Extrusion"] = "Boss-Extrude", ["Cut"] = "Cut-Extrude",
            ["BossThin"] = "Thin boss-extrude", ["CutThin"] = "Thin cut-extrude", ["Revolution"] = "Revolve", ["RevCut"] = "Cut-Revolve",
            ["Sweep"] = "Sweep", ["SweepCut"] = "Cut-Sweep", ["Blend"] = "Loft", ["BlendCut"] = "Cut-Loft", ["Fillet"] = "Fillet",
            ["VarFillet"] = "Variable fillet", ["FaceFillet"] = "Face fillet", ["Chamfer"] = "Chamfer", ["Shell"] = "Shell",
            ["HoleWzd"] = "Hole Wizard", ["AdvHoleWzd"] = "Advanced hole", ["SimpleHole"] = "Hole", ["LPattern"] = "Linear pattern",
            ["CirPattern"] = "Circular pattern", ["MirrorPattern"] = "Mirror", ["MirrorSolid"] = "Mirror body", ["SketchPattern"] = "Sketch pattern",
            ["CurvePattern"] = "Curve pattern", ["TablePattern"] = "Table pattern", ["FillPattern"] = "Fill pattern", ["RefPlane"] = "Plane",
            ["RefAxis"] = "Axis", ["RefPoint"] = "Point", ["CoordSys"] = "Coordinate system", ["Draft"] = "Draft", ["Rib"] = "Rib",
            ["Thicken"] = "Thicken", ["MoveFace"] = "Move face", ["Split"] = "Split", ["Combine"] = "Combine", ["DeleteBody"] = "Delete body",
            ["MoveCopyBody"] = "Move/copy body", ["Dome"] = "Dome", ["Wrap"] = "Wrap", ["Imported"] = "Imported body",
            ["BaseBody"] = "Base body", ["Reference"] = "Component", ["SheetMetal"] = "Sheet metal", ["SMBaseFlange"] = "Base flange",
            ["EdgeFlange"] = "Edge flange", ["SketchBend"] = "Sketched bend", ["Thread"] = "Thread", ["CosmeticThread"] = "Cosmetic thread",
            ["SurfExtrude"] = "Extruded surface", ["SurfRevolve"] = "Revolved surface", ["OffsetRefSurface"] = "Offset surface",
            ["SurfKnit"] = "Knit surface", ["SurfCut"] = "Surface cut", ["ICE"] = "Boss-Extrude",
        };

        /// <summary>The whole tree (top-level features, their sub-features and mates), in tree order.</summary>
        public static async Task<List<FeatureState>> SnapshotAsync(IModelDoc2 model, Slicer slicer, CancellationToken ct)
        {
            var result = new List<FeatureState>();
            var seen = new HashSet<int>();
            var feat = model.FirstFeature() as IFeature;
            while (feat != null)
            {
                await slicer.TickAsync(ct);
                Add(result, seen, feat, model);
                var sub = feat.GetFirstSubFeature() as IFeature;
                var n = 0;
                while (sub != null && n++ < 500)
                {
                    Add(result, seen, sub, model);
                    sub = sub.GetNextSubFeature() as IFeature;
                }
                feat = feat.GetNextFeature() as IFeature;
            }
            return result;
        }

        /// <summary>Cheap fingerprint (ids, names, suppression) - when it changes, a full snapshot is worth taking.</summary>
        public static string Signature(IModelDoc2 model)
        {
            var sb = new System.Text.StringBuilder();
            var feat = model.FirstFeature() as IFeature;
            while (feat != null)
            {
                sb.Append(feat.GetID()).Append(':').Append(feat.Name).Append(';');
                feat = feat.GetNextFeature() as IFeature;
            }
            return sb.ToString();
        }

        private static void Add(List<FeatureState> list, HashSet<int> seen, IFeature feat, IModelDoc2 model)
        {
            try
            {
                var type = feat.GetTypeName2();
                if (Skip.Contains(type) || !seen.Add(feat.GetID())) return;
                list.Add(Read(feat, type, model));
            }
            catch
            {
                // A feature that can't be read (e.g. mid-rebuild) is picked up by the next snapshot.
            }
        }

        internal static FeatureState Read(IFeature feat, string type, IModelDoc2 model)
        {
            var s = new FeatureState { Id = feat.GetID(), Name = feat.Name, Type = type, Kind = KindOf(type, feat) };
            Try(() => s.Suppressed = feat.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) is bool[] sup && sup.Length > 0 && sup[0]);
            Try(() =>
            {
                if (feat.GetParents() is object[] parents && parents.Length > 0)
                    s.Parents = string.Join(", ", parents.OfType<IFeature>().Select(p => p.Name).Take(4));
            });
            if (s.Suppressed) return s;

            var summary = new List<string>();
            void P(string key, string value, bool show = true)
            {
                if (string.IsNullOrEmpty(value)) return;
                s.Params.Add(new KeyValuePair<string, string>(key, value));
                if (show) summary.Add($"{key} {value}");
            }

            Try(() =>
            {
                switch (feat.GetDefinition())
                {
                    case IExtrudeFeatureData2 ex:
                        P("end", $"{End(ex.GetEndCondition(true))}{(Depthless(ex.GetEndCondition(true)) ? "" : " " + Mm(ex.GetDepth(true)))}");
                        if (ex.BothDirections) P("direction 2", $"{End(ex.GetEndCondition(false))}{(Depthless(ex.GetEndCondition(false)) ? "" : " " + Mm(ex.GetDepth(false)))}");
                        if (ex.ReverseDirection) P("reversed", "yes");
                        if (ex.GetDraftWhileExtruding(true)) P("draft", Deg(ex.GetDraftAngle(true)));
                        break;
                    case ISimpleFilletFeatureData2 fi:
                    {
                        var kind = fi.Type == (int)swSimpleFilletType_e.swFaceFillet ? "face fillet" : fi.Type == (int)swSimpleFilletType_e.swFullRoundFillet ? "full round" : "constant radius";
                        P("type", kind, false);
                        P("radius", Mm(fi.DefaultRadius), false);
                        summary.Add(fi.Type == (int)swSimpleFilletType_e.swFullRoundFillet ? kind : $"{kind} {Mm(fi.DefaultRadius)}");
                        if (fi.IsMultipleRadius) P("multiple radius", "yes");
                        var edges = 0;
                        Try(() => edges = fi.GetEdgeCount());
                        if (edges > 0) P("edges", edges.ToString());
                        break;
                    }
                    case IChamferFeatureData2 ch:
                    {
                        var kind = ch.Type == (int)swChamferType_e.swChamferAngleDistance ? "angle-distance" : ch.Type == (int)swChamferType_e.swChamferDistanceDistance ? "distance-distance"
                            : ch.Type == (int)swChamferType_e.swChamferEqualDistance ? "equal distance" : "vertex";
                        P("type", kind, false);
                        var dist = "";
                        Try(() => dist = Mm(ch.GetEdgeChamferDistance(0)));
                        P("distance", dist, false);
                        if (ch.Type == (int)swChamferType_e.swChamferAngleDistance) P("angle", Deg(ch.EdgeChamferAngle), false);
                        summary.Add($"{kind} {dist}{(ch.Type == (int)swChamferType_e.swChamferAngleDistance ? " x " + Deg(ch.EdgeChamferAngle) : "")}".Trim());
                        var cedges = 0;
                        Try(() => cedges = ch.GetEdgeCount());
                        if (cedges > 0) P("edges", cedges.ToString());
                        break;
                    }
                    case IShellFeatureData sh:
                        P("thickness", Mm(sh.Thickness));
                        if (sh.FacesRemovedCount > 0) P("faces removed", sh.FacesRemovedCount.ToString());
                        break;
                    case IWizardHoleFeatureData2 h:
                        P("standard", h.Standard);
                        P("fastener", h.FastenerType);
                        P("size", h.FastenerSize);
                        Try(() =>
                        {
                            if (h.HoleDiameter > 0) P("diameter", Mm(h.HoleDiameter));
                            if (h.HoleDepth > 0) P("depth", Mm(h.HoleDepth));
                        });
                        break;
                    case ILinearPatternFeatureData lp:
                        P("instances", lp.D1TotalInstances.ToString());
                        P("spacing", Mm(lp.D1Spacing));
                        if (lp.D2TotalInstances > 1)
                        {
                            P("instances 2", lp.D2TotalInstances.ToString());
                            P("spacing 2", Mm(lp.D2Spacing));
                        }
                        break;
                    case ICircularPatternFeatureData cp:
                        P("instances", cp.TotalInstances.ToString());
                        P(cp.EqualSpacing ? "total angle" : "angle", Deg(cp.Spacing));
                        break;
                    case IRevolveFeatureData2 rv:
                        P("angle", Deg(rv.GetRevolutionAngle(true)));
                        break;
                    case IRefPlaneFeatureData pl:
                        if (Math.Abs(pl.Distance) > 1e-12) P("offset", Mm(pl.Distance));
                        if (Math.Abs(pl.Angle) > 1e-12) P("angle", Deg(pl.Angle));
                        break;
                }
            });

            Try(() =>
            {
                switch (feat.GetSpecificFeature2())
                {
                    case ISketch sk:
                        summary.Add(SketchSummary(sk, s));
                        break;
                    case IMate2 mate:
                        P("mate", ((swMateType_e)mate.Type).ToString().Replace("swMate", "").ToLowerInvariant());
                        P("alignment", ((swMateAlign_e)mate.Alignment).ToString().Replace("swMateAlign", "").Replace("swAlign", "").ToLowerInvariant().Replace('_', '-'), false);
                        break;
                    case IComponent2 comp:
                        P("file", System.IO.Path.GetFileName(comp.GetPathName()));
                        break;
                }
            });

            // Every dimension that drives it, by full name - the values a tool would make into inputs.
            Try(() =>
            {
                var dd = feat.GetFirstDisplayDimension() as IDisplayDimension;
                var n = 0;
                while (dd != null && n++ < 60)
                {
                    var dim = dd.GetDimension2(0);
                    if (dim != null)
                    {
                        var angular = dim.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
                        var name = dim.FullName;
                        if (!string.IsNullOrEmpty(name)) s.Params.Add(new KeyValuePair<string, string>(name, angular ? Deg(dim.SystemValue) : Mm(dim.SystemValue)));
                    }
                    dd = feat.GetNextDisplayDimension(dd) as IDisplayDimension;
                }
            });

            if (summary.Count == 0)
            {
                var dims = s.Params.Where(p => p.Key.Contains("@")).Take(4).ToList();
                if (dims.Count > 0) summary.Add(string.Join(", ", dims.Select(d => $"{d.Key.Split('@')[0]} = {d.Value}")));
            }
            var edgeCount = s.Param("edges");
            s.Summary = string.Join(", ", summary.Where(x => !string.IsNullOrEmpty(x) && !x.StartsWith("edges ")));
            if (edgeCount != null) s.Summary += $" on {edgeCount} edge{(edgeCount == "1" ? "" : "s")}";
            if (!string.IsNullOrEmpty(s.Parents) && (type == "ProfileFeature" || type == "3DProfileFeature")) s.Summary = $"on {s.Parents}{(s.Summary.Length > 0 ? ": " + s.Summary : "")}";
            s.Summary = s.Summary.Trim();
            return s;
        }

        /// <summary>"4 lines, 1 circle Ø10 mm, 2 relations"; also records counts as parameters so sketch edits show up.</summary>
        private static string SketchSummary(ISketch sk, FeatureState s)
        {
            var segs = (sk.GetSketchSegments() as object[])?.OfType<ISketchSegment>().ToList() ?? new List<ISketchSegment>();
            var lines = 0;
            var arcs = 0;
            var circles = new List<string>();
            var other = 0;
            var construction = 0;
            foreach (var seg in segs)
            {
                if (seg.ConstructionGeometry)
                {
                    construction++;
                    continue;
                }
                switch (seg)
                {
                    case ISketchLine _:
                        lines++;
                        break;
                    case ISketchArc a:
                    {
                        var st = (ISketchPoint)a.GetStartPoint2();
                        var en = (ISketchPoint)a.GetEndPoint2();
                        if (Math.Abs(st.X - en.X) < 1e-9 && Math.Abs(st.Y - en.Y) < 1e-9) circles.Add("Ø" + Mm(a.GetRadius() * 2));
                        else arcs++;
                        break;
                    }
                    default:
                        other++;
                        break;
                }
            }
            var parts = new List<string>();
            if (lines > 0) parts.Add($"{lines} line{(lines == 1 ? "" : "s")}");
            if (circles.Count > 0)
            {
                var groups = circles.GroupBy(c => c).Select(g => g.Count() == 1 ? $"circle {g.Key}" : $"{g.Count()} circles {g.Key}");
                parts.Add(string.Join(", ", groups));
            }
            if (arcs > 0) parts.Add($"{arcs} arc{(arcs == 1 ? "" : "s")}");
            if (other > 0) parts.Add($"{other} other segment{(other == 1 ? "" : "s")}");
            if (construction > 0) parts.Add($"{construction} construction");
            s.Params.Add(new KeyValuePair<string, string>("segments", (segs.Count - construction).ToString()));
            if (circles.Count > 0) s.Params.Add(new KeyValuePair<string, string>("circles", string.Join(" ", circles)));
            return parts.Count == 0 ? "empty" : string.Join(", ", parts);
        }

        private static string KindOf(string type, IFeature feat)
        {
            if (type.StartsWith("Mate", StringComparison.OrdinalIgnoreCase) && type.Length > 4) return "Mate";
            return Kinds.TryGetValue(type, out var k) ? k : type;
        }

        private static string End(int cond)
        {
            switch ((swEndConditions_e)cond)
            {
                case swEndConditions_e.swEndCondBlind: return "blind";
                case swEndConditions_e.swEndCondThroughAll: return "through all";
                case swEndConditions_e.swEndCondThroughNext: return "up to next";
                case swEndConditions_e.swEndCondUpToVertex: return "up to vertex";
                case swEndConditions_e.swEndCondUpToSurface: return "up to surface";
                case swEndConditions_e.swEndCondOffsetFromSurface: return "offset from surface";
                case swEndConditions_e.swEndCondMidPlane: return "mid-plane";
                case swEndConditions_e.swEndCondUpToBody: return "up to body";
                case swEndConditions_e.swEndCondThroughAllBoth: return "through all both";
                default: return ((swEndConditions_e)cond).ToString().Replace("swEndCond", "").ToLowerInvariant();
            }
        }

        private static bool Depthless(int cond) =>
            cond == (int)swEndConditions_e.swEndCondThroughAll || cond == (int)swEndConditions_e.swEndCondThroughNext ||
            cond == (int)swEndConditions_e.swEndCondUpToVertex || cond == (int)swEndConditions_e.swEndCondUpToSurface ||
            cond == (int)swEndConditions_e.swEndCondUpToBody || cond == (int)swEndConditions_e.swEndCondThroughAllBoth;

        private static string Mm(double meters) => ModelTools.Mm(meters) + " mm";

        private static string Deg(double radians) => WatchFormat.Num(radians * 180 / Math.PI) + " deg";

        private static void Try(Action a)
        {
            try
            {
                a();
            }
            catch
            {
                // Each parameter is optional; one unreadable value must not hide the feature.
            }
        }
    }
}

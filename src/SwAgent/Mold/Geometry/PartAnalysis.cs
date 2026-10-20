using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.UI;

namespace SwAgent.Mold.Geometry
{
    /// <summary>What mold_part (and Mold.Analyze) looks at and how.</summary>
    public sealed class PartAnalysisOptions
    {
        /// <summary>Pull direction in model coordinates; null = +W (the Top Plane's normal).</summary>
        public double[] Pull;
        /// <summary>base | lid | none: the side the liquid is poured into (air traps).</summary>
        public string Fill = "base";
        public double TiltDeg;
        public double MinDraftDeg = 0.5;
        public double SampleMm = 1.0;
        /// <summary>Specific gravity for the mass (null: not reported).</summary>
        public double? Sg;
        /// <summary>Critical faces (no parting line on them): id and face.</summary>
        public List<(string Id, IFace2 Face)> Critical = new List<(string, IFace2)>();
        /// <summary>Vents and risers (model meters): they drain the air above them.</summary>
        public List<double[]> Vents = new List<double[]>();
        /// <summary>Fragile thresholds (design 3.14): insert when the core is under this diameter...</summary>
        public double MinCoreMm = 3.0;
        /// <summary>...or taller than this times its diameter.</summary>
        public double MaxHOverD = 2.0;
        public int MaxSamples = 20000;
        public int MaxColumns = 20000;
        public int Batch = 2000;
    }

    /// <summary>The analysis result: text for the agent, JSON for the record, and overlay items to show it.</summary>
    public sealed class PartReport
    {
        public string Text;
        public JObject Json;
        public JObject RecordPatch;
        internal List<SceneOverlay.Item> Overlay = new List<SceneOverlay.Item>();
        public PartingLine.Result PartingLine;
        public List<AirTraps.Trap> Traps;
        public List<Fragile.Feature> Fragile;
        public MoldFrame Frame;
    }

    /// <summary>
    /// mold_part's analysis of the molded part [SolidWorks] (design 2.3.3 with critique A7 and B4): tessellation draft per
    /// face, undercut rays per sample, the projection raster and outline, planar parting-line candidates per sample,
    /// column rays (ENTRY_EXIT | UNBLOCK) for the part's W intervals, air traps by priority-flood, and fragile steel. The
    /// SolidWorks calls are the tessellation, the mass properties and the ray casts, all on the UI thread inside the tool
    /// call; everything else is the pure code in this folder. Read-only on the model.
    /// </summary>
    public static class PartAnalysis
    {
        private sealed class Tri
        {
            public int Face;
            public double[] P0, P1, P2; // frame coordinates (m)
            public double[] N; // outward unit normal, frame
            public double Area;
        }

        public static PartReport Run(ISldWorks sw, IModelDoc2 model, IBody2 body, PartAnalysisOptions o, CancellationToken ct = default)
        {
            SwGeo.RequirePart(model);
            if (body == null) throw new MoldApiException("No body to analyze.");
            o = o ?? new PartAnalysisOptions();
            var notes = new List<string>();
            var top = SwGeo.Frame(model);
            var frame = top;
            if (o.Pull != null && o.Pull.Length >= 3)
            {
                var pull = V3.Unit(o.Pull);
                if (V3.Dot(pull, top.W) < 1 - 1e-9) frame = MoldFrame.FromNormal(pull);
            }
            var sinMin = Math.Sin(o.MinDraftDeg * Math.PI / 180);

            // 1. Tessellation into the frame.
            var faces = ((body.GetFaces() as object[]) ?? new object[0]).OfType<IFace2>().ToList();
            if (faces.Count == 0) throw new MoldApiException("The body has no faces.");
            var tris = new List<Tri>();
            var facesWithout = 0;
            for (var fi = 0; fi < faces.Count; fi++)
            {
                ct.ThrowIfCancellationRequested();
                var t = faces[fi].GetTessTriangles(true) as float[];
                var n = faces[fi].GetTessNorms() as float[];
                if (t == null || t.Length < 9)
                {
                    facesWithout++;
                    continue;
                }
                var hasNorms = n != null && n.Length == t.Length;
                for (var k = 0; k + 8 < t.Length; k += 9)
                {
                    var p0 = frame.ToFrame(new double[] { t[k], t[k + 1], t[k + 2] });
                    var p1 = frame.ToFrame(new double[] { t[k + 3], t[k + 4], t[k + 5] });
                    var p2 = frame.ToFrame(new double[] { t[k + 6], t[k + 7], t[k + 8] });
                    var cr = V3.Cross(V3.Sub(p1, p0), V3.Sub(p2, p0));
                    var len = V3.Len(cr);
                    if (len < 1e-18) continue;
                    var normal = V3.Mul(cr, 1 / len);
                    if (hasNorms)
                    {
                        var nv = frame.DirToFrame(new double[] { n[k] + n[k + 3] + n[k + 6], n[k + 1] + n[k + 4] + n[k + 7], n[k + 2] + n[k + 5] + n[k + 8] });
                        var nl = V3.Len(nv);
                        if (nl > 1e-12) normal = V3.Mul(nv, 1 / nl); // the surface's own (outward) normals
                    }
                    tris.Add(new Tri { Face = fi, P0 = p0, P1 = p1, P2 = p2, N = normal, Area = len / 2 });
                }
            }
            if (tris.Count == 0)
                throw new MoldApiException("The body has no display tessellation (is it hidden?). Show the body, rebuild, and run again.");
            if (facesWithout > 0) notes.Add($"{facesWithout} face(s) had no display tessellation and were skipped (hidden or never drawn)");

            // Volume, area, box, mass.
            var volume = SwGeo.Volume(body);
            var area = faces.Sum(f => f.GetArea());
            var box = SwGeo.Box(body, frame);
            double wMin = box[2], wMax = box[5];

            // 2. Draft per face.
            var faceDraft = new Dictionary<int, Draft.FaceDraft>();
            foreach (var g in tris.GroupBy(t => t.Face))
                faceDraft[g.Key] = Draft.Classify(g.Select(t => (t.Area, t.N[2])), o.MinDraftDeg);

            // 3. Samples and undercut rays (one ray per sample, the way it faces; first hit only).
            var sampleArea = Math.Pow(Math.Max(0.05, o.SampleMm) * SwGeo.Mm, 2);
            var picks = Draft.Samples(tris.Select(t => t.Area).ToList(), sampleArea, o.MaxSamples);
            var samples = new List<PartSample>(picks.Count);
            foreach (var (ti, a, b, w) in picks)
            {
                var t = tris[ti];
                var p = V3.Add(t.P0, V3.Add(V3.Mul(V3.Sub(t.P1, t.P0), a), V3.Mul(V3.Sub(t.P2, t.P0), b)));
                samples.Add(new PartSample { Face = t.Face, U = p[0], V = p[1], W = p[2], NW = t.N[2], Area = w });
            }
            var rayIdx = new List<int>();
            var pts = new List<double[]>();
            var dirs = new List<double[]>();
            for (var k = 0; k < samples.Count; k++)
            {
                var s = samples[k];
                if (Math.Abs(s.NW) < sinMin) continue;
                var t = tris[picks[k].Tri];
                var start = V3.Add(new[] { s.U, s.V, s.W }, V3.Mul(t.N, 1e-5)); // 0.01 mm off the surface
                rayIdx.Add(k);
                pts.Add(frame.ToModel(start));
                dirs.Add(frame.DirToModel(new double[] { 0, 0, s.NW > 0 ? 1 : -1 }));
            }
            var hitsPerRay = CastFirstHits(model, body, pts, dirs, o.Batch, ct);
            for (var r = 0; r < rayIdx.Count; r++)
            {
                var s = samples[rayIdx[r]];
                if (s.NW > 0) s.UpClear = !hitsPerRay[r];
                else s.DownClear = !hitsPerRay[r];
            }

            // 4. Projection raster, outline, perimeter.
            var lMin = Math.Min(box[3] - box[0], box[4] - box[1]);
            var h = Math.Max(0.1, Math.Min(1.0, lMin / SwGeo.Mm / 400)) * SwGeo.Mm;
            var grid = Grid2.Covering(box[0], box[1], box[3], box[4], h, 2);
            var footprint = Raster.Fill(grid, tris.Select(t => new[] { t.P0[0], t.P0[1], t.P1[0], t.P1[1], t.P2[0], t.P2[1] }));
            var projectedArea = Raster.Area(footprint, h);
            var loops = Raster.Outlines(footprint, grid).Select(l => Raster.Clean(l, h)).Where(l => l.Count >= 3).ToList();
            var outer = loops.Where(l => Raster.SignedArea(l) > 0).OrderByDescending(Raster.SignedArea).ToList();
            var holes = loops.Count(l => Raster.SignedArea(l) < 0);
            var perimeter = outer.Sum(l => Raster.Length(l));

            // 5. Columns (multi-hit), the part's W intervals per cell.
            var spacing = Math.Max(o.SampleMm * SwGeo.Mm, Math.Sqrt(projectedArea / Math.Max(1, o.MaxColumns)));
            var cgrid = Grid2.Covering(box[0], box[1], box[3], box[4], spacing, 1);
            var columns = new ColumnMap(cgrid);
            var cells = new List<(int I, int J)>();
            for (var i = 0; i < cgrid.Nu; i++)
                for (var j = 0; j < cgrid.Nv; j++)
                {
                    var fi = (int)Math.Floor((cgrid.CU(i) - grid.U0) / h);
                    var fj = (int)Math.Floor((cgrid.CV(j) - grid.V0) / h);
                    if (fi >= 0 && fj >= 0 && fi < grid.Nu && fj < grid.Nv && footprint[fi, fj]) cells.Add((i, j));
                }
            var colMode = FillColumns(model, body, frame, columns, cells, wMin, wMax, o.Batch, ct, notes);

            // 6. Parting-line candidates (per sample), with critical faces.
            var critical = new List<(string Id, double WMin, double WMax)>();
            foreach (var (id, face) in o.Critical)
            {
                var ws = WRange(face, frame);
                if (ws != null) critical.Add((id, ws.Value.Min, ws.Value.Max));
                else notes.Add($"critical face {id} has no tessellation; not checked against the parting line");
            }
            var tol = Math.Max(0.01 * SwGeo.Mm, 1e-6);
            var pl = PartingLine.Analyze(samples, o.MinDraftDeg, tol, columns.SectionArea, critical);

            // 7. Air traps per candidate.
            var fill = (o.Fill ?? "base").Trim().ToLowerInvariant();
            var lidPour = fill == "lid";
            List<AirTraps.Trap> bestTraps = null;
            var ventCells = o.Vents.Select(v => frame.ToFrame(v)).Select(p => ((int)Math.Floor((p[0] - cgrid.U0) / spacing), (int)Math.Floor((p[1] - cgrid.V0) / spacing))).ToList();
            var vOfColumn = Enumerable.Range(0, cgrid.Nv).Select(cgrid.CV).ToArray();
            var ceiling = columns.Ceiling(lidPour);
            List<AirTraps.Trap> TrapsAt(double plW) => fill == "none" ? new List<AirTraps.Trap>() : AirTraps.Find(new AirTraps.Input
            {
                Ceiling = ceiling,
                Cell = spacing,
                PlLevel = lidPour ? -plW : plW,
                VOfColumn = vOfColumn,
                TiltRad = o.TiltDeg * Math.PI / 180,
                Vents = ventCells,
                MinDepth = 0.05 * SwGeo.Mm,
            });
            foreach (var c in pl.Candidates)
            {
                var tr = TrapsAt(c.W);
                c.AirTraps = tr.Count;
                c.AirTrapVolume = tr.Sum(x => x.Volume);
            }
            PartingLine.Score(pl, fill, volume);
            if (pl.Candidates.Count > 0) bestTraps = TrapsAt(pl.Candidates[0].W);
            var internalCeilings = fill == "none" ? new List<(int I, int J, double CeilingW)>() : columns.InternalCeilings(lidPour);

            // 8. Fragile steel.
            var fragile = Fragile.Find(columns, new Fragile.Options { MinD = o.MinCoreMm * SwGeo.Mm, MaxHOverD = o.MaxHOverD, MinHeight = Math.Max(0.5 * SwGeo.Mm, spacing / 2) });

            // ---------- report ----------
            var report = new PartReport { Frame = frame, PartingLine = pl, Traps = bestTraps ?? new List<AirTraps.Trap>(), Fragile = fragile };
            string F(double x, string fmt = "0.##") => x.ToString(fmt, CultureInfo.InvariantCulture);
            double Mm(double m) => Math.Round(m / SwGeo.Mm, 3);
            var faceRefs = new Dictionary<int, string>();
            string Ref(int fi)
            {
                if (!faceRefs.TryGetValue(fi, out var r)) faceRefs[fi] = r = SwGeo.PersistRef(model, faces[fi]);
                return r;
            }
            JArray P(double[] uvw) => new JArray(frame.ToModel(uvw).Select(x => Mm(x)));

            var draftFaces = new JArray();
            foreach (var kv in faceDraft.OrderBy(k => k.Key))
            {
                if (kv.Value.Class == DraftClass.Positive || kv.Value.Class == DraftClass.Negative) continue;
                draftFaces.Add(new JObject
                {
                    ["id"] = $"F{kv.Key}",
                    ["class"] = kv.Value.Class == DraftClass.NeedsDraft ? "needs_draft" : "straddle",
                    ["minDeg"] = Math.Round(kv.Value.MinDeg, 2),
                    ["maxDeg"] = Math.Round(kv.Value.MaxDeg, 2),
                    ["lowShare"] = Math.Round(kv.Value.LowFraction, 3),
                    ["area_mm2"] = Math.Round(kv.Value.Area / 1e-6, 1),
                    ["persist"] = Ref(kv.Key),
                });
            }
            var undercuts = new JArray(pl.UndercutFaces.Select(fi => new JObject
            {
                ["id"] = $"F{fi}",
                ["area_mm2"] = Math.Round(samples.Where(s => s.Face == fi && PartingLine.Classify(s, sinMin) == SampleSide.Undercut).Sum(s => s.Area) / 1e-6, 1),
                ["side"] = samples.Any(s => s.Face == fi && s.NW > 0 && s.UpClear == false) ? "lid" : "base",
                ["persist"] = Ref(fi),
            }));
            var candidates = new JArray(pl.Candidates.Select(c => new JObject
            {
                ["id"] = c.Id,
                ["w_mm"] = Mm(c.W),
                ["why"] = c.Why,
                ["lidDepth_mm"] = Mm(c.LidDepth),
                ["baseDepth_mm"] = Mm(c.BaseDepth),
                ["draftArea_mm2"] = new JObject { ["lid"] = Math.Round(c.DraftAreaLid / 1e-6, 1), ["base"] = Math.Round(c.DraftAreaBase / 1e-6, 1) },
                ["airTraps"] = c.AirTraps,
                ["airTrapVolume_mm3"] = Math.Round(c.AirTrapVolume / 1e-9, 1),
                ["veto"] = c.Veto ? c.VetoReason : null,
                ["score"] = c.Score,
                ["scoreNotes"] = new JArray(c.ScoreNotes),
            }));
            var traps = new JArray(report.Traps.Select(t => new JObject
            {
                ["id"] = $"AT{t.Id}",
                ["at"] = P(new[] { cgrid.CU(t.HighI), cgrid.CV(t.HighJ), lidPour ? -t.HighC : t.HighC }),
                ["volume_mm3"] = Math.Round(t.Volume / 1e-9, 1),
                ["area_mm2"] = Math.Round(t.Area / 1e-6, 1),
                ["depth_mm"] = Mm(t.Depth),
                ["fill"] = fill,
                ["tiltDeg"] = o.TiltDeg,
            }));
            var fragileJson = new JArray(fragile.Select((x, k) => new JObject
            {
                ["id"] = $"K{k + 1}",
                ["kind"] = x.Kind,
                ["d_mm"] = Mm(x.D),
                ["h_mm"] = Mm(x.H),
                ["side"] = x.Side,
                ["at"] = P(new[] { x.U, x.V, x.Side == "base" ? x.WFrom : x.WTo }),
                ["flagged"] = x.Flagged,
                ["reason"] = x.Reason,
            }));
            var json = new JObject
            {
                ["body"] = SwGeo.SafeName(body),
                ["frame"] = frame.ToJson(),
                ["volume_mm3"] = Math.Round(volume / 1e-9, 1),
                ["area_mm2"] = Math.Round(area / 1e-6, 1),
                ["box_mm"] = new JArray(Mm(box[3] - box[0]), Mm(box[4] - box[1]), Mm(box[5] - box[2])),
                ["boxFrame_mm"] = new JArray(box.Select(x => Mm(x))),
                ["projectedArea_mm2"] = Math.Round(projectedArea / 1e-6, 1),
                ["perimeter_mm"] = Math.Round(perimeter / SwGeo.Mm, 1),
                ["outlineHoles"] = holes,
                ["mass_kg"] = o.Sg.HasValue ? Math.Round(volume * o.Sg.Value * 1000, 4) : (JToken)null,
                ["draft"] = new JObject
                {
                    ["minDeg"] = o.MinDraftDeg,
                    ["pull"] = new JArray(frame.W.Select(x => Math.Round(x, 9))),
                    ["areaShare"] = new JObject
                    {
                        ["lidOnly"] = Math.Round(pl.AreaBySide[SampleSide.LidOnly] / pl.TotalArea, 3),
                        ["baseOnly"] = Math.Round(pl.AreaBySide[SampleSide.BaseOnly] / pl.TotalArea, 3),
                        ["needsDraft"] = Math.Round(pl.AreaBySide[SampleSide.Either] / pl.TotalArea, 3),
                        ["undercut"] = Math.Round(pl.AreaBySide[SampleSide.Undercut] / pl.TotalArea, 3),
                    },
                    ["faces"] = draftFaces,
                },
                ["undercuts"] = undercuts,
                ["partingLine"] = new JObject
                {
                    ["planar"] = pl.Planar,
                    ["zLo_mm"] = Mm(pl.ZLo),
                    ["zHi_mm"] = Mm(pl.ZHi),
                    ["candidates"] = candidates,
                    ["conflictFaces"] = new JArray(pl.ConflictFaces.Select(fi => new JObject { ["id"] = $"F{fi}", ["persist"] = Ref(fi) })),
                },
                ["airTraps"] = traps,
                ["internalCeilings"] = internalCeilings.Count,
                ["fragile"] = fragileJson,
                ["sampling"] = new JObject
                {
                    ["samples"] = samples.Count,
                    ["undercutRays"] = rayIdx.Count,
                    ["columns"] = cells.Count,
                    ["columnSpacing_mm"] = Mm(spacing),
                    ["columnRays"] = colMode,
                    ["rasterCell_mm"] = Mm(h),
                },
                ["notes"] = new JArray(notes),
            };
            report.Json = json;

            // The record keeps the part section and the analysis results (merged by id).
            report.RecordPatch = new JObject
            {
                ["frame"] = frame.ToJson(),
                ["part"] = new JObject
                {
                    ["volume_mm3"] = json["volume_mm3"],
                    ["area_mm2"] = json["area_mm2"],
                    ["box_mm"] = json["box_mm"],
                    ["projectedArea_mm2"] = json["projectedArea_mm2"],
                    ["perimeter_mm"] = json["perimeter_mm"],
                    ["draft"] = new JObject { ["minDeg"] = o.MinDraftDeg, ["faces"] = draftFaces.DeepClone() },
                    ["undercuts"] = undercuts.DeepClone(),
                    ["fragile"] = fragileJson.DeepClone(),
                    ["partingLineCandidates"] = candidates.DeepClone(),
                },
                ["airTraps"] = traps.DeepClone(),
            };

            // Text: the key numbers first, problems after.
            var sb = new StringBuilder();
            sb.AppendLine($"Part '{SwGeo.SafeName(body)}': volume {F(volume / 1e-9, "0.#")} mm3, area {F(area / 1e-6, "0.#")} mm2, box {F(Mm(box[3] - box[0]))} x {F(Mm(box[4] - box[1]))} x {F(Mm(box[5] - box[2]))} mm (U x V x W)" +
                          (o.Sg.HasValue ? $", mass {F(volume * o.Sg.Value * 1000, "0.###")} kg at SG {F(o.Sg.Value, "0.###")}" : "") + ".");
            sb.AppendLine($"Pull +W = ({F(frame.W[0], "0.###")}, {F(frame.W[1], "0.###")}, {F(frame.W[2], "0.###")}); projected area {F(projectedArea / 1e-6, "0.#")} mm2, outline perimeter {F(perimeter / SwGeo.Mm, "0.#")} mm" +
                          (holes > 0 ? $", {holes} through-hole(s) in the outline" : "") + ".");
            sb.AppendLine($"Draft (min {F(o.MinDraftDeg)} deg): lid side {F(100 * pl.AreaBySide[SampleSide.LidOnly] / pl.TotalArea, "0")}%, base side {F(100 * pl.AreaBySide[SampleSide.BaseOnly] / pl.TotalArea, "0")}%, " +
                          $"needs draft {F(100 * pl.AreaBySide[SampleSide.Either] / pl.TotalArea, "0.#")}%, undercut {F(100 * pl.AreaBySide[SampleSide.Undercut] / pl.TotalArea, "0.#")}% of the area.");
            if (draftFaces.Count > 0)
                sb.AppendLine("  Faces needing draft / spanning both halves: " + string.Join(", ", draftFaces.Take(12).Select(x => $"{x["id"]} {x["class"]} {x["minDeg"]}..{x["maxDeg"]} deg, {x["area_mm2"]} mm2")) + (draftFaces.Count > 12 ? $" (+{draftFaces.Count - 12} more)" : ""));
            if (undercuts.Count > 0)
                sb.AppendLine($"Undercuts ({undercuts.Count} face(s)): " + string.Join(", ", undercuts.Take(12).Select(x => $"{x["id"]} ({x["area_mm2"]} mm2, blocks the {x["side"]})")) + " - side action or an insert.");
            else sb.AppendLine("Undercuts: none along the pull.");
            if (pl.Planar)
            {
                sb.AppendLine($"Planar parting line possible anywhere in W {F(Mm(pl.ZLo))}..{F(Mm(pl.ZHi))} mm. Candidates (best first, {fill} pour):");
                foreach (var c in pl.Candidates)
                    sb.AppendLine($"  {c.Id} at W {F(Mm(c.W))} mm - {c.Why}; lid depth {F(Mm(c.LidDepth))}, base depth {F(Mm(c.BaseDepth))} mm; " +
                                  $"{c.AirTraps} air trap(s); score {F(c.Score, "0.###")}" + (c.Veto ? $" VETO: {c.VetoReason}" : "") + (c.ScoreNotes.Count > 0 ? $" ({string.Join(", ", c.ScoreNotes)})" : ""));
            }
            else
            {
                sb.AppendLine($"No planar parting line: lid-only faces reach down to W {F(Mm(pl.ZHi))} mm but base-only faces up to {F(Mm(pl.ZLo))} mm. " +
                              $"A stepped parting line or side action is needed; faces in conflict: {string.Join(", ", pl.ConflictFaces.Take(20).Select(fi => "F" + fi))}.");
            }
            if (fill != "none")
            {
                if (report.Traps.Count == 0) sb.AppendLine($"Air traps ({fill} pour, tilt {F(o.TiltDeg)} deg, best candidate): none.");
                else
                    sb.AppendLine($"Air traps ({fill} pour, tilt {F(o.TiltDeg)} deg, best candidate): " + string.Join("; ", traps.Take(8).Select(x =>
                        $"{x["id"]} {x["volume_mm3"]} mm3, high point ({string.Join(", ", ((JArray)x["at"]).Select(v => (double)v))}) mm")) + " - vent or riser at each high point.");
                if (internalCeilings.Count > 0) sb.AppendLine($"Internal ceilings in {internalCeilings.Count} column(s) (a ceiling inside the part): side core, or check by hand.");
            }
            var flagged = fragile.Where(x => x.Flagged).ToList();
            if (fragile.Count > 0)
                sb.AppendLine($"Steel reaching into the part ({fragile.Count}, {flagged.Count} fragile): " + string.Join("; ", fragileJson.Take(10).Select(x =>
                    $"{x["id"]} {x["kind"]} D {x["d_mm"]} x H {x["h_mm"]} mm ({x["side"]})" + ((bool)x["flagged"] ? $" FRAGILE: {x["reason"]} - steel insert" : ""))));
            if (notes.Count > 0) sb.AppendLine("Notes: " + string.Join("; ", notes) + ".");
            sb.AppendLine("Face ids F<n> are this body's face order; persistent refs are in the record (part.draft.faces, part.undercuts).");
            report.Text = sb.ToString().TrimEnd();

            // Overlay: red = needs draft or undercut, cyan = parting line, magenta = air traps, orange = fragile steel.
            var red = draftFaces.Select(x => int.Parse(((string)x["id"]).Substring(1), CultureInfo.InvariantCulture))
                .Concat(pl.UndercutFaces).Distinct().Select(fi => faces[fi]).ToList();
            if (red.Count > 0) report.Overlay.AddRange(Tools.OverlayTools.FaceItems(red, Tools.OverlayTools.ParseColor("red"), "needs draft / undercut", 0.35));
            if (pl.Candidates.Count > 0)
            {
                var best = pl.Candidates[0];
                foreach (var loop in outer)
                    report.Overlay.Add(new SceneOverlay.Item
                    {
                        Type = "polygon",
                        Points = loop.Select(p => frame.ToModel(p[0], p[1], best.W)).ToList(),
                        Color = Tools.OverlayTools.ParseColor("cyan"),
                    });
                if (outer.Count > 0)
                    report.Overlay.Add(new SceneOverlay.Item { Type = "label", Points = { frame.ToModel(outer[0][0][0], outer[0][0][1], best.W) }, Text = $"parting line {best.Id} (W {F(Mm(best.W))} mm)", Color = Tools.OverlayTools.ParseColor("cyan") });
            }
            foreach (var t in report.Traps.Take(12))
            {
                var at = frame.ToModel(cgrid.CU(t.HighI), cgrid.CV(t.HighJ), lidPour ? -t.HighC : t.HighC);
                report.Overlay.Add(new SceneOverlay.Item { Type = "marker", Points = { at }, Color = Tools.OverlayTools.ParseColor("magenta"), Radius = 12 });
                report.Overlay.Add(new SceneOverlay.Item { Type = "label", Points = { at }, Text = $"AT{t.Id} {F(t.Volume / 1e-9, "0")} mm3", Color = Tools.OverlayTools.ParseColor("magenta") });
            }
            foreach (var (x, k) in fragile.Select((x, k) => (x, k)).Where(z => z.x.Flagged).Take(12))
            {
                var at = frame.ToModel(x.U, x.V, x.Side == "base" ? x.WFrom : x.WTo);
                report.Overlay.Add(new SceneOverlay.Item { Type = "marker", Points = { at }, Color = Tools.OverlayTools.ParseColor("#FF8C1A"), Radius = 12 });
                report.Overlay.Add(new SceneOverlay.Item { Type = "label", Points = { at }, Text = $"K{k + 1} D{F(Mm(x.D), "0.#")} H{F(Mm(x.H), "0.#")}", Color = Tools.OverlayTools.ParseColor("#FF8C1A") });
            }
            return report;
        }

        /// <summary>The W range (frame) of a face's display triangles, or null.</summary>
        private static (double Min, double Max)? WRange(IFace2 face, MoldFrame frame)
        {
            if (!(face?.GetTessTriangles(true) is float[] t) || t.Length < 9) return null;
            double lo = double.MaxValue, hi = double.MinValue;
            for (var k = 0; k + 2 < t.Length; k += 3)
            {
                var w = frame.ToFrame(new double[] { t[k], t[k + 1], t[k + 2] })[2];
                lo = Math.Min(lo, w);
                hi = Math.Max(hi, w);
            }
            return (lo, hi);
        }

        /// <summary>
        /// Rays that stop at their first hit (TOPOLS | NORMALS), in batches, the stop button checked between batches. True
        /// per ray = it hit the body.
        /// </summary>
        internal static bool[] CastFirstHits(IModelDoc2 model, IBody2 body, IList<double[]> pts, IList<double[]> dirs, int batch, CancellationToken ct)
        {
            var result = new bool[pts.Count];
            const int Opts = (int)(swRayPtsOpts_e.swRayPtsOptsTOPOLS | swRayPtsOpts_e.swRayPtsOptsNORMALS);
            for (var s = 0; s < pts.Count; s += batch)
            {
                ct.ThrowIfCancellationRequested();
                var n = Math.Min(batch, pts.Count - s);
                var hits = Cast(model, body, pts, dirs, s, n, Opts);
                foreach (var h in hits) result[s + h.Ray] = true;
            }
            return result;
        }

        private static List<RayHit> Cast(IModelDoc2 model, IBody2 body, IList<double[]> pts, IList<double[]> dirs, int start, int n, int opts)
        {
            var bp = new double[n * 3];
            var dv = new double[n * 3];
            for (var k = 0; k < n; k++)
            {
                Array.Copy(pts[start + k], 0, bp, k * 3, 3);
                Array.Copy(dirs[start + k], 0, dv, k * 3, 3);
            }
            var count = model.Extension.RayIntersections(new object[] { body }, bp, dv, opts, 1e-7, 0, true);
            if (count <= 0) return new List<RayHit>();
            return Columns.Parse(model.GetRayIntersectionsPoints() as double[], count, n, 1, (opts & (int)swRayPtsOpts_e.swRayPtsOptsNORMALS) != 0);
        }

        /// <summary>
        /// Fill the column map: one ray per column from above the part straight down (-W), with ENTRY_EXIT | UNBLOCK so it
        /// reports every crossing (critique B4). If that comes back unusable (most columns with an odd or zero hit count:
        /// UNBLOCK not honoured), fall back to two first-hit rays per column - from above and from below - which give the
        /// part's envelope (no internal gaps). Returns how the columns were read.
        /// </summary>
        private static string FillColumns(IModelDoc2 model, IBody2 body, MoldFrame frame, ColumnMap map, List<(int I, int J)> cells,
            double wMin, double wMax, int batch, CancellationToken ct, List<string> notes)
        {
            if (cells.Count == 0) return "none";
            var g = map.Grid;
            var above = wMax + 0.002;
            var below = wMin - 0.002;
            var down = frame.DirToModel(new double[] { 0, 0, -1 });
            var up = frame.DirToModel(new double[] { 0, 0, 1 });
            var pts = cells.Select(c => frame.ToModel(g.CU(c.I), g.CV(c.J), above)).ToList();
            var dirs = cells.Select(c => down).ToList();
            const int Multi = (int)(swRayPtsOpts_e.swRayPtsOptsTOPOLS | swRayPtsOpts_e.swRayPtsOptsNORMALS | swRayPtsOpts_e.swRayPtsOptsENTRY_EXIT | swRayPtsOpts_e.swRayPtsOptsUNBLOCK);
            var perRay = new List<(double W, int Type)>[cells.Count];
            for (var s = 0; s < cells.Count; s += batch)
            {
                ct.ThrowIfCancellationRequested();
                var n = Math.Min(batch, cells.Count - s);
                foreach (var h in Cast(model, body, pts, dirs, s, n, Multi))
                {
                    var k = s + h.Ray;
                    (perRay[k] ?? (perRay[k] = new List<(double, int)>())).Add((frame.ToFrame(h.Point)[2], h.Type));
                }
            }
            var good = 0;
            for (var k = 0; k < cells.Count; k++)
            {
                var iv = perRay[k] == null ? new List<(double Bottom, double Top)>() : Columns.Intervals(perRay[k]);
                if (iv.Count > 0) good++;
                map.Cells[cells[k].I, cells[k].J] = iv;
            }
            if (good >= 0.8 * cells.Count) return "multi-hit (ENTRY_EXIT | UNBLOCK)";

            // Fallback: the envelope from two first-hit rays per column.
            notes.Add($"multi-hit column rays answered for only {good} of {cells.Count} columns; used the part's envelope (top and bottom first hits) instead - internal ceilings are not seen");
            var topHit = new double?[cells.Count];
            var bottomHit = new double?[cells.Count];
            var upPts = cells.Select(c => frame.ToModel(g.CU(c.I), g.CV(c.J), below)).ToList();
            const int First = (int)(swRayPtsOpts_e.swRayPtsOptsTOPOLS | swRayPtsOpts_e.swRayPtsOptsNORMALS);
            for (var s = 0; s < cells.Count; s += batch)
            {
                ct.ThrowIfCancellationRequested();
                var n = Math.Min(batch, cells.Count - s);
                foreach (var h in Cast(model, body, pts, dirs, s, n, First))
                {
                    var w = frame.ToFrame(h.Point)[2];
                    var k = s + h.Ray;
                    topHit[k] = topHit[k].HasValue ? Math.Max(topHit[k].Value, w) : w;
                }
                foreach (var h in Cast(model, body, upPts, cells.Select(c => up).ToList(), s, n, First))
                {
                    var w = frame.ToFrame(h.Point)[2];
                    var k = s + h.Ray;
                    bottomHit[k] = bottomHit[k].HasValue ? Math.Min(bottomHit[k].Value, w) : w;
                }
            }
            for (var k = 0; k < cells.Count; k++)
                map.Cells[cells[k].I, cells[k].J] = topHit[k].HasValue && bottomHit[k].HasValue && topHit[k] > bottomHit[k]
                    ? new List<(double, double)> { (bottomHit[k].Value, topHit[k].Value) }
                    : new List<(double, double)>();
            return "envelope (first hits from above and below)";
        }
    }
}

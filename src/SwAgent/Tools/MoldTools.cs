using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SwAgent.Infrastructure;
using SwAgent.Mold;
using SwAgent.Mold.Geometry;
using SwAgent.Scripting;
using SwAgent.UI;

namespace SwAgent.Tools
{
    /// <summary>
    /// The design-compression-mold tool's agent tools (design 2.3.2-2.3.5): mold_calc (the sizing calculator), mold_part
    /// (analysis of the molded part), mold_bodies (the mold's bodies: report, clearance, export) and mold_record (the
    /// design record, also the simulator manifest). Only mold_bodies export writes files beside the model, and mold_record
    /// init sets two custom properties; none of them adds features. Model changes stay in execute_csharp cells with Mold.*.
    /// </summary>
    public sealed class MoldTools
    {
        public const string ToolName = "design-compression-mold";

        /// <summary>The built-in tool's folder (data/, templates), next to the add-in.</summary>
        internal static string ToolDir => Path.Combine(UserTools.BuiltInRoot, ToolName);

        /// <summary>The shop's overrides of the shipped data (survive reinstalls): %APPDATA%\SwAgent\tools\builtin-data\design-compression-mold.</summary>
        internal static string OverrideDir => Path.Combine(UserTools.Root, "builtin-data", ToolName);

        private static MoldData LoadData() => MoldData.Load(ToolDir, Directory.Exists(OverrideDir) ? OverrideDir : null);

        /// <summary>The record of the chat's mold part, or null when there is none yet.</summary>
        private static MoldRecord RecordOf(IModelDoc2 model)
        {
            var folder = SwGeo.RecordFolder(model);
            return folder != null && File.Exists(Path.Combine(folder, "mold.json")) ? MoldRecord.Open(folder) : null;
        }

        private static string Json(JToken t, int max)
        {
            var s = t?.ToString(Formatting.Indented) ?? "null";
            if (s.Length <= max) return s;
            var compact = t.ToString(Formatting.None);
            return compact.Length <= max ? compact : compact.Substring(0, max) + " ... (truncated)";
        }

        /// <summary>
        /// The record's status on its own line at column 0, printed after every write of mold.json (never after a read): the
        /// panel ends a long tool run's sticky follow-up settings when a write reports done (Track C, ui/src/lib/toolRun.ts).
        /// </summary>
        internal static string StatusLine(MoldRecord record) => "\nrecord_status: " + ((string)record?.Json?["status"] ?? "planning");

        private static ToolResult Missing(NotImplementedException ex) =>
            ToolResult.Error("The mold calculator library isn't part of this build yet (" + ex.Message + ").");

        // ---------- mold_calc ----------

        public sealed class MoldCalcInput
        {
            [ToolParam("lookup | process | press | layout | size | split | check", Required = true)]
            public string Action { get; set; }

            [ToolParam("Design in SI with the record's keys (inputs, materials, process, part, layout, structural.inputs). Omit to use the mold record.")]
            public JObject Design { get; set; }

            [ToolParam("lookup: {part:id} | {mold:id, temp_c} | {fastener:\"1/2-13\"|\"M10\"} | {stock:id} | {family:name}. split: {plate:\"base\"|\"lid\" (default base), option:i (1-based: writes that option to structural.split.<plate>), box_mm:[L,W,T], cavities:[...], subplate_grade:id, depth_mm}.")]
            public JObject Query { get; set; }

            [ToolParam("This call's changes to sizing defaults or support, e.g. {delta_allow_mm:0.02, support:\"rails\", rail_span_mm:80}. Support geometry (mm from the plate corner; kept in structural.inputs): rails_v_mm [v1,v2], rails_u_mm, opening_mm [u1,v1,u2,v2], pillars_mm {u:[..], v:[..]}, span_mm.")]
            public JObject Overrides { get; set; }

            [ToolParam("Write the results to the record (default true, except lookup, split without query.option, and calls with design - a what-if).")]
            public bool? Record { get; set; }
        }

        [AgentTool("mold_calc",
            "For the design-compression-mold tool: the sizing calculator (no model changes). lookup a material/grade/fastener/stock row; " +
            "process (shrink, charge, cure), press (force, gauge), layout, size (walls, plates, bolts, fits...), split (options for " +
            "over-limit pieces) or check (as built). Uses the mold record unless design is given; writes results back. Lowest margins first.")]
        public ToolResult Calc(ToolContext ctx, MoldCalcInput input)
        {
            var action = (input.Action ?? "").Trim().ToLowerInvariant();
            var known = new[] { "lookup", "process", "press", "layout", "size", "split", "check" };
            if (!known.Contains(action)) return ToolResult.Error("action must be one of: " + string.Join(", ", known));
            try
            {
                var data = LoadData();
                MoldRecord record = null;
                var design = input.Design;
                if (ctx.Model != null)
                {
                    try
                    {
                        record = RecordOf(ctx.Model);
                    }
                    catch (Exception ex) when (!(ex is NotImplementedException))
                    {
                        Log.Warn("mold_calc: the record couldn't be read: " + ex.Message);
                    }
                }
                if (design == null && action != "lookup")
                {
                    if (record == null) return ToolResult.Error("No mold record for this document yet: run mold_record init (needs a saved part), or pass design.");
                    design = record.Json;
                }
                var result = MoldCalc.Run(action, design ?? new JObject(), input.Query ?? new JObject(), input.Overrides ?? new JObject(), data);
                var sb = new StringBuilder(result.Text ?? "");
                // the calculator's text already carries its JSON block (MoldCalc.Done); only lookup's doesn't
                if (result.Json != null && !(result.Text ?? "").Contains("```json")) sb.Append("\n```json\n").Append(Json(result.Json, 4000)).Append("\n```");
                // A design passed in is a what-if: it doesn't overwrite the as-designed record unless asked. split writes only
                // the option chosen with query.option.
                var write = input.Record ?? (input.Design == null && action != "lookup" && (action != "split" || input.Query?["option"] != null));
                if (write && record != null && (result.RecordPatch != null || (result.Checks?.Count ?? 0) > 0))
                {
                    var patch = (JObject)(result.RecordPatch?.DeepClone() ?? new JObject());
                    if (result.Checks != null && result.Checks.Count > 0)
                    {
                        if (!(patch["checks"] is JArray checks)) patch["checks"] = checks = new JArray();
                        foreach (var c in result.Checks) checks.Add(c);
                    }
                    var status = record.Merge(patch);
                    record.Save();
                    sb.Append("\nRecord: ").Append(status).Append(StatusLine(record));
                }
                else if (write && record == null && input.Design == null)
                {
                    sb.Append("\n(Not recorded: no mold record.)");
                }
                return ToolResult.Ok(sb.ToString());
            }
            catch (NotImplementedException ex)
            {
                return Missing(ex);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException)
            {
                // what the agent can fix (a missing input, an unknown grade, a stale layout): the calculator's message is the fix
                return ToolResult.Error(ex.Message);
            }
        }

        // ---------- mold_part ----------

        public sealed class MoldPartInput
        {
            [ToolParam("analyze (the only action; default).")]
            public string Action { get; set; }

            [ToolParam("auto (the Insert Part body, else PART-REF, else the largest solid), PART-REF, a body id or a SolidWorks body name; " +
                       "several, comma-separated (one per cavity): the full analysis of the first, and each one's projection (area, outline, box).")]
            public string Body { get; set; }

            [ToolParam("Pull direction [x, y, z] in model coordinates. Default +W (the Top Plane's normal).")]
            public double[] Pull { get; set; }

            [ToolParam("Pour side for air traps: base (default), lid or none.")]
            public string Fill { get; set; }

            [ToolParam("Mold tilt about U for air traps, degrees (default 0).")]
            public double? TiltDeg { get; set; }

            [ToolParam("Minimum draft, degrees (default: the page value, else the part material's, else 0.5).")]
            public double? MinDraftDeg { get; set; }

            [ToolParam("Sample spacing, mm (default 1.0).")]
            public double? SampleMm { get; set; }

            [ToolParam("Critical faces as selection refs [\"S7\", ...]: no parting line on them.")]
            public string[] Critical { get; set; }

            [ToolParam("Vents and risers [[x, y, z] mm, model] that drain air above them.")]
            public double[][] Vents { get; set; }

            [ToolParam("Specific gravity for the part's mass (default: the record's part material).")]
            public double? Sg { get; set; }

            [ToolParam("Draw the result on the model (red draft/undercut, cyan parting line, magenta traps, orange fragile). Default false.")]
            public bool? Show { get; set; }

            [ToolParam("Write the part section to the record (default true).")]
            public bool? Record { get; set; }
        }

        [AgentTool("mold_part",
            "For the design-compression-mold tool: read-only analysis of the molded part along the pull: volume, box, projected area " +
            "and outline, draft per face, undercuts (rays), planar parting-line candidates (scored, critical faces veto), air traps " +
            "for the pour side and tilt, fragile cores/walls (steel inserts). show draws it; results go to the record.")]
        public ToolResult Part(ToolContext ctx, MoldPartInput input)
        {
            var model = ctx.RequireModel();
            var action = (input.Action ?? "analyze").Trim().ToLowerInvariant();
            if (action != "analyze") return ToolResult.Error("action must be analyze.");
            SwGeo.RequirePart(model);
            MoldRecord record = null;
            try
            {
                record = RecordOf(model);
            }
            catch (NotImplementedException)
            {
                record = null;
            }
            // Several bodies (one per cavity, design 2.3.3 step 4): the first is analyzed in full, the others' projections are added.
            var names = (input.Body ?? "auto").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).DefaultIfEmpty("auto").ToList();
            var targets = new List<(IBody2 Body, string Id)>();
            foreach (var n in names)
            {
                var t = FindBody(model, n);
                if (t.Body == null) return ToolResult.Error($"No body '{n}' in this part.");
                targets.Add(t);
            }
            var (body, bodyId) = targets[0];
            var o = new PartAnalysisOptions
            {
                BodyId = bodyId,
                Pull = input.Pull,
                Fill = string.IsNullOrWhiteSpace(input.Fill) ? "base" : input.Fill.Trim().ToLowerInvariant(),
                TiltDeg = input.TiltDeg ?? 0,
                MinDraftDeg = input.MinDraftDeg ?? PageDraftDeg(record) ?? RecordNumber(record, "materials.part.used.draftMinDeg") ?? 0.5,
                SampleMm = Math.Max(0.1, input.SampleMm ?? 1.0),
                Sg = input.Sg ?? RecordNumber(record, "materials.part.used.sg") ?? RecordInput(record, "sg"),
                Vents = (input.Vents ?? new double[0][]).Where(v => v != null && v.Length >= 3).Select(v => new[] { v[0] / 1000, v[1] / 1000, v[2] / 1000 }).ToList(),
            };
            if (o.Fill != "base" && o.Fill != "lid" && o.Fill != "none") return ToolResult.Error("fill must be base, lid or none.");
            foreach (var id in input.Critical ?? new string[0])
            {
                if (SelectionTools.Resolve(model, ctx.Refs, id) is IFace2 f) o.Critical.Add((id.Trim().Trim('[', ']'), f));
                else return ToolResult.Error($"{id} is not a face.");
            }
            var report = PartAnalysis.Run(ctx.Sw, model, body, o, CancellationToken.None);
            var sb = new StringBuilder(report.Text);
            if (targets.Count > 1)
            {
                // Per cavity: projected area, outline perimeter and box of every body given (the first included).
                var cavities = new JArray();
                sb.Append("\nPer cavity:");
                foreach (var (b, id) in targets)
                {
                    var r = ReferenceEquals(b, body) ? report : PartAnalysis.Run(ctx.Sw, model, b, new PartAnalysisOptions
                    {
                        BodyId = id, Pull = o.Pull, Fill = "none", MinDraftDeg = o.MinDraftDeg, SampleMm = o.SampleMm,
                    }, CancellationToken.None);
                    var c = new JObject
                    {
                        ["id"] = id,
                        ["projectedArea_mm2"] = r.Json["projectedArea_mm2"],
                        ["perimeter_mm"] = r.Json["perimeter_mm"],
                        ["box_mm"] = r.Json["box_mm"],
                        ["boxFrame_mm"] = r.Json["boxFrame_mm"],
                        ["volume_mm3"] = r.Json["volume_mm3"],
                    };
                    cavities.Add(c);
                    sb.Append($"\n  {id}: projected area {c["projectedArea_mm2"]} mm2, outline {c["perimeter_mm"]} mm, box {string.Join(" x ", ((JArray)c["box_mm"]).Select(x => (double)x))} mm, volume {c["volume_mm3"]} mm3");
                }
                report.Json["cavities"] = cavities;
                if (report.RecordPatch["part"] is JObject part) part["cavities"] = cavities.DeepClone();
            }
            if (input.Show == true && report.Overlay.Count > 0)
            {
                sb.Append(Show(ctx, model, report.Overlay)
                    ? "\nShown on the model (red: draft/undercut, cyan: best parting line, magenta: air traps, orange: fragile steel)."
                    : "\n(Couldn't draw on the view: the document has no active view.)");
            }
            // the record's part section is the UNSCALED molded part (Design reads it so): a plate, an insert, a cavity copy or
            // anything analysed after MOLD Shrink would overwrite it - never recorded, whatever record says
            var notPart = input.Record != false ? NotTheMoldedPart(model, body) : null;
            if (notPart != null)
            {
                sb.Append("\n(Not recorded: " + notPart + ". The record keeps the molded part's own analysis; use record: false for checks like this.)");
            }
            else if (input.Record != false)
            {
                if (record != null)
                {
                    try
                    {
                        sb.Append("\nRecord: ").Append(record.Merge(report.RecordPatch));
                        record.Save();
                        sb.Append(StatusLine(record));
                    }
                    catch (NotImplementedException ex)
                    {
                        sb.Append("\n(Not recorded: " + ex.Message + ")");
                    }
                }
                else sb.Append("\n(Not recorded: no mold record yet - mold_record init.)");
            }
            sb.Append("\n```json\n").Append(Json(Compact(report.Json), 4000)).Append("\n```");
            return ToolResult.Ok(sb.ToString());
        }

        private static bool Show(ToolContext ctx, IModelDoc2 model, List<SceneOverlay.Item> items)
        {
            try
            {
                SceneOverlay.ShowItems(ctx.Sw, model, items, append: false);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                Log.Warn("Mold overlay: " + ex.Message);
                return false;
            }
        }

        /// <summary>The JSON for the agent without persistent references (they are long; the record keeps them).</summary>
        private static JToken Compact(JObject json)
        {
            var c = (JObject)json.DeepClone();
            foreach (var p in c.Descendants().OfType<JProperty>().Where(p => p.Name == "persist").ToList()) p.Remove();
            return c;
        }

        /// <summary>
        /// Why this body's analysis must not go into the record's part section (null when it may): it sits in a mold body folder
        /// other than the reference, or the part has been scaled for shrink already (MOLD Shrink).
        /// </summary>
        private static string NotTheMoldedPart(IModelDoc2 model, IBody2 body)
        {
            try
            {
                var frame = SwGeo.Frame(model);
                var name = SwGeo.SafeName(body);
                var mb = BodyReport.List(model, frame).FirstOrDefault(b => b.Name == name);
                var role = mb?.Role;
                if (role != null && role != "reference" && role != "unfiled")
                    return $"'{mb.Id}' is a mold body ({mb.Folder}), not the molded part";
                if (SwGeo.AllFeatures(model).Any(f => f.Name.StartsWith("MOLD Shrink", StringComparison.Ordinal)))
                    return "the part is already scaled for shrink (MOLD Shrink): this is the scaled part, the record holds the unscaled one";
                return null;
            }
            catch (Exception ex) when (!(ex is NotImplementedException))
            {
                Log.Warn("mold_part: checking the body's role failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>The body to analyze and its mold id (for the record's face locators).</summary>
        private static (IBody2 Body, string Id) FindBody(IModelDoc2 model, string which)
        {
            which = (which ?? "auto").Trim();
            var frame = SwGeo.Frame(model);
            var bodies = BodyReport.List(model, frame);
            (IBody2, string) Of(MoldBody m) => m == null ? ((IBody2)null, (string)null) : (m.Body, m.Id);
            if (which.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var f in SwGeo.AllFeatures(model).Where(f => f.Name.StartsWith("MOLD Part ", StringComparison.Ordinal)))
                {
                    var b = SwGeo.BodiesOf(f).FirstOrDefault();
                    if (b != null) return (b, bodies.FirstOrDefault(m => m.Name == SwGeo.SafeName(b))?.Id ?? SwGeo.SafeName(b));
                }
                return Of(bodies.FirstOrDefault(b => b.Id == "PART-REF") ?? bodies.OrderByDescending(b => b.Volume).FirstOrDefault());
            }
            return Of(bodies.FirstOrDefault(b => string.Equals(b.Id, which, StringComparison.OrdinalIgnoreCase)) ??
                      bodies.FirstOrDefault(b => string.Equals(b.Name, which, StringComparison.Ordinal)));
        }

        /// <summary>
        /// The page's minimum draft from the record's inputs, in degrees. Page angles reach macros in radians, so a bare
        /// value is radians unless it says "deg" (a draft in radians is far below 0.2).
        /// </summary>
        private static double? PageDraftDeg(MoldRecord record)
        {
            try
            {
                var t = record?.Json?.SelectToken("inputs.draft_min_deg");
                if (t == null) return null;
                // the page's payload form {si (rad), value, unit}: the calculator's own conversion
                if (t is JObject so && so["si"] != null) return Si.Num(t, "angle");
                var unit = (t as JObject)?["unit"]?.ToString();
                var v = t is JObject o ? o["value"] : t;
                if (v == null || (v.Type != JTokenType.Float && v.Type != JTokenType.Integer)) return null;
                var x = (double)v;
                if (unit != null && unit.StartsWith("deg", StringComparison.OrdinalIgnoreCase)) return x;
                if (unit != null && unit.StartsWith("rad", StringComparison.OrdinalIgnoreCase)) return x * 180 / Math.PI;
                return x < 0.2 ? x * 180 / Math.PI : x;
            }
            catch (NotImplementedException)
            {
                return null;
            }
        }

        /// <summary>A page input from the record's inputs in calculator units (Design.Quantities: {si}, {value, unit} or a number).</summary>
        private static double? RecordInput(MoldRecord record, string id)
        {
            var t = record?.Json?.SelectToken("inputs." + id);
            return t == null ? null : Si.Num(t, Design.Quantities.TryGetValue(id, out var q) ? q : null);
        }

        private static double? RecordNumber(MoldRecord record, params string[] paths)
        {
            if (record == null) return null;
            try
            {
                foreach (var p in paths)
                {
                    var t = record.Json?.SelectToken(p);
                    if (t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)) return (double)t;
                }
            }
            catch (Exception ex) when (!(ex is NotImplementedException))
            {
                Log.Warn("mold record value: " + ex.Message);
            }
            catch (NotImplementedException)
            {
                return null;
            }
            return null;
        }

        // ---------- mold_bodies ----------

        public sealed class MoldBodiesInput
        {
            [ToolParam("report | clearance | export", Required = true)]
            public string Action { get; set; }

            [ToolParam("clearance: pairs [[a, b], ...] of body ids (BASE-1), SolidWorks body names or feature names.")]
            public string[][] Pairs { get; set; }

            [ToolParam("clearance: required minimum, mm (pairs closer fail).")]
            public double? MinClearanceMm { get; set; }

            [ToolParam("export: step (default) or x_t.")]
            public string Format { get; set; }

            [ToolParam("export: STEP application protocol 214 (default) or 242.")]
            public int? StepAp { get; set; }

            [ToolParam("export: only these body ids (default: every mold body plus the part).")]
            public string[] Bodies { get; set; }

            [ToolParam("report: density by role {base: 2700, insert: 7850, ...} kg/m3 (default: the record's grades, else the SolidWorks material).")]
            public JObject Densities { get; set; }

            [ToolParam("report: limits {max_plate_mass_kg, max_lid_mass_kg, max_base_mass_kg, max_plate_size_mm:[a,b,c], blank_allowance_mm} (default: the record's inputs).")]
            public JObject Limits { get; set; }

            [ToolParam("Draw clearance results on the model. Default false.")]
            public bool? Show { get; set; }

            [ToolParam("Write bodies[] and checks to the record (default true).")]
            public bool? Record { get; set; }
        }

        [AgentTool("mold_bodies",
            "For the design-compression-mold tool: the mold's bodies. report: role (\"Mold - *\" folder), id, mass, box, material, the " +
            "weight/size limits, lid and base totals, interference. clearance: closest distance between pairs. export: one STEP per " +
            "body plus the part into the record folder (hidden documents; the tree is unchanged).")]
        public ToolResult Bodies(ToolContext ctx, MoldBodiesInput input)
        {
            var model = ctx.RequireModel();
            SwGeo.RequirePart(model);
            var action = (input.Action ?? "").Trim().ToLowerInvariant();
            MoldRecord record = null;
            try
            {
                record = RecordOf(model);
            }
            catch (NotImplementedException)
            {
                record = null;
            }
            var frame = SwGeo.Frame(model);
            var cavities = Cavities(record);
            var bodies = BodyReport.List(model, frame, cavities);
            switch (action)
            {
                case "report":
                {
                    var limits = ReadLimits(input.Limits, record);
                    var fits = ReadFits(record);
                    var densities = input.Densities;
                    MoldData data = null;
                    try
                    {
                        data = LoadData();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("mold_bodies: data not loaded: " + ex.Message);
                    }
                    (double?, string) Density(MoldBody b)
                    {
                        var d = densities?[b.Role]?.Type == JTokenType.Float || densities?[b.Role]?.Type == JTokenType.Integer ? (double?)densities[b.Role] : null;
                        if (d.HasValue) return (d, "input density");
                        var gradeId = GradeOf(record, b.Role);
                        var rho = GradeDensity(data, gradeId);
                        return rho.HasValue ? (rho, $"grade {gradeId}") : ((double?)null, null);
                    }
                    var (json, text, checks) = BodyReport.Report(model, ctx.Sw, frame, bodies, Density, limits, fits, interference: true);
                    var sb = new StringBuilder(text);
                    if (input.Record != false && record != null)
                    {
                        try
                        {
                            // the report is the whole truth about the bodies: bodies[] and this tool's earlier checks are replaced, so a
                            // body or an interference that is gone (a restore, a fix) does not linger
                            var patch = new JObject
                            {
                                [MoldRecord.ReplaceKey] = new JArray("bodies"), [MoldRecord.ReplaceChecksFromKey] = new JArray("mold_bodies"),
                                ["bodies"] = json["bodies"].DeepClone(),
                            };
                            // an empty array would clear every check in the record (agent and calculator ones too)
                            if (checks.Count > 0) patch["checks"] = new JArray(checks);
                            sb.Append("\nRecord: ").Append(record.Merge(patch));
                            record.Save();
                            sb.Append(StatusLine(record));
                        }
                        catch (NotImplementedException ex)
                        {
                            sb.Append("\n(Not recorded: " + ex.Message + ")");
                        }
                    }
                    sb.Append("\n```json\n").Append(Json(json, 4000)).Append("\n```");
                    return ToolResult.Ok(sb.ToString());
                }
                case "clearance":
                {
                    if (input.Pairs == null || input.Pairs.Length == 0) return ToolResult.Error("clearance needs pairs [[a, b], ...].");
                    var sb = new StringBuilder();
                    var items = new List<SceneOverlay.Item>();
                    var checks = new JArray();
                    var budget = 400;
                    foreach (var pair in input.Pairs)
                    {
                        if (pair == null || pair.Length != 2)
                        {
                            sb.AppendLine("skipped a pair that isn't two names");
                            continue;
                        }
                        var a = FacesOf(model, bodies, pair[0]);
                        var b = FacesOf(model, bodies, pair[1]);
                        if (a == null || b == null)
                        {
                            sb.AppendLine($"{pair[0]} / {pair[1]}: {(a == null ? pair[0] : pair[1])} not found (body id, body name or feature name)");
                            continue;
                        }
                        var (d, pa, pb, used) = BodyReport.Closest(model, a, b, Math.Max(1, budget));
                        budget -= used;
                        if (double.IsNaN(d))
                        {
                            sb.AppendLine($"{pair[0]} / {pair[1]}: no distance measured");
                            continue;
                        }
                        var mm = d * 1000;
                        var ok = !input.MinClearanceMm.HasValue || mm >= input.MinClearanceMm.Value - 1e-6;
                        sb.AppendLine($"{pair[0]} / {pair[1]}: {mm:0.###} mm" + (input.MinClearanceMm.HasValue ? (ok ? " (pass)" : $" - FAIL, needs {input.MinClearanceMm:0.###} mm") : "") +
                                      (pa != null && pb != null ? $" between ({string.Join(", ", pa.Take(3).Select(x => (x * 1000).ToString("0.##", CultureInfo.InvariantCulture)))}) and ({string.Join(", ", pb.Take(3).Select(x => (x * 1000).ToString("0.##", CultureInfo.InvariantCulture)))}) mm" : ""));
                        if (input.MinClearanceMm.HasValue)
                            checks.Add(new JObject
                            {
                                ["id"] = $"CLR-{pair[0]}-{pair[1]}",
                                ["source"] = "mold_bodies",
                                ["type"] = "clearance",
                                ["body"] = pair[0],
                                // A lower limit: demand = what is there (measured), capacity = the required minimum,
                                // margin = demand / capacity - 1 (the record schema's rule for lower limits).
                                ["limit"] = "lower",
                                ["demand"] = Math.Round(mm, 4),
                                ["capacity"] = Math.Round(input.MinClearanceMm.Value, 4),
                                ["unit"] = "mm",
                                ["margin"] = Math.Round(mm / Math.Max(1e-9, input.MinClearanceMm.Value) - 1, 4),
                                ["status"] = ok ? "pass" : "fail",
                                ["method"] = "IModelDoc2.ClosestDistance",
                            });
                        if (pa != null && pb != null && pa.Length >= 3 && pb.Length >= 3)
                            items.Add(new SceneOverlay.Item { Type = "dimension", Points = { new[] { pa[0], pa[1], pa[2] }, new[] { pb[0], pb[1], pb[2] } }, Text = $"{mm:0.##} mm", Color = OverlayTools.ParseColor(ok ? "green" : "red") });
                        if (budget <= 0)
                        {
                            sb.AppendLine("(stopped: 400 face pairs measured in this call)");
                            break;
                        }
                    }
                    if (input.Show == true && items.Count > 0 && !Show(ctx, model, items)) sb.AppendLine("(Couldn't draw on the view: no active view.)");
                    if (input.Record != false && record != null && checks.Count > 0)
                    {
                        try
                        {
                            sb.Append("Record: ").Append(record.Merge(new JObject { ["checks"] = checks }));
                            record.Save();
                            sb.Append(StatusLine(record));
                        }
                        catch (NotImplementedException ex)
                        {
                            sb.Append("(Not recorded: " + ex.Message + ")");
                        }
                    }
                    return ToolResult.Ok(sb.ToString().TrimEnd());
                }
                case "export":
                {
                    var folder = SwGeo.RecordFolder(model);
                    if (folder == null) return ToolResult.Error("Save the mold part first: exports go into its record folder.");
                    var format = string.IsNullOrWhiteSpace(input.Format) ? "step" : input.Format.Trim().ToLowerInvariant();
                    if (format != "step" && format != "x_t") return ToolResult.Error("format must be step or x_t.");
                    var wanted = input.Bodies?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
                    var mold = bodies.Where(b => BodyReport.IsMachined(b.Role) && (wanted == null || wanted.Count == 0 || wanted.Contains(b.Id, StringComparer.OrdinalIgnoreCase))).ToList();
                    var items = mold.Select(b => (b.Id, b.Body, b.SwMaterial)).ToList();
                    var result = BodyExport.Export(ctx.Sw, model, items, Path.Combine(folder, "exports", "bodies"), format, input.StepAp ?? 214, CancellationToken.None);
                    var sb = new StringBuilder($"Exported {result.Written.Count} of {items.Count} bodies to {Path.Combine(folder, "exports", "bodies")}.");
                    var reference = bodies.FirstOrDefault(b => b.Id == "PART-REF");
                    var partFiles = new JObject();
                    if (wanted == null || wanted.Count == 0)
                    {
                        if (reference != null)
                        {
                            var r = BodyExport.Export(ctx.Sw, model, new[] { ("shrunk-part", reference.Body, reference.SwMaterial) }, Path.Combine(folder, "exports", "part"), format, input.StepAp ?? 214, CancellationToken.None);
                            result.Problems.AddRange(r.Problems);
                            if (r.Written.Count > 0)
                            {
                                sb.Append(" Shrunk part (PART-REF): exports/part/shrunk-part" + (format == "x_t" ? ".x_t" : ".step") + ".");
                                partFiles["shrunk"] = "exports/part/shrunk-part" + (format == "x_t" ? ".x_t" : ".step");
                            }
                        }
                        else sb.Append(" No PART-REF body (folder \"Mold - Reference\"): shrunk-part not exported.");
                        string source = null;
                        try
                        {
                            source = (string)record?.Json?.SelectToken("files.moldedPart.path");
                        }
                        catch (NotImplementedException)
                        {
                            source = null;
                        }
                        if (!string.IsNullOrWhiteSpace(source))
                        {
                            try
                            {
                                BodyExport.ExportSourcePart(ctx.Sw, source, Path.Combine(folder, "exports", "part", "molded-part.step"));
                                sb.Append(" Molded part as received: exports/part/molded-part.step.");
                                partFiles["molded"] = "exports/part/molded-part.step";
                            }
                            catch (Exception ex)
                            {
                                result.Problems.Add("molded-part: " + ex.Message);
                            }
                        }
                    }
                    if (result.Problems.Count > 0) sb.Append("\nProblems: " + string.Join("; ", result.Problems));
                    if (input.Record != false && record != null)
                    {
                        try
                        {
                            var patch = new JObject
                            {
                                ["bodies"] = new JArray(result.Written.Select(w => new JObject { ["id"] = w.Id, ["step"] = "exports/bodies/" + Path.GetFileName(w.Path) })),
                            };
                            if (partFiles.Count > 0) patch["files"] = new JObject { ["exports"] = new JObject { ["part"] = partFiles } };
                            sb.Append("\nRecord: ").Append(record.Merge(patch));
                            record.Save();
                            sb.Append(StatusLine(record));
                        }
                        catch (NotImplementedException ex)
                        {
                            sb.Append("\n(Not recorded: " + ex.Message + ")");
                        }
                    }
                    return result.Written.Count == 0 && items.Count > 0 ? ToolResult.Error(sb.ToString()) : ToolResult.Ok(sb.ToString());
                }
                default:
                    return ToolResult.Error("action must be report, clearance or export.");
            }
        }

        /// <summary>Faces of a body (by id or SolidWorks name) or of a feature (by name).</summary>
        private static List<IFace2> FacesOf(IModelDoc2 model, List<MoldBody> bodies, string name)
        {
            var b = bodies.FirstOrDefault(x => string.Equals(x.Id, name, StringComparison.OrdinalIgnoreCase)) ?? bodies.FirstOrDefault(x => x.Name == name);
            if (b != null) return ((b.Body.GetFaces() as object[]) ?? new object[0]).OfType<IFace2>().ToList();
            var f = SwGeo.FeatureByName(model, name);
            return f == null ? null : ((f.GetFaces() as object[]) ?? new object[0]).OfType<IFace2>().ToList();
        }

        /// <summary>Cavity origins from the record's layout (for insert ids INS-C1-1).</summary>
        private static List<(string Id, double U, double V)> Cavities(MoldRecord record)
        {
            var list = new List<(string, double, double)>();
            try
            {
                if (record?.Json?.SelectToken("layout.cavities") is JArray arr)
                    foreach (var c in arr.OfType<JObject>())
                        if (c["origin"] is JArray o && o.Count >= 2) list.Add(((string)c["id"] ?? $"C{list.Count + 1}", (double)o[0] / 1000, (double)o[1] / 1000));
            }
            catch (Exception ex) when (!(ex is NotImplementedException))
            {
                Log.Warn("mold record layout: " + ex.Message);
            }
            catch (NotImplementedException)
            {
                // No record library: no cavity names.
            }
            return list;
        }

        private static BodyReport.Limits ReadLimits(JObject input, MoldRecord record)
        {
            double? Num(params string[] paths)
            {
                foreach (var p in paths)
                {
                    var t = input?.SelectToken(p);
                    if (t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)) return (double)t;
                }
                foreach (var p in paths)
                    if (RecordInput(record, p) is double v) return v;
                return null;
            }
            double[] Size()
            {
                var t = input?["max_plate_size_mm"] as JArray;
                if (t == null)
                {
                    try
                    {
                        var r = record?.Json?.SelectToken("inputs.max_plate_size");
                        // the page's payload form {si: [m, m, m], value: "600 x 400 x 150 mm"}: the calculator's reading (mm)
                        if (r is JObject ro && ro["si"] != null) return Si.Lengths(r) is double[] mm && mm.Length == 3 ? mm : null;
                        t = (r?["value"] ?? r) as JArray;
                    }
                    catch (NotImplementedException)
                    {
                        t = null;
                    }
                }
                if (t == null || t.Count != 3) return null;
                var v = t.Select(x => (double)x).ToArray();
                // Page lengths are meters (a plate is never under 5 mm): convert to mm.
                return v.All(x => x < 5) ? v.Select(x => x * 1000).ToArray() : v;
            }
            return new BodyReport.Limits
            {
                PlateMassKg = Num("max_plate_mass_kg", "max_plate_mass"),
                LidMassKg = Num("max_lid_mass_kg", "max_lid_mass"),
                BaseMassKg = Num("max_base_mass_kg", "max_base_mass"),
                PlateSizeMm = Size(),
                BlankAllowanceMm = Num("blank_allowance_mm") ?? 3,
            };
        }

        /// <summary>Recorded fits (hardware.fits[].bodies pairs): interference there is intended.</summary>
        private static List<(string A, string B)> ReadFits(MoldRecord record)
        {
            var list = new List<(string, string)>();
            try
            {
                if (record?.Json?.SelectToken("hardware.fits") is JArray fits)
                    foreach (var f in fits.OfType<JObject>())
                        if (f["bodies"] is JArray b && b.Count == 2) list.Add(((string)b[0], (string)b[1]));
            }
            catch (NotImplementedException)
            {
                // No record library.
            }
            return list;
        }

        /// <summary>
        /// The grade id for a role from the record: what mold_calc size wrote (structural.materials.plates / inserts; the stop
        /// pads' grade from hardware.stops), else materials.*.id, else the page's grade inputs (mold_grade, insert_grade).
        /// </summary>
        internal static string GradeOf(MoldRecord record, string role) => GradeOf(record?.Json, role);

        internal static string GradeOf(JObject json, string role)
        {
            try
            {
                if (json == null) return null;
                string S(string path)
                {
                    var t = json.SelectToken(path);
                    if (t is JObject o) t = o["value"] ?? o["display"];
                    return t != null && t.Type == JTokenType.String && ((string)t).Trim().Length > 0 ? (string)t : null;
                }
                if (role == "stop") return S("hardware.stops[0].grade") ?? S("structural.materials.inserts.id") ?? S("materials.inserts.id") ?? S("inputs.insert_grade") ?? S("structural.materials.plates.id") ?? S("inputs.mold_grade");
                if (role == "insert") return S("structural.materials.inserts.id") ?? S("materials.inserts.id") ?? S("inputs.insert_grade") ?? S("structural.materials.plates.id") ?? S("inputs.mold_grade");
                return S("structural.materials.plates.id") ?? S("materials.plates.id") ?? S("inputs.mold_grade");
            }
            catch (NotImplementedException)
            {
                return null;
            }
        }

        /// <summary>A grade's density in kg/m^3 from the mold-materials data (the field name is looked up leniently).</summary>
        internal static double? GradeDensity(MoldData data, string gradeId)
        {
            if (data == null || string.IsNullOrEmpty(gradeId)) return null;
            try
            {
                var row = data.MoldGrade(gradeId);
                if (row == null) return null;
                foreach (var key in new[] { "density_kg_m3", "densityKgM3", "rho_kg_m3", "density.kg_m3", "density.typ", "density" })
                {
                    var t = row.SelectToken(key);
                    if (t == null) continue;
                    if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                    {
                        var v = (double)t;
                        return v < 100 ? v * 1000 : v; // g/cm3 or kg/m3
                    }
                }
            }
            catch (Exception ex) when (!(ex is NotImplementedException))
            {
                Log.Warn("Grade density: " + ex.Message);
            }
            catch (NotImplementedException)
            {
                return null;
            }
            return null;
        }

        // ---------- mold_record ----------

        public sealed class MoldRecordInput
        {
            [ToolParam("init | read | merge | setup_sheet | machining_sheet | save_default | path", Required = true)]
            public string Action { get; set; }

            [ToolParam("read: summary (default, <= 6000 chars) or a top-level key (that section as JSON).")]
            public string Section { get; set; }

            [ToolParam("merge: the patch - objects merge key by key, arrays of objects with an id merge by id, other arrays are replaced.")]
            public JObject Data { get; set; }

            [ToolParam("save_default: materials | mold-materials | shop-standards | sizing-defaults.")]
            public string Table { get; set; }

            [ToolParam("save_default: the row id (tables with rows).")]
            public string Id { get; set; }

            [ToolParam("save_default: {field: value} with dotted paths that exist in the shipped table, e.g. {\"shrinkPct.typ\": 2.1}.")]
            public JObject Values { get; set; }

            [ToolParam("init: true = Start over - the existing mold.json is kept as mold.json.bak-<yyyyMMdd-HHmmss> and a new record (new id) starts. Without it, init on an existing record continues it.")]
            public bool? StartOver { get; set; }
        }

        [AgentTool("mold_record",
            "For the design-compression-mold tool: the record mold.json in <mold>.mold\\. init (saved part; start_over keeps a " +
            "backup), read (summary or a section), merge (deep, by id; $replace / $replaceChecks), setup_sheet, machining_sheet (list " +
            "unresolved placeholders), save_default (the shop's own table value), path. Writes end with record_status: <status>. " +
            "Re-read the summary at every phase start.")]
        public ToolResult Record(ToolContext ctx, MoldRecordInput input)
        {
            var action = (input.Action ?? "").Trim().ToLowerInvariant();
            try
            {
                if (action == "save_default") return SaveDefault(input);
                var model = ctx.RequireModel();
                SwGeo.RequirePart(model);
                var path = model.GetPathName();
                if (string.IsNullOrEmpty(path)) return ToolResult.Error("Save the mold part first: the record lives next to it (<name>.mold\\mold.json).");
                var folder = SwGeo.RecordFolder(model);
                switch (action)
                {
                    case "path":
                        return ToolResult.Ok(folder + (File.Exists(Path.Combine(folder, "mold.json")) ? "" : " (no record yet - mold_record init)"));
                    case "init":
                    {
                        var file = Path.Combine(folder, "mold.json");
                        var exists = File.Exists(file);
                        string backup = null;
                        if (exists && input.StartOver == true)
                        {
                            // Start over: never delete a record - keep it next to the new one
                            backup = BackupPath(file);
                            File.Move(file, backup);
                            exists = false;
                        }
                        MoldRecord rec;
                        // the page values of this tool's run on THIS document (never another mold's run or a stale slot)
                        var page = ToolRunner.InputsFor(ToolName, model);
                        var pageNote = "";
                        if (exists)
                        {
                            rec = MoldRecord.Open(folder);
                            if (page != null)
                            {
                                var diff = InputDifferences(InputsJson(page), rec.Json["inputs"] as JObject);
                                if (diff.Count > 0)
                                    pageNote = "\nPAGE CHANGED - this run's page differs from the record's inputs (the calculator reads only the record):\n" +
                                               string.Join("\n", diff.Select(x => "  " + x)) +
                                               "\nAsk the user whether to use the new values; they may invalidate finished stages. To use them, merge them into inputs (as on the page), then re-run the stages they affect.\n";
                            }
                        }
                        else
                        {
                            foreach (var sub in new[] { "part", Path.Combine("exports", "bodies"), Path.Combine("exports", "part"), "checks", "views" })
                                Directory.CreateDirectory(Path.Combine(folder, sub));
                            if (page == null)
                                pageNote = "\nNO PAGE VALUES - this session has no page run for this document (SolidWorks restarted, or the tool ran on another document): inputs is empty. Merge the values from <tool_inputs> into inputs as \"<number> <unit>\" strings before mold_calc.\n";
                            rec = MoldRecord.Init(folder, path, InputsJson(page), ToolVersion(), typeof(MoldTools).Assembly.GetName().Version?.ToString());
                            // the calculator reads the display system from here (screws, stock and rounding follow it): MMGS, CGS
                            // and MKS are metric, IPS (and custom inch or pound units) imperial
                            rec.Json["units"]["display"] = Units.IsImperial(model) ? "imperial" : "metric";
                            // the tool profile the page values came from, and what the user changed after loading it
                            if (page?.Profile != null) rec.Json["profile"] = page.Profile.DeepClone();
                            rec.Save();
                        }
                        // The part finds its record by these even after a rename or move; checkpoints are keyed by the id (B2).
                        SwGeo.SetProperty(model, SwGeo.RecordProperty, Path.GetFileName(folder.TrimEnd('\\', '/')));
                        SwGeo.SetProperty(model, SwGeo.IdProperty, rec.Id);
                        RebuildTools.RegisterMoldDesign(path, rec.Id);
                        return ToolResult.Ok((exists ? "The record already exists - continuing it" : "Record created") + $": {file} (id {rec.Id}, units {(string)rec.Json.SelectToken("units.display")}). " +
                                             (backup != null ? $"The earlier record is kept as {backup}. " : "") +
                                             "Checkpoints for this mold restore over this file (checkpoint save/restore). Save the part to keep the record link.\n" +
                                             pageNote +
                                             (exists ? rec.Summary(6000) : "") +
                                             // a new record was written; continuing an existing one writes nothing (and must not end a run on a done record)
                                             (exists ? "" : StatusLine(rec)));
                    }
                }
                if (!File.Exists(Path.Combine(folder, "mold.json"))) return ToolResult.Error("No mold record for this part yet: run mold_record init.");
                var record = MoldRecord.Open(folder);
                switch (action)
                {
                    case "read":
                    {
                        var section = (input.Section ?? "summary").Trim();
                        if (section.Length == 0 || section.Equals("summary", StringComparison.OrdinalIgnoreCase)) return ToolResult.Ok(record.Summary(6000));
                        var token = record.Section(section);
                        return token == null ? ToolResult.Error($"The record has no section '{section}'.") : ToolResult.Ok(Json(token, 20000));
                    }
                    case "merge":
                    {
                        if (input.Data == null || !input.Data.HasValues) return ToolResult.Error("merge needs data (a JSON object).");
                        var status = record.Merge(input.Data);
                        record.Save();
                        // A finished mold is no longer restorable in place (critique B2: the registration ends with the design).
                        if (string.Equals((string)record.Json?["status"], "done", StringComparison.OrdinalIgnoreCase)) RebuildTools.UnregisterMoldDesign(path);
                        return ToolResult.Ok(status + StatusLine(record));
                    }
                    case "setup_sheet":
                    {
                        var written = SetupSheet.Write(record, Path.Combine(ToolDir, "data", "setup-sheet.html"), LoadData(), out var unresolved);
                        return ToolResult.Ok("Setup sheet: " + written + Unresolved(unresolved) + StatusLine(record));
                    }
                    case "machining_sheet":
                    {
                        var written = MachiningSheet.Write(record, Path.Combine(ToolDir, "data", "machining-sheet.html"), LoadData(), out var unresolved);
                        return ToolResult.Ok("Machining sheet: " + written + " (and hardware-bom.csv)" + Unresolved(unresolved) + StatusLine(record));
                    }
                    default:
                        return ToolResult.Error("action must be init, read, merge, setup_sheet, machining_sheet, save_default or path.");
                }
            }
            catch (NotImplementedException ex)
            {
                return Missing(ex);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException)
            {
                return ToolResult.Error(ex.Message);
            }
        }

        /// <summary>mold.json.bak-yyyyMMdd-HHmmss next to the record (a numbered suffix if that exists already).</summary>
        internal static string BackupPath(string file)
        {
            var b = file + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var candidate = b;
            for (var k = 2; File.Exists(candidate); k++) candidate = b + "-" + k;
            return candidate;
        }

        /// <summary>The placeholders a sheet could not fill ("-" in the sheet), for the agent to fix or list as open items.</summary>
        internal static string Unresolved(IList<string> unresolved)
        {
            var list = (unresolved ?? new List<string>()).Distinct().ToList();
            return list.Count == 0 ? "\nUnresolved placeholders: none." : $"\nUnresolved placeholders ({list.Count}, printed as \"-\"): " + string.Join(", ", list);
        }

        /// <summary>The page inputs of the latest run (ToolRunner.LastInputs) as JSON: values, selections (label + ref), files.</summary>
        /// <summary>The inputs a page run would write that differ from the record's: "id: page X, record Y".</summary>
        internal static List<string> InputDifferences(JObject page, JObject record)
        {
            var list = new List<string>();
            if (page == null) return list;
            string Show(JToken t)
            {
                if (t == null || t.Type == JTokenType.Null) return "(not set)";
                if (t is JObject o && (o["value"] ?? o["display"]) is JToken v) return v.Type == JTokenType.String ? (string)v : v.ToString(Formatting.None);
                return t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
            }
            foreach (var p in page.Properties())
            {
                var r = record?[p.Name];
                if (JToken.DeepEquals(Comparable(p.Value), Comparable(r))) continue;
                if (Show(p.Value) == Show(r)) continue;
                list.Add($"{p.Name}: page {Show(p.Value)}, record {Show(r)}");
            }
            return list;
        }

        /// <summary>A value without its display text, for comparing a page value with what the record holds.</summary>
        private static JToken Comparable(JToken t)
        {
            if (t is JObject o && o["si"] != null) return o["si"];
            return t;
        }

        internal static JObject InputsJson(ToolInputs page)
        {
            var o = new JObject();
            if (page == null) return o;
            foreach (var kv in page.Values)
            {
                var meta = page.Meta(kv.Key);
                if (kv.Value == null) o[kv.Key] = JValue.CreateNull();
                else if (meta?.Si != null && meta.Si.Type != JTokenType.Null && (kv.Value is double || kv.Value is double[] || kv.Value is int || kv.Value is long))
                {
                    // numbers and sizes as the page's payload has them: the SI value (Pa, m, N, s, Pa·s, rad; degC, kg, % as
                    // they are) with the text the user saw - the calculator converts "si" by the input's quantity, so a bare
                    // SI number is never read as MPa or mm (Design.Quantities)
                    var j = new JObject { ["si"] = meta.Si.DeepClone() };
                    if (meta.Display != null) j["value"] = meta.Display;
                    if (meta.Unit != null) j["unit"] = meta.Unit;
                    if (meta.Table != null) j["table"] = meta.Table;
                    if (meta.Edited) j["edited"] = true;
                    o[kv.Key] = j;
                }
                else o[kv.Key] = JToken.FromObject(kv.Value);
            }
            foreach (var id in page.SelectionIds)
                o[id] = new JArray(page.SelectedItems(id).Select(s => new JObject { ["type"] = s.Type, ["label"] = s.Label, ["detail"] = s.Detail, ["persist"] = s.Ref }));
            foreach (var id in page.FileIds)
                o[id] = new JArray(page.Files(id));
            return o;
        }

        private static string ToolVersion()
        {
            try
            {
                var md = Path.Combine(ToolDir, "TOOL.md");
                if (File.Exists(md))
                {
                    var m = Regex.Match(File.ReadAllText(md), @"^version:\s*(\S+)\s*$", RegexOptions.Multiline);
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Tool version: " + ex.Message);
            }
            return "1.0.0";
        }

        // ---------- save_default ----------

        private static readonly Dictionary<string, string> Tables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["materials"] = "materials.json",
            ["mold-materials"] = "mold-materials.json",
            ["shop-standards"] = "shop-standards.json",
            ["sizing-defaults"] = "sizing-defaults.json",
        };

        private static ToolResult SaveDefault(MoldRecordInput input)
        {
            var table = (input.Table ?? "").Trim().Replace(".json", "");
            if (!Tables.TryGetValue(table, out var file)) return ToolResult.Error("table must be materials, mold-materials, shop-standards or sizing-defaults.");
            if (input.Values == null || !input.Values.HasValues) return ToolResult.Error("save_default needs values {field: value}.");
            var shippedPath = Path.Combine(ToolDir, "data", file);
            if (!File.Exists(shippedPath)) return ToolResult.Error($"The shipped table {file} isn't installed.");
            var shipped = JObject.Parse(File.ReadAllText(shippedPath));
            var overridePath = Path.Combine(Directory.CreateDirectory(OverrideDir).FullName, file);
            var current = File.Exists(overridePath) ? JObject.Parse(File.ReadAllText(overridePath)) : new JObject();
            var error = ApplyOverride(shipped, current, input.Id, input.Values);
            if (error != null) return ToolResult.Error(error);
            File.WriteAllText(overridePath, current.ToString(Formatting.Indented));
            return ToolResult.Ok($"Saved as the shop's default in {overridePath}: " + string.Join(", ", input.Values.Properties().Select(p => $"{p.Name} = {p.Value.ToString(Formatting.None)}")) +
                                 (string.IsNullOrWhiteSpace(input.Id) ? "" : $" (row {input.Id})") + ". It wins over the shipped table from the next run on and survives reinstalls.");
        }

        /// <summary>
        /// Write values into the override object (critique/design 1.5.5 format): for a table with rows, the whole row (the
        /// shipped one plus earlier overrides plus these values) goes into the override's array of the same name - a row
        /// with the same id replaces the shipped row; otherwise the values merge key by key. Every dotted path must exist
        /// in the shipped table. Returns an error message, or null.
        /// </summary>
        internal static string ApplyOverride(JObject shipped, JObject overrides, string id, JObject values)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                var arrayProp = shipped.Properties().FirstOrDefault(p => p.Value is JArray a && a.OfType<JObject>().Any(r => r["id"] != null));
                if (arrayProp == null) return "This table has no rows with ids; leave id out.";
                var shippedRow = ((JArray)arrayProp.Value).OfType<JObject>().FirstOrDefault(r => string.Equals((string)r["id"], id.Trim(), StringComparison.OrdinalIgnoreCase));
                if (shippedRow == null) return $"No row '{id}' in the shipped {arrayProp.Name}.";
                var bad = values.Properties().Where(p => shippedRow.SelectToken(p.Name) == null).Select(p => p.Name).ToList();
                if (bad.Count > 0) return $"Unknown field(s) for row {id}: {string.Join(", ", bad)}.";
                if (!(overrides[arrayProp.Name] is JArray arr))
                {
                    // Assign only a new array: re-assigning one that already has a parent stores a copy.
                    arr = new JArray();
                    overrides[arrayProp.Name] = arr;
                }
                var row = arr.OfType<JObject>().FirstOrDefault(r => string.Equals((string)r["id"], (string)shippedRow["id"], StringComparison.OrdinalIgnoreCase));
                if (row == null)
                {
                    row = (JObject)shippedRow.DeepClone();
                    arr.Add(row);
                }
                foreach (var p in values.Properties()) SetPath(row, p.Name, p.Value.DeepClone());
                return null;
            }
            var unknown = values.Properties().Where(p => shipped.SelectToken(p.Name) == null).Select(p => p.Name).ToList();
            if (unknown.Count > 0) return $"Unknown field(s): {string.Join(", ", unknown)}.";
            foreach (var p in values.Properties()) SetPath(overrides, p.Name, p.Value.DeepClone());
            return null;
        }

        private static void SetPath(JObject root, string path, JToken value)
        {
            var parts = path.Split('.');
            var cur = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!(cur[parts[i]] is JObject next))
                {
                    next = new JObject();
                    cur[parts[i]] = next;
                }
                cur = next;
            }
            cur[parts[parts.Length - 1]] = value;
        }
    }
}

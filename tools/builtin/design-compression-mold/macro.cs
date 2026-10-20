// Design compression mold - gathering macro. Read-only: it changes nothing in the model.
// It prints the facts Phase 0-1 of TOOL.md need (document, units, frame, bodies, inserted parts, the mold record, the
// page's files), and the mold_part analysis when the part is already in the file, so the agent starts from real data.
// The agent receives this output as the macro output (cut at 6,000 characters), so it stays compact.

const int Budget = 5600;
var o = new StringBuilder();
void Line(string s) => o.AppendLine(s);
string Mm(double m) => (m * 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
string Num(double v, string f = "0.###") => v.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
string FirstLine(string s) => string.IsNullOrEmpty(s) ? "" : s.Split('\n')[0].Trim();

var part = Model as IPartDoc;
if (Model == null || part == null)
    Fail("Open a part first: a new blank part (it becomes the mold) or your mold file. Attach the molded part or its drawing on the page.");

Line("MOLD INTAKE (gathering macro, read-only)");

// --- Document and units ---
var path = Model.GetPathName() ?? "";
var saved = path.Length > 0;
var ext = Model.Extension;
var unitSystem = ext.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
var linear = ext.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
var massUnit = ext.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsMassPropMass, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
var imperial = unitSystem == (int)swUnitSystem_e.swUnitSystem_IPS ||
               (unitSystem == (int)swUnitSystem_e.swUnitSystem_Custom &&
                (linear == (int)swLengthUnit_e.swINCHES || linear == (int)swLengthUnit_e.swFEET || linear == (int)swLengthUnit_e.swFEETINCHES ||
                 linear == (int)swLengthUnit_e.swMIL || linear == (int)swLengthUnit_e.swUIN || massUnit == (int)swUnitsMassPropMass_e.swUnitsMassPropMass_Pounds));
Line($"Document: {Model.GetTitle()} | {(saved ? "saved: " + path : "NOT SAVED yet")}");
Line($"Units: {((swUnitSystem_e)unitSystem).ToString().Replace("swUnitSystem_", "")}, length {((swLengthUnit_e)linear).ToString().Replace("sw", "").ToLowerInvariant()} -> report {(imperial ? "imperial (in, psi, degF, lb)" : "metric (mm, MPa, degC, kg)")} with SI in brackets");

// --- Frame: the press axis W is the normal of the Top Plane = the 2nd RefPlane feature (language independent) ---
double[] W = null;
var planes = 0;
for (var f = (IFeature)Model.FirstFeature(); f != null; f = (IFeature)f.GetNextFeature())
{
    if (f.GetTypeName2() != "RefPlane") continue;
    if (++planes != 2) continue;
    var rp = f.GetSpecificFeature2() as IRefPlane;
    var t = rp?.Transform as MathTransform;
    var a = t?.ArrayData as double[];
    if (a != null && a.Length >= 9) W = new[] { a[6], a[7], a[8] };
    Line($"Frame: Top Plane = '{f.Name}' (2nd RefPlane), W = [{Num(W?[0] ?? 0)}, {Num(W?[1] ?? 0)}, {Num(W?[2] ?? 0)}] ({(W != null && Math.Abs(W[2]) > 0.99 ? "Z-up" : W != null && Math.Abs(W[1]) > 0.99 ? "Y-up" : "tilted")})");
    break;
}
if (W == null)
{
    W = new[] { 0.0, 0.0, 1.0 };
    Line("Frame: Top Plane not found - assumed W = +Z. Check before building.");
}
// U, V in the plane: the model axis least aligned with W, made orthogonal; V = W x U.
var k = Enumerable.Range(0, 3).OrderBy(i => Math.Abs(W[i])).First();
var e = new double[3]; e[k] = 1;
var d0 = e[0] * W[0] + e[1] * W[1] + e[2] * W[2];
var U = new[] { e[0] - d0 * W[0], e[1] - d0 * W[1], e[2] - d0 * W[2] };
var un = Math.Sqrt(U[0] * U[0] + U[1] * U[1] + U[2] * U[2]);
U = new[] { U[0] / un, U[1] / un, U[2] / un };
var V = new[] { W[1] * U[2] - W[2] * U[1], W[2] * U[0] - W[0] * U[2], W[0] * U[1] - W[1] * U[0] };

// Extent of a body along a direction: max and min by GetExtremePoint (tight on curved bodies, unlike GetBodyBox).
double[] Extent(IBody2 b, double[] dir)
{
    b.GetExtremePoint(dir[0], dir[1], dir[2], out var x1, out var y1, out var z1);
    b.GetExtremePoint(-dir[0], -dir[1], -dir[2], out var x0, out var y0, out var z0);
    return new[] { x0 * dir[0] + y0 * dir[1] + z0 * dir[2], x1 * dir[0] + y1 * dir[1] + z1 * dir[2] };
}

// --- Bodies ---
var solids = ((object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false) ?? new object[0]).OfType<IBody2>().ToList();
var sheets = ((object[])part.GetBodies2((int)swBodyType_e.swSheetBody, false) ?? new object[0]).OfType<IBody2>().ToList();
Line($"Solid bodies: {solids.Count}; surface bodies: {sheets.Count}{(sheets.Count > 0 ? " (surface bodies can't be molded - ask for a solid)" : "")}");
foreach (var b in solids.Take(12))
{
    var mp = b.GetMassProperties(1.0) as double[];
    var vol = mp != null && mp.Length > 3 ? mp[3] * 1e9 : double.NaN;
    var eu = Extent(b, U); var ev = Extent(b, V); var ew = Extent(b, W);
    Line($"  - {b.Name}: volume {Num(vol, "0")} mm3; U {Mm(eu[0])}..{Mm(eu[1])}, V {Mm(ev[0])}..{Mm(ev[1])}, W {Mm(ew[0])}..{Mm(ew[1])} mm (size {Mm(eu[1] - eu[0])} x {Mm(ev[1] - ev[0])} x {Mm(ew[1] - ew[0])} mm)");
}
if (solids.Count > 12) Line($"  ... and {solids.Count - 12} more");

// --- Inserted parts and MOLD features (resume state) ---
var moldFeatures = new List<string>();
for (var f = (IFeature)Model.FirstFeature(); f != null; f = (IFeature)f.GetNextFeature())
{
    var name = f.Name ?? "";
    if (name.StartsWith("MOLD ", StringComparison.Ordinal)) moldFeatures.Add(name);
    string inserted = null;
    try
    {
        if (f.GetDefinition() is IDerivedPartFeatureData dp) inserted = dp.PathName;
    }
    catch { }
    if (inserted != null) Line($"Inserted part: '{name}' <- {inserted}");
}
Line(moldFeatures.Count == 0 ? "MOLD features: none" :
     $"MOLD features: {moldFeatures.Count}, last: {string.Join(", ", moldFeatures.Skip(Math.Max(0, moldFeatures.Count - 4)))}");

// --- The mold record (custom property Unify3D.MoldRecord -> '<name>.mold' folder) ---
string recordStatus = null;
if (saved)
{
    var cpm = ext.get_CustomPropertyManager("");
    string recVal = null, recResolved = null, idVal = null, idResolved = null;
    cpm?.Get4("Unify3D.MoldRecord", false, out recVal, out recResolved);
    cpm?.Get4("Unify3D.MoldId", false, out idVal, out idResolved);
    var folder = string.IsNullOrWhiteSpace(recResolved) ? Path.ChangeExtension(path, null) + ".mold"
               : Path.IsPathRooted(recResolved) ? recResolved : Path.Combine(Path.GetDirectoryName(path) ?? "", recResolved);
    var json = Path.Combine(folder, "mold.json");
    if (File.Exists(json))
    {
        try
        {
            var rec = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(json));
            recordStatus = (string)rec["status"];
            var stages = rec["stages"] as Newtonsoft.Json.Linq.JArray;
            var done = stages?.Where(s => (string)s["status"] == "done").Select(s => (string)s["id"]).ToList() ?? new List<string>();
            Line($"Mold record: {folder} | id {(string)rec["id"]} | status {recordStatus} | scope {(string)rec["scope"] ?? "build"} | stages done: {(done.Count == 0 ? "none" : string.Join(", ", done))} | updated {(string)rec["updatedAt"]}");
            if (!string.IsNullOrEmpty(idResolved) && idResolved != (string)rec["id"])
                Line($"  WARNING: the part's Unify3D.MoldId ({idResolved}) differs from the record id - the record may belong to another file.");
        }
        catch (Exception ex)
        {
            Line($"Mold record: {json} exists but can't be read ({FirstLine(ex.Message)})");
        }
    }
    else
    {
        Line(string.IsNullOrWhiteSpace(recResolved) ? "Mold record: none" : $"Mold record: property points to {folder}, but mold.json is missing");
    }
}
else
{
    Line("Mold record: none (save the part first; mold_record init needs a saved file)");
}

// --- The page's files and selections ---
var partFiles = Inputs.Files("part_file");
var drawings = Inputs.Files("drawing");
foreach (var p in partFiles)
{
    var fi = new FileInfo(p);
    var x = fi.Extension.ToLowerInvariant();
    var note = x == ".stl" || x == ".3mf" || x == ".obj" ? " - MESH: can't become a reliable solid, ask for STEP or SLDPRT"
             : x == ".sldprt" ? "" : " - neutral format: converted to SLDPRT in <record>\\part\\ before Insert Part";
    Line($"part_file: {fi.Name} ({(fi.Exists ? Num(fi.Length / 1024.0, "0") + " KB" : "MISSING")}){note}");
}
if (partFiles.Count == 0) Line("part_file: (none)");
foreach (var p in drawings)
{
    var fi = new FileInfo(p);
    var x = fi.Extension.ToLowerInvariant();
    var note = x == ".dxf" || x == ".dwg" ? " - path only: ask for a PDF"
             : x == ".pdf" && fi.Exists && fi.Length > 20000000 ? " - over 20 MB: ask for a smaller PDF" : "";
    Line($"drawing: {fi.Name} ({(fi.Exists ? Num(fi.Length / 1024.0, "0") + " KB" : "MISSING")}){note}");
}
if (drawings.Count == 0) Line("drawing: (none)");
Line($"critical_faces selected on the page: {Inputs.Selected("critical_faces").Count}");

// Nothing to mold and nothing to resume: stop here instead of starting the agent.
if (solids.Count == 0 && partFiles.Count == 0 && drawings.Count == 0 && recordStatus == null)
    Fail("Nothing to mold: add the part file or its drawing on the page, or open the mold file that contains the part.");

var start = recordStatus != null && recordStatus != "planning" ? "resume (record exists)"
          : solids.Count > 0 ? "part already in the file"
          : partFiles.Count > 0 ? "insert part_file (Phase 1)"
          : "drawing only (Phase 1a: model the part from the drawing)";
Line($"Start: {start}");

// --- The part analysis, when the molded part is already here and nothing has been built yet ---
if (solids.Count > 0 && (recordStatus == null || recordStatus == "planning"))
{
    try
    {
        var analysis = await Agent.CallToolAsync("mold_part", new { action = "analyze", body = "auto", record = false, show = false });
        var room = Math.Max(800, Budget - o.Length - 200);
        Line("mold_part analyze (pull +W, not recorded):");
        Line(analysis.Length > room ? analysis.Substring(0, room) + "\n... (cut - run mold_part analyze for the rest)" : analysis);
    }
    catch (Exception ex)
    {
        Line($"mold_part analyze not run here: {FirstLine(ex.Message)}. Run it in Phase 1.");
    }
}

var text = o.ToString();
Print(text.Length > Budget ? text.Substring(0, Budget) + "\n... (cut)" : text.TrimEnd());

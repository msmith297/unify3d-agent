using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SwAgent.Infrastructure;

namespace SwAgent.Mold.Geometry
{
    /// <summary>One solid body of the mold part, identified by role and geometry (design D3).</summary>
    public sealed class MoldBody
    {
        public IBody2 Body;
        public string Id;
        public string Role;
        public string Folder;
        /// <summary>The SolidWorks body name (auto-named; renumbers - never use it to find a body later).</summary>
        public string Name;
        public double Volume;
        /// <summary>Box in the mold frame [u0, v0, w0, u1, v1, w1], meters.</summary>
        public double[] Box;
        /// <summary>Centroid in the mold frame, meters.</summary>
        public double[] Centroid;
        public string SwMaterial;

        public override string ToString() =>
            $"{Id} ({Role}, '{Name}'): {Volume / 1e-9:0.#} mm3, box {(Box[3] - Box[0]) * 1000:0.##} x {(Box[4] - Box[1]) * 1000:0.##} x {(Box[5] - Box[2]) * 1000:0.##} mm";
    }

    /// <summary>
    /// mold_bodies report and clearance [SolidWorks] (design 2.3.4): every solid with its role (its "Mold - *" folder), id
    /// (geometric order within the role), volume, mass, tight box, material, the weight and size limits, interference
    /// between mold bodies, and closest distances between named pairs. Read-only.
    /// </summary>
    public static class BodyReport
    {
        /// <summary>The bodies with roles and ids. Cavities (id, u, v in meters) name inserts after their cavity.</summary>
        public static List<MoldBody> List(IModelDoc2 model, MoldFrame frame, IList<(string Id, double U, double V)> cavities = null)
        {
            SwGeo.RequirePart(model);
            var folderOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (_, name, bodies) in SwGeo.BodyFolders(model))
                foreach (var b in bodies)
                {
                    var n = SwGeo.SafeName(b);
                    if (!folderOf.ContainsKey(n)) folderOf[n] = name;
                }
            var cfg = ActiveConfig(model);
            var partMaterial = PartMaterial(model, cfg);
            var list = new List<MoldBody>();
            var entries = new List<BodyRoles.Entry>();
            foreach (var b in SwGeo.Solids(model))
            {
                var name = SwGeo.SafeName(b);
                var c = SwGeo.Centroid(b) ?? new double[3];
                var mb = new MoldBody
                {
                    Body = b,
                    Name = name,
                    Folder = folderOf.TryGetValue(name, out var f) ? f : null,
                    Volume = SwGeo.Volume(b),
                    Box = SwGeo.Box(b, frame),
                    Centroid = frame.ToFrame(c),
                };
                try
                {
                    var m = b.GetMaterialPropertyName(cfg, out _);
                    mb.SwMaterial = string.IsNullOrWhiteSpace(m) ? partMaterial : m;
                }
                catch
                {
                    mb.SwMaterial = partMaterial;
                }
                list.Add(mb);
                entries.Add(new BodyRoles.Entry { Tag = mb, Folder = mb.Folder, Centroid = mb.Centroid });
            }
            foreach (var e in BodyRoles.Assign(entries, cavities))
            {
                var mb = (MoldBody)e.Tag;
                mb.Id = e.Id;
                mb.Role = e.Role;
            }
            return list.OrderBy(x => RoleOrder(x.Role)).ThenBy(x => x.Id.Length).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
        }

        private static int RoleOrder(string role)
        {
            for (var i = 0; i < BodyRoles.Table.Count; i++) if (BodyRoles.Table[i].Name == role) return i;
            return BodyRoles.Table.Count;
        }

        public static string ActiveConfig(IModelDoc2 model)
        {
            try
            {
                return model.ConfigurationManager?.ActiveConfiguration?.Name ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string PartMaterial(IModelDoc2 model, string cfg)
        {
            try
            {
                var m = ((IPartDoc)model).GetMaterialPropertyName2(cfg, out _);
                return string.IsNullOrWhiteSpace(m) ? null : m;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Mass by the assigned SolidWorks material (IMassProperty2 on the body), or null.</summary>
        public static double? SolidWorksMass(IModelDoc2 model, IBody2 body)
        {
            try
            {
                if (!(model.Extension.CreateMassProperty2() is IMassProperty2 mp)) return null;
                mp.UseSystemUnits = true;
                mp.SelectedItems = new object[] { body };
                var m = mp.Mass;
                return m > 0 ? m : (double?)null;
            }
            catch (Exception ex)
            {
                Log.Warn("Mass by material: " + ex.Message);
                return null;
            }
        }

        public sealed class Limits
        {
            public double? PlateMassKg, LidMassKg, BaseMassKg;
            /// <summary>Largest machinable blank, sorted descending (mm).</summary>
            public double[] PlateSizeMm;
            public double BlankAllowanceMm = 3;
        }

        /// <summary>Volume x density per body (kg/m^3 by role), else the SolidWorks material's mass; plus the limit checks.</summary>
        public static (JObject Json, string Text, List<JObject> Checks) Report(IModelDoc2 model, ISldWorks sw, MoldFrame frame, List<MoldBody> bodies,
            Func<MoldBody, (double? Density, string Source)> density, Limits limits, ICollection<(string A, string B)> fits, bool interference)
        {
            var sb = new StringBuilder();
            var checks = new List<JObject>();
            var rows = new JArray();
            var masses = new Dictionary<MoldBody, double?>();
            string F(double x, string fmt = "0.##") => x.ToString(fmt, CultureInfo.InvariantCulture);
            sb.AppendLine($"{bodies.Count} solid bodies (frame W = ({F(frame.W[0], "0.###")}, {F(frame.W[1], "0.###")}, {F(frame.W[2], "0.###")})):");
            foreach (var b in bodies)
            {
                var (rho, source) = density(b);
                double? mass = rho.HasValue ? b.Volume * rho.Value : SolidWorksMass(model, b.Body);
                if (!rho.HasValue) source = mass.HasValue ? $"SolidWorks material {b.SwMaterial ?? "(part default)"}" : "unknown";
                masses[b] = mass;
                var dims = new[] { b.Box[3] - b.Box[0], b.Box[4] - b.Box[1], b.Box[5] - b.Box[2] }.Select(x => x * 1000).ToArray();
                var row = new JObject
                {
                    ["id"] = b.Id,
                    ["role"] = b.Role,
                    ["folder"] = b.Folder,
                    ["swName"] = b.Name,
                    ["swMaterial"] = b.SwMaterial,
                    ["volume_mm3"] = Math.Round(b.Volume / 1e-9, 1),
                    ["mass_kg"] = mass.HasValue ? Math.Round(mass.Value, 3) : (JToken)null,
                    ["massBasis"] = source,
                    ["box_mm"] = new JArray(dims.Select(x => Math.Round(x, 3))),
                    ["boxFrame_mm"] = new JArray(b.Box.Select(x => Math.Round(x * 1000, 3))),
                    ["centroid"] = new JArray(b.Centroid.Select(x => Math.Round(x * 1000, 3))),
                };
                var limitFlags = new JObject();
                if (IsMachined(b.Role) && limits != null)
                {
                    if (limits.PlateMassKg.HasValue && mass.HasValue)
                    {
                        var ok = mass.Value <= limits.PlateMassKg.Value;
                        limitFlags["mass"] = ok ? "pass" : "fail";
                        checks.Add(Check($"WEIGHT-{b.Id}", "weight", b.Id, mass.Value, limits.PlateMassKg.Value, "kg", "S72: machined piece <= max_plate_mass"));
                    }
                    if (limits.PlateSizeMm != null && limits.PlateSizeMm.Length == 3)
                    {
                        var need = dims.Select(x => x + 2 * limits.BlankAllowanceMm).OrderByDescending(x => x).ToArray();
                        var have = limits.PlateSizeMm.OrderByDescending(x => x).ToArray();
                        var worst = Enumerable.Range(0, 3).Select(k => need[k] / have[k]).Max();
                        limitFlags["box"] = worst <= 1 ? "pass" : "fail";
                        checks.Add(Check($"BOX-{b.Id}", "box", b.Id, worst, 1, "ratio", $"S73: sorted box + {F(limits.BlankAllowanceMm)} mm blank allowance per side <= sorted max_plate_size"));
                    }
                }
                if (limitFlags.Count > 0) row["limits"] = limitFlags;
                rows.Add(row);
                sb.AppendLine($"  {b.Id,-10} {b.Role,-9} {F(dims[0], "0.#")} x {F(dims[1], "0.#")} x {F(dims[2], "0.#")} mm, {F(b.Volume / 1e-9, "0")} mm3" +
                              (mass.HasValue ? $", {F(mass.Value, "0.###")} kg ({source})" : ", mass unknown") +
                              $", '{b.Name}'" + (b.Folder == null ? " - NOT IN A MOLD FOLDER" : "") + (limitFlags.Properties().Any(p => (string)p.Value == "fail") ? " - OVER A LIMIT" : ""));
            }

            // Lid and base totals.
            double Sum(Func<MoldBody, bool> pick) => bodies.Where(pick).Sum(b => masses[b] ?? 0);
            var lid = Sum(b => b.Role == "lid");
            var baseTotal = Sum(b => b.Role == "base" || b.Role == "subplate");
            var json = new JObject { ["bodies"] = rows, ["lid_kg"] = Math.Round(lid, 3), ["base_kg"] = Math.Round(baseTotal, 3) };
            var middle = bodies.Where(b => b.Role == "middle").ToList();
            sb.AppendLine($"Lid total {F(lid, "0.###")} kg, base total {F(baseTotal, "0.###")} kg" + (middle.Count > 0 ? $", middle plates {string.Join(", ", middle.Select(b => $"{b.Id} {F(masses[b] ?? 0, "0.###")} kg"))}" : "") + ".");
            if (limits?.LidMassKg != null && bodies.Any(b => b.Role == "lid")) checks.Add(Check("WEIGHT-LID", "weight", "LID", lid, limits.LidMassKg.Value, "kg", "lid total <= max_lid_mass"));
            if (limits?.BaseMassKg != null && bodies.Any(b => b.Role == "base")) checks.Add(Check("WEIGHT-BASE", "weight", "BASE", baseTotal, limits.BaseMassKg.Value, "kg", "base total <= max_base_mass"));

            // Interference between mold bodies (not the reference part or garbage), except recorded fits.
            if (interference)
            {
                var mold = bodies.Where(b => IsMachined(b.Role) || b.Role == "unfiled").ToList();
                var modeler = sw.GetModeler() as IModeler;
                var found = new JArray();
                for (var i = 0; i < mold.Count && modeler != null; i++)
                    for (var j = i + 1; j < mold.Count; j++)
                    {
                        var a = mold[i];
                        var b = mold[j];
                        if (!BoxesTouch(a.Box, b.Box, 1e-6)) continue;
                        var v = InterferenceVolume(modeler, a.Body, b.Body);
                        if (v * 1e9 <= 1) continue;
                        var isFit = fits != null && fits.Any(x => (x.A == a.Id && x.B == b.Id) || (x.A == b.Id && x.B == a.Id));
                        found.Add(new JObject { ["a"] = a.Id, ["b"] = b.Id, ["volume_mm3"] = Math.Round(v * 1e9, 2), ["fit"] = isFit });
                        if (!isFit) checks.Add(Check($"INTERF-{a.Id}-{b.Id}", "interference", a.Id, v * 1e9, 1, "mm3", $"{a.Id} and {b.Id} overlap"));
                    }
                json["interference"] = found;
                sb.AppendLine(found.Count == 0 ? "Interference: none between mold bodies (over 1 mm3)." :
                    "Interference: " + string.Join("; ", found.Select(x => $"{x["a"]}/{x["b"]} {x["volume_mm3"]} mm3{((bool)x["fit"] ? " (recorded fit)" : " - NOT a recorded fit")}")));
            }
            var failing = checks.Where(c => (string)c["status"] == "fail").ToList();
            if (failing.Count > 0) sb.AppendLine($"FAILING ({failing.Count}): " + string.Join("; ", failing.Select(c => $"{c["id"]} {c["demand"]} vs {c["capacity"]} {c["unit"]}")));
            json["checks"] = new JArray(checks);
            return (json, sb.ToString().TrimEnd(), checks);
        }

        public static bool IsMachined(string role) => role == "base" || role == "subplate" || role == "middle" || role == "lid" || role == "insert" || role == "stop";

        private static JObject Check(string id, string type, string body, double demand, double capacity, string unit, string method)
        {
            var margin = capacity / Math.Max(1e-12, demand) - 1;
            return new JObject
            {
                ["id"] = id,
                ["source"] = "mold_bodies",
                ["type"] = type,
                ["body"] = body,
                ["demand"] = Math.Round(demand, 4),
                ["capacity"] = Math.Round(capacity, 4),
                ["unit"] = unit,
                ["margin"] = Math.Round(margin, 4),
                ["status"] = demand <= capacity ? "pass" : "fail",
                ["method"] = method,
            };
        }

        public static bool BoxesTouch(double[] a, double[] b, double tol)
        {
            for (var k = 0; k < 3; k++)
                if (a[k] - tol > b[k + 3] || b[k] - tol > a[k + 3]) return false;
            return true;
        }

        /// <summary>Volume of the overlap of two bodies (m^3): IModeler.CheckInterference3 returning the interfering bodies.</summary>
        public static double InterferenceVolume(IModeler modeler, IBody2 a, IBody2 b)
        {
            try
            {
                object faces1 = null, faces2 = null, bodies = null;
                var ok = modeler.CheckInterference3(new object[] { a }, new object[] { b },
                    (int)SolidWorks.Interop.swconst.swCheckInterferenceOption_e.swBodyInterference_ReturnInterferingObject, ref faces1, ref faces2, ref bodies);
                if (!ok || !(bodies is object[] list)) return 0;
                return list.OfType<IBody2>().Sum(x => Math.Max(0, SwGeo.Volume(x)));
            }
            catch (Exception ex)
            {
                Log.Warn("Interference check: " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// Closest distance between two sets of faces: pre-filtered by box, at most `maxPairs` face pairs through
        /// IModelDoc2.ClosestDistance. Returns the distance (m) and the two points (model meters), or NaN.
        /// </summary>
        public static (double D, double[] A, double[] B, int Pairs) Closest(IModelDoc2 model, IList<IFace2> a, IList<IFace2> b, int maxPairs = 400)
        {
            double BoxGap(double[] x, double[] y)
            {
                double s = 0;
                for (var k = 0; k < 3; k++)
                {
                    var d = Math.Max(0, Math.Max(x[k] - y[k + 3], y[k] - x[k + 3]));
                    s += d * d;
                }
                return Math.Sqrt(s);
            }
            var boxesA = a.Select(f => f.GetBox() as double[]).ToList();
            var boxesB = b.Select(f => f.GetBox() as double[]).ToList();
            var pairs = new List<(int I, int J, double Gap)>();
            for (var i = 0; i < a.Count; i++)
                for (var j = 0; j < b.Count; j++)
                    if (boxesA[i] != null && boxesB[j] != null) pairs.Add((i, j, BoxGap(boxesA[i], boxesB[j])));
            var best = (D: double.NaN, A: (double[])null, B: (double[])null);
            var done = 0;
            foreach (var (i, j, gap) in pairs.OrderBy(p => p.Gap))
            {
                if (done >= maxPairs) break;
                if (!double.IsNaN(best.D) && gap >= best.D) break; // no closer pair can follow
                done++;
                try
                {
                    var d = model.ClosestDistance(a[i], b[j], out var p1, out var p2);
                    if (d < 0) continue;
                    if (double.IsNaN(best.D) || d < best.D) best = (d, p1 as double[], p2 as double[]);
                }
                catch (Exception ex)
                {
                    Log.Warn("ClosestDistance: " + ex.Message);
                }
            }
            return (best.D, best.A, best.B, done);
        }
    }
}

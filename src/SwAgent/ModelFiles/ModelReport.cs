using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SwAgent.ModelFiles
{
    /// <summary>What SolidWorks itself reported about a model it loaded (plain data, so the summary is testable without it).</summary>
    internal sealed class SolidWorksFacts
    {
        /// <summary>"opened hidden in SolidWorks and closed again" / "already open in SolidWorks - read in place".</summary>
        public string How;

        public string DocumentType;
        public string LengthUnit;
        public int SolidBodies;
        public int SurfaceBodies;
        public int Components;
        public int Features;
        public int Configurations;
        public string Material;

        /// <summary>mm³, mm², kg - from SolidWorks' mass properties (exact, unlike the mesh's).</summary>
        public double? Volume, Area, Mass;

        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>A 3D model file turned into something the agent can read: a measurement summary and one image of six views.</summary>
    internal sealed class ModelReport
    {
        public string Name;
        public string Path;
        public string Format;
        public long Bytes;
        public string Summary;

        /// <summary>JPEG (base64) of the six-view sheet, or null.</summary>
        public string Views;

        /// <summary>Set when nothing useful could be produced (the summary still says what was tried).</summary>
        public string Error;

        public double Seconds;

        public JObject ToJson() => new JObject
        {
            ["name"] = Name,
            ["path"] = Path,
            ["format"] = Format,
            ["bytes"] = Bytes,
            ["summary"] = Summary,
            ["views"] = Views,
            ["viewsMediaType"] = Views == null ? null : "image/jpeg",
            ["error"] = Error,
            ["seconds"] = Math.Round(Seconds, 2),
        };

        public static string FormatName(string path)
        {
            switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
            {
                case ".stl": return "STL mesh";
                case ".3mf": return "3MF mesh";
                case ".obj": return "OBJ mesh";
                case ".step":
                case ".stp": return "STEP";
                case ".iges":
                case ".igs": return "IGES";
                case ".x_t":
                case ".x_b": return "Parasolid";
                case ".sldprt": return "SOLIDWORKS part";
                case ".sldasm": return "SOLIDWORKS assembly";
                default: return System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            }
        }

        /// <summary>The text the agent reads, e.g. "Size: 120 x 80 x 10 mm ...". Every number is in mm.</summary>
        internal static string Summarize(string name, string format, long bytes, string units, MeshStats mesh, SolidWorksFacts sw, char up, bool hasViews, IEnumerable<string> notes = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"3D model: {name} ({format}, {Size(bytes)})");
            if (sw != null) sb.AppendLine($"Read by: {sw.How}");
            if (!string.IsNullOrEmpty(units)) sb.AppendLine($"Units: {units}");

            if (mesh != null && mesh.Triangles > 0)
            {
                sb.AppendLine($"Size: {N(mesh.SizeX)} x {N(mesh.SizeY)} x {N(mesh.SizeZ)} mm (X {N(mesh.MinX)}..{N(mesh.MaxX)}, Y {N(mesh.MinY)}..{N(mesh.MaxY)}, Z {N(mesh.MinZ)}..{N(mesh.MaxZ)})");
            }

            if (sw != null)
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(sw.DocumentType)) parts.Add(sw.DocumentType);
                if (sw.Components > 0) parts.Add($"{sw.Components} component{(sw.Components == 1 ? "" : "s")}");
                if (sw.SolidBodies > 0 || sw.SurfaceBodies > 0) parts.Add($"{sw.SolidBodies} solid bod{(sw.SolidBodies == 1 ? "y" : "ies")}{(sw.SurfaceBodies > 0 ? $", {sw.SurfaceBodies} surface bod{(sw.SurfaceBodies == 1 ? "y" : "ies")}" : "")}");
                if (sw.Features > 0) parts.Add($"{sw.Features} features");
                if (sw.Configurations > 1) parts.Add($"{sw.Configurations} configurations");
                if (parts.Count > 0) sb.AppendLine("Structure: " + string.Join(", ", parts));
                var mass = new List<string>();
                if (sw.Volume.HasValue) mass.Add($"volume {N(sw.Volume.Value)} mm³");
                if (sw.Area.HasValue) mass.Add($"surface area {N(sw.Area.Value)} mm²");
                if (sw.Mass.HasValue && sw.Mass.Value > 0) mass.Add($"mass {sw.Mass.Value.ToString("0.####", CultureInfo.InvariantCulture)} kg");
                if (!string.IsNullOrEmpty(sw.Material)) mass.Add($"material {sw.Material}");
                if (mass.Count > 0) sb.AppendLine("Mass properties (SolidWorks): " + string.Join(", ", mass));
            }
            else if (mesh != null && mesh.Triangles > 0)
            {
                sb.AppendLine(mesh.Closed
                    ? $"Volume {N(mesh.Volume)} mm³, surface area {N(mesh.Area)} mm² (closed mesh)"
                    : $"Surface area {N(mesh.Area)} mm²; volume not reliable - the mesh is open ({mesh.OpenEdges} open edges{(mesh.NonManifoldEdges > 0 ? $", {mesh.NonManifoldEdges} non-manifold" : "")})");
            }

            if (mesh != null && mesh.Triangles > 0)
            {
                var shells = mesh.Shells.Count;
                var line = $"Mesh: {mesh.Triangles:N0} triangles, {shells} separate shell{(shells == 1 ? "" : "s")}";
                if (shells > 1)
                {
                    line += " - largest: " + string.Join("; ", mesh.Shells.Take(4).Select(s => $"{N(s.SizeX)} x {N(s.SizeY)} x {N(s.SizeZ)} mm" + (s.Volume > 0 ? $", {N(s.Volume)} mm³" : "")));
                    if (shells > 4) line += $"; ... {shells - 4} more";
                }
                if (sw == null && mesh.Closed) line += ", watertight";
                sb.AppendLine(line);
            }

            if (hasViews)
            {
                sb.AppendLine("Attached: one image with six views of the model - isometric, front, top, right, back iso, from below " +
                              (up == 'Z' ? "(Z up, as the file is laid out for 3D printing; a triad in each tile shows the axes)." : "(SolidWorks axes, Y up; a triad in each tile shows the axes). Colours tell separate shells apart."));
            }
            foreach (var n in (notes ?? Enumerable.Empty<string>()).Concat(sw?.Notes ?? Enumerable.Empty<string>())) sb.AppendLine("Note: " + n);
            return sb.ToString().TrimEnd();
        }

        internal static string N(double v)
        {
            var a = Math.Abs(v);
            var s = a >= 10000 ? v.ToString("N0", CultureInfo.InvariantCulture) : a >= 100 ? v.ToString("0.#", CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        internal static string Size(long bytes) =>
            bytes >= 1 << 20 ? $"{(bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture)} MB"
            : bytes >= 1024 ? $"{bytes / 1024} KB"
            : $"{bytes} bytes";
    }
}

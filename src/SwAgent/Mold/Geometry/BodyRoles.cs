using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Mold.Geometry
{
    /// <summary>
    /// Body identity in the mold part (design D3): a body's role is its "Mold - *" solid-body folder; its id within the
    /// role follows geometry (centroid U, then V), never the auto-numbered SolidWorks body name, which renumbers when
    /// bodies before it change. Pure (GeometryBodyRolesTests).
    /// </summary>
    public static class BodyRoles
    {
        public sealed class Role
        {
            public string Folder, Name, Prefix;
            /// <summary>Lid side of the stack (for the lid total), base side, or neither.</summary>
            public string Half;
        }

        public static readonly IReadOnlyList<Role> Table = new[]
        {
            new Role { Folder = "Mold - Base", Name = "base", Prefix = "BASE", Half = "base" },
            new Role { Folder = "Mold - Subplate", Name = "subplate", Prefix = "SUB", Half = "base" },
            new Role { Folder = "Mold - Middle", Name = "middle", Prefix = "MID", Half = "middle" },
            new Role { Folder = "Mold - Lid", Name = "lid", Prefix = "LID", Half = "lid" },
            new Role { Folder = "Mold - Inserts", Name = "insert", Prefix = "INS", Half = null },
            new Role { Folder = "Mold - Stops", Name = "stop", Prefix = "STOP", Half = null },
            new Role { Folder = "Mold - Reference", Name = "reference", Prefix = "PART-REF", Half = null },
            new Role { Folder = "Garbage", Name = "garbage", Prefix = "GARBAGE", Half = null },
        };

        /// <summary>The role of a folder name (case-insensitive), or null for a folder the tool doesn't own.</summary>
        public static Role RoleOf(string folder) =>
            string.IsNullOrWhiteSpace(folder) ? null : Table.FirstOrDefault(r => string.Equals(r.Folder, folder.Trim(), StringComparison.OrdinalIgnoreCase));

        public static Role ForName(string role) =>
            Table.FirstOrDefault(r => string.Equals(r.Name, role, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Prefix, role, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(r.Folder, role, StringComparison.OrdinalIgnoreCase));

        public sealed class Entry
        {
            public object Tag;
            public string Folder;
            /// <summary>Centroid in the mold frame (u, v, w).</summary>
            public double[] Centroid;
            public string Role;
            public string Id;
        }

        /// <summary>
        /// Ids per role: sorted by centroid U, then V (positions within 0.1 mm count as equal, so tessellation noise can't
        /// swap two bodies), numbered from 1: BASE-1, BASE-2, LID-1 ... Inserts take the nearest cavity's id when the
        /// cavities are given (INS-C1-1). The reference body is PART-REF (PART-REF-2 ... if there are several). Bodies in
        /// no mold folder are "unfiled" (BODY-n).
        /// </summary>
        public static List<Entry> Assign(IEnumerable<Entry> bodies, IList<(string Id, double U, double V)> cavities = null)
        {
            var list = bodies.ToList();
            foreach (var group in list.GroupBy(b => RoleOf(b.Folder)?.Name ?? "unfiled"))
            {
                var role = RoleOf(group.First().Folder);
                var ordered = GeometricOrder(group.ToList());
                if (role?.Name == "insert" && cavities != null && cavities.Count > 0)
                {
                    foreach (var byCavity in ordered.GroupBy(b => cavities.OrderBy(c => (c.U - b.Centroid[0]) * (c.U - b.Centroid[0]) + (c.V - b.Centroid[1]) * (c.V - b.Centroid[1])).First().Id))
                    {
                        var k = 0;
                        foreach (var b in byCavity)
                        {
                            b.Role = role.Name;
                            b.Id = $"INS-{byCavity.Key}-{++k}";
                        }
                    }
                    continue;
                }
                for (var k = 0; k < ordered.Count; k++)
                {
                    var b = ordered[k];
                    b.Role = role?.Name ?? "unfiled";
                    b.Id = role == null ? $"BODY-{k + 1}" : role.Name == "reference" ? (k == 0 ? "PART-REF" : $"PART-REF-{k + 1}") : $"{role.Prefix}-{k + 1}";
                }
            }
            // Stable output order: the table's role order, then id.
            return list.OrderBy(b => RoleIndex(b.Role)).ThenBy(b => b.Id.Length).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Sorted by U; bodies whose U is within 0.1 mm of the first of a run count as one column and are sorted by V
        /// (then W) - a tolerant comparison rather than rounding, which would split two equal positions at a rounding edge.
        /// </summary>
        private static List<Entry> GeometricOrder(List<Entry> items)
        {
            const double Tol = 1e-4;
            var byU = items.OrderBy(b => b.Centroid[0]).ToList();
            var result = new List<Entry>();
            var k = 0;
            while (k < byU.Count)
            {
                var run = new List<Entry> { byU[k] };
                var u0 = byU[k].Centroid[0];
                while (++k < byU.Count && byU[k].Centroid[0] - u0 <= Tol) run.Add(byU[k]);
                result.AddRange(run.OrderBy(b => b.Centroid[1]).ThenBy(b => b.Centroid[2]));
            }
            return result;
        }

        private static int RoleIndex(string role)
        {
            for (var i = 0; i < Table.Count; i++) if (Table[i].Name == role) return i;
            return Table.Count;
        }
    }
}

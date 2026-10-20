using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Mold.Geometry
{
    /// <summary>
    /// Fragile steel (design 3.14, 2.3.3 step 7): mold steel that reaches into the part - core pins in holes, plugs in
    /// pockets, thin walls between ribs - found from the part's column intervals. Inside the part's outer outline, steel
    /// comes from the lid side (the part is missing from the top of the part down to the column's top) or from the base
    /// side (from the bottom up); an empty column is a through hole. Each connected region of one side's steel is one
    /// feature: width D (the area-equivalent diameter for round pins, else twice the inscribed radius from a distance
    /// transform), height H its deepest steel. Flags: D below 3 mm, or H/D above 2 for a region the part surrounds.
    /// Regions open to the outline count only where they reach more than 3 mm in from it (a slot between ribs), so the
    /// edge of the part itself (a step, a fillet, a chamfer) is not reported. 2.5D, pure (GeometryFragileTests).
    /// </summary>
    public static class Fragile
    {
        public sealed class Feature
        {
            public string Kind; // core_pin | pocket_core | steel_wall
            public double D, H;
            public double U, V;
            public double WFrom, WTo;
            public double Area;
            /// <summary>Surrounded by part material (doesn't reach the outline).</summary>
            public bool Enclosed;
            public bool Flagged;
            public string Reason;
            public string Side; // "lid" (steel from above), "base" (from below) or "through"
        }

        public sealed class Options
        {
            public double MinD = 0.003;
            public double MaxHOverD = 2.0;
            /// <summary>Steel shallower than this isn't a feature (sampling noise).</summary>
            public double MinHeight = 0.0005;
        }

        public static List<Feature> Find(ColumnMap map, Options o = null)
        {
            o = o ?? new Options();
            var g = map.Grid;
            var footprint = map.Footprint();
            if (Raster.Count(footprint) == 0) return new List<Feature>();
            var outside = Raster.Outside(footprint);
            double zTop = double.MinValue, zBottom = double.MaxValue;
            for (var i = 0; i < g.Nu; i++)
                for (var j = 0; j < g.Nv; j++)
                    if (map.HasPart(i, j))
                    {
                        zTop = Math.Max(zTop, map.Top(i, j));
                        zBottom = Math.Min(zBottom, map.Bottom(i, j));
                    }
            var inside = new bool[g.Nu, g.Nv];
            for (var i = 0; i < g.Nu; i++)
                for (var j = 0; j < g.Nv; j++)
                    inside[i, j] = !outside[i, j];
            var inFrom = Raster.DistanceToOutside(inside); // how far in from the outline, in cells

            var result = new List<Feature>();
            foreach (var lidSide in new[] { true, false })
            {
                var steel = new bool[g.Nu, g.Nv];
                var height = new double[g.Nu, g.Nv];
                for (var i = 0; i < g.Nu; i++)
                    for (var j = 0; j < g.Nv; j++)
                    {
                        if (!inside[i, j]) continue;
                        height[i, j] = !map.HasPart(i, j) ? zTop - zBottom : lidSide ? zTop - map.Top(i, j) : map.Bottom(i, j) - zBottom;
                        steel[i, j] = height[i, j] > o.MinHeight;
                    }
                var width = Raster.DistanceToOutside(steel); // to the nearest part (or outside) cell
                foreach (var comp in Raster.Components(steel))
                {
                    var empty = comp.Count(c => !map.HasPart(c.I, c.J));
                    var through = empty * 2 > comp.Count;
                    if (through && !lidSide) continue; // a through hole: already reported from the lid side
                    var enclosed = comp.All(c => !Touches(outside, c.I, c.J));
                    // Open to the outline: only the part of the region deeper than MinD inside the outline can be a wall.
                    var core = enclosed ? comp : comp.Where(c => (inFrom[c.I, c.J] - 0.5) * g.H > o.MinD).ToList();
                    if (core.Count == 0) continue;
                    var maxDist = core.Max(c => width[c.I, c.J]);
                    var dEdt = Math.Max(1, 2 * maxDist - 1) * g.H;
                    var area = comp.Count * g.H * g.H;
                    // A round pin: the area-equivalent diameter beats the distance transform at a few cells per radius.
                    var round = enclosed && area <= 1.2 * Math.PI * dEdt * dEdt / 4;
                    var d = round ? Math.Sqrt(4 * area / Math.PI) : dEdt;
                    var h = core.Max(c => height[c.I, c.J]);
                    var f = new Feature
                    {
                        D = d,
                        H = h,
                        U = core.Average(c => g.CU(c.I)),
                        V = core.Average(c => g.CV(c.J)),
                        Area = area,
                        Enclosed = enclosed,
                        Side = through ? "through" : lidSide ? "lid" : "base",
                    };
                    f.WFrom = f.Side == "lid" ? zTop - h : zBottom;
                    f.WTo = f.Side == "base" ? zBottom + h : zTop;
                    var compact = area <= 3 * Math.PI * d * d / 4;
                    f.Kind = !compact || !enclosed ? "steel_wall" : through ? "core_pin" : "pocket_core";
                    if (d < o.MinD - 1e-12)
                    {
                        // At the outline only a tall wall counts (a fillet or a chamfer is as wide as it is high).
                        if (enclosed || h > 1.5 * d)
                        {
                            f.Flagged = true;
                            f.Reason = $"{(f.Kind == "steel_wall" ? "wall" : "core")} {d * 1000:0.##} mm wide < {o.MinD * 1000:0.#} mm";
                        }
                    }
                    else if (enclosed && h / d > o.MaxHOverD + 1e-9)
                    {
                        f.Flagged = true;
                        f.Reason = $"H/D {h / d:0.##} > {o.MaxHOverD:0.#}";
                    }
                    if (!enclosed && !f.Flagged) continue; // a wide region open to the edge: just the part's shape
                    result.Add(f);
                }
            }
            return result.OrderByDescending(x => x.Flagged).ThenByDescending(x => x.H / x.D).ToList();
        }

        private static bool Touches(bool[,] outside, int i, int j)
        {
            int nu = outside.GetLength(0), nv = outside.GetLength(1);
            for (var di = -1; di <= 1; di++)
                for (var dj = -1; dj <= 1; dj++)
                {
                    int x = i + di, y = j + dj;
                    if (x < 0 || y < 0 || x >= nu || y >= nv || outside[x, y]) return true;
                }
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Mold.Geometry
{
    /// <summary>One ray hit from IModelDoc2.GetRayIntersectionsPoints.</summary>
    public struct RayHit
    {
        public int Body;
        public int Ray;
        /// <summary>swRayPtsResults_e flags: FACE 1, SILHOUETTE 2, EDGE 4, VERTEX 8, ENTER 16, EXIT 32.</summary>
        public int Type;
        public double[] Point;
        public double[] Normal;
        /// <summary>Index of this hit in the topology array (GetRayIntersectionsTopology), i.e. its record number.</summary>
        public int Index;

        public bool Enter => (Type & 16) != 0;
        public bool Exit => (Type & 32) != 0;
    }

    /// <summary>Parsing ray-cast results and turning a column's hits into the part's W intervals. Pure (ColumnsTests).</summary>
    public static class Columns
    {
        public const int Enter = 16, Exit = 32, Silhouette = 2;

        /// <summary>
        /// Records of [body, ray, type, x, y, z, (nx, ny, nz)]. The record length is checked rather than assumed (9 with
        /// NORMALS, 6 without), so a different layout fails loudly instead of returning wrong geometry.
        /// </summary>
        public static List<RayHit> Parse(double[] data, int hits, int rays, int bodies, bool normals)
        {
            var result = new List<RayHit>();
            if (data == null || hits <= 0) return result;
            var strides = normals ? new[] { 9, 6, 11, 12 } : new[] { 6, 9, 11, 12 };
            foreach (var stride in strides)
            {
                if (data.Length < hits * stride) continue;
                var ok = true;
                result.Clear();
                for (var h = 0; h < hits && ok; h++)
                {
                    double b = data[h * stride], ray = data[h * stride + 1], type = data[h * stride + 2];
                    ok = b >= 0 && b < bodies && Math.Abs(b - Math.Round(b)) < 1e-9 && ray >= 0 && ray < rays && Math.Abs(ray - Math.Round(ray)) < 1e-9
                         && type >= 0 && Math.Abs(type - Math.Round(type)) < 1e-9;
                    if (!ok) break;
                    var hit = new RayHit
                    {
                        Body = (int)Math.Round(b),
                        Ray = (int)Math.Round(ray),
                        Type = (int)Math.Round(type),
                        Point = new[] { data[h * stride + 3], data[h * stride + 4], data[h * stride + 5] },
                        Index = h,
                    };
                    if (stride >= 9) hit.Normal = new[] { data[h * stride + 6], data[h * stride + 7], data[h * stride + 8] };
                    result.Add(hit);
                }
                if (ok) return result;
            }
            throw new InvalidOperationException($"unexpected ray-intersection data ({data.Length} values for {hits} hits)");
        }

        /// <summary>
        /// The part's solid intervals [bottom, top] along a vertical column, from the heights of the hits of one ray cast
        /// downward from above the part (ENTRY_EXIT | UNBLOCK). Uses the enter/exit flags when SolidWorks gives them,
        /// else pairs the sorted hits; zero-length pairs (grazing an edge or a silhouette) are dropped.
        /// </summary>
        public static List<(double Bottom, double Top)> Intervals(IEnumerable<(double W, int Type)> hits, double tol = 1e-7)
        {
            var sorted = hits.OrderByDescending(h => h.W).ToList();
            // Duplicates (a hit on an edge shared by two faces is reported twice).
            var clean = new List<(double W, int Type)>();
            foreach (var h in sorted)
            {
                if (clean.Count > 0 && Math.Abs(clean[clean.Count - 1].W - h.W) < tol && (clean[clean.Count - 1].Type & (Enter | Exit)) == (h.Type & (Enter | Exit))) continue;
                clean.Add(h);
            }
            var result = new List<(double, double)>();
            var typed = clean.Count > 0 && clean.All(h => (h.Type & (Enter | Exit)) != 0 && (h.Type & (Enter | Exit)) != (Enter | Exit));
            if (typed)
            {
                double? top = null;
                foreach (var h in clean)
                {
                    if ((h.Type & Enter) != 0)
                    {
                        if (top == null) top = h.W;
                    }
                    else if (top != null)
                    {
                        if (top.Value - h.W > tol) result.Add((h.W, top.Value));
                        top = null;
                    }
                }
                if (result.Count > 0 || clean.Count < 2) return Merge(result, tol);
            }
            // Untyped (or inconsistent flags): drop exact double hits, then pair in order.
            var ws = new List<double>();
            foreach (var h in clean)
            {
                if (ws.Count > 0 && Math.Abs(ws[ws.Count - 1] - h.W) < tol)
                {
                    ws.RemoveAt(ws.Count - 1); // a tangent touch: in and straight out
                    continue;
                }
                ws.Add(h.W);
            }
            for (var k = 0; k + 1 < ws.Count; k += 2)
                if (ws[k] - ws[k + 1] > tol) result.Add((ws[k + 1], ws[k]));
            return Merge(result, tol);
        }

        private static List<(double Bottom, double Top)> Merge(List<(double Bottom, double Top)> list, double tol)
        {
            var sorted = list.OrderByDescending(x => x.Top).ToList();
            var r = new List<(double Bottom, double Top)>();
            foreach (var x in sorted)
            {
                if (r.Count > 0 && r[r.Count - 1].Bottom - x.Top <= tol)
                {
                    var last = r[r.Count - 1];
                    r[r.Count - 1] = (Math.Min(last.Bottom, x.Bottom), last.Top);
                    continue;
                }
                r.Add(x);
            }
            return r;
        }
    }

    /// <summary>
    /// Column data of the part on a grid: the solid W intervals per cell (top first). Built from ray casts by the
    /// analysis; everything derived from it (ceiling map, cross-section area at a level, internal ceilings, cores) is pure.
    /// </summary>
    public sealed class ColumnMap
    {
        public ColumnMap(Grid2 grid)
        {
            Grid = grid;
            Cells = new List<(double Bottom, double Top)>[grid.Nu, grid.Nv];
        }

        public Grid2 Grid { get; }
        public List<(double Bottom, double Top)>[,] Cells { get; }

        public bool HasPart(int i, int j) => Cells[i, j] != null && Cells[i, j].Count > 0;

        public double Top(int i, int j) => HasPart(i, j) ? Cells[i, j].Max(x => x.Top) : double.NaN;
        public double Bottom(int i, int j) => HasPart(i, j) ? Cells[i, j].Min(x => x.Bottom) : double.NaN;

        /// <summary>Ceiling map for a fill from below (base pour): the upper envelope. Lid pour: the lower envelope, negated (the lid is filled upside down).</summary>
        public double[,] Ceiling(bool lidPour)
        {
            var c = new double[Grid.Nu, Grid.Nv];
            for (var i = 0; i < Grid.Nu; i++)
                for (var j = 0; j < Grid.Nv; j++)
                    c[i, j] = !HasPart(i, j) ? double.NaN : lidPour ? -Bottom(i, j) : Top(i, j);
            return c;
        }

        /// <summary>Cross-section area of the part at height w.</summary>
        public double SectionArea(double w)
        {
            var n = 0;
            for (var i = 0; i < Grid.Nu; i++)
                for (var j = 0; j < Grid.Nv; j++)
                    if (Cells[i, j] != null && Cells[i, j].Any(x => x.Bottom <= w && w <= x.Top)) n++;
            return n * Grid.H * Grid.H;
        }

        /// <summary>Columns with more than one solid interval: a ceiling inside the part (side core, or check by hand).</summary>
        public List<(int I, int J, double CeilingW)> InternalCeilings(bool lidPour)
        {
            var r = new List<(int, int, double)>();
            for (var i = 0; i < Grid.Nu; i++)
                for (var j = 0; j < Grid.Nv; j++)
                {
                    var c = Cells[i, j];
                    if (c == null || c.Count < 2) continue;
                    // Base pour: the top of every lower interval is a ceiling under part material. Lid pour: the bottoms above.
                    var ordered = c.OrderByDescending(x => x.Top).ToList();
                    if (lidPour) r.Add((i, j, ordered[0].Bottom));
                    else r.Add((i, j, ordered[1].Top));
                }
            return r;
        }

        /// <summary>The section at height w (cells whose solid contains w within tol).</summary>
        public bool[,] SectionAt(double w, double tol = 0)
        {
            var m = new bool[Grid.Nu, Grid.Nv];
            for (var i = 0; i < Grid.Nu; i++)
                for (var j = 0; j < Grid.Nv; j++)
                    m[i, j] = Cells[i, j] != null && Cells[i, j].Any(x => x.Bottom - tol <= w && w <= x.Top + tol);
            return m;
        }

        /// <summary>
        /// Whether any of the points (u, v) lies within `reach` of a cell outside the part at height w that connects to the
        /// grid border - the part's outer outline at that level, not a hole or pocket inside it. `exterior` caches
        /// Raster.Outside(SectionAt(w)).
        /// </summary>
        public bool NearOuterOutline(IEnumerable<(double U, double V)> pts, bool[,] exterior, double reach)
        {
            var g = Grid;
            var k = Math.Max(1, (int)Math.Ceiling(reach / g.H));
            foreach (var (u, v) in pts)
            {
                var ci = (int)Math.Floor((u - g.U0) / g.H);
                var cj = (int)Math.Floor((v - g.V0) / g.H);
                for (var i = Math.Max(0, ci - k); i <= Math.Min(g.Nu - 1, ci + k); i++)
                    for (var j = Math.Max(0, cj - k); j <= Math.Min(g.Nv - 1, cj + k); j++)
                    {
                        if (!exterior[i, j]) continue;
                        // Distance from the point to the cell's square.
                        var du = Math.Max(0, Math.Abs(u - g.CU(i)) - g.H / 2);
                        var dv = Math.Max(0, Math.Abs(v - g.CV(j)) - g.H / 2);
                        if (du * du + dv * dv <= reach * reach) return true;
                    }
            }
            return false;
        }

        public bool[,] Footprint()
        {
            var m = new bool[Grid.Nu, Grid.Nv];
            for (var i = 0; i < Grid.Nu; i++)
                for (var j = 0; j < Grid.Nv; j++)
                    m[i, j] = HasPart(i, j);
            return m;
        }
    }
}

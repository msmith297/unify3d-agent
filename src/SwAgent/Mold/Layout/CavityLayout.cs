using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    public sealed class LayoutInput
    {
        public int Cavities = 1;
        /// <summary>Cavity outline extents along U and V (mm, the shrunk part's box).</summary>
        public double PartU, PartV;
        /// <summary>Steel between neighbouring cavities (mm) along U and V, and from the outer cavities to the plate edge.</summary>
        public double GapU, GapV, EdgeU, EdgeV;
        /// <summary>"CxR" (columns along U x rows along V) to force a pattern; null = the smallest footprint.</summary>
        public string Pattern;
        public bool Imperial = true;
        public string Moats = "separate";
    }

    public sealed class LayoutResult
    {
        public int Cols, Rows;
        public double FootprintL, FootprintW, RawL, RawW;
        /// <summary>Edge steel after rounding the plate up to the shop step (mm).</summary>
        public double EdgeU, EdgeV, GapU, GapV;
        public string Moats;
        /// <summary>What the agent asked for (kept in the record so the next size run lays out the same way).</summary>
        public string PatternRequest;
        public double? GapRequest;
        public List<(string Id, double U, double V)> Cavities = new List<(string, double, double)>();
        public string Pattern => Cols.ToString(CultureInfo.InvariantCulture) + "x" + Rows.ToString(CultureInfo.InvariantCulture);

        public JObject ToJson() => new JObject
        {
            ["pattern"] = "grid " + Pattern,
            ["footprint_mm"] = new JArray(Si.R(FootprintL, 6), Si.R(FootprintW, 6)),
            ["cavities"] = new JArray(Cavities.Select(c => new JObject { ["id"] = c.Id, ["origin"] = new JArray(Si.R(c.U, 6), Si.R(c.V, 6), 0), ["rotationDeg"] = 0 })),
            ["moats"] = Moats,
            ["gap_mm"] = new JArray(Si.R(GapU, 5), Si.R(GapV, 5)),
            ["edge_mm"] = new JArray(Si.R(EdgeU, 5), Si.R(EdgeV, 5)),
            ["patternRequest"] = PatternRequest,
            ["gapRequest_mm"] = GapRequest,
        };
    }

    /// <summary>
    /// Cavity layout (design 3.16 rules, design-rules 20): a centred grid, numbered C1..Cn in reading order (rows from +V,
    /// left to right), a short last row centred in U and the whole grid shifted in V by the rows' centroid (the plate grows
    /// by that shift on both sides) so the load centre stays on the plate centre and the press axis (S8). The pattern with the
    /// smallest footprint wins (then the smaller longest side, then more columns). The plate is rounded up to the shop's
    /// L x W step and the extra steel goes to the edges.
    /// </summary>
    public static class CavityLayout
    {
        public static LayoutResult Compute(LayoutInput i, MoldData data)
        {
            var options = new List<(int c, int r)>();
            if (!string.IsNullOrWhiteSpace(i.Pattern))
            {
                var p = i.Pattern.ToLowerInvariant().Replace("grid", "").Trim().Split('x');
                if (p.Length == 2 && int.TryParse(p[0].Trim(), out var pc) && int.TryParse(p[1].Trim(), out var pr) && pc * pr >= i.Cavities)
                    options.Add((pc, pr));
                else throw new ArgumentException("Pattern '" + i.Pattern + "' must be columns x rows holding " + i.Cavities + " cavities");
            }
            else
            {
                for (var r = 1; r <= i.Cavities; r++)
                {
                    var c = (int)Math.Ceiling(i.Cavities / (double)r);
                    if ((r - 1) * c >= i.Cavities) continue;   // no empty row
                    options.Add((c, r));
                }
            }
            var pitchU = i.PartU + i.GapU;
            var pitchV = i.PartV + i.GapV;
            LayoutResult best = null;
            double bestShift = 0;
            foreach (var (c, r) in options)
            {
                // a short last row moves the cavities' centroid off the middle row: shift the grid in V so the load
                // centre stays on the plate centre (S8) and widen the plate by the shift on both sides to keep the edges
                var shift = RowShift(i.Cavities, c, r, pitchV);
                var L = c * i.PartU + (c - 1) * i.GapU + 2 * i.EdgeU;
                var W = r * i.PartV + (r - 1) * i.GapV + 2 * i.EdgeV + 2 * Math.Abs(shift);
                var res = new LayoutResult { Cols = c, Rows = r, RawL = L, RawW = W, GapU = i.GapU, GapV = i.GapV, Moats = i.Moats };
                res.FootprintL = StockRounding.Planform(L, data, i.Imperial);
                res.FootprintW = StockRounding.Planform(W, data, i.Imperial);
                if (best == null) { best = res; bestShift = shift; continue; }
                var a = res.FootprintL * res.FootprintW;
                var b = best.FootprintL * best.FootprintW;
                if (a < b - 1e-6 || (Math.Abs(a - b) <= 1e-6 && (Math.Max(res.FootprintL, res.FootprintW) < Math.Max(best.FootprintL, best.FootprintW) - 1e-6 ||
                    (Math.Abs(Math.Max(res.FootprintL, res.FootprintW) - Math.Max(best.FootprintL, best.FootprintW)) <= 1e-6 && res.Cols > best.Cols))))
                {
                    best = res;
                    bestShift = shift;
                }
            }
            best.EdgeU = (best.FootprintL - (best.Cols * i.PartU + (best.Cols - 1) * i.GapU)) / 2;
            // the narrower of the two V edges after the shift
            best.EdgeV = (best.FootprintW - (best.Rows * i.PartV + (best.Rows - 1) * i.GapV)) / 2 - Math.Abs(bestShift);
            var n = 0;
            for (var row = 0; row < best.Rows && n < i.Cavities; row++)
            {
                var inRow = Math.Min(best.Cols, i.Cavities - n);
                var v = ((best.Rows - 1) / 2.0 - row) * pitchV - bestShift;
                for (var col = 0; col < inRow; col++)
                {
                    var u = (col - (inRow - 1) / 2.0) * pitchU;
                    n++;
                    best.Cavities.Add(("C" + n.ToString(CultureInfo.InvariantCulture), Clean(u), Clean(v)));
                }
            }
            return best;
        }

        private static double Clean(double x) => Math.Abs(x) < 1e-9 ? 0 : x;

        /// <summary>The V centroid of a grid filled row by row from +V (only a short last row makes it non-zero).</summary>
        public static double RowShift(int n, int cols, int rows, double pitchV)
        {
            double sum = 0;
            var left = n;
            for (var row = 0; row < rows && left > 0; row++)
            {
                var inRow = Math.Min(cols, left);
                sum += inRow * ((rows - 1) / 2.0 - row) * pitchV;
                left -= inRow;
            }
            return n > 0 ? Clean(sum / n) : 0;
        }

        /// <summary>
        /// The class A wall needed by the shop's flash standard (critique-free geometry, S25 w_geom): the moat band plus the
        /// feature clearance at a side, and at a corner the dowel - inset from both edges, one shifted by the key offset -
        /// kept clear of the moat's R0.25 outer corner. Sharp cavity corners are assumed (conservative).
        /// </summary>
        public static double ClassAWall(double lip, double groove, double cornerR, double clearance, double dowelD, double inset, double keyOffset)
        {
            var side = lip + groove + clearance;
            var c = lip + groove - cornerR;              // moat outer corner-arc centre, from the cavity corner (both axes)
            var rr = cornerR + clearance + dowelD / 2;   // dowel centre to arc centre
            // offset dowel at (w - inset - key, w - inset): (a - key)² + a² = rr², a = w - inset - c
            var a = (keyOffset + Math.Sqrt(Math.Max(0, 2 * rr * rr - keyOffset * keyOffset))) / 2;
            var corner = a + inset + c;
            return Math.Max(side, corner);
        }
    }
}

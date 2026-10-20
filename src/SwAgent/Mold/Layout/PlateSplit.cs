using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>A rectangular cavity pocket in a plate (mm, centred at U, V).</summary>
    public sealed class Pocket
    {
        public string Id;
        public double U, V, SizeU, SizeV, Depth;
        public double U0 => U - SizeU / 2;
        public double U1 => U + SizeU / 2;
        public double V0 => V - SizeV / 2;
        public double V1 => V + SizeV / 2;
        public double Volume => SizeU * SizeV * Depth;
    }

    public sealed class SplitInput
    {
        public string PlateId = "BASE";
        /// <summary>Id prefix of the backing plate the pieces key into (SUB under the base, LSUB on top of a split lid).</summary>
        public string SubplateId = "SUB";
        /// <summary>Plate centred on the origin: L along U, W along V, thickness T (mm).</summary>
        public double L, W, T;
        public List<Pocket> Pockets = new List<Pocket>();
        public double Density_kg_m3 = 2700;
        public double? MaxMass_kg;
        /// <summary>Largest blank (any order, mm) and the blank allowance per side (L/W and T).</summary>
        public double[] MaxBox;
        public double AllowLW = 3, AllowT = 1.5;
        /// <summary>Cuts stay this far from a cavity (S74: 15 mm, and clear of the moat band + feature clearance).</summary>
        public double CavityClearance = 15;
        public double FlashBand, FeatureClearance;
        /// <summary>Subplate grade and thickness rule: max(min plate, key step + min floor), rounded to stock.</summary>
        public JObject SubplateGrade;
        public double SubplateMinPlate = 12, KeyStep = 9.5, SubplateMinFloor = 12;
        public bool Imperial = true;
        public int MaxOptions = 3;
    }

    public sealed class SplitPiece
    {
        public string Id;
        public double U0, U1, V0, V1, T, Mass;
        public double[] Box;
        public bool MassOk, BoxOk;
        public bool Ok => MassOk && BoxOk;
        public List<string> Cavities = new List<string>();

        public JObject ToJson() => new JObject
        {
            ["id"] = Id, ["u_mm"] = new JArray(Si.R(U0, 6), Si.R(U1, 6)), ["v_mm"] = new JArray(Si.R(V0, 6), Si.R(V1, 6)),
            ["box_mm"] = new JArray(Box.Select(b => Si.R(b, 6))), ["mass_kg"] = Si.R(Mass, 4),
            ["mass"] = MassOk ? "pass" : "fail", ["box"] = BoxOk ? "pass" : "fail", ["cavities"] = new JArray(Cavities),
        };
    }

    public sealed class SplitOption
    {
        public List<double> CutsU = new List<double>(), CutsV = new List<double>();
        public List<SplitPiece> Pieces = new List<SplitPiece>();
        public double SubplateT, SubplateStock;
        public List<double> SubCutsU = new List<double>(), SubCutsV = new List<double>();
        public List<SplitPiece> SubPieces = new List<SplitPiece>();
        public bool SubplateOk;
        public string Note;
        public double Heaviest => Pieces.Max(p => p.Mass);

        public JObject ToJson() => new JObject
        {
            ["cutsU_mm"] = new JArray(CutsU.Select(c => Si.R(c, 6))), ["cutsV_mm"] = new JArray(CutsV.Select(c => Si.R(c, 6))),
            ["pieces"] = new JArray(Pieces.Select(p => p.ToJson())), ["heaviest_kg"] = Si.R(Heaviest, 4),
            ["subplate"] = new JObject
            {
                ["t_mm"] = Si.R(SubplateT, 6), ["stock_mm"] = Si.R(SubplateStock, 6), ["ok"] = SubplateOk,
                ["cutsU_mm"] = new JArray(SubCutsU.Select(c => Si.R(c, 6))), ["cutsV_mm"] = new JArray(SubCutsV.Select(c => Si.R(c, 6))),
                ["pieces"] = new JArray(SubPieces.Select(p => p.ToJson())),
            },
            ["note"] = Note ?? "",
        };
    }

    /// <summary>
    /// S74 splitting a plate that breaks the mass or box limit (S72/S73). Cuts are straight, full-length, in dead steel
    /// between cavity rows or columns: at least 15 mm from any cavity and clear of the flash band by the feature clearance,
    /// never through a cavity. Every combination of candidate cuts is tried; options pass when every piece meets the limits;
    /// they rank by the fewest pieces, then the lightest heaviest piece; mirror-equivalent options (same piece masses) are
    /// shown once. Pieces are keyed into a subplate (3.10), which must pass the limits itself - if it does not, it is split
    /// at lines staggered from the base joints (between them), else the option says "frame plus inserts or a hoist".
    /// </summary>
    public static class PlateSplit
    {
        public static List<SplitOption> Options(SplitInput i, MoldData data)
        {
            var clear = Math.Max(i.CavityClearance, i.FlashBand + i.FeatureClearance);
            var cu = Candidates(i.Pockets.Select(p => (p.U0, p.U1)).ToList(), -i.L / 2, i.L / 2, clear);
            var cv = Candidates(i.Pockets.Select(p => (p.V0, p.V1)).ToList(), -i.W / 2, i.W / 2, clear);
            var all = new List<SplitOption>();
            var nU = Math.Min(cu.Count, 10);
            var nV = Math.Min(cv.Count, 10);
            for (var mu = 0; mu < (1 << nU); mu++)
                for (var mv = 0; mv < (1 << nV); mv++)
                {
                    if (mu == 0 && mv == 0) continue;
                    var o = new SplitOption();
                    for (var k = 0; k < nU; k++) if ((mu & (1 << k)) != 0) o.CutsU.Add(cu[k]);
                    for (var k = 0; k < nV; k++) if ((mv & (1 << k)) != 0) o.CutsV.Add(cv[k]);
                    o.Pieces = Pieces(i, o.CutsU, o.CutsV, i.T, i.PlateId, true);
                    if (o.Pieces.All(p => p.Ok)) all.Add(o);
                }
            var ranked = all.OrderBy(o => o.Pieces.Count).ThenBy(o => o.Heaviest).ThenBy(o => o.CutsU.Count + o.CutsV.Count)
                            .ThenBy(o => string.Join(",", o.CutsU.Concat(o.CutsV).Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)))).ToList();
            var seen = new HashSet<string>();
            var result = new List<SplitOption>();
            foreach (var o in ranked)
            {
                var sig = o.Pieces.Count + ":" + string.Join(",", o.Pieces.Select(p => Math.Round(p.Mass, 3)).OrderBy(x => x).Select(x => x.ToString(CultureInfo.InvariantCulture)));
                if (!seen.Add(sig)) continue;
                Subplate(i, o, data);
                result.Add(o);
                if (result.Count >= i.MaxOptions) break;
            }
            return result;
        }

        /// <summary>Cut positions along one axis: the middle of each feasible window between neighbouring pocket extents.</summary>
        public static List<double> Candidates(List<(double Lo, double Hi)> extents, double min, double max, double clearance)
        {
            var sorted = extents.OrderBy(e => e.Lo).ToList();
            // merge overlapping extents (a cut may never cross a cavity anywhere along its length)
            var merged = new List<(double Lo, double Hi)>();
            foreach (var e in sorted)
            {
                if (merged.Count > 0 && e.Lo <= merged[merged.Count - 1].Hi) merged[merged.Count - 1] = (merged[merged.Count - 1].Lo, Math.Max(merged[merged.Count - 1].Hi, e.Hi));
                else merged.Add(e);
            }
            var cuts = new List<double>();
            for (var k = 0; k < merged.Count - 1; k++)
            {
                var a = merged[k].Hi + clearance;
                var b = merged[k + 1].Lo - clearance;
                if (b >= a - 1e-9) cuts.Add(Clean(0.5 * (a + b)));
            }
            return cuts.Where(c => c > min && c < max).ToList();
        }

        private static double Clean(double x) => Math.Abs(x) < 1e-9 ? 0 : x;

        public static List<SplitPiece> Pieces(SplitInput i, List<double> cutsU, List<double> cutsV, double t, string prefix, bool withPockets)
        {
            var us = new[] { -i.L / 2 }.Concat(cutsU.OrderBy(x => x)).Concat(new[] { i.L / 2 }).ToList();
            var vs = new[] { -i.W / 2 }.Concat(cutsV.OrderBy(x => x)).Concat(new[] { i.W / 2 }).ToList();
            var list = new List<SplitPiece>();
            var n = 0;
            // reading order: rows from +V, left to right
            for (var b = vs.Count - 2; b >= 0; b--)
                for (var a = 0; a < us.Count - 1; a++)
                {
                    var p = new SplitPiece { Id = prefix + "-" + (++n).ToString(CultureInfo.InvariantCulture), U0 = us[a], U1 = us[a + 1], V0 = vs[b], V1 = vs[b + 1], T = t };
                    var vol = (p.U1 - p.U0) * (p.V1 - p.V0) * t;
                    if (withPockets)
                        foreach (var k in i.Pockets.Where(k => k.U >= p.U0 && k.U <= p.U1 && k.V >= p.V0 && k.V <= p.V1))
                        {
                            vol -= k.Volume;
                            p.Cavities.Add(k.Id);
                        }
                    p.Mass = vol * i.Density_kg_m3 * 1e-9;
                    p.Box = new[] { p.U1 - p.U0, p.V1 - p.V0, t };
                    p.MassOk = i.MaxMass_kg == null || p.Mass <= i.MaxMass_kg.Value + 1e-9;
                    p.BoxOk = BoxFits(p.Box, i.MaxBox, i.AllowLW, i.AllowT);
                    list.Add(p);
                }
            return list;
        }

        /// <summary>(S72) the finished box plus the blank allowance, sorted, fits the sorted machine limit component by component.</summary>
        public static bool BoxFits(double[] box, double[] max, double allowLW, double allowT)
        {
            if (max == null || max.Length < 3) return true;
            var b = new[] { box[0] + 2 * allowLW, box[1] + 2 * allowLW, box[2] + 2 * allowT }.OrderByDescending(x => x).ToArray();
            var m = max.OrderByDescending(x => x).ToArray();
            for (var k = 0; k < 3; k++) if (b[k] > m[k] + 1e-9) return false;
            return true;
        }

        private static void Subplate(SplitInput i, SplitOption o, MoldData data)
        {
            var req = Math.Max(i.SubplateMinPlate, i.KeyStep + i.SubplateMinFloor);
            var st = StockRounding.Thickness(req, i.SubplateGrade, data, i.Imperial);
            o.SubplateT = st.Finished;
            o.SubplateStock = st.Stock;
            var whole = Pieces(i, new List<double>(), new List<double>(), o.SubplateT, i.SubplateId, false);
            if (whole.All(p => p.Ok))
            {
                o.SubPieces = whole;
                o.SubplateOk = true;
                return;
            }
            // staggered: halfway between the base joints (and the plate edges) along each axis
            List<double> Stagger(List<double> cuts, double half)
            {
                var xs = new[] { -half }.Concat(cuts.OrderBy(x => x)).Concat(new[] { half }).ToList();
                var mids = new List<double>();
                for (var k = 0; k < xs.Count - 1; k++) mids.Add(Clean(0.5 * (xs[k] + xs[k + 1])));
                return mids;
            }
            var su = Stagger(o.CutsU, i.L / 2);
            var sv = Stagger(o.CutsV, i.W / 2);
            SplitOption best = null;
            for (var mu = 0; mu < (1 << Math.Min(su.Count, 8)); mu++)
                for (var mv = 0; mv < (1 << Math.Min(sv.Count, 8)); mv++)
                {
                    if (mu == 0 && mv == 0) continue;
                    var cand = new SplitOption();
                    for (var k = 0; k < su.Count && k < 8; k++) if ((mu & (1 << k)) != 0) cand.SubCutsU.Add(su[k]);
                    for (var k = 0; k < sv.Count && k < 8; k++) if ((mv & (1 << k)) != 0) cand.SubCutsV.Add(sv[k]);
                    cand.SubPieces = Pieces(i, cand.SubCutsU, cand.SubCutsV, o.SubplateT, i.SubplateId, false);
                    if (!cand.SubPieces.All(p => p.Ok)) continue;
                    if (best == null || cand.SubPieces.Count < best.SubPieces.Count ||
                        (cand.SubPieces.Count == best.SubPieces.Count && cand.SubPieces.Max(p => p.Mass) < best.SubPieces.Max(p => p.Mass) - 1e-9))
                        best = cand;
                }
            if (best != null)
            {
                o.SubCutsU = best.SubCutsU;
                o.SubCutsV = best.SubCutsV;
                o.SubPieces = best.SubPieces;
                o.SubplateOk = true;
                o.Note = "subplate split at lines staggered from the base joints";
            }
            else
            {
                o.SubPieces = whole;
                o.SubplateOk = false;
                o.Note = "the subplate breaks the limits even when split: propose a frame plus cavity inserts, or a hoist";
            }
        }
    }
}

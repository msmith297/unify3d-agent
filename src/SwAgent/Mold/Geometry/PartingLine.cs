using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Mold.Geometry
{
    /// <summary>A tessellation sample of the part: an area-weighted triangle centroid, in the mold frame.</summary>
    public sealed class PartSample
    {
        public int Face;
        public double U, V, W;
        /// <summary>n . W of the outward normal.</summary>
        public double NW;
        public double Area;
        /// <summary>Ray along +W from the sample leaves the part without a hit (null = not cast).</summary>
        public bool? UpClear;
        /// <summary>Ray along -W leaves the part without a hit (null = not cast).</summary>
        public bool? DownClear;
        /// <summary>W range of the area the sample stands for (NaN: just W).</summary>
        public double WLo = double.NaN, WHi = double.NaN;
        /// <summary>
        /// Lowest W where that area faces the lid and highest where it faces the base, from the vertices' own normals
        /// (+inf / -inf: nowhere; NaN: not known, then the sample's class at W is used).
        /// </summary>
        public double LidMinW = double.NaN, BaseMaxW = double.NaN;

        public double Lo => double.IsNaN(WLo) ? W : WLo;
        public double Hi => double.IsNaN(WHi) ? W : WHi;
    }

    public enum SampleSide
    {
        /// <summary>Faces the lid (n . W >= sin(min draft)) and the lid can pull away from it: must be above the parting line.</summary>
        LidOnly,
        /// <summary>Faces the base and the base can pull away: must be below the parting line.</summary>
        BaseOnly,
        /// <summary>|n . W| below sin(min draft): may sit on either side; needs draft wherever it goes.</summary>
        Either,
        /// <summary>Faces one way but the pull is blocked by the part itself: side action or an insert.</summary>
        Undercut,
    }

    /// <summary>
    /// Planar parting-line candidates, classified per tessellation sample (critique A7), not per face: a face may span
    /// both halves (a sphere, an O-ring, a 0 deg wall). z_lo = the highest point that faces the base, z_hi = the lowest
    /// that faces the lid (undercut samples excluded); a planar parting plane fits anywhere in [z_lo, z_hi]. The extents
    /// come from the samples' vertex bounds (PartSample.LidMinW / BaseMaxW, which lie on the true surface), so they are
    /// exact to the model's tolerance, not to the sample spacing. Pure (GeometryPartingLineTests).
    /// </summary>
    public static class PartingLine
    {
        public static SampleSide Classify(PartSample s, double sinMin)
        {
            if (s.NW >= sinMin) return s.UpClear == false ? SampleSide.Undercut : SampleSide.LidOnly;
            if (s.NW <= -sinMin) return s.DownClear == false ? SampleSide.Undercut : SampleSide.BaseOnly;
            return SampleSide.Either;
        }

        /// <summary>The lowest W where a (non-undercut) sample faces the lid, +inf if nowhere.</summary>
        private static double LidLow(PartSample s, SampleSide side) =>
            side == SampleSide.Undercut ? double.PositiveInfinity : !double.IsNaN(s.LidMinW) ? s.LidMinW : side == SampleSide.LidOnly ? s.W : double.PositiveInfinity;

        /// <summary>The highest W where a (non-undercut) sample faces the base, -inf if nowhere.</summary>
        private static double BaseHigh(PartSample s, SampleSide side) =>
            side == SampleSide.Undercut ? double.NegativeInfinity : !double.IsNaN(s.BaseMaxW) ? s.BaseMaxW : side == SampleSide.BaseOnly ? s.W : double.NegativeInfinity;

        public sealed class Candidate
        {
            public string Id;
            public double W;
            public string Why;
            public double LidDepth, BaseDepth;
            /// <summary>Area of samples that need draft on each side of this plane.</summary>
            public double DraftAreaLid, DraftAreaBase;
            public bool Veto;
            public string VetoReason;
            /// <summary>Subtracted from the score (e.g. a shut-off on a critical face inside the outline), with the reason.</summary>
            public double Penalty;
            public string PenaltyReason;
            public int AirTraps;
            public double AirTrapVolume;
            public double Score;
            public List<string> ScoreNotes = new List<string>();
        }

        public sealed class Result
        {
            public double WMin, WMax;
            public double ZLo, ZHi;
            public bool Planar;
            public double TotalArea;
            public Dictionary<SampleSide, double> AreaBySide = new Dictionary<SampleSide, double>();
            /// <summary>Faces with lid-facing points below z_lo or base-facing points above z_hi (no planar parting line).</summary>
            public List<int> ConflictFaces = new List<int>();
            public List<int> UndercutFaces = new List<int>();
            public List<Candidate> Candidates = new List<Candidate>();
        }

        /// <summary>
        /// Analyze the samples. `tol` is the geometric tolerance (the model's, e.g. 0.01 mm: the extents are exact);
        /// `minSeparation` drops a candidate closer than that to one already listed (the sample spacing).
        /// `sectionArea(w)` is the part's cross-section at a height (from the column rays) for the "largest outline"
        /// candidate; `critical` are the W ranges of critical faces. A candidate whose plane meets a critical face is
        /// vetoed when `reachesOutline(id, w)` says the face reaches the part's outer outline at that level (the parting
        /// line runs on it), or is null; otherwise (a hole wall or pocket inside the outline: a shut-off) it loses 0.2.
        /// </summary>
        public static Result Analyze(IList<PartSample> samples, double minDraftDeg, double tol, Func<double, double> sectionArea = null,
            IList<(string Id, double WMin, double WMax)> critical = null, double minSeparation = 0, Func<string, double, bool> reachesOutline = null)
        {
            if (samples == null || samples.Count == 0) throw new ArgumentException("no samples");
            var sinMin = Math.Sin(minDraftDeg * Math.PI / 180);
            var r = new Result { WMin = samples.Min(s => s.Lo), WMax = samples.Max(s => s.Hi), TotalArea = samples.Sum(s => s.Area) };
            foreach (SampleSide k in Enum.GetValues(typeof(SampleSide))) r.AreaBySide[k] = 0;
            var sides = samples.Select(s => Classify(s, sinMin)).ToList();
            for (var i = 0; i < samples.Count; i++) r.AreaBySide[sides[i]] += samples[i].Area;

            var lidLow = samples.Select((s, i) => LidLow(s, sides[i])).ToList();
            var baseHigh = samples.Select((s, i) => BaseHigh(s, sides[i])).ToList();
            var zHi = lidLow.Min();
            var zLo = baseHigh.Max();
            r.ZLo = double.IsNegativeInfinity(zLo) ? r.WMin : zLo;
            r.ZHi = double.IsPositiveInfinity(zHi) ? r.WMax : zHi;
            r.Planar = r.ZLo <= r.ZHi + tol;
            r.UndercutFaces = samples.Where((s, i) => sides[i] == SampleSide.Undercut).Select(s => s.Face).Distinct().OrderBy(f => f).ToList();
            if (!r.Planar)
            {
                r.ConflictFaces = samples.Where((s, i) => lidLow[i] < r.ZLo - tol || baseHigh[i] > r.ZHi + tol)
                    .Select(s => s.Face).Distinct().OrderBy(f => f).ToList();
                return r;
            }

            // Candidates: the top of the feasible band (part in the base, flat lid), its bottom (part in the lid, flat
            // base) and the level of the largest outline, if it is somewhere else.
            var lo = Math.Min(r.ZLo, r.ZHi);
            var hi = Math.Max(r.ZLo, r.ZHi);
            var sep = Math.Max(tol, minSeparation);
            var levels = new List<(string Id, double W, string Why)>();
            if (hi - lo <= sep)
            {
                // A single feasible level (a drafted puck: its top; a sphere or O-ring: the equator).
                var w = (lo + hi) / 2;
                if (w >= r.WMax - sep) levels.Add(("top", r.WMax, "the only planar level, the part's top: the part sits in the base, the lid is flat"));
                else if (w <= r.WMin + sep) levels.Add(("bottom", r.WMin, "the only planar level, the part's bottom: the part sits in the lid, the base is flat"));
                else levels.Add(("equator", w, "the only planar level (the part's widest section)"));
            }
            else
            {
                levels.Add(("top", hi, "top of the feasible band: the part sits in the base, the lid is flat"));
                levels.Add(("bottom", lo, "bottom of the feasible band: the part sits in the lid, the base is flat"));
                if (sectionArea != null)
                {
                    const int N = 40;
                    var areas = Enumerable.Range(0, N + 1).Select(k => (W: lo + (hi - lo) * k / N, A: sectionArea(lo + (hi - lo) * k / N))).ToList();
                    var max = areas.Max(a => a.A);
                    // The middle of the plateau of near-maximal sections (a straight wall gives a plateau).
                    var plateau = areas.Where(a => a.A >= max * 0.995).ToList();
                    var mid = (plateau.First().W + plateau.Last().W) / 2;
                    if (levels.All(l => Math.Abs(l.W - mid) > sep)) levels.Add(("outline", mid, "level of the largest outline"));
                }
            }

            foreach (var (id, w, why) in levels)
            {
                var c = new Candidate { Id = id, W = w, Why = why, LidDepth = r.WMax - w, BaseDepth = w - r.WMin };
                for (var i = 0; i < samples.Count; i++)
                {
                    if (sides[i] != SampleSide.Either) continue;
                    var s = samples[i];
                    // The share of the sample's area above the plane (its W range split linearly).
                    var up = s.Hi - s.Lo > 1e-15 ? Math.Max(0, Math.Min(1, (s.Hi - w) / (s.Hi - s.Lo))) : s.W >= w ? 1 : 0;
                    c.DraftAreaLid += s.Area * up;
                    c.DraftAreaBase += s.Area * (1 - up);
                }
                if (critical != null)
                {
                    foreach (var f in critical.Where(f => f.WMin - tol <= w && w <= f.WMax + tol))
                    {
                        if (reachesOutline == null || reachesOutline(f.Id, w))
                        {
                            c.Veto = true;
                            c.VetoReason = $"the parting line would run on critical face {f.Id}";
                            break;
                        }
                        if (c.Penalty == 0)
                        {
                            c.Penalty = 0.2;
                            c.PenaltyReason = $"shut-off on critical face {f.Id} (inside the outline)";
                        }
                    }
                }
                r.Candidates.Add(c);
            }
            return r;
        }

        /// <summary>
        /// Score candidates once their air traps are known: 1.0, minus the share of the surface needing draft, minus 0.1
        /// per air trap, minus any penalty, plus 0.1 when the part sits on the pour side ("base" or "lid"); 0 when vetoed.
        /// Highest first.
        /// </summary>
        public static void Score(Result r, string pourSide, double partVolume = 0)
        {
            var height = Math.Max(1e-12, r.WMax - r.WMin);
            foreach (var c in r.Candidates)
            {
                c.ScoreNotes.Clear();
                if (c.Veto)
                {
                    c.Score = 0;
                    c.ScoreNotes.Add("vetoed: " + c.VetoReason);
                    continue;
                }
                var s = 1.0;
                var draft = r.TotalArea > 0 ? (c.DraftAreaLid + c.DraftAreaBase) / r.TotalArea : 0;
                if (draft > 0)
                {
                    s -= draft;
                    c.ScoreNotes.Add($"-{draft:0.00} needs draft");
                }
                if (c.AirTraps > 0)
                {
                    s -= 0.1 * c.AirTraps;
                    c.ScoreNotes.Add($"-{0.1 * c.AirTraps:0.0} air traps");
                    if (partVolume > 0 && c.AirTrapVolume > 0)
                    {
                        // A pour side that can't hold the part (everything above the plane) traps most of it.
                        var share = Math.Min(0.5, c.AirTrapVolume / partVolume);
                        s -= share;
                        c.ScoreNotes.Add($"-{share:0.00} trapped volume");
                    }
                }
                if (c.Penalty > 0)
                {
                    s -= c.Penalty;
                    c.ScoreNotes.Add($"-{c.Penalty:0.0} {c.PenaltyReason}");
                }
                var inBase = c.BaseDepth / height;
                var onPourSide = pourSide == "base" ? inBase : pourSide == "lid" ? 1 - inBase : 0;
                if (onPourSide > 0.999)
                {
                    s += 0.1;
                    c.ScoreNotes.Add("+0.1 part on the pour side");
                }
                if (c.Id == "outline" || c.Id == "equator")
                {
                    s += 0.02; // the widest section: the shallowest halves and no hidden undercut at the edge
                    c.ScoreNotes.Add("+0.02 widest section");
                }
                c.Score = Math.Max(0, Math.Round(s, 3));
            }
            var ordered = r.Candidates.OrderByDescending(c => c.Score).ToList();
            r.Candidates.Clear();
            r.Candidates.AddRange(ordered);
        }
    }
}

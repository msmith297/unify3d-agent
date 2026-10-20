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
    /// both halves (a sphere, an O-ring, a 0 deg wall). z_lo = the highest base-only sample, z_hi = the lowest lid-only
    /// sample; a planar parting plane fits anywhere in [z_lo, z_hi]. Pure (GeometryPartingLineTests).
    /// </summary>
    public static class PartingLine
    {
        public static SampleSide Classify(PartSample s, double sinMin)
        {
            if (s.NW >= sinMin) return s.UpClear == false ? SampleSide.Undercut : SampleSide.LidOnly;
            if (s.NW <= -sinMin) return s.DownClear == false ? SampleSide.Undercut : SampleSide.BaseOnly;
            return SampleSide.Either;
        }

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
            /// <summary>Faces with lid-only samples below z_lo or base-only samples above z_hi (no planar parting line).</summary>
            public List<int> ConflictFaces = new List<int>();
            public List<int> UndercutFaces = new List<int>();
            public List<Candidate> Candidates = new List<Candidate>();
        }

        /// <summary>
        /// Analyze the samples. `sectionArea(w)` is the part's cross-section at a height (from the column rays) for the
        /// "largest outline" candidate; `critical` are the W ranges of critical faces (a candidate touching one is vetoed).
        /// </summary>
        public static Result Analyze(IList<PartSample> samples, double minDraftDeg, double tol, Func<double, double> sectionArea = null,
            IList<(string Id, double WMin, double WMax)> critical = null)
        {
            if (samples == null || samples.Count == 0) throw new ArgumentException("no samples");
            var sinMin = Math.Sin(minDraftDeg * Math.PI / 180);
            var r = new Result { WMin = samples.Min(s => s.W), WMax = samples.Max(s => s.W), TotalArea = samples.Sum(s => s.Area) };
            foreach (SampleSide k in Enum.GetValues(typeof(SampleSide))) r.AreaBySide[k] = 0;
            var sides = samples.Select(s => Classify(s, sinMin)).ToList();
            for (var i = 0; i < samples.Count; i++) r.AreaBySide[sides[i]] += samples[i].Area;

            var baseOnly = samples.Where((s, i) => sides[i] == SampleSide.BaseOnly).ToList();
            var lidOnly = samples.Where((s, i) => sides[i] == SampleSide.LidOnly).ToList();
            r.ZLo = baseOnly.Count > 0 ? baseOnly.Max(s => s.W) : r.WMin;
            r.ZHi = lidOnly.Count > 0 ? lidOnly.Min(s => s.W) : r.WMax;
            r.Planar = r.ZLo <= r.ZHi + tol;
            r.UndercutFaces = samples.Where((s, i) => sides[i] == SampleSide.Undercut).Select(s => s.Face).Distinct().OrderBy(f => f).ToList();
            if (!r.Planar)
            {
                r.ConflictFaces = samples.Where((s, i) => (sides[i] == SampleSide.LidOnly && s.W < r.ZLo - tol) || (sides[i] == SampleSide.BaseOnly && s.W > r.ZHi + tol))
                    .Select(s => s.Face).Distinct().OrderBy(f => f).ToList();
                return r;
            }

            // Candidates: the top of the feasible band (part in the base, flat lid), its bottom (part in the lid, flat
            // base) and the level of the largest outline, if it is somewhere else.
            var lo = Math.Min(r.ZLo, r.ZHi);
            var hi = Math.Max(r.ZLo, r.ZHi);
            var levels = new List<(string Id, double W, string Why)>
            {
                ("top", hi, "top of the feasible band: the part sits in the base, the lid is flat"),
                ("bottom", lo, "bottom of the feasible band: the part sits in the lid, the base is flat"),
            };
            if (sectionArea != null && hi - lo > tol)
            {
                const int N = 40;
                var areas = Enumerable.Range(0, N + 1).Select(k => (W: lo + (hi - lo) * k / N, A: sectionArea(lo + (hi - lo) * k / N))).ToList();
                var max = areas.Max(a => a.A);
                // The middle of the plateau of near-maximal sections (a straight wall gives a plateau).
                var plateau = areas.Where(a => a.A >= max * 0.995).ToList();
                var mid = (plateau.First().W + plateau.Last().W) / 2;
                if (levels.All(l => Math.Abs(l.W - mid) > tol)) levels.Add(("outline", mid, "level of the largest outline"));
            }
            else if (hi - lo <= tol)
            {
                levels.RemoveAt(1); // a single feasible level (sphere, O-ring: the equator)
                levels[0] = ("equator", (lo + hi) / 2, "the only planar level (the part's widest section)");
            }

            foreach (var (id, w, why) in levels)
            {
                var c = new Candidate { Id = id, W = w, Why = why, LidDepth = r.WMax - w, BaseDepth = w - r.WMin };
                for (var i = 0; i < samples.Count; i++)
                {
                    if (sides[i] != SampleSide.Either) continue;
                    if (samples[i].W >= w) c.DraftAreaLid += samples[i].Area;
                    else c.DraftAreaBase += samples[i].Area;
                }
                if (critical != null)
                {
                    var hit = critical.FirstOrDefault(f => f.WMin - tol <= w && w <= f.WMax + tol);
                    if (hit.Id != null)
                    {
                        c.Veto = true;
                        c.VetoReason = $"touches critical face {hit.Id}";
                    }
                }
                r.Candidates.Add(c);
            }
            return r;
        }

        /// <summary>
        /// Score candidates once their air traps are known: 1.0, minus the share of the surface needing draft, minus 0.1
        /// per air trap, plus 0.1 when the part sits on the pour side ("base" or "lid"); 0 when vetoed. Highest first.
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

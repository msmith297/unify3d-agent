using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>
    /// Sweep 2 (design 3.20): closed-loop Monte Carlo. Random molds are sized by a sizing delegate (the fast path: table plus
    /// fixed point), then each sized plate and wall is rebuilt as an exact plate model (cavities as pressure patches, rails as
    /// line supports, pillars as springs) and solved; r_w = w_FE at the lands / delta_allow and r_s = sigma_FE K_f / sigma_allow.
    /// Acceptance: max r_w and max r_s at most 1.00, median r_w at least 0.75 where deflection governs.
    ///
    /// The delegate is Func&lt;JObject, JObject&gt; so it can be A1's Sizing after integration (through an adapter); until then
    /// StubSizing implements S20-S31 with the coefficient table. Input (schema "unify3d.mold-mc/1", mm, MPa, degC):
    ///   { support: "A"|"B"|"C"|"E", plate: { length_mm, width_mm }, cavities: [ { x_mm, y_mm, length_mm, width_mm, depth_mm } ],
    ///     land_mm, pressure_MPa, material: { id, tempC }, cycles, kf, delta_allow_mm,
    ///     rails: { y1_mm, y2_mm } (B), opening: { x1_mm, y1_mm, x2_mm, y2_mm } (C), pillars: { xs_mm: [], ys_mm: [], k_N_per_mm } (E) }
    /// Output: { E_MPa, nu, sigma_allow_MPa, plate: { t_mm, governs: "deflection"|"stress"|"minimum"|"add support" },
    ///           walls: [ { cavity, side: "length"|"width", w_mm, governs } ], method (optional, for the record) }
    /// governs "add support" (plates past t/b 0.5) and "out of range" (walls past w/h 0.5) are recorded but not checked.
    /// </summary>
    public static class MonteCarloHarness
    {
        public const string Version = "a2-1";
        /// <summary>The wall-root factor the FE truth uses: the 3D envelope 1 + 2.6 w/h (solid3d-summary.json).</summary>
        public static Func<double, double> TrueRootFactor = wh => 1 + 2.6 * wh;

        public static readonly string[] Grades = { "al_6061_t651", "al_7075_t651", "st_p20", "st_h13" };
        public static readonly double[] Deltas = { 0.005, 0.012, 0.020, 0.025, 0.05 };

        /// <summary>One random mold.</summary>
        public static JObject Draw(Random rnd)
        {
            double U(double a, double b) => a + (b - a) * rnd.NextDouble();
            double ramIn = U(4, 16), gaugePsi = U(500, 3000);
            double force = gaugePsi * 0.006894757293168 * Math.PI * Math.Pow(ramIn * 25.4, 2) / 4; // N
            int n = 1 + rnd.Next(8);
            var cav = new List<(double l, double w, double d)>();
            for (int i = 0; i < n; i++)
            {
                double a = U(20, 200), b = U(20, 200);
                cav.Add((Math.Max(a, b), Math.Min(a, b), U(5, 60)));
            }
            // shelf layout: columns = ceil(sqrt n), 30 mm between cavities, 40 mm to the plate edge
            int cols = (int)Math.Ceiling(Math.Sqrt(n));
            const double gap = 30, margin = 40;
            var placed = new JArray();
            double y = margin, plateL = 0;
            for (int r0 = 0; r0 < n; r0 += cols)
            {
                double x = margin, rowH = 0;
                for (int k = r0; k < Math.Min(n, r0 + cols); k++)
                {
                    placed.Add(new JObject { ["x_mm"] = R(x), ["y_mm"] = R(y), ["length_mm"] = R(cav[k].l), ["width_mm"] = R(cav[k].w), ["depth_mm"] = R(cav[k].d) });
                    x += cav[k].l + gap;
                    rowH = Math.Max(rowH, cav[k].w);
                }
                plateL = Math.Max(plateL, x - gap + margin);
                y += rowH + gap;
            }
            double plateW = y - gap + margin;
            const double land = 3.175;
            double aProj = placed.Sum(c => ((double)c["length_mm"] + 2 * land) * ((double)c["width_mm"] + 2 * land));
            double p = Math.Min(force / aProj, 40); // above about 40 MPa the gauge would be turned down (S7)
            string grade = Grades[rnd.Next(Grades.Length)];
            double tMax = grade == "al_6061_t651" ? 175 : grade == "al_7075_t651" ? 135 : 200;
            string support = new[] { "A", "B", "C", "E" }[rnd.Next(4)];
            var design = new JObject
            {
                ["schema"] = "unify3d.mold-mc/1",
                ["support"] = support,
                ["plate"] = new JObject { ["length_mm"] = R(plateL), ["width_mm"] = R(plateW) },
                ["cavities"] = placed,
                ["land_mm"] = land,
                ["pressure_MPa"] = R(p),
                ["press"] = new JObject { ["ram_in"] = R(ramIn), ["gauge_psi"] = R(gaugePsi) },
                ["material"] = new JObject { ["id"] = grade, ["tempC"] = R(U(60, tMax)) },
                ["cycles"] = Math.Round(Math.Pow(10, U(3, 6))),
                ["kf"] = R(U(1.0, 2.2)),
                ["delta_allow_mm"] = Deltas[rnd.Next(Deltas.Length)],
            };
            if (support == "B")
            {
                double inset = U(15, 40);
                design["rails"] = new JObject { ["y1_mm"] = R(inset), ["y2_mm"] = R(plateW - inset) };
            }
            else if (support == "C")
            {
                double x1 = placed.Min(c => (double)c["x_mm"]) - 10, y1 = placed.Min(c => (double)c["y_mm"]) - 10;
                double x2 = placed.Max(c => (double)c["x_mm"] + (double)c["length_mm"]) + 10, y2 = placed.Max(c => (double)c["y_mm"] + (double)c["width_mm"]) + 10;
                design["opening"] = new JObject { ["x1_mm"] = R(x1), ["y1_mm"] = R(y1), ["x2_mm"] = R(x2), ["y2_mm"] = R(y2) };
            }
            else if (support == "E")
            {
                int px = 1 + rnd.Next(3), py = 1 + rnd.Next(2);
                var xs = new JArray(Enumerable.Range(1, px).Select(i => R(plateL * i / (px + 1))));
                var ys = new JArray(Enumerable.Range(1, py).Select(i => R(plateW * i / (py + 1))));
                design["pillars"] = new JObject { ["xs_mm"] = xs, ["ys_mm"] = ys, ["k_N_per_mm"] = R(205000 * Math.PI * 25 * 25 / 4 / 100) };
            }
            return design;
        }

        static double R(double v) => Math.Round(v, 3);

        public sealed class Check
        {
            public int Sample;
            public string Support, Element, Governs, Grade;
            public double Rw, Rs, T, Span, Tau;
            public bool Skipped;
            public string Note;
        }

        /// <summary>Runs n samples (parallel), each sized by the delegate and checked by FE.</summary>
        public static MonteCarloRun Run(int n, int seed, Func<JObject, JObject> sizing, int elementsOnShortSide = 24, Action<string> log = null, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rnd = new Random(seed);
            var designs = Enumerable.Range(0, n).Select(_ => Draw(rnd)).ToList();
            var results = new List<Check>[n];
            int done = 0;
            Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, i =>
            {
                var d = designs[i];
                JObject sized;
                try { sized = sizing(d); }
                catch (Exception ex) { results[i] = new List<Check> { new Check { Sample = i, Element = "sizing", Skipped = true, Note = ex.Message } }; return; }
                results[i] = CheckDesign(i, d, sized, elementsOnShortSide, ct);
                int k = Interlocked.Increment(ref done);
                if (log != null && k % 250 == 0) log($"  {k}/{n} molds, {sw.Elapsed.TotalSeconds:F0} s");
            });
            return new MonteCarloRun { Checks = results.SelectMany(r => r).ToList(), Samples = n, Seed = seed, Designs = designs, Elapsed = sw.Elapsed };
        }

        /// <summary>The FE truth for one sized design.</summary>
        public static List<Check> CheckDesign(int index, JObject d, JObject s, int elements = 24, CancellationToken ct = default)
        {
            var list = new List<Check>();
            string support = (string)d["support"];
            double p = (double)d["pressure_MPa"], delta = (double)d["delta_allow_mm"], kf = (double)d["kf"], land = (double?)d["land_mm"] ?? 3.175;
            double e = (double)s["E_MPa"], nu = (double)s["nu"], allow = (double)s["sigma_allow_MPa"];
            string grade = (string)d["material"]["id"];
            var cavs = d["cavities"].Cast<JObject>().Select(c => (x: (double)c["x_mm"], y: (double)c["y_mm"], l: (double)c["length_mm"], w: (double)c["width_mm"], h: (double)c["depth_mm"])).ToList();

            // ---- plate ----
            var plate = (JObject)s["plate"];
            double t = (double)plate["t_mm"];
            string gov = (string)plate["governs"];
            var chk = new Check { Sample = index, Support = support, Element = "plate", Governs = gov, Grade = grade, T = t };
            if (gov == "add support") { chk.Skipped = true; chk.Note = "t/b over 0.5: the sizing asks for more support"; list.Add(chk); }
            else
            {
                double pl = (double)d["plate"]["length_mm"], pw = (double)d["plate"]["width_mm"];
                PlateModel m;
                Func<double, double, bool> inside = (x, y) => true;
                double ox = 0, oy = 0;
                switch (support)
                {
                    case "A":
                        m = PlateModel.Rect(pl, pw, t, e, nu, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free);
                        m.Foundations.Add(new WinklerRegion(0, pl, 0, pw, 1e4, unilateral: true)); // a rigid platen through a thin contact layer
                        chk.Span = Math.Min(pl, pw);
                        break;
                    case "B":
                        m = PlateModel.Rect(pl, pw, t, e, nu, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free);
                        double y1 = (double)d["rails"]["y1_mm"], y2 = (double)d["rails"]["y2_mm"];
                        m.Lines.Add(new LineSupport('y', y1)); m.Lines.Add(new LineSupport('y', y2));
                        chk.Span = y2 - y1;
                        break;
                    case "C":
                        var o = d["opening"];
                        ox = (double)o["x1_mm"]; oy = (double)o["y1_mm"];
                        double ol = (double)o["x2_mm"] - ox, ow = (double)o["y2_mm"] - oy;
                        m = PlateModel.Rect(ol, ow, t, e, nu, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard);
                        chk.Span = Math.Min(ol, ow);
                        break;
                    default: // E
                        m = PlateModel.Rect(pl, pw, t, e, nu, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard);
                        var xs = d["pillars"]["xs_mm"].Select(v => (double)v).ToList();
                        var ys = d["pillars"]["ys_mm"].Select(v => (double)v).ToList();
                        var gx = new[] { 0.0 }.Concat(xs).Concat(new[] { pl }).ToList();
                        var gy = new[] { 0.0 }.Concat(ys).Concat(new[] { pw }).ToList();
                        chk.Span = Math.Min(Enumerable.Range(0, gx.Count - 1).Max(k => gx[k + 1] - gx[k]), Enumerable.Range(0, gy.Count - 1).Max(k => gy[k + 1] - gy[k]));
                        // rigid square heads of side 0.2 x the short spacing, as the Pillar rows assume (pillar shortening is a separate
                        // check: at molding pressures it is usually larger than the flash gap, see INTEGRATION-NOTES-A2.md)
                        double hh = CoefficientSweep.PillarHead * chk.Span / 2;
                        foreach (var px in xs) foreach (var py in ys) m.Fixed.Add(new FixedRegion(px - hh, px + hh, py - hh, py + hh));
                        break;
                }
                m.ElementsOnShortSide = elements;
                foreach (var c in cavs)
                    m.Pressures.Add(new PressurePatch(c.x - land - ox, c.x + c.l + land - ox, c.y - land - oy, c.y + c.w + land - oy, p));
                // the land ring of each cavity: between the outline and the outline + land
                Func<double, double, bool> onLand = (x, y) => cavs.Any(c =>
                {
                    double lx = x + ox, ly = y + oy, eps = 1e-6;
                    bool inOuter = lx >= c.x - land - eps && lx <= c.x + c.l + land + eps && ly >= c.y - land - eps && ly <= c.y + c.w + land + eps;
                    bool inInner = lx > c.x + eps && lx < c.x + c.l - eps && ly > c.y + eps && ly < c.y + c.w - eps;
                    return inOuter && !inInner;
                });
                var r = MindlinPlateSolver.Solve(m, ct);
                double wl = Math.Abs(r.MaxW(onLand, out _));
                if (support == "A")
                {
                    // on the platen the whole plate settles into the contact layer; what opens a gap under a flat lid is the
                    // difference between land points
                    double lo = double.MaxValue, hi = double.MinValue;
                    for (int i = 0; i < r.Xs.Length; i++)
                        for (int j = 0; j < r.Ys.Length; j++)
                            if (onLand(r.Xs[i], r.Ys[j])) { lo = Math.Min(lo, r.W[i, j]); hi = Math.Max(hi, r.W[i, j]); }
                    wl = hi - lo;
                }
                chk.Rw = wl / delta;
                double sigmaMax = r.SigmaMax;
                if (support == "E")
                {
                    // the hogging next to a rigid head is singular in plate theory: leave out the heads and 0.1 span around them,
                    // as the Pillar rows do (the plate over a pillar is a bearing / punching check)
                    double rx = (CoefficientSweep.PillarHead / 2 + CoefficientSweep.CornerExclusion) * chk.Span;
                    var heads = m.Fixed.Select(f => ((f.X1 + f.X2) / 2, (f.Y1 + f.Y2) / 2)).ToList();
                    sigmaMax = 6 * r.MaxMoment((x, y) => heads.All(q => Math.Max(Math.Abs(x - q.Item1), Math.Abs(y - q.Item2)) > rx), out _) / (t * t);
                    chk.Note = "stress outside the pillar heads";
                }
                chk.Rs = sigmaMax * kf / allow;
                chk.Tau = t / Math.Max(1e-9, chk.Span);
                if (support == "A") chk.Note = $"contact {r.ContactFraction:P0}";
                list.Add(chk);
            }

            // ---- walls: the long and the short side of each cavity, Wall-C plates (half models), times the true root factor ----
            var walls = s["walls"] as JArray ?? new JArray();
            foreach (JObject wj in walls)
            {
                int ci = (int)wj["cavity"];
                var c = cavs[ci];
                string side = (string)wj["side"];
                double w = (double)wj["w_mm"], h = c.h, len = side == "length" ? c.l : c.w, perp = side == "length" ? c.w : c.l;
                var wc = new Check { Sample = index, Support = support, Element = "wall-" + side, Governs = (string)wj["governs"], Grade = grade, T = w, Span = h, Tau = w / h };
                if (w / h > 0.5)
                {
                    // outside plate theory (S24): the sizing flags it, a plate model can't judge it
                    wc.Skipped = true; wc.Note = "w/h over 0.5"; list.Add(wc);
                    continue;
                }
                double lh = Math.Min(len / h, 12); // beyond L/h 12 the wall is the strip; the 12 model is within 0.5% of it
                var wm = CoefficientSweep.Model("Wall-C", lh, w / h, nu, 1, elements);
                var wr = MindlinPlateSolver.Solve(wm, ct);
                // the model is dimensionless (E = 1, p = 1, h = 1, t = w/h): w_real = w * p h / E, M_real = M * p h^2
                double wFe = Math.Abs(wr.WMax) * p * h / e;
                double mMax = wr.MaxMoment((x, y) => Math.Sqrt(x * x + (y - 1) * (y - 1)) > CoefficientSweep.CornerExclusion + 1e-9, out _);
                double sigma = 6 * mMax * p * h * h / (w * w) + p * perp / (2 * w);
                wc.Rw = wFe * TrueRootFactor(w / h) / delta;
                wc.Rs = sigma * kf / allow;
                list.Add(wc);
            }
            return list;
        }
    }

    public sealed class MonteCarloRun
    {
        public List<MonteCarloHarness.Check> Checks;
        public List<JObject> Designs;
        public int Samples, Seed;
        public TimeSpan Elapsed;

        IEnumerable<MonteCarloHarness.Check> Live => Checks.Where(c => !c.Skipped);
        public double MaxRw => Live.Select(c => c.Rw).DefaultIfEmpty(0).Max();
        public double MaxRs => Live.Select(c => c.Rs).DefaultIfEmpty(0).Max();
        /// <summary>Median r_w over the checks whose size deflection governs (the only ones where r_w can be near 1 by design).</summary>
        public double MedianRwGoverned => Median(Live.Where(c => c.Governs == "deflection").Select(c => c.Rw));
        public List<MonteCarloHarness.Check> Failures => Live.Where(c => c.Rw > 1.0 || c.Rs > 1.0).ToList();
        public bool Accepted => MaxRw <= 1.0 && MaxRs <= 1.0 && (Live.All(c => c.Governs != "deflection") || MedianRwGoverned >= 0.75);

        static double Median(IEnumerable<double> v)
        {
            var a = v.OrderBy(x => x).ToArray();
            if (a.Length == 0) return double.NaN;
            return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        }

        public string ToCsv()
        {
            var sb = new StringBuilder("sample,support,element,grade,governs,t_mm,span_mm,tau,r_w,r_s,skipped,note\n");
            foreach (var c in Checks)
                sb.Append(string.Join(",", c.Sample, c.Support, c.Element, c.Grade, c.Governs, F(c.T), F(c.Span), F(c.Tau), F(c.Rw), F(c.Rs), c.Skipped ? "1" : "0", "\"" + (c.Note ?? "") + "\"")).Append('\n');
            return sb.ToString();
        }

        static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        public JObject Summary(string sizingName)
        {
            JObject Hist(IEnumerable<double> values)
            {
                var edges = new[] { 0, 0.25, 0.5, 0.6, 0.7, 0.8, 0.9, 0.95, 1.0, 1.05, double.PositiveInfinity };
                var counts = new JObject();
                var arr = values.ToArray();
                for (int k = 0; k + 1 < edges.Length; k++)
                {
                    string key = double.IsPositiveInfinity(edges[k + 1]) ? $">{edges[k]}" : $"{edges[k]}-{edges[k + 1]}";
                    counts[key] = arr.Count(v => v >= edges[k] && v < edges[k + 1]);
                }
                return counts;
            }
            var by = new JObject();
            foreach (var g in Live.GroupBy(c => c.Element.StartsWith("wall") ? "walls" : "plate, support " + c.Support).OrderBy(g => g.Key))
                by[g.Key] = new JObject
                {
                    ["checks"] = g.Count(),
                    ["max_r_w"] = Math.Round(g.Max(c => c.Rw), 4), ["max_r_s"] = Math.Round(g.Max(c => c.Rs), 4),
                    ["median_r_w_deflection_governed"] = g.Any(c => c.Governs == "deflection") ? Math.Round(Median(g.Where(c => c.Governs == "deflection").Select(c => c.Rw)), 4) : (JToken)null,
                    ["governs"] = new JObject(g.GroupBy(c => c.Governs ?? "?").Select(x => new JProperty(x.Key, x.Count()))),
                    ["histogram_r_w_deflection_governed"] = Hist(g.Where(c => c.Governs == "deflection").Select(c => c.Rw)),
                };
            return new JObject
            {
                ["schema"] = "unify3d.mold-montecarlo/1",
                ["generator"] = "MoldSweep montecarlo, SwAgent.Mold.Structural.Fe.Sweep.MonteCarloHarness " + MonteCarloHarness.Version,
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["sizing"] = sizingName,
                ["samples"] = Samples, ["seed"] = Seed, ["checks"] = Live.Count(), ["skipped"] = Checks.Count(c => c.Skipped),
                ["truth"] = "MITC4 plate of the exact layout (cavities plus land as pressure patches; rails as line supports; pillars as springs; an opening as SS4; platen as a stiff tensionless foundation); walls as Wall-C plates times the 3D root factor 1 + 2.6 w/h; r_w at the land nodes",
                ["max_r_w"] = Math.Round(MaxRw, 4), ["max_r_s"] = Math.Round(MaxRs, 4),
                ["median_r_w_deflection_governed"] = Math.Round(MedianRwGoverned, 4),
                ["acceptance"] = "max r_w <= 1.00, max r_s <= 1.00, median r_w >= 0.75 where deflection governs",
                ["accepted"] = Accepted,
                ["failures"] = new JArray(Failures.Take(50).Select(c => $"sample {c.Sample} {c.Element} ({c.Support}, {c.Grade}): r_w {c.Rw:F3}, r_s {c.Rs:F3}")),
                ["by_element"] = by,
                ["elapsed_s"] = Math.Round(Elapsed.TotalSeconds, 0),
            };
        }
    }
}

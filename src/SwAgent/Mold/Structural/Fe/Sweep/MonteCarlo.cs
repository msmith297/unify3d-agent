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
    /// fixed point, or the layout FE), then each sized plate and wall is rebuilt as an exact plate model (cavities as pressure
    /// patches, rails as line supports, pillars as rigid heads) and solved on the truth mesh; r_w = w_FE at the lands /
    /// delta_allow and r_s = sigma_FE K_f / sigma_allow.
    ///
    /// Acceptance (every item must hold; Summary lists the ones that do not):
    ///   - max r_w and max r_s at most 1.00 over the live checks;
    ///   - no errors: a sizing delegate or a truth model that throws is a failure, never a skip;
    ///   - coverage: every group (plates on supports A, B, C, E; walls) has at least MinLiveChecks live checks and at least
    ///     MinCoverage of its checks live ("add support" plates and walls past w/h 0.5 are out of the method's range and skipped);
    ///   - not wasteful: in every group with at least MinLiveChecks deflection-governed checks, median r_w at least 0.75.
    ///
    /// The truth never shares a discretisation with the sizing: TruthMesh (64 elements on the plate's short side, pillar heads
    /// at least 8 elements wide) is finer than both meshes the layout-FE sizing extrapolates from (24/3 and 48/6), and the walls
    /// are solved at 64 elements against the table's 48.
    ///
    /// The delegate is Func&lt;JObject, JObject&gt; so it can be A1's Sizing after integration (through an adapter); until then
    /// StubSizing implements S20-S31. Input (schema "unify3d.mold-mc/1", mm, MPa, degC):
    ///   { support: "A"|"B"|"C"|"E", plate: { length_mm, width_mm }, cavities: [ { x_mm, y_mm, length_mm, width_mm, depth_mm } ],
    ///     land_mm, pressure_MPa, material: { id, tempC }, cycles, kf, delta_allow_mm,
    ///     rails: { y1_mm, y2_mm } (B), opening: { x1_mm, y1_mm, x2_mm, y2_mm } (C), pillars: { xs_mm: [], ys_mm: [] } (E) }
    /// Output: { E_MPa, nu, sigma_allow_MPa, plate: { t_mm, governs: "deflection"|"stress"|"minimum"|"add support" },
    ///           walls: [ { cavity, side: "length"|"width", w_mm, governs } ], method (optional, for the record) }
    /// governs "add support" (plates past t/b 0.5) and "out of range" (walls past w/h 0.5) are recorded but not checked.
    /// Pillars are rigid square heads of side 0.2 x the short spacing (the Pillar rows' assumption).
    /// </summary>
    public static class MonteCarloHarness
    {
        public const string Version = "a2-2";
        /// <summary>The wall-root factor the FE truth uses: the 3D envelope 1 + c w/h (solid3d-summary.json; MoldSweep sets it).</summary>
        public static Func<double, double> TrueRootFactor = wh => 1 + Solid3dValidation.DefaultWallRootSlope * wh;
        /// <summary>The 3D pillar-head factor on plate deflection over rigid heads (solid3d-summary.json pillarCell; MoldSweep sets it).</summary>
        public static double TruePillarFactor = Solid3dValidation.DefaultPillarHeadFactor;

        public const int MinLiveChecks = 10;
        public const double MinCoverage = 0.5, MinMedianRw = 0.75;

        /// <summary>Plate mesh: elements on the plate's short side, and at least HeadElements across a pillar head.</summary>
        public sealed class MeshSpec
        {
            public int Elements, HeadElements;
            public MeshSpec(int elements, int headElements) { Elements = elements; HeadElements = headElements; }
            public override string ToString() => $"{Elements} elements on the short side, pillar heads {HeadElements} elements wide";
        }

        public static readonly MeshSpec TruthMesh = new MeshSpec(64, 8);

        public static readonly string[] Grades = { "al_6061_t651", "al_7075_t651", "st_p20", "st_h13" };
        public static readonly double[] Deltas = { 0.005, 0.012, 0.020, 0.025, 0.05 };
        public static readonly string[] Groups = { "plate, support A", "plate, support B", "plate, support C", "plate, support E", "walls" };

        static double NominalE(string grade) => grade.StartsWith("al_") ? 70000 : 205000;

        /// <summary>
        /// One random mold, drawn so that most plates and walls fall inside the plate method's range (critique: a run that skips
        /// most of its checks proves little). The layout and the support come first; then the molding pressure, log-uniform from
        /// 1 MPa up to min(25 MPa, 0.3 E delta / b) with b the plate's span (above that the plate passes t/b 0.5 and the sizing asks
        /// for support instead); then each cavity's depth, between 2.2 x the minimum wall (shallower pockets get stubby walls past
        /// w/h 0.5) and 0.03 E delta / p (deeper pockets need walls past w/h 0.5; S24 sends both to steel, a keyed wall or a 3D
        /// check), 5-80 mm, uniform; where that range is empty, 5-60 mm (the wall is then out of range and counted as such).
        /// </summary>
        public static JObject Draw(Random rnd)
        {
            double U(double a, double b) => a + (b - a) * rnd.NextDouble();
            string grade = Grades[rnd.Next(Grades.Length)];
            double delta = Deltas[rnd.Next(Deltas.Length)], eNom = NominalE(grade), wMin = grade.StartsWith("al_") ? 15 : 10;
            int n = 1 + rnd.Next(8);
            var cav = new List<(double l, double w)>();
            for (int i = 0; i < n; i++)
            {
                double a = U(20, 200), b = U(20, 200);
                cav.Add((Math.Max(a, b), Math.Min(a, b)));
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
                    placed.Add(new JObject { ["x_mm"] = R(x), ["y_mm"] = R(y), ["length_mm"] = R(cav[k].l), ["width_mm"] = R(cav[k].w) });
                    x += cav[k].l + gap;
                    rowH = Math.Max(rowH, cav[k].w);
                }
                plateL = Math.Max(plateL, x - gap + margin);
                y += rowH + gap;
            }
            double plateW = y - gap + margin;
            const double land = 3.175;
            double tMax = grade == "al_6061_t651" ? 150 : grade == "al_7075_t651" ? 120 : 200;
            string support = new[] { "A", "B", "C", "E" }[rnd.Next(4)];
            var design = new JObject
            {
                ["schema"] = "unify3d.mold-mc/1",
                ["support"] = support,
                ["plate"] = new JObject { ["length_mm"] = R(plateL), ["width_mm"] = R(plateW) },
                ["cavities"] = placed,
                ["land_mm"] = land,
            };
            double span = Math.Min(plateL, plateW);
            if (support == "B")
            {
                double inset = U(15, 40);
                design["rails"] = new JObject { ["y1_mm"] = R(inset), ["y2_mm"] = R(plateW - inset) };
                span = plateW - 2 * inset;
            }
            else if (support == "C")
            {
                double x1 = placed.Min(c => (double)c["x_mm"]) - 10, y1 = placed.Min(c => (double)c["y_mm"]) - 10;
                double x2 = placed.Max(c => (double)c["x_mm"] + (double)c["length_mm"]) + 10, y2 = placed.Max(c => (double)c["y_mm"] + (double)c["width_mm"]) + 10;
                design["opening"] = new JObject { ["x1_mm"] = R(x1), ["y1_mm"] = R(y1), ["x2_mm"] = R(x2), ["y2_mm"] = R(y2) };
                span = Math.Min(x2 - x1, y2 - y1);
            }
            else if (support == "E")
            {
                int px = 1 + rnd.Next(3), py = 1 + rnd.Next(2);
                var xs = new JArray(Enumerable.Range(1, px).Select(i => R(plateL * i / (px + 1))));
                var ys = new JArray(Enumerable.Range(1, py).Select(i => R(plateW * i / (py + 1))));
                design["pillars"] = new JObject { ["xs_mm"] = xs, ["ys_mm"] = ys };
                span = Math.Max(plateL / (px + 1), plateW / (py + 1));
            }
            double pMax = Math.Max(1.5, Math.Min(25, support == "A" ? 25 : 0.3 * eNom * delta / span));
            double p = Math.Exp(U(Math.Log(1), Math.Log(pMax)));
            double hLo = 2.2 * wMin, hHi = Math.Min(80, 0.03 * eNom * delta / p);
            foreach (JObject c in placed) c["depth_mm"] = R(hHi > hLo ? U(hLo, hHi) : U(5, 60));
            design["pressure_MPa"] = R(p);
            design["material"] = new JObject { ["id"] = grade, ["tempC"] = R(U(40, tMax)) };
            design["cycles"] = Math.Round(Math.Pow(10, U(3, 6)));
            design["kf"] = R(U(1.0, 2.2));
            design["delta_allow_mm"] = delta;
            return design;
        }

        static double R(double v) => Math.Round(v, 3);

        public sealed class Check
        {
            public int Sample;
            public string Support, Element, Governs, Grade;
            public double Rw, Rs, T, Span, Tau;
            /// <summary>Out of the method's range (recorded, not checked).</summary>
            public bool Skipped;
            /// <summary>The sizing delegate or the truth model threw: a failure.</summary>
            public bool Error;
            public string Note;
        }

        /// <summary>Runs n samples (parallel), each sized by the delegate and checked by FE on the truth mesh.</summary>
        public static MonteCarloRun Run(int n, int seed, Func<JObject, JObject> sizing, MeshSpec truth = null, Action<string> log = null, CancellationToken ct = default)
        {
            truth = truth ?? TruthMesh;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rnd = new Random(seed);
            var designs = Enumerable.Range(0, n).Select(_ => Draw(rnd)).ToList();
            var results = new List<Check>[n];
            int done = 0;
            Parallel.For(0, n, new ParallelOptions { CancellationToken = ct }, i =>
            {
                var d = designs[i];
                string support = (string)d["support"], grade = (string)d["material"]["id"];
                JObject sized = null;
                try { sized = sizing(d); }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    results[i] = new List<Check> { new Check { Sample = i, Support = support, Grade = grade, Element = "sizing", Error = true, Note = "sizing threw: " + ex.Message } };
                }
                if (sized != null)
                {
                    try { results[i] = CheckDesign(i, d, sized, truth, ct); }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        results[i] = new List<Check> { new Check { Sample = i, Support = support, Grade = grade, Element = "truth", Error = true, Note = "truth model threw: " + ex.Message } };
                    }
                }
                int k = Interlocked.Increment(ref done);
                if (log != null && k % 250 == 0) log($"  {k}/{n} molds, {sw.Elapsed.TotalSeconds:F0} s");
            });
            return new MonteCarloRun { Checks = results.SelectMany(r => r).ToList(), Samples = n, Seed = seed, Designs = designs, Elapsed = sw.Elapsed, Truth = truth };
        }

        /// <summary>The plate model of a sized design (t), its x/y offset (case C models the opening only) and the span.</summary>
        public static PlateModel PlateOf(JObject d, double t, double e, double nu, MeshSpec mesh, bool clampedHeads, out double ox, out double oy, out double span)
        {
            string support = (string)d["support"];
            double pl = (double)d["plate"]["length_mm"], pw = (double)d["plate"]["width_mm"], p = (double)d["pressure_MPa"], land = (double?)d["land_mm"] ?? 3.175;
            PlateModel m;
            ox = 0; oy = 0;
            double shortSide;
            switch (support)
            {
                case "A":
                    m = PlateModel.Rect(pl, pw, t, e, nu, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free);
                    m.Foundations.Add(new WinklerRegion(0, pl, 0, pw, 1e4, unilateral: true)); // a rigid platen through a thin contact layer
                    span = shortSide = Math.Min(pl, pw);
                    break;
                case "B":
                    m = PlateModel.Rect(pl, pw, t, e, nu, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free, EdgeBc.Free);
                    double y1 = (double)d["rails"]["y1_mm"], y2 = (double)d["rails"]["y2_mm"];
                    m.Lines.Add(new LineSupport('y', y1)); m.Lines.Add(new LineSupport('y', y2));
                    span = y2 - y1; shortSide = Math.Min(pl, pw);
                    break;
                case "C":
                    var o = d["opening"];
                    ox = (double)o["x1_mm"]; oy = (double)o["y1_mm"];
                    double ol = (double)o["x2_mm"] - ox, ow = (double)o["y2_mm"] - oy;
                    m = PlateModel.Rect(ol, ow, t, e, nu, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard);
                    span = shortSide = Math.Min(ol, ow);
                    break;
                case "E":
                    m = PlateModel.Rect(pl, pw, t, e, nu, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard, EdgeBc.SimpleHard);
                    var xs = d["pillars"]["xs_mm"].Select(v => (double)v).ToList();
                    var ys = d["pillars"]["ys_mm"].Select(v => (double)v).ToList();
                    span = PillarSpan(d);
                    shortSide = Math.Min(pl, pw);
                    double hh = CoefficientSweep.PillarHead * span / 2;
                    foreach (var px in xs) foreach (var py in ys) m.Fixed.Add(new FixedRegion(px - hh, px + hh, py - hh, py + hh, clampedHeads));
                    break;
                default: throw new ArgumentException("Unknown support " + support);
            }
            m.ElementSize = shortSide / mesh.Elements;
            if (support == "E") m.ElementSize = Math.Min(m.ElementSize, CoefficientSweep.PillarHead * span / mesh.HeadElements);
            m.MaxMemoryMB = 4000; // dev harness, outside SolidWorks
            foreach (var c in d["cavities"])
            {
                double cx = (double)c["x_mm"], cy = (double)c["y_mm"], cl = (double)c["length_mm"], cw = (double)c["width_mm"];
                m.Pressures.Add(new PressurePatch(cx - land - ox, cx + cl + land - ox, cy - land - oy, cy + cw + land - oy, p));
                // grid lines on the cavity outline too, so both edges of the land ring are nodes on every mesh (the land is
                // narrower than an element: without them a coarse mesh reads the land only at its outer edge)
                m.XLines.Add(cx - ox); m.XLines.Add(cx + cl - ox); m.YLines.Add(cy - oy); m.YLines.Add(cy + cw - oy);
            }
            return m;
        }

        /// <summary>The short pillar spacing of case E: min over x and y of the largest gap between pillars and plate edges.</summary>
        public static double PillarSpan(JObject d)
        {
            double pl = (double)d["plate"]["length_mm"], pw = (double)d["plate"]["width_mm"];
            var gx = new[] { 0.0 }.Concat(d["pillars"]["xs_mm"].Select(v => (double)v)).Concat(new[] { pl }).ToList();
            var gy = new[] { 0.0 }.Concat(d["pillars"]["ys_mm"].Select(v => (double)v)).Concat(new[] { pw }).ToList();
            return Math.Min(Enumerable.Range(0, gx.Count - 1).Max(k => gx[k + 1] - gx[k]), Enumerable.Range(0, gy.Count - 1).Max(k => gy[k + 1] - gy[k]));
        }

        /// <summary>The FE truth for one sized design (plate and walls) on the given mesh (TruthMesh by default).</summary>
        public static List<Check> CheckDesign(int index, JObject d, JObject s, MeshSpec mesh = null, CancellationToken ct = default)
        {
            mesh = mesh ?? TruthMesh;
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
                var (rw, rs, span, note) = PlateRatios(d, t, e, nu, allow, mesh, ct);
                chk.Rw = rw; chk.Rs = rs; chk.Span = span; chk.Tau = t / Math.Max(1e-9, span); chk.Note = note;
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
                var wm = CoefficientSweep.Model("Wall-C", lh, w / h, nu, 1, mesh.Elements);
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

        /// <summary>
        /// r_w and r_s of the plate of a design at thickness t on a mesh: the largest land deflection over delta (case A: the
        /// spread between land points, the plate settles into the platen as a whole), and the largest stress x K_f over the
        /// allowable. Case E: deflection over w-only heads times TruePillarFactor; stress the larger of the plate outside the heads
        /// (and 0.1 span around them) and the hogging on the faces of clamped heads, read a quarter of the head side or more from
        /// the head corners (what the Pillar rows tabulate).
        /// </summary>
        public static (double Rw, double Rs, double Span, string Note) PlateRatios(JObject d, double t, double e, double nu, double allow, MeshSpec mesh, CancellationToken ct = default)
        {
            string support = (string)d["support"];
            double delta = (double)d["delta_allow_mm"], kf = (double)d["kf"], land = (double?)d["land_mm"] ?? 3.175;
            var cavs = d["cavities"].Select(c => (x: (double)c["x_mm"], y: (double)c["y_mm"], l: (double)c["length_mm"], w: (double)c["width_mm"])).ToList();
            var m = PlateOf(d, t, e, nu, mesh, false, out double ox, out double oy, out double span);
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
            string note = null;
            if (support == "A")
            {
                double lo = double.MaxValue, hi = double.MinValue;
                for (int i = 0; i < r.Xs.Length; i++)
                    for (int j = 0; j < r.Ys.Length; j++)
                        if (onLand(r.Xs[i], r.Ys[j])) { lo = Math.Min(lo, r.W[i, j]); hi = Math.Max(hi, r.W[i, j]); }
                wl = hi - lo;
                note = $"contact {r.ContactFraction:P0}";
            }
            double mMax = r.MMax;
            if (support == "E")
            {
                wl *= TruePillarFactor;
                double hs = CoefficientSweep.PillarHead * span;
                double band = hs / 2 + CoefficientSweep.CornerExclusion * span;
                var heads = m.Fixed.Select(f => ((f.X1 + f.X2) / 2, (f.Y1 + f.Y2) / 2)).ToList();
                double interior = r.MaxMoment((x, y) => heads.All(q => Math.Max(Math.Abs(x - q.Item1), Math.Abs(y - q.Item2)) > band), out _);
                var mc = PlateOf(d, t, e, nu, mesh, true, out _, out _, out _);
                var rc = MindlinPlateSolver.Solve(mc, ct);
                double face = CoefficientSweep.HeadFaceMoment(rc, mc, CoefficientSweep.PillarCornerExclusion / CoefficientSweep.PillarHead * hs);
                mMax = Math.Max(interior, face);
                note = face >= interior ? "stress: hogging on a head face" : "stress: between the pillars";
            }
            return (wl / delta, 6 * mMax / (t * t) * kf / allow, span, note);
        }
    }

    public sealed class MonteCarloRun
    {
        public List<MonteCarloHarness.Check> Checks;
        public List<JObject> Designs;
        public int Samples, Seed;
        public TimeSpan Elapsed;
        public MonteCarloHarness.MeshSpec Truth;

        IEnumerable<MonteCarloHarness.Check> Live => Checks.Where(c => !c.Skipped && !c.Error);
        public List<MonteCarloHarness.Check> Errors => Checks.Where(c => c.Error).ToList();
        public double MaxRw => Live.Select(c => c.Rw).DefaultIfEmpty(0).Max();
        public double MaxRs => Live.Select(c => c.Rs).DefaultIfEmpty(0).Max();
        /// <summary>Median r_w over the checks whose size deflection governs (the only ones where r_w can be near 1 by design).</summary>
        public double MedianRwGoverned => Median(Live.Where(c => c.Governs == "deflection").Select(c => c.Rw));
        public List<MonteCarloHarness.Check> Failures => Checks.Where(c => c.Error || (!c.Skipped && (c.Rw > 1.0 || c.Rs > 1.0))).ToList();

        public static string GroupOf(MonteCarloHarness.Check c) => c.Element.StartsWith("wall") ? "walls" : "plate, support " + c.Support;

        public sealed class GroupStats
        {
            public string Name;
            public int Checks, Live, Skipped, Errors, DeflectionGoverned;
            public double Coverage, MaxRw, MaxRs, MedianRw = double.NaN;
        }

        public List<GroupStats> Stats()
        {
            var list = new List<GroupStats>();
            foreach (var g in MonteCarloHarness.Groups)
            {
                // a sizing error has no walls to report: it counts against its support's plate group
                var all = Checks.Where(c => GroupOf(c) == g).ToList();
                var live = all.Where(c => !c.Skipped && !c.Error).ToList();
                var defl = live.Where(c => c.Governs == "deflection").Select(c => c.Rw).ToList();
                list.Add(new GroupStats
                {
                    Name = g, Checks = all.Count, Live = live.Count, Skipped = all.Count(c => c.Skipped), Errors = all.Count(c => c.Error),
                    DeflectionGoverned = defl.Count, Coverage = all.Count == 0 ? 0 : (double)live.Count / all.Count,
                    MaxRw = live.Select(c => c.Rw).DefaultIfEmpty(0).Max(), MaxRs = live.Select(c => c.Rs).DefaultIfEmpty(0).Max(),
                    MedianRw = defl.Count > 0 ? Median(defl) : double.NaN,
                });
            }
            return list;
        }

        /// <summary>Every acceptance item that does not hold (empty = accepted).</summary>
        public List<string> AcceptanceFailures()
        {
            var f = new List<string>();
            if (MaxRw > 1.0) f.Add($"max r_w {MaxRw:F4} > 1");
            if (MaxRs > 1.0) f.Add($"max r_s {MaxRs:F4} > 1");
            var errs = Errors;
            if (errs.Count > 0) f.Add($"{errs.Count} errors (first: sample {errs[0].Sample}: {errs[0].Note})");
            foreach (var g in Stats())
            {
                if (g.Live < MonteCarloHarness.MinLiveChecks) f.Add($"{g.Name}: only {g.Live} live checks (at least {MonteCarloHarness.MinLiveChecks})");
                if (g.Coverage < MonteCarloHarness.MinCoverage) f.Add($"{g.Name}: coverage {g.Coverage:P0} (live / drawn; at least {MonteCarloHarness.MinCoverage:P0})");
                if (g.DeflectionGoverned >= MonteCarloHarness.MinLiveChecks && g.MedianRw < MonteCarloHarness.MinMedianRw)
                    f.Add($"{g.Name}: median r_w {g.MedianRw:F3} where deflection governs (at least {MonteCarloHarness.MinMedianRw}): wasteful");
            }
            return f;
        }

        public bool Accepted => AcceptanceFailures().Count == 0;

        static double Median(IEnumerable<double> v)
        {
            var a = v.OrderBy(x => x).ToArray();
            if (a.Length == 0) return double.NaN;
            return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        }

        public string ToCsv()
        {
            var sb = new StringBuilder("sample,support,element,grade,governs,t_mm,span_mm,tau,r_w,r_s,skipped,error,note\n");
            foreach (var c in Checks)
                sb.Append(string.Join(",", c.Sample, c.Support, c.Element, c.Grade, c.Governs, F(c.T), F(c.Span), F(c.Tau), F(c.Rw), F(c.Rs), c.Skipped ? "1" : "0", c.Error ? "1" : "0", "\"" + (c.Note ?? "").Replace("\"", "'") + "\"")).Append('\n');
            return sb.ToString();
        }

        static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        static JToken N(double v, int digits = 4) => double.IsNaN(v) ? (JToken)null : Math.Round(v, digits);

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
            foreach (var g in Stats())
            {
                var live = Checks.Where(c => GroupOf(c) == g.Name && !c.Skipped && !c.Error).ToList();
                by[g.Name] = new JObject
                {
                    ["checks"] = g.Checks, ["live"] = g.Live, ["skipped_out_of_range"] = g.Skipped, ["errors"] = g.Errors, ["coverage"] = N(g.Coverage, 3),
                    ["max_r_w"] = N(g.MaxRw), ["max_r_s"] = N(g.MaxRs),
                    ["deflection_governed"] = g.DeflectionGoverned, ["median_r_w_deflection_governed"] = N(g.MedianRw),
                    ["governs"] = new JObject(live.GroupBy(c => c.Governs ?? "?").OrderBy(x => x.Key).Select(x => new JProperty(x.Key, x.Count()))),
                    ["histogram_r_w_deflection_governed"] = Hist(live.Where(c => c.Governs == "deflection").Select(c => c.Rw)),
                };
            }
            var fails = AcceptanceFailures();
            return new JObject
            {
                ["schema"] = "unify3d.mold-montecarlo/2",
                ["generator"] = "MoldSweep montecarlo, SwAgent.Mold.Structural.Fe.Sweep.MonteCarloHarness " + MonteCarloHarness.Version,
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["sizing"] = sizingName,
                ["samples"] = Samples, ["seed"] = Seed, ["checks"] = Checks.Count, ["live"] = Live.Count(), ["skipped"] = Checks.Count(c => c.Skipped), ["errors"] = Errors.Count,
                ["truth"] = $"MITC4 plate of the exact layout ({Truth}; cavities plus land as pressure patches; rails as line supports; pillars as rigid heads 0.2 x the short spacing, holding w for the deflection (times the 3D pillar-head factor {MonteCarloHarness.TruePillarFactor.ToString("0.00", CultureInfo.InvariantCulture)}) and clamped for the hogging on the head faces; an opening as SS4; platen as a stiff tensionless foundation); walls as Wall-C plates ({Truth.Elements} elements) times the 3D root factor 1 + c w/h; r_w at the land nodes. The truth never uses a mesh the sizing used.",
                ["max_r_w"] = N(MaxRw), ["max_r_s"] = N(MaxRs),
                ["median_r_w_deflection_governed"] = N(MedianRwGoverned),
                ["acceptance"] = $"max r_w <= 1.00 and max r_s <= 1.00; no sizing or truth errors; per group (plates on A, B, C, E; walls) at least {MonteCarloHarness.MinLiveChecks} live checks and coverage (live / drawn) at least {MonteCarloHarness.MinCoverage:P0}; per group with at least {MonteCarloHarness.MinLiveChecks} deflection-governed checks, median r_w at least {MonteCarloHarness.MinMedianRw}",
                ["accepted"] = fails.Count == 0,
                ["acceptance_failures"] = new JArray(fails),
                ["failures"] = new JArray(Failures.Take(50).Select(c => c.Error ? $"sample {c.Sample} {c.Element} ({c.Support}, {c.Grade}): {c.Note}" : $"sample {c.Sample} {c.Element} ({c.Support}, {c.Grade}): r_w {c.Rw:F3}, r_s {c.Rs:F3}")),
                ["by_group"] = by,
                ["elapsed_s"] = Math.Round(Elapsed.TotalSeconds, 0),
            };
        }
    }
}

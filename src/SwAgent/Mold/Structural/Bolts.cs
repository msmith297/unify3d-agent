using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>A screw size from the fastener tables, in mm, mm², MPa and N.</summary>
    public sealed class BoltSpec
    {
        public string Size, Spec;
        public bool Imperial;
        public double D, At, Sp, Fp, Su, Se, MinorDia, HeadDia, HeadBearingArea, CboreDia, CboreDepth, ClearanceHole, TapDrill;
        public double WasherOd, WasherId, WasherT;
        public double DisplayD => Imperial ? D / Si.InToMm : D;

        public static BoltSpec From(JObject f, Cfg cfg)
        {
            if (f == null) throw new ArgumentNullException(nameof(f));
            var inch = f["d_in"] != null;
            var b = new BoltSpec { Size = (string)f["size"], Imperial = inch };
            if (inch)
            {
                b.Spec = "ASTM A574";
                b.D = (double)f["d_in"] * Si.InToMm;
                b.At = (double)f["tensile_stress_area_in2"] * Si.InToMm * Si.InToMm;
                b.Sp = (double)f["proof_stress_ksi"] * Si.KsiToMPa;
                b.Su = cfg.D("bolts.utsInchA574_MPa", 1241);
                b.Se = cfg.D("bolts.enduranceInchA574_MPa", 160);
                b.MinorDia = ((double?)f["minor_dia_in"] ?? ((double)f["d_in"] - 1.226869 / (double)f["tpi"])) * Si.InToMm;
                b.HeadDia = (double)f["head_dia_in"] * Si.InToMm;
                b.ClearanceHole = (double)f["clearance_normal_in"] * Si.InToMm;
                b.CboreDia = (double)f["cbore_dia_in"] * Si.InToMm;
                b.CboreDepth = (double)f["cbore_depth_in"] * Si.InToMm;
                b.TapDrill = (double)f["tap_drill_in"] * Si.InToMm;
                if (f["washer"] is JObject w)
                {
                    b.WasherOd = (double)w["od_in"] * Si.InToMm;
                    b.WasherId = (double)w["id_in"] * Si.InToMm;
                    b.WasherT = (double)w["t_in"] * Si.InToMm;
                }
            }
            else
            {
                b.Spec = "ISO 4762 12.9";
                b.D = (double)f["d_mm"];
                b.At = (double)f["tensile_stress_area_mm2"];
                b.Sp = 970.0;
                b.Su = cfg.D("bolts.utsMetric129_MPa", 1220);
                b.Se = cfg.D("bolts.enduranceMetric129_MPa", 190);
                b.MinorDia = (double?)f["minor_dia_mm"] ?? ((double)f["d_mm"] - 1.226869 * (double)f["pitch_mm"]);
                b.HeadDia = (double)f["head_dia_mm"];
                b.ClearanceHole = (double)f["clearance_medium_mm"];
                b.CboreDia = (double)f["cbore_dia_mm"];
                b.CboreDepth = (double)f["cbore_depth_mm"];
                b.TapDrill = (double)f["tap_drill_mm"];
                if (f["washer"] is JObject w)
                {
                    b.WasherOd = (double)w["od_mm"];
                    b.WasherId = (double)w["id_mm"];
                    b.WasherT = (double)w["t_mm"];
                }
            }
            b.Fp = b.At * b.Sp;
            b.HeadBearingArea = Math.PI / 4 * (b.HeadDia * b.HeadDia - b.ClearanceHole * b.ClearanceHole);
            return b;
        }
    }

    /// <summary>One clamped layer (thickness mm, E at temperature MPa, aluminium or not).</summary>
    public sealed class Layer
    {
        public double T, E;
        public bool Aluminium;
    }

    public sealed class JointInput
    {
        public BoltSpec Bolt;
        /// <summary>Clamped length (mm) for a through bolt; for a tapped hole give TopThickness and TappedThickness instead.</summary>
        public double Grip;
        public bool Tapped;
        public double TopThickness, TappedThickness;
        /// <summary>Clamped layers (top to bottom); a uniform stack uses Wileman (S43), a mixed one the frustum method.</summary>
        public List<Layer> Layers = new List<Layer>();
        public double AlphaMember = 2.36e-5;
        public double TempC = 20, DeltaT = double.NaN;
        /// <summary>External tension per bolt at LC1 and at the abuse case LC1x (class A, A3), N.</summary>
        public double P, PAbuse;
        /// <summary>Member ultimate at temperature (MPa) for head bearing (S51), and whether a hardened washer is used.</summary>
        public double MemberSuT;
        public bool Washer = true;
        /// <summary>Fix the preload fraction (tests, the as-built check); null = choose it (A1).</summary>
        public double? PreloadFraction;
        public double? Scatter;
    }

    public sealed class JointResult
    {
        public double Grip, Kb, Km, C, Fp, FpT, KT, Fraction, Fi, FiMax, FiMin, DFth, HotMax, N0, NL, SigmaA, SeAllow;
        public double HeadPressure, HeadAllow, TorqueDry_Nm, TorqueLubed_Nm;
        public double FractionLo, FractionHi;
        public double HotMaxAbuse, N0Abuse;
        public bool Passes, AbusePasses;
        public string Failing;
        public List<JCheck> Checks = new List<JCheck>();
    }

    /// <summary>
    /// S42-S53 (Shigley 10th ed., ch. 8) with critique A1, A3 and H5.
    /// (S42) k_b = A_t E_b / l. (S43) k_m: Wileman E d A exp(B d/l) for a uniform stack (Al: 0.79670/0.63816; steel:
    /// 0.78715/0.62873), frustum method per layer for mixed stacks. H5: a tapped hole uses Shigley's effective grip
    /// l' = h + t2/2 (t2 < d) or h + d/2. (S44) C = k_b/(k_b + k_m). (S45, A1) F_i = f F_p with tightening scatter s
    /// (0.25 torque wrench): F_i,max = (1+s) F_i, F_i,min = (1-s)(1-e) F_i (e = 0.10 embedding in aluminium).
    /// (S46) dF = (a_m - a_b) dT l k_b k_m/(k_b + k_m). (S47) F_i,max + dF + C P <= 0.90 F_p,T with F_p,T = k_T F_p
    /// (hot proof factor). (S48) n_0 = F_i,min / (P (1-C)) >= 2 (a heating loss of preload counts against it).
    /// (S49) n_L = (F_p,T - F_i,max)/(C P) >= 1.5. (S50) C P/(2 A_t) <= S_e/1.5. (S51) head bearing
    /// (F_i,max + dF + C P)/A <= 0.9 S_u,T (washer spreads the head load at 45 deg). (S52) T = K d F_i, printed with the
    /// preload band. A3: class A joints are also checked at LC1x with SF 1.0: F_i,max + dF + C P_x <= F_p,T and n_0 >= 1.
    /// The preload fraction is chosen as the highest one (up to 0.75 steel / 0.60 aluminium) that passes every check.
    /// </summary>
    public static class Bolts
    {
        public static double HotProofFactor(double tempC, Cfg cfg)
        {
            var xs = cfg.Arr("bolts.hotProofFactor.tempsC");
            var ys = cfg.Arr("bolts.hotProofFactor.factor");
            if (xs.Length == 0 || xs.Length != ys.Length) return 1.0;
            return Cfg.Interp(xs, ys, tempC);
        }

        /// <summary>H5: Shigley's effective grip for a cap screw in a tapped hole.</summary>
        public static double EffectiveGrip(double topThickness, double tappedThickness, double d) =>
            topThickness + (tappedThickness < d ? tappedThickness / 2 : d / 2);

        public static double Wileman(double E, double d, double l, bool aluminium) =>
            aluminium ? E * d * 0.79670 * Math.Exp(0.63816 * d / l) : E * d * 0.78715 * Math.Exp(0.62873 * d / l);

        /// <summary>Shigley eq. 8-20 frustum stiffness of a layer of thickness t starting at diameter D (30 deg cone).</summary>
        public static double Frustum(double E, double d, double t, double D)
        {
            var num = (1.155 * t + D - d) * (D + d);
            var den = (1.155 * t + D + d) * (D - d);
            return 0.5774 * Math.PI * E * d / Math.Log(num / den);
        }

        public static double MemberStiffness(JointInput j, double grip)
        {
            var d = j.Bolt.D;
            var layers = j.Layers ?? new List<Layer>();
            var uniform = layers.Count == 0 || layers.All(x => x.Aluminium == layers[0].Aluminium && Math.Abs(x.E - layers[0].E) < 1e-6 * layers[0].E);
            if (layers.Count == 0) throw new ArgumentException("A joint needs at least one clamped layer.");
            if (uniform) return Wileman(layers[0].E, d, grip, layers[0].Aluminium);
            // Mixed stack: two 30 deg frustums from the outer faces (washer face 1.5 d) meeting at mid-grip, per layer.
            var scale = grip / layers.Sum(x => x.T);
            var segs = layers.Select(x => (T: x.T * scale, x.E)).ToList();
            var inv = 0.0;
            foreach (var half in new[] { segs, Enumerable.Reverse(segs).ToList() })
            {
                var left = grip / 2;
                var D = 1.5 * d;
                foreach (var s in half)
                {
                    if (left <= 0) break;
                    var t = Math.Min(s.T, left);
                    inv += 1 / Frustum(s.E, d, t, D);
                    D += 2 * t * Math.Tan(Math.PI / 6);
                    left -= t;
                }
            }
            return 1 / inv;
        }

        /// <summary>Evaluates the joint at preload fraction f (all checks, LC1 and the abuse case when PAbuse > 0).</summary>
        public static JointResult Evaluate(JointInput j, Cfg cfg, double f)
        {
            var b = j.Bolt;
            var r = new JointResult();
            r.Grip = j.Tapped ? EffectiveGrip(j.TopThickness, j.TappedThickness, b.D) : j.Grip;
            var Eb = cfg.D("bolts.boltE_GPa", 205) * 1000.0;
            r.Kb = b.At * Eb / r.Grip;
            r.Km = MemberStiffness(j, r.Grip);
            r.C = r.Kb / (r.Kb + r.Km);
            r.Fp = b.Fp;
            r.KT = HotProofFactor(j.TempC, cfg);
            r.FpT = r.KT * r.Fp;
            var dT = double.IsNaN(j.DeltaT) ? j.TempC - 20.0 : j.DeltaT;
            r.DFth = (j.AlphaMember - cfg.D("bolts.alphaBolt_perK", 1.17e-5)) * dT * r.Grip * r.Kb * r.Km / (r.Kb + r.Km);
            var s = j.Scatter ?? cfg.D("bolts.tighteningScatter", 0.25);
            var aluminium = j.Layers.Any(x => x.Aluminium);
            var e = aluminium ? cfg.D("bolts.embedmentLossAluminium", 0.10) : 0.0;
            r.Fraction = f;
            r.Fi = f * r.Fp;
            r.FiMax = (1 + s) * r.Fi;
            r.FiMin = (1 - s) * (1 - e) * r.Fi;
            var loss = Math.Min(0, r.DFth);   // cooling-type mismatch (steel clamping aluminium bolts...) lowers preload
            r.HotMax = r.FiMax + Math.Max(0, r.DFth) + r.C * j.P;
            r.N0 = j.P > 0 ? (r.FiMin + loss) / (j.P * (1 - r.C)) : double.PositiveInfinity;
            r.NL = j.P > 0 ? (r.FpT - r.FiMax) / (r.C * j.P) : double.PositiveInfinity;
            r.SigmaA = r.C * j.P / (2 * b.At);
            r.SeAllow = b.Se / cfg.D("sfFatigue", 1.5);
            var area = HeadArea(b, j.Washer, cfg);
            r.HeadPressure = r.HotMax / area;
            r.HeadAllow = cfg.D("bolts.headBearingOfUts", 0.9) * j.MemberSuT;
            r.TorqueDry_Nm = cfg.D("bolts.kDry", 0.2) * b.D * r.Fi / 1000.0;
            r.TorqueLubed_Nm = cfg.D("bolts.kLubed", 0.15) * b.D * r.Fi / 1000.0;

            var hotLim = cfg.D("bolts.hotMaxOfProof", 0.9);
            r.Checks.Add(new JCheck { Id = "BOLT-HOT", Type = "boltLoad", Demand = r.HotMax, Capacity = hotLim * r.FpT, Unit = "N", LoadCase = "LC1+LC3", Method = "S47 (A1): (1+s) F_i + dF + C P <= 0.90 k_T F_p" });
            if (j.P > 0)
            {
                r.Checks.Add(new JCheck { Id = "BOLT-SEP", Type = "boltSeparation", Demand = r.N0, Capacity = cfg.D("bolts.separationMin", 2.0), Unit = "n0", Lower = true, LoadCase = "LC1", Method = "S48 (A1): (1-s)(1-e) F_i / (P (1-C)) >= 2" });
                r.Checks.Add(new JCheck { Id = "BOLT-NL", Type = "boltLoad", Demand = r.NL, Capacity = cfg.D("bolts.loadFactorMin", 1.5), Unit = "nL", Lower = true, LoadCase = "LC1", Method = "S49 (A1): (k_T F_p - (1+s) F_i)/(C P) >= 1.5" });
                r.Checks.Add(new JCheck { Id = "BOLT-FAT", Type = "boltFatigue", Demand = r.SigmaA, Capacity = r.SeAllow, Unit = "MPa", LoadCase = "LC1", Method = "S50: C P/(2 A_t) <= S_e/1.5" });
            }
            r.Checks.Add(new JCheck { Id = "BOLT-HEAD", Type = "bearing", Demand = r.HeadPressure, Capacity = r.HeadAllow, Unit = "MPa", LoadCase = "LC1+LC3", Method = "S51: head (washer) bearing <= 0.9 S_u,T of the member" });

            r.AbusePasses = true;
            if (j.PAbuse > 0)
            {
                r.HotMaxAbuse = r.FiMax + Math.Max(0, r.DFth) + r.C * j.PAbuse;
                r.N0Abuse = (r.FiMin + loss) / (j.PAbuse * (1 - r.C));
                r.Checks.Add(new JCheck { Id = "BOLT-HOT-X", Type = "boltLoad", Demand = r.HotMaxAbuse, Capacity = cfg.D("classA.abuseSf", 1.0) * r.FpT, Unit = "N", LoadCase = "LC1x", Method = "A3: abuse case, (1+s) F_i + dF + C P_x <= k_T F_p (SF 1.0)" });
                r.Checks.Add(new JCheck { Id = "BOLT-SEP-X", Type = "boltSeparation", Demand = r.N0Abuse, Capacity = cfg.D("classA.abuseSeparationMin", 1.0), Unit = "n0", Lower = true, LoadCase = "LC1x", Method = "A3: abuse case, no separation (n0 >= 1.0)" });
                r.AbusePasses = r.HotMaxAbuse <= cfg.D("classA.abuseSf", 1.0) * r.FpT * (1 + 1e-12) && r.N0Abuse >= cfg.D("classA.abuseSeparationMin", 1.0) * (1 - 1e-12);
            }
            var fails = r.Checks.Where(c => c.LoadCase != "LC1x" && !Passes(c)).Select(c => c.Id).ToList();
            r.Passes = fails.Count == 0;
            r.Failing = string.Join(", ", fails);
            return r;
        }

        public static bool Passes(JCheck c)
        {
            if (c.Status != null) return c.Status != "fail";
            return c.Lower ? c.Demand >= c.Capacity * (1 - 1e-12) : c.Demand <= c.Capacity * (1 + 1e-12);
        }

        /// <summary>Head (or hardened washer) bearing area on the member, mm²: the washer spreads the head load at 45 deg.</summary>
        public static double HeadArea(BoltSpec b, bool washer, Cfg cfg)
        {
            if (!washer || b.WasherOd <= 0) return b.HeadBearingArea;
            var spread = Math.Tan(cfg.D("bolts.washerSpreadDeg", 45) * Math.PI / 180);
            var dEff = Math.Min(b.WasherOd, b.HeadDia + 2 * b.WasherT * spread);
            var hole = Math.Max(b.WasherId, b.ClearanceHole);
            return Math.PI / 4 * (dEff * dEff - hole * hole);
        }

        /// <summary>
        /// Picks the preload fraction (A1): the highest f up to the joint's ceiling (0.75 steel, 0.60 with aluminium) that
        /// passes S47-S51 with the scatter; the window [lo, hi] is reported. With requireAbuse, LC1x must pass too.
        /// Returns the evaluation at the chosen f (or at the ceiling, failing, when there is no window).
        /// </summary>
        public static JointResult Design(JointInput j, Cfg cfg, bool requireAbuse)
        {
            if (j.PreloadFraction != null) return Evaluate(j, cfg, j.PreloadFraction.Value);
            var aluminium = j.Layers.Any(x => x.Aluminium);
            var ceiling = aluminium ? cfg.D("bolts.preloadAluminiumJoint", 0.60) : cfg.D("bolts.preloadSteelJoint", 0.75);
            // Each check is monotonic in f: the separation checks fail below some f, all others (hot load, load factor,
            // head bearing; fatigue does not depend on f) fail above some f. Bisect both ends of the window.
            bool Relevant(JCheck c) => c.LoadCase != "LC1x" || requireAbuse;
            bool UpperOk(double f) => Evaluate(j, cfg, f).Checks.Where(c => Relevant(c) && !IsSeparation(c)).All(Passes);
            bool LowerOk(double f) => Evaluate(j, cfg, f).Checks.Where(c => Relevant(c) && IsSeparation(c)).All(Passes);
            double hi;
            if (UpperOk(ceiling)) hi = ceiling;
            else if (!UpperOk(0)) hi = -1;
            else hi = Bisect(UpperOk, 0, ceiling, true);
            double lo;
            if (LowerOk(0)) lo = 0;
            else if (!LowerOk(Math.Max(ceiling, 1.0))) lo = 2;
            else lo = Bisect(LowerOk, 0, Math.Max(ceiling, 1.0), false);
            JointResult res;
            if (hi >= 0 && hi >= lo)
            {
                // the highest passing preload, rounded down to 0.005 when that stays in the window (readable torque)
                var f = Math.Floor(hi * 200) / 200;
                if (f < lo) f = hi;
                res = Evaluate(j, cfg, f);
            }
            else res = Evaluate(j, cfg, ceiling);
            res.FractionLo = lo;
            res.FractionHi = hi;
            return res;
        }

        private static bool IsSeparation(JCheck c) => c.Id == "BOLT-SEP" || c.Id == "BOLT-SEP-X";

        private static double Bisect(Func<double, bool> ok, double a, double b, bool okAtLow)
        {
            // okAtLow: ok(a) true, ok(b) false -> largest passing; else ok(a) false, ok(b) true -> smallest passing
            for (var k = 0; k < 60; k++)
            {
                var m = 0.5 * (a + b);
                if (ok(m) == okAtLow) a = m; else b = m;
            }
            return okAtLow ? a : b;
        }

        /// <summary>
        /// The largest abuse load per bolt this joint carries (A3), for a gauge cap: bisect P_x where a preload window
        /// still exists with LC1x included.
        /// </summary>
        public static double AbuseCapacity(JointInput j, Cfg cfg)
        {
            var hiP = Math.Max(j.PAbuse, j.Bolt.Fp) * 4;
            double lo = 0, hi = hiP;
            for (var k = 0; k < 50; k++)
            {
                var m = 0.5 * (lo + hi);
                var t = Clone(j);
                t.PAbuse = m;
                var r = Design(t, cfg, true);
                var ok = r.FractionHi >= 0 && r.FractionHi >= r.FractionLo && r.AbusePasses;
                if (ok) lo = m; else hi = m;
            }
            return lo;
        }

        private static JointInput Clone(JointInput j) => new JointInput
        {
            Bolt = j.Bolt, Grip = j.Grip, Tapped = j.Tapped, TopThickness = j.TopThickness, TappedThickness = j.TappedThickness,
            Layers = j.Layers, AlphaMember = j.AlphaMember, TempC = j.TempC, DeltaT = j.DeltaT, P = j.P, PAbuse = j.PAbuse,
            MemberSuT = j.MemberSuT, Washer = j.Washer, PreloadFraction = j.PreloadFraction, Scatter = j.Scatter,
        };
    }
}

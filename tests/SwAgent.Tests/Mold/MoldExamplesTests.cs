using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// The worked examples E1-E11 (design 3.19 re-derived with the critique) from data/validation/examples.json, whose
    /// expected values come from the independent script tools/dev/mold-data/examples_check.py (and, for E11, by hand).
    /// Table-dependent values (wall coefficients) get the wider tableDependentTolerancePct so Track A2's regenerated
    /// coefficients.csv does not break them; everything else must match within tolerancePct.
    /// </summary>
    public class MoldExamplesTests
    {
        private static double Tol(JObject e, string key)
        {
            var dep = e["tableDependent"] as JArray;
            if (dep != null && dep.Any(x => (string)x == key)) return (double)e["tableDependentTolerancePct"];
            return (double)e["tolerancePct"];
        }

        [Fact]
        public void E1_press_class_B_gauge_reading()
        {
            var e = Example("E1");
            var i = e["input"];
            var cfg = Cfg();
            var r = Press.Compute(PressFor(i, PressMeaning.Gauge, (double)i["gauge_MPa"]), cfg);
            var x = e["expected"];
            Near((double)x["ramArea_mm2"], r.RamArea_mm2, 0.01, "A_ram");
            Near((double)x["force_N"], r.Force_N, 0.01, "F");
            Near((double)x["pLC1_MPa"], r.PLC1, 0.01, "p_LC1");
            Near(3142, r.PLC1 / Si.PsiToMPa, 0.05, "p_LC1 psi (hand)");
            Near(670.8, r.Force_N / 1000, 0.05, "F kN (hand)");
            // the same gauge on a 12 in ram gives 7,069 psi on the part
            var i12 = PressFor(i, PressMeaning.Gauge, (double)i["gauge_MPa"]);
            i12.RamArea_mm2 = Math.PI * 12 * 12 / 4 * 645.16;
            Near((double)x["pLC1On12inRam_psi"], Press.Compute(i12, cfg).PLC1 / Si.PsiToMPa, 0.01, "12 in ram");
        }

        [Fact]
        public void E7_press_on_the_part_gives_the_gauge()
        {
            var e = Example("E7");
            var i = e["input"];
            var r = Press.Compute(PressFor(i, PressMeaning.OnPart, (double)i["onPart_MPa"]), Cfg());
            Near((double)e["expected"]["gaugeForInput_psi"], r.GaugeForInput_MPa / Si.PsiToMPa, 0.01, "gauge");
            Near(955, r.GaugeForInput_MPa / Si.PsiToMPa, 0.05, "gauge (hand)");
        }

        private static PressInput PressFor(JToken i, PressMeaning m, double value)
        {
            var p = new PressInput
            {
                Class = ProcessClass.B_Compression, Meaning = m, Value = value, RamArea_mm2 = (double)i["ramArea_mm2"],
                RatedGauge_MPa = 3000 * Si.PsiToMPa, RatedForce_kN = 1e6, LandWidth = (double)i["land_mm"], MoldingPressureTyp = 7, MoldingPressureMax = 10,
            };
            for (var k = 0; k < (int)i["cavities"]; k++) p.Cavities.Add(new CavityArea { Id = "C" + k, Area = (double)i["area_mm2"], Perimeter = (double)i["perimeter_mm"] });
            return p;
        }

        [Fact]
        public void E2_class_A_typical_job_full_size()
        {
            var e = Example("E2");
            var design = (JObject)e["design"];
            var data = Data;
            var proc = MoldCalc.Run("process", design, null, null, data).Json;
            var press = MoldCalc.Run("press", design, null, null, data).Json;
            var size = MoldCalc.Run("size", design, null, null, data);
            var s = (JObject)size.RecordPatch["structural"];
            double Get(string key)
            {
                if (key.StartsWith("shrink.") || key.StartsWith("process.")) return (double)proc.SelectToken(key);
                if (key.StartsWith("press.")) return (double)press.SelectToken(key.Substring(6));
                if (key.StartsWith("structural.")) return (double)s.SelectToken(key.Substring(11));
                if (key.StartsWith("wall."))
                {
                    var parts = key.Split('.');
                    var w = s.SelectToken("results.walls").OfType<JObject>().Single(x => (string)x["id"] == "W-" + parts[1]);
                    return (double)w[parts[2]];
                }
                if (key.StartsWith("layout.")) return (double)s.SelectToken("results." + key);
                if (key.StartsWith("plates.") || key.StartsWith("bearing.") || key.StartsWith("lid.")) return (double)s.SelectToken("results." + key);
                if (key.StartsWith("pieces."))
                {
                    var parts = key.Split('.');
                    return (double)s.SelectToken("results.pieces").OfType<JObject>().Single(x => (string)x["id"] == parts[1])[parts[2]];
                }
                if (key.StartsWith("gauge.")) return (double)s.SelectToken(key);
                throw new ArgumentException(key);
            }
            foreach (var p in ((JObject)e["expected"]).Properties())
                Near((double)p.Value, Get(p.Name), Tol(e, p.Name), "E2 " + p.Name);

            // the design's hand figures that did not change
            Near(66.8, (double)s.SelectToken("materials.plates.E_GPa"), 0.1, "E 6061 at 100 C");
            Near(262.2, (double)s.SelectToken("materials.plates.Sy_MPa"), 0.1, "Sy");
            Near(1.75 * 25.4, (double)s.SelectToken("results.plates.base.t_mm"), 0.01, "base 1.75 in");
            Near(0.875 * 25.4, (double)s.SelectToken("results.plates.lid.t_mm"), 0.01, "lid 0.875 in");
            Assert.DoesNotContain(size.Checks, Check.Fails);
            Assert.Equal("TC well", (string)s.SelectToken("results.floors[0].governs"));
            // the drawn wall is set by the flash system and dowels, not by structure (the user's practice)
            Assert.All(s.SelectToken("results.walls").OfType<JObject>(), w => Assert.Equal("flash system and dowels", (string)w["governs"]));
        }

        [Fact]
        public void E3_class_B_wall_aluminium_and_P20()
        {
            var e = Example("E3");
            var x = (JObject)e["expected"];
            var tol = (double)e["tableDependentTolerancePct"];
            var cfg = Cfg();
            var al = AtTemperature.Get(Data.MoldGrade("al_6061_t651"), 150);
            Near((double)x["Al.E_MPa"], al.E, 0.01, "E");
            Near((double)x["Al.Sy_MPa"], al.Sy, 0.01, "Sy");
            var fa = Fatigue.AllowablesKf(al, 1e5, 1.5, cfg);
            Near((double)x["Al.allowFatigue_MPa"], fa.AllowFatigue, 0.1, "fatigue allowable");
            WallInput W(GradeAtT g, FatigueResult f, double root) => new WallInput
            {
                Id = "E3", P = 10.3, H = 25, L = 150, BPerp = 0, E = g.E, DeltaAllow = 0.05, RootFactor = root,
                AllowStatic = f.AllowStatic, AllowFatigue = f.AllowFatigue,
            };
            var noRoot = Walls.Size(W(al, new FatigueResult { AllowStatic = 1e9, AllowFatigue = 1e9 }, 1.0));
            Near((double)x["Al.wDefl_noRoot_mm"], noRoot.WDefl, tol, "Al no root");
            var a = Walls.Size(W(al, fa, 1.2));
            Near((double)x["Al.wDefl_mm"], a.WDefl, tol, "Al defl");
            Near((double)x["Al.wStruct_mm"], a.WStruct, tol, "Al struct (fatigue)");
            Assert.True(a.StockyFlag, "w/h > 0.5 must be flagged");

            var p20 = AtTemperature.Get(Data.MoldGrade("st_p20"), 150);
            var f5 = Fatigue.AllowablesKf(p20, 1e5, 1.5, cfg);
            var f6 = Fatigue.AllowablesKf(p20, 1e6, 1.5, cfg);
            Near((double)x["P20.allow1e5_MPa"], f5.Allow, 0.1, "P20 allow 1e5");
            Near((double)x["P20.allow1e6_MPa"], f6.Allow, 0.1, "P20 allow 1e6");
            var p5 = Walls.Size(W(p20, f5, 1.2));
            var p6 = Walls.Size(W(p20, f6, 1.2));
            Near((double)x["P20.wDefl_mm"], p5.WDefl, tol, "P20 defl");
            Near((double)x["P20.wStruct1e5_mm"], p5.WStruct, tol, "P20 1e5");
            Near((double)x["P20.wStruct1e6_mm"], p6.WStruct, tol, "P20 1e6");
            Assert.False(p5.StockyFlag);
        }

        [Fact]
        public void E4_bolted_corner_fails_with_scatter_and_six_bolts_pass()
        {
            var e = Example("E4");
            var x = (JObject)e["expected"];
            var cfg = Cfg();
            JointInput J(string size, double gripIn, int n) => new JointInput
            {
                Bolt = BoltSpec.From(Data.Fastener(size), cfg), Grip = gripIn * 25.4,
                Layers = new List<Layer> { new Layer { T = gripIn * 25.4, E = (double)e["input"]["memberE_MPa"], Aluminium = true } },
                AlphaMember = 2.36e-5, TempC = 100, DeltaT = 100, P = 56500.0 / 2 / n * Si.LbfToN, MemberSuT = 310 * 0.94,
            };
            var four = J("1/2-13", 2.0, 4);
            var at060 = Bolts.Evaluate(four, cfg, 0.60);
            Near((double)x["Fp_N"], at060.Fp, 0.01, "F_p");
            Near((double)x["C"], at060.C, 0.05, "C");
            Near((double)x["dF_N"], at060.DFth, 0.05, "dF");
            Near((double)x["at060.hotPctHotProof"], 100 * at060.HotMax / at060.FpT, 0.05, "hot % of hot proof");
            Near((double)x["at060.n0"], at060.N0, 0.05, "n0");
            Assert.False(at060.Passes);
            Assert.Contains("BOLT-HOT", at060.Failing);
            Assert.Contains("BOLT-SEP", at060.Failing);

            var d4 = Bolts.Design(four, cfg, false);
            Assert.False((bool)x["four.windowExists"]);
            Assert.True(d4.FractionHi < d4.FractionLo, "no preload passes both the hot and the separation checks for 4 x 1/2-13");
            Assert.False(d4.Passes);

            var d6 = Bolts.Design(J("1/2-13", 2.0, 6), cfg, false);
            Near((double)x["six.fractionHi"], d6.FractionHi, 0.05, "6 bolts f_hi");
            Near((double)x["six.fractionLo"], d6.FractionLo, 0.05, "6 bolts f_lo");
            Assert.True(d6.Passes, d6.Failing);
            Assert.InRange(d6.Fraction, d6.FractionLo, d6.FractionHi);

            var d58 = Bolts.Design(J("5/8-11", 2.5, 4), cfg, false);
            Near((double)x["five8x4.fractionHi"], d58.FractionHi, 0.05, "5/8 f_hi");
            Near((double)x["five8x4.fractionLo"], d58.FractionLo, 0.05, "5/8 f_lo");
            Assert.True(d58.Passes, d58.Failing);

            // S53: sizes ascending, counts upward - the first passing is 6 x 1/2-13
            var pick = new[] { ("1/2-13", 2.0, 4), ("1/2-13", 2.0, 6), ("5/8-11", 2.5, 4) }.First(t => Bolts.Design(J(t.Item1, t.Item2, t.Item3), cfg, false).Passes);
            Assert.Equal(("1/2-13", 2.0, 6), pick);
        }

        [Fact]
        public void E5_thermal_preload_M10_in_hot_6061()
        {
            var e = Example("E5");
            var x = (JObject)e["expected"];
            var cfg = Cfg();
            var j = new JointInput
            {
                Bolt = BoltSpec.From(Data.Fastener("M10"), cfg), Grip = 50,
                Layers = new List<Layer> { new Layer { T = 50, E = (double)e["input"]["memberE_MPa"], Aluminium = true } },
                AlphaMember = 2.36e-5, TempC = 150, DeltaT = 130, P = 0, MemberSuT = (double)e["input"]["memberSu_MPa"],
            };
            var r = Bolts.Evaluate(j, cfg, 0.60);
            Near((double)x["kb_N_mm"], r.Kb, 0.05, "k_b");
            Near((double)x["km_N_mm"], r.Km, 0.05, "k_m");
            Near((double)x["dF_N"], r.DFth, 0.05, "dF");
            Near((double)x["at060.hot_N"], r.HotMax, 0.05, "hot at 0.60");
            Near((double)x["at060.hotPctHotProof"], 100 * r.HotMax / r.FpT, 0.05, "% hot proof");
            Assert.False(r.Passes);
            var d = Bolts.Design(j, cfg, false);
            Near((double)x["fractionHi"], d.FractionHi, 0.05, "f_hi (head bearing governs)");
            Near((double)x["chosen"], d.Fraction, 1e-6, "chosen preload fraction");
            Assert.True(d.Passes, d.Failing);
        }

        [Fact]
        public void E6_shrink_bases()
        {
            var x = (JObject)Example("E6")["expected"];
            Near((double)x["scale"], Shrink.Factor(1.5, ShrinkBasis.DatasheetSteelMold, 170, 2.36e-5), 1e-4, "datasheet basis 170 C");
            Near(1.013468, Shrink.Factor(1.5, ShrinkBasis.DatasheetSteelMold, 170, 2.36e-5), 1e-4, "hand");
            Near((double)x["hotCavityAt100"], Shrink.Factor(1.5, ShrinkBasis.HotCavityColdPart, 100, 2.36e-5), 1e-4, "hot cavity 100 C");
            Near((double)x["measured"], Shrink.Factor(1.5, ShrinkBasis.MeasuredInMyMolds, 100, 2.36e-5), 1e-6, "measured: no metal term");
            Near((double)x["v1"], Shrink.Factor(1.5, ShrinkBasis.V1Style, 100, 2.36e-5), 1e-9, "v1 1+S");
            var r = Shrink.Compute(1.5, ShrinkBasis.DatasheetSteelMold, 170, 2.36e-5, "6061-T651");
            Near((double)x["effectivePct"], r.EffectivePct, 0.01, "effective");
            Assert.Equal("MOLD Shrink s=1.013468 (datasheet basis, 6061-T651 at 170 C)", r.FeatureName);
        }

        [Fact]
        public void E8_thread_engagement_needs_an_insert_and_25d_helical()
        {
            var x = (JObject)Example("E8")["expected"];
            var cfg = Cfg();
            var r = Threads.Engagement(BoltSpec.From(Data.Fastener("M10"), cfg), AtTemperature.Get(Data.MoldGrade("al_6061_t651"), 150), cfg);
            Near((double)x["LeOverD"], r.LeOverD, 0.05, "L_e/d");
            Assert.True(r.Insert);
            Near((double)x["helicalNeedD"], r.InsertNeedD, 0.05, "need at 1.2 d");
            Assert.Equal((double)x["helicalLengthD"], r.InsertLengthD);
            Assert.StartsWith("helical coil 2.5 d", r.InsertKind);
        }

        [Fact]
        public void E9_T16_plate_thickness()
        {
            var e = Example("E9");
            var sp = Plates.Span("SS4", 1, 100, 10, 66000, 0.3, 0.025, 0.5);
            Near((double)e["expected"]["t_mm"], sp.T, (double)e["tableDependentTolerancePct"], "t");
            Near(35.4, sp.T, 0.5, "hand");
        }

        [Fact]
        public void E10_insert_cold_interference()
        {
            Near((double)Example("E10")["expected"]["interference_mm"], Fits.InsertInterference(25, 23.6e-6, 12.8e-6, 140), 0.01, "interference");
        }

        [Fact]
        public void E11_split_options_are_the_hand_derived_ones()
        {
            var e = Example("E11");
            var i = e["input"];
            var x = (JObject)e["expected"];
            var input = new SplitInput
            {
                L = (double)i["L"], W = (double)i["W"], T = (double)i["T"], MaxMass_kg = (double)i["maxMass"], MaxBox = i["maxBox"].Select(v => (double)v).ToArray(),
                FlashBand = 0.625 * 25.4, FeatureClearance = 0.25 * 25.4, SubplateGrade = Data.MoldGrade("al_6061_t651"), Imperial = true,
            };
            foreach (var c in i["cavities"]) input.Pockets.Add(new Pocket { Id = (string)c["id"], U = (double)c["u"], V = (double)c["v"], SizeU = (double)c["su"], SizeV = (double)c["sv"], Depth = (double)c["depth"] });
            var opts = PlateSplit.Options(input, Data);
            Assert.Equal((int)x["options"], opts.Count);
            var o1 = opts[0];
            Assert.Equal((int)x["opt1.pieces"], o1.Pieces.Count);
            Assert.Equal(x["opt1.cutsU"].Select(v => (double)v), o1.CutsU.Select(v => Math.Round(v, 6)));
            Assert.Empty(o1.CutsV);
            var masses = x["opt1.masses"].Select(v => (double)v).ToList();
            for (var k = 0; k < 3; k++) Near(masses[k], o1.Pieces[k].Mass, 0.1, "piece " + (k + 1));
            Near((double)x["opt1.subplate_t"], o1.SubplateT, 0.01, "subplate thickness");
            Assert.True(o1.SubplateOk);
            Assert.Equal(x["opt1.subplate_cutsU"].Select(v => (double)v), o1.SubCutsU);
            Assert.All(o1.SubPieces, p => Near((double)x["opt1.subplate_mass"], p.Mass, 0.1, "subplate half"));
            Assert.Equal((int)x["opt2.pieces"], opts[1].Pieces.Count);
            Near((double)x["opt2.heaviest"], opts[1].Heaviest, 0.1, "option 2 heaviest");
            Assert.Equal((int)x["opt3.pieces"], opts[2].Pieces.Count);
            Near((double)x["opt3.heaviest"], opts[2].Heaviest, 0.1, "option 3 heaviest");
            // hand figures
            Near(14.136, o1.Pieces[0].Mass, 0.01, "hand end piece");
            Near(11.813, o1.Pieces[1].Mass, 0.01, "hand middle piece");
        }
    }
}

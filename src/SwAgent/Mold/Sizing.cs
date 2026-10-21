using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Mold.Structural.Fe;

namespace SwAgent.Mold
{
    /// <summary>Everything one calculator pass produced: JSON blocks for the record, the checks and notes.</summary>
    public sealed class SizingResult
    {
        public Design Design;
        public GradeAtT Plate, Insert;
        public ShrinkResult Shrink;
        public ChargeResult Charge;
        public PressResult Press;
        public GapResult Gap;
        public LayoutResult Layout;
        public JObject Process = new JObject(), ShrinkJson = new JObject(), PressJson = new JObject(), Structural = new JObject(), Hardware = new JObject(), Summary = new JObject();
        public List<JObject> Checks = new List<JObject>();
        public List<string> Notes = new List<string>();
        public double? GaugeMax_MPa;
        public string GaugeMaxBasis;
        public List<Sizing.GaugeCap> GaugeCaps = new List<Sizing.GaugeCap>();
        public bool Advisory;
    }

    /// <summary>
    /// The deterministic design pass behind mold_calc (design 2.3.2): the same design JSON always gives the same numbers,
    /// checks and text (no clocks, no randomness, ordered output), so `check` can be re-run safely. Each block cites the
    /// formula numbers of design section 3 and the critique items that changed them.
    /// </summary>
    public static class Sizing
    {
        // ------------------------------------------------------------------ shared prelude

        private static GradeAtT Grade(JObject row, double t) => AtTemperature.Get(row, t);

        public static SizingResult Prelude(JObject design, MoldData data, Cfg cfg)
        {
            var d = Design.Read(design, data);
            var r = new SizingResult { Design = d };
            r.Plate = Grade(d.PlateGrade, d.TempC);
            r.Insert = d.InsertGrade != null ? Grade(d.InsertGrade, d.TempC) : null;
            if (d.Class == ProcessClass.C_Positive)
            {
                r.Advisory = true;
                r.Notes.Add("A10: " + (string)d.PartRow["processLabel"] + " needs a positive mold (force + loading chamber, 0.025-0.075 mm per side): advisory plan only - not built by this tool in v1.");
                r.Checks.Add(Check.Status("SCOPE-CLASS-C", "scope", "warn", "A10: class C (positive) molds are not built in v1", "advisory numbers only"));
            }
            // every action reports the scope (the record's scope checks are replaced by each run)
            if (d.Class == ProcessClass.B_Compression && d.HasGeometry && d.PartH > Math.Min(d.PartU, d.PartV))
            {
                r.Advisory = true;
                r.Checks.Add(Check.Status("SCOPE-SEMI-POSITIVE", "scope", "warn", "A10: class B part deeper than wide needs a semi-positive mold", "advisory plan only - not built by this tool in v1"));
            }
            return r;
        }

        // ------------------------------------------------------------------ process: S64-S70

        public static void Process(SizingResult r, Cfg cfg, MoldData data)
        {
            var d = r.Design;
            var basis = Shrink.ParseBasis(d.ShrinkBasis);
            r.Shrink = Shrink.Compute(d.ShrinkPct, basis, d.TempC, r.Plate.Cte_perK, r.Plate.Short, d.ShrinkPerAxisPct,
                d.ProcessType == "preform_sinter", cfg.D("thermal.alphaRefSteel_perK", 1.2e-5));
            r.Notes.AddRange(r.Shrink.Notes);
            if ((string)d.PartRow["shrinkBasisConfidence"] == "estimate" && basis != ShrinkBasis.MeasuredInMyMolds)
                r.Notes.Add("A5: the shrink value and its basis are estimates for this material - confirm with your datasheet or a first article");
            var sx = r.Shrink;

            // groove perimeter for S67 (class B: centre line of a groove one land width out)
            var land = LandWidth(d, data);
            var perim = d.PartPerimeter * (sx.Su + sx.Sv) / 2;
            var ci = new ChargeInput
            {
                Class = d.Class, ProcessType = d.ProcessType, Cavities = d.Cavities, PartVolume_mm3 = d.PartVolume, Sg = d.Sg,
                ExcessPct = d.ExcessPct, OverflowPct = d.OverflowPct, PotLifeMin = d.PotLifeMin,
                MassRate_g_s = data.ShopNum("pour.massRate_g_per_s", 10), HandlingSeconds = data.ShopNum("pour.handlingSecondsPerCavity", 15),
                CureTypMin = d.CureMin, MoldTempTypC = Si.Num(d.PartRow.SelectToken("moldTempC.typ")) ?? d.TempC,
                MoldTempMinC = Si.Num(d.PartRow.SelectToken("moldTempC.min")) ?? d.TempC, MoldTempMaxC = Si.Num(d.PartRow.SelectToken("moldTempC.max")) ?? d.TempC,
                PressTempC = d.TempC, MaxSection_mm = d.MaxSection, BulkFactor = Si.Num(d.PartRow.SelectToken("bulkFactor.typ")) ?? 1,
                WellArea_mm2 = d.PartArea * sx.Su * sx.Sv, CavityVolume_mm3 = d.PartVolume * sx.Su * sx.Sv * sx.Sw,
            };
            if (d.Class == ProcessClass.A_Pour)
            {
                var lip = data.ShopMm("flash.lip", 3.175);
                var W = data.ShopMm("flash.grooveWidth", 12.7);
                var D = data.ShopMm("flash.grooveDepth", 3.175);
                ci.MoatCapacity_mm3 = (perim + 2 * Math.PI * (lip + W / 2)) * W * D;
                // the shop standard runs the moat out through the plate edge; a closed moat (no runners, e.g. a critical
                // face in the way) must hold the whole excess itself: layout.runnersExitPlate false or the override
                ci.RunnersExitPlate = (bool?)cfg.Param("runners_exit_plate") ?? (bool?)d.Json.SelectToken("layout.runnersExitPlate") ?? true;
            }
            else
            {
                ci.GroovePerimeter = perim + 2 * Math.PI * land;
                ci.GrooveRadiusMin = Si.Num(d.PartRow.SelectToken("flash.overflowGrooveRadiusMm.min")) ?? 0;
                ci.GrooveRadiusMax = Si.Num(d.PartRow.SelectToken("flash.overflowGrooveRadiusMm.max")) ?? 0;
            }
            r.Charge = Charge.Compute(ci, cfg);
            r.Notes.AddRange(r.Charge.Notes);
            foreach (var c in r.Charge.Checks) r.Checks.Add(ToJson(c, "process"));

            // S65 steel-safe dimensions
            var steelSafe = new JArray();
            if (d.Tolerances != null)
            {
                var smin = Si.Num(d.PartRow.SelectToken("shrinkPct.min")) ?? d.ShrinkPct;
                var smax = Si.Num(d.PartRow.SelectToken("shrinkPct.max")) ?? d.ShrinkPct;
                foreach (var t in d.Tolerances.OfType<JObject>())
                {
                    var L = Si.Num(t["nominal_mm"]) ?? Si.Num(t["L_mm"]);
                    var tol = Si.Num(t["tol_mm"]);
                    if (L == null || tol == null) continue;
                    if (Shrink.SteelSafe(smin, smax, L.Value, tol.Value))
                        steelSafe.Add(new JObject { ["id"] = t["id"], ["what"] = t["what"], ["spread_mm"] = Si.R((smax - smin) / 100 * L.Value, 3), ["tol_mm"] = tol });
                }
            }

            r.ShrinkJson = new JObject
            {
                ["pct"] = d.ShrinkPct, ["basis"] = sx.BasisCode, ["basisLabel"] = sx.BasisLabel,
                ["alphaRef"] = sx.AlphaRef, ["alphaCavity"] = sx.AlphaCavity, ["deltaT_K"] = Si.R(sx.DeltaT, 6),
                ["scale"] = new JArray(Math.Round(sx.Su, 6), Math.Round(sx.Sv, 6), Math.Round(sx.Sw, 6)),
                ["effectivePct"] = Math.Round(sx.EffectivePct, 4), ["feature"] = sx.FeatureName,
                ["pctAxis"] = d.ShrinkPerAxisPct == null ? null : new JArray(d.ShrinkPerAxisPct),
                ["about"] = "MOLD CS (parting-plane centroid; swScaleAboutCoordinateSystem, critique A6)",
                ["steelSafe"] = steelSafe,
            };
            var ch = r.Charge;
            r.Process = new JObject
            {
                ["class"] = ProcessClasses.Letter(d.Class), ["processType"] = d.ProcessType, ["processLabel"] = (string)d.PartRow["processLabel"],
                ["builtByV1"] = !r.Advisory,
                ["charge"] = new JObject { ["perCavity_g"] = Si.R(ch.PerCavity_g, 5), ["shot_g"] = Si.R(ch.Shot_g, 5), ["holdUpPct"] = ch.HoldUpPct, ["tolerancePct"] = cfg.D("charge.tolerancePct", 2), ["excessPct"] = d.ExcessPct },
                ["overflowRequired_mm3"] = Si.R(ch.OverflowRequired_mm3, 5),
                ["grooveRadius_mm"] = ch.GrooveRadius_mm > 0 ? (JToken)Si.R(ch.GrooveRadius_mm, 4) : null,
                ["pour"] = d.Class == ProcessClass.A_Pour ? new JObject
                {
                    ["secondsPerCavity"] = Si.R(ch.PourSecondsPerCavity, 4), ["secondsTotal"] = Si.R(ch.PourSecondsTotal, 4), ["limitSeconds"] = Si.R(ch.PourLimitSeconds, 4),
                    ["massRate_g_s"] = ci.MassRate_g_s, ["handlingSeconds"] = ci.HandlingSeconds,
                } : null,
                ["cureMin"] = Si.R(ch.CureMin, 4), ["cureNote"] = ch.CureNote,
                ["postCure"] = d.PartRow["postCure"]?.DeepClone(),
                ["loadingWell"] = ch.LoadingWellDepth_mm == null ? null : new JObject { ["depth_mm"] = Si.R(ch.LoadingWellDepth_mm.Value, 4), ["volume_mm3"] = Si.R(ch.LoadVolume_mm3 ?? 0, 5) },
                ["moldType"] = MoldType(d),
            };
        }

        private static string MoldType(Design d)
        {
            if (d.Class == ProcessClass.A_Pour) return "flash_full_land";
            if (d.Class == ProcessClass.C_Positive) return "positive (advisory)";
            return d.PartH > Math.Min(d.PartU, d.PartV) && d.PartU > 0 ? "semi_positive (advisory)" : "flash_stop_pads";
        }

        /// <summary>Smallest class B stop pad (0.75 in square); larger pads come from the press force (S33).</summary>
        public const double MinPadSide = 19.05;

        /// <summary>(S33) the side of each of the 4 square stop pads that carries the press force, on the 1/4 in step.</summary>
        public static double PadSide(double force_N, GradeAtT g, double cycles, Cfg cfg)
        {
            var need = Bearing.StopAreaNeeded(force_N, g, cycles, cfg);
            return Math.Max(MinPadSide, Math.Ceiling(Math.Sqrt(need / 4) / 6.35 - 1e-9) * 6.35);
        }

        /// <summary>Class A: the shop's lip; class B: the row's land width; class C: none (S2).</summary>
        public static double LandWidth(Design d, MoldData data)
        {
            if (d.Class == ProcessClass.A_Pour) return data.ShopMm("flash.lip", 3.175);
            if (d.Class == ProcessClass.B_Compression) return Si.Num(d.PartRow.SelectToken("flash.landWidthMm.typ")) ?? 1.0;
            return 0;
        }

        // ------------------------------------------------------------------ press: S1-S9, S35

        public static void PressBlock(SizingResult r, Cfg cfg, MoldData data, LayoutResult layout = null, double? stackHeight = null)
        {
            var d = r.Design;
            d.RequirePress();
            var sx = r.Shrink;
            var land = LandWidth(d, data);
            var area = d.PartArea * sx.Su * sx.Sv;
            var per = d.PartPerimeter * (sx.Su + sx.Sv) / 2;
            var groove = 0.0;
            if (d.Class == ProcessClass.B_Compression && r.Charge.GrooveRadius_mm > 0)
            {
                // A9: the rubber fills the overflow groove too; its area loads the press
                var rg = r.Charge.GrooveRadius_mm;
                groove = (per + 2 * Math.PI * (land + rg)) * 2 * rg;
            }
            var well = Si.Num(d.Json.SelectToken("part.wellArea_mm2")) ?? 0;
            var origins = layout?.Cavities ?? LayoutFromRecord(d);
            if (origins.Count > 0 && origins.Count != d.Cavities)
                throw new ArgumentException("The record's layout has " + origins.Count + " cavities but inputs.cavities is " + d.Cavities + ": run mold_calc layout or size again (with record) before press or check.");
            var pi = new PressInput
            {
                Class = d.Class, Meaning = d.Meaning, Value = d.PressValue, RatedForce_kN = d.RatedForce_kN, RatedGauge_MPa = d.RatedGauge_MPa,
                RamArea_mm2 = d.RamArea_mm2, LandWidth = land, MoldingPressureTyp = d.MoldingPressureTyp, MoldingPressureMax = d.MoldingPressureMax,
                ThermosetCompound = (string)d.PartRow["group"] == "thermoset_compound",
                PlatenL = d.PlatenL, PlatenW = d.PlatenW, Daylight = d.Daylight, Stroke = d.Stroke,
                FootprintL = layout?.FootprintL, FootprintW = layout?.FootprintW, StackHeight = stackHeight,
            };
            for (var k = 0; k < d.Cavities; k++)
            {
                // no layout yet (process/press before layout): the cavities sit on the press axis
                var o = k < origins.Count ? origins[k] : ("C" + (k + 1), 0.0, 0.0);
                pi.Cavities.Add(new CavityArea { Id = o.Item1, Area = area, Perimeter = per, GrooveArea = groove, WellArea = well, U = o.Item2, V = o.Item3, Depth = (d.DepthBase + d.DepthLid) * sx.Sw });
            }
            r.Press = Press.Compute(pi, cfg);
            foreach (var c in r.Press.Checks) r.Checks.Add(ToJson(c, "press"));
            var p = r.Press;
            r.PressJson = new JObject
            {
                ["ramArea_mm2"] = Si.R(p.RamArea_mm2, 6), ["force_kN"] = Si.R(p.Force_N / 1000, 6), ["meaning"] = d.Meaning.ToString(),
                ["aProj_mm2"] = Si.R(p.AProj_mm2, 6), ["aProjForce_mm2"] = Si.R(p.AProjForce_mm2, 6),
                ["pLC1_MPa"] = Si.R(p.PLC1, 5), ["pLC1x_MPa"] = Si.R(p.PLC1x, 5), ["pReq_MPa"] = Si.R(p.PReq, 5),
                ["fReq_kN"] = Si.R(p.FReq_N / 1000, 5), ["gaugeNeeded_MPa"] = Si.R(p.GaugeNeeded_MPa, 5),
                ["gauge"] = new JObject { ["recommended_MPa"] = Si.R(p.GaugeSet_MPa, 5), ["basis"] = "S7: F_req / A_ram", ["unitShown"] = d.GaugeUnit },
                ["loadCentre_mm"] = new JArray(Si.R(p.LoadCentreU, 5), Si.R(p.LoadCentreV, 5)),
            };
            if (d.Meaning == PressMeaning.OnPart) r.PressJson["gaugeForInput_MPa"] = Si.R(p.GaugeForInput_MPa, 5);
        }

        private static List<(string, double, double)> LayoutFromRecord(Design d)
        {
            var list = new List<(string, double, double)>();
            if (d.Json.SelectToken("layout.cavities") is JArray a)
                foreach (var c in a.OfType<JObject>())
                {
                    var o = c["origin"] as JArray;
                    list.Add(((string)c["id"] ?? ("C" + (list.Count + 1)), Si.Num(o?[0]) ?? 0, Si.Num(o?[1]) ?? 0));
                }
            return list;
        }

        // ------------------------------------------------------------------ layout

        /// <summary>The flash band, gaps and edge steel the layout uses before the walls are sized (w_geom only).</summary>
        public static (double GapU, double GapV, double Edge, string Moats, double Band) FlashGeometry(SizingResult r, Cfg cfg, MoldData data, double padSide = MinPadSide)
        {
            var d = r.Design;
            var clr = data.ShopMm("flash.featureClearance", 6.35);
            var dowelD = data.ShopMm("dowels.diameter", 12.7);
            if (d.Class == ProcessClass.A_Pour)
            {
                var lip = data.ShopMm("flash.lip", 3.175);
                var W = data.ShopMm("flash.grooveWidth", 12.7);
                var R = data.ShopMm("flash.cornerRadius", 6.35);
                var edge = CavityLayout.ClassAWall(lip, W, R, clr, dowelD, data.ShopMm("dowels.inset", 15.875), data.ShopMm("dowels.keyOffset", 6.35));
                var moats = (string)d.Json.SelectToken("layout.moats") ?? "separate";
                var gap = moats == "combined" ? 2 * lip + W : data.ShopMm("flash.separateMoatGap", 38.1);
                return (gap, gap, edge, moats, lip + W);
            }
            var land = LandWidth(d, data);
            var rg = r.Charge?.GrooveRadius_mm ?? 1.5;
            var band = land + 2 * rg;
            var al = r.Plate.IsAluminium;
            var minWall = cfg.D(al ? "minWallClassB_mm.aluminium" : "minWallClassB_mm.steel", al ? 15 : 10);
            // the steel between cavities is a cavity wall too: never below the class B minimum wall
            var gapB = Math.Max(Math.Max(2 * band + 3, al ? 12 : 10), minWall);
            // edge: land + groove + clearance + the corner hardware (dowel or stop pad, as sized from the force) + clearance
            var edgeB = Math.Max(minWall, band + clr + Math.Max(dowelD, padSide) + clr);
            return (gapB, gapB, edgeB, "separate", band);
        }

        public static LayoutResult LayoutBlock(SizingResult r, Cfg cfg, MoldData data, double? edgeU = null, double? edgeV = null, double? gapU = null, double? gapV = null)
        {
            var d = r.Design;
            d.RequireGeometry();
            var g = FlashGeometry(r, cfg, data);
            // requests the agent made (override or kept in the record): a pattern, a wider gap
            var gapReq = cfg.ParamNum("gap_mm") ?? Si.Num(d.Json.SelectToken("layout.gapRequest_mm"));
            var patternReq = cfg.ParamStr("pattern") ?? (string)d.Json.SelectToken("layout.patternRequest");
            var li = new LayoutInput
            {
                Cavities = d.Cavities, PartU = d.PartU * r.Shrink.Su, PartV = d.PartV * r.Shrink.Sv,
                // gap_mm (override) widens the steel between cavities, e.g. to leave dead steel for a split (S74)
                GapU = Math.Max(Math.Max(g.GapU, gapU ?? 0), gapReq ?? 0), GapV = Math.Max(Math.Max(g.GapV, gapV ?? 0), gapReq ?? 0),
                EdgeU = Math.Max(g.Edge, edgeU ?? 0), EdgeV = Math.Max(g.Edge, edgeV ?? 0),
                Pattern = patternReq, Imperial = d.Imperial, Moats = g.Moats,
            };
            r.Layout = CavityLayout.Compute(li, data);
            r.Layout.PatternRequest = patternReq;
            r.Layout.GapRequest = gapReq;
            return r.Layout;
        }

        // ------------------------------------------------------------------ size: S10-S62, S71-S75

        public static SizingResult Size(JObject design, MoldData data, Cfg cfg, bool asBuilt = false)
        {
            var r = Prelude(design, data, cfg);
            var d = r.Design;
            Process(r, cfg, data);
            d.RequireGeometry();
            var g = r.Plate;
            var sx = r.Shrink;

            // temperature beyond the data: refuse the structural part (A8)
            if (g.Service == "no-data")
            {
                r.Checks.Add(Check.Status("TEMP-" + g.Id, "temperature", "fail", "A8: no strength data above 250 C", g.Note));
                PressBlockSafe(r, cfg, data, null, null);
                return Finish(r, cfg);
            }

            // gap budget (S13-S14, A11)
            var flow = GapBudget.FlowClass(d.Class, d.ProcessType, (string)d.PartRow["viscosityClass"], d.ViscosityCp);
            r.Gap = GapBudget.Compute(flow, cfg, d.ClosureTol, d.WidthTol, cfg.ParamNum("delta_allow_mm"), cfg.ParamNum("delta_wall_mm"));

            // first layout with the flash geometry, press for the loads
            var fg = FlashGeometry(r, cfg, data);
            LayoutBlock(r, cfg, data);
            PressBlock(r, cfg, data, r.Layout);
            var p = r.Press;
            // class B: the cavity pressure at the force the operator may use (the setting or the recommended one, whichever
            // is higher), so the recommended gauge is always inside what was checked; class A: the hydrostatic load
            var pLC1 = p.PDesign;
            var field = PressureFieldMax(r);
            if (field != null && field.Value > pLC1)
            {
                r.Notes.Add("H3: the simulator's pressure field peaks at " + Si.F(field.Value) + " MPa, above the uniform " + Si.F(pLC1) + " MPa: walls and pins use the field peak");
                pLC1 = field.Value;
            }
            var pAbuse = d.Class == ProcessClass.A_Pour ? p.PLC1x : 0.0;
            // class B: the stop pads carry the press force (S33) and sit in the edge steel, so the edge is sized with them
            if (d.Class != ProcessClass.A_Pour) fg = FlashGeometry(r, cfg, data, PadSide(p.ForceDesign_N, g, d.Cycles, cfg));

            // allowables (S15-S19) at the wall root: K_t from the part's floor radius over the wall width
            var hBase = d.DepthBase * sx.Sw;
            var hLid = d.DepthLid * sx.Sw;
            var pu = d.PartU * sx.Su;
            var pv = d.PartV * sx.Sv;
            // S21 wall-root factor base + c w/h (Track A2's 3D pocket models: 1 + 2.8 w/h); a plain number is a constant factor
            var (rootBase, rootSlope) = RootFactor(cfg);
            var rootText = rootSlope == 0 ? Si.F(rootBase) : Si.F(Walls.OwnBulgeFactor) + " (" + Si.F(rootBase) + " + " + Si.F(rootSlope) + " w/h; above w/h " + Si.F(Walls.RootCalibratedTau) + ": an added bulge falling as h/w)";
            var tauMax = cfg.D("tauMax", 0.5);
            var kf = cfg.ParamNum("kf");
            // the required floor under each half's cavity (S26): the walls' floor-stretch term needs it (Walls.Stretch), and it
            // does not depend on the walls
            var al = g.IsAluminium;
            var tc = data.ShopMm("thermocouple.diameter", 6.35);
            double FloorUnder(double h) => h > 0 ? Plates.Floor(al, h, tc, cfg) : 0;

            // the wall as S21-S24 see it (the stress allowables and drawn widths are added per pass)
            WallInput WallIn(string id, double L, double bPerp, double h) => new WallInput
            {
                Id = id, P = pLC1 * cfg.D("lateralPressureFactor", 1.0), H = h, L = L, BPerp = bPerp, E = g.E, Nu = g.Nu,
                DeltaAllow = r.Gap.DeltaWall, SidesClamped = Walls.SidesClampedFor(L, bPerp, h), RootFactor = rootBase, RootSlope = rootSlope, Floor = FloorUnder(h),
            };

            WallResult SizeWall(string id, double L, double bPerp, double h, double wGeom, double wMin)
            {
                // K_t depends on r/w and w on the allowable: raise K_t until the wall sized with it has r/w that needs no
                // more (K_t only grows as w grows, so this ends at the latest at K_t = 2.5)
                var kt = Fatigue.Kt(d.CornerRadius / (0.3 * h), cfg);
                WallResult wr = null;
                for (var pass = 0; pass < 10; pass++)
                {
                    var fa = kf != null ? Fatigue.AllowablesKf(g, d.Cycles, kf.Value, cfg) : Fatigue.Allowables(g, d.Cycles, kt, cfg);
                    var wi = WallIn(id, L, bPerp, h);
                    wi.AllowStatic = fa.AllowStatic;
                    wi.AllowFatigue = fa.AllowFatigue;
                    wi.WGeom = wGeom;
                    wi.WMin = wMin;
                    wi.PAbuse = pAbuse > 0 ? pAbuse : (double?)null;
                    wi.SyT = g.Sy;
                    wr = Walls.Size(wi, tauMax);
                    var ktAt = Fatigue.Kt(d.CornerRadius / wr.WStruct, cfg);
                    if (kf != null || ktAt <= kt) break;
                    kt = ktAt;
                }
                return wr;
            }

            var minWallB = d.Class == ProcessClass.B_Compression ? cfg.D(g.IsAluminium ? "minWallClassB_mm.aluminium" : "minWallClassB_mm.steel", g.IsAluminium ? 15 : 10) : 0;
            var walls = new List<WallResult>();
            foreach (var (h, half) in new[] { (hBase, "B"), (hLid, "L") })
            {
                if (h <= 0) continue;
                // W-V: walls facing +-V (run along U, length pu); W-U: walls facing +-U (length pv)
                walls.Add(SizeWall("V-" + half, pu, pv, h, fg.Edge, minWallB));
                walls.Add(SizeWall("U-" + half, pv, pu, h, fg.Edge, minWallB));
            }
            // edge steel and the steel between cavities must carry the walls (partial loading loads a gap wall from one side)
            var needU = walls.Where(w => w.Id.StartsWith("U-")).Select(w => w.WStruct).DefaultIfEmpty(0).Max();
            var needV = walls.Where(w => w.Id.StartsWith("V-")).Select(w => w.WStruct).DefaultIfEmpty(0).Max();
            var lay = asBuilt && d.Json.SelectToken("layout.footprint_mm") is JArray ? RecordLayout(r, data) : LayoutBlock(r, cfg, data, Math.Max(needU, fg.Edge), Math.Max(needV, fg.Edge), needU, needV);
            r.Layout = lay;
            // re-run the press with the final layout (load centre, footprint) - same loads
            r.Checks.RemoveAll(c => ((string)c["id"]).StartsWith("PRESS-"));

            // plates (S26-S28 case A; S29-S32 other supports)
            var tFloor = Plates.Floor(al, hBase, tc, cfg);
            var longest = Math.Max(lay.FootprintL, lay.FootprintW);
            var baseReq = hBase > 0 ? Plates.PocketPlate(al, tFloor, hBase, longest, cfg) : Plates.Lid(al, longest, cfg);
            // the lid carries its own thermocouple well (shop standard: one per piece), so it keeps d_TC + 2 x 6 mm of steel
            double LidReq(double? fraction) => hLid > 0 ? Plates.PocketPlate(al, Plates.Floor(al, hLid, tc, cfg), hLid, longest, cfg, lengthFraction: fraction)
                                                      : Plates.Lid(al, longest, cfg, d.PlugHeight, tc + 2 * cfg.D("steelAroundTcWell_mm", 6), fraction);
            var lidReq = LidReq(null);
            var lidRule = "S28: 0.05 x longest side, min plate, plug + 10 mm";
            var support = d.FlatOnPlaten ? Plates.ParseSupport(cfg.ParamStr("support") ?? (string)d.Json.SelectToken("structural.inputs.support")) : Plates.ParseSupport(cfg.ParamStr("support") ?? "B");
            var spanNotes = new JObject();
            // the as-built check (A12) checks the plates as they are: the measured thickness (structural.asBuilt), else the
            // planned one in the record - never a freshly re-sized plate, which would pass by construction
            // measured by mold_bodies: the thickest body of the role (its W extent) when the record has no explicit value
            double? Measured(string role) => (d.Json["bodies"] as JArray)?.OfType<JObject>().Where(b => string.Equals((string)b["role"], role, StringComparison.OrdinalIgnoreCase))
                .Select(b => Si.Lengths(b["box_mm"])).Where(x => x != null && x.Length == 3 && x[2] > 0).Select(x => (double?)x[2]).DefaultIfEmpty(null).Max();
            double? Built(string key, string role, string planned) => asBuilt ? Num(d.Json, "structural.asBuilt." + key) ?? Measured(role) ?? Num(d.Json, planned) : null;
            var tBaseBuilt = Built("baseThickness_mm", "base", "structural.results.plates.base.t_mm");
            var tLidBuilt = Built("lidThickness_mm", "lid", "structural.results.plates.lid.t_mm");
            if (support != Support.A)
                baseReq = Math.Max(baseReq, SpanBlock(r, cfg, data, support, lay, hBase, spanNotes, tBaseBuilt - hBase) + hBase);
            var baseT = StockRounding.Thickness(tBaseBuilt ?? baseReq, d.PlateGrade, data, d.Imperial);
            var lidT = StockRounding.Thickness(tLidBuilt ?? lidReq, d.PlateGrade, data, d.Imperial);
            var tBase = tBaseBuilt ?? baseT.Finished;
            var tLid = tLidBuilt ?? lidT.Finished;
            if (tBaseBuilt != null)
            {
                // the floor under the deepest cavity and the plate rules (S26-S28) at the as-built thickness
                if (hBase > 0)
                    r.Checks.Add(ToJson(new JCheck { Id = "PLATE-BASE-T", Type = "deflection", Demand = tBase, Capacity = hBase + tFloor, Unit = "mm", Lower = true, Method = "S26-S27 (as built): base thickness >= cavity depth + required floor" + (support == Support.A ? "" : " (the span is checked at this thickness)") }, "size", "BASE-1"));
            }
            // the lid's own cavity half has a floor too (the walls' floor stretch reads it): the same rule at the as-built thickness
            if (tLidBuilt != null && hLid > 0)
                r.Checks.Add(ToJson(new JCheck { Id = "PLATE-LID-T", Type = "deflection", Demand = tLid, Capacity = hLid + FloorUnder(hLid), Unit = "mm", Lower = true, Method = "S26-S27 (as built): lid thickness >= its cavity depth + required floor" }, "size", "LID-1"));

            // masses and boxes (S71-S73), lid limit (A2)
            var cavVolBase = d.PartVolume * sx.Su * sx.Sv * sx.Sw * (d.PartH > 0 ? hBase / (d.PartH * sx.Sw) : 1);
            var cavVolLid = d.PartVolume * sx.Su * sx.Sv * sx.Sw * (d.PartH > 0 ? hLid / (d.PartH * sx.Sw) : 0);
            var rho = g.Density_kg_m3 * 1e-9;
            var massBase = (lay.FootprintL * lay.FootprintW * tBase - d.Cavities * cavVolBase) * rho;
            var massLid = (lay.FootprintL * lay.FootprintW * tLid - d.Cavities * cavVolLid) * rho;
            massBase = Num(d.Json, "structural.asBuilt.baseMass_kg") ?? massBase;
            massLid = Num(d.Json, "structural.asBuilt.lidMass_kg") ?? massLid;
            var lift = Lifting.LidLimit(d.LidLiftAt, d.LidReach_cm, d.LidLiftsPerHour, cfg);
            var lidLimit = d.MaxLidMass ?? lift.Rwl_kg;
            var lidBasis = d.MaxLidMass != null ? "page max_lid_mass" : lift.Basis;
            // critique A2.6: on the platen (case A) nothing structural needs the 0.05 L rule; when it is what pushes the lid
            // over the lift limit, drop it (the minimum plate, plug + 10 mm and the TC well still hold)
            if (support == Support.A && massLid > lidLimit && Num(d.Json, "structural.asBuilt.lidThickness_mm") == null && Num(d.Json, "structural.asBuilt.lidMass_kg") == null)
            {
                var relaxed = LidReq(0);
                if (relaxed < lidReq - 1e-9)
                {
                    var t2 = StockRounding.Thickness(relaxed, d.PlateGrade, data, d.Imperial);
                    if (t2.Finished < tLid - 1e-9)
                    {
                        r.Notes.Add($"A2.6: the lid was over the {Si.F(lidLimit)} kg lift limit at {Si.F(tLid)} mm because of the 0.05 L rule; on the platen nothing structural needs it, so the lid is {Si.F(t2.Finished)} mm");
                        lidReq = relaxed;
                        lidT = t2;
                        tLid = t2.Finished;
                        massLid = (lay.FootprintL * lay.FootprintW * tLid - d.Cavities * cavVolLid) * rho;
                        lidRule = "S28 without the 0.05 L rule (A2.6: lid over the lift limit, case A): min plate, plug + 10 mm, TC well";
                    }
                }
            }
            var plateLimit = d.MaxPlateMass;
            var pieces = new JArray();
            var allowLW = data.ShopMm("plates.blankAllowancePerSide", 3);
            var allowT = data.ShopMm("plates.blankAllowanceThicknessPerSide", 1.5);
            void Piece(string id, string role, double l, double w, double t, double mass, StockChoice st, double[] u = null, double[] v = null)
            {
                var box = new[] { l, w, t };
                var boxOk = PlateSplit.BoxFits(box, d.MaxPlateSize, allowLW, allowT);
                var pj = new JObject
                {
                    ["id"] = id, ["role"] = role, ["material"] = g.Id, ["t_mm"] = Si.R(t, 6), ["stock_mm"] = Si.R(st.Stock, 6),
                    ["box_mm"] = new JArray(box.Select(x => Si.R(x, 6))), ["mass_kg"] = Si.R(mass, 5),
                    ["blank_mm"] = new JArray(Si.R(box[0] + 2 * allowLW, 6), Si.R(box[1] + 2 * allowLW, 6), Si.R(st.Stock + 2 * allowT, 6)),
                };
                if (u != null) { pj["u_mm"] = new JArray(Si.R(u[0], 6), Si.R(u[1], 6)); pj["v_mm"] = new JArray(Si.R(v[0], 6), Si.R(v[1], 6)); }
                pieces.Add(pj);
                if (plateLimit != null) r.Checks.Add(ToJson(new JCheck { Id = "WEIGHT-" + id, Type = "weight", Demand = mass, Capacity = plateLimit.Value, Unit = "kg", Method = "S73: each machined piece <= max_plate_mass" }, "size", id));
                if (d.MaxPlateSize != null)
                    r.Checks.Add(Check.Status("BOX-" + id, "box", boxOk ? "pass" : "fail", "S72: sorted box + blank allowance <= sorted max_plate_size", boxOk ? "" : (role == "lid" && d.Class == ProcessClass.A_Pour ? "no split lid for class A" : "run mold_calc split"), id));
            }
            var basePlan = SplitPlan(d.Json, "base");
            var lidPlan = SplitPlan(d.Json, "lid");
            if (lidPlan != null && d.Class == ProcessClass.A_Pour)
            {
                r.Checks.Add(Check.Status("SPLIT-LID", "scope", "fail", "S74: a class A lid is never split (its seam would cross the seal)", "remove structural.split.lid; use fewer cavities, a smaller footprint, lightening pockets or a lift assist"));
                lidPlan = null;
            }
            // machined pieces with a measured mass (mold_bodies writes "mass_kg": null when it cannot weigh a body)
            var machined = new[] { "base", "subplate", "middle", "lid", "insert", "stop" };
            var built = asBuilt ? (d.Json["bodies"] as JArray)?.OfType<JObject>().Where(b => (b["mass_kg"]?.Type == JTokenType.Float || b["mass_kg"]?.Type == JTokenType.Integer)
                && machined.Contains(((string)b["role"] ?? "base").ToLowerInvariant()) && Si.Lengths(b["box_mm"])?.Length == 3).ToList() : null;
            // the chosen split (S74): the pieces between the cut lines, keyed into a (possibly split) backing plate
            double SplitPieces(JObject plan, string plateId, string subId, string role, string subRole, double t, double depth, StockChoice st)
            {
                var si = new SplitInput { L = lay.FootprintL, W = lay.FootprintW, T = t, Density_kg_m3 = g.Density_kg_m3 };
                foreach (var c in lay.Cavities) si.Pockets.Add(new Pocket { Id = c.Id, U = c.U, V = c.V, SizeU = pu, SizeV = pv, Depth = depth });
                var tSub = Si.Num(plan.SelectToken("subplate.t_mm")) ?? 22.225;
                var subStock = StockRounding.Thickness(tSub, d.PlateGrade, data, d.Imperial);
                var total = 0.0;
                foreach (var pc in PlateSplit.Pieces(si, Cuts(plan["cutsU_mm"]), Cuts(plan["cutsV_mm"]), t, plateId, true))
                {
                    total += pc.Mass;
                    Piece(pc.Id, role, pc.Box[0], pc.Box[1], t, pc.Mass, st, new[] { pc.U0, pc.U1 }, new[] { pc.V0, pc.V1 });
                }
                foreach (var pc in PlateSplit.Pieces(si, Cuts(plan.SelectToken("subplate.cutsU_mm")), Cuts(plan.SelectToken("subplate.cutsV_mm")), tSub, subId, false))
                {
                    total += pc.Mass;
                    Piece(pc.Id, subRole, pc.Box[0], pc.Box[1], tSub, pc.Mass, subStock, new[] { pc.U0, pc.U1 }, new[] { pc.V0, pc.V1 });
                }
                return total;
            }
            if (built != null && built.Count > 0)
            {
                // the as-built check: the bodies mold_bodies measured (mass from the grade density, extreme-point boxes)
                massBase = 0;
                massLid = 0;
                foreach (var b in built)
                {
                    var role = (string)b["role"] ?? "base";
                    var box = Si.Lengths(b["box_mm"]);
                    var m = (double)b["mass_kg"];
                    // a split class B lid's backing plate is a subplate whose id starts LSUB (the record's body roles)
                    var id = (string)b["id"] ?? "";
                    if (role == "lid" || (role == "subplate" && id.StartsWith("LSUB", StringComparison.Ordinal))) massLid += m;
                    else if (role == "base" || role == "subplate") massBase += m;
                    Piece((string)b["id"], role, box[0], box[1], box[2], m, new StockChoice { Finished = box[2], Stock = box[2] + 2 * data.ShopMm("plates.skimPerFace", 1.5) });
                }
            }
            else
            {
                if (basePlan != null) massBase = SplitPieces(basePlan, "BASE", "SUB", "base", "subplate", tBase, hBase, baseT);
                else Piece("BASE-1", "base", lay.FootprintL, lay.FootprintW, tBase, massBase, baseT);
                // a split class B lid is still lifted as one bolted unit: WEIGHT-LID takes the whole assembly
                if (lidPlan != null) massLid = SplitPieces(lidPlan, "LID", "LSUB", "lid", "subplate", tLid, hLid, lidT);
                else Piece("LID-1", "lid", lay.FootprintL, lay.FootprintW, tLid, massLid, lidT);
            }
            r.Checks.Add(ToJson(new JCheck { Id = "WEIGHT-LID", Type = "lifting", Demand = massLid, Capacity = lidLimit, Unit = "kg", Method = "S73 + A2: lid <= " + lidBasis }, "size"));
            if (d.MaxBaseMass != null) r.Checks.Add(ToJson(new JCheck { Id = "WEIGHT-BASE", Type = "weight", Demand = massBase, Capacity = d.MaxBaseMass.Value, Unit = "kg", Method = "S73: sum of base pieces <= max_base_mass" }, "size"));
            var liftIndex = Lifting.Index(massLid, lift.Rwl_kg);

            // walls at the final widths (edges after rounding, gaps), abuse caps
            var edgeU = lay.EdgeU;
            var edgeV = lay.EdgeV;
            var wallJson = new JArray();
            double wallAbuseCap = double.PositiveInfinity;
            var finalWalls = new List<WallResult>();
            foreach (var w in walls)
            {
                var isU = w.Id.StartsWith("U-");
                var h = w.Id.EndsWith("-B") ? hBase : hLid;
                var drawn = Num(d.Json, "structural.asBuilt.walls." + w.Id) ?? Math.Min(isU ? edgeU : edgeV, d.Cavities > 1 ? (isU && lay.Cols > 1 ? lay.GapU : !isU && lay.Rows > 1 ? lay.GapV : double.MaxValue) : double.MaxValue);
                if (!(drawn > 0) || double.IsInfinity(drawn))
                    throw new ArgumentException("Wall " + w.Id + " is " + Si.F(drawn) + " mm wide: the layout or structural.asBuilt.walls is inconsistent - re-measure (mold_bodies) or run mold_calc size again.");
                var wr = SizeWall(w.Id, isU ? pv : pu, isU ? pu : pv, h, drawn, 0);
                wr.WDrawn = drawn;
                var co = Coefficients.Get(wr.Bc, wr.LOverH, drawn / h, g.Nu, 1.0);
                // re-evaluate demand at the drawn width
                var kt = Fatigue.Kt(d.CornerRadius / drawn, cfg);
                var fa = kf != null ? Fatigue.AllowablesKf(g, d.Cycles, kf.Value, cfg) : Fatigue.Allowables(g, d.Cycles, kt, cfg);
                // the same sideways pressure (lambda x p_LC1) that SizeWall used
                var pLat = pLC1 * cfg.D("lateralPressureFactor", 1.0);
                wr.Sigma = co.Beta * pLat * h * h / (drawn * drawn) + pLat * (isU ? pu : pv) / (2 * drawn);
                var wallIn = WallIn(w.Id, isU ? pv : pu, isU ? pu : pv, h);
                // the floor that is there (plate minus cavity depth) when it is thinner than the required one - an as-built
                // plate can be (PLATE-BASE-T / PLATE-LID-T fail it, and its walls' floor stretch must say so too)
                var actualFloor = (w.Id.EndsWith("-B") ? tBase - hBase : tLid - hLid);
                if (actualFloor > 0 && actualFloor < wallIn.Floor) wallIn.Floor = actualFloor;
                wr.Deflection = Walls.Deflection(wallIn, wr.Bc, drawn);
                wr.SigmaAllow = fa.Allow;
                var noSoln = wr.Checks.FirstOrDefault(c => c.Id.StartsWith("WALL-NOSOLN-", StringComparison.Ordinal));
                wr.Checks.Clear();
                if (noSoln != null) wr.Checks.Add(noSoln);
                wr.Checks.Add(new JCheck { Id = "WALL-DEFL-" + w.Id, Type = "deflection", Demand = wr.Deflection, Capacity = r.Gap.DeltaWall, Unit = "mm", LoadCase = "LC1", Method = "S21 + A11: " + rootText + " alpha p h^4/(E w^3)" + Walls.StretchText(wallIn) + " <= 0.25 x width tolerance" });
                wr.Checks.Add(new JCheck { Id = "WALL-STRESS-" + w.Id, Type = fa.AllowFatigue < fa.AllowStatic ? "fatigue" : "stress", Demand = wr.Sigma, Capacity = fa.Allow, Unit = "MPa", LoadCase = "LC1", Method = "S22-S23: beta p h^2/w^2 + p b/(2w) <= min(Sy/2, fatigue), K_f " + Si.F(fa.Kf) });
                if (wr.StockyFlag)
                    wr.Checks.Add(new JCheck { Id = "WALL-STOCKY-" + w.Id, Type = "stress", Demand = wr.TauStruct, Capacity = tauMax, Unit = "w/h", Status = "warn", Method = "S24: w/h > 0.5 - plate theory no longer applies; prefer steel, a keyed wall, or a 3D check" });
                if (pAbuse > 0)
                {
                    // S24: beta for a plate-like wall; the strip upper bound only for a stocky one (w/h above tauMax)
                    var stocky = drawn / h > tauMax;
                    var f1 = Walls.AbuseFactor(stocky, co.Beta, h, isU ? pu : pv, drawn);
                    wr.SigmaAbuse = pAbuse * f1;
                    wr.PAbuseCap = g.Sy / f1;
                    wallAbuseCap = Math.Min(wallAbuseCap, wr.PAbuseCap.Value);
                    wr.Checks.Add(new JCheck { Id = "WALL-ABUSE-" + w.Id, Type = "stress", Demand = wr.SigmaAbuse.Value, Capacity = g.Sy, Unit = "MPa", LoadCase = "LC1x", Method = Walls.AbuseMethod(stocky) });
                }
                foreach (var c in wr.Checks) r.Checks.Add(ToJson(c, "size"));
                finalWalls.Add(wr);
                wallJson.Add(new JObject
                {
                    ["id"] = "W-" + w.Id, ["half"] = w.Id.EndsWith("-B") ? "base" : "lid", ["faces"] = isU ? "+-U" : "+-V",
                    ["L_mm"] = Si.R(isU ? pv : pu, 6), ["h_mm"] = Si.R(h, 6), ["LoverH"] = Si.R(wr.LOverH, 4),
                    ["w_defl_mm"] = Si.R(w.WDefl, 5), ["w_stress_mm"] = Si.R(w.WStress, 5), ["w_struct_mm"] = Si.R(w.WStruct, 5),
                    ["w_drawn_mm"] = Si.R(drawn, 6), ["alpha"] = Si.R(co.Alpha, 5), ["beta"] = Si.R(co.Beta, 5),
                    ["sigma_MPa"] = Si.R(wr.Sigma, 5), ["allow_MPa"] = Si.R(fa.Allow, 5), ["Kf"] = Si.R(fa.Kf, 4), ["Sa_MPa"] = Si.R(fa.Sa, 5), ["sigmaMaxR0_MPa"] = Si.R(fa.SigmaMaxR0, 5),
                    ["deflection_mm"] = Si.R(wr.Deflection, 5), ["floorStretch_mm"] = Si.R(Walls.Stretch(wallIn), 5), ["floor_mm"] = Si.R(wallIn.Floor, 5), ["governs"] = drawn > w.WStruct + 1e-9 ? (d.Class == ProcessClass.A_Pour ? "flash system and dowels" : "flash band, hardware and class B minimum") : (w.WStress > w.WDefl ? "stress" : "deflection"),
                    ["stocky"] = wr.StockyFlag, ["noSolution"] = w.NoSolution, ["sigmaAbuse_MPa"] = wr.SigmaAbuse == null ? null : (JToken)Si.R(wr.SigmaAbuse.Value, 5),
                });
            }

            // bearing (S33-S36)
            var force = p.Force_N;
            double aBear;
            string bearBasis;
            var stops = new JArray();
            var area = d.PartArea * sx.Su * sx.Sv;
            var per = d.PartPerimeter * (sx.Su + sx.Sv) / 2;
            if (d.Class == ProcessClass.A_Pour)
            {
                var lip = data.ShopMm("flash.lip", 3.175);
                var W = data.ShopMm("flash.grooveWidth", 12.7);
                var moat = (per + 2 * Math.PI * (lip + W / 2)) * W;
                var runners = RunnerArea(r, data, lay, pu, pv);
                aBear = lay.FootprintL * lay.FootprintW - d.Cavities * (area + moat) - runners;
                bearBasis = "S33 class A: parting face - cavities - moat - runners (estimate until mold_bodies measures it)";
            }
            else
            {
                var side = PadSide(force, g, d.Cycles, cfg);
                aBear = 4 * side * side;
                bearBasis = "S33 class B: 4 hardened stop pads, " + Si.F(side) + " mm square, on the plate (weaker) material";
                var sgrade = data.MoldGrade("st_h13") ?? d.InsertGrade;
                for (var k = 0; k < 4; k++)
                    stops.Add(new JObject { ["id"] = "STOP-" + (k + 1), ["side_mm"] = Si.R(side, 6), ["grade"] = (string)sgrade?["id"], ["hardness"] = "40-50 HRC", ["at"] = "corner " + (k + 1) + ", outside the overflow" });
                // (S36) lands alone would crush?
                var aLand = d.Cavities * per * LandWidth(d, data);
                var dbx = Bearing.Divisor(d.Cycles, g, cfg, out _);
                if (aLand > 0 && force / aLand > g.Sy / dbx)
                    r.Notes.Add("S36: the lands alone would see " + Si.F(force / aLand) + " MPa against " + Si.F(g.Sy / dbx) + " MPa - stop pads are mandatory (included)");
            }
            aBear = Num(d.Json, "structural.asBuilt.bearingArea_mm2") ?? aBear;
            var bear = Bearing.Compute(force, aBear, p.RamArea_mm2, g, d.Cycles, cfg);
            r.Checks.Add(ToJson(new JCheck { Id = "STR-BEAR-PL1", Type = "bearing", Demand = bear.Stress, Capacity = bear.Allow, Unit = "MPa", LoadCase = "LC2", Method = bear.DbBasis }, "size", "BASE-1"));

            // multi-piece base (keys, bolts) when a split was chosen
            var hardwareBolts = new JArray();
            var keysJson = new JArray();
            double jointAbuseCap = double.PositiveInfinity, jointLc1Cap = double.PositiveInfinity;
            if (basePlan != null) SplitJoints(r, cfg, data, basePlan, "BASE", "SUB", pLC1, pAbuse, hBase, tBase, hardwareBolts, keysJson, ref jointAbuseCap, ref jointLc1Cap);
            if (lidPlan != null) SplitJoints(r, cfg, data, lidPlan, "LID", "LSUB", pLC1, pAbuse, hLid, tLid, hardwareBolts, keysJson, ref jointAbuseCap, ref jointLc1Cap);

            // threads for the handle / insert screws are listed in the hardware; dowels (S58), inserts (S59), pins (S61-S63)
            var dowelD = data.ShopMm("dowels.diameter", 12.7);
            var inset = data.ShopMm("dowels.inset", 15.875);
            var diag = Math.Sqrt(Math.Pow(lay.FootprintL - 2 * inset, 2) + Math.Pow(lay.FootprintW - 2 * inset, 2));
            var scheme = Fits.Dowels(diag, g.Cte_perK, g.Cte_perK, d.TempC, d.SlideIn, (int)data.ShopNum("dowels.count", 4), cfg);
            if (scheme.AskShop) r.Notes.Add(scheme.Note);
            var dowels = new JArray();
            var dl = new[] { (-1.0, -1.0), (1.0, 1.0), (1.0, -1.0), (-1.0, 1.0) };
            var keyOff = data.ShopMm("dowels.keyOffset", 6.35);
            for (var k = 0; k < Math.Min(scheme.Count, 4); k++)
            {
                var (su_, sv_) = dl[k];
                var u = su_ * (lay.FootprintL / 2 - inset) + (k == 1 ? -keyOff : 0);
                var v = sv_ * (lay.FootprintW / 2 - inset);
                var kind = k == 0 ? "round" : scheme.RoundPlusRelieved ? "slot (radial from D1)" : "round";
                dowels.Add(new JObject { ["id"] = "D" + (k + 1), ["d_mm"] = Si.R(dowelD, 6), ["kind"] = kind, ["at"] = new JArray(Si.R(u, 6), Si.R(v, 6), 0) });
            }

            // fragile features (S61-S63): which become steel inserts in aluminium construction, and the pin checks
            var pinsJson = new JArray();
            var fitsJson = new JArray();
            double pinAbuseCap = double.PositiveInfinity;
            var tierNow = Tier(d.Cycles, cfg);
            var tierT3 = string.CompareOrdinal(tierNow, "T3") >= 0;
            if (d.Fragile != null)
                foreach (var f in d.Fragile.OfType<JObject>())
                {
                    var id = (string)f["id"] ?? ("K" + (pinsJson.Count + 1));
                    var kind = ((string)f["kind"] ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
                    var dd = Si.Num(f["d_mm"]) ?? 0;
                    var hh = Si.Num(f["h_mm"]) ?? 0;
                    var shutOff = (bool?)f["shutOff"] == true || kind == "shut_off";
                    var plateAl = r.Plate.IsAluminium;
                    if (kind == "steel_wall")
                    {
                        // S61: steel under 3 mm between features (w_mm measured on the molded part, the gap between them)
                        var w = Si.Num(f["w_mm"]);
                        if (w == null || w <= 0) { Unevaluated(r, id, kind, "a steel_wall entry needs w_mm"); continue; }
                        var lim = cfg.D("pins.insertIfSteelWallBelow_mm", 3.0);
                        var thin = w.Value * Math.Min(sx.Su, sx.Sv) < lim - 1e-9;
                        pinsJson.Add(new JObject
                        {
                            ["id"] = id, ["kind"] = "steel_wall", ["w_mm"] = Si.R(w.Value * Math.Min(sx.Su, sx.Sv), 5), ["h_mm"] = hh > 0 ? (JToken)Si.R(hh * sx.Sw, 5) : null,
                            ["insert"] = thin && plateAl, ["reason"] = thin ? (plateAl ? "S61: steel wall under " + Si.F(lim) + " mm between features - a steel insert" : "S61: thin steel wall - keep it replaceable (insert) or widen it") : "",
                        });
                        if (thin && !plateAl)
                            r.Checks.Add(Check.Status("INSERT-" + id, "machinability", "warn", "S61: steel wall under " + Si.F(lim) + " mm between features", "in a steel mold keep it replaceable (an insert) or widen it"));
                        continue;
                    }
                    if (kind == "pinch_off" || kind == "pry" || kind == "pry_abuse" || kind == "cleaning" || kind == "cut_off" || (shutOff && (dd <= 0 || hh <= 0)))
                    {
                        // S61 rules that need no dimensions: pinch-offs carry the closing load, pry and cleaning abuse wear
                        // the edge, cut-offs and shut-off faces on the parting line wear at tier T3 and above
                        var always = kind == "pinch_off" || kind.StartsWith("pry") || kind == "cleaning";
                        var need = always || tierT3;
                        var why = always ? (kind == "pinch_off" ? "pinch-off edge carries the closing load" : "takes pry or cleaning abuse")
                                         : tierT3 ? "parting-line " + (kind == "cut_off" ? "cut-off" : "shut-off") + " at tier " + tierNow + " (>= T3)" : "tier " + tierNow + " is below T3: machined in the plate";
                        pinsJson.Add(new JObject { ["id"] = id, ["kind"] = kind, ["insert"] = need && plateAl, ["reason"] = (need && plateAl ? "S61: " : plateAl ? "S61: no insert - " : "steel plate - ") + why });
                        if (shutOff) r.Notes.Add("A12: " + id + " - shut-off faces sit " + string.Join("-", cfg.Arr("pins.shutOffRecess_mm").Select(x => Si.F(x))) + " mm below the land, never proud");
                        continue;
                    }
                    if (dd <= 0 || hh <= 0)
                    {
                        Unevaluated(r, id, kind, "a core pin or shut-off pin needs d_mm and h_mm");
                        continue;
                    }
                    var mat = (plateAl && r.Insert != null) ? r.Insert : r.Plate;
                    var fa = Fatigue.Allowables(mat, d.Cycles, 2.5, cfg);
                    var pr = Pins.Check(id, dd * sx.Su, hh * sx.Sw, pLC1, mat, fa.Allow, r.Gap.DeltaWall, cfg, shutOff, (bool?)f["guided"] == true, pAbuse);
                    foreach (var c in pr.Checks) r.Checks.Add(ToJson(c, "size"));
                    pinAbuseCap = Math.Min(pinAbuseCap, pr.PAbuseCap);
                    pinsJson.Add(new JObject
                    {
                        ["id"] = id, ["kind"] = string.IsNullOrEmpty(kind) ? (shutOff ? "shut_off" : "core_pin") : kind,
                        ["d_mm"] = Si.R(dd * sx.Su, 5), ["h_mm"] = Si.R(hh * sx.Sw, 5), ["material"] = mat.Id,
                        ["insert"] = pr.InsertRequired || (plateAl && shutOff), ["reason"] = pr.Reason ?? (plateAl && shutOff ? "shut-off pin in aluminium" : ""),
                        ["sigma_MPa"] = Si.R(pr.LateralStress, 5), ["deflection_mm"] = Si.R(pr.LateralDeflection, 5),
                        ["sigmaAbuse_MPa"] = pAbuse > 0 ? (JToken)Si.R(pr.SigmaAbuse, 5) : null,
                        ["shutOffRecess_mm"] = shutOff ? new JArray(cfg.Arr("pins.shutOffRecess_mm")) : null,
                        ["axialForce_N"] = shutOff ? (JToken)Si.R(pr.AxialForce, 5) : null, ["euler_N"] = shutOff ? (JToken)Si.R(pr.EulerLoad, 5) : null,
                    });
                    if ((pr.InsertRequired || shutOff) && plateAl && r.Insert != null)
                    {
                        var di = Math.Ceiling(dd * sx.Su + 6);
                        var dT = d.TempC - 20;
                        var interf = Fits.InsertInterference(di, r.Plate.Cte_perK, r.Insert.Cte_perK, dT);
                        var hoop = Fits.InsertHoop(di, 3 * di, interf, r.Plate.E20, r.Plate.Nu, r.Insert.E20, r.Insert.Nu, r.Plate.Sy20, cfg);
                        r.Checks.Add(ToJson(new JCheck { Id = "FIT-HOOP-" + id, Type = "fit", Demand = hoop.Hoop, Capacity = hoop.HoopAllow, Unit = "MPa", Method = "S59: Lame hoop stress of the cold fit <= Sy,20/2" }, "size"));
                        fitsJson.Add(new JObject
                        {
                            ["id"] = "FIT-" + id, ["bodies"] = new JArray("INS-" + id, "BASE-1"), ["d_mm"] = di, ["coldInterference_mm"] = Math.Round(interf, 4),
                            ["pocketHot_mm"] = "0 to +0.01 clearance when hot", ["retention"] = "shoulder + screws; fit by heating the plate",
                            ["cornerRelief"] = "dog-bone or R >= 1.1 x cutter radius (B7); insert corners chamfered to match",
                        });
                    }
                }

            // temperature gate (A8): stress-aware for limited / beyond-limit temperatures, over every plate-material check
            TemperatureGate(r, cfg);

            // maximum gauge: press rating, bearing (S35), wall, pin and joint abuse (S24, S62, A3)
            var caps = new List<GaugeCap>();
            if (p.RatingGauge_MPa > 0) caps.Add(new GaugeCap(p.RatingGauge_MPa, d.RatedGauge_MPa > 0 ? "press rating" : "press rating (F_rated / A_ram)", null));
            if (!double.IsNaN(bear.GaugeMax_MPa) && !double.IsInfinity(bear.GaugeMax_MPa)) caps.Add(new GaugeCap(bear.GaugeMax_MPa, "bearing S35", null));
            foreach (var (pc, basis) in new[] { (wallAbuseCap, "wall abuse LC1x (S24)"), (pinAbuseCap, "pin/insert abuse LC1x (S62)"), (jointAbuseCap, "joint abuse LC1x (A3)") })
                if (pAbuse > 0 && !double.IsInfinity(pc) && !double.IsNaN(pc)) caps.Add(new GaugeCap(pc * p.AProj_mm2 / p.RamArea_mm2, basis, pc));
            // every LC1 capacity that follows the press force caps the gauge too, so the setup sheet's maximum never exceeds
            // what was checked: class B walls, pins, keys and joints (their cavity pressure is F/A_proj), and the supported
            // span in both classes (its load grows with F)
            caps.AddRange(StructuralCaps(r, cfg, pLC1, d.Class != ProcessClass.A_Pour ? jointLc1Cap : double.PositiveInfinity));
            ApplyGaugeCaps(r, caps);
            // abuse (LC1x) failures: the user's own setting is above what the part carries. Covered by the maximum gauge
            // (a warning with the number to set) when the recommended gauge is inside it; failing even that stays a fail
            var userGauge = p.GaugeNeeded_MPa;
            foreach (var c in r.Checks.Where(c => (string)c["loadCase"] == "LC1x" && (string)c["status"] == "fail").ToList())
            {
                if (r.GaugeMax_MPa != null && p.GaugeSet_MPa <= r.GaugeMax_MPa.Value * (1 + 1e-9))
                {
                    c["status"] = "warn";
                    c["note"] = userGauge > r.GaugeMax_MPa.Value * (1 + 1e-9)
                        ? "fails at your setting (" + Si.F(userGauge) + " MPa gauge): set the gauge to " + Si.F(p.GaugeSet_MPa) + " MPa, never above the maximum " + Si.F(r.GaugeMax_MPa.Value) + " MPa on the setup sheet"
                        : "covered by the maximum gauge " + Si.F(r.GaugeMax_MPa.Value) + " MPa on the setup sheet";
                }
            }

            // press block again with the final footprint and stack height (S8, S9): the subplate under a split base and the lid
            // plate of a split class B lid are part of the closed stack
            var stack = tBase + tLid;
            if (basePlan != null) stack += Si.Num(basePlan.SelectToken("subplate.t_mm")) ?? 22.225;
            if (lidPlan != null) stack += Si.Num(lidPlan.SelectToken("subplate.t_mm")) ?? 22.225;
            PressBlock(r, cfg, data, lay, stack);
            if (r.GaugeMax_MPa != null) { r.PressJson["gauge"]["max_MPa"] = Si.R(r.GaugeMax_MPa.Value, 5); r.PressJson["gauge"]["maxBasis"] = r.GaugeMaxBasis; }

            // partial cavity loading (critique E3)
            var partial = PartialLoading(r, cfg, data, lay);

            // hardware
            var tcDepth = Math.Min(data.ShopMm("thermocouple.depth", 50.8), lay.FootprintL / 2);
            var handlesAbove = data.ShopNum("handlesAbove.kg", 5);
            r.Hardware = new JObject
            {
                ["bolts"] = hardwareBolts,
                ["dowels"] = dowels,
                ["dowelScheme"] = new JObject { ["scheme"] = scheme.Scheme, ["mismatch_mm"] = Si.R(scheme.Mismatch_mm, 4), ["lidBaseDeltaT_K"] = scheme.LidBaseDeltaT, ["clearance_mm"] = scheme.Clearance_mm, ["note"] = scheme.Note, ["askShop"] = scheme.AskShop },
                ["fits"] = fitsJson,
                ["stops"] = stops,
                ["tcWells"] = new JArray(
                    new JObject { ["id"] = "TC-B1", ["body"] = "BASE-1", ["d_mm"] = Si.R(tc, 6), ["depth_mm"] = Si.R(tcDepth, 6), ["endFromCavity_mm"] = data.ShopMm("thermocouple.endFromCavity", 9) },
                    new JObject { ["id"] = "TC-L1", ["body"] = "LID-1", ["d_mm"] = Si.R(tc, 6), ["depth_mm"] = Si.R(tcDepth, 6), ["endFromCavity_mm"] = data.ShopMm("thermocouple.endFromCavity", 9) }),
                ["lift"] = new JArray(
                    LiftPoint(r, cfg, data, "BASE-1", massBase, massBase > (plateLimit ?? cfg.D("lifting.occasionalPlate_kg", 23)) ? "hoist rings (DIN 580) + pull handles" : massBase > handlesAbove ? "pull handles (slide-in, E4)" : "by hand"),
                    LiftPoint(r, cfg, data, "LID-1", massLid, massLid > lidLimit ? "handles; over the lid limit: lightening pockets (keep >= 80% platen contact), lift assist or hinge, two-person lift" : massLid > handlesAbove ? "handles" : "by hand",
                        new JObject { ["liftingIndex"] = Si.R(liftIndex, 3), ["rwl_kg"] = Si.R(lift.Rwl_kg, 4), ["basis"] = lift.Basis })),
                ["handlesRule"] = "outside the platen outline or wholly below the lid's platen face (D2, acceptance A16); on steel thread inserts",
                ["slideIn"] = d.SlideIn ? new JObject { ["locatingStop"] = true, ["basePullHandles"] = true, ["nothingProudBelowBase"] = true } : null,
                ["washers"] = "hardened (ASTM F436 / ISO 7089 300HV) on every aluminium clamped face; counterbore = washer OD + 0.5 mm",
            };

            r.Structural = new JObject
            {
                ["inputs"] = new JObject
                {
                    ["support"] = support.ToString(), ["deltaGap_mm"] = Si.R(r.Gap.DeltaGap, 4), ["deltaWall_mm"] = Si.R(r.Gap.DeltaWall, 4),
                    ["gapBasis"] = r.Gap.GapBasis, ["wallBasis"] = r.Gap.WallBasis, ["flowClass"] = flow,
                    ["deltaFlash_mm"] = r.Gap.DeltaFlash, ["landFlatness_mm"] = r.Gap.Flatness,
                    ["cycles"] = d.Cycles, ["tempC"] = d.TempC, ["tier"] = Tier(d.Cycles, cfg), ["rootFactor"] = rootText,
                },
                ["loadCases"] = new JObject
                {
                    ["LC1"] = new JObject { ["p_MPa"] = Si.R(pLC1, 5), ["pressureField"] = d.Json.SelectToken("structural.loadCases.LC1.pressureField")?.DeepClone() },
                    ["LC1x"] = d.Class == ProcessClass.A_Pour ? new JObject { ["p_MPa"] = Si.R(pAbuse, 5) } : null,
                    ["LC2"] = new JObject { ["F_kN"] = Si.R(force / 1000, 5) },
                    ["LC3"] = new JObject { ["deltaT_K"] = d.TempC - 20, ["lidBaseDeltaT_K"] = scheme.LidBaseDeltaT },
                },
                ["materials"] = new JObject { ["plates"] = AtTJson(g), ["inserts"] = r.Insert == null ? null : AtTJson(r.Insert) },
                ["results"] = new JObject
                {
                    ["walls"] = wallJson,
                    ["floors"] = new JArray(new JObject { ["body"] = "BASE-1", ["t_mm"] = Si.R(tBase - hBase, 6), ["required_mm"] = Si.R(tFloor, 5), ["governs"] = Governs(tFloor, al, hBase, tc, cfg) }),
                    ["plates"] = new JObject
                    {
                        ["base"] = new JObject { ["required_mm"] = Si.R(baseReq, 5), ["t_mm"] = Si.R(tBase, 6), ["stock_mm"] = Si.R(baseT.Stock, 6) },
                        ["lid"] = new JObject { ["required_mm"] = Si.R(lidReq, 5), ["t_mm"] = Si.R(tLid, 6), ["stock_mm"] = Si.R(lidT.Stock, 6), ["basis"] = lidRule },
                        ["span"] = spanNotes.HasValues ? spanNotes : null,
                    },
                    ["pieces"] = pieces,
                    ["bearing"] = new JObject { ["area_mm2"] = Si.R(aBear, 6), ["stress_MPa"] = Si.R(bear.Stress, 5), ["allow_MPa"] = Si.R(bear.Allow, 5), ["Db"] = bear.Db, ["gaugeMax_MPa"] = Si.R(bear.GaugeMax_MPa, 5), ["basis"] = bearBasis },
                    ["keys"] = keysJson,
                    ["pins"] = pinsJson,
                    ["layout"] = lay.ToJson(),
                    ["stackHeight_mm"] = Si.R(stack, 6),
                    ["lid"] = new JObject { ["mass_kg"] = Si.R(massLid, 5), ["limit_kg"] = Si.R(lidLimit, 4), ["limitBasis"] = lidBasis, ["liftingIndex"] = Si.R(liftIndex, 3) },
                    ["partialLoading"] = partial,
                },
                ["coefficients"] = new JObject { ["file"] = "coefficients.csv", ["version"] = Coefficients.Version, ["sha256"] = Coefficients.HeaderSha, ["provisional"] = Coefficients.Provisional },
                ["gauge"] = new JObject
                {
                    ["recommended_MPa"] = Si.R(p.GaugeSet_MPa, 5), ["max_MPa"] = r.GaugeMax_MPa == null ? null : (JToken)Si.R(r.GaugeMax_MPa.Value, 5), ["maxBasis"] = r.GaugeMaxBasis,
                    // the unit the user's gauge reads (the page's picked unit), for the setup sheet's gauge filter
                    ["unitShown"] = d.GaugeUnit,
                    // every cap with its cavity pressure, so a later `press` run (other press, other gauge) keeps them (H3 fix)
                    ["caps"] = GaugeCapsJson(r.GaugeCaps),
                },
            };
            // the support geometry (overrides or the record) stays with the record, so check and later runs size the same plate
            var sInputs = (JObject)r.Structural["inputs"];
            foreach (var key in SupportKeys)
            {
                var v = cfg.Param(key) ?? d.Json.SelectToken("structural.inputs." + key);
                if (v != null && v.Type != JTokenType.Null) sInputs[key] = v.DeepClone();
            }
            if (r.Advisory) r.Structural["advisory"] = (d.Class == ProcessClass.C_Positive ? "class C" : "class B deeper than wide (semi-positive)") + ": numbers for planning only - not built by this tool in v1";
            return Finish(r, cfg);
        }

        /// <summary>
        /// LC4 handling (design 3.2): handles, lift holes and their threads carry 2 x the piece weight. Handles: 2, each on
        /// 2 screws; hoist rings: 2, lifted vertically (the ring's rated WLL covers the sling angle). Each thread is sized
        /// with S54-S56 at its share of 2 W - hoist-ring threads are removed routinely and handle threads on the lid are
        /// loaded every cycle, so S55 puts both on steel thread inserts in aluminium.
        /// </summary>
        private static JObject LiftPoint(SizingResult r, Cfg cfg, MoldData data, string body, double mass, string kind, JObject extra = null)
        {
            var j = new JObject { ["body"] = body, ["kind"] = kind };
            if (extra != null) foreach (var p in extra.Properties()) j[p.Name] = p.Value;
            var d = r.Design;
            var g = r.Plate;
            var weight = mass * 9.80665;
            var factor = cfg.D("lifting.handlingLoadFactor", 2.0);
            var minD = cfg.D("bolts.minDiameter_mm", 6.0);
            var sizes = data.Fasteners(d.Imperial).Select(f => BoltSpec.From(f, cfg)).Where(b => b.D >= minD - 1e-9).ToList();
            var points = new JArray();
            foreach (var (what, n, removed) in new[] { ("handle screws", 4, false), ("hoist ring threads", 2, true) })
            {
                if (what.StartsWith("handle") ? !kind.Contains("handles") : !kind.Contains("hoist")) continue;
                var load = factor * weight / n;
                foreach (var b in sizes)
                {
                    var th = Threads.ForLoad(b, g, load, cfg, removed, true, d.Cycles);   // handles are used every cycle
                    if (th.StripStress > th.StripAllow * (1 + 1e-9) || load > 0.9 * b.Fp * Bolts.HotProofFactor(d.TempC, cfg)) continue;
                    points.Add(new JObject
                    {
                        ["what"] = what, ["count"] = n, ["size"] = b.Size, ["loadEach_N"] = Si.R(load, 5), ["engagement_mm"] = Si.R(th.Le, 5), ["tapDepth_mm"] = Si.R(th.TapDepth, 5),
                        ["insert"] = th.Insert ? th.InsertKind : null, ["insertReason"] = th.Reason,
                        ["rating"] = what.StartsWith("hoist") ? "swivel hoist ring (DIN 580 / ASME B30.26) with WLL >= " + Si.F(mass / n, 3) + " kg each, vertical lift" : "",
                    });
                    r.Checks.Add(ToJson(new JCheck
                    {
                        Id = "THREAD-LIFT-" + body + (what.StartsWith("hoist") ? "-HOIST" : "-HANDLE"), Type = "stripping", Demand = th.StripStress, Capacity = th.StripAllow, Unit = "MPa", LoadCase = "LC4",
                        Method = "LC4 + S54-S56: " + n + " x " + b.Size + " at 2 x the piece weight" + (th.Insert ? " (" + th.InsertKind + ")" : ""),
                    }, "size", body));
                    break;
                }
            }
            if (points.Count > 0) j["threads"] = points;
            return j;
        }

        /// <summary>A fragile feature the calculator could not judge: said so instead of skipped silently (S61).</summary>
        private static void Unevaluated(SizingResult r, string id, string kind, string why) =>
            r.Checks.Add(Check.Status("FRAGILE-" + id, "machinability", "warn", "S61: fragile feature not evaluated" + (string.IsNullOrEmpty(kind) ? "" : " (" + kind + ")"),
                why + " (kinds: core_pin {d_mm, h_mm, guided}, shut_off {d_mm, h_mm}, steel_wall {w_mm}, pinch_off, cut_off, pry)"));

        private static void PressBlockSafe(SizingResult r, Cfg cfg, MoldData data, LayoutResult lay, double? stack)
        {
            try { PressBlock(r, cfg, data, lay, stack); }
            catch (ArgumentException ex) { r.Notes.Add(ex.Message); }
        }

        private static string Governs(double tFloor, bool al, double h, double tc, Cfg cfg)
        {
            var minF = cfg.D(al ? "minFloor_mm.aluminium" : "minFloor_mm.steel", al ? 12 : 10);
            if (Math.Abs(tFloor - (tc + 2 * cfg.D("steelAroundTcWell_mm", 6))) < 1e-9) return "TC well";
            if (Math.Abs(tFloor - minF) < 1e-9) return "minimum floor";
            return "0.25 x cavity depth";
        }

        public static string Tier(double cycles, Cfg cfg)
        {
            if (cfg.T("cycleTiers") is JArray a)
                foreach (var t in a.OfType<JObject>())
                    if (cycles <= (double)t["upTo"]) return (string)t["tier"];
            return "T4";
        }

        private static JObject AtTJson(GradeAtT g) => new JObject
        {
            ["id"] = g.Id, ["tempC"] = g.TempC, ["E_GPa"] = Si.R(g.E / 1000, 4), ["Sy_MPa"] = Si.R(g.Sy, 4), ["Su_MPa"] = Si.R(g.Su, 4), ["service"] = g.Service, ["note"] = g.Note,
        };

        private static double? Num(JObject o, string path) => Si.Num(o.SelectToken(path));

        private static List<double> Cuts(JToken t) => (t as JArray)?.Select(x => Si.Num(x)).Where(x => x != null).Select(x => x.Value).ToList() ?? new List<double>();

        /// <summary>
        /// The chosen split of one plate (S74): structural.split.base / structural.split.lid ({cutsU_mm, cutsV_mm,
        /// subplate {t_mm, cutsU_mm, cutsV_mm}}, one option of mold_calc split). A plan written straight into
        /// structural.split (the first layout of the record) is the base's.
        /// </summary>
        public static JObject SplitPlan(JObject design, string plate)
        {
            var split = design.SelectToken("structural.split") as JObject;
            if (split == null) return null;
            if (split[plate] is JObject keyed) return keyed;
            return plate == "base" && (split["cutsU_mm"] != null || split["cutsV_mm"] != null) ? split : null;
        }

        /// <summary>
        /// The peak of the H3 pressure field (MPa): samples {p | p_MPa, ...} (design F3: {bodyId, point, normal, p}),
        /// [u, v, w, p] tuples, or {max_MPa} / {samples: [...]}. A field the calculator cannot read (a file path - the
        /// calculator reads no files - or another shape) is reported as a warning instead of being ignored silently.
        /// </summary>
        private static double? PressureFieldMax(SizingResult r)
        {
            var f = r.Design.Json.SelectToken("structural.loadCases.LC1.pressureField");
            if (f == null || f.Type == JTokenType.Null) return null;
            var ps = new List<double>();
            void Samples(JArray a)
            {
                foreach (var s in a)
                {
                    if (s is JObject o) { if (Si.Num(o["p"] ?? o["p_MPa"], "pressure") is double v) ps.Add(v); }
                    else if (s is JArray t && t.Count >= 4 && Si.Num(t[3]) is double v2) ps.Add(v2);
                }
            }
            if (f is JArray arr) Samples(arr);
            else if (f is JObject fo)
            {
                if (Si.Num(fo["max_MPa"]) is double m) ps.Add(m);
                if (fo["samples"] is JArray sa) Samples(sa);
            }
            if (ps.Count > 0) return ps.Max();
            r.Checks.Add(Check.Status("LOAD-PFIELD", "process", "warn", "H3: the simulator's pressure field could not be read",
                f.Type == JTokenType.String ? "it is a file path (" + (string)f + "); the calculator reads no files - pass the samples [{point, p}] or [u, v, w, p], or {max_MPa}" : "expected samples [{p}] or [u, v, w, p] tuples (MPa), or {max_MPa}; the uniform p_LC1 was used"));
            return null;
        }

        private static LayoutResult RecordLayout(SizingResult r, MoldData data)
        {
            var d = r.Design;
            var fp = Si.Lengths(d.Json.SelectToken("layout.footprint_mm"));
            if (fp == null || fp.Length < 2 || !(fp[0] > 0) || !(fp[1] > 0))
                throw new ArgumentException("layout.footprint_mm in the record is not a valid [L, W]: run mold_calc layout or size again.");
            var lr = new LayoutResult { FootprintL = fp[0], FootprintW = fp[1], Moats = (string)d.Json.SelectToken("layout.moats") ?? "separate" };
            foreach (var c in LayoutFromRecord(d)) lr.Cavities.Add(c);
            if (lr.Cavities.Count == 0)
                throw new ArgumentException("The record has a footprint but no layout.cavities: run mold_calc layout or size again.");
            if (lr.Cavities.Count != d.Cavities)
                throw new ArgumentException("The record's layout has " + lr.Cavities.Count + " cavities but inputs.cavities is " + d.Cavities + ": run mold_calc layout or size again (with record).");
            var us = lr.Cavities.Select(c => c.Item2).Distinct().OrderBy(x => x).ToList();
            var vs = lr.Cavities.Select(c => c.Item3).Distinct().OrderBy(x => x).ToList();
            var pu = d.PartU * r.Shrink.Su;
            var pv = d.PartV * r.Shrink.Sv;
            lr.Rows = Math.Max(1, vs.Count);
            lr.Cols = Math.Max(1, lr.Cavities.GroupBy(c => Math.Round(c.Item3, 6)).Max(g => g.Count()));
            // the narrowest steel between neighbours that face each other, and from each side to the plate edge (a
            // layout need not be centred; a short last row sits between the columns of the row next to it)
            var gu = new List<double>();
            var gv = new List<double>();
            for (var i = 0; i < lr.Cavities.Count; i++)
                for (var k = i + 1; k < lr.Cavities.Count; k++)
                {
                    var du = Math.Abs(lr.Cavities[i].Item2 - lr.Cavities[k].Item2);
                    var dv = Math.Abs(lr.Cavities[i].Item3 - lr.Cavities[k].Item3);
                    if (dv < pv - 1e-6) gu.Add(du - pu);
                    if (du < pu - 1e-6) gv.Add(dv - pv);
                }
            lr.GapU = gu.Count > 0 ? gu.Min() : 0;
            lr.GapV = gv.Count > 0 ? gv.Min() : 0;
            lr.Cols = gu.Count > 0 ? lr.Cols : 1;
            lr.EdgeU = Math.Min(lr.FootprintL / 2 - (us.Last() + pu / 2), (us.First() - pu / 2) + lr.FootprintL / 2);
            lr.EdgeV = Math.Min(lr.FootprintW / 2 - (vs.Last() + pv / 2), (vs.First() - pv / 2) + lr.FootprintW / 2);
            if (lr.EdgeU <= 0 || lr.EdgeV <= 0 || (gu.Count > 0 && lr.GapU <= 0) || (gv.Count > 0 && lr.GapV <= 0))
                throw new ArgumentException($"The record's layout does not fit the scaled cavities (edge {Si.F(lr.EdgeU)} / {Si.F(lr.EdgeV)} mm, gap {Si.F(lr.GapU)} / {Si.F(lr.GapV)} mm): the layout is stale - run mold_calc layout or size again.");
            return lr;
        }

        /// <summary>Runner area for the class A bearing estimate: n = max(1, round(side/pitch)) runners per moat side, each W wide,
        /// from the moat to the plate edge (outer sides) or across the gap to the neighbour (inner sides).</summary>
        private static double RunnerArea(SizingResult r, MoldData data, LayoutResult lay, double pu, double pv)
        {
            var lip = data.ShopMm("flash.lip", 3.175);
            var W = data.ShopMm("flash.grooveWidth", 12.7);
            var R = data.ShopMm("flash.cornerRadius", 6.35);
            var pitch = data.ShopMm("flash.runnerPitch", 50.8);
            var band = lip + W;
            var total = 0.0;
            var us = lay.Cavities.Select(c => c.U).Distinct().OrderBy(x => x).ToList();
            var vs = lay.Cavities.Select(c => c.V).Distinct().OrderBy(x => x).ToList();
            foreach (var c in lay.Cavities)
            {
                foreach (var (dirU, dirV) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                {
                    var sideLen = (dirU != 0 ? pv : pu) + 2 * band - 2 * R;
                    var n = Math.Max(1, (int)Math.Round(sideLen / pitch, MidpointRounding.AwayFromZero));
                    double len;
                    if (dirU != 0)
                    {
                        var outer = dirU < 0 ? c.U == us.First() : c.U == us.Last();
                        len = outer ? lay.EdgeU - band : lay.GapU - 2 * band;
                    }
                    else
                    {
                        var outer = dirV < 0 ? c.V == vs.First() : c.V == vs.Last();
                        len = outer ? lay.EdgeV - band : lay.GapV - 2 * band;
                    }
                    total += n * W * Math.Max(0, len);
                }
            }
            return total;
        }

        /// <summary>
        /// Support cases B-E: the base floor spans b under the cavity pressure (S29-S32); returns the floor thickness the span needs.
        /// Cases B (rails), C (an opening) and E (pillars): the coefficient table gives the first guess (Rails, SS4 / C4, and the
        /// periodic Pillar rows with alpha x the 3D pillar-head factor), then the layout plate FE of the planned layout finishes it
        /// (design 2.3.2, Track A2: cavities plus land loaded, supports where they are, two meshes extrapolated) - the table alone
        /// is about 17% thick on rails and over openings, and unsafe next to the plate edges on pillars. The press force beyond
        /// what the cavities carry is spread over the footprint. Case D is the overhang strip; case F needs a separate FE.
        /// Loads: the design force (the user's setting or the recommended one, whichever is higher). With tBuilt (the
        /// as-built check, A12) nothing is sized: the floor is checked at that thickness and 0 is returned.
        /// </summary>
        private static double SpanBlock(SizingResult r, Cfg cfg, MoldData data, Support s, LayoutResult lay, double hBase, JObject notes, double? tBuilt = null)
        {
            var d = r.Design;
            var g = r.Plate;
            var p = Math.Max(r.Press.PDesign, r.Press.ForceDesign_N / (lay.FootprintL * lay.FootprintW));
            if (tBuilt != null && !(tBuilt > 0))
                throw new ArgumentException("The as-built base leaves no floor under the cavities (" + Si.F(tBuilt.Value) + " mm): re-measure structural.asBuilt.baseThickness_mm.");
            var delta = r.Gap.DeltaGap;
            var tauMax = cfg.D("tauMax", 0.5);
            if (s == Support.D)
            {
                var c = cfg.ParamNum("overhang_mm") ?? Si.Num(d.Json.SelectToken("structural.inputs.overhang_mm")) ?? 0;
                if (c <= 0) return 0;
                var t = tBuilt ?? Plates.OverhangThickness(p, c, g.E, g.Nu, delta);
                var ov = Plates.Overhang(p, c, t, g.E, g.Nu);
                notes["case"] = "D"; notes["overhang_mm"] = c; notes["t_mm"] = Si.R(t, 5); notes["sigma_MPa"] = Si.R(ov.Sigma, 5);
                if (tBuilt != null)
                    r.Checks.Add(ToJson(new JCheck { Id = "SPAN-DEFL", Type = "deflection", Demand = ov.W, Capacity = delta, Unit = "mm", LoadCase = "LC1", Method = "S32 (as built): overhang strip deflection at the as-built floor <= delta_gap" }, "size", "BASE-1"));
                r.Checks.Add(ToJson(new JCheck { Id = "SPAN-STRESS", Type = "stress", Demand = ov.Sigma, Capacity = g.Sy / cfg.D("sfStatic", 2), Unit = "MPa", LoadCase = "LC1", Method = "S32: overhang strip 3 p c^2/t^2 <= Sy/2" }, "size"));
                return tBuilt != null ? 0 : t;
            }
            if (s == Support.F)
            {
                r.Checks.Add(Check.Status("SPAN-CASE-F", "deflection", "warn", "Case F (ram or platen smaller than the lid): FE only", "model the ram contact and run the plate FE before building"));
                return 0;
            }
            var integral = (cfg.ParamStr("frame") ?? (string)d.Json.SelectToken("structural.inputs.frame")) == "integral";
            var kf = cfg.ParamNum("kf");
            var fa = kf != null ? Fatigue.AllowablesKf(g, d.Cycles, kf.Value, cfg) : Fatigue.Allowables(g, d.Cycles, cfg.D("fatigue.ktHoleInBentPlate", 2.5), cfg);
            var plan = PlatePlan(r, cfg, data, s, lay, delta);
            double L = (double)plan["plate"]["length_mm"], W = (double)plan["plate"]["width_mm"];
            string bc;
            double b, len, alphaFactor = 1;
            switch (s)
            {
                case Support.B:
                    bc = "Rails";
                    b = (double)plan["rails"]["y2_mm"] - (double)plan["rails"]["y1_mm"];
                    len = L;
                    break;
                case Support.C:
                    bc = integral ? "C4" : "SS4";
                    var o = plan["opening"];
                    double ol = (double)o["x2_mm"] - (double)o["x1_mm"], ow = (double)o["y2_mm"] - (double)o["y1_mm"];
                    b = Math.Min(ol, ow); len = Math.Max(ol, ow);
                    break;
                default:
                    if (plan["pillars"] != null)
                    {
                        bc = "Pillar";
                        b = LayoutPlate.PillarSpan(plan);
                        len = LayoutPlate.PillarSpanLong(plan);
                        alphaFactor = cfg.D("pillarHeadFactor", 1.2);
                    }
                    else
                    {
                        // no pillar positions (pillars_mm or span_mm): the plate on its edges alone
                        bc = integral ? "C4" : "SS4";
                        b = Math.Min(L, W); len = Math.Max(L, W);
                        r.Notes.Add("Case E without pillar positions: the plate is sized on its edges alone - pass overrides {\"pillars_mm\": {\"u\": [...], \"v\": [...]}} (from the plate corner) or {\"span_mm\": pitch}");
                    }
                    break;
            }
            if (tBuilt != null)
            {
                // the as-built check: the floor as it is, against delta_gap and the allowable (layout FE, else the table)
                var tb = tBuilt.Value;
                notes["case"] = s.ToString(); notes["bc"] = bc; notes["span_mm"] = Si.R(b, 6); notes["p_MPa"] = Si.R(p, 5); notes["t_mm"] = Si.R(tb, 5);
                notes["tOverB"] = Si.R(tb / b, 4); notes["asBuilt"] = true; notes["geometry"] = plan;
                if ((bool?)cfg.Param("plate_fe") != false)
                {
                    try
                    {
                        var (rw, rs) = LayoutPlate.Extrapolated(plan, tb, g.E, g.Nu, fa.Allow, alphaFactor);
                        notes["fe"] = new JObject { ["method"] = "layout plate FE at the as-built floor, two meshes extrapolated", ["t_mm"] = Si.R(tb, 5), ["r_w"] = Si.R(rw, 4), ["r_s"] = Si.R(rs, 4) };
                        r.Checks.Add(ToJson(new JCheck { Id = "SPAN-DEFL", Type = "deflection", Demand = rw * delta, Capacity = delta, Unit = "mm", LoadCase = "LC1", Method = "S29 + 2.3.2 (as built): land deflection by the layout plate FE at the as-built floor <= delta_gap" }, "size", "BASE-1"));
                        r.Checks.Add(ToJson(new JCheck { Id = "SPAN-STRESS", Type = fa.AllowFatigue < fa.AllowStatic ? "fatigue" : "stress", Demand = rs * fa.Allow, Capacity = fa.Allow, Unit = "MPa", LoadCase = "LC1", Method = "S30 + 2.3.2 (as built): plate stress by the layout plate FE at the as-built floor <= min(Sy/2, fatigue)" }, "size", "BASE-1"));
                        return 0;
                    }
                    catch (InvalidOperationException ex)
                    {
                        r.Checks.Add(Check.Status("SPAN-FE", "deflection", "warn", "2.3.2: the layout plate FE could not run", ex.Message + " - the coefficient table is used; check the supports"));
                    }
                }
                var cob = Coefficients.Get(bc, len / b, tb / b, g.Nu, 1.0);
                r.Checks.Add(ToJson(new JCheck { Id = "SPAN-DEFL", Type = "deflection", Demand = alphaFactor * cob.Alpha * p * Math.Pow(b, 4) / (g.E * Math.Pow(tb, 3)), Capacity = delta, Unit = "mm", LoadCase = "LC1", Method = "S29 (as built): alpha p b^4/(E t^3) at the as-built floor <= delta_gap (" + bc + " rows)" }, "size", "BASE-1"));
                r.Checks.Add(ToJson(new JCheck { Id = "SPAN-STRESS", Type = "stress", Demand = cob.Beta * p * b * b / (tb * tb), Capacity = fa.Allow, Unit = "MPa", LoadCase = "LC1", Method = "S30 (as built): beta p b^2/t^2 at the as-built floor <= min(Sy/2, fatigue)" }, "size", "BASE-1"));
                return 0;
            }
            var sp = Plates.Span(bc, len / b, b, p, g.E, g.Nu, delta, tauMax, alphaFactor: alphaFactor, allow: fa.Allow);
            notes["case"] = s.ToString(); notes["bc"] = bc; notes["span_mm"] = Si.R(b, 6); notes["p_MPa"] = Si.R(p, 5);
            notes["tTable_mm"] = Si.R(sp.T, 5); notes["bMax_mm"] = Si.R(sp.BMax, 5); notes["tSteel_mm"] = Si.R(sp.TSteelEquivalent, 5);
            if (alphaFactor != 1) notes["pillarHeadFactor"] = alphaFactor;
            notes["geometry"] = plan;
            var t0 = sp.T;
            var tFinal = t0;
            var governs = sp.Sigma > fa.Allow * (1 - 1e-6) ? "stress" : "deflection";
            var feDone = false;
            if (sp.TooThick)
            {
                r.Checks.Add(Check.Status("SPAN-TOO-THICK", "deflection", "warn", "S31: t/b > 0.5 - add support instead", "largest span for this plate " + Si.F(sp.BMax) + " mm; or steel at " + Si.F(sp.TSteelEquivalent) + " mm"));
                governs = "add support";
            }
            else if ((bool?)cfg.Param("plate_fe") != false)
            {
                try
                {
                    var fe = LayoutPlate.Size(plan, g.E, g.Nu, fa.Allow, t0, alphaFactor);
                    tFinal = fe.T;
                    governs = fe.Governs;
                    feDone = true;
                    notes["fe"] = new JObject
                    {
                        ["method"] = "layout plate FE: MITC4, " + LayoutPlate.Coarse.Elements + " and " + LayoutPlate.Fine.Elements + " elements on the short side, extrapolated; sized to " + Si.F(LayoutPlate.Target * 100) + "% of the limits",
                        ["t_mm"] = Si.R(fe.T, 5), ["r_w"] = Si.R(fe.Rw, 4), ["r_s"] = Si.R(fe.Rs, 4), ["governs"] = fe.Governs, ["solves"] = fe.Solves,
                    };
                    var how = "layout plate FE (from the " + bc + " rows' " + Si.F(t0) + " mm)";
                    r.Checks.Add(ToJson(new JCheck { Id = "SPAN-DEFL", Type = "deflection", Demand = fe.Rw * delta, Capacity = delta, Unit = "mm", LoadCase = "LC1", Method = "S29 + 2.3.2: land deflection by the " + how + " <= delta_gap" }, "size", "BASE-1"));
                    r.Checks.Add(ToJson(new JCheck { Id = "SPAN-STRESS", Type = fa.AllowFatigue < fa.AllowStatic ? "fatigue" : "stress", Demand = fe.Rs * fa.Allow, Capacity = fa.Allow, Unit = "MPa", LoadCase = "LC1", Method = "S30 + 2.3.2: plate stress by the " + how + " <= min(Sy/2, fatigue" + (kf != null ? ", K_f " + Si.F(kf.Value) : " with K_t 2.5 at holes") + ")" }, "size", "BASE-1"));
                }
                catch (InvalidOperationException ex)
                {
                    // a mechanism (supports that do not hold the plate) or a model over the memory cap: say so, keep the table
                    r.Checks.Add(Check.Status("SPAN-FE", "deflection", "warn", "2.3.2: the layout plate FE could not run", ex.Message + " - the coefficient table's thickness is used; check the supports"));
                }
            }
            if (!feDone)
            {
                var defl = sp.Alpha * p * Math.Pow(b, 4) / (g.E * Math.Pow(t0, 3));
                r.Checks.Add(ToJson(new JCheck { Id = "SPAN-DEFL", Type = "deflection", Demand = defl, Capacity = delta, Unit = "mm", LoadCase = "LC1", Method = "S29: " + (alphaFactor != 1 ? Si.F(alphaFactor) + " x " : "") + "alpha p b^4/(E t^3) <= delta_gap (" + bc + " rows)" }, "size", "BASE-1"));
                r.Checks.Add(ToJson(new JCheck { Id = "SPAN-STRESS", Type = "stress", Demand = sp.Sigma, Capacity = fa.Allow, Unit = "MPa", LoadCase = "LC1", Method = "S30: beta p b^2/t^2 <= min(Sy/2, fatigue with K_t 2.5 at holes)" }, "size", "BASE-1"));
            }
            notes["t_mm"] = Si.R(tFinal, 5); notes["tOverB"] = Si.R(tFinal / b, 4); notes["governs"] = governs;
            return tFinal;
        }

        /// <summary>
        /// The base plate as the layout FE sees it (LayoutPlate's JSON, from the plate corner): the planned footprint and cavities
        /// (or the `plate_layout` override {footprint_mm, cavities_mm [[u0, v0, u1, v1], ...]} for a what-if or the Monte Carlo),
        /// the land, and the supports from the overrides or the record's structural.inputs: rails `rails_v_mm` [v1, v2] (along U)
        /// or `rails_u_mm` [u1, u2] (along V; the plan is then transposed so the rails run along x), else `rail_span_mm` centred
        /// under the long side (default: the plate's edges); an opening `opening_mm` [u1, v1, u2, v2], else `span_mm` x
        /// `span_length_mm` centred (default: the footprint); pillars `pillars_mm` {u: [...], v: [...]}, else an even grid at
        /// most `span_mm` apart.
        /// </summary>
        public static JObject PlatePlan(SizingResult r, Cfg cfg, MoldData data, Support s, LayoutResult lay, double delta)
        {
            var d = r.Design;
            JToken G(string key) => cfg.Param(key) ?? d.Json.SelectToken("structural.inputs." + key);
            double? GN(string key) => Si.Num(G(key));
            double[] GA(string key) => (G(key) as JArray)?.Select(x => Si.Num(x) ?? double.NaN).ToArray();
            double L, W;
            var cav = new List<double[]>();   // x0, y0, x1, y1
            if (cfg.Param("plate_layout") is JObject over)
            {
                var fp = Si.Lengths(over["footprint_mm"]);
                if (fp == null || fp.Length < 2 || !(fp[0] > 0) || !(fp[1] > 0)) throw new ArgumentException("overrides.plate_layout.footprint_mm must be [L, W] in mm.");
                L = fp[0]; W = fp[1];
                foreach (var c in (over["cavities_mm"] as JArray ?? new JArray()).OfType<JArray>())
                {
                    var v = c.Select(x => Si.Num(x) ?? double.NaN).ToArray();
                    if (v.Length != 4 || v.Any(double.IsNaN) || v[2] <= v[0] || v[3] <= v[1]) throw new ArgumentException("overrides.plate_layout.cavities_mm items are [u0, v0, u1, v1] from the plate corner (mm).");
                    cav.Add(v);
                }
            }
            else
            {
                L = lay.FootprintL; W = lay.FootprintW;
                double pu = d.PartU * r.Shrink.Su, pv = d.PartV * r.Shrink.Sv;
                foreach (var c in lay.Cavities) cav.Add(new[] { c.U + L / 2 - pu / 2, c.V + W / 2 - pv / 2, c.U + L / 2 + pu / 2, c.V + W / 2 + pv / 2 });
            }
            var land = cfg.ParamNum("land_mm") ?? LandWidth(d, data);
            var pCav = r.Press.PDesign;
            var loaded = cav.Sum(c => (c[2] - c[0] + 2 * land) * (c[3] - c[1] + 2 * land));
            var extra = Math.Max(0, r.Press.ForceDesign_N - pCav * loaded) / (L * W);
            // rails along V: transpose so the rails run along x like the Rails rows and the FE model expect
            var railsU = s == Support.B ? GA("rails_u_mm") : null;
            var railsV = s == Support.B ? GA("rails_v_mm") : null;
            var transpose = s == Support.B && (railsU == null && railsV == null ? W > L + 1e-9 : railsU != null && railsV == null);
            if (transpose)
            {
                var tmp = L; L = W; W = tmp;
                cav = cav.Select(c => new[] { c[1], c[0], c[3], c[2] }).ToList();
            }
            var j = new JObject
            {
                ["schema"] = "unify3d.mold-mc/1", ["support"] = s.ToString(),
                ["plate"] = new JObject { ["length_mm"] = L, ["width_mm"] = W },
                ["cavities"] = new JArray(cav.Select(c => new JObject { ["x_mm"] = c[0], ["y_mm"] = c[1], ["length_mm"] = c[2] - c[0], ["width_mm"] = c[3] - c[1] })),
                ["land_mm"] = land, ["pressure_MPa"] = pCav, ["kf"] = 1.0, ["delta_allow_mm"] = delta,
                ["transposed"] = transpose,
            };
            if (extra > 0) j["uniform_MPa"] = extra;
            switch (s)
            {
                case Support.B:
                {
                    var rl = transpose ? railsU : railsV;
                    double y1, y2;
                    if (rl != null && rl.Length == 2 && !rl.Any(double.IsNaN)) { y1 = Math.Min(rl[0], rl[1]); y2 = Math.Max(rl[0], rl[1]); }
                    else { var span = Math.Min(GN("rail_span_mm") ?? W, W); y1 = (W - span) / 2; y2 = (W + span) / 2; }
                    if (!(y2 > y1) || y1 < -1e-9 || y2 > W + 1e-9) throw new ArgumentException("The rails must lie on the plate: rails_v_mm / rails_u_mm are positions from the plate corner (0 to the plate width).");
                    j["rails"] = new JObject { ["y1_mm"] = y1, ["y2_mm"] = y2 };
                    break;
                }
                case Support.C:
                {
                    var op = GA("opening_mm");
                    double x1, y1, x2, y2;
                    if (op != null && op.Length == 4 && !op.Any(double.IsNaN)) { x1 = op[0]; y1 = op[1]; x2 = op[2]; y2 = op[3]; }
                    else
                    {
                        var sShort = GN("span_mm") ?? Math.Min(L, W);
                        var sLong = GN("span_length_mm") ?? Math.Max(L, W);
                        double ou = L >= W ? sLong : sShort, ov = L >= W ? sShort : sLong;
                        ou = Math.Min(ou, L); ov = Math.Min(ov, W);
                        x1 = (L - ou) / 2; x2 = (L + ou) / 2; y1 = (W - ov) / 2; y2 = (W + ov) / 2;
                    }
                    if (!(x2 > x1) || !(y2 > y1)) throw new ArgumentException("opening_mm is [u1, v1, u2, v2] from the plate corner with u2 > u1 and v2 > v1.");
                    j["opening"] = new JObject { ["x1_mm"] = x1, ["y1_mm"] = y1, ["x2_mm"] = x2, ["y2_mm"] = y2 };
                    break;
                }
                case Support.E:
                {
                    var pil = G("pillars_mm") as JObject;
                    double[] xs = (pil?["u"] as JArray)?.Select(x => Si.Num(x) ?? double.NaN).Where(x => x > 0 && x < L).ToArray();
                    double[] ys = (pil?["v"] as JArray)?.Select(x => Si.Num(x) ?? double.NaN).Where(x => x > 0 && x < W).ToArray();
                    if ((xs == null || ys == null) && GN("span_mm") is double pitch && pitch > 0)
                    {
                        double[] Grid(double size) { var n = Math.Max(0, (int)Math.Ceiling(size / pitch - 1e-9) - 1); return Enumerable.Range(1, n).Select(i => size * i / (n + 1)).ToArray(); }
                        xs = xs ?? Grid(L);
                        ys = ys ?? Grid(W);
                    }
                    if (xs != null && ys != null && xs.Length > 0 && ys.Length > 0)
                        j["pillars"] = new JObject { ["xs_mm"] = new JArray(xs), ["ys_mm"] = new JArray(ys) };
                    break;
                }
            }
            return j;
        }

        /// <summary>
        /// Keys and bolts of a split plate (S37-S41 keys first, S42-S56 bolts, A3 abuse) from its split plan {cutsU_mm,
        /// cutsV_mm, subplate {t_mm}} - the base (pieces BASE-n keyed into the subplate SUB-n) or a class B lid (LID-n into
        /// the lid plate LSUB-n). Each piece side on a cut line is a joint: the cavity pressure on the piece's wall toward
        /// it is the separating force F_sep = p L_c h (L_c = cavity extent x cavities lined up along the joint). The key (a
        /// step of k >= 9.5 mm into the backing plate) carries it; the screws from the backing plate into the piece only
        /// take the tipping moment F_sep h/2 over the piece width (S40). Screw heads sit in counterbores in the backing
        /// plate so nothing stands proud of the base's underside (E4). Screws along a joint keep S53's 1.5 d edge distance
        /// and 4-6 d spacing; a second row is used when one row cannot hold the count.
        /// </summary>
        private static void SplitJoints(SizingResult r, Cfg cfg, MoldData data, JObject split, string plateId, string subId, double p, double pAbuse, double h, double tPlate, JArray bolts, JArray keys, ref double abuseCap, ref double lc1Cap)
        {
            var d = r.Design;
            var g = r.Plate;
            var lay = r.Layout;
            var tSub = Si.Num(split.SelectToken("subplate.t_mm")) ?? 22.225;
            var pu = d.PartU * r.Shrink.Su;
            var pv = d.PartV * r.Shrink.Sv;
            var keyStep = cfg.D("keys.minStep_mm", 9.5);
            var minD = cfg.D("bolts.minDiameter_mm", 6.0);
            var edgeD = cfg.D("bolts.edgeDistanceD", 1.5);
            var sizes = data.Fasteners(d.Imperial).Select(f => BoltSpec.From(f, cfg)).Where(b => b.D >= minD - 1e-9).ToList();
            var cutsU = Cuts(split["cutsU_mm"]);
            var cutsV = Cuts(split["cutsV_mm"]);
            var si = new SplitInput { L = lay.FootprintL, W = lay.FootprintW, T = tPlate, Density_kg_m3 = g.Density_kg_m3 };
            foreach (var c in lay.Cavities) si.Pockets.Add(new Pocket { Id = c.Id, U = c.U, V = c.V, SizeU = pu, SizeV = pv, Depth = h });
            foreach (var pc in PlateSplit.Pieces(si, cutsU, cutsV, tPlate, plateId, true))
            {
                var inside = lay.Cavities.Where(c => c.U > pc.U0 && c.U < pc.U1 && c.V > pc.V0 && c.V < pc.V1).ToList();
                var onU = cutsU.Any(x => Math.Abs(x - pc.U0) < 1e-6 || Math.Abs(x - pc.U1) < 1e-6);   // a side on a U cut: joint along V
                var onV = cutsV.Any(x => Math.Abs(x - pc.V0) < 1e-6 || Math.Abs(x - pc.V1) < 1e-6);
                if (!onU && !onV) continue;
                // the worse joint direction of the piece (a piece without a cavity still gets its screws: F_sep = 0)
                KeyResult kr = null;
                double Lkey = 0;
                foreach (var alongV in new[] { true, false })
                {
                    if (alongV ? !onU : !onV) continue;
                    var Lc = alongV ? pv * inside.Select(c => c.V).Distinct().Count() : pu * inside.Select(c => c.U).Distinct().Count();
                    var len = alongV ? pc.V1 - pc.V0 : pc.U1 - pc.U0;
                    var bar = alongV ? pc.U1 - pc.U0 : pc.V1 - pc.V0;
                    var k = Keys.Check(pc.Id, p, pAbuse, Lc, h, len, keyStep, keyStep, bar, g, cfg);
                    if (kr == null || k.PTotTipping > kr.PTotTipping) { kr = k; Lkey = len; }
                }
                foreach (var c in kr.Checks) r.Checks.Add(ToJson(c, "size", pc.Id));
                abuseCap = Math.Min(abuseCap, kr.PAbuseCap);
                keys.Add(new JObject { ["piece"] = pc.Id, ["Fsep_kN"] = Si.R(kr.FSep / 1000, 5), ["keyLength_mm"] = Si.R(Lkey, 6), ["step_mm"] = keyStep, ["bearing_MPa"] = Si.R(kr.KeyBearing, 5), ["shear_MPa"] = Si.R(kr.KeyShear, 5), ["tipping_kN"] = Si.R(kr.PTotTipping / 1000, 5) });

                // S53: sizes ascending, counts upward; the first that passes S47-S51, S54 (engagement fits the piece) and S56
                // (stripping, unless an insert) wins. Class A (LC1x) must pass the abuse case too; otherwise the first joint
                // that passes LC1 on its own preload window (without the abuse case) is kept and the maximum gauge is capped
                // at what that joint carries at that preload (A3).
                (JointResult J, BoltSpec B, int N, ThreadResult T, double Clamp, int Rows)? best = null, fallback = null;
                var spacingNote = "";
                foreach (var b in sizes)
                {
                    var th = Threads.Engagement(b, g, cfg, false, true, d.Cycles);
                    if (th.TapDepth > tPlate - 6 + 1e-9) continue;               // keep 6 mm of steel under the parting face
                    var usable = Lkey - 2 * edgeD * b.D;                          // S53: 1.5 d from each end of the joint
                    if (usable < 0) { spacingNote = "the joint is too short for " + b.Size + " at 1.5 d edge distance"; continue; }
                    var clamp = Clamp(tSub, b);
                    var perRowMin = (int)Math.Ceiling(usable / (cfg.D("bolts.maxSpacingD", 6) * b.D) - 1e-9) + 1;
                    var perRowMax = (int)Math.Floor(usable / (cfg.D("bolts.minSpacingD", 4) * b.D) + 1e-9) + 1;
                    var minN = Math.Max((int)cfg.D("bolts.minPerPlate", 4), perRowMin);
                    var maxN = perRowMax * Math.Max(1, (int)cfg.D("bolts.maxRows", 2));
                    if (minN > maxN) { spacingNote = "no room for " + minN + " x " + b.Size + " at 4 d spacing along " + Si.F(Lkey) + " mm"; continue; }
                    for (var count = minN; count <= maxN; count++)
                    {
                        var rows = (int)Math.Ceiling(count / (double)perRowMax);
                        var j = new JointInput
                        {
                            Bolt = b, Tapped = true, TopThickness = clamp + b.WasherT, TappedThickness = th.Le,
                            Layers = new List<Layer> { new Layer { T = clamp, E = g.E, Aluminium = g.IsAluminium } }, AlphaMember = g.Cte_perK,
                            TempC = d.TempC, P = kr.PTotTipping / count, PAbuse = pAbuse > 0 && p > 0 ? kr.PTotTipping / count * pAbuse / p : 0, MemberSuT = g.Su,
                        };
                        var res = Bolts.Design(j, cfg, j.PAbuse > 0);
                        if (res.Passes && (j.PAbuse <= 0 || res.AbusePasses))
                        {
                            // S54-S56 at this joint's maximum bolt load: direct tapped if it fits, else a steel insert
                            var thr = Threads.ForLoad(b, g, res.HotMax, cfg, false, true, d.Cycles);
                            if (thr.TapDepth > tPlate - 6 + 1e-9) continue;
                            best = (res, b, count, thr, clamp, rows);
                            break;
                        }
                        if (j.PAbuse > 0 && fallback == null)
                        {
                            // the LC1 window on its own: the abuse case then caps the gauge instead
                            var lc1 = Bolts.Design(j, cfg, false);
                            if (!lc1.Passes) continue;
                            var thr = Threads.ForLoad(b, g, lc1.HotMax, cfg, false, true, d.Cycles);
                            if (thr.TapDepth > tPlate - 6 + 1e-9) continue;
                            fallback = (lc1, b, count, thr, clamp, rows);
                        }
                    }
                    if (best != null) break;
                }
                if (best == null && fallback != null)
                {
                    best = fallback;
                    var f = fallback.Value;
                    var perBoltP = kr.PTotTipping / f.N;
                    var t = new JointInput
                    {
                        Bolt = f.B, Tapped = true, TopThickness = f.Clamp + f.B.WasherT, TappedThickness = f.T.Le,
                        Layers = new List<Layer> { new Layer { T = f.Clamp, E = g.E, Aluminium = g.IsAluminium } }, AlphaMember = g.Cte_perK,
                        TempC = d.TempC, P = perBoltP, PAbuse = perBoltP * pAbuse / p, MemberSuT = g.Su, PreloadFraction = f.J.Fraction,
                    };
                    // the abuse load per bolt this joint carries at its LC1 preload, as a cavity pressure (F/A_proj)
                    var capP = perBoltP > 0 ? Bolts.AbuseCapacity(t, cfg) / (perBoltP / p) : double.PositiveInfinity;
                    abuseCap = Math.Min(abuseCap, capP);
                    r.Notes.Add($"A3: the {f.N} x {f.B.Size} joint of {pc.Id} passes LC1 but not the abuse case; the maximum gauge is capped at what it carries ({Si.F(capP)} MPa in the cavity)");
                }
                if (best == null)
                {
                    r.Checks.Add(Check.Status("BOLTS-" + pc.Id, "boltLoad", "fail", "S53: no listed size and count passes S47-S51 and S54-S56", "use a keyed frame (3.10) or a larger piece" + (spacingNote.Length > 0 ? "; " + spacingNote : ""), pc.Id));
                    continue;
                }
                var bb = best.Value;
                // the LC1 cavity pressure this joint carries at its chosen preload (bolt checks and thread stripping): with a
                // pressure that follows the press (class B) it caps the maximum gauge, so the setup sheet's maximum stays checked
                if (p > 0 && kr.PTotTipping > 0)
                {
                    var perBolt = kr.PTotTipping / bb.N;
                    var ji = new JointInput
                    {
                        Bolt = bb.B, Tapped = true, TopThickness = bb.Clamp + bb.B.WasherT, TappedThickness = bb.T.Le,
                        Layers = new List<Layer> { new Layer { T = bb.Clamp, E = g.E, Aluminium = g.IsAluminium } }, AlphaMember = g.Cte_perK,
                        TempC = d.TempC, P = perBolt, MemberSuT = g.Su,
                    };
                    var hotStrip = bb.T.StripStress > 0 ? bb.J.HotMax * bb.T.StripAllow / bb.T.StripStress : double.PositiveInfinity;
                    bool Ok(double load)
                    {
                        ji.P = load;
                        var ev = Bolts.Evaluate(ji, cfg, bb.J.Fraction);
                        return ev.Checks.Where(c => c.LoadCase != "LC1x").All(Bolts.Passes) && ev.HotMax <= hotStrip * (1 + 1e-12);
                    }
                    double lo = 0, hi = perBolt;
                    if (Ok(perBolt)) { lo = perBolt; hi = 2 * perBolt; for (var k = 0; k < 60 && Ok(hi); k++) { lo = hi; hi *= 2; } }
                    for (var k = 0; k < 60; k++) { var m = 0.5 * (lo + hi); if (Ok(m)) lo = m; else hi = m; }
                    lc1Cap = Math.Min(lc1Cap, p * lo / perBolt);
                }
                foreach (var c in bb.J.Checks) { var cj = ToJson(c, "size", pc.Id); cj["id"] = c.Id + "-" + pc.Id; r.Checks.Add(cj); }
                r.Checks.Add(ToJson(new JCheck
                {
                    Id = "THREAD-STRIP-" + pc.Id, Type = "stripping", Demand = bb.T.StripStress, Capacity = bb.T.StripAllow, Unit = "MPa",
                    Method = bb.T.Insert ? "S55/S56: parent shear at the insert's outer diameter <= 0.58 Sy,T/2 (" + bb.T.InsertKind + ")" : "S56 (conservative, H4): F/(pi d_minor L_e 0.75) <= 0.58 Sy,T/2",
                }, "size", pc.Id));
                if (bb.Clamp < 0.5 * tSub + 1e-9)
                    r.Notes.Add($"{pc.Id}: the backing plate leaves only {Si.F(bb.Clamp)} mm under the {bb.B.Size} counterbores - a thicker plate stiffens the joint");
                if (bb.Rows > 1) r.Notes.Add($"{pc.Id}: {bb.N} x {bb.B.Size} in {bb.Rows} rows along the joint (one row cannot hold them at 4 d spacing)");
                var bj = BoltJson("B-" + pc.Id, subId + "/" + pc.Id, bb.B, bb.N, bb.J, bb.T, bb.Clamp, data, d.Imperial);
                bj["rows"] = bb.Rows;
                bj["edgeDistance_mm"] = Si.R(edgeD * bb.B.D, 4);
                bolts.Add(bj);
            }
        }

        /// <summary>Clamped length under a counterbored head: the subplate minus the counterbore (head + washer + 0.5 mm), at least half the plate.</summary>
        private static double Clamp(double plate, BoltSpec b) => Math.Max(plate - (b.HeadHeight + b.WasherT + 0.5), 0.5 * plate);

        /// <summary>
        /// A bolt group for the record and the hardware list (critique A1.6, E2): preload with its scatter band, torques,
        /// engagement, the stock screw length (shortest list length that reaches the engagement) and the tapped depth that
        /// screw needs plus a 0.5 d relief, so it never bottoms out; counterbores sized for the hardened washer.
        /// </summary>
        public static JObject BoltJson(string id, string joint, BoltSpec b, int count, JointResult j, ThreadResult thr, double clampT, MoldData data, bool imperial)
        {
            var lengths = imperial ? (data.Raw("mold-materials.json")?.SelectToken("fasteners_common.stock_lengths_in") as JArray)?.Select(x => (double)x * Si.InToMm).ToList()
                                   : (data.Raw("mold-materials.json")?.SelectToken("fasteners_common.stock_lengths_mm") as JArray)?.Select(x => (double)x).ToList();
            var minL = clampT + b.WasherT + thr.Le;
            var stock = lengths?.Where(x => x >= minL - 1e-9).DefaultIfEmpty(double.NaN).Min() ?? double.NaN;
            var tap = double.IsNaN(stock) ? thr.TapDepth : Math.Max(thr.TapDepth, stock - clampT - b.WasherT + 0.5 * b.D);
            return new JObject
            {
                ["id"] = id, ["joint"] = joint, ["size"] = b.Size, ["spec"] = b.Spec, ["count"] = count,
                ["grip_mm"] = Si.R(j.Grip, 5), ["clamp_mm"] = Si.R(clampT, 5), ["C"] = Si.R(j.C, 4), ["preloadFraction"] = Si.R(j.Fraction, 4),
                ["preload_kN"] = Si.R(j.Fi / 1000, 5), ["preloadBand_kN"] = new JArray(Si.R(j.FiMin / 1000, 5), Si.R(j.FiMax / 1000, 5)),
                ["preloadWindow"] = new JArray(Si.R(j.FractionLo, 4), Si.R(j.FractionHi, 4)),
                ["thermal_kN"] = Si.R(j.DFth / 1000, 5), ["hot_kN"] = Si.R(j.HotMax / 1000, 5), ["hotProofFactor"] = Si.R(j.KT, 4),
                ["n0"] = Si.R(j.N0, 4), ["nL"] = Si.R(j.NL, 4),
                ["torqueDry_Nm"] = Si.R(j.TorqueDry_Nm, 4), ["torqueLubed_Nm"] = Si.R(j.TorqueLubed_Nm, 4),
                ["engagement_mm"] = Si.R(thr.Le, 5), ["tapDepth_mm"] = Si.R(tap, 5),
                ["insert"] = thr.Insert ? thr.InsertKind : null, ["insertReason"] = thr.Reason,
                ["washer"] = b.Imperial ? "ASTM F436" : "ISO 7089 300HV", ["cboreForWasher_mm"] = Si.R(b.WasherOd + 0.5, 5),
                ["cboreDepth_mm"] = Si.R(b.HeadHeight + b.WasherT + 0.5, 5),
                ["stockLength_mm"] = double.IsNaN(stock) ? null : (JToken)Si.R(stock, 6),
                ["retorque"] = "re-torque hot after the first 2-3 heat cycles",
            };
        }

        // ------------------------------------------------------------------ temperature gate (A8)

        private static void TemperatureGate(SizingResult r, Cfg cfg)
        {
            var g = r.Plate;
            var d = r.Design;
            if (g.Service == "ok") return;
            if (g.Service == "ok-derated") { r.Checks.Add(Check.Status("TEMP-" + g.Id, "temperature", "pass", "S12: ok-derated", g.Note)); return; }
            if (g.Service == "blocked") { r.Checks.Add(Check.Status("TEMP-" + g.Id, "temperature", "fail", "S12 + A8: never_above is a hard block for a grade that ages and changes size", g.Note)); return; }
            // governing stress ratio over the derated allowables of everything made of the plate grade: walls, spans,
            // bearing, keys, thread stripping, bolt head bearing and pins machined in the plate (not steel inserts)
            var pinsInInsert = g.IsAluminium && r.Insert != null;
            var ratios = new List<(double Ratio, string Id)>();
            foreach (var c in r.Checks)
            {
                var type = (string)c["type"];
                var id = (string)c["id"] ?? "";
                if ((string)c["loadCase"] == "LC1x" || (string)c["unit"] != "MPa") continue;
                if (type != "stress" && type != "fatigue" && type != "bearing" && type != "stripping") continue;
                if (pinsInInsert && id.StartsWith("PIN-", StringComparison.Ordinal)) continue;
                var dem = Si.Num(c["demand"]);
                var cap = Si.Num(c["capacity"]);
                if (dem == null || cap == null || cap <= 0) continue;
                ratios.Add((dem.Value / cap.Value, id));
            }
            var worst = ratios.OrderByDescending(x => x.Ratio).ThenBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
            var ratio = ratios.Count == 0 ? 0 : worst.Ratio;
            var lim = cfg.D("limitedTemperature.stressRatioMax", 0.5);
            var note = "governing stress ratio " + Si.F(ratio) + " of the derated allowable" + (ratios.Count == 0 ? "" : " (" + worst.Id + ")");
            if (ratio <= lim)
                r.Checks.Add(Check.Status("TEMP-" + g.Id, "temperature", "pass", "A8: " + g.Service + " temperature allowed - every governing stress <= " + Si.F(lim) + " x the derated allowable", note + ". " + g.Note));
            else if (g.Service == "limited" && d.Cycles <= cfg.D("limitedTemperature.maxCycles", 10000))
                r.Checks.Add(Check.Status("TEMP-" + g.Id, "temperature", "warn", "S12: limited - needs N <= 10,000 and the user's confirmation", note + ". " + g.Note + ". Otherwise switch to steel (P20)."));
            else
                r.Checks.Add(Check.Status("TEMP-" + g.Id, "temperature", "fail", "S12 + A8: " + g.Service + " at this stress", note + ". Switch to steel (P20) or lower the stresses."));
        }

        // ------------------------------------------------------------------ maximum gauge (S35, S24, S62, A3)

        /// <summary>One upper limit on the gauge: the gauge value and, for an abuse cap, the cavity pressure behind it.</summary>
        public sealed class GaugeCap
        {
            public double Gauge;
            public string Basis;
            public double? PCavity;
            public GaugeCap(double gauge, string basis, double? pCavity) { Gauge = gauge; Basis = basis; PCavity = pCavity; }
        }

        private static JArray GaugeCapsJson(List<GaugeCap> caps) => new JArray((caps ?? new List<GaugeCap>()).Select(c => new JObject
        {
            ["basis"] = c.Basis, ["gauge_MPa"] = Si.R(c.Gauge, 5), ["pCavity_MPa"] = c.PCavity == null ? null : (JToken)Si.R(c.PCavity.Value, 5),
        }));

        /// <summary>The lowest cap is the maximum gauge on the setup sheet; checks PRESS-GAUGE-MAX and PRESS-SETTING.</summary>
        private static void ApplyGaugeCaps(SizingResult r, List<GaugeCap> caps)
        {
            var d = r.Design;
            var p = r.Press;
            r.GaugeCaps = caps.Where(c => c.Gauge > 0 && !double.IsNaN(c.Gauge) && !double.IsInfinity(c.Gauge)).ToList();
            r.GaugeMax_MPa = null;
            r.GaugeMaxBasis = null;
            if (r.GaugeCaps.Count == 0) return;
            var min = r.GaugeCaps.OrderBy(c => c.Gauge).ThenBy(c => c.Basis, StringComparer.Ordinal).First();
            // the printed maximum is rounded down, so a gauge set to it never exceeds what was checked
            r.GaugeMax_MPa = Si.RDown(min.Gauge, 5);
            r.GaugeMaxBasis = min.Basis;
            r.Checks.Add(ToJson(new JCheck { Id = "PRESS-GAUGE-MAX", Type = "press", Demand = p.GaugeSet_MPa, Capacity = min.Gauge, Unit = "MPa", Method = "S35/S24/S62/A3: recommended gauge <= the lowest maximum (" + min.Basis + ")" }, "press"));
            // the user's own setting (as the gauge it needs, F/A_ram, whatever its meaning) above the cap: a warning with the
            // number to set (the cap is on the setup sheet)
            var userGauge = p.GaugeNeeded_MPa;
            if (userGauge > min.Gauge * (1 + 1e-9))
                r.Checks.Add(Check.Status("PRESS-SETTING", "press", "warn", "S35/A3: your " + (d.Meaning == PressMeaning.Gauge ? "gauge setting" : d.Meaning == PressMeaning.OnPart ? "pressure on the part" : "force reading") + " is above the maximum (it needs a gauge of " + Si.F(userGauge) + " MPa)", "set the gauge to " + Si.F(p.GaugeSet_MPa) + " MPa (max " + Si.F(min.Gauge) + " MPa, " + min.Basis + ")"));
            // a gauge setting the operator cannot read: below 5% of the gauge's full scale (the gauge at the rating)
            var full = p.RatingGauge_MPa;
            if (full > 0)
            {
                var low = new List<string>();
                if (p.GaugeSet_MPa < 0.05 * full) low.Add("recommended " + Si.F(p.GaugeSet_MPa) + " MPa");
                if (min.Gauge < 0.05 * full) low.Add("maximum " + Si.F(min.Gauge) + " MPa");
                if (low.Count > 0)
                    r.Checks.Add(Check.Status("PRESS-GAUGE-READ", "press", "warn", "S7: gauge setting below 5% of the gauge's full scale (" + Si.F(full) + " MPa) cannot be read reliably", string.Join(", ", low) + ": use a low-range gauge or a smaller press, or set by force on a load cell"));
            }
        }

        /// <summary>
        /// The gauge caps of the LC1 checks whose load follows the press force (the setup sheet's maximum must have been
        /// checked): the demand is linear (walls, pins, keys) or affine with a non-negative intercept (a span under the
        /// cavities plus the rest of the force) in F, so gauge_design x capacity/demand is the gauge at which the check is
        /// exactly met, or a conservative bound below it. Class A walls, pins and keys carry the hydrostatic load and do
        /// not follow the press. A beyond-limit temperature (A8) holds plate stresses to its stress ratio.
        /// </summary>
        private static List<GaugeCap> StructuralCaps(SizingResult r, Cfg cfg, double pDesign, double jointLc1Cap)
        {
            var d = r.Design;
            var p = r.Press;
            var caps = new List<GaugeCap>();
            if (!(p.ForceDesign_N > 0) || !(p.RamArea_mm2 > 0)) return caps;
            var gaugeDesign = p.ForceDesign_N / p.RamArea_mm2;
            var classB = d.Class != ProcessClass.A_Pour;
            var g = r.Plate;
            var strict = g.Service == "beyond-limit" || (g.Service == "limited" && d.Cycles > cfg.D("limitedTemperature.maxCycles", 10000));
            var lim = strict ? cfg.D("limitedTemperature.stressRatioMax", 0.5) : 1.0;
            var pinsInInsert = g.IsAluminium && r.Insert != null;
            var groups = new[]
            {
                (Prefixes: new[] { "WALL-DEFL-", "WALL-STRESS-" }, Basis: "walls LC1 (S21-S23)", B: true),
                (Prefixes: new[] { "PIN-STRESS-", "PIN-DEFL-" }, Basis: "pins LC1 (S62)", B: true),
                (Prefixes: new[] { "KEY-BEAR-", "KEY-SHEAR-" }, Basis: "keys LC1 (S38-S39)", B: true),
                (Prefixes: new[] { "SPAN-DEFL", "SPAN-STRESS" }, Basis: "plate span LC1 (S29-S30)", B: false),
            };
            var ratios = new List<(double, string)>();
            foreach (var grp in groups)
            {
                if (grp.B && !classB) continue;
                var ratio = double.PositiveInfinity;
                foreach (var c in r.Checks)
                {
                    var id = (string)c["id"] ?? "";
                    if ((string)c["loadCase"] != "LC1" || !grp.Prefixes.Any(x => id.StartsWith(x, StringComparison.Ordinal)) || id.Contains("-X-")) continue;
                    var dem = Si.Num(c["demand"]);
                    var cap = Si.Num(c["capacity"]);
                    if (dem == null || cap == null || !(dem > 0) || !(cap > 0)) continue;
                    var type = (string)c["type"];
                    var plateStress = type != "deflection" && !(pinsInInsert && id.StartsWith("PIN-", StringComparison.Ordinal));
                    ratio = Math.Min(ratio, cap.Value * (plateStress ? lim : 1.0) / dem.Value);
                }
                // 0.1% below the exact ratio (the record's demands and capacities are rounded to 4 digits), but never below
                // the design force when the check passes there (a wall sized exactly for it)
                if (!double.IsInfinity(ratio)) ratios.Add((ratio >= 1 ? Math.Max(1.0, ratio * 0.999) : ratio, grp.Basis));
            }
            if (classB && !double.IsInfinity(jointLc1Cap) && !double.IsNaN(jointLc1Cap) && pDesign > 0)
                ratios.Add((jointLc1Cap / pDesign >= 1 ? Math.Max(1.0, jointLc1Cap / pDesign * 0.999) : jointLc1Cap / pDesign, "joint bolts LC1 (S47-S56)"));
            // each cap is a press force; its cavity pressure F/A_proj is stored so a later press run converts it to its own ram
            foreach (var (ratio, basis) in ratios)
                caps.Add(new GaugeCap(gaugeDesign * ratio, basis, p.ForceDesign_N * ratio / p.AProj_mm2));
            return caps;
        }

        // ------------------------------------------------------------------ partial cavity loading (critique E3)

        private static JObject PartialLoading(SizingResult r, Cfg cfg, MoldData data, LayoutResult lay)
        {
            var d = r.Design;
            var p = r.Press;
            var n = lay.Cavities.Count;
            var rows = new JArray();
            var off = cfg.D("offCentre", 0.05);
            var refL = d.PlatenL ?? lay.FootprintL;
            var refW = d.PlatenW ?? lay.FootprintW;
            var kMin = n;
            var minReadable = 0.05 * d.RatedGauge_MPa;
            const double maxEnumerated = 20000;
            bool Centred(IEnumerable<int> set)
            {
                var l = set.ToList();
                return Math.Abs(l.Average(i => lay.Cavities[i].U)) <= off * refL + 1e-9 && Math.Abs(l.Average(i => lay.Cavities[i].V)) <= off * refW + 1e-9;
            }
            for (var k = n; k >= 1; k--)
            {
                var gauge = p.GaugeSet_MPa * k / n;
                int allowed = 0, total = 0;
                string example = null;
                var exhaustive = Choose(n, k) <= maxEnumerated;
                if (exhaustive)
                {
                    foreach (var subset in Subsets(n, k))
                    {
                        total++;
                        if (Centred(subset))
                        {
                            allowed++;
                            if (example == null) example = string.Join(" ", subset.Select(i => lay.Cavities[i].Id));
                        }
                    }
                }
                else
                {
                    // too many patterns to list: build one by leaving out cavities in pairs mirrored through the centre
                    // (and the centre cavity when the count needs it), which keeps the load centre where it is
                    var set = SymmetricPattern(lay, k);
                    if (set != null && Centred(set)) { allowed = 1; example = string.Join(" ", set.Select(i => lay.Cavities[i].Id)); }
                    total = -1;
                }
                var readable = d.RatedGauge_MPa <= 0 || gauge >= minReadable;
                if (allowed > 0 && readable) kMin = k;
                rows.Add(new JObject
                {
                    ["active"] = k, ["gauge_MPa"] = Si.R(gauge, 5), ["force_kN"] = Si.R(gauge * p.RamArea_mm2 / 1000, 5),
                    ["patterns"] = allowed, ["of"] = exhaustive ? (JToken)total : "many (one symmetric pattern shown)", ["example"] = example,
                    ["note"] = allowed == 0 ? (exhaustive ? "no centred pattern (S8)" : "no symmetric pattern found (S8)") : readable ? "" : "gauge below 5% of full scale",
                });
            }
            return new JObject
            {
                ["rows"] = rows, ["neverFewerThan"] = kMin,
                ["rule"] = "same pressure per cavity: gauge x active/total; patterns keep the load centre within " + Si.F(off * 100) + "% (S8); same-gauge with fewer cavities raises the cavity pressure",
            };
        }

        private static double Choose(int n, int k)
        {
            double c = 1;
            for (var i = 1; i <= k; i++) c = c * (n - k + i) / i;
            return c;
        }

        /// <summary>k active cavities chosen by dropping mirror pairs (through the plate centre) and the centre cavity.</summary>
        private static List<int> SymmetricPattern(LayoutResult lay, int k)
        {
            var n = lay.Cavities.Count;
            var drop = n - k;
            var active = Enumerable.Range(0, n).ToList();
            int First(Func<int, bool> ok) { for (var j = 0; j < n; j++) if (ok(j)) return j; return -1; }
            int Mirror(int i) => First(j => j != i && Math.Abs(lay.Cavities[j].U + lay.Cavities[i].U) < 1e-6 && Math.Abs(lay.Cavities[j].V + lay.Cavities[i].V) < 1e-6 && active.Contains(j));
            var centre = First(i => Math.Abs(lay.Cavities[i].U) < 1e-6 && Math.Abs(lay.Cavities[i].V) < 1e-6);
            if (drop % 2 == 1)
            {
                if (centre < 0) return null;
                active.Remove(centre);
                drop--;
            }
            // drop pairs from the outside in (reading order from the last cavity), so the active ones stay compact
            for (var i = n - 1; i >= 0 && drop > 0; i--)
            {
                if (!active.Contains(i) || i == centre) continue;
                var m = Mirror(i);
                if (m < 0) continue;
                active.Remove(i);
                active.Remove(m);
                drop -= 2;
            }
            return drop == 0 ? active : null;
        }

        private static IEnumerable<int[]> Subsets(int n, int k)
        {
            var idx = Enumerable.Range(0, k).ToArray();
            while (true)
            {
                yield return (int[])idx.Clone();
                var i = k - 1;
                while (i >= 0 && idx[i] == n - k + i) i--;
                if (i < 0) yield break;
                idx[i]++;
                for (var j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The support geometry parameters (overrides) that are kept in structural.inputs.</summary>
        public static readonly string[] SupportKeys = { "frame", "rail_span_mm", "rails_v_mm", "rails_u_mm", "opening_mm", "span_mm", "span_length_mm", "pillars_mm", "overhang_mm" };

        /// <summary>The S21 wall-root factor from sizing-defaults: {base, c} (base + c w/h) or a plain number (constant).</summary>
        public static (double Base, double Slope) RootFactor(Cfg cfg)
        {
            var t = cfg.T("wallRootFactor");
            if (t is JObject) return (cfg.D("wallRootFactor.base", 1.0), cfg.D("wallRootFactor.c", Walls.DefaultRootSlope));
            var n = Si.Num(t);
            return n != null ? (n.Value, 0.0) : (1.0, Walls.DefaultRootSlope);
        }

        /// <summary>A computed check as a record checks[] entry; an explicit status (warn) replaces the margin's.</summary>
        public static JObject ToJson(JCheck c, string stage, string body = null)
        {
            var j = c.Lower ? Check.Lower(c.Id, c.Type, c.Demand, c.Capacity, c.Unit, c.Method, body, c.LoadCase, c.Note)
                            : Check.Upper(c.Id, c.Type, c.Demand, c.Capacity, c.Unit, c.Method, body, c.LoadCase, c.Note);
            // an advisory check (Status = warn) only warns when its numbers fail; it never turns a pass into a warning
            if (c.Status != null && (string)j["status"] == "fail") j["status"] = c.Status;
            j["stage"] = "3";
            return j;
        }

        private static SizingResult Finish(SizingResult r, Cfg cfg)
        {
            // one entry per id (the last computed wins), lowest margins first
            var byId = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var c in r.Checks) byId[(string)c["id"]] = c;
            r.Checks = Check.Ordered(byId.Values);
            return r;
        }

        /// <summary>The process action alone (S64-S70) for MoldCalc.</summary>
        public static SizingResult ProcessOnly(JObject design, MoldData data, Cfg cfg)
        {
            var r = Prelude(design, data, cfg);
            Process(r, cfg, data);
            return Finish(r, cfg);
        }

        /// <summary>
        /// The press action (S1-S9) on the record's layout. Its maximum gauge is the lowest of the press rating, the
        /// bearing cap (S35, from the record's bearing area) and every abuse cap the last size/check stored as a cavity
        /// pressure (structural.gauge.caps), converted with this press's ram area - so a later press run never lifts the
        /// maximum above what the walls, pins or joints carry.
        /// </summary>
        public static SizingResult PressOnly(JObject design, MoldData data, Cfg cfg)
        {
            var r = Prelude(design, data, cfg);
            Process(r, cfg, data);
            var d = r.Design;
            LayoutResult lay = null;
            if (d.HasGeometry && d.Json.SelectToken("layout.footprint_mm") is JArray) lay = RecordLayout(r, data);
            PressBlock(r, cfg, data, lay, Si.Num(d.Json.SelectToken("structural.results.stackHeight_mm")));
            var p = r.Press;
            var caps = new List<GaugeCap>();
            if (d.RatedGauge_MPa > 0) caps.Add(new GaugeCap(d.RatedGauge_MPa, d.Json.SelectToken("structural.results.bearing.area_mm2") == null ? "press rating (bearing not sized yet)" : "press rating", null));
            var aBear = Si.Num(d.Json.SelectToken("structural.results.bearing.area_mm2"));
            if (r.Plate.Service == "no-data")
                r.Notes.Add("A8: no strength data at " + Si.F(d.TempC) + " C - no bearing cap; size refuses this temperature");
            else if (aBear != null && aBear > 0)
            {
                var b = Bearing.Compute(p.Force_N, aBear.Value, p.RamArea_mm2, r.Plate, d.Cycles, cfg);
                if (!double.IsNaN(b.GaugeMax_MPa) && !double.IsInfinity(b.GaugeMax_MPa)) caps.Add(new GaugeCap(b.GaugeMax_MPa, "bearing S35", null));
            }
            var stored = d.Json.SelectToken("structural.gauge.caps") as JArray;
            if (stored != null)
            {
                foreach (var c in stored.OfType<JObject>())
                {
                    var pc = Si.Num(c["pCavity_MPa"]);
                    if (pc != null && pc > 0) caps.Add(new GaugeCap(pc.Value * p.AProj_mm2 / p.RamArea_mm2, (string)c["basis"] + " (from the last size/check)", pc));
                }
            }
            else if (Si.Num(d.Json.SelectToken("structural.gauge.max_MPa")) is double oldMax && oldMax > 0)
            {
                // a record from before the caps were stored: keep its maximum as it stands
                caps.Add(new GaugeCap(oldMax, ((string)d.Json.SelectToken("structural.gauge.maxBasis") ?? "last size/check") + " (from the last size/check)", null));
            }
            ApplyGaugeCaps(r, caps);
            if (r.GaugeMax_MPa != null) { r.PressJson["gauge"]["max_MPa"] = Si.R(r.GaugeMax_MPa.Value, 5); r.PressJson["gauge"]["maxBasis"] = r.GaugeMaxBasis; }
            return Finish(r, cfg);
        }

        public static SizingResult LayoutOnly(JObject design, MoldData data, Cfg cfg)
        {
            var r = Prelude(design, data, cfg);
            Process(r, cfg, data);
            LayoutBlock(r, cfg, data);
            return Finish(r, cfg);
        }
    }
}

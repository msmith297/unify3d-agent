using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>
    /// The Monte Carlo's sizing delegate: the calculator itself. Each random mold (MonteCarloHarness, schema
    /// "unify3d.mold-mc/1") is turned into mold_calc designs and sized by MoldCalc.Run("size"), exactly as the agent runs it:
    ///   - one design per cavity (its box l x w x depth, all of it in the base), whose structural.results.walls give that
    ///     cavity's two walls (W-V-B: the walls along the cavity's length; W-U-B: along its width);
    ///   - the first cavity's design also carries the plate: the support and its geometry (rails_v_mm, opening_mm, pillars_mm)
    ///     and the drawn layout (plate_layout: footprint and every cavity outline) as overrides, so `size` runs its span check
    ///     (table first guess, layout plate FE finish) on the Monte Carlo's plate.
    /// The cavity pressure is set exactly: a class A material (room-temperature cast PU, the smallest table molding pressure)
    /// whose design pressure is 1.5 x molding_pressure, with molding_pressure = p / 1.5 and a negligible press force. No shrink
    /// (measured basis, 0%), so the cavities are the drawn sizes. K_f and delta come in as overrides (kf, delta_allow_mm,
    /// delta_wall_mm), the land as land_mm. The reported allowable is the calculator's notched allowable times K_f, because the
    /// harness divides by K_f again (r_s = sigma K_f / sigma_allow); E and nu are the grade's at temperature.
    /// Plate: support A reports the calculator's floor (case A plates do not bend); B, C and E the span thickness (never below
    /// the floor). Any exception propagates: the harness counts it as a failure.
    /// </summary>
    public static class MoldCalcSizing
    {
        /// <summary>Room-temperature cast PU elastomer: class A, table molding pressure at most 60 psi (0.41 MPa).</summary>
        public const string PartGrade = "pu-rt-elastomer-60-90a";

        public static string Name => "MoldCalc.Run(\"size\") " + MoldCalc.Version + " through MoldCalcSizing (walls: coefficient table + fixed point, root factor 1 + c w/h, +1% interpolation allowance; plates B, C, E: table first guess - Pillar rows x the pillar-head factor for E - finished by the layout plate FE at "
                                     + LayoutPlate.Coarse.Elements + " and " + LayoutPlate.Fine.Elements + " elements, extrapolated; coefficients " + Coefficients.Version + ")";

        public static Func<JObject, JObject> Create(MoldData data) => d => Size(data, d);

        public static JObject Size(MoldData data, JObject mc)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            string support = (string)mc["support"] ?? "A";
            double p = (double)mc["pressure_MPa"], delta = (double)mc["delta_allow_mm"], kf = (double)mc["kf"], land = (double?)mc["land_mm"] ?? 3.175;
            string grade = (string)mc["material"]["id"];
            double tempC = (double)mc["material"]["tempC"], cycles = (double)mc["cycles"];
            var cavs = mc["cavities"].Cast<JObject>().ToList();
            if (cavs.Count == 0) throw new ArgumentException("A Monte Carlo design without cavities.");

            // class A design pressure = factor x the molding pressure (above the table maximum, critique C3)
            var factor = new Cfg(data.Defaults).D("classA.pressureFactorOnTableMax", 1.5);
            var walls = new JArray();
            JObject plate = null;
            for (int i = 0; i < cavs.Count; i++)
            {
                var c = cavs[i];
                double l = (double)c["length_mm"], w = (double)c["width_mm"], h = (double)c["depth_mm"];
                var overrides = new JObject
                {
                    ["kf"] = kf, ["delta_allow_mm"] = delta, ["delta_wall_mm"] = delta, ["land_mm"] = land,
                    ["support"] = i == 0 ? support : "A",
                };
                if (i == 0 && support != "A")
                {
                    double pl = (double)mc["plate"]["length_mm"], pw = (double)mc["plate"]["width_mm"];
                    overrides["plate_layout"] = new JObject
                    {
                        ["footprint_mm"] = new JArray(pl, pw),
                        ["cavities_mm"] = new JArray(cavs.Select(k => new JArray((double)k["x_mm"], (double)k["y_mm"], (double)k["x_mm"] + (double)k["length_mm"], (double)k["y_mm"] + (double)k["width_mm"]))),
                    };
                    if (support == "B") overrides["rails_v_mm"] = new JArray((double)mc["rails"]["y1_mm"], (double)mc["rails"]["y2_mm"]);
                    if (support == "C") overrides["opening_mm"] = new JArray((double)mc["opening"]["x1_mm"], (double)mc["opening"]["y1_mm"], (double)mc["opening"]["x2_mm"], (double)mc["opening"]["y2_mm"]);
                    if (support == "E") overrides["pillars_mm"] = new JObject { ["u"] = mc["pillars"]["xs_mm"].DeepClone(), ["v"] = mc["pillars"]["ys_mm"].DeepClone() };
                }
                var res = MoldCalc.Run("size", Design(grade, tempC, cycles, p / factor, l, w, h), null, overrides, data);
                var st = (JObject)res.RecordPatch["structural"];
                // the calculator must have sized for exactly the drawn pressure (else this mapping is wrong, not the sizing)
                var pUsed = (double)st.SelectToken("loadCases.LC1.p_MPa");
                if (Math.Abs(pUsed / p - 1) > 1e-4) throw new InvalidOperationException($"MoldCalcSizing: the calculator sized for {pUsed} MPa instead of {p} MPa");
                foreach (var (id, side) in new[] { ("W-V-B", "length"), ("W-U-B", "width") })
                {
                    var wj = st.SelectToken("results.walls").OfType<JObject>().Single(x => (string)x["id"] == id);
                    double wd = (double)wj["w_defl_mm"], ws = (double)wj["w_stress_mm"], wst = (double)wj["w_struct_mm"];
                    var gov = (bool?)wj["noSolution"] == true ? "no solution" : wst / h > 0.5 ? "out of range" : ws > wd ? "stress" : "deflection";
                    walls.Add(new JObject { ["cavity"] = i, ["side"] = side, ["w_mm"] = wst, ["governs"] = gov, ["floor_mm"] = st.SelectToken("results.floors[0].required_mm")?.DeepClone() });
                }
                if (i == 0) plate = PlateOf(st, support);
            }

            var g = AtTemperature.Get(data.MoldGrade(grade) ?? throw new ArgumentException("Unknown grade " + grade), tempC);
            var fa = Fatigue.AllowablesKf(g, cycles, kf, new Cfg(data.Defaults));
            return new JObject
            {
                ["E_MPa"] = g.E, ["nu"] = g.Nu, ["sigma_allow_MPa"] = fa.Allow * kf, ["kf"] = kf,
                ["plate"] = plate, ["walls"] = walls, ["method"] = Name,
            };
        }

        /// <summary>The plate the harness checks: the span thickness (B, C, E) or the floor (A), never below the floor rule.</summary>
        static JObject PlateOf(JObject st, string support)
        {
            var floor = (double)st.SelectToken("results.floors[0].required_mm");
            var span = st.SelectToken("results.plates.span") as JObject;
            if (support == "A" || span == null)
                return new JObject { ["t_mm"] = floor, ["governs"] = "minimum", ["note"] = "case A: platen-backed, the floor rule (S26)" };
            var t = (double)span["t_mm"];
            var gov = (string)span["governs"];
            var j = new JObject { ["bc"] = span["bc"], ["span_mm"] = span["span_mm"], ["t_table_mm"] = span["tTable_mm"], ["fe"] = span["fe"]?.DeepClone() };
            if (gov == "add support") { j["t_mm"] = t; j["governs"] = gov; }
            else if (floor > t) { j["t_mm"] = floor; j["governs"] = "minimum"; }
            else { j["t_mm"] = t; j["governs"] = gov; }
            return j;
        }

        /// <summary>One cavity as a mold_calc design (mm, MPa, degC), all of its depth in the base.</summary>
        public static JObject Design(string grade, double tempC, double cycles, double moldingPressure, double l, double w, double h) => new JObject
        {
            ["units"] = new JObject { ["display"] = "metric" },
            ["inputs"] = new JObject
            {
                ["part_grade"] = PartGrade, ["mold_material"] = grade.StartsWith("st_") ? "Tool steel" : "Aluminium + steel inserts", ["mold_grade"] = grade,
                ["press_temp"] = tempC, ["cycle_life"] = cycles, ["cavities"] = 1,
                ["press_pressure_is"] = "On the part", ["part_pressure"] = 0.001, ["press_pressure"] = 0.001,
                ["press_rated_force"] = 10000, ["press_rated_gauge"] = 20,
                ["molding_pressure"] = moldingPressure, ["shrink_pct"] = 0, ["shrink_basis"] = "Measured in my molds at this temperature",
            },
            ["part"] = new JObject
            {
                ["box_mm"] = new JArray(l, w, h), ["projectedArea_mm2"] = l * w, ["perimeter_mm"] = 2 * (l + w),
                ["volume_mm3"] = 0.6 * l * w * h, ["depthBase_mm"] = h, ["depthLid_mm"] = 0,
            },
        };
    }
}

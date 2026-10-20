# Generates data/shop-standards.json (the user's shop rules) and data/sizing-defaults.json (every judgement factor of the
# structural method), plus the compiled copy of the sizing defaults the calculator falls back to
# (src/SwAgent/Mold/SizingDefaults.Builtin.cs; a unit test asserts it equals the shipped JSON).
#
# Run: python -I gen_defaults.py [--check]
# Sources: design 1.5.3 / 1.5.4 with the critique corrections (A1 bolt scatter + hot proof, A2 lifting, A3 abuse case,
# A8 creep bearing divisor, A11 gap budget, A12 shut-off pins, D1 slide-in dT, D3 pour time, E2 washers, H5 grip).
# [K] = practice/judgement, [C] = calculated, [S] = published.
import json, os, sys

SHOP = {
    "schema": "unify3d.mold-shop/1",
    "note": "Lengths are {\"in\": x} or {\"mm\": x}; the loader accepts either. A shop override file with the same name in "
            "%APPDATA%\\SwAgent\\tools\\builtin-data\\design-compression-mold\\ is merged key by key.",
    "flash": {
        "styleClassA": "moat",
        "lip": {"in": 0.125}, "grooveWidth": {"in": 0.5}, "grooveDepth": {"in": 0.125},
        "runnerPitch": {"in": 2.0}, "runnerPastEdge": {"in": 0.1}, "cornerRadius": {"in": 0.25},
        "lipCornerRule": "min(0.25 in, cavity corner R + lip)",
        "featureClearance": {"in": 0.25}, "separateMoatGap": {"in": 1.5},
        "partialMoatWidth": {"in": 0.25}, "noDeadEnds": True,
        "seams": "runners leave only through the assembled outer edge, may cross a seam at 90 deg, never run along one (>= 0.25 in); seam edges not filleted",
    },
    "plates": {"roundLW": {"in": 0.5}, "roundT": {"in": 0.125}, "roundTMetric": {"mm": 1.0}, "warnBelow": {"in": 0.25},
               "blankAllowancePerSide": {"mm": 3}, "blankAllowanceThicknessPerSide": {"mm": 1.5}, "skimPerFace": {"mm": 1.5}},
    "dowels": {"diameter": {"in": 0.5}, "inset": {"in": 0.625}, "keyOffset": {"in": 0.25}, "count": 4,
               "scheme": "round_plus_radial_slots",
               "schemeNote": "critique D1: one round dowel plus the others slotted along radial lines from it (or round + diamond + two relieved). "
                             "Confirm against how the shop's existing 4 dowels are fitted before overriding the shop standard."},
    "prySlots": {"width": {"in": 1.0}, "reach": {"in": 0.375}, "depth": {"in": 0.125}},
    "chamfer": {"size": {"in": 0.06}, "angleDeg": 45, "where": "base bottom outer edges and lid top outer edges"},
    "thermocouple": {"diameter": {"in": 0.25}, "depth": {"in": 2.0}, "endFromCavity": {"mm": 9}, "perPiece": 1},
    "handlesAbove": {"kg": 5},
    "handles": {"rule": "outside the platen outline, or wholly below the lid's platen face (critique D2, acceptance A16)"},
    "pour": {"massRate_g_per_s": 10, "handlingSecondsPerCavity": 15, "holdUpPct": 5,
             "note": "critique D3: t_pour per cavity = charge / mass rate + handling time"},
    "slideIn": {"locatingStop": True, "basePullHandles": True, "nothingProudBelowBase": True,
                "note": "critique E4: a positive stop lands the mold centred under the ram every cycle (makes S8 true)"},
    "washers": {"hardenedOnAluminium": True, "cboreOverWasherOd": {"mm": 0.5}},
    "machining": {"defaultTolerance": {"mm": 0.125}, "platenFaceFlatness": {"mm": 0.05}, "subplateParallelism": {"mm": 0.02},
                  "shutOffRecess": {"mm": [0.01, 0.03]}, "tappedReliefD": 0.5, "cornerReliefOverCutterRadius": 1.1,
                  "finishDefault": "as machined, 1.6 um Ra on cavity faces unless the drawing says otherwise"},
    "customProperties": ["MoldNumber", "Resin", "CureTime_min", "PressTemp_F"],
    "materialFallback": "6061-T6 (SS)",
    "designLibrary": "W:\\Solidworks-library\\library-features",
}

SIZING = {
    "schema": "unify3d.mold-sizing/1",
    "sfStatic": 2.0, "sfFatigue": 1.5,
    "bearingDivisor": {"highCycle": 3.0, "lowCycle": 2.0, "highCycleFrom": 100000,
                       "aluminiumHot": 4.0, "aluminiumHotAboveC": 150,
                       "note": "critique A8: aluminium lands and stops creep under sustained bearing above ~120 C; D_b = 4 above 150 C until 10,000 h creep data is loaded"},
    "wallRootFactor": {"form": "base + c w/h", "base": 1.0, "c": 2.8,
                       "note": "Track A2 3D pocket models (data/validation/solid3d-summary.json): 3D / plate deflection 1.24-2.23, envelope 1 + 2.8 w/h; the design's constant 1.2 holds only below w/h 0.07. A plain number here is a constant factor"},
    "pillarHeadFactor": 1.2, "lateralPressureFactor": 1.0, "tauMax": 0.5,
    "deltaFlash_mm": {"liquidBelow1000cP": 0.012, "liquid1000to10000cP": 0.020, "liquidAbove10000cP": 0.025,
                      "paste_lsr": 0.005, "rubber": 0.050, "compound": 0.025, "melt": 0.025, "no_flow": 0.025},
    "landFlatness_mm": {"liquidBelow1000cP": 0.006, "liquid1000to10000cP": 0.010, "liquidAbove10000cP": 0.012,
                        "paste_lsr": 0.0025, "rubber": 0.030, "compound": 0.012, "melt": 0.012, "no_flow": 0.012,
                        "note": "critique A11 gap budget: delta_allow = delta_flash - land flatness - platen parallelism. These flatness "
                                "values leave the deflection at about half the gap (design-rules 13.2.1); they go on the machining sheet"},
    "platenParallelism_mm": 0.0,
    "deltaFromTolerance": 0.25,
    "wallWidthTolFraction": 0.25, "defaultTolerance_mm": 0.125, "telescopingClearanceFraction": 0.5,
    "classA": {"pressureFactorOnTableMax": 1.5, "minDesignPressure_MPa": 0.35, "closeForceReserve": 1.25,
               "abuseSf": 1.0, "abuseSeparationMin": 1.0},
    "thermosetDepthAdder_MPa_per_25mm": 4.1,
    "pressReserve": 1.25, "excessForceFlag": 1.5, "offCentre": 0.05, "platenEdgeMargin_mm": 25, "daylightMargin_mm": 25,
    "strokeMargin_mm": 5,
    "minFloor_mm": {"aluminium": 12, "steel": 10}, "minFloorDepthFraction": 0.25,
    "minPlateFractionOfLongestSide": 0.05, "minPlate_mm": {"aluminium": 12, "steel": 10},
    "minWallClassB_mm": {"aluminium": 15, "steel": 10}, "steelAroundTcWell_mm": 6,
    "platenCoverageForCaseA": 0.8,
    "bolts": {"preloadSteelJoint": 0.75, "preloadAluminiumJoint": 0.60, "hotMaxOfProof": 0.90, "separationMin": 2.0,
              "loadFactorMin": 1.5, "embedmentLossAluminium": 0.10, "kDry": 0.20, "kLubed": 0.15,
              "edgeDistanceD": 1.5, "minSpacingD": 4.0, "maxSpacingD": 6.0, "insertAboveLeD": 2.5,
              "alphaBolt_perK": 1.17e-5, "boltE_GPa": 205, "enduranceInchA574_MPa": 160, "enduranceMetric129_MPa": 190,
              "headBearingOfUts": 0.9, "utsInchA574_MPa": 1241, "utsMetric129_MPa": 1220,
              "tighteningScatter": 0.25, "tighteningScatterAngleOrDti": 0.10,
              "hotProofFactor": {"tempsC": [20, 100, 150, 200, 250], "factor": [1.0, 0.95, 0.92, 0.89, 0.86],
                                 "note": "critique A1: stand-in from P20/4140 retained yield until supplier data for A574/12.9 is found"},
              "minPerPlate": 4, "minPerJointEnd": 2, "washerSpreadDeg": 45, "minDiameter_mm": 6.0, "maxRows": 2,
              "note": "preload fractions are ceilings: the sizing picks the highest preload that passes the hot and separation checks with tightening scatter; minDiameter_mm: the smallest screw used on mold plates (1/4 in or M6) [K]"},
    "fatigue": {"kaAluminium": 0.9, "kbSteel": 0.80, "keSteel": 0.868, "qAluminium": 0.8, "qSteel": 0.95,
                "ktByFilletRatio": [[0.2, 1.5], [0.1, 1.8], [0.05, 2.2], [0.0, 2.5]], "ktHoleInBentPlate": 2.5},
    "keys": {"minStep_mm": 9.5, "bearingSf": 2.5, "shearSf": 2.0},
    "pins": {"lateralImbalance": 0.5, "insertIfDiameterBelow_mm": 3.0, "insertIfHeightOverD": 2.0,
             "insertIfSteelWallBelow_mm": 3.0,
             "shutOffRecess_mm": [0.01, 0.03], "eulerK": 2.0, "eulerKGuided": 0.7, "axialSf": 2.0,
             "note": "critique A12: shut-off faces sit 0.01-0.03 mm below the land, never proud; the axial check assumes the pin ends proud by the full height tolerance"},
    "threads": {"insertShearFactorHelical": 1.2, "insertShearFactorKeyLocking": 1.75, "helicalLengthsD": [1.5, 2.0, 2.5, 3.0],
                "minHelicalInAluminiumD": 2.0, "stripThreadFill": 0.75, "tappedReliefD": 0.5},
    "thermal": {"alphaRefSteel_perK": 1.2e-5, "lidBaseGradient_K": 10, "lidBaseGradientSlideIn_K": 30,
                "runningClearance_mm": 0.02, "pinEngagementBeforeEntry_mm": 12},
    "cure": {"alphaThermal_m2_s": 1.0e-7, "alphaThermalThermoset_m2_s": 1.3e-7},
    "charge": {"holdUpPctLiquids": 5, "overflowFactorClassA": 2.0, "overflowFactorClassB": 1.5, "tolerancePct": 2},
    "lifting": {"loadConstant_kg": 23, "unknownLid_kg": 11, "occasionalPlate_kg": 23, "handlingLoadFactor": 2.0,
                "locations": {"table": {"H_cm": 40, "V_cm": 90, "D_cm": 25, "coupling": 1.0},
                              "press": {"H_cm": 55, "V_cm": 100, "D_cm": 25, "coupling": 0.90}},
                "workHours": 8,
                "frequencyMultiplier": {
                    "liftsPerMin": [0.2, 0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
                    "upTo1h": [1.00, 0.97, 0.94, 0.91, 0.88, 0.84, 0.80, 0.75, 0.70, 0.60, 0.52, 0.45, 0.41, 0.37, 0.34, 0.31, 0.28],
                    "upTo2h": [0.95, 0.92, 0.88, 0.84, 0.79, 0.72, 0.60, 0.50, 0.42, 0.35, 0.30, 0.26, 0.23, 0.21, 0.00, 0.00, 0.00],
                    "upTo8h": [0.85, 0.81, 0.75, 0.65, 0.55, 0.45, 0.35, 0.27, 0.22, 0.18, 0.15, 0.13, 0.00, 0.00, 0.00, 0.00, 0.00],
                    "note": "Waters et al. 1993 (NIOSH 94-110) Table 5, V >= 75 cm columns"},
                "note": "critique A2: RWL = 23 (25/H)(1 - 0.003|V-75|)(0.82 + 4.5/D) FM CM; 11 kg (25 lb) when the three lifting answers are unknown"},
    "cycleTiers": [{"upTo": 500, "tier": "T1"}, {"upTo": 10000, "tier": "T2"}, {"upTo": 100000, "tier": "T3"}, {"upTo": 1e12, "tier": "T4"}],
    "splitClearanceFromCavity_mm": 15,
    "limitedTemperature": {"stressRatioMax": 0.5, "maxCycles": 10000,
                           "note": "critique A8: a 'limited' temperature is allowed when every governing stress is <= 0.5 x the derated allowable"},
    "maxDataTemperature_C": 250,
}


def cs_literal(text):
    return text.replace('"', '""')


def outputs():
    here = os.path.dirname(os.path.abspath(__file__))
    data = os.path.normpath(os.path.join(here, "..", "..", "builtin", "design-compression-mold", "data"))
    src = os.path.normpath(os.path.join(here, "..", "..", "..", "src", "SwAgent", "Mold", "SizingDefaults.Builtin.cs"))
    sizing = json.dumps(SIZING, indent=1) + "\n"
    cs = ("// <auto-generated> by tools/dev/mold-data/gen_defaults.py - do not edit; change the generator and re-run it.\n"
          "namespace SwAgent.Mold\n{\n"
          "    internal static partial class SizingDefaults\n    {\n"
          "        /// <summary>The shipped data/sizing-defaults.json, compiled in so the calculator works without the data folder.</summary>\n"
          "        internal const string BuiltinJson = @\"" + cs_literal(sizing) + "\";\n    }\n}\n")
    return {os.path.join(data, "shop-standards.json"): json.dumps(SHOP, indent=1) + "\n",
            os.path.join(data, "sizing-defaults.json"): sizing,
            src: cs}


def main():
    bad = 0
    for path, text in outputs().items():
        if "--check" in sys.argv:
            with open(path, "r", encoding="utf-8") as f:
                same = f.read() == text
            print(os.path.basename(path), "matches" if same else "DIFFERS from the generator")
            bad += 0 if same else 1
        else:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
            print("wrote", path)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()

# Post-processing for data/mold-materials.json (design 1.5.2 + D18 + critique E2). build_mold_materials.py builds the
# research document unchanged; this adds the page and calculator fields and writes the shipped file.
import json, os, sys

# Page label, which page lists offer the grade, default flags, SOLIDWORKS library name (null = spike S10 fills it),
# skim per face for stock rounding (S75), which stock_thickness list it buys from, and whether it changes size when held
# hot (2024 ages above ~120 C: the only grade where never_above stays a hard block, critique A8).
GRADES = {
    "al_6061_t651":  dict(short="6061-T651", use=["plate"], isDefault=True, swMaterial="6061-T6 (SS)", skimPerFace_mm=1.5, stockKey="6061_T651_plate"),
    "al_7075_t651":  dict(short="7075-T651", use=["plate"], isDefault=False, swMaterial=None, skimPerFace_mm=1.5, stockKey="7075_T651_plate"),
    "al_mic6":       dict(short="MIC-6 cast plate", use=["plate", "backing"], isDefault=False, swMaterial=None, skimPerFace_mm=0.0, stockKey="mic6_cast_plate"),
    "al_qc10":       dict(short="QC-10", use=["plate"], isDefault=False, swMaterial=None, skimPerFace_mm=1.5, stockKey=None),
    "al_alumold500": dict(short="Alumold 500", use=["plate"], isDefault=False, swMaterial=None, skimPerFace_mm=1.5, stockKey=None),
    "al_2024_t351":  dict(short="2024-T351", use=[], isDefault=False, swMaterial=None, skimPerFace_mm=1.5, stockKey=None, agesDimensionally=True),
    "al_5083":       dict(short="5083", use=["plate", "backing"], isDefault=False, swMaterial=None, skimPerFace_mm=1.5, stockKey=None),
    "st_p20":        dict(short="P20 pre-hard", use=["plate", "insert"], isDefault=True, swMaterial=None, skimPerFace_mm=2.0, stockKey="p20_prehard"),
    "st_4140_ph":    dict(short="4140 pre-hard", use=["plate", "insert", "backing"], isDefault=False, swMaterial=None, skimPerFace_mm=2.0, stockKey="p20_prehard"),
    "st_a36":        dict(short="A36", use=["backing"], isDefault=False, swMaterial=None, skimPerFace_mm=2.0, stockKey=None),
    "st_1018_cd":    dict(short="1018 CD", use=["backing"], isDefault=False, swMaterial=None, skimPerFace_mm=1.0, stockKey=None),
    "st_h13":        dict(short="H13 44-50 HRC", use=["plate", "insert", "stop"], isDefault=False, swMaterial=None, skimPerFace_mm=2.0, stockKey=None),
    "st_s7":         dict(short="S7 54-56 HRC", use=["insert", "stop"], isDefault=False, swMaterial=None, skimPerFace_mm=2.0, stockKey=None),
    "st_a2":         dict(short="A2 57-62 HRC", use=["insert", "stop"], isDefault=False, swMaterial=None, skimPerFace_mm=2.0, stockKey=None),
    "st_420_ph":     dict(short="420 SS pre-hard", use=["plate", "insert"], isDefault=False, swMaterial=None, skimPerFace_mm=2.0, stockKey="p20_prehard"),
    "st_17_4ph":     dict(short="17-4PH H1025", use=["insert"], isDefault=False, swMaterial=None, skimPerFace_mm=2.0, stockKey=None),
}

# D18: the 6061 S-N points drop to the conservative Engineers Edge fit until checked against MMPDS.
FATIGUE_6061 = {"1e5": 142, "1e6": 111, "1e7": 101, "5e8": 96.5, "endurance_limit": False,
                "basis": "Engineers Edge fit S = 14479/sqrt(N) + 96.5 MPa (R=-1, smooth); lower than MMPDS-approx 185/130/105 - verify before release"}

# Hardened flat washers (critique E2: counterbores are sized for the washer, OD + 0.5 mm). Inch: ASTM F436 type 1 from
# 1/4 in (#10: through-hardened SAE pattern); metric: ISO 7089 300HV. Typical catalogue values - verify with the supplier.
WASHERS_INCH = {"#10-24": (0.219, 0.438, 0.049, "hardened SAE #10 (F436 starts at 1/4 in)"),
                "#10-32": (0.219, 0.438, 0.049, "hardened SAE #10 (F436 starts at 1/4 in)"),
                "1/4-20": (0.281, 0.625, 0.122, "ASTM F436"), "5/16-18": (0.344, 0.688, 0.122, "ASTM F436"),
                "3/8-16": (0.406, 0.813, 0.122, "ASTM F436"), "7/16-14": (0.469, 0.922, 0.122, "ASTM F436"),
                "1/2-13": (0.531, 1.063, 0.122, "ASTM F436"), "5/8-11": (0.688, 1.313, 0.122, "ASTM F436"),
                "3/4-10": (0.813, 1.469, 0.122, "ASTM F436")}
WASHERS_METRIC = {"M5": (5.3, 10, 1.0), "M6": (6.4, 12, 1.6), "M8": (8.4, 16, 1.6), "M10": (10.5, 20, 2.0),
                  "M12": (13.0, 24, 2.5), "M14": (15.0, 28, 2.5), "M16": (17.0, 30, 3.0), "M20": (21.0, 37, 3.0)}

# Stock screw lengths for the hardware BOM (ASME B18.3 / ISO 4762 common lengths).
SCREW_LENGTHS_IN = [0.5, 0.625, 0.75, 0.875, 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.25, 3.5, 3.75, 4.0, 4.5, 5.0, 5.5, 6.0]
SCREW_LENGTHS_MM = [8, 10, 12, 16, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 180, 200]


def finish(doc):
    for m in doc["mold_materials"]:
        extra = GRADES[m["id"]]
        m["short"] = extra["short"]
        m["use"] = extra["use"]
        m["isDefault"] = extra["isDefault"]
        m["swMaterial"] = extra["swMaterial"]
        m["skimPerFace_mm"] = extra["skimPerFace_mm"]
        m["stockKey"] = extra["stockKey"]
        m["agesDimensionally"] = extra.get("agesDimensionally", False)
        if m["id"] == "al_6061_t651":
            m["fatigue_MPa"] = FATIGUE_6061
    for f in doc["fasteners_inch"]:
        i, o, t, spec = WASHERS_INCH[f["size"]]
        f["washer"] = {"spec": spec, "id_in": i, "od_in": o, "t_in": t, "cbore_for_washer_in": round(o + 0.5 / 25.4, 4)}
        f["minor_dia_in"] = round(f["d_in"] - 1.226869 / f["tpi"], 4)
    for f in doc["fasteners_metric"]:
        i, o, t = WASHERS_METRIC[f["size"]]
        f["washer"] = {"spec": "ISO 7089 300HV", "id_mm": i, "od_mm": o, "t_mm": t, "cbore_for_washer_mm": o + 0.5}
        f["minor_dia_mm"] = round(f["d_mm"] - 1.226869 * f["pitch_mm"], 3)
    doc["fasteners_common"]["stock_lengths_in"] = SCREW_LENGTHS_IN
    doc["fasteners_common"]["stock_lengths_mm"] = SCREW_LENGTHS_MM
    doc["fasteners_common"]["washer_rule"] = ("Hardened washers on every aluminium clamped face. A hardened washer does not fit a "
                                              "standard SHCS counterbore: counterbore = washer OD + 0.5 mm (critique E2).")
    doc["schema"] = "unify3d.mold-materials/1"
    doc["generator"] = "tools/dev/mold-data/build_mold_materials.py"
    doc["conventions"]["use"] = "page lists: plate, insert, backing, stop; [] = not offered on the page (2024 ages and changes size above ~120 C)"
    doc["conventions"]["short"] = "page label (about 300 px wide)"
    return doc


def validate(doc):
    grades = doc["mold_materials"]
    assert len(grades) == 16, len(grades)
    assert len({g["id"] for g in grades}) == 16
    assert sum(1 for g in grades if g["isDefault"] and g["family"] == "aluminium") == 1
    assert sum(1 for g in grades if g["isDefault"] and g["family"] == "steel") == 1
    for g in grades:
        r = g["retained"]
        assert r["temps_C"] == [100, 150, 200, 250], g["id"]
        for k in ("yield_long_10000h", "uts_long_10000h", "E"):
            assert len(r[k]) == 4, (g["id"], k)
        ms = g["max_service_C"]
        assert ms["full_strength"] <= ms["derated"] <= ms["never_above"], g["id"]
        assert g["stockKey"] is None or g["stockKey"] in doc["stock_thickness"], g["id"]


def main(doc):
    doc = finish(doc)
    validate(doc)
    text = json.dumps(doc, indent=1) + "\n"
    here = os.path.dirname(os.path.abspath(__file__))
    path = os.path.normpath(os.path.join(here, "..", "..", "builtin", "design-compression-mold", "data", "mold-materials.json"))
    if "--check" in sys.argv:
        with open(path, "r", encoding="utf-8") as f:
            same = f.read() == text
        print("mold-materials.json", "matches" if same else "DIFFERS from the generator")
        sys.exit(0 if same else 1)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote", path)

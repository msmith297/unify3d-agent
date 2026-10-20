"""Generates data/mold-materials.json (mold materials + hardware data) for the
built-in tool design-compression-mold. Derived values (preload, torque, head bearing
pressure, thread engagement, thermal preload change, dowel fit loss) are
computed here so the formulas are explicit and repeatable.

Run: python -I build_mold_materials.py [--check]   (moved from the research notes, gen/build_materials.py;
the page/calculator fields are added in mold_materials_post.py)"""
import json, math, os
IN2MM = 25.4
LBF2N = 4.448222
KSI2MPA = 6.894757

def r(x, n=3):
    return None if x is None else float(f"{x:.{n}g}")

# ---------------------------------------------------------------------------
# 1) MOLD MATERIALS
# retained fractions: value(T)/value(20 C), tested AT temperature.
#   "long_10000h": after 10,000 h soak at T (design basis for heated molds)
#   "short_0p5h":  after <=0.5 h at T (only valid for first heat-ups)
# For steels tempered well above service temperature the two are the same.
# ---------------------------------------------------------------------------
TEMPS = [100, 150, 200, 250]
E_RET_AL = [0.97, 0.93, 0.86, 0.78]       # EN 1999-1-2 E(theta)/E(20)
E_RET_TOOLSTEEL = [0.99, 0.98, 0.975, 0.965]  # Uddeholm data 205 -> 200 GPa at 200 C
E_RET_CARBON = [1.0, 0.95, 0.90, 0.85]    # EN 1993-1-2 kE (conservative)

def shigley_steel_fatigue(su):
    """R=-1 polished specimen estimate (Shigley): S'e = 0.5 Su (Su<=1400 MPa) else 700 MPa;
    S(1e3)=0.9 Su; log-log line to 1e6; flat beyond 1e6."""
    se = 0.5 * su if su <= 1400 else 700.0
    s1e3 = 0.9 * su
    b = math.log10(se / s1e3) / 3.0
    s1e5 = s1e3 * (100 ** b)
    return {"1e5": round(s1e5), "1e6": round(se), "1e7": round(se),
            "basis": "derived: Shigley estimate, R=-1 rotating-beam polished; apply Marin factors (machined ka~0.7-0.8) and Goodman for R=0 press loading",
            "endurance_limit": True}

def mat(**k):
    k.setdefault("E_retained", None)
    return k

materials = [
  # ---------------- ALUMINIUM ----------------
  mat(id="al_6061_t651", name="6061-T6 / T651 rolled plate", family="aluminium",
      spec="ASTM B209 / AMS 4027; EN AW-6061 (EN 485)",
      E_GPa=68.9, poisson=0.33, density_kg_m3=2700, cte_per_K=23.6e-6, cte_range_C="20-100",
      k_W_mK=167, yield_MPa=276, uts_MPa=310, elongation_pct=12,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.95, 0.78, 0.40, 0.15],
                "uts_long_10000h":   [0.94, 0.75, 0.42, 0.19],
                "yield_short_0p5h":  [0.95, 0.90, 0.78, 0.55],
                "E": E_RET_AL,
                "notes": "Long-term values from ASM/Kaufman typical properties after 10,000 h at temperature (24 C 310/276; 100 C 290/262; 149 C 234/214; 204 C 131/103; 260 C 51/34 MPa UTS/YS), interpolated to 150/200/250 C. MMPDS data: 100 h at 150 C gives no loss, so loss develops between ~100 h and 10,000 h. Short-time row approximate (MMPDS-style 1/2 h curves)."},
      fatigue_MPa={"1e5": 185, "1e6": 130, "1e7": 105, "5e8": 96.5, "endurance_limit": False,
                   "basis": "R=-1 smooth; 5e8 rotating-beam value 96.5 MPa (ASM/MatWeb); 1e5-1e7 read from MMPDS/ASM S-N curves (approx)"},
      hardness="95 HB", machinability={"aa_rating": "C (some charts B)", "rel_mrr_vs_P20": 5.0, "index_1212": None},
      cost_index_per_kg=1.0,
      max_service_C={"full_strength": 120, "derated": 150, "never_above": 175},
      typical_use="General cavity plates, cores, lids, backing plates for PU/epoxy/resin molds; the default aluminium choice. At 150 C design to 0.78 x yield (long-term).",
      sources=["ASM Handbook Vol 2; Kaufman, Properties of Aluminum Alloys (ASM 1999)", "MatWeb 6061-T6", "LIGO T1000564 (MMPDS citation, 150 C/100 h no loss)", "EN 1999-1-2 Table 1 (E vs T)"],
      confidence="published (interpolated at 200/250 C)"),

  mat(id="al_7075_t651", name="7075-T651 rolled plate", family="aluminium",
      spec="ASTM B209 / AMS 4045; EN AW-7075",
      E_GPa=71.7, poisson=0.33, density_kg_m3=2810, cte_per_K=23.6e-6, cte_range_C="20-100",
      k_W_mK=130, yield_MPa=503, uts_MPa=572, elongation_pct=11,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.89, 0.37, 0.19, 0.13],
                "uts_long_10000h":   [0.84, 0.37, 0.20, 0.14],
                "yield_short_0p5h":  [0.93, 0.85, 0.65, 0.40],
                "E": E_RET_AL,
                "notes": "ASM/Kaufman after 10,000 h: 24 C 572/503; 100 C 483/448; 149 C 214/186; 204 C 110/87; 260 C 76/62 MPa. Strength collapses by overaging above ~120 C. Short-time row: Lin et al. 2020 (CJME) flow stress at 0.2% strain 536/408/305/198 MPa at 25/130/180/230 C."},
      fatigue_MPa={"1e5": 300, "1e6": 220, "1e7": 175, "5e8": 159, "endurance_limit": False,
                   "basis": "R=-1 smooth; 5e8 value 159 MPa (ASM/MatWeb); others read from S-N curves (approx)"},
      hardness="150 HB", machinability={"aa_rating": "B", "rel_mrr_vs_P20": 5.0, "index_1212": None},
      cost_index_per_kg=2.2,
      max_service_C={"full_strength": 100, "derated": 120, "never_above": 135},
      typical_use="High-pressure cavity/core plates and thin ribs for LOW temperature work only (RTV, PU, epoxy <= 100 C). Do not use for rubber or hot-cure resin (>= 130 C).",
      sources=["ASM Handbook Vol 2; Kaufman (ASM 1999)", "MatWeb 7075-T651", "Lin et al., Chin. J. Mech. Eng. 33 (2020) doi:10.1186/s10033-020-00494-8"],
      confidence="published"),

  mat(id="al_mic6", name="MIC-6 cast tool & jig plate (Alcoa/Arconic, now Howmet/Kaiser distributors)", family="aluminium",
      spec="proprietary cast, stress-relieved; ~7xxx-type chemistry",
      E_GPa=71.0, poisson=0.33, density_kg_m3=2700, cte_per_K=23.6e-6, cte_range_C="20-100 (24.5e-6 for 20-200)",
      k_W_mK=142, yield_MPa=105, uts_MPa=166, elongation_pct=3,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.97, 0.85, 0.65, 0.45],
                "uts_long_10000h":   [0.95, 0.82, 0.62, 0.42],
                "yield_short_0p5h":  [0.98, 0.92, 0.80, 0.62],
                "E": E_RET_AL,
                "notes": "ESTIMATE. No published elevated-temperature curve found; already stress-relieved and low strength, so it is dimensionally stable but has little strength to lose. Treat as estimate until supplier data is obtained."},
      fatigue_MPa={"1e5": 90, "1e6": 70, "1e7": 55, "endurance_limit": False,
                   "basis": "ESTIMATE ~0.33 x UTS at 1e7 (typical for cast aluminium)"},
      hardness="65 HB", machinability={"aa_rating": "B", "rel_mrr_vs_P20": 5.0, "index_1212": None},
      cost_index_per_kg=1.4,
      max_service_C={"full_strength": 150, "derated": 200, "never_above": 250},
      typical_use="Flat, stable backing plates, heater/thermal plates, lids and fixture plates where flatness (0.015 in max) and +/-0.005 in thickness matter more than strength. Low thread strength: tapped holes need inserts or through-bolts.",
      sources=["Upmet MIC-6 datasheet (upmet.com/products/aluminum/mic-6)", "Knowde Alcoa KAMA MIC-6", "McMaster-Carr MIC6 listings (tolerances)"],
      confidence="published at 20 C; elevated & fatigue estimated"),

  mat(id="al_qc10", name="QC-10 mold plate (Alcoa -> Arconic, sold via voestalpine HPM)", family="aluminium",
      spec="proprietary 7xxx high-strength mold alloy (ASM Alloy Digest Al-423)",
      E_GPa=71.7, poisson=0.33, density_kg_m3=2830, cte_per_K=24.7e-6, cte_range_C="not stated (13.7e-6/F per Arconic)",
      k_W_mK=159, yield_MPa=490, uts_MPa=530, elongation_pct=7,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.90, 0.55, 0.25, 0.15],
                "uts_long_10000h":   [0.88, 0.55, 0.27, 0.17],
                "yield_short_0p5h":  [0.93, 0.85, 0.70, 0.45],
                "E": E_RET_AL,
                "notes": "ESTIMATE based on overaged 7xxx (7050-T74-like) behaviour. Supplier positions QC-10 for injection molds (40-120 C). Get supplier data before using above 120 C."},
      fatigue_MPa={"1e5": 270, "1e6": 200, "1e7": 160, "endurance_limit": False,
                   "basis": "ESTIMATE from 7050-T7451 class data"},
      hardness="150-170 HB", machinability={"aa_rating": "A/B", "rel_mrr_vs_P20": 5.0, "index_1212": None},
      cost_index_per_kg=2.6,
      max_service_C={"full_strength": 100, "derated": 120, "never_above": 150},
      typical_use="Production aluminium cavity blocks in thick sections (properties uniform through 1-24 in); replaces P20 where temperature <= 120 C. Hard steel inserts/bushings at all bolt-down, dowel and wear contacts (Arconic fact sheet).",
      sources=["Arconic QC-10 Mold & Tooling Fact Sheet 2016 (CTE 13.7e-6/F, 4-5x P20 conductivity)", "Total Materia QC-10 (UTS 500-565 MPa, 150-170 HB, k~160)", "PlasticsToday NPE 2006 launch article"],
      confidence="partly published; yield and elevated values estimated"),

  mat(id="al_alumold500", name="Alumold 500 mold plate (Constellium)", family="aluminium",
      spec="proprietary 7xxx mold alloy, rolled 1-8 in / forged 10-27 in",
      E_GPa=73.1, poisson=0.33, density_kg_m3=2823, cte_per_K=23.8e-6, cte_range_C="not stated (13.2e-6/F per Alro)",
      k_W_mK=152, yield_MPa=538, uts_MPa=586, elongation_pct=10,
      thickness_dependence=[{"t_in": "1-3", "ys_ksi": 78, "uts_ksi": 85, "HB": 185},
                            {"t_in": "3-5", "ys_ksi": 76, "uts_ksi": 84, "HB": 185},
                            {"t_in": "5-6", "ys_ksi": 75, "uts_ksi": 82, "HB": 180},
                            {"t_in": "6-8", "ys_ksi": 73, "uts_ksi": 80, "HB": 180},
                            {"t_in": "10-11 forged", "ys_ksi": 67, "uts_ksi": 76, "HB": 180},
                            {"t_in": "17-27 forged", "ys_ksi": 60, "uts_ksi": 70, "HB": 165}],
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.90, 0.55, 0.25, 0.15],
                "uts_long_10000h":   [0.88, 0.55, 0.27, 0.17],
                "yield_short_0p5h":  [0.93, 0.85, 0.70, 0.45],
                "E": E_RET_AL,
                "notes": "ESTIMATE (same as QC-10, 7xxx overaging). Use <= 120 C."},
      fatigue_MPa={"1e5": 280, "1e6": 210, "1e7": 165, "endurance_limit": False, "basis": "ESTIMATE"},
      hardness="165-185 HB", machinability={"aa_rating": "A/B", "rel_mrr_vs_P20": 5.0, "index_1212": None},
      cost_index_per_kg=2.6,
      max_service_C={"full_strength": 100, "derated": 120, "never_above": 150},
      typical_use="Same role as QC-10. Alro stocks up to 20 in thick.",
      sources=["Alro Alumold 500 sheet (Mar 2025): YS/UTS/HB by thickness, k 88 BTU/ft-hr-F, CTE 13.2e-6/F, 0.102 lb/in3, E 10.6e6 psi", "Constellium mold plates page"],
      confidence="published at 20 C; elevated & fatigue estimated"),

  mat(id="al_2024_t351", name="2024-T351 plate", family="aluminium",
      spec="ASTM B209 / AMS 4037; EN AW-2024",
      E_GPa=73.1, poisson=0.33, density_kg_m3=2780, cte_per_K=23.2e-6, cte_range_C="20-100",
      k_W_mK=121, yield_MPa=324, uts_MPa=469, elongation_pct=19,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.96, 0.90, 0.41, 0.20],
                "uts_long_10000h":   [0.94, 0.78, 0.38, 0.17],
                "yield_short_0p5h":  [0.95, 0.90, 0.80, 0.60],
                "E": E_RET_AL,
                "notes": "ASM 2024-T3 after 10,000 h (approx 24 C 483/345; 100 C ~455/330; 149 C 379/310; 204 C 186/138; 260 C 76/62 MPa). WARNING: T3/T351 artificially ages above ~120 C; it holds strength at 150 C but changes dimensions and loses toughness while doing so. Use 2024-T851 or 2219/2618 for hot service."},
      fatigue_MPa={"1e5": 280, "1e6": 200, "1e7": 160, "5e8": 138, "endurance_limit": False,
                   "basis": "R=-1; 5e8 value 138 MPa (ASM/MatWeb); others approx S-N"},
      hardness="120 HB", machinability={"aa_rating": "B", "rel_mrr_vs_P20": 4.5, "index_1212": None},
      cost_index_per_kg=2.2,
      max_service_C={"full_strength": 100, "derated": 120, "never_above": 150},
      typical_use="Rarely used in molds: tough and fatigue-resistant clamp bars or levers at <= 100 C. Poor corrosion resistance, does not anodize well. Not a cavity material.",
      sources=["ASM Handbook Vol 2; Kaufman (ASM 1999)", "MatWeb 2024-T351"],
      confidence="published (elevated values approximate)"),

  mat(id="al_5083", name="5083-O / H116 plate, or cast 5083 tooling plate", family="aluminium",
      spec="ASTM B209 / B928; EN AW-5083; cast tooling plates (e.g. Alpase M-1, ACP 5080, ALCA PLUS)",
      E_GPa=70.3, poisson=0.33, density_kg_m3=2660, cte_per_K=23.8e-6, cte_range_C="20-100",
      k_W_mK=117, yield_MPa=145, uts_MPa=290, elongation_pct=20,
      alt_tempers={"H116": {"yield_MPa": 228, "uts_MPa": 317}, "cast_tooling_plate": {"yield_MPa": 120, "uts_MPa": 260, "note": "approx; varies by brand"}},
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [1.00, 0.90, 0.80, 0.56],
                "uts_long_10000h":   [0.95, 0.74, 0.53, 0.41],
                "yield_short_0p5h":  [1.00, 0.92, 0.82, 0.60],
                "E": E_RET_AL,
                "notes": "ASM 5083-O after 10,000 h (24 C 290/145; 100 C 275/145; 149 C 215/130; 204 C 150/115; 260 C 115/75 MPa). Non-heat-treatable, so no overaging: the most stable aluminium here. H116 strain-hardening anneals out progressively above ~150 C. Sensitization (beta phase) above 65 C makes it prone to intergranular/stress corrosion if wet."},
      fatigue_MPa={"1e5": 190, "1e6": 155, "1e7": 140, "endurance_limit": False, "basis": "approx (MatWeb 5083-H321 ~160 MPa at 5e8)"},
      hardness="75 HB (O), 85 HB (H116)", machinability={"aa_rating": "D (gummy)", "rel_mrr_vs_P20": 2.5, "index_1212": None},
      cost_index_per_kg=1.1,
      max_service_C={"full_strength": 200, "derated": 250, "never_above": 300},
      typical_use="Large, light, stable platens and lid frames at higher temperatures where strength needs are low; weldable. Cast 5083 tooling plate is a flat, stress-free alternative to MIC-6.",
      sources=["ASM Handbook Vol 2; Kaufman (ASM 1999)", "ASTM B928 (65 C sensitization limit)", "MatWeb 5083-O"],
      confidence="published"),

  # ---------------- STEELS ----------------
  mat(id="st_p20", name="P20 / 1.2311 pre-hardened (e.g. Uddeholm Impax Supreme, Bohler M200)", family="steel",
      spec="AISI P20; DIN 1.2311 (1.2738 with Ni for large blocks)",
      E_GPa=205, poisson=0.29, density_kg_m3=7800, cte_per_K=12.7e-6, cte_range_C="20-200",
      k_W_mK=29, yield_MPa=900, uts_MPa=1020, elongation_pct=14,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.95, 0.92, 0.89, 0.87],
                "uts_long_10000h":   [0.96, 0.93, 0.91, 0.89],
                "E": E_RET_TOOLSTEEL,
                "notes": "Uddeholm Impax Supreme (325 HB): Rp0.2 900 -> 800 MPa and Rm 1020 -> 930 MPa from 20 to 200 C; tempered ~550-600 C so no time effect below ~400 C. 100/150/250 C interpolated/extrapolated."},
      fatigue_MPa=shigley_steel_fatigue(1020),
      hardness="290-330 HB (28-34 HRC)", machinability={"aa_rating": None, "rel_mrr_vs_P20": 1.0, "index_1212": 45},
      cost_index_per_kg=1.0,
      max_service_C={"full_strength": 250, "derated": 350, "never_above": 450},
      typical_use="Production cavities and cores for rubber/silicone/PU compression molds up to ~200 C; textures and polishes; can be nitrided.",
      sources=["Uddeholm Impax Supreme technical datasheet (2024 ed.)", "MatWeb AISI P20"],
      confidence="published"),

  mat(id="st_4140_ph", name="4140 pre-hardened (28-32 HRC, 'HT' or 'PH')", family="steel",
      spec="AISI 4140 Q&T; DIN 1.7225 (42CrMo4)",
      E_GPa=205, poisson=0.29, density_kg_m3=7850, cte_per_K=12.3e-6, cte_range_C="20-100",
      k_W_mK=42.6, yield_MPa=850, uts_MPa=1000, elongation_pct=15,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.95, 0.92, 0.89, 0.86],
                "uts_long_10000h":   [0.96, 0.93, 0.91, 0.89],
                "E": E_RET_TOOLSTEEL,
                "notes": "Tempered ~540-600 C, so stable; ratios taken as for P20 (similar chemistry)."},
      fatigue_MPa=shigley_steel_fatigue(1000),
      hardness="28-32 HRC (270-300 HB)", machinability={"aa_rating": None, "rel_mrr_vs_P20": 1.1, "index_1212": 50},
      cost_index_per_kg=0.6,
      max_service_C={"full_strength": 300, "derated": 400, "never_above": 450},
      typical_use="Cheaper alternative to P20 for clamp plates, backing plates, support plates and simple cavities; hardness less uniform in thick sections; can be nitrided.",
      sources=["MatWeb AISI 4140 Q&T", "ASM Handbook Vol 1"],
      confidence="published (typical)"),

  mat(id="st_a36", name="A36 hot-rolled structural plate", family="steel",
      spec="ASTM A36; EN 10025 S235/S275 equivalent",
      E_GPa=200, poisson=0.26, density_kg_m3=7850, cte_per_K=11.7e-6, cte_range_C="20-100",
      k_W_mK=51.9, yield_MPa=250, uts_MPa=400, elongation_pct=20,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [1.00, 0.95, 0.90, 0.84],
                "uts_long_10000h":   [1.00, 1.00, 1.00, 1.00],
                "E": E_RET_CARBON,
                "notes": "EN 1993-1-2 0.2%-proof reduction (Annex E) and kE. UTS of carbon steel rises slightly 200-300 C (blue brittleness range); held at 1.0. Minimum values (250/400 MPa), typical is ~290/450."},
      fatigue_MPa=shigley_steel_fatigue(400),
      hardness="120-160 HB", machinability={"aa_rating": None, "rel_mrr_vs_P20": 1.3, "index_1212": 72},
      cost_index_per_kg=0.25,
      max_service_C={"full_strength": 300, "derated": 350, "never_above": 425},
      typical_use="Frames, rails, lifting lugs, press adapter plates, support pillars. Not for cavities (porosity/inclusions, poor polish). Mill scale and flatness need machining allowance.",
      sources=["ASTM A36", "EN 1993-1-2", "MatWeb A36"],
      confidence="published (minimums)"),

  mat(id="st_1018_cd", name="1018 cold-drawn bar/flat", family="steel",
      spec="AISI 1018 CD; EN C15/C18",
      E_GPa=205, poisson=0.29, density_kg_m3=7870, cte_per_K=11.9e-6, cte_range_C="20-100",
      k_W_mK=51.9, yield_MPa=370, uts_MPa=440, elongation_pct=15,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [1.00, 0.95, 0.90, 0.84],
                "uts_long_10000h":   [1.00, 1.00, 1.00, 1.00],
                "E": E_RET_CARBON,
                "notes": "As A36 (EN 1993-1-2). Cold-drawn work hardening is stable below ~400 C."},
      fatigue_MPa=shigley_steel_fatigue(440),
      hardness="126 HB", machinability={"aa_rating": None, "rel_mrr_vs_P20": 1.5, "index_1212": 78},
      cost_index_per_kg=0.35,
      max_service_C={"full_strength": 300, "derated": 350, "never_above": 425},
      typical_use="Small frame parts, spacers, keys, clamp straps, handles; case-hardenable for wear pads.",
      sources=["MatWeb AISI 1018 CD", "Machinery's Handbook machinability table"],
      confidence="published"),

  mat(id="st_h13", name="H13 / 1.2344 hardened 44-50 HRC (e.g. Uddeholm Orvar, Bohler W302)", family="steel",
      spec="AISI H13; DIN 1.2344",
      E_GPa=210, poisson=0.30, density_kg_m3=7800, cte_per_K=11.5e-6, cte_range_C="20-200 (published 10.4-12.6 depending on range)",
      k_W_mK=25, yield_MPa=1380, uts_MPa=1590, elongation_pct=9,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.97, 0.95, 0.93, 0.91],
                "uts_long_10000h":   [0.97, 0.95, 0.93, 0.91],
                "E": E_RET_TOOLSTEEL,
                "notes": "Secondary-hardening hot-work steel tempered 550-620 C; no time effect below ~500 C. Values at ~46 HRC."},
      fatigue_MPa=shigley_steel_fatigue(1590),
      hardness="44-50 HRC (supplied annealed ~200 HB, or pre-hardened 40-46 HRC)", machinability={"aa_rating": None, "rel_mrr_vs_P20": 0.4, "index_1212": 50, "note": "50% is annealed; hardened needs hard-milling/EDM/grinding"},
      cost_index_per_kg=1.5,
      max_service_C={"full_strength": 500, "derated": 540, "never_above": 600},
      typical_use="Hot cavities and inserts for rubber/silicone at 160-200 C, pinch-off and flash-land inserts, steel inserts for fragile features in aluminium molds; nitride for wear.",
      sources=["Uddeholm Orvar Supreme datasheet", "MatWeb AISI H13", "ASM Handbook Vol 1 (tool steels)"],
      confidence="published (typical)"),

  mat(id="st_s7", name="S7 shock-resisting tool steel, hardened 54-56 HRC", family="steel",
      spec="AISI S7; DIN ~1.2357",
      E_GPa=207, poisson=0.29, density_kg_m3=7830, cte_per_K=12.6e-6, cte_range_C="20-200",
      k_W_mK=28, yield_MPa=1600, uts_MPa=2000, elongation_pct=6,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.97, 0.95, 0.90, 0.85],
                "uts_long_10000h":   [0.97, 0.95, 0.90, 0.85],
                "E": E_RET_TOOLSTEEL,
                "notes": "Depends on tempering temperature: at 54-56 HRC S7 is tempered ~200-260 C, so service above ~175 C keeps tempering it (softening, size change). For hot molds temper at ~540 C (~48-50 HRC) instead. Strength from hardness conversion (ASTM A370/E140), approximate."},
      fatigue_MPa=shigley_steel_fatigue(2000),
      hardness="54-56 HRC (or 48-50 HRC high-tempered)", machinability={"aa_rating": None, "rel_mrr_vs_P20": 0.4, "index_1212": 45, "note": "annealed"},
      cost_index_per_kg=2.0,
      max_service_C={"full_strength": 175, "derated": 200, "never_above": 425, "note": "low-temper condition; ~425 C if tempered at 540 C"},
      typical_use="Tough inserts: pinch-offs, cutting lands, cores that see impact or side load, ejector/stripper pins.",
      sources=["ASM Handbook Vol 1 (tool steels)", "MatWeb AISI S7", "ASTM E140 hardness conversion"],
      confidence="typical / approximate"),

  mat(id="st_a2", name="A2 air-hardening tool steel, 57-62 HRC", family="steel",
      spec="AISI A2; DIN 1.2363",
      E_GPa=203, poisson=0.29, density_kg_m3=7860, cte_per_K=10.7e-6, cte_range_C="20-100",
      k_W_mK=26, yield_MPa=1700, uts_MPa=2100, elongation_pct=2,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.97, 0.95, 0.88, 0.82],
                "uts_long_10000h":   [0.97, 0.95, 0.88, 0.82],
                "E": E_RET_TOOLSTEEL,
                "notes": "At 60 HRC A2 is tempered ~175-200 C; service near that temperature softens it. Double-temper at ~500 C (~57 HRC) for hot molds. Tensile values approximate (brittle; compressive yield ~2200 MPa)."},
      fatigue_MPa=shigley_steel_fatigue(2100),
      hardness="57-62 HRC", machinability={"aa_rating": None, "rel_mrr_vs_P20": 0.4, "index_1212": 42, "note": "annealed"},
      cost_index_per_kg=1.5,
      max_service_C={"full_strength": 150, "derated": 175, "never_above": 400, "note": "low-temper condition; ~400 C if tempered at 500 C"},
      typical_use="Wear plates, flash-cutting lands, locating/interlock blocks, guide wear strips.",
      sources=["ASM Handbook Vol 1", "MatWeb AISI A2"],
      confidence="typical / approximate"),

  mat(id="st_420_ph", name="420 stainless pre-hardened 29-34 HRC (e.g. Bohler M303, Uddeholm Ramax HH/Mirrax; Stavax ESR when hardened)", family="steel",
      spec="AISI 420 mod.; DIN 1.2083 / 1.2316",
      E_GPa=200, poisson=0.28, density_kg_m3=7740, cte_per_K=10.8e-6, cte_range_C="20-200",
      k_W_mK=22, yield_MPa=880, uts_MPa=1050, elongation_pct=12,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.96, 0.94, 0.92, 0.90],
                "uts_long_10000h":   [0.96, 0.94, 0.92, 0.90],
                "E": E_RET_TOOLSTEEL,
                "notes": "Pre-hardened grades are tempered >= 550-600 C; stable to ~300 C. Avoid the 425-550 C temper-embrittlement range. Conductivity 16-25 W/mK depending on grade (Stavax ESR low end)."},
      fatigue_MPa=shigley_steel_fatigue(1050),
      hardness="29-34 HRC (Stavax ESR hardened 45-52 HRC)", machinability={"aa_rating": None, "rel_mrr_vs_P20": 0.8, "index_1212": 40},
      cost_index_per_kg=2.5,
      max_service_C={"full_strength": 250, "derated": 300, "never_above": 400},
      typical_use="Corrosion-resistant cavities: moisture-curing PU, PVC, latex, aggressive release agents, humid shops; mirror polish; no plating needed.",
      sources=["Uddeholm Stavax ESR / Ramax HH datasheets", "Bohler M303 datasheet", "MatWeb AISI 420"],
      confidence="typical"),

  mat(id="st_17_4ph", name="17-4PH (UNS S17400) H1025", family="steel",
      spec="ASTM A564 / A693 Type 630",
      E_GPa=197, poisson=0.27, density_kg_m3=7780, cte_per_K=10.8e-6, cte_range_C="20-100",
      k_W_mK=18.3, yield_MPa=1000, uts_MPa=1070, elongation_pct=12,
      retained={"temps_C": TEMPS,
                "yield_long_10000h": [0.95, 0.92, 0.90, 0.88],
                "uts_long_10000h":   [0.95, 0.92, 0.90, 0.88],
                "E": E_RET_TOOLSTEEL,
                "notes": "ASTM A564 minimums for H1025 (1070/1000 MPa, 35 HRC). Long exposure 300-400 C embrittles (475 C embrittlement); H1150 more stable."},
      fatigue_MPa=shigley_steel_fatigue(1070),
      hardness="35 HRC min (H1025); H900 40-44 HRC", machinability={"aa_rating": None, "rel_mrr_vs_P20": 0.7, "index_1212": 35},
      cost_index_per_kg=2.7,
      max_service_C={"full_strength": 300, "derated": 315, "never_above": 350},
      typical_use="Corrosion-resistant inserts and cores that must be machined finished then age-hardened with almost no distortion.",
      sources=["ASTM A564", "AK Steel 17-4PH product data bulletin", "MatWeb 17-4PH H1025"],
      confidence="published (minimums)"),
]

for m in materials:
    m["density_lb_in3"] = r(m["density_kg_m3"] / 27679.9, 3)
    m["cost_index_per_volume"] = r(m["cost_index_per_kg"] * m["density_kg_m3"] / 2700, 2)
    m["k_over_rho_cp_note"] = None
    del m["k_over_rho_cp_note"]

# ---------------------------------------------------------------------------
# 2) FASTENERS
# ---------------------------------------------------------------------------
inch_sizes = [
    # name, d_in, tpi, As_in2, head_dia, cbore_dia, cbore_depth, clr_close, clr_normal, tap_drill_name, tap_drill_in
    ("#10-24", 0.190, 24, 0.0175, 0.312, 0.375, 0.200, 0.201, 0.219, "#25", 0.1495),
    ("#10-32", 0.190, 32, 0.0200, 0.312, 0.375, 0.200, 0.201, 0.219, "#21", 0.159),
    ("1/4-20", 0.250, 20, 0.0318, 0.375, 0.438, 0.260, 0.266, 0.281, "#7", 0.201),
    ("5/16-18", 0.3125, 18, 0.0524, 0.469, 0.531, 0.322, 0.328, 0.344, "F", 0.257),
    ("3/8-16", 0.375, 16, 0.0775, 0.562, 0.625, 0.385, 0.391, 0.406, "5/16", 0.3125),
    ("7/16-14", 0.4375, 14, 0.1063, 0.656, 0.719, 0.447, 0.453, 0.469, "U", 0.368),
    ("1/2-13", 0.500, 13, 0.1419, 0.750, 0.812, 0.510, 0.516, 0.531, "27/64", 0.4219),
    ("5/8-11", 0.625, 11, 0.2260, 0.938, 1.000, 0.635, 0.641, 0.656, "17/32", 0.5312),
    ("3/4-10", 0.750, 10, 0.3345, 1.125, 1.188, 0.760, 0.766, 0.781, "21/32", 0.6562),
]
fasteners_inch = []
for (nm, d, tpi, As, hd, cb, cbd, cc, cn, tdn, td) in inch_sizes:
    proof_ksi = 140 if d <= 0.5 else 135      # ASTM A574 proof stress (derived from Fastenal chart)
    uts_ksi = 180 if d <= 0.5 else 170        # ASTM A574 min tensile
    proof = As * proof_ksi * 1000
    pre = 0.75 * proof
    t15 = 0.15 * d * pre / 12
    t16 = 0.16 * d * pre / 12
    t20 = 0.20 * d * pre / 12
    bearing_area = math.pi / 4 * (hd ** 2 - cn ** 2)  # head on plate with normal clearance hole
    fasteners_inch.append({
        "size": nm, "d_in": d, "tpi": tpi, "d_mm": r(d * IN2MM, 4),
        "tensile_stress_area_in2": As, "tensile_stress_area_mm2": r(As * IN2MM ** 2, 3),
        "proof_stress_ksi": proof_ksi, "min_uts_ksi": uts_ksi,
        "proof_load_lbf": round(proof), "proof_load_kN": r(proof * LBF2N / 1000, 3),
        "preload_75pct_lbf": round(pre), "preload_75pct_kN": r(pre * LBF2N / 1000, 3),
        "min_break_lbf": round(As * uts_ksi * 1000),
        "torque_K015_lubricated": {"in_lbf": round(t15 * 12), "ft_lbf": r(t15, 3), "N_m": r(t15 * 1.35582, 3)},
        "torque_K016_as_received": {"ft_lbf": r(t16, 3), "N_m": r(t16 * 1.35582, 3)},
        "torque_K020_dry": {"in_lbf": round(t20 * 12), "ft_lbf": r(t20, 3), "N_m": r(t20 * 1.35582, 3)},
        "head_dia_in": hd, "head_height_in": d, "hex_key_in": None,
        "cbore_dia_in": cb, "cbore_depth_in": cbd,
        "clearance_close_in": cc, "clearance_normal_in": cn,
        "tap_drill": tdn, "tap_drill_in": td,
        "head_bearing_area_in2": r(bearing_area, 3),
        "head_bearing_pressure_at_preload_MPa": round(pre / bearing_area * KSI2MPA / 1000),
    })
hex_keys = {"#10-24": "5/32", "#10-32": "5/32", "1/4-20": "3/16", "5/16-18": "1/4", "3/8-16": "5/16",
            "7/16-14": "3/8", "1/2-13": "3/8", "5/8-11": "1/2", "3/4-10": "5/8"}
for f in fasteners_inch:
    f["hex_key_in"] = hex_keys[f["size"]]

metric_sizes = [
    # name, d, P, As, dk, k(head h), s(key), cbore, cbore_depth, clr_fine, clr_medium, tap_drill
    ("M5", 5, 0.8, 14.2, 8.5, 5, 4, 10, 5.4, 5.3, 5.5, 4.2),
    ("M6", 6, 1.0, 20.1, 10, 6, 5, 11, 6.4, 6.4, 6.6, 5.0),
    ("M8", 8, 1.25, 36.6, 13, 8, 6, 15, 8.6, 8.4, 9.0, 6.8),
    ("M10", 10, 1.5, 58.0, 16, 10, 8, 18, 10.6, 10.5, 11.0, 8.5),
    ("M12", 12, 1.75, 84.3, 18, 12, 10, 20, 12.6, 13.0, 13.5, 10.2),
    ("M14", 14, 2.0, 115, 21, 14, 12, 24, 14.6, 15.0, 15.5, 12.0),
    ("M16", 16, 2.0, 157, 24, 16, 14, 26, 16.6, 17.0, 17.5, 14.0),
    ("M20", 20, 2.5, 245, 30, 20, 17, 33, 20.6, 21.0, 22.0, 17.5),
]
vdi_torque_012 = {"M5": 9.5, "M6": 16.5, "M8": 40, "M10": 79, "M12": 136, "M14": 217, "M16": 333, "M20": 649}
fasteners_metric = []
for (nm, d, P, As, dk, k, s, cb, cbd, cf, cm, td) in metric_sizes:
    proof = As * 970          # ISO 898-1 class 12.9 proof stress Sp = 970 MPa
    pre = 0.75 * proof
    bearing_area = math.pi / 4 * (dk ** 2 - cm ** 2)
    fasteners_metric.append({
        "size": nm, "d_mm": d, "pitch_mm": P, "tensile_stress_area_mm2": As,
        "class": "12.9 (ISO 898-1: Rm 1220 MPa min, Rp0.2 1100 MPa min, Sp 970 MPa)",
        "proof_load_kN": r(proof / 1000, 3), "preload_75pct_kN": r(pre / 1000, 3),
        "min_break_kN": r(As * 1220 / 1000, 3),
        "torque_K015_lubricated_N_m": r(0.15 * d * pre / 1000, 3),
        "torque_K020_dry_N_m": r(0.20 * d * pre / 1000, 3),
        "vdi2230_torque_mu012_N_m_approx": vdi_torque_012[nm],
        "head_dia_mm": dk, "head_height_mm": k, "hex_key_mm": s,
        "cbore_dia_mm": cb, "cbore_depth_mm": cbd,
        "clearance_fine_mm": cf, "clearance_medium_mm": cm, "tap_drill_mm": td,
        "head_bearing_area_mm2": r(bearing_area, 3),
        "head_bearing_pressure_at_preload_MPa": round(pre / bearing_area),
    })

# Thread engagement: L_e/D required so the tapped thread does not strip before the bolt breaks.
# Internal-thread shear area per unit length ~ 2.25*D (UN/ISO 60 deg, class 2B/6H, checked on 1/2-13),
# shear strength 0.6*Su_parent. As/D^2 ~ 0.57. => Le/D = 0.57*Su_b/(1.35*Su_p) = 0.42*Su_b/Su_p.
# Add 1.2 margin -> Le/D = 0.5 * Su_b / Su_p, minimum 1.0.
SU_BOLT = 1220.0
def le_over_d(su_parent):
    return max(1.0, 0.5 * SU_BOLT / su_parent)

engagement = []
for m in materials:
    su = m["uts_MPa"]
    ret = m["retained"]["uts_long_10000h"]
    row = {"material_id": m["id"], "uts_20C_MPa": su,
           "LeD_min_20C": r(le_over_d(su), 2),
           "LeD_min_100C_long": r(le_over_d(su * ret[0]), 2),
           "LeD_min_150C_long": r(le_over_d(su * ret[1]), 2),
           "LeD_min_200C_long": r(le_over_d(su * ret[2]), 2),
           "rule": "Pick the column for the press temperature. <=1.5: tap directly at 1.0-1.5 D. 1.5-2.5: tap directly at that length, inserts where screws come out often. >2.5: key-locking insert, 2D Heli-Coil (re-check with the insert's larger shear diameter) or through-bolt with nut."}
    engagement.append(row)

# ---------------------------------------------------------------------------
# Thermal preload change (steel bolt clamping aluminium)
# dF = (a_c - a_b) * dT * L_grip / (L_b/(E_b A_b) + L_grip/(E_c A_c))   (springs in series)
# Worked example: M10 12.9, grip 50 mm in 6061, 20 -> 150 C
# ---------------------------------------------------------------------------
def thermal_preload_example(alpha_c, E_c, A_c, label):
    a_b = 11.5e-6; E_b = 205000.0; L = 50.0; dT = 130.0
    A_b = 0.5 * (math.pi / 4 * 10 ** 2) + 0.5 * 58.0   # half shank, half threaded (approx)
    kb = E_b * A_b / L
    kc = E_c * A_c / L
    dF = (alpha_c - a_b) * dT * L / (1 / kb + 1 / kc)
    pre = 0.75 * 58.0 * 970
    return {"case": label, "kb_kN_per_mm": r(kb / 1000, 3), "kc_kN_per_mm": r(kc / 1000, 3),
            "free_mismatch_mm": r((alpha_c - a_b) * dT * L, 3),
            "dF_kN": r(dF / 1000, 3), "preload_cold_kN": r(pre / 1000, 3),
            "preload_hot_kN": r((pre + dF) / 1000, 3),
            "hot_preload_pct_of_proof": round((pre + dF) / (58.0 * 970) * 100)}

thermal_examples = [
    thermal_preload_example(23.6e-6, 68900 * 0.93, 300.0, "M10 12.9, 50 mm grip, 6061 plates, 20->150 C (E_Al at 150 C)"),
    thermal_preload_example(12.7e-6, 205000 * 0.98, 300.0, "M10 12.9, 50 mm grip, P20 plates, 20->150 C"),
]

# ---------------------------------------------------------------------------
# 3) INSERTS
# ---------------------------------------------------------------------------
inserts = {
  "when_to_use": [
    "Tapped hole in aluminium where the screw is removed often (lids, clamp bars, cavity inserts changed for maintenance): aluminium threads wear and gall.",
    "Required L_e/D for a direct tapped thread exceeds what the plate thickness allows (see thread_engagement).",
    "Any preloaded thread in MIC-6, 5083-O or cast plate (UTS < ~300 MPa), and any aluminium thread that runs hot (>= 150 C) for long periods.",
    "Repair of stripped threads.",
    "Hot runner / heated-platen contact points and dowel/bolt-down locations in 7xxx mold plate (Arconic QC-10 guidance: hardened steel inserts/bushings at all such contacts)."],
  "helical_wire_free_running": {
    "brands": "Heli-Coil (Stanley Engineered Fastening), Recoil, Bollhoff HELICOIL Plus; ASME B18.29.1 (inch), NASM/MS21209, DIN 8140 (metric)",
    "material": "304 stainless wire (standard), Inconel X-750 for >430 C",
    "lengths": "1.0D, 1.5D, 2.0D, 2.5D, 3.0D nominal; use 2.0D in aluminium for structural joints",
    "needs": "STI tap (not a standard tap); tap depth = insert length + ~1/4 turn + tap chamfer",
    "effect": "Parent-material shear diameter rises from D to ~D + 1.3 P (about 1.2 D for 1/2-13); spreads load more evenly; stainless wire stops galling",
    "inch": [
      {"thread": "#10-24", "sti_drill": "13/64", "sti_drill_in": 0.2031},
      {"thread": "#10-32", "sti_drill": "13/64", "sti_drill_in": 0.2031},
      {"thread": "1/4-20", "sti_drill": "17/64", "sti_drill_in": 0.2656},
      {"thread": "5/16-18", "sti_drill": "Q", "sti_drill_in": 0.332},
      {"thread": "3/8-16", "sti_drill": "X (Y per some charts)", "sti_drill_in": 0.397},
      {"thread": "7/16-14", "sti_drill": "29/64", "sti_drill_in": 0.4531},
      {"thread": "1/2-13", "sti_drill": "17/32", "sti_drill_in": 0.5312},
      {"thread": "5/8-11", "sti_drill": "21/32", "sti_drill_in": 0.6562},
      {"thread": "3/4-10", "sti_drill": "25/32", "sti_drill_in": 0.7812}],
    "metric": [
      {"thread": "M5x0.8", "sti_drill_mm": "5.2-5.4"}, {"thread": "M6x1", "sti_drill_mm": "6.3-6.4"},
      {"thread": "M8x1.25", "sti_drill_mm": "8.4-8.5"}, {"thread": "M10x1.5", "sti_drill_mm": "10.5-10.7"},
      {"thread": "M12x1.75", "sti_drill_mm": "12.5-12.8"}, {"thread": "M14x2", "sti_drill_mm": "14.5-15.0"},
      {"thread": "M16x2", "sti_drill_mm": "16.5-17.0"}, {"thread": "M20x2.5", "sti_drill_mm": "20.75-21.2"}],
    "drill_note": "Drill sizes vary by maker and hole tolerance class; the tool must read the chosen maker's table. Values above: Heli-Coil inch catalog practice / DIN 8140 range; Albany County Fasteners chart (2025) gives Q for 5/16-18, Y for 3/8-16, 17/32 for 1/2-13."},
  "key_locking_solid": {
    "brands": "Keensert (Howmet/Camloc; distributed by Clinserts, Bollhoff), Tridair/TICO key-locking, MS51830/MS51831 (mil-spec)",
    "material": "303 stainless or 4140 alloy steel (heavy duty), solid body with 2-4 keys driven into parent",
    "effect": "Much larger external thread (e.g. 1/4-20 inside -> 3/8-16 light, 7/16-14 heavy, 1/2-13 extra-heavy) so parent shear diameter is 1.5-2.0 D; highest pull-out; removable by drilling out keys",
    "series_1_4_20_example": [
      {"part": "KN420J (light, non-locking)", "external": "3/8-16", "tap_drill_in": 0.332, "countersink_in": 0.385, "min_depth_in": 0.43},
      {"part": "KNL420J (light, locking)", "external": "3/8-16", "tap_drill_in": 0.332},
      {"part": "KNHL420J (heavy, locking)", "external": "7/16-14", "tap_drill_in": 0.397, "countersink_in": 0.447, "min_depth_in": 0.43},
      {"part": "KNHXH420J (extra heavy)", "external": "1/2-13", "tap_drill_in": 0.453, "countersink_in": 0.51, "min_depth_in": 0.44}],
    "larger_size_example": {"part": "5/8-11 internal", "tap_drill_in": 0.578, "countersink_in": 0.63, "min_depth_in": 0.68},
    "sizing_rule": "Heavy-duty external thread ~ next standard size 1.5-1.75 x internal D; drill/tap with STANDARD taps for the external thread; read Clinserts/Keensert tables per part number.",
    "use_when": "MIC-6/5083/cast plate, frequently-removed lid and clamp screws, high-preload joints in 6061 that cannot get 2D engagement, hot (>= 150 C) aluminium."},
  "sources": ["Albany County Fasteners Thread Repair Sizing Chart (2025)", "ASME B18.29.1 (amesweb.info summary)", "Clinserts.com KN420J/KNL420J/KNHL420J/KNHXH420J pages", "Arconic QC-10 fact sheet (2016)"]
}

# ---------------------------------------------------------------------------
# 4) DOWELS AND GUIDES
# ---------------------------------------------------------------------------
def m6_dev(d):
    # ISO 286 m6 deviations (um)
    if d <= 3: return (2, 8)
    if d <= 6: return (4, 12)
    if d <= 10: return (6, 15)
    if d <= 18: return (7, 18)
    if d <= 30: return (8, 21)
    return (9, 25)
def h7_dev(d):
    if d <= 3: return (0, 10)
    if d <= 6: return (0, 12)
    if d <= 10: return (0, 15)
    if d <= 18: return (0, 18)
    if d <= 30: return (0, 21)
    return (0, 25)

metric_dowels = []
for d in [2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 25]:
    lo, hi = m6_dev(d)
    hl, hh = h7_dev(d)
    metric_dowels.append({"d_mm": d, "pin_m6_um": [lo, hi], "hole_H7_um": [hl, hh],
                          "slip_hole_mm": [round(d + (0.010 if d <= 10 else 0.015), 3), round(d + (0.025 if d <= 10 else 0.030), 3)],
                          "press_hole_steel": "H7 gives a light transition fit with m6; for positive press use N7/P7 hole (or p6 pin in H7)",
                          "press_hole_aluminium_150C_mm": [round(d - 0.005 - 0.0016 * d, 3), round(d - 0.0016 * d, 3)]})

inch_dowels = []
for frac, d in [("1/8", 0.125), ("3/16", 0.1875), ("1/4", 0.25), ("5/16", 0.3125), ("3/8", 0.375),
                ("1/2", 0.5), ("5/8", 0.625), ("3/4", 0.75), ("1", 1.0)]:
    inch_dowels.append({"nominal": frac, "d_in": d,
                        "standard_pin_in": [r(d + 0.0001, 6), r(d + 0.0003, 6)],
                        "oversize_pin_in": [r(d + 0.0009, 6), r(d + 0.0011, 6)],
                        "slip_hole_in": [r(d + 0.0005, 6), r(d + 0.0010, 6)],
                        "press_hole_steel_in": [r(d - 0.0003, 6), r(d, 6)],
                        "press_hole_aluminium_150C_in": [r(d - 0.0002 - 0.0016 * d, 6), r(d - 0.0016 * d, 6)]})

def fit_loss(d_mm, dT, a_hub=23.6e-6, a_pin=11.5e-6):
    return (a_hub - a_pin) * dT * d_mm

dowels = {
  "inch_hardened_ground": {
    "standard": "ASME B18.8.2 hardened ground machine dowel pins (alloy steel, 58 HRC min surface, typ 60-64; core 47-58 HRC)",
    "standard_tolerance": "nominal +0.0002 +/- 0.0001 in (oversize series: nominal +0.001 +/- 0.0001 in for repair)",
    "sizes": inch_dowels},
  "metric_hardened": {
    "standard": "ISO 8734 (hardened, m6) / ISO 2338 (unhardened m6 or h8); Misumi MSTH/MSTM (m6), Misumi dual-fit p6/g6, HASCO Z25, DME",
    "sizes": metric_dowels},
  "fit_rules": [
    "Each located pair of parts: ONE round dowel (press in one part, slip in the other) + ONE diamond/relieved pin or a slot oriented radially from the round pin. Never two tight round dowels across a hot aluminium/steel joint.",
    "Dowel holes depth: ~1.5-2.0 D in each part; through holes or a pull-out thread (tapped dowels) on blind holes so they can be extracted.",
    "Press in the plate that stays put, slip in the plate that comes apart. Slip-fit L/D >= 1.0 for repeatability."],
  "steel_pin_in_aluminium_temperature_effect": {
    "formula": "loss of interference (diametral) = (alpha_Al - alpha_steel) * dT * d",
    "examples_mm": [
      {"d_mm": 6, "dT_K": 130, "loss_mm": r(fit_loss(6, 130), 3)},
      {"d_mm": 10, "dT_K": 130, "loss_mm": r(fit_loss(10, 130), 3)},
      {"d_mm": 20, "dT_K": 130, "loss_mm": r(fit_loss(20, 130), 3)},
      {"d_mm": 25, "dT_K": 160, "loss_mm": r(fit_loss(25, 160), 3)}],
    "rule": "Loss is ~0.0016 mm per mm of diameter at 20->150 C (~0.0016 in/in). A standard light press fit (0.005-0.02 mm) of a 10 mm pin in aluminium is gone by ~80-120 C. Either (a) build in interference >= loss (the press_hole_aluminium_150C columns give ~0.0016 x d + the pin's own m6/+0.0002 in allowance; contact pressure ~90 MPa, von Mises ~150 MPa in the aluminium at 20 C - fine for 6061/7075/QC-10, NOT for MIC-6/5083-O) - install by shrink fit (heat plate to 150-180 C or chill pin), because pressing a hardened pin into aluminium at that interference shaves and galls the bore, (b) shrink-fit by heating the plate to 150-180 C, (c) use a steel bushing that is retained by flange+screws or a high-temperature retaining compound (Loctite 620 rated to 230 C; 638/648 to ~175-180 C), or (d) treat the pin as a slip fit and capture it."},
  "guide_pins_and_bushings": {
    "products": "Leader/guide pins and bushings: DME, Progressive Components, HASCO Z00/Z10, Misumi GPA/GBA, Meusburger E1000",
    "inch_diameters_in": [0.5, 0.625, 0.75, 0.875, 1.0, 1.25, 1.5, 2.0],
    "metric_diameters_mm": [12, 16, 20, 25, 30, 35, 40, 50],
    "fit": "pin g6/h6, bushing bore H7: running clearance ~0.01-0.04 mm diametral at assembly temperature",
    "count_and_layout": "4 pins for lids larger than ~250 mm; one offset (or one different diameter) so the lid only goes on one way; pins long enough to engage >= 1 D before any cavity/core contact.",
    "thermal_mismatch": {
      "formula": "mismatch between pin centres = L * |alpha_1 * dT_1 - alpha_2 * dT_2|  (L = distance from the datum pin, or half the pin spacing if centred)",
      "example_same_material": "Both halves 6061 at the same temperature: no mismatch. A 10 K difference between lid and base at L = 200 mm: 200 * 23.6e-6 * 10 = 0.047 mm -> more than the 0.01-0.04 mm running clearance; lid binds.",
      "example_mixed": "Steel lid on 6061 base, both 20->150 C, L = 200 mm: 200 * (23.6-12.7)e-6 * 130 = 0.28 mm. Do NOT mix lid/base materials with round pins.",
      "recommendation": "Use round pin + radially-slotted (or diamond) bushing so growth is free along the line to the datum; add tapered/side interlocks (2-5 deg, hardened, ~0.01-0.02 mm preload at temperature) for final cavity-to-core registration; size guide clearance for the worst plate-to-plate temperature difference the heated platens allow (typically 5-15 K)."},
    "bushing_in_aluminium": "Hardened steel or bronze bushings in all aluminium guide bores (aluminium galls against steel pins). Press with ~0.002 x d interference for 150 C service, or flange-and-screw retained."}
}

# ---------------------------------------------------------------------------
# 5) STOCK THICKNESS
# ---------------------------------------------------------------------------
stock_thickness = {
  "6061_T651_plate": {
    "inch": [0.25, 0.3125, 0.375, 0.5, 0.625, 0.75, 0.875, 1.0, 1.125, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0, 7.0, 8.0],
    "metric_mm": [5, 6, 8, 10, 12, 15, 16, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 90, 100, 110, 120, 130, 140, 150, 160, 180, 200],
    "notes": "Rolled plate is supplied OVER nominal (ANSI H35.2 / EN 485-4 tolerances, roughly +0.5-3% on thick plate); plan to skim both faces: finished thickness ~ nominal - 0.03 to 0.06 in (0.8-1.5 mm). Above 6-8 in buy 6061-T6 forged block or switch to Alumold/QC-10. Metric plate in Europe is often 6082-T651 (similar properties)."},
  "mic6_cast_plate": {
    "inch": [0.25, 0.3125, 0.375, 0.5, 0.625, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 3.5, 4.0],
    "metric_mm": [5, 6, 8, 10, 12, 15, 20, 25, 30, 35, 40, 45, 50, 60, 70, 80, 90, 100],
    "notes": "Sold to finished decimal thickness +/-0.005 in (+/-0.13 mm), both faces machined, flatness ~0.015 in max; no skim needed. Common stock to 2.5 in (Speedy Metals), thicker by order. Metric column is generic cast tooling plate (5083-type ALCA/ACP/Alpase), +/-0.1 mm typical."},
  "7075_T651_plate": {
    "inch": [0.25, 0.3125, 0.375, 0.5, 0.625, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 5.0, 6.0],
    "metric_mm": [5, 6, 8, 10, 12, 15, 20, 25, 30, 35, 40, 45, 50, 60, 70, 80, 90, 100, 120, 150],
    "notes": "T651 to ~4-6 in; thicker sections T7351/T7451 (lower strength, better stress-corrosion). Over-nominal like 6061."},
  "qc10_alumold500": {
    "inch": "1-24 (QC-10); 1-8 rolled, 10-27 forged (Alumold 500; Alro stocks to 20 in), sawn to order",
    "notes": "Mold-block alloys sold cut-to-size; buy finished-size + ~0.06-0.12 in per face."},
  "p20_prehard": {
    "inch": [0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0, 7.0, 8.0, 9.0, 10.0, 12.0],
    "metric_mm": [20, 25, 30, 35, 40, 45, 50, 60, 70, 80, 90, 100, 120, 150, 200, 250, 300],
    "notes": "Distributor sawn plate/blocks, any size to ~24 in / 600 mm; supplied rough (+0.06-0.125 in over) with decarb, so allow ~1.5-3 mm per face. Standard mold-base plates (DME inch A/B plates 0.875, 1.375, 1.875, 2.375, 2.875, 3.375, 3.875 in...; HASCO K-plates 22, 27, 36, 46, 56, 66, 76, 96, 116, 136, 156, 196 mm) come finish-ground and are often the cheapest P20 for a standard footprint - verify against catalogs."},
  "sources": ["Speedy Metals cast plate list / tolerance", "McMaster-Carr MIC6 and cast plate tables", "Alro Alumold 500 sheet", "Upmet MIC-6", "ANSI H35.2 / EN 485-4 (dimensional tolerances)", "DME and HASCO mold-base catalogs (plate series - verify)"]
}

doc = {
  "schema_version": 1,
  "generated": "2026-10-19",
  "units": {"E_GPa": "GPa", "cte_per_K": "1/K", "k_W_mK": "W/(m K)", "density": "kg/m3",
            "strength": "MPa", "retained": "fraction of 20 C value, tested at temperature",
            "fatigue_MPa": "stress amplitude, R=-1 unless noted"},
  "conventions": {
    "retained_long_vs_short": "Design heated aluminium molds on the long_10000h row: a mold held at temperature 8 h/day reaches 10,000 h in ~5 years. short_0p5h is only for first heat-up checks.",
    "max_service_C": "full_strength = design with the 20 C numbers less <=5-10%; derated = usable with the long-term retained fraction; never_above = overaging/softening/embrittlement risk.",
    "cost_index": "per kg relative to 6061-T651 = 1.0 (~US$8/kg distributor cut plate, 2025-26); order-of-magnitude only - get quotes.",
    "machinability": "aa_rating = Aluminum Association-style A-E letter (approx); index_1212 = AISI basis 1212=100 (steels, annealed or as noted); rel_mrr_vs_P20 = rough metal removal rate vs P20 pre-hard on a typical VMC with carbide (aluminium is usually spindle-limited).",
    "confidence": "published | derived | estimate - estimates must be shown to the user as such."},
  "mold_materials": materials,
  "fasteners_common": {
    "inch_spec": "ASTM A574 alloy-steel SHCS (ASME B18.3 dimensions). Proof stress 140 ksi (<=1/2 in), 135 ksi (>1/2 in); min UTS 180/170 ksi.",
    "metric_spec": "ISO 4762 SHCS, property class 12.9 (ISO 898-1).",
    "preload_rule": "Fi = 0.75 x proof load (Fastenal/Unbrako practice for re-usable joints). For steel bolts in hot aluminium start lower, see thermal_preload.",
    "torque_formula": "T = K * d * Fi, K = 0.15 lubricated (oil/anti-seize), 0.16 as-received black oxide, 0.20 dry. Torque is an indirect measure: +/-25% preload scatter.",
    "head_bearing": "Allowable surface pressure under the head is ~UTS of the clamped material (VDI 2230 Table A9 values are roughly 0.9-1.2 x Rm). 12.9 SHCS at 75% proof put ~400-700 MPa under the head (see head_bearing_pressure_at_preload_MPa) - above 6061 (310 MPa, ~230 hot) and far above MIC-6 (166). Use through-hardened washers (ASTM F436 / ISO 7089-300HV or DIN 6340) or SHCS with larger heads, or reduce preload, on ALL aluminium clamped faces.",
    "thread_engagement": {
      "formula": "Le/D >= 0.5 * Su_bolt / Su_parent (min 1.0). Derived: internal-thread shear area/length ~2.25 D, parent shear strength 0.6 Su, As ~0.57 D^2, 1.2 margin so the bolt breaks before the tapped thread strips. Use Su_parent at operating temperature (long-term).",
      "by_material": engagement},
    "thermal_preload": {
      "formula": "dF = (alpha_clamped - alpha_bolt) * dT * L_grip / (L_grip/(E_b*A_b) + L_grip/(E_c*A_c_eq))  [N]; A_c_eq = VDI 2230 equivalent clamped-cone area (~3-5 x bolt As for typical plates). Also include E(T) for both parts. Heating ADDS preload when the clamped part expands more (aluminium); cooling below assembly temperature removes it.",
      "examples": thermal_examples,
      "consequences": [
        "12.9 SHCS torqued to 75% proof in 6061 reaches ~100% of proof load at 150 C - near yield. Torque aluminium joints to ~60-65% proof, or use a reduced-shank / long-grip bolt (lower kb), or spacer sleeves.",
        "Aluminium under the head and in the threads then creeps/relaxes at 150 C, so preload drops over the first few cycles: re-torque hot after the first 2-3 heat cycles and specify hardened washers.",
        "Steel clamping steel (P20) changes little (~5% of preload)."]}
  },
  "fasteners_inch": fasteners_inch,
  "fasteners_metric": fasteners_metric,
  "inserts": inserts,
  "dowels": dowels,
  "stock_thickness": stock_thickness,
  "hooks": {
    "machinability_sim": "mold_materials[].id + machinability{} + hardness are the inputs a future MCP machinability checker should receive.",
    "process_sim": "mold_materials[].k_W_mK, density, cte, retained.E are the thermal/structural inputs for a future process/demolding simulator."}
}

# Shipped-file fields, validation and output (mold_materials_post.py; --check compares with the shipped file).
if __name__ == "__main__":
    import sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import mold_materials_post
    mold_materials_post.main(doc)

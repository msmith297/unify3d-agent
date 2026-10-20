# Generates data/materials.json (part-material lookup table) for the built-in tool design-compression-mold.
# Run: python -I gen_materials.py           writes tools/builtin/design-compression-mold/data/materials.json
#      python -I gen_materials.py --check   fails (exit 1) when the shipped file differs from what this script makes
# Moved here from the research notes (scratchpad mold/gen_materials.py); the rows are unchanged, the post-processing at
# the end adds the fields of design 1.5.1 and critique A5/A10/C5 (see the comments there).
import json, copy, os

def R(a, b, c):
    return {"min": a, "typ": b, "max": c}

# ---------- flash / land / vent presets by flow class ----------
FLASH = {
    "liquid_low": dict(moldStyle="closed pour mold, metal-to-metal land, risers/vents at high points",
                       landWidthMm=R(2.0, 3.0, 4.0), flashThicknessMm=R(0.0, 0.02, 0.05), ventDepthMm=R(0.02, 0.03, 0.05),
                       overflowGrooveRadiusMm=R(1.0, 1.5, 2.0)),
    "liquid_med": dict(moldStyle="closed pour/press mold, metal-to-metal land, overflow groove + risers",
                       landWidthMm=R(1.5, 2.5, 3.0), flashThicknessMm=R(0.0, 0.03, 0.05), ventDepthMm=R(0.03, 0.04, 0.05),
                       overflowGrooveRadiusMm=R(1.0, 1.5, 2.5)),
    "liquid_high": dict(moldStyle="closed press mold, land + overflow groove",
                        landWidthMm=R(1.0, 1.5, 2.0), flashThicknessMm=R(0.02, 0.04, 0.08), ventDepthMm=R(0.03, 0.05, 0.08),
                        overflowGrooveRadiusMm=R(1.5, 2.0, 2.5)),
    "paste_lsr": dict(moldStyle="closed press mold, parting-line fit <=0.005 mm, vacuum or fine vents",
                      landWidthMm=R(1.0, 2.0, 3.0), flashThicknessMm=R(0.0, 0.003, 0.005), ventDepthMm=R(0.003, 0.005, 0.01),
                      overflowGrooveRadiusMm=R(1.0, 1.5, 2.0)),
    "rubber": dict(moldStyle="flash-type compression mold: land then ball-milled overflow groove (tear-trim optional)",
                   landWidthMm=R(0.5, 1.0, 1.5), flashThicknessMm=R(0.05, 0.10, 0.15), ventDepthMm=R(0.03, 0.05, 0.08),
                   overflowGrooveRadiusMm=R(1.0, 1.5, 2.0)),
    "rubber_stiff": dict(moldStyle="flash-type compression mold, wider land and larger overflow groove, high pressure",
                         landWidthMm=R(0.8, 1.2, 2.0), flashThicknessMm=R(0.05, 0.10, 0.20), ventDepthMm=R(0.03, 0.05, 0.08),
                         overflowGrooveRadiusMm=R(1.5, 2.0, 3.0)),
    "compound": dict(moldStyle="semi-positive mold with telescoping shear edge (vertical flash) or flash land 1.5-3 mm",
                     landWidthMm=R(1.5, 2.0, 3.0), flashThicknessMm=R(0.025, 0.05, 0.10), ventDepthMm=R(0.02, 0.03, 0.05),
                     overflowGrooveRadiusMm=R(0.0, 0.0, 1.0)),
    "sheet_bulk": dict(moldStyle="positive/semi-positive shear-edge mold, radial clearance 0.05-0.10 mm, shear-edge length 3-6 mm",
                       landWidthMm=R(3.0, 4.5, 6.0), flashThicknessMm=R(0.05, 0.08, 0.10), ventDepthMm=R(0.02, 0.03, 0.05),
                       overflowGrooveRadiusMm=R(0.0, 0.0, 0.0)),
    "no_flow": dict(moldStyle="positive (plunger/sleeve) mold, plunger clearance <=0.05 mm, no overflow",
                    landWidthMm=R(0.0, 0.0, 0.0), flashThicknessMm=R(0.0, 0.02, 0.05), ventDepthMm=R(0.0, 0.0, 0.02),
                    overflowGrooveRadiusMm=R(0.0, 0.0, 0.0)),
    "foam": dict(moldStyle="clamped closed mold, sealed parting line with vent holes at last-fill points",
                 landWidthMm=R(3.0, 5.0, 8.0), flashThicknessMm=R(0.0, 0.02, 0.05), ventDepthMm=R(0.0, 0.0, 0.0),
                 overflowGrooveRadiusMm=R(0.0, 0.0, 0.0)),
}

COMPAT = {
    # aluminium / P20 prehard / hardened tool steel (H13, 1.2343) / 420 stainless (1.2083)
    "pu_hot":   dict(aluminium="good", p20Steel="good", hardenedToolSteel="good", stainless420="good",
                     plating="none needed; optional electroless nickel or PTFE-impregnated hard anodize for release",
                     notes="100-120 C mold and <150 psi: aluminium 6061-T651/7075-T651 is the normal choice; steel inserts only for thin/fragile cores and wear features."),
    "rt_cast":  dict(aluminium="good", p20Steel="good", hardenedToolSteel="good", stainless420="good",
                     plating="none", notes="Room-temperature cast; silicone, urethane, epoxy or aluminium molds all usable."),
    "rubber":   dict(aluminium="limited", p20Steel="good", hardenedToolSteel="good", stainless420="good",
                     plating="hard chrome 0.01-0.025 mm or electroless nickel recommended for production",
                     notes="150-190 C and 700-3000 psi: aluminium OK for prototypes/short runs (7075 overages above its 121 C aging temperature; lands peen/hob at high pressure). Steel cavity plates or steel lands for production."),
    "rubber_corrosive": dict(aluminium="not_recommended", p20Steel="limited", hardenedToolSteel="good", stainless420="good",
                     plating="hard chrome or electroless nickel(-PTFE) required on carbon/tool steel",
                     notes="Acid by-products (HCl/HF) corrode and foul unplated steel and aluminium; use 420 stainless or plated H13."),
    "compound_abrasive": dict(aluminium="not_recommended", p20Steel="limited", hardenedToolSteel="good", stainless420="good",
                     plating="hard chrome 0.01-0.025 mm on hardened steel (wear + release)",
                     notes="Glass/mineral/silica fillers and 2000-8000 psi wear and hob soft metals; P20 only for prototype runs; production H13 / S7 / 420 at 48-56 HRC."),
    "compound_corrosive": dict(aluminium="not_recommended", p20Steel="limited", hardenedToolSteel="good", stainless420="good",
                     plating="hard chrome required on carbon/tool steel",
                     notes="Formaldehyde/acid-catalysed amino resins and ammonia-releasing phenolics stain/corrode unplated steel; chrome-plated hardened steel or 420 stainless."),
    "smc":      dict(aluminium="limited", p20Steel="good", hardenedToolSteel="good", stainless420="good",
                     plating="hard chrome on shear edges and Class-A surfaces",
                     notes="Glass fibre wears shear edges; P20 acceptable for moderate volume, hardened inserts/shear edges recommended; aluminium for prototypes only."),
    "hot_tp":   dict(aluminium="not_recommended", p20Steel="limited", hardenedToolSteel="good", stainless420="limited",
                     plating="none; polished",
                     notes="Mold temperature above 200 C: aluminium loses temper/strength; use H13 (Victrex: H13 >50 HRC for PEEK)."),
    "uhmw":     dict(aluminium="limited", p20Steel="good", hardenedToolSteel="good", stainless420="good",
                     plating="none; polished",
                     notes="200-230 C for hours: aluminium overages and softens; steel recommended. Mold is a positive sleeve/plunger; must be strong enough for 1500-3000 psi at temperature."),
    "ptfe_preform": dict(aluminium="limited", p20Steel="good", hardenedToolSteel="good", stainless420="good",
                     plating="hard chrome or polished hardened steel",
                     notes="Preform die is used at room temperature (sintering is done free-standing in an oven); 3000-10000 psi needs hardened, polished steel to avoid galling and contamination."),
    "foam":     dict(aluminium="good", p20Steel="good", hardenedToolSteel="good", stainless420="good",
                     plating="none", notes="Low pressure (<30 psi); cast or machined aluminium standard."),
    "pvc":      dict(aluminium="good", p20Steel="limited", hardenedToolSteel="limited", stainless420="good",
                     plating="none on aluminium; chrome/nickel on steel",
                     notes="Plastisol releases HCl if overheated (>190-200 C); aluminium (industry standard for heat-press patches) and stainless are fine, unplated carbon steel rusts."),
}

TEMPLATE = dict(
    id=None, family=None, familyLabel=None, group=None, name=None, hardness=None, processType=None,
    shrinkPct=None, shrinkBasis="cold_mold_cold_part_steel_ref", shrinkNotes="",
    viscosityClass=None, viscosityCp=None, viscosityTempC=None, mooney=None, bulkFactor=R(1.0, 1.0, 1.0),
    materialTempC=None, potLifeMin=None,
    moldTempC=None, demoldMin=None, cureBasis="",
    postCure=None, sg=None,
    moldingPressurePsi=None, pressureNotes="",
    draftDeg={"min": 0.5, "typ": 1.0},
    flash=None, flashAllowancePct=None, overflowPctOfPartVolume=None,
    ventingSensitivity=None, ventingNotes="",
    releaseAgent="", moistureSensitivity=None, moistureNotes="",
    corrosiveness="none", corrosionNotes="",
    moldCompat=None, hazards="", confidence="medium", sources=[], notes="",
)

out = []
def add(base, **kw):
    d = copy.deepcopy(TEMPLATE)
    d.update(copy.deepcopy(base))
    d.update(copy.deepcopy(kw))
    if isinstance(d["flash"], str):
        d["flash"] = copy.deepcopy(FLASH[d["flash"]])
    if isinstance(d["moldCompat"], str):
        d["moldCompat"] = copy.deepcopy(COMPAT[d["moldCompat"]])
    for k, v in TEMPLATE.items():
        if k not in ("hardness", "viscosityCp", "viscosityTempC", "mooney", "materialTempC", "potLifeMin", "postCure") and d[k] is None:
            raise ValueError(f"{d['id']}: missing {k}")
    out.append(d)

def pc(req, t, h, notes=""):
    return {"required": req, "tempC": t, "hours": h, "notes": notes}

# ======================= POLYURETHANES =======================
PU_HOT = dict(group="polyurethane", processType="liquid_cast_hot",
    shrinkBasis="cold_mold_cold_part_steel_ref",
    shrinkNotes="Hot-cast in 100-120 C metal mold; most of the shrink is thermal contraction of the elastomer (CTE ~1.5-2.0e-4 /K) from mold to room temperature plus ~0.3-0.5% chemical shrink. Supplier data rarely published; prove out on first article and leave cavities steel-safe.",
    viscosityClass="liquid_low", viscosityTempC=85,
    flash="liquid_low", flashAllowancePct=R(3, 5, 8), overflowPctOfPartVolume=R(3, 5, 8),
    moldingPressurePsi=R(0, 50, 150),
    pressureNotes="Press only clamps the closed mold against hydrostatic head and reaction expansion; no flow pressure needed. Pour-then-close: close before gel (within pot life).",
    ventingSensitivity="very_high",
    ventingNotes="Liquid traps air under every downward-facing surface: orient part so the pour enters low and air exits high; risers/vent holes 1-3 mm at all high points; degas prepolymer at 1-5 mbar before mixing.",
    releaseAgent="Semi-permanent or silicone/wax release (e.g. Chem-Trend, Stoner urethane releases); PTFE or nickel-PTFE coating for production. Use silicone-free release if parts are bonded/painted.",
    moistureSensitivity="very_high",
    moistureNotes="Isocyanate + water -> CO2 bubbles and soft spots. Keep prepolymer under dry N2, molds preheated and dry, inserts degreased and primed.",
    corrosiveness="none", corrosionNotes="Not corrosive; liquid isocyanate bonds strongly to bare/unreleased metal.",
    moldCompat="pu_hot", confidence="medium")

TDI_E = dict(PU_HOT, family="pu_tdi_ether", familyLabel="Cast PU - TDI polyether (MBOCA cured)",
    materialTempC=R(70, 80, 90), moldTempC=R(90, 100, 110),
    postCure=pc(True, 100, 16, "16 h at 100 C typical for TDI/MBOCA systems; full properties after ~7 days at room temperature."),
    hazards="MBOCA (MOCA) is a suspected human carcinogen; molten curative at 110-120 C; TDI vapour. Use ventilation, gloves, low-free (LF) prepolymers where possible.",
    sources=["S19", "S20", "S3", "K-PU"])
add(TDI_E, id="pu-tdi-ether-70a", name="TDI-polyether / MBOCA 70A", hardness="70A",
    shrinkPct=R(1.2, 1.7, 2.2), viscosityCp=R(1500, 2500, 4000), potLifeMin=R(8, 12, 20),
    demoldMin=R(45, 60, 90), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.05, 1.06, 1.08),
    draftDeg={"min": 0.5, "typ": 1.0}, confidence="low",
    notes="Low-NCO prepolymer (about 2.5-3% NCO): more viscous, long pot life, slow green strength.")
add(TDI_E, id="pu-tdi-ether-80a", name="TDI-polyether / MBOCA 80A", hardness="80A",
    shrinkPct=R(1.2, 1.6, 2.0), viscosityCp=R(1000, 1800, 3000), potLifeMin=R(6, 10, 15),
    demoldMin=R(30, 45, 60), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.06, 1.07, 1.09))
add(TDI_E, id="pu-tdi-ether-90a", name="TDI-polyether / MBOCA 90A", hardness="90A",
    shrinkPct=R(1.0, 1.5, 2.0), viscosityCp=R(600, 1200, 2000), potLifeMin=R(4, 6, 10),
    demoldMin=R(20, 30, 45), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.08, 1.10, 1.11),
    notes="Most common general-purpose cast elastomer (Adiprene L-100 / LF 900 class).")
add(TDI_E, id="pu-tdi-ether-95a", name="TDI-polyether / MBOCA 95A", hardness="95A",
    shrinkPct=R(1.0, 1.4, 1.8), viscosityCp=R(500, 900, 1500), potLifeMin=R(3, 5, 7),
    demoldMin=R(20, 25, 40), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.10, 1.12, 1.13))
add(TDI_E, id="pu-tdi-ether-60d", name="TDI-polyether / MBOCA 60D", hardness="60D",
    shrinkPct=R(0.9, 1.3, 1.7), viscosityCp=R(400, 700, 1200), potLifeMin=R(2, 3, 5),
    demoldMin=R(15, 20, 30), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.12, 1.15, 1.17),
    draftDeg={"min": 1.0, "typ": 1.5}, notes="High-NCO prepolymer: short pot life, high exotherm in thick sections.")
add(TDI_E, id="pu-tdi-ether-75d", name="TDI-polyether / MBOCA 75D", hardness="75D",
    shrinkPct=R(0.8, 1.2, 1.6), viscosityCp=R(300, 600, 1000), potLifeMin=R(1, 2, 3),
    demoldMin=R(10, 15, 25), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.15, 1.18, 1.20),
    draftDeg={"min": 1.0, "typ": 2.0}, confidence="low",
    notes="About 8-9% NCO (e.g. Adiprene LF 750D class). Very short pot life: mold must be closed within 1-2 min; machine or meter-mix.")

MDI_E = dict(PU_HOT, family="pu_mdi_ether", familyLabel="Cast PU - MDI polyether (BDO cured)",
    materialTempC=R(60, 70, 85), moldTempC=R(90, 105, 120),
    postCure=pc(True, 100, 16, "16-24 h at 100-110 C."),
    hazards="MDI vapour at process temperature; BDO curative low hazard.",
    sources=["S19", "S20", "S3", "K-PU"])
add(MDI_E, id="pu-mdi-ether-60a", name="MDI-polyether 60A (triol / plasticised)", hardness="60A",
    shrinkPct=R(1.3, 1.8, 2.3), viscosityCp=R(1000, 2000, 3500), potLifeMin=R(5, 8, 15),
    demoldMin=R(45, 60, 120), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.03, 1.05, 1.08),
    draftDeg={"min": 0.5, "typ": 1.0}, confidence="low",
    notes="Soft MDI grades use triol/polyol curative blends or plasticiser; low tear strength at demold - generous radii, avoid deep undercuts.")
add(MDI_E, id="pu-mdi-ether-70a", name="MDI-polyether 70A", hardness="70A",
    shrinkPct=R(1.2, 1.7, 2.2), viscosityCp=R(1000, 1800, 3000), potLifeMin=R(4, 7, 12),
    demoldMin=R(40, 60, 90), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.04, 1.06, 1.08))
add(MDI_E, id="pu-mdi-ether-80a", name="MDI-polyether 80A", hardness="80A",
    shrinkPct=R(1.1, 1.6, 2.0), viscosityCp=R(800, 1500, 2500), potLifeMin=R(4, 6, 10),
    demoldMin=R(30, 45, 60), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.05, 1.07, 1.09))
add(MDI_E, id="pu-mdi-ether-90a", name="MDI-polyether 90A", hardness="90A",
    shrinkPct=R(1.0, 1.5, 2.0), viscosityCp=R(700, 1200, 2000), potLifeMin=R(3, 5, 8),
    demoldMin=R(25, 40, 60), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.08, 1.10, 1.12))
add(MDI_E, id="pu-mdi-ether-95a", name="MDI-polyether 95A", hardness="95A",
    shrinkPct=R(0.9, 1.4, 1.8), viscosityCp=R(600, 1000, 1800), potLifeMin=R(2, 4, 6),
    demoldMin=R(20, 30, 45), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.10, 1.12, 1.14))
add(MDI_E, id="pu-mdi-ether-60d", name="MDI-polyether 60D", hardness="60D",
    shrinkPct=R(0.8, 1.2, 1.6), viscosityCp=R(500, 900, 1500), potLifeMin=R(2, 3, 5),
    demoldMin=R(15, 25, 40), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.12, 1.15, 1.18),
    draftDeg={"min": 1.0, "typ": 1.5})

MDI_S = dict(PU_HOT, family="pu_mdi_ester", familyLabel="Cast PU - MDI polyester (BDO cured)",
    viscosityClass="liquid_med", flash="liquid_med", materialTempC=R(70, 80, 95), moldTempC=R(100, 110, 120),
    postCure=pc(True, 110, 16, "16-24 h at 100-110 C."),
    moistureNotes="Isocyanate + water -> CO2 bubbles. Cured polyester PU is also hydrolysis-sensitive in service (hot water, humidity).",
    hazards="MDI vapour at process temperature.", sources=["S19", "S20", "K-PU"])
add(MDI_S, id="pu-mdi-ester-70a", name="MDI-polyester 70A", hardness="70A",
    shrinkPct=R(1.1, 1.6, 2.1), viscosityCp=R(2000, 3500, 6000), potLifeMin=R(4, 6, 10),
    demoldMin=R(40, 60, 90), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.18, 1.20, 1.22), confidence="low")
add(MDI_S, id="pu-mdi-ester-80a", name="MDI-polyester 80A", hardness="80A",
    shrinkPct=R(1.0, 1.5, 2.0), viscosityCp=R(1500, 3000, 5000), potLifeMin=R(3, 5, 8),
    demoldMin=R(30, 45, 60), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.19, 1.21, 1.23))
add(MDI_S, id="pu-mdi-ester-90a", name="MDI-polyester 90A", hardness="90A",
    shrinkPct=R(1.0, 1.4, 1.9), viscosityCp=R(1200, 2500, 4000), potLifeMin=R(2, 4, 6),
    demoldMin=R(25, 40, 60), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.21, 1.23, 1.25))
add(MDI_S, id="pu-mdi-ester-95a", name="MDI-polyester 95A", hardness="95A",
    shrinkPct=R(0.9, 1.3, 1.8), viscosityCp=R(1000, 2000, 3500), potLifeMin=R(2, 3, 5),
    demoldMin=R(20, 30, 45), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.22, 1.24, 1.26))

TDI_S = dict(PU_HOT, family="pu_tdi_ester", familyLabel="Cast PU - TDI polyester hot-cast (MBOCA cured)",
    viscosityClass="liquid_med", flash="liquid_med", materialTempC=R(80, 90, 100), moldTempC=R(95, 105, 115),
    postCure=pc(True, 100, 16, "16 h at 100 C."),
    moistureNotes="Isocyanate + water -> CO2 bubbles; cured part hydrolysis-sensitive.",
    hazards="MBOCA suspected carcinogen; TDI vapour.", sources=["S19", "S20", "K-PU"])
add(TDI_S, id="pu-tdi-ester-80a", name="TDI-polyester / MBOCA 80A", hardness="80A",
    shrinkPct=R(1.1, 1.5, 2.0), viscosityCp=R(2500, 4000, 6000), potLifeMin=R(5, 8, 12),
    demoldMin=R(30, 45, 60), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.22, 1.24, 1.26), confidence="low")
add(TDI_S, id="pu-tdi-ester-90a", name="TDI-polyester / MBOCA 90A", hardness="90A",
    shrinkPct=R(1.0, 1.4, 1.9), viscosityCp=R(2000, 3000, 5000), potLifeMin=R(3, 5, 8),
    demoldMin=R(20, 30, 45), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.24, 1.26, 1.28), confidence="low")
add(TDI_S, id="pu-tdi-ester-95a", name="TDI-polyester / MBOCA 95A", hardness="95A",
    shrinkPct=R(0.9, 1.3, 1.8), viscosityCp=R(1500, 2500, 4000), potLifeMin=R(2, 4, 6),
    demoldMin=R(20, 25, 40), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.25, 1.27, 1.29), confidence="low")

PPDI = dict(PU_HOT, family="pu_ppdi", familyLabel="Cast PU - PPDI / high-performance",
    viscosityClass="liquid_med", flash="liquid_med", materialTempC=R(80, 90, 100), moldTempC=R(110, 120, 127),
    postCure=pc(True, 120, 24, "16-24 h at 110-127 C; PPDI needs full post-cure for dynamic properties."),
    hazards="PPDI prepolymer handled hot; very short pot life (meter-mix machine recommended).",
    sources=["S19", "K-PU"], confidence="low")
add(PPDI, id="pu-ppdi-90a", name="PPDI-polyether/polycarbonate 90A (BDO or HQEE cured)", hardness="90A",
    shrinkPct=R(1.0, 1.5, 2.0), viscosityCp=R(1000, 1800, 3000), potLifeMin=R(1, 2, 4),
    demoldMin=R(30, 45, 60), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.08, 1.10, 1.13),
    notes="Low heat build-up for wheels/rollers; mold must be fully closed within ~1-2 min.")
add(PPDI, id="pu-ppdi-95a", name="PPDI-polyether 95A / 50D", hardness="95A",
    shrinkPct=R(0.9, 1.4, 1.9), viscosityCp=R(800, 1500, 2500), potLifeMin=R(1, 2, 3),
    demoldMin=R(30, 40, 60), cureBasis="Demold time at mold temperature for 6-12 mm section", sg=R(1.10, 1.13, 1.16))

RT = dict(group="polyurethane", processType="liquid_cast_rt", shrinkBasis="rt_mold_rt_part",
    viscosityClass="liquid_med", viscosityTempC=23, flash="liquid_med",
    flashAllowancePct=R(3, 5, 10), overflowPctOfPartVolume=R(3, 5, 8),
    moldingPressurePsi=R(0, 0, 60),
    pressureNotes="Gravity pour; optional pressure-pot at 40-60 psi (air) to collapse bubbles. Heated press not required.",
    ventingSensitivity="high", ventingNotes="Vacuum degas mixed material; vent all high points.",
    releaseAgent="Wax or silicone-based release (e.g. Mann Ease Release 200) on metal/epoxy molds; silicone molds need none.",
    moistureSensitivity="very_high", moistureNotes="Isocyanate + water -> bubbles; keep containers N2 blanketed; molds dry.",
    corrosiveness="none", moldCompat="rt_cast", family="pu_rt_cast", familyLabel="Cast PU - room-temperature castable",
    sources=["S18", "K-PU"])
add(RT, id="pu-rt-elastomer-60-90a", name="RT-cure urethane elastomer 60-90A (non-MBOCA)", hardness="80A",
    shrinkPct=R(0.1, 0.3, 0.6), shrinkNotes="BJB M-3190 (90A) 0.0010 in/in and TC-680 (80A) 0.006 in/in per ASTM D2566.",
    viscosityCp=R(800, 2000, 4000), potLifeMin=R(5, 15, 30), moldTempC=R(20, 25, 65),
    demoldMin=R(120, 960, 1440), cureBasis="16-24 h at RT, or 2-4 h at 60-65 C",
    postCure=pc(False, 65, 16, "Optional 16 h at 60-70 C (BJB uses 16 h at 160 F for test specimens)."),
    sg=R(1.03, 1.06, 1.10), draftDeg={"min": 0.5, "typ": 1.0})
add(RT, id="pu-rt-rigid-casting", name="RT rigid urethane casting resin 70-80D", hardness="75D",
    viscosityClass="liquid_low", flash="liquid_low",
    shrinkPct=R(0.2, 0.5, 1.0), viscosityCp=R(70, 150, 500), potLifeMin=R(2, 3, 7), moldTempC=R(20, 25, 50),
    demoldMin=R(10, 30, 120), cureBasis="Fast RT systems demold in 10-30 min; full cure 24 h",
    postCure=pc(False, 65, 4, "Optional 4-6 h at 60-70 C for heat resistance."),
    sg=R(1.04, 1.07, 1.15), draftDeg={"min": 1.0, "typ": 1.5},
    notes="Very low viscosity: flashes at any gap >0.02 mm; exotherm thick sections (>25 mm) can crack or yellow.", confidence="low")

RIG = dict(group="polyurethane", family="pu_rigid_structural", familyLabel="Rigid / structural PU",
    sources=["K-PU"], confidence="low", corrosiveness="none",
    moistureSensitivity="very_high", moistureNotes="Water reacts with isocyanate (blowing / bubbles).")
add(RIG, id="pu-rigid-hotcast-80d", name="Hot-cast rigid solid PU 80-85D (high-NCO TDI/MDI)", hardness="83D",
    processType="liquid_cast_hot", shrinkPct=R(0.6, 1.0, 1.4),
    shrinkNotes="Higher modulus, lower CTE than elastomers; exotherm-driven shrink in thick sections.",
    viscosityClass="liquid_low", viscosityCp=R(300, 600, 1200), viscosityTempC=70, materialTempC=R(50, 70, 80),
    potLifeMin=R(1, 2, 4), moldTempC=R(70, 90, 110), demoldMin=R(10, 20, 40), cureBasis="Demold at mold temperature, 6-12 mm",
    postCure=pc(True, 100, 16), sg=R(1.18, 1.20, 1.24), moldingPressurePsi=R(0, 50, 150),
    draftDeg={"min": 1.0, "typ": 2.0}, flash="liquid_low", flashAllowancePct=R(3, 5, 8), overflowPctOfPartVolume=R(3, 5, 8),
    ventingSensitivity="very_high", releaseAgent="Semi-permanent or wax release; PTFE coating.", moldCompat="pu_hot")
add(RIG, id="pu-structural-foam", name="Structural rigid PU foam / SRIM-type (density 0.5-1.1)", hardness="70D",
    processType="foam_pour", shrinkPct=R(0.2, 0.5, 0.8), shrinkBasis="cold_mold_cold_part_steel_ref",
    viscosityClass="liquid_low", viscosityCp=R(200, 500, 1500), viscosityTempC=25, materialTempC=R(20, 25, 35),
    potLifeMin=R(0.3, 0.5, 1.5), moldTempC=R(40, 55, 70), demoldMin=R(3, 6, 15), cureBasis="Demold at mold temperature",
    postCure=None, sg=R(0.5, 0.8, 1.1), moldingPressurePsi=R(15, 50, 120),
    pressureNotes="Foaming generates 1-8 bar internal pressure; the press/clamp must hold this (projected area x internal pressure).",
    draftDeg={"min": 1.0, "typ": 2.0}, flash="foam", flashAllowancePct=R(5, 10, 15), overflowPctOfPartVolume=R(0, 2, 5),
    ventingSensitivity="very_high", ventingNotes="Vent holes 1-2 mm at last-fill points; overpack 10-15%.",
    releaseAgent="Solvent or water-based wax release each shot.", moldCompat="foam")

FOAM = dict(group="polyurethane", family="pu_foam", familyLabel="PU foams (pour-in-place, clamped)",
    processType="foam_pour", shrinkBasis="cold_mold_cold_part_steel_ref",
    viscosityClass="liquid_low", viscosityTempC=25, materialTempC=R(20, 25, 30),
    flash="foam", overflowPctOfPartVolume=R(0, 1, 3), moldingPressurePsi=R(5, 15, 45),
    pressureNotes="Internal foam pressure 0.3-3 bar; clamp only. A heat press is a convenient clamp but not needed for pressure.",
    ventingSensitivity="very_high", ventingNotes="Vent holes 1-3 mm (flexible foam) at last-fill points; air traps cause voids and skin defects.",
    releaseAgent="Wax-based solvent or water-borne foam release every shot.",
    moistureSensitivity="high", moistureNotes="Water is often the blowing agent; ambient humidity and mold moisture change density.",
    corrosiveness="none", moldCompat="foam", sources=["K-PU"], confidence="low", postCure=None)
add(FOAM, id="pu-foam-flexible-molded", name="Flexible molded foam (HR cold-cure)", hardness=None,
    shrinkPct=R(0.0, 0.5, 1.5), viscosityCp=R(500, 1500, 3000), potLifeMin=R(0.1, 0.15, 0.3),
    moldTempC=R(45, 55, 65), demoldMin=R(3, 5, 8), cureBasis="Demold; crush to open cells immediately",
    sg=R(0.035, 0.05, 0.08), draftDeg={"min": 0.0, "typ": 0.5}, flashAllowancePct=R(5, 10, 20))
add(FOAM, id="pu-foam-integral-skin", name="Integral-skin PU foam", hardness="60A",
    shrinkPct=R(0.5, 1.0, 1.5), viscosityCp=R(500, 1200, 2500), potLifeMin=R(0.1, 0.2, 0.4),
    moldTempC=R(40, 50, 60), demoldMin=R(3, 5, 10), cureBasis="Demold at mold temperature",
    sg=R(0.3, 0.5, 0.7), draftDeg={"min": 1.0, "typ": 2.0}, flashAllowancePct=R(10, 15, 25))
add(FOAM, id="pu-foam-microcellular", name="Microcellular PU elastomer (MDI-ester, sole/bumper type)", hardness="55A",
    shrinkPct=R(0.5, 1.0, 1.5), viscosityCp=R(800, 1500, 3000), potLifeMin=R(0.1, 0.2, 0.3),
    moldTempC=R(45, 50, 60), demoldMin=R(3, 4, 6), cureBasis="Demold at mold temperature",
    sg=R(0.4, 0.6, 1.0), draftDeg={"min": 1.0, "typ": 1.5}, flashAllowancePct=R(5, 10, 15))

add(dict(group="polyurethane", family="pu_millable", familyLabel="Millable PU rubber (AU/EU)"),
    id="pu-millable-70a", name="Millable polyurethane rubber (e.g. Millathane) 70A", hardness="70A",
    processType="rubber_compression", shrinkPct=R(1.5, 2.0, 2.5), shrinkBasis="cold_mold_cold_part_steel_ref",
    shrinkNotes="TSE: linear shrinkage 1.5-2.5% depending on loading.",
    viscosityClass="rubber", mooney={"min": 35, "typ": 45, "max": 60, "test": "ML1+4@100C"}, bulkFactor=R(1.0, 1.0, 1.0),
    materialTempC=R(20, 40, 70), moldTempC=R(140, 160, 182),
    demoldMin=R(2, 15, 60), cureBasis="Sulfur 140-165 C 7-60 min; peroxide 149-182 C 2-20 min (TSE). Halve time per +10 C.",
    postCure=None, sg=R(1.10, 1.20, 1.30), moldingPressurePsi=R(700, 1500, 2500),
    draftDeg={"min": 0.5, "typ": 1.0}, flash="rubber", flashAllowancePct=R(5, 8, 10), overflowPctOfPartVolume=R(8, 10, 12),
    ventingSensitivity="medium", ventingNotes="Bump (breathe) the press 1-2 times early in the cycle.",
    releaseAgent="Water-based or silicone release sparingly (TSE: overuse causes poor flow and knit lines).",
    moistureSensitivity="medium", moistureNotes="Polyester-based gums hydrolyse; store compounds dry.",
    corrosiveness="low", moldCompat="rubber", sources=["S2"], confidence="medium")

# ======================= EPOXY =======================
add(dict(group="thermoset_resin", family="epoxy_casting", familyLabel="Epoxy - casting"),
    id="epoxy-cast-anhydride", name="Epoxy casting, heat-cure anhydride (unfilled)", processType="liquid_cast_hot",
    shrinkPct=R(0.8, 1.2, 2.0), viscosityClass="liquid_low", viscosityCp=R(300, 600, 1500), viscosityTempC=70,
    materialTempC=R(50, 60, 80), potLifeMin=R(60, 240, 480), moldTempC=R(80, 100, 140),
    demoldMin=R(120, 240, 480), cureBasis="Gel at 80-100 C then cure 120-140 C; staged to limit exotherm",
    postCure=pc(True, 140, 8, "4-16 h at 130-150 C."), sg=R(1.15, 1.18, 1.22),
    moldingPressurePsi=R(0, 15, 75), pressureNotes="Gravity or low pressure; vacuum casting preferred.",
    flash="liquid_low", flashAllowancePct=R(3, 5, 8), overflowPctOfPartVolume=R(3, 5, 8),
    ventingSensitivity="very_high", ventingNotes="Vacuum degas and vacuum cast; vent all high points.",
    releaseAgent="Semi-permanent (Frekote, Chemlease) or PTFE/silicone release; epoxy bonds aggressively to metal.",
    moistureSensitivity="medium", moistureNotes="Anhydride hardeners hydrolyse; keep sealed.",
    corrosiveness="none", moldCompat="pu_hot", draftDeg={"min": 1.0, "typ": 1.5},
    hazards="Exotherm in thick sections (>25 mm) can run away; skin sensitiser.", sources=["K-EP"], confidence="low")
add(dict(group="thermoset_resin", family="epoxy_casting", familyLabel="Epoxy - casting"),
    id="epoxy-cast-amine-rt", name="Epoxy casting, room-temperature amine cure", processType="liquid_cast_rt",
    shrinkPct=R(0.3, 0.6, 1.5), shrinkBasis="rt_mold_rt_part", viscosityClass="liquid_med", viscosityCp=R(500, 1200, 3000), viscosityTempC=23,
    materialTempC=R(20, 25, 30), potLifeMin=R(20, 45, 120), moldTempC=R(20, 25, 60),
    demoldMin=R(240, 960, 1440), cureBasis="16-24 h at RT; 2-4 h at 60 C",
    postCure=pc(False, 70, 4, "2-4 h at 60-80 C for full Tg."), sg=R(1.10, 1.15, 1.20),
    moldingPressurePsi=R(0, 0, 60), pressureNotes="Gravity or pressure pot.",
    flash="liquid_med", flashAllowancePct=R(3, 5, 10), overflowPctOfPartVolume=R(3, 5, 8),
    ventingSensitivity="high", releaseAgent="Wax + PVA, or semi-permanent release.",
    moistureSensitivity="medium", moistureNotes="Amine blush with humidity/CO2 on exposed surfaces.",
    corrosiveness="none", moldCompat="rt_cast", draftDeg={"min": 1.0, "typ": 1.5}, sources=["K-EP"], confidence="low")
add(dict(group="thermoset_resin", family="epoxy_filled", familyLabel="Epoxy - filled / tooling"),
    id="epoxy-tooling-al-filled", name="Aluminium/mineral-filled tooling epoxy (cast)", processType="liquid_cast_rt",
    shrinkPct=R(0.02, 0.1, 0.3), shrinkBasis="rt_mold_rt_part", viscosityClass="liquid_high",
    viscosityCp=R(10000, 40000, 100000), viscosityTempC=23, materialTempC=R(20, 25, 30), potLifeMin=R(30, 60, 180),
    moldTempC=R(20, 25, 60), demoldMin=R(720, 1440, 2880), cureBasis="12-24 h at RT, staged post-cure",
    postCure=pc(True, 80, 4, "Stepped post-cure to service temperature (e.g. 2 h 60 C + 4 h 80 C + 2 h 120 C)."),
    sg=R(1.5, 1.7, 2.2), moldingPressurePsi=R(0, 0, 30), flash="liquid_high", flashAllowancePct=R(3, 5, 8),
    overflowPctOfPartVolume=R(3, 5, 8), ventingSensitivity="high", releaseAgent="Wax + PVA film.",
    moistureSensitivity="low", corrosiveness="none", moldCompat="rt_cast", draftDeg={"min": 1.0, "typ": 2.0},
    sources=["K-EP"], confidence="low")
add(dict(group="thermoset_resin", family="epoxy_filled", familyLabel="Epoxy - filled / tooling"),
    id="epoxy-silica-apg", name="Silica-filled anhydride epoxy (electrical casting / APG)", processType="liquid_cast_hot",
    shrinkPct=R(0.2, 0.35, 0.6), viscosityClass="liquid_high", viscosityCp=R(5000, 15000, 30000), viscosityTempC=60,
    materialTempC=R(40, 60, 70), potLifeMin=R(120, 480, 1440), moldTempC=R(130, 145, 160),
    demoldMin=R(10, 20, 40), cureBasis="APG (pressure gelation) in hot mold; longer for gravity cast",
    postCure=pc(True, 135, 8, "4-10 h at 130-140 C."), sg=R(1.7, 1.8, 1.9),
    moldingPressurePsi=R(15, 45, 75), pressureNotes="APG feeds resin at 1-5 bar while it gels from the hot wall inward to compensate shrink.",
    flash="liquid_high", flashAllowancePct=R(2, 4, 6), overflowPctOfPartVolume=R(3, 5, 8),
    ventingSensitivity="high", releaseAgent="Semi-permanent release; silica abrades soft molds.",
    moistureSensitivity="medium", corrosiveness="none", moldCompat="compound_abrasive", draftDeg={"min": 1.0, "typ": 1.5},
    sources=["K-EP"], confidence="low")
EMC = dict(group="thermoset_compound", family="epoxy_emc", familyLabel="Epoxy molding compound (EMC)",
    processType="compound_compression", viscosityClass="compound", viscosityCp=None,
    flash="compound", flashAllowancePct=R(2, 3, 5), overflowPctOfPartVolume=R(0, 2, 5),
    ventingSensitivity="high", ventingNotes="Vents 0.01-0.025 mm (EMC flashes readily at low viscosity minimum).",
    releaseAgent="Internal wax; mold-conditioning (release) compound shots after cleaning.",
    moistureSensitivity="high", moistureNotes="Stored at <=5 C; warm sealed bag to room temperature (>=4-16 h) before opening or voids/delamination.",
    corrosiveness="low", moldCompat="compound_abrasive", sources=["S17", "K-EP"])
add(EMC, id="epoxy-emc-silica", name="EMC, silica-filled (encapsulation grade)", shrinkPct=R(0.1, 0.3, 0.5),
    bulkFactor=R(1.0, 1.1, 1.8), materialTempC=R(20, 25, 90), moldTempC=R(165, 175, 185),
    demoldMin=R(1, 2, 3), cureBasis="In-mold cure 60-180 s at 175 C",
    postCure=pc(True, 175, 6, "4-8 h at 175 C (post-mold cure)."), sg=R(1.80, 1.95, 2.05),
    moldingPressurePsi=R(500, 1000, 1500), pressureNotes="Transfer grades 40-70 kgf/cm2 (570-1000 psi); compression similar.",
    draftDeg={"min": 1.0, "typ": 2.0}, confidence="medium")
add(EMC, id="epoxy-emc-glass", name="Glass-filled epoxy molding compound (general/MIL)", shrinkPct=R(0.1, 0.25, 0.5),
    bulkFactor=R(2.0, 3.0, 6.0), materialTempC=R(20, 80, 100), moldTempC=R(150, 165, 180),
    demoldMin=R(2, 3, 5), cureBasis="In-mold cure for 3-6 mm wall",
    postCure=pc(False, 160, 4, "Optional 2-4 h at 150-175 C for max Tg."), sg=R(1.80, 1.90, 2.00),
    moldingPressurePsi=R(500, 1500, 3000), draftDeg={"min": 1.0, "typ": 1.5}, confidence="low",
    moistureSensitivity="medium")

# ======================= UNSATURATED POLYESTER / VINYL ESTER =======================
BMC = dict(group="thermoset_compound", processType="compound_compression", viscosityClass="compound",
    flash="sheet_bulk", flashAllowancePct=R(1, 2, 3), overflowPctOfPartVolume=R(0, 0, 2),
    ventingSensitivity="medium", ventingNotes="Place charge so flow pushes air to shear edge; vacuum assist for thick/Class-A parts.",
    releaseAgent="Internal (zinc stearate) - external release normally not used.",
    moistureSensitivity="low", corrosiveness="low", corrosionNotes="Styrene volatiles; not corrosive.",
    moldCompat="smc", draftDeg={"min": 1.0, "typ": 1.5}, postCure=None, materialTempC=R(20, 25, 30),
    hazards="Styrene vapour; glass fibre dust on deflashing.")
add(BMC, family="up_bmc", familyLabel="Unsaturated polyester - BMC", id="up-bmc-standard", name="Polyester BMC, standard (glass + CaCO3)",
    shrinkPct=R(0.2, 0.3, 0.5), bulkFactor=R(1.0, 1.1, 1.3), moldTempC=R(140, 150, 165),
    demoldMin=R(1, 2, 5), cureBasis="About 30-40 s per mm wall at 150 C", sg=R(1.80, 1.90, 2.05),
    moldingPressurePsi=R(500, 1200, 2000), sources=["S7", "S8", "K-TS"], moldCompat="compound_abrasive")
add(BMC, family="up_bmc", familyLabel="Unsaturated polyester - BMC", id="up-bmc-lowshrink", name="Polyester BMC, low-shrink / low-profile",
    shrinkPct=R(0.0, 0.05, 0.2), bulkFactor=R(1.0, 1.1, 1.3), moldTempC=R(138, 150, 165),
    demoldMin=R(1, 2, 5), cureBasis="About 30-40 s per mm wall at 150 C", sg=R(1.80, 1.90, 2.05),
    moldingPressurePsi=R(500, 1200, 2000), sources=["S7", "K-TS"], moldCompat="compound_abrasive")
add(BMC, family="up_smc", familyLabel="Unsaturated polyester - SMC", id="up-smc-standard", name="Polyester SMC, standard (25-30% glass)",
    shrinkPct=R(0.1, 0.2, 0.4), bulkFactor=R(1.0, 1.0, 1.1), moldTempC=R(140, 150, 160),
    demoldMin=R(1, 2, 3), cureBasis="1-3 min at 150 C (30-150 s typical)", sg=R(1.70, 1.85, 1.95),
    moldingPressurePsi=R(500, 1000, 2000), pressureNotes="500-1500 psi on projected area; Class-A and deep draws up to ~2000 psi.",
    sources=["S7", "S8", "K-TS"], shrinkNotes="Flow-direction anisotropy; charge covers 40-70% of tool area.")
add(BMC, family="up_smc", familyLabel="Unsaturated polyester - SMC", id="up-smc-lp-classa", name="Polyester SMC, low-profile Class A",
    shrinkPct=R(0.0, 0.03, 0.08), bulkFactor=R(1.0, 1.0, 1.1), moldTempC=R(140, 150, 160),
    demoldMin=R(1, 2, 3), cureBasis="1-3 min at 150 C", sg=R(1.60, 1.85, 1.95),
    moldingPressurePsi=R(800, 1200, 2000), sources=["S8", "K-TS"],
    notes="Class-A surface needs polished, chrome-plated hardened steel and +/-3 C cavity temperature uniformity.")
add(dict(group="thermoset_resin", family="up_casting", familyLabel="Unsaturated polyester - casting"),
    id="up-cast-clear", name="Polyester casting resin (unfilled, MEKP cure)", processType="liquid_cast_rt",
    shrinkPct=R(1.5, 2.0, 3.0), shrinkBasis="rt_mold_rt_part", shrinkNotes="Volumetric cure shrink 7-9%; linear about 2%.",
    viscosityClass="liquid_low", viscosityCp=R(300, 500, 1000), viscosityTempC=23, materialTempC=R(20, 23, 30),
    potLifeMin=R(10, 20, 40), moldTempC=R(20, 25, 60), demoldMin=R(60, 120, 480), cureBasis="RT cure",
    postCure=pc(False, 70, 2, "2-3 h at 60-80 C."), sg=R(1.12, 1.16, 1.20), moldingPressurePsi=R(0, 0, 30),
    flash="liquid_low", flashAllowancePct=R(3, 5, 8), overflowPctOfPartVolume=R(3, 5, 8),
    ventingSensitivity="high", releaseAgent="Wax + PVA.", moistureSensitivity="low",
    corrosiveness="none", moldCompat="rt_cast", draftDeg={"min": 1.0, "typ": 2.0},
    hazards="Styrene; MEKP oxidiser. Thick sections crack from exotherm.", sources=["K-TS"], confidence="low")
add(dict(group="thermoset_resin", family="up_casting", familyLabel="Unsaturated polyester - casting"),
    id="up-cast-filled", name="Filled polyester casting (polymer concrete / cultured marble)", processType="liquid_cast_rt",
    shrinkPct=R(0.1, 0.25, 0.5), shrinkBasis="rt_mold_rt_part", viscosityClass="liquid_high", viscosityCp=R(20000, 50000, 150000),
    viscosityTempC=23, materialTempC=R(20, 23, 30), potLifeMin=R(15, 30, 60), moldTempC=R(20, 30, 60),
    demoldMin=R(60, 180, 720), cureBasis="RT/warm cure", postCure=None, sg=R(1.7, 2.0, 2.3),
    moldingPressurePsi=R(0, 0, 30), flash="liquid_high", flashAllowancePct=R(3, 5, 8), overflowPctOfPartVolume=R(3, 5, 8),
    ventingSensitivity="high", ventingNotes="Vibrate to release air.", releaseAgent="Wax + PVA or gelcoat.",
    moistureSensitivity="low", corrosiveness="none", moldCompat="rt_cast", draftDeg={"min": 1.0, "typ": 2.0},
    sources=["K-TS"], confidence="low")
add(BMC, family="vinylester", familyLabel="Vinyl ester", id="ve-bmc", name="Vinyl ester BMC (glass + mineral)",
    shrinkPct=R(0.0, 0.1, 0.22), bulkFactor=R(1.0, 1.1, 1.3), moldTempC=R(138, 150, 166),
    demoldMin=R(1, 2, 5), cureBasis="About 30-40 s per mm wall", sg=R(1.85, 1.95, 2.05),
    moldingPressurePsi=R(500, 1200, 2000), moldCompat="compound_abrasive",
    shrinkNotes="LyondellBasell 695-3583: 0.0000-0.0022 in/in (ASTM D955), compression molded 138-165 C.", sources=["S7"])
add(BMC, family="vinylester", familyLabel="Vinyl ester", id="ve-smc", name="Vinyl ester SMC",
    shrinkPct=R(0.05, 0.15, 0.35), bulkFactor=R(1.0, 1.0, 1.1), moldTempC=R(140, 150, 160),
    demoldMin=R(1, 2, 4), cureBasis="1-4 min at 150 C", sg=R(1.70, 1.85, 1.95),
    moldingPressurePsi=R(500, 800, 1000), pressureNotes="LyondellBasell Premi-Glas 1281-47VE: 300 F, 500-1000 psi in matched metal molds.",
    sources=["S7"], shrinkNotes="Premi-Glas 1103 (urethane/VE hybrid): 0.00322 in/in (RT mold / RT part).")
add(dict(group="thermoset_resin", family="vinylester", familyLabel="Vinyl ester"),
    id="ve-casting", name="Vinyl ester casting/laminating resin (unfilled)", processType="liquid_cast_rt",
    shrinkPct=R(1.2, 2.0, 3.0), shrinkBasis="rt_mold_rt_part", viscosityClass="liquid_low", viscosityCp=R(200, 400, 600),
    viscosityTempC=23, materialTempC=R(20, 23, 30), potLifeMin=R(15, 30, 60), moldTempC=R(20, 25, 60),
    demoldMin=R(120, 240, 720), cureBasis="RT cure (MEKP/CoNap)", postCure=pc(True, 90, 4, "2-4 h at 80-120 C for full chemical resistance."),
    sg=R(1.10, 1.13, 1.16), moldingPressurePsi=R(0, 0, 30), flash="liquid_low", flashAllowancePct=R(3, 5, 8),
    overflowPctOfPartVolume=R(3, 5, 8), ventingSensitivity="high", releaseAgent="Wax + PVA.", moistureSensitivity="low",
    corrosiveness="none", moldCompat="rt_cast", draftDeg={"min": 1.0, "typ": 2.0}, hazards="Styrene; MEKP.",
    sources=["K-TS"], confidence="low")

# ======================= PHENOLIC / AMINO / DAP =======================
TSC = dict(group="thermoset_compound", processType="compound_compression", viscosityClass="compound",
    flash="compound", flashAllowancePct=R(2, 4, 8), overflowPctOfPartVolume=R(0, 2, 5),
    ventingSensitivity="high", ventingNotes="Condensation cure releases water/ammonia/formaldehyde: 1-3 'breathe' (bump) openings early in cure; vents 0.025-0.05 mm.",
    releaseAgent="Internal (zinc/calcium stearate); occasional external silicone-free release on new/polished molds.",
    draftDeg={"min": 0.5, "typ": 1.0}, sources=["K-TS"])
PF = dict(TSC, family="phenolic", familyLabel="Phenolic molding compound",
    materialTempC=R(80, 90, 120), moldTempC=R(150, 165, 190),
    postCure=pc(False, 150, 8, "Optional stepped bake (e.g. 4-24 h ramp to 150-200 C) for heat-resistant grades to cut post-shrink."),
    moistureSensitivity="medium", moistureNotes="Preheat compound/preforms to 80-120 C (RF or oven) to drive volatiles and cut cure time.",
    corrosiveness="medium", corrosionNotes="Hexa-cured novolacs release ammonia (attacks copper/brass inserts) and water; chrome-plated hardened steel standard.",
    hazards="Phenol/formaldehyde/ammonia fumes at the press.")
add(PF, id="phenolic-gp-woodflour", name="Phenolic, general purpose (wood-flour filled)",
    shrinkPct=R(0.4, 0.6, 0.9), shrinkNotes="Bakelite PF datasheets: compression molding shrink 0.45-0.5% longitudinal.",
    bulkFactor=R(2.1, 2.8, 4.4), demoldMin=R(1, 2, 5), cureBasis="About 30-60 s per mm wall at 165 C (Bakelite test 1 min at 160 C)",
    sg=R(1.37, 1.40, 1.46), moldingPressurePsi=R(2000, 3000, 6000), pressureNotes="Bakelite: cavity pressure >15 MPa (>2175 psi).",
    moldCompat="compound_corrosive", sources=["S6", "K-TS"])
add(PF, id="phenolic-glass", name="Phenolic, glass-filled",
    shrinkPct=R(0.1, 0.2, 0.4), bulkFactor=R(2.0, 4.0, 10.0), demoldMin=R(1, 3, 6), cureBasis="About 30-60 s per mm wall at 165 C",
    sg=R(1.69, 1.85, 2.00), moldingPressurePsi=R(2000, 4000, 8000), moldCompat="compound_abrasive",
    sources=["S6", "K-TS"])
add(PF, id="phenolic-mineral", name="Phenolic, mineral-filled",
    shrinkPct=R(0.2, 0.4, 0.6), bulkFactor=R(2.0, 2.3, 2.6), demoldMin=R(1, 2, 5), cureBasis="About 30-60 s per mm wall at 165 C",
    sg=R(1.65, 1.80, 1.95), moldingPressurePsi=R(2000, 3500, 6000), moldCompat="compound_abrasive", sources=["S6", "K-TS"])
add(PF, id="phenolic-impact-cotton", name="Phenolic, impact (cotton flock / rag filled)",
    shrinkPct=R(0.3, 0.5, 0.9), bulkFactor=R(3.0, 5.0, 10.0), demoldMin=R(2, 3, 6), cureBasis="About 40-60 s per mm wall",
    sg=R(1.37, 1.40, 1.45), moldingPressurePsi=R(2000, 4000, 8000), moldCompat="compound_corrosive",
    notes="High bulk factor: deep loading well or preform; poor flow - not for thin ribs.", confidence="low")
AMINO = dict(TSC, materialTempC=R(20, 70, 100), postCure=None,
    moistureSensitivity="high", moistureNotes="Hygroscopic powders; store sealed, preheat (RF) before molding.",
    corrosiveness="high", corrosionNotes="Acid-catalysed formaldehyde resins corrode and stain unplated steel; hard chrome plating standard.",
    moldCompat="compound_corrosive", hazards="Formaldehyde release at the press.")
add(AMINO, family="melamine", familyLabel="Melamine-formaldehyde (MF)", id="mf-cellulose", name="Melamine-formaldehyde, cellulose-filled",
    shrinkPct=R(0.4, 0.7, 1.0), shrinkNotes="Plus post-shrink up to ~0.4% after molding (patent data).", bulkFactor=R(2.1, 2.5, 3.1),
    moldTempC=R(145, 160, 175), demoldMin=R(0.5, 2, 5), cureBasis="30 s-5 min (patent US4303561)", sg=R(1.47, 1.50, 1.52),
    moldingPressurePsi=R(2000, 4000, 8000), pressureNotes="Patent: 150-800 bar, preferred 200-500 bar (2900-7250 psi).",
    sources=["S9", "S10", "K-TS"])
add(AMINO, family="melamine", familyLabel="Melamine-formaldehyde (MF)", id="mf-glass-mineral", name="Melamine, glass/mineral-filled",
    shrinkPct=R(0.1, 0.3, 0.6), bulkFactor=R(2.0, 2.5, 4.0), moldTempC=R(150, 165, 175), demoldMin=R(1, 2, 5),
    cureBasis="About 40-60 s per mm wall", sg=R(1.80, 1.90, 2.00), moldingPressurePsi=R(2000, 4000, 8000),
    moldCompat="compound_abrasive", corrosiveness="medium", sources=["S9", "K-TS"], confidence="low")
add(AMINO, family="urea_formaldehyde", familyLabel="Urea-formaldehyde (UF)", id="uf-cellulose", name="Urea-formaldehyde, alpha-cellulose filled",
    shrinkPct=R(0.6, 0.8, 1.4), bulkFactor=R(2.2, 2.6, 3.0), moldTempC=R(135, 150, 175), demoldMin=R(1, 2, 4),
    cureBasis="About 30-50 s per mm wall", sg=R(1.47, 1.50, 1.52), moldingPressurePsi=R(2000, 4000, 8000),
    sources=["K-TS"], confidence="low", notes="ASTM D705 covers UF compression molding compounds.")
DAP = dict(TSC, family="dap", familyLabel="Diallyl phthalate (DAP)", materialTempC=R(20, 60, 100),
    moldTempC=R(140, 160, 190), postCure=pc(False, 155, 3, "Optional 2-4 h at 150-160 C for minimum post-shrink."),
    moistureSensitivity="low", corrosiveness="low", ventingSensitivity="medium",
    ventingNotes="Addition cure, few volatiles; vent 0.025 mm at last-fill points.")
add(DAP, id="dap-glass", name="DAP, short-glass-filled (MIL-M-14 SDG type)", shrinkPct=R(0.1, 0.3, 0.5),
    shrinkNotes="Raschig DAP 5562: 0.15-0.45% (ISO 2577).", bulkFactor=R(2.0, 4.0, 8.0),
    demoldMin=R(1, 3, 6), cureBasis="About 30-60 s per mm wall", sg=R(1.70, 1.85, 1.98),
    moldingPressurePsi=R(300, 1000, 3000), pressureNotes="MIL-M-14 grades molded at 300-380 F and 250-1000 psi (Professional Plastics).",
    moldCompat="compound_abrasive", sources=["S11", "K-TS"])
add(DAP, id="dap-mineral", name="DAP, mineral-filled (MIL-M-14 MDG type)", shrinkPct=R(0.4, 0.6, 0.8),
    bulkFactor=R(1.9, 2.0, 2.2), demoldMin=R(1, 2, 5), cureBasis="About 30-60 s per mm wall", sg=R(1.65, 1.75, 1.85),
    moldingPressurePsi=R(300, 1000, 3000), moldCompat="compound_abrasive", sources=["S11", "K-TS"], confidence="low")

# ======================= SILICONES =======================
HCR = dict(group="silicone", family="silicone_hcr", familyLabel="Silicone - HCR (high consistency, peroxide)",
    processType="rubber_compression", shrinkBasis="cold_mold_cold_part_steel_ref",
    shrinkNotes="2.0-5.0% (Clippard, Precision Associates); includes ~0.5-1% extra from post-cure volatiles loss. Softer = more shrink.",
    viscosityClass="rubber", mooney={"min": 150, "typ": 250, "max": 400, "test": "Williams plasticity (x100 mm)"},
    materialTempC=R(20, 23, 30), moldTempC=R(150, 170, 177), demoldMin=R(5, 10, 15),
    cureBasis="Dow: 10 min at 170 C; Shin-Etsu: 10 min at 177 C; Wacker: 15 min at 165 C (2 mm sheets). Add ~1 min per extra mm.",
    postCure=pc(True, 200, 4, "4 h at 200 C (Dow, Shin-Etsu, Wacker datasheets); needed for compression set and to remove peroxide by-products."),
    moldingPressurePsi=R(500, 1000, 2000), draftDeg={"min": 0.0, "typ": 0.5},
    flash="rubber", flashAllowancePct=R(5, 8, 10), overflowPctOfPartVolume=R(8, 10, 12),
    ventingSensitivity="medium", ventingNotes="Bump 1-2 times; silicone flows easily when hot so fine vents work.",
    releaseAgent="Usually none on polished/chrome steel; water-based soap or silicone-free release if sticking.",
    moistureSensitivity="low", corrosiveness="low",
    corrosionNotes="Dichlorobenzoyl-peroxide by-products are acidic (bloom, mild mold staining); dicumyl/DBPH grades benign.",
    moldCompat="rubber", sources=["S3", "S4", "S12"])
add(HCR, id="si-hcr-30a", name="Silicone HCR 30A", hardness="30A", shrinkPct=R(2.8, 3.5, 5.0), sg=R(1.08, 1.10, 1.14), confidence="low",
    notes="Low tear strength at demold; generous radii, avoid sharp undercuts.")
add(HCR, id="si-hcr-50a", name="Silicone HCR 50A", hardness="50A", shrinkPct=R(2.5, 3.0, 4.5), sg=R(1.12, 1.15, 1.41))
add(HCR, id="si-hcr-70a", name="Silicone HCR 70A", hardness="70A", shrinkPct=R(2.2, 2.7, 4.0), sg=R(1.18, 1.23, 1.35))
add(HCR, id="si-hcr-80a", name="Silicone HCR 80A", hardness="80A", shrinkPct=R(2.0, 2.5, 3.5), sg=R(1.22, 1.28, 1.40),
    draftDeg={"min": 0.5, "typ": 1.0})
LSR = dict(group="silicone", family="silicone_lsr", familyLabel="Silicone - LSR (platinum addition)",
    processType="liquid_compression", shrinkBasis="cold_mold_cold_part_steel_ref",
    shrinkNotes="Simtec: LSR expands in hot mold then shrinks 2.5-3% after demold; Liveo C6-550: 2.0% (flow).",
    viscosityClass="paste_lsr", viscosityTempC=23, materialTempC=R(15, 20, 25), potLifeMin=R(1440, 4320, 10080),
    moldTempC=R(150, 180, 210), demoldMin=R(1, 3, 10),
    cureBasis="Injection cures in seconds (about 4-5 s/mm at 180-200 C); thick press-molded parts 2-10 min.",
    postCure=pc(False, 200, 4, "Often not needed (Dow); 4 h at 200 C for food/medical low-volatiles."),
    moldingPressurePsi=R(100, 500, 1500), pressureNotes="Pressure only to fill and hold closed; parting-line force must keep gap <=0.005 mm.",
    draftDeg={"min": 0.0, "typ": 0.5}, flash="paste_lsr", flashAllowancePct=R(2, 3, 5), overflowPctOfPartVolume=R(3, 5, 8),
    ventingSensitivity="very_high", ventingNotes="Vacuum the cavity or vents 0.003-0.01 mm; LSR flashes into anything >=0.005 mm.",
    releaseAgent="None normally; polished or PVD/nickel-PTFE surfaces.",
    moistureSensitivity="none", corrosiveness="none",
    corrosionNotes="Platinum cure inhibited by sulfur, tin, amines, some rubber/PU release agents and latex: dedicate molds or clean thoroughly if previously used for sulfur rubber or PU.",
    moldCompat="rubber", sources=["S13"])
add(LSR, id="si-lsr-30a", name="LSR 30A", hardness="30A", shrinkPct=R(2.3, 3.0, 3.5),
    viscosityCp=R(50000, 150000, 400000), sg=R(1.08, 1.10, 1.12))
add(LSR, id="si-lsr-50a", name="LSR 50A", hardness="50A", shrinkPct=R(2.0, 2.8, 3.3),
    viscosityCp=R(100000, 300000, 700000), sg=R(1.10, 1.13, 1.15))
add(LSR, id="si-lsr-70a", name="LSR 70A", hardness="70A", shrinkPct=R(1.8, 2.5, 3.0),
    viscosityCp=R(200000, 500000, 1000000), sg=R(1.13, 1.18, 1.25))
add(HCR, family="silicone_fvmq", familyLabel="Silicone - fluorosilicone (FVMQ)", id="si-fvmq-60a", name="Fluorosilicone FVMQ 60A",
    hardness="60A", shrinkPct=R(2.8, 3.5, 4.7), shrinkNotes="Precision Associates: FVMQ 2.8-4.7%.",
    moldTempC=R(165, 170, 177), demoldMin=R(5, 10, 15), cureBasis="10 min at 170 C (2 mm); add ~1 min per extra mm",
    postCure=pc(True, 200, 4, "4-8 h at 200 C."), sg=R(1.40, 1.45, 1.55),
    corrosiveness="medium", corrosionNotes="Fluorinated volatiles in post-cure; chrome/nickel plating recommended for production molds.",
    moldCompat="rubber_corrosive", sources=["S4", "K-RUB"], confidence="low")
add(dict(group="silicone", family="silicone_rtv", familyLabel="Silicone - RTV liquid casting"),
    id="si-rtv-casting", name="RTV liquid silicone casting (platinum or tin)", hardness="30A", processType="liquid_cast_rt",
    shrinkPct=R(0.05, 0.1, 0.5), shrinkBasis="rt_mold_rt_part", shrinkNotes="Platinum <0.1%; tin (condensation) 0.2-0.5% and keeps shrinking for days.",
    viscosityClass="liquid_high", viscosityCp=R(3000, 20000, 50000), viscosityTempC=23, materialTempC=R(20, 23, 25),
    potLifeMin=R(10, 40, 90), moldTempC=R(20, 25, 80), demoldMin=R(30, 240, 1440), cureBasis="4-24 h RT; platinum 30-60 min at 60-80 C",
    postCure=pc(False, 80, 2), sg=R(1.08, 1.15, 1.40), moldingPressurePsi=R(0, 0, 60),
    flash="liquid_high", flashAllowancePct=R(3, 5, 10), overflowPctOfPartVolume=R(3, 5, 8),
    ventingSensitivity="high", ventingNotes="Vacuum degas; pour low, vent high.", releaseAgent="Usually none on metal; release for silicone-on-silicone.",
    moistureSensitivity="low", moistureNotes="Tin systems need ambient humidity; platinum inhibited by sulfur/tin/amines.",
    corrosiveness="none", moldCompat="rt_cast", draftDeg={"min": 0.0, "typ": 0.5}, sources=["K-RUB"], confidence="low")

# ======================= RUBBERS =======================
RUB = dict(group="rubber", processType="rubber_compression", shrinkBasis="cold_mold_cold_part_steel_ref",
    viscosityClass="rubber", materialTempC=R(20, 40, 70), bulkFactor=R(1.0, 1.0, 1.0),
    moldingPressurePsi=R(700, 1500, 3000), pressureNotes="On projected area of cavities + lands + overflow grooves.",
    draftDeg={"min": 0.0, "typ": 0.5}, flash="rubber", flashAllowancePct=R(5, 8, 10), overflowPctOfPartVolume=R(8, 10, 12),
    ventingSensitivity="medium", ventingNotes="1-3 bump (breathe) cycles; vents 0.03-0.08 mm or 0.5-1 mm vent holes at last-fill points.",
    releaseAgent="Water-based silicone or semi-permanent release, sparingly (excess causes knit lines).",
    moistureSensitivity="low", corrosiveness="low", moldCompat="rubber", postCure=None,
    cureBasis="Press cure for ~6 mm section at typ temperature; roughly halve per +10 C (TSE rule), add ~1 min per extra mm.")
NR = dict(RUB, family="rubber_nr", familyLabel="Natural rubber (NR)", moldTempC=R(140, 155, 170),
    mooney={"min": 40, "typ": 60, "max": 80, "test": "ML1+4@100C"},
    corrosionNotes="Sulfur cures can foul molds (sulfide deposits); chrome helps.", sources=["K-RUB"], confidence="medium",
    notes="Reversion if over-cured above ~165 C; thick parts cure at lower temperature for longer.")
add(NR, id="nr-40a", name="Natural rubber 40A", hardness="40A", shrinkPct=R(1.6, 2.2, 2.8), demoldMin=R(8, 12, 20), sg=R(0.98, 1.02, 1.08), confidence="low")
add(NR, id="nr-60a", name="Natural rubber 60A", hardness="60A", shrinkPct=R(1.4, 1.8, 2.4), demoldMin=R(6, 10, 15), sg=R(1.08, 1.12, 1.18))
add(NR, id="nr-80a", name="Natural rubber 80A", hardness="80A", shrinkPct=R(1.0, 1.4, 2.0), demoldMin=R(6, 10, 15), sg=R(1.18, 1.25, 1.35),
    draftDeg={"min": 0.5, "typ": 1.0}, confidence="low")
SBR = dict(RUB, family="rubber_sbr", familyLabel="Styrene-butadiene (SBR)", moldTempC=R(150, 160, 180),
    mooney={"min": 40, "typ": 55, "max": 70, "test": "ML1+4@100C"}, sources=["K-RUB"], confidence="low")
add(SBR, id="sbr-60a", name="SBR 60A", hardness="60A", shrinkPct=R(1.5, 1.9, 2.5), demoldMin=R(6, 10, 15), sg=R(1.12, 1.18, 1.25))
add(SBR, id="sbr-70a", name="SBR 70A", hardness="70A", shrinkPct=R(1.3, 1.7, 2.3), demoldMin=R(6, 10, 15), sg=R(1.15, 1.22, 1.30))
EPDM = dict(RUB, family="rubber_epdm", familyLabel="EPDM", moldTempC=R(160, 170, 190),
    mooney={"min": 40, "typ": 60, "max": 90, "test": "ML1+4@100C"},
    shrinkNotes="Clippard: EPDM 1.9-3.5%. Peroxide cures shrink more than sulfur (Apple Rubber).", sources=["S3", "S5", "K-RUB"])
add(EPDM, id="epdm-s-60a", name="EPDM sulfur-cured 60A", hardness="60A", shrinkPct=R(2.0, 2.6, 3.5), demoldMin=R(5, 8, 15), sg=R(1.08, 1.12, 1.20))
add(EPDM, id="epdm-s-70a", name="EPDM sulfur-cured 70A", hardness="70A", shrinkPct=R(1.9, 2.4, 3.2), demoldMin=R(5, 8, 15), sg=R(1.10, 1.15, 1.25))
add(EPDM, id="epdm-s-80a", name="EPDM sulfur-cured 80A", hardness="80A", shrinkPct=R(1.9, 2.2, 3.0), demoldMin=R(5, 8, 15), sg=R(1.15, 1.25, 1.35),
    draftDeg={"min": 0.5, "typ": 1.0})
add(EPDM, id="epdm-p-70a", name="EPDM peroxide-cured 70A", hardness="70A", shrinkPct=R(2.2, 2.8, 3.5),
    moldTempC=R(170, 177, 190), demoldMin=R(5, 8, 12), sg=R(1.10, 1.15, 1.25),
    postCure=pc(False, 150, 2, "Optional 2-4 h at 150 C for lowest compression set."),
    notes="Peroxide cure is inhibited by air at the surface: avoid trapped air (sticky skin) - vent well.")
NBR = dict(RUB, family="rubber_nbr", familyLabel="Nitrile (NBR)", moldTempC=R(150, 165, 180),
    mooney={"min": 35, "typ": 55, "max": 80, "test": "ML1+4@100C"},
    shrinkNotes="Clippard: 1.5-3.5%. Apple Rubber: sulfur-cured NBR 2.4%, peroxide 3.02%.", sources=["S3", "S5"])
add(NBR, id="nbr-s-70a", name="NBR sulfur-cured 70A", hardness="70A", shrinkPct=R(1.8, 2.4, 3.0), demoldMin=R(5, 8, 15), sg=R(1.20, 1.25, 1.32))
add(NBR, id="nbr-s-90a", name="NBR sulfur-cured 90A", hardness="90A", shrinkPct=R(1.5, 1.8, 2.5), demoldMin=R(5, 10, 15), sg=R(1.25, 1.32, 1.40),
    draftDeg={"min": 0.5, "typ": 1.0}, moldingPressurePsi=R(1000, 2000, 3000), flash="rubber_stiff", overflowPctOfPartVolume=R(10, 12, 15),
    confidence="low")
add(NBR, id="nbr-p-70a", name="NBR peroxide-cured 70A", hardness="70A", shrinkPct=R(2.4, 3.0, 3.5),
    moldTempC=R(165, 175, 185), demoldMin=R(5, 8, 12), sg=R(1.20, 1.25, 1.32),
    postCure=pc(False, 150, 2, "Optional 2-4 h at 150 C."))
HNBR = dict(RUB, family="rubber_hnbr", familyLabel="Hydrogenated nitrile (HNBR)", moldTempC=R(170, 180, 190),
    mooney={"min": 50, "typ": 70, "max": 100, "test": "ML1+4@100C"}, flash="rubber_stiff",
    overflowPctOfPartVolume=R(10, 12, 15), moldingPressurePsi=R(1000, 2000, 3000),
    postCure=pc(True, 150, 4, "2-4 h at 150-160 C typical for peroxide HNBR."), sources=["K-RUB"], confidence="low")
add(HNBR, id="hnbr-70a", name="HNBR peroxide-cured 70A", hardness="70A", shrinkPct=R(2.0, 2.6, 3.5), demoldMin=R(5, 10, 15), sg=R(1.18, 1.24, 1.30))
add(HNBR, id="hnbr-90a", name="HNBR peroxide-cured 90A", hardness="90A", shrinkPct=R(1.8, 2.2, 3.0), demoldMin=R(5, 10, 15), sg=R(1.25, 1.30, 1.38),
    draftDeg={"min": 0.5, "typ": 1.0})
CR = dict(RUB, family="rubber_cr", familyLabel="Polychloroprene (CR, Neoprene)", moldTempC=R(150, 160, 175),
    mooney={"min": 35, "typ": 50, "max": 70, "test": "ML1+4@100C"},
    shrinkNotes="Clippard: neoprene 1.0-3.0%.", moistureSensitivity="medium",
    moistureNotes="MgO/ZnO cure systems pick up moisture (scorch, porosity); store dry.",
    corrosiveness="medium", corrosionNotes="Chlorine-containing; releases traces of HCl and fouls molds; sticks to metal. Chrome/nickel plating recommended.",
    moldCompat="rubber_corrosive", hazards="ETU-type accelerators are reproductive toxicants.", sources=["S3", "K-RUB"])
add(CR, id="cr-50a", name="Neoprene (CR) 50A", hardness="50A", shrinkPct=R(1.4, 2.0, 3.0), demoldMin=R(8, 12, 20), sg=R(1.28, 1.32, 1.40), confidence="low")
add(CR, id="cr-70a", name="Neoprene (CR) 70A", hardness="70A", shrinkPct=R(1.0, 1.6, 2.5), demoldMin=R(8, 12, 20), sg=R(1.35, 1.42, 1.50))
FKM = dict(RUB, family="rubber_fkm", familyLabel="Fluoroelastomer (FKM / FFKM)", moldTempC=R(170, 177, 190),
    mooney={"min": 20, "typ": 40, "max": 80, "test": "ML1+10@121C"}, flash="rubber_stiff",
    overflowPctOfPartVolume=R(8, 10, 12), flashAllowancePct=R(3, 5, 8), moldingPressurePsi=R(1000, 2000, 3000),
    shrinkNotes="Clippard and Precision Associates: FKM 2.0-4.5%; includes post-cure shrink.",
    ventingSensitivity="high", ventingNotes="Trapped air/volatiles blister in post-cure; bump 2-3 times; vent well.",
    releaseAgent="Semi-permanent release; internal process aids (some cause fouling).",
    moistureSensitivity="medium", moistureNotes="Bisphenol cures with Ca(OH)2/MgO: moisture causes porosity/blisters; store compound dry.",
    corrosiveness="high", corrosionNotes="Releases HF/acid during cure; acids micro-crack even austenitic stainless and cause mold fouling (US4957975). Hard-chrome or electroless-nickel H13/420; clean molds regularly.",
    moldCompat="rubber_corrosive", hazards="HF and toxic fumes in post-cure ovens above ~250 C; ventilate.",
    sources=["S3", "S4", "S16", "K-RUB"])
add(FKM, id="fkm-bisphenol-75a", name="FKM bisphenol-cured 75A (A-type, 66% F)", hardness="75A", shrinkPct=R(2.5, 3.2, 4.0),
    demoldMin=R(5, 10, 15), sg=R(1.80, 1.85, 1.92),
    postCure=pc(True, 230, 16, "Typically 16-24 h at 230-232 C; some newer grades 2-4 h."))
add(FKM, id="fkm-bisphenol-90a", name="FKM bisphenol-cured 90A", hardness="90A", shrinkPct=R(2.2, 2.8, 3.5),
    demoldMin=R(5, 10, 15), sg=R(1.88, 1.95, 2.05), draftDeg={"min": 0.5, "typ": 1.0},
    postCure=pc(True, 230, 16, "16-24 h at 230-232 C."), confidence="low")
add(FKM, id="fkm-peroxide-75a", name="FKM peroxide-cured 75A (GF/GLT/GFLT high-fluorine)", hardness="75A", shrinkPct=R(2.5, 3.2, 4.0),
    demoldMin=R(5, 8, 12), sg=R(1.85, 1.95, 2.05),
    postCure=pc(True, 230, 4, "1-4 h at 200-230 C (grade dependent)."),
    notes="Peroxide FKM sticks to molds and is air-inhibited at the surface; vent well.", confidence="low")
add(FKM, id="ffkm-75a", name="Perfluoroelastomer FFKM 75A", hardness="75A", shrinkPct=R(2.5, 3.5, 4.5),
    moldTempC=R(170, 180, 200), demoldMin=R(10, 15, 30), sg=R(1.90, 2.00, 2.10),
    postCure=pc(True, 290, 24, "Stepped post-cure up to 250-300 C over 24-48 h, often in nitrogen."),
    flashAllowancePct=R(2, 3, 5), notes="Extremely expensive compound: weigh preforms to +/-1%, minimise flash.", confidence="low")
add(RUB, family="rubber_acm", familyLabel="Polyacrylate (ACM) / ethylene-acrylic (AEM)", id="acm-70a", name="ACM polyacrylate 70A", hardness="70A",
    shrinkPct=R(2.0, 2.5, 3.0), moldTempC=R(170, 180, 190), demoldMin=R(3, 6, 10),
    mooney={"min": 30, "typ": 45, "max": 60, "test": "ML1+4@100C"}, sg=R(1.25, 1.32, 1.40),
    postCure=pc(True, 175, 4, "4 h at 175 C (essential for compression set)."),
    releaseAgent="Sticky compound: semi-permanent release.", corrosiveness="medium",
    corrosionNotes="Chlorine/active-halogen cure sites and soap/sulfur cures foul and corrode molds; plating recommended.",
    moldCompat="rubber_corrosive", sources=["K-RUB"], confidence="low")
add(RUB, family="rubber_acm", familyLabel="Polyacrylate (ACM) / ethylene-acrylic (AEM)", id="aem-70a", name="AEM ethylene-acrylic (Vamac type) 70A", hardness="70A",
    shrinkPct=R(2.5, 3.0, 3.5), moldTempC=R(170, 180, 190), demoldMin=R(3, 6, 10),
    mooney={"min": 20, "typ": 40, "max": 60, "test": "ML1+4@100C"}, sg=R(1.28, 1.35, 1.45),
    postCure=pc(True, 175, 4, "4 h at 175 C."), releaseAgent="Sticky in the mold: internal release + semi-permanent external release.",
    corrosiveness="low", sources=["K-RUB"], confidence="low")
IIR = dict(RUB, family="rubber_iir", familyLabel="Butyl / halobutyl (IIR, CIIR, BIIR)", moldTempC=R(160, 170, 190),
    mooney={"min": 40, "typ": 55, "max": 70, "test": "ML1+8@125C"},
    ventingSensitivity="very_high", ventingNotes="Very low gas permeability: trapped air does not dissolve - blisters/backrind. Vent every last-fill point, bump 2-3 times.",
    sources=["K-RUB"], confidence="low")
add(IIR, id="iir-60a", name="Butyl IIR 60A (sulfur or resin cure)", hardness="60A", shrinkPct=R(1.5, 2.2, 3.0),
    demoldMin=R(10, 15, 30), sg=R(1.10, 1.15, 1.25),
    corrosionNotes="Resin cures activated with SnCl2/halogen donors are corrosive (bladder curing) - plate molds for resin cure.")
add(IIR, id="xiir-60a", name="Halobutyl CIIR/BIIR 60A", hardness="60A", shrinkPct=R(1.5, 2.2, 3.0),
    demoldMin=R(8, 12, 20), sg=R(1.10, 1.17, 1.25), corrosiveness="medium",
    corrosionNotes="Zinc oxide cure releases zinc halide/HX; mild mold corrosion and fouling - plating recommended.",
    moldCompat="rubber_corrosive")
add(RUB, family="rubber_eco", familyLabel="Epichlorohydrin (CO/ECO)", id="eco-70a", name="Epichlorohydrin ECO 70A", hardness="70A",
    shrinkPct=R(1.5, 2.0, 2.5), moldTempC=R(160, 170, 180), demoldMin=R(8, 12, 20),
    mooney={"min": 40, "typ": 60, "max": 90, "test": "ML1+4@100C"}, sg=R(1.30, 1.38, 1.45),
    postCure=pc(False, 150, 3, "Optional 2-4 h at 150 C."),
    moistureSensitivity="medium", corrosiveness="high",
    corrosionNotes="Chlorinated polymer: releases HCl, notorious for mold corrosion and fouling; hard chrome or 420 stainless with acid acceptor in compound.",
    moldCompat="rubber_corrosive", sources=["K-RUB"], confidence="low")

# ======================= THERMOPLASTICS (compression) =======================
add(dict(group="thermoplastic", family="tp_uhmwpe", familyLabel="UHMWPE"),
    id="uhmwpe-virgin", name="UHMWPE, virgin powder (GUR type)", processType="thermoplastic_compression",
    shrinkPct=R(2.0, 3.0, 4.0), shrinkBasis="cold_mold_cold_part_steel_ref",
    shrinkNotes="No standard supplier value for pressed parts; depends on cooling rate and pressure during crystallisation. Industry practice: mold oversize stock and machine to size.",
    viscosityClass="no_flow", bulkFactor=R(1.9, 2.1, 2.4), materialTempC=R(20, 23, 30), potLifeMin=None,
    moldTempC=R(190, 210, 230), demoldMin=R(60, 240, 600),
    cureBasis="Full cycle: heat-soak to uniform melt (rough guide 10-15 min per mm thickness at 200-230 C), then cool under pressure to <80 C.",
    postCure=None, sg=R(0.93, 0.935, 0.94),
    moldingPressurePsi=R(700, 1500, 3000),
    pressureNotes="5-20 MPa; best results reported at 190 C / 20 MPa / 30 min (RMUTT study). Keep pressure ON through cooling; slow cooling (about 1 MPa residual stress) vs fast (about 10 MPa).",
    draftDeg={"min": 0.0, "typ": 0.5}, flash="no_flow", flashAllowancePct=R(0, 1, 2), overflowPctOfPartVolume=R(0, 0, 0),
    ventingSensitivity="medium", ventingNotes="Air between powder grains must escape before melt seals: apply pressure gradually while heating.",
    releaseAgent="Usually none; PTFE sheet or high-temperature release on platens.",
    moistureSensitivity="none", corrosiveness="none", moldCompat="uhmw",
    sources=["S15", "K-TP"], confidence="low",
    notes="Melt does not flow (MFI ~0): charge must be exact (positive mold); powder bulk density 0.40-0.50 g/cm3 sets the loading-chamber depth.")
add(dict(group="thermoplastic", family="tp_uhmwpe", familyLabel="UHMWPE"),
    id="uhmwpe-filled", name="UHMWPE, filled/modified (glass bead, MoS2, regrind)", processType="thermoplastic_compression",
    shrinkPct=R(1.5, 2.3, 3.0), viscosityClass="no_flow", bulkFactor=R(1.8, 2.0, 2.3), materialTempC=R(20, 23, 30),
    moldTempC=R(190, 210, 230), demoldMin=R(60, 240, 600), cureBasis="As virgin UHMWPE.", postCure=None,
    sg=R(0.95, 1.00, 1.10), moldingPressurePsi=R(700, 1500, 3000),
    draftDeg={"min": 0.0, "typ": 0.5}, flash="no_flow", flashAllowancePct=R(0, 1, 2), overflowPctOfPartVolume=R(0, 0, 0),
    ventingSensitivity="medium", releaseAgent="Usually none.", moistureSensitivity="none", corrosiveness="none",
    moldCompat="uhmw", sources=["S15", "K-TP"], confidence="low")
PTFE = dict(group="thermoplastic", family="tp_ptfe", familyLabel="PTFE (preform + free sinter)", processType="preform_sinter",
    shrinkBasis="preform_die_rt_to_sintered_part_rt",
    viscosityClass="no_flow", materialTempC=R(21, 23, 27), potLifeMin=None,
    moldTempC=R(21, 23, 27), cureBasis="Preform dwell 1-5 min after slow, steady compression; then sinter free-standing in oven.",
    demoldMin=R(2, 5, 10),
    draftDeg={"min": 0.0, "typ": 0.0}, flash="no_flow", flashAllowancePct=R(0, 0, 1), overflowPctOfPartVolume=R(0, 0, 0),
    ventingSensitivity="medium", ventingNotes="Compress slowly so air escapes past the punch (clearance about 0.05 mm).",
    releaseAgent="None (self-lubricating); polished die.",
    moistureSensitivity="none", corrosiveness="low",
    corrosionNotes="Preform die at room temperature: no attack. Overheating above ~400 C in sintering releases HF/fluorinated fumes (toxic, corrosive to oven).",
    moldCompat="ptfe_preform", hazards="Polymer fume fever from overheated PTFE; ventilate sintering oven.")
add(PTFE, id="ptfe-virgin", name="PTFE, virgin granular (free-flow / fine-cut)", shrinkPct=R(1.8, 2.5, 3.5),
    shrinkNotes="Diametric 2-3% (Poly-Smith CMG-115), 1.8-2.8% (Halopolymer PN90), 3% (3M TF-1620). Preform grows in height and shrinks in diameter on sintering - anisotropic; machine to size.",
    bulkFactor=R(2.4, 3.0, 5.0),
    postCure=pc(True, 370, 4, "Free sintering 360-382 C (Chemours 363-382 C; gel point 342 C). Hours value is a small-part hold; hold roughly 1 h per 10 mm wall plus slow ramps (large billets take days)."),
    sg=R(2.14, 2.16, 2.18), moldingPressurePsi=R(3000, 4500, 5000),
    pressureNotes="Preform: Chemours 3000-4500 psi; AGC Fluon G307/G355 4500-5000 psi. Preform above 19 C (crystal transition) to avoid cracks.",
    sources=["S14"], confidence="medium",
    notes="The heat press is NOT used for sintering; the 'mold' is a room-temperature preform die. Press temperature input must be ignored/flagged.")
add(PTFE, id="ptfe-glass25", name="PTFE + 25% glass fibre", shrinkPct=R(1.2, 1.6, 2.2), bulkFactor=R(2.2, 2.8, 4.0),
    postCure=pc(True, 370, 4, "Free sintering 360-380 C; hold roughly 1 h per 10 mm wall plus slow ramps."), sg=R(2.20, 2.24, 2.27), moldingPressurePsi=R(5000, 7000, 10000),
    sources=["S14", "K-TP"], confidence="low")
add(PTFE, id="ptfe-carbon-graphite", name="PTFE + carbon/graphite (25%)", shrinkPct=R(1.0, 1.5, 2.0), bulkFactor=R(2.2, 2.8, 4.0),
    postCure=pc(True, 370, 4, "Free sintering 360-380 C; hold roughly 1 h per 10 mm wall plus slow ramps."), sg=R(2.05, 2.09, 2.12), moldingPressurePsi=R(5000, 7000, 10000),
    sources=["S14", "K-TP"], confidence="low")
PEEK = dict(group="thermoplastic", family="tp_paek", familyLabel="PEEK / PAEK", processType="thermoplastic_compression",
    shrinkBasis="cold_mold_cold_part_steel_ref",
    shrinkNotes="Victrex gives no design shrink for compression parts (geometry dependent). Estimate from injection data and slow-cooled crystallinity (~35%). Mold oversize and machine.",
    viscosityClass="melt", viscosityTempC=400, bulkFactor=R(2.5, 2.9, 3.2), materialTempC=R(150, 150, 180), potLifeMin=None,
    demoldMin=R(180, 300, 600), cureBasis="Several hours: pack cold at 350 bar, heat to set temperature at ~20 bar, hold for homogeneity, apply high pressure and cool naturally to RT (Victrex 7-step process).",
    postCure=pc(False, 200, 4, "Optional anneal (e.g. 200 C) for machined stock stress relief."),
    moldingPressurePsi=R(290, 5000, 5800),
    pressureNotes="Victrex: 350 bar (5076 psi) cavity pressure for packing and cooling; ~20 bar (290 psi) while heating.",
    draftDeg={"min": 0.0, "typ": 0.5}, flash="no_flow", flashAllowancePct=R(2, 2, 3), overflowPctOfPartVolume=R(0, 0, 0),
    ventingSensitivity="medium", ventingNotes="Pack powder under pressure cold before heating; seal-ring clearance <50 um.",
    releaseAgent="High-temperature release stable above max cycle temperature (Victrex).",
    moistureSensitivity="medium", moistureNotes="Dry powder 3 h at 150 C (450PF/HT) or 180 C (ST) before molding.",
    corrosiveness="none", moldCompat="hot_tp", sources=["S1"])
add(PEEK, id="peek-450pf", name="PEEK unfilled fine powder (450PF type)", shrinkPct=R(1.0, 1.4, 2.0),
    viscosityCp=R(350000, 450000, 500000), moldTempC=R(380, 400, 410), sg=R(1.30, 1.30, 1.32), confidence="medium",
    notes="Powder weight = part volume x density x 102% (Victrex). Fine powder strongly preferred over granules (grain-boundary weakness).")
add(PEEK, id="paek-ht-st", name="PAEK high-temperature (Victrex HT / ST powder)", shrinkPct=R(1.0, 1.4, 2.0),
    viscosityCp=R(400000, 500000, 700000), moldTempC=R(400, 420, 430), materialTempC=R(150, 180, 180), sg=R(1.30, 1.30, 1.32),
    confidence="medium", notes="Victrex: HT P45PF at 400 C, ST P45PF at 420 C; tool must be capable of 450 C.")
add(PEEK, id="peek-milled-fibre", name="PEEK powder + 30% milled glass/carbon fibre blend", shrinkPct=R(0.3, 0.6, 1.0),
    viscosityCp=R(500000, 800000, 1500000), moldTempC=R(380, 400, 410), sg=R(1.40, 1.45, 1.52), confidence="low",
    notes="Victrex recommends powder + milled fibre blends rather than reinforced granules for compression molding.")

# ======================= PVC plastisol (heat-press patches) =======================
add(dict(group="thermoplastic", family="tp_pvc_plastisol", familyLabel="PVC plastisol"),
    id="pvc-plastisol", name="PVC plastisol (heat-press fused, e.g. patches/grips)", hardness="60A", processType="liquid_cast_hot",
    shrinkPct=R(1.0, 2.0, 3.0), viscosityClass="liquid_med", viscosityCp=R(1000, 4000, 15000), viscosityTempC=25,
    materialTempC=R(20, 25, 30), potLifeMin=R(1440, 10080, 43200), moldTempC=R(150, 170, 190),
    demoldMin=R(3, 8, 15), cureBasis="Fuse until whole section reaches 170-180 C, then cool mold below ~60 C before demold.",
    postCure=None, sg=R(1.15, 1.25, 1.40), moldingPressurePsi=R(0, 20, 100),
    pressureNotes="Fill by dispensing; press supplies heat and light clamp.",
    draftDeg={"min": 1.0, "typ": 2.0}, flash="liquid_med", flashAllowancePct=R(2, 4, 6), overflowPctOfPartVolume=R(2, 4, 6),
    ventingSensitivity="medium", ventingNotes="Vacuum degas plastisol; trapped air expands on fusion.",
    releaseAgent="None usually on aluminium; silicone release for deep cavities.",
    moistureSensitivity="low", corrosiveness="medium",
    corrosionNotes="Overheating (>190-200 C) dehydrochlorinates PVC: HCl rusts carbon steel and discolours parts; aluminium or stainless molds.",
    moldCompat="pvc", sources=["K-TP"], confidence="low",
    hazards="HCl fumes on overheating; plasticiser fumes.")

# ---------- shipped-file fields, validation and output (materials_post.py) ----------
if __name__ == "__main__":
    import sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import materials_post
    materials_post.main(out)


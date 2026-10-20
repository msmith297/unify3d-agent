# Part-material lookup table: compression molding in a heat press

Companion notes for `materials.json`, which has 96 subtypes in 38 families. `gen_materials.py` in the same folder regenerates it and checks it: unique ids, the same keys in the same order on every object, `min <= typ <= max`.

These are design starting values, not specification values. Every subtype has a `confidence`:

- **medium:** the main numbers trace to a supplier datasheet, guide or patent.
- **low:** the numbers come from handbook knowledge or engineering estimates, with no specific document behind them.

The page should load a subtype into editable fields, show the confidence, and keep the user's edits.

## 1. Schema (every object has every key, in this order)

| key | type | meaning |
|---|---|---|
| `id` | string | Stable kebab-case id, e.g. `pu-tdi-ether-90a` |
| `family`, `familyLabel` | string | Family id plus display label. Page cascade: family, then subtype. |
| `group` | string | `polyurethane`, `thermoset_resin`, `thermoset_compound`, `silicone`, `rubber` or `thermoplastic` |
| `name`, `hardness` | string, string or null | Display name and Shore grade (`"90A"`, `"60D"`) |
| `processType` | enum | `liquid_cast_hot`, `liquid_cast_rt`, `liquid_compression` (LSR), `rubber_compression`, `compound_compression`, `thermoplastic_compression`, `preform_sinter` (PTFE) or `foam_pour`. Drives which formulas and mold style the tool uses. |
| `shrinkPct` | {min,typ,max} % | Linear mold shrinkage |
| `shrinkBasis` | enum | `cold_mold_cold_part_steel_ref`, `rt_mold_rt_part` or `preform_die_rt_to_sintered_part_rt` (see section 2) |
| `shrinkNotes` | string | Anisotropy, source values |
| `viscosityClass` | enum | `liquid_low` (<1,000 cP), `liquid_med` (1k-10k), `liquid_high` (10k-150k), `paste_lsr`, `rubber` (use `mooney`), `compound` (thermoset molding compound, flow by cup/spiral test), `melt` (PEEK) or `no_flow` (UHMWPE, PTFE) |
| `viscosityCp`, `viscosityTempC` | {min,typ,max} or null, number or null | Initial mixed/process viscosity at that temperature |
| `mooney` | {min,typ,max,test} or null | Compound Mooney (or Williams plasticity for HCR). `test` names the method. |
| `bulkFactor` | {min,typ,max} | Loose-charge volume / molded volume. 1.0 for liquids, preforms and SMC. |
| `materialTempC` | range or null | Prepolymer, compound or powder preheat (or drying) temperature |
| `potLifeMin` | range or null | Working time after mixing (liquids) |
| `moldTempC` | range | Mold / press-platen setpoint. PTFE: the preform die at room temperature. |
| `demoldMin`, `cureBasis` | range, string | In-mold time and what that time refers to |
| `postCure` | {required, tempC, hours, notes} or null | Oven post-cure, or the sintering step for PTFE |
| `sg` | range | Specific gravity of the cured part |
| `moldingPressurePsi`, `pressureNotes` | range, string | Pressure on the projected area |
| `draftDeg` | {min,typ} | Recommended draft, in degrees per side |
| `flash` | object | `moldStyle`, `landWidthMm`, `flashThicknessMm`, `ventDepthMm`, `overflowGrooveRadiusMm` (each {min,typ,max}) |
| `flashAllowancePct` | range | Charge excess over part volume (section 4) |
| `overflowPctOfPartVolume` | range | Overflow-groove/riser capacity per cavity (section 5) |
| `ventingSensitivity`, `ventingNotes` | enum `low/medium/high/very_high`, string | |
| `releaseAgent` | string | |
| `moistureSensitivity`, `moistureNotes` | enum `none/low/medium/high/very_high`, string | |
| `corrosiveness`, `corrosionNotes` | enum `none/low/medium/high`, string | |
| `moldCompat` | object | `aluminium`, `p20Steel`, `hardenedToolSteel`, `stainless420` (each `good/limited/not_recommended`), `plating`, `notes` |
| `hazards`, `notes` | string | |
| `confidence` | `medium` or `low` | |
| `sources` | string[] | Keys into section 7 |

Families covered:

- **PU:** TDI-ether (70A-75D), MDI-ether (60A-60D), MDI-ester, TDI-ester hot-cast, PPDI, room-temperature castables, rigid/structural, foams (flexible, integral-skin, microcellular), millable.
- **Epoxy:** anhydride casting, amine casting, filled tooling, silica APG, EMC (silica-filled and glass-filled).
- **Unsaturated polyester:** BMC standard and low-shrink, SMC standard and Class A, clear casting, filled casting.
- **Vinyl ester:** BMC, SMC, casting.
- **Phenolic:** wood-flour, glass, mineral, cotton.
- **Amino and DAP:** MF cellulose, MF glass/mineral, UF, DAP glass, DAP mineral.
- **Silicone:** HCR 30/50/70/80A, LSR 30/50/70A, FVMQ, RTV.
- **Rubber:** NR, SBR, EPDM (sulfur and peroxide), NBR (sulfur and peroxide), HNBR, CR, FKM (bisphenol and peroxide), FFKM, ACM, AEM, IIR, CIIR/BIIR, ECO.
- **Thermoplastics:** UHMWPE (virgin and filled), PTFE (virgin, glass, carbon), PEEK 450PF, PAEK HT/ST, PEEK milled-fibre blend, PVC plastisol.

PVC plastisol is extra. It is what heat-press patch shops actually run, and it is the HCl corrosion case the brief mentioned.

## 2. Cavity size from part size (shrink basis and mold thermal expansion)

**How shrink data are defined.** ASTM D955 (thermoplastics), ISO 294-4, ISO 2577 (thermosets) and the rubber-industry practice described by Apple Rubber all measure:

- the mold cavity at room temperature (about 23 C), and
- the part at room temperature after conditioning (24-48 h, after post-cure where one applies).

`S = (L_cavity,RT - L_part,RT) / L_cavity,RT`

The test mold was steel at process temperature. The steel's thermal expansion while hot is therefore already included in the published number. The tool should not add steel expansion on top. That would double-count it.

**Formula 1, steel mold and supplier shrink (basis `cold_mold_cold_part_steel_ref`):**

`L_cav,RT = L_part / (1 - S)`

`L_part * (1 + S)` differs by S^2: 0.04% at S = 2%. Use the exact form.

**Formula 2, mold metal different from the reference steel** (aluminium plate, Be-Cu insert):

`L_cav,RT = L_part / (1 - S) * (1 + a_ref * dT) / (1 + a_mold * dT)`, where `dT = T_mold - 20 C`

Expansion coefficients, 20-200 C:

| metal | alpha (1/K) |
|---|---|
| P20 / 1.2311 | 12.8e-6 |
| H13 / 1.2344 | 11.5-12.4e-6 |
| 420 / 1.2083 | 10.3-11.0e-6 |
| 17-4PH | 10.8e-6 |
| 6061 / 7075 aluminium | 23.4-23.6e-6 |
| Be-Cu | 17e-6 |

Use `a_ref = 12.0e-6` when the source does not say.

Size of the correction for an aluminium mold, which is the "aluminium with steel inserts" option:

| process | mold temperature | correction | effect on 100 mm |
|---|---|---|---|
| Hot-cast PU | 100 C | -0.09% | 0.09 mm |
| Rubber | 170 C | -0.17% | 0.17 mm |
| UHMWPE | 210 C | -0.22% | 0.22 mm |

These are comparable to typical rubber tolerances (RMA A2 / ISO 3302-1 M2: roughly +/-0.25 mm at 100 mm), so the tool must apply this correction.

**Formula 3, hot-mold basis** (some in-house data are hot cavity to cold part):

`L_cav,RT = L_part / (1 - S_hot) / (1 + a_mold * dT)`

**Formula 4, room-temperature cast (basis `rt_mold_rt_part`):** `L_cav = L_part / (1 - S)` with no thermal term.

**PTFE (basis `preform_die_rt_to_sintered_part_rt`):** free sintering is anisotropic. The diameter shrinks 2-3% and the height in the pressing direction changes differently. Treat the part as stock that will be machined: oversize all dimensions by the shrink plus a machining allowance of 1.5-3 mm per side.

**Dimensions that cross metals.** A dimension between two features on different inserts uses the alpha of the plate that locates them, normally the aluminium base. A dimension within one steel insert uses steel.

**Directional shrink.**

- SMC/BMC: lower shrink along the flow direction.
- PEEK and UHMWPE: thickness shrinks more than in-plane.

For these the tool should allow per-axis S (x, y, z), defaulting to the scalar `typ`.

**Tolerance check (hook for the simulators).** If `(S_max - S_min) * L > 0.5 * tolerance(L)` from the drawing, flag the dimension. Make that cavity "steel-safe": undersized where a feature can later be enlarged, oversized for cores. Then plan a first-article measurement loop. This flag is a natural input for the later process/demolding simulator.

## 3. Cure time scaling (when the user changes press temperature)

**Rubbers.** Each +10 C roughly halves the press time (TSE Industries rule):

`t(T) = t_typ * 2^((T_typ - T) / 10)`

Clamp T to `moldTempC.min..max`. Above `max` there is a risk of reversion or scorch.

**Thick sections.** The time to bring the centre to temperature is about `(h/2)^2 / alpha_th`. Use alpha_th of about 1.0e-7 m^2/s for rubber and PU (1.3e-7 for filled thermosets). Example: a 10 mm wall needs about 250 s of extra heating. The rubber rule of thumb is +1 min per mm over 6 mm.

**Liquid PU.** Demold time does not scale this way. Keep `demoldMin` and warn if T drops below `moldTempC.min`.

## 4. Charge (pour) weight per cavity

`m_cav [g] = V_part [cm^3] * SG * (1 + flashAllowancePct/100)`

`m_shot = n_cav * m_cav + m_runner/riser`

Liquids also add 5-10% of the batch as hold-up in the mixing cup or dispensing line. PEEK matches Victrex's own rule: part volume x density x 102%.

**Loading chamber for powders/compounds** (bulkFactor > 1):

`V_load = m_cav / SG * bulkFactor`

`loading-well depth above the cavity = (V_load - V_cavity) / A_well`

This is a hard requirement for UHMWPE (about 2.1), PTFE (3-5), PEEK powder (about 2.9) and phenolic/amino powders (2-10). Preforming or tableting the compound reduces it.

**Press force:**

`F [lbf] = P [psi] * A_projected [in^2]`

`A_projected` covers cavities + lands + overflow grooves + loading wells, for every cavity in the heat. Divide F by 2000 to get US tons. Check F against the press rating and against the plate bending/bolt calculation.

**Foams:** use internal foam pressure (0.3-8 bar) x mold area for clamping. The charge is the target density x cavity volume x (1 + overpack 5-15%).

## 5. Overflow groove volume vs viscosity

**Rule:** groove capacity per cavity must hold all excess charge with margin:

`V_groove >= max(overflowPctOfPartVolume.typ/100 * V_part, 1.5 * flashAllowancePct.typ/100 * V_part)`

**Ball-milled semicircular groove around a cavity perimeter P:**

`V = (pi * r^2 / 2) * P`, so `r = sqrt(2 * V / (pi * P))`

Clamp r to `flash.overflowGrooveRadiusMm.min..max`. If the computed r exceeds max, add a second concentric groove or spill pockets at the last-fill points; do not use one oversized groove. Place the groove centre at `landWidthMm + r` from the cavity edge.

Guidance by flow class:

| class | mold style | land width | parting-line gap / flash | vent depth | groove r | overflow % |
|---|---|---|---|---|---|---|
| `liquid_low` <1k cP (hot-cast PU 80A-75D, epoxy, rigid PU) | closed pour, metal-to-metal land, risers at high points | 2-4 mm | <=0.02 mm | 0.02-0.05 mm, or 1-3 mm vent holes to risers | 1-2 mm | 3-8 |
| `liquid_med` 1k-10k cP (MDI/TDI ester, PPDI, room-temperature PU, plastisol) | same plus overflow groove | 1.5-3 mm | <=0.03-0.05 mm | 0.03-0.05 mm | 1-2.5 mm | 3-8 |
| `liquid_high` 10k-150k cP (filled epoxy, RTV, filled UP) | land + groove | 1-2 mm | 0.02-0.08 mm | 0.03-0.08 mm | 1.5-2.5 mm | 3-8 |
| `paste_lsr` (LSR) | land fit <=0.005 mm, vacuum | 1-3 mm | <=0.005 mm | 0.003-0.01 mm | 1-2 mm | 3-8 |
| `rubber` (Mooney 35-70) | flash land, then groove; tear-trim optional | 0.5-1.5 mm (tear-trim knife 0.1-0.25 mm) | 0.05-0.15 mm | 0.03-0.08 mm or 0.5-1 mm holes | 1-2 mm | 8-12 |
| `rubber` stiff (FKM, HNBR, 90A NBR) | as above, wider | 0.8-2 mm | 0.05-0.2 mm | same | 1.5-3 mm | 8-15 |
| `compound` (phenolic, amino, DAP, EMC) | semi-positive shear edge, or flash land | 1.5-3 mm | 0.025-0.10 mm | 0.02-0.05 mm, plus "breathe" bumps | 0-1 mm | 0-5 |
| SMC/BMC (`flash.moldStyle` shear edge) | positive/semi-positive telescoping shear edge | shear edge 3-6 mm long | radial clearance 0.05-0.10 mm | 0.02-0.05 mm, or vacuum | none | 0-2 |
| `melt` / `no_flow` (PEEK, UHMWPE, PTFE) | positive plunger/sleeve | n/a | clearance <=0.05 mm (Victrex: seal ring <50 um) | none | none | 0 |

The flash thickness the land produces maps to the drawing's flash classes:

- ISO 3302-1 / RMA flash-thickness codes T.08, T.10, T.15, T.20 ... (mm).
- Flash-extension codes F1 = 0.08 mm, F2 = 0.40 mm, F3 = 0.80 mm, F4 = no limit.

If the drawing calls out F1 or T.08, the tool must choose the tighter land gap and plan for cryogenic deflash or tear-trim.

**No parting line on critical surfaces.** This is a geometry constraint, not a material one. The material table only says how visible a flash line will be: LSR and liquid PU produce a witness at gaps of only 5-20 um.

## 6. Mold-material rules used in `moldCompat`

**Aluminium (6061-T651, 7075-T651, mold plate) is `good` when all of these hold:**

- mold temperature <= about 120 C,
- pressure <= about 1000 psi,
- non-abrasive,
- non-corrosive.

That covers all cast PU, room-temperature resins, foams and plastisol (aluminium is standard there).

**Aluminium is `limited`:**

- from 120-200 C (7075-T6 is aged at about 121 C and over-ages above that; 6061-T6 is aged at 160-177 C, so it softens slowly with long exposure);
- at 1000-3000 psi (lands peen and hob).

That covers rubber and silicone. Use steel lands/inserts or a steel cavity plate for production runs.

**Aluminium is `not_recommended`:**

- above about 200 C (UHMWPE is limited, PEEK/PAEK is out; Victrex specifies H13 >50 HRC);
- with abrasive glass/silica/mineral compounds;
- with acid-releasing compounds (FKM/HF, CR/ECO/ACM/halobutyl HCl, amino resins) at production volume.

**Corrosive materials** call for 420 stainless (1.2083) or H13/P20 with hard chrome 0.01-0.025 mm or electroless nickel(-PTFE). FKM acids micro-crack even austenitic stainless (US 4,957,975), so plan scheduled mold cleaning.

**LSR / platinum RTV:** do not reuse a mold that has run sulfur-cured rubber or tin/amine systems without thorough cleaning. Sulfur, tin and amines inhibit the platinum cure.

## 7. Sources

Key, then what was used from it:

- **S1 Victrex, Compression Molding Processing Guide (Jan 2022)** (https://www.victrex.com/-/media/downloads/technical-guides/victrex_compression-molding-brochure_jan2022.pdf). Read in full.
  - 350 bar cavity packing pressure, about 20 bar while heating; 400 C (450PF/HT), 420 C (ST).
  - Dry 3 h at 150 C (450PF/HT) or 180 C (ST).
  - Powder = V x rho x 102%; seal clearance <50 um; H13 >50 HRC.
  - Cool under high pressure naturally to room temperature; cycle several hours; crystallinity about 35%.
  - Fine powder or powder + milled fibre preferred over granules.
- **S2 TSE Industries, "Molding of Millathane Millable Urethane Rubber", Tips 2-2 Rev 1** (https://www.tse-industries.com/wp-content/uploads/Millathane-Molding-Tips2-2-Rev1.pdf). Read in full.
  - Shrink 1.5-2.5%.
  - Sulfur cure 140-165 C, 7-60 min; peroxide 149-182 C, 2-20 min.
  - Cure time halves per +10 C; Mooney 35-45 for injection.
  - Excess release causes knit problems.
- **S3 Clippard, Material Properties Chart** (https://www.clippard.com/cms/node/583): NBR 1.5-3.5%, EPDM 1.9-3.5%, neoprene 1.0-3.0%, urethane 1.6-3.3%, silicone 2.0-5.0%, FKM 2.0-4.5%.
- **S4 Precision Associates, Size & Tolerance catalog** (https://catalog.precisionassoc.com/Asset/PAI%20Size%20and%20Tol.pdf, via search summary): FKM 2.0-4.5%, VMQ 2.0-5.0%, FVMQ 2.8-4.7%.
- **S5 Apple Rubber, "Hot topics for engineers": shrink** (https://www.applerubber.com/hot-topics-for-engineers/?p=1803).
  - NBR sulfur 2.4%, NBR peroxide 3.02%; peroxide shrinks more than sulfur.
  - Silicone/FKM shrink more than NBR/EPDM; softer shrinks more.
  - Shrink is measured as molded ring vs actual cavity dimensions.
- **S6 Bakelite (Hexion) PF datasheets on Material Data Center** (e.g. https://www.materialdatacenter.com/ms/en/Bakelite/Hexion/Bakelite%C2%AE+PF+31%252E5/9e0795bd/295, via search summary).
  - Compression mold temperature 160 C (160-190 C); cavity pressure >15 MPa.
  - Compression shrink 0.45-0.5%; cure 1 min (specimen); material preheat 80-100 C.
- **S7 LyondellBasell TDS** (via search summaries):
  - BMC 695-3583 / Dura-BMC 695-3583B: vinyl ester, shrink 0-0.0022 in/in, compression molded 138-165 C.
  - BMC 940-8649: 310-330 F, 3-4 tons/in^2.
  - Premi-Glas 1281-47VE SMC and TMC 1266: 300 F, 500-1000 psi.
  - Premi-Glas 1103: shrink 0.00322 in/in, RT mold / RT part.
  - URLs under https://www.lyondellbasell.com/globalassets/... (listed in search results).
- **S8 SMC process data.**
  - EP0103954 (https://data.epo.org/publication-server/rest/v1.2/patents/EP0103954NWA1/document.html): about 150 C, 3.4-10.5 MPa, 1-3 min cure.
  - Open University Manupedia, compression moulding (https://www.open.edu/openlearn/science-maths-technology/engineering-technology/manupedia/compression-moulding): 14-40 MPa, 140-170 C, draft >=1 deg, walls 3-6 mm.
  - Low-profile SMC <0.05% shrink (secondary sources).
- **S9 Melamine resin patents US 4,303,561 / 4,328,326** (https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/4303561): compression 120-180 C (preferred 140-170 C), 150-800 bar (preferred 200-500), cycle 30 s-5 min; after-shrink <=0.4%.
- **S10 MakeItFrom, Melamine Formaldehyde Moulding Compound** (https://www.makeitfrom.com/material-properties/Melamine-Formaldehyde-Moulding-Compound): shrinkage 0.26-1.0%.
- **S11 DAP.**
  - Raschig DAP 5562 TDB (https://www.raschig.de/downloads/buk/tdb/TDB%20-%20DAP%205562.pdf): 0.15-0.45% (ISO 2577).
  - Professional Plastics MIL-M-14 overview (https://professionalplastics.com/MILM14): 300-380 F, 250-1000 psi.
- **S12 HCR silicone datasheets.**
  - Dow SILASTIC SH 502 U and DY 32-625 U (https://www.dow.com/en-us/pdp.silastic-sh-502-u-silicone-rubber.02039079h.html): 10 min at 170 C press, 4 h at 200 C post-cure, SG 1.41 / 1.30.
  - Shin-Etsu SV-15001U: 10 min at 177 C + 4 h at 200 C.
  - Wacker ELASTOSIL R 701/40 S: 15 min at 165 C + 4 h at 200 C.
- **S13 LSR.**
  - Simtec, "Expansion or shrinkage of LSR" (https://www.simtec-silicone.com/blogs/lsr-parts-expansion-or-shrinkage-of-liquid-silicone-rubber-lsr/): 2.5-3%.
  - Dow Liveo C6-550 datasheet (https://protolabs.com/media/njgn2fee/liveo-c6-550-lsr.pdf): 2.0%.
  - Momentive Silopren LSR 2010 / 3366-50: mold 170-230 C.
  - Dow LIM processing guide: post-cure often not needed.
- **S14 PTFE.**
  - Chemours Teflon granular (https://www.teflon.com/Products/resins/ptfe-granular): preform 3000-4500 psi; sinter 363-382 C.
  - AGC Fluon G307/G355 (Knowde TDS): compression ratio 2.7, preform 4500-5000 psi, gel point 342 C.
  - Inoflon processing guides: sinter 360-380 C, 25-30% volume expansion on melting.
  - Halopolymer PN90: 1.8-2.8%. 3M TF-1620: 3% (ASTM D4894). Poly-Smith CMG-115: 2-3%, SG 2.14-2.18.
- **S15 UHMWPE.**
  - RMUTT study (https://research.rmutt.ac.th/?p=14988): 170-210 C, 5-20 MPa; best 190 C / 20 MPa / 30 min.
  - US 6,475,094: 200-300 C, example 220-230 C for 45 min.
  - Intern. Polymer Processing 2000 (https://www.degruyterbrill.com/document/doi/10.1515/ipp-2000-0010/pdf): residual stress about 10 MPa fast cooling vs about 1 MPa slow.
  - Purdue dissertation AAI9939405: about 15 MPa at melt, about 77 MPa at crystallisation for maximum crystallinity.
- **S16 FKM mold fouling/corrosion.** US 4,957,975 (https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/4957975) and US 4,663,395 / 4,673,715:
  - Acids from VDF-fluoroelastomer cure micro-crack even austenitic stainless molds.
  - Bisphenol/phosphonium cures foul molds.
- **S17 EMC.**
  - CAPLINQ EMC datasheet (https://storefront.caplinq.com/products/6830/files/4567): post-mold cure 6-8 h at 175 C; transfer 40-70 kgf/cm^2.
  - US 6,319,739: about 175 C molding, about 5 h at 175 C PMC.
  - US 9,890,307: shrink test 175 C / 70 kgf/cm^2 / 4 h post-cure.
  - UMD thesis: post-gel effective cure shrink 0.077%.
- **S18 Room-temperature cast PU.**
  - BJB M-3190 (90A) 0.0010 in/in and TC-680 (80A) 0.006 in/in (ASTM D2566), via revchem.com.
  - Xometry: about 0.15% for urethane cast in silicone molds (https://www.xometry.com/resources/casting/top-5-tips-for-designing-custom-urethane-casted-parts/).
- **S19 LANXESS Adiprene TDI / LF TDI** (https://lanxess.com/en/products-and-brands/brands/adiprene/adiprene-tdi-and-lf-tdi).
  - LF free TDI <0.1%, longer pot life, faster demold.
  - LF 750D is TDI-PTMEG at 8.79% NCO (patent citation).
  - Vibrathane literature: curative at 95-105% of theory.
- **S20 US 10,766,171** (https://patents.google.com/patent/US10766171): PU shrinks 1-4% during molding and cure, mostly before it gains strength.
- **S21 Mearthane, cast molding design tips** (https://mearthane.com/5-design-tips-for-cast-molding-with-thermoset-polyurethanes/): minimum wall 0.050 in; fillets help air escape and demold.
- **S22 Rubber design guides.**
  - Goodyear Rubber design guide: >=0.020 in rubber around inserts.
  - Robinson Rubber, "Designing with Rubber" (https://robinsonrubber.com/wp-content/uploads/2024/02/RRPC-Designing-With-Rubber.pdf): flash and closure-dimension definitions, mold layouts.
- **K-PU, K-EP, K-TS, K-RUB, K-TP: knowledge-based** (no specific document was fetched in this session). They draw on:
  - standard handbook ranges (Modern Plastics Encyclopedia / Plastics Design Library tables for thermoset compounds: shrink, bulk factor, 2000-8000 psi; RMA Rubber Handbook and ISO 3302-1 tolerance/flash classes; ASM aluminium temper data);
  - typical supplier datasheets for cast PU (Adiprene/Vibrathane/Andur class), epoxies, polyester/vinyl ester, HNBR/ACM/AEM/ECO/IIR compounds.
  - Hot-cast PU shrink (about 1.0-2.0%) is an engineering estimate. Thermal contraction from a 100 C mold (CTE about 1.5-2e-4 /K x 77 K = 1.1-1.5%) plus about 0.3-0.5% cure shrink. It is consistent with S3 (urethane 1.6-3.3%, likely millable/thermoplastic PU) and S20 (1-4%), and much higher than the room-temperature cast values in S18.

## 8. Known gaps / what to verify before shipping

1. **Hot-cast PU shrink and viscosity (all `pu_*` hot entries are `low`/`medium`).** No public Adiprene/Vibrathane TDS with shrink was retrievable. Ask LANXESS, Era Polymers or Notcutt for grade sheets, or measure on the first article.
2. **Rubber shrink depends on the compound** (filler level, cure system, post-cure). The table gives polymer-family defaults. Shops should overwrite them with their compounder's value. The `shrinkBasis` field tells the tool how to apply the value.
3. **UHMWPE and PEEK compression shrink** have no authoritative design values. Mold oversize stock and machine.
4. **PTFE:** the heat press is not used for sintering. When `processType = preform_sinter`, the tool should ignore or flag the press-temperature input and the aluminium/thermal-expansion correction.
5. **Viscosity values** for hot-cast PU, PPDI and ester systems are estimates of initial mixed viscosity. Viscosity rises quickly toward gel within `potLifeMin`.
6. **Molding pressures for liquid systems** (0-150 psi) are clamp requirements, not flow pressures. Plate thickness and bolt sizing must also include internal hydrostatic/foam pressure and the platen force the user actually sets. A heat press can easily apply far more than the mold needs.

# Design rules: heat-press compression molds for PU, resin and rubber

Knowledge base for the built-in tool "Design compression mold for plastics and resins" (category "Molding & Toolmaking").
It is written for the agent to follow and for the tool author to turn into TOOL.md text, inputs.json lookup tables, and structural-calc checks.

## How to read the tags

- **[S#]**: the rule or number comes from a published source (listed at the end). Supplier and vendor numbers are typical values. The material TDS (technical data sheet) always overrides them.
- **[K]**: established mold-shop practice or engineering handbook knowledge that was not confirmed by a fetched source in this research. Treat it as a default that the user can change, and validate it with the structural and process simulations.
- **[C]**: a number calculated here from physical properties. The working is shown.

Units are given in mm first, with inches in brackets. The agent should show both on the setup sheet.

## 0. Two process classes (decide this first, from the part-material family)

Almost every rule below changes with the process class, so the tool must classify the job before it designs anything.

| | **A. Liquid pour** (cast PU elastomer, RT or hot-cast PU, epoxy, polyester or "resin") | **B. Compound compression** (rubber: NBR, EPDM, FKM, silicone HCR, NR, CR, millable PU; thermoset BMC, phenolic) |
|---|---|---|
| Charge | Liquid poured into the open cavity (base, lid or both) | Solid preform or blank placed in the cavity |
| Viscosity at process temperature | ~80 cP (Smooth-Cast 300 [S18]) up to a few thousand cP for hot-cast prepolymers [K] | Mooney ~35–45 ML(1+4)100 °C for flowable millable PU [S10]. These materials do not flow until heated and pressed. |
| Cavity pressure | Low: hydrostatic pressure plus the lid squeeze. The press force goes mostly into the lid stops. | High: 8–12 MPa (1,160–1,740 psi) for rubber [S4], 13.8–20.7 MPa (2,000–3,000 psi) for thermosets on the projected cavity area plus the land area [S7] |
| Mold temperature | RT systems: often no heated mold above 49 °C (120 °F) [S19]. Hot-cast PU: about 80–115 °C (~100 °C typical) [S28][K]. | Rubber: 150–200 °C [S2][S5]. Millable PU: 140–182 °C [S10]. Thermosets: 141–204 °C [S7]. |
| Main failure modes | Leaks, air traps at high points, bubbles, short shots from pot life | Flash, backrind, non-fill, porosity, undercure, mold crush |
| Flash/vent gaps | Very tight (a liquid finds any gap) | 0.05–0.2 mm |

**Rule 0.1**: When the part material is a liquid, the press mainly clamps the lid and squeezes out the excess. The design is governed by sealing, venting, the pour path and handling. When the material is a solid compound, the press pressure forms the part. The design is then governed by projected area times pressure, land and overflow design, and mold strength.

**Rule 0.2**: Check the press temperature input against the material's allowed mold temperature. If the user picks an RT-cure resin and a hot press, warn them: some RT casting PUs say not to use heated molds above 120 °F / 49 °C because the cure becomes uneven [S19]. If the cure is a rubber cure above about 150 °C and the mold is aluminium, warn them as well (see 14.3).

## 1. Mold type selection

Definitions are from [S7][S8].

- **Open / flash type**: the lid closes onto a land around the cavity, and the excess escapes over the land into overflow. Simple and cheap. It produces little back-pressure, so density is lower and flash scrap is high. It needs no exact charge [S7][S8].
  - **Fits**: most PU, resin and rubber jobs in a heat-press shop. Shallow to medium-depth parts. Parts where flash on the parting line can be trimmed. Low-viscosity liquids.
  - **Default for class A.**
- **Positive**: the plunger fits into the cavity like a piston and nothing escapes. The charge must be exact, because the mold will not close on an overcharge [S7][S8].
  - **Fits**: deep-draw parts (depth greater than diameter), high-bulk materials, maximum density, expensive materials [S7][S8].
  - **Avoid for liquids.** Air has nowhere to go, and liquid leaks past the plunger clearance [K].
  - Plunger-to-cavity clearance for thermosets is about 0.025–0.075 mm (0.001–0.003 in) per side [K].
- **Semi-positive** (vertical or horizontal flash): the plunger telescopes into the cavity for the last part of the stroke, and the flash escapes at a controlled ridge. It controls density and critical dimensions. It costs more because it needs a double fit [S7].
  - The horizontal-flash variant is cheaper and more popular for close-tolerance parts [S7].
  - **Fits**: rubber or thermoset parts that need thickness and density control, and deep rubber parts.
- **Landed positive**: like positive, but the press force is taken by lands, so part height is set by the lands and density by the side clearance [S7]. The excess forms a prescribed tab that is trimmed [S8].
  - **Fits**: accurate thickness with an accurate charge, and parts whose thickness is a critical (closure) dimension.

**Rule 1.1**: Always put **lid stops (landing pads) outside the overflow** on flash and semi-positive molds. The stops carry the press force, so the cutoff land is not hammered and the closed height is repeatable [K]. Without stops, the flash land carries the whole press force. An aluminium land then peens over within a few hundred cycles [K].

**Rule 1.2** (which type to propose):
- Class A: flash type with stops.
- Class B shallow parts: flash type with overflow grooves.
- Class B parts deeper than their width: semi-positive.
- Tight closure dimension with a known charge weight: landed positive.

**Rule 1.3**: A three-plate mold (with a middle cavity plate) solves two problems: parts too delicate or soft to pull from a two-plate mold, and air trapped under the charge. The part is pushed out by hand or blown out with air [S1].

## 2. Parting line (PL) selection

1. **Hard constraint**: no PL, flash, vent, pour sprue or overflow touches a surface the drawing marks as critical or cosmetic. In the RMA flash notation these surfaces are "T.000" (no flash allowed) [S13]. The tool's overlay shows these surfaces. Before choosing the PL, the agent must confirm the list of critical surfaces with the user.
2. Put the PL on a **sharp edge** of the part (a silhouette edge in the pull direction). Side view: square edges at the PL. Top view: rounded corners [S1]. If a radius is needed at that edge, relocate the PL rather than splitting the radius [S1].
3. Make the PL **planar and straight** wherever possible. Every step or contour in the PL adds fitting work and flash that has to be removed by hand or by machining [S9].
4. Use **straight pull** only: one opening direction for a 3-axis-machinable mold. An undercut needs a side core or a loose piece, which is costly [S1][S-Fictiv].
   - Soft elastomers (below about 50 Shore A) can be stripped over small undercuts.
   - Very soft (below 30 A) or very hard (above 80 A) compounds make complex geometry and deep undercuts hard to produce [S1].
5. Move the PL to the middle of the part if a PL at the top or bottom is objectionable [S1].
6. Choose the PL so that **critical dimensions are "fixed" dimensions** (inside one mold piece), not "closure" dimensions (across the PL).
   - Closure dimensions carry the flash thickness and the closing variation. Their tolerances are looser: for example, ±0.127 mm closure against ±0.102 mm fixed for dimensions up to 12.7 mm [S1]. Dimensions across the PL add ±0.05–0.15 mm [S5].
7. O-rings and round sections: the PL goes at 0°/180° of the cross-section, or 30°/210° for special parts [S3].
8. Keep the PL away from **thin feather edges**. Square them off with a flat of at least 0.25 mm (0.010 in) [S1].
9. For liquid pours, the PL is also the **seal line**. It must be continuous, flat and lapped. Put it where the liquid level is lowest during the pour (see section 8).

**Rule 2.1**: Score each candidate PL by:
- critical-surface hits (veto)
- undercut volume (veto, or a side core)
- PL length and non-planarity
- closure dimensions it creates
- air-trap count in the pour orientation it forces
- machinability of the resulting pieces

Present the best two candidates to the user on the model.

## 3. Draft

- **Rubber**: soft (below 50 Shore A) 0.1° minimum. Hard (above 70 Shore A) 0.5° minimum [S2]. Elastic parts strip easily, so many rubber molds run with 0–1° [K].
- **Cast PU elastomer** (60A–90A): 0.5–1° by default. 95A, Shore D and rigid resins: 1–2°, with at least 0.5° [K].
  - Ribs 0.25–0.5° per side, equal on both sides [S30].
  - The vacuum-casting guidance of 1–2° [S-makerverse] and 3–5° (to protect silicone tools) [S30] is for soft tools. Metal tools can use less on elastomers but need at least as much on rigid resins.
- **Rigid thermosets** (epoxy, BMC, phenolic, rigid PU): 1–2°, minimum 0.5° [K]. Use 3°+ on textured faces [S2], plus about 1° per 0.025 mm (0.001 in) of texture depth (the injection-molding convention) [K].
- **Machining note**: draft on mold walls is cut with tapered end mills or by 3D surfacing. Give drafts in whole or half degrees to match standard tapered cutters (0.5°, 1°, 1.5°, 2°, 3°) [K].

## 4. Shrinkage and cavity sizing

- The cavity is sized larger than the part: cavity = part / (1 − s), or about part × (1 + s) [S3].
- Typical linear shrink (the TDS or the user's value in the editable field overrides these):

| Material | Shrink | Source |
|---|---|---|
| NBR | 1.5–3.5 % (sulfur-cured NBR example 2.4 %, peroxide-cured 3.02 %) | [S11][S12] |
| EPDM | 1.9–3.5 % (case study used 2.1 %) | [S11][S2] |
| CR (neoprene) | 1.0–3.0 % | [S11] |
| Silicone HCR | 2.0–5.0 % (peroxide VMQ 2.5–4.5 %, addition-cure 1.5–2.5 %) | [S11][S3] |
| FKM | 2.0–4.5 % | [S11] |
| NR | 1.5–2.5 % | [S2] |
| Millable PU | 1.5–2.5 % | [S10] |
| Cast PU elastomer (hot-cast) | about 1–2 % typical. Published range 0.5–4 % depending on system. | [S-PU patents][K]: get it from the TDS |
| RT casting resin | Smooth-Cast 300: 1.0 % (0.01 in/in). Smooth-Cast 305: 0.65 %. | [S18] |
| General "rubber" when nothing is known | 1.5–1.8 % | [S4] |

- **Rule 4.1** [C]: for an aluminium mold running hot, correct the shrink. Supplier shrink values assume a steel mold. Aluminium (about 23.6 µm/m·K) grows about 11.6 µm/m·K more than steel (about 12.0) [S26].
  - At a 150 K rise (a 170 °C rubber cure), the aluminium cavity is about 0.17 % larger than a steel cavity at temperature.
  - Effective shrink seen in an aluminium mold ≈ s_supplier − 0.17 %. At 100 °C (ΔT 80 K) the correction is about 0.09 %.
  - Apply it when tolerances are tighter than about ±0.3 %.
- Cure state, post-cure and filler level change shrink. Harder compounds shrink less [S3]. Post-cure (for example FKM at 220 °C for 24 h) adds more shrink [S3]. Record the shrink value used on the mold and on the setup sheet so a re-cut can correct it.

## 5. Flash land (cutoff, pinch-off)

- **Rubber** (class B):
  - Land width 1.5–3 mm.
  - Land clearance at closure 0.02–0.05 mm.
  - Flash target below 0.1 mm [S2], ideally about 0.05 mm [S4].
  - A wider land restricts flow near closure and raises back-pressure. That is good for low-viscosity compounds. High-viscosity compounds want a narrow land with deep flash grooves [S4].
  - A small protruding cavity "lip" makes deflashing easier [S6].
- **Tear-trim pinch-off**: a sharp edge with a slight gap just outside the part contour, so the flash is torn off by hand. An overflow groove sits further out [S3].
  - Normal tear-trim flash class is T.063 (1.6 mm extension). T.032 (0.8 mm) needs die, machine, tumble or hand trimming [S13].
- **Liquid PU or resin** (class A) [K]:
  - Land 3–6 mm wide, lapped flat.
  - Target closed gap 0.01–0.025 mm (0.0004–0.001 in).
  - Lid flatness and the stop height must hold this across the whole land. Mold deflection under the press force must stay below the gap (see 13.2).
  - Low-viscosity resins (below about 500 cP) need the narrow end of the gap range and the wide end of the land range.
- **Rule 5.1**: Flash thickness adds to every closure dimension. Pick the land so the flash target is no more than a third of the closure tolerance [K].

## 6. Overflow (spew) grooves

- **Purpose**: give the excess charge somewhere to go without propping the mold open, and give air an exit [S6]. Compression molds deliberately overfill into the grooves [S1].
- **Distance from cavity**: the land width, so 1.5–3 mm for rubber [S2]. A trapezoidal groove about 1 mm from the cavity cleared the trapped residue in a T-seal case study (98 % pass rate after the change) [S-T-seal].
- **Section** [K]:
  - Small rubber parts: semicircle R1–R1.5 mm, or a trapezoid 2–3 mm wide × 1–1.5 mm deep.
  - Large parts or a large excess: 3–6 mm wide × 1.5–3 mm deep.
  - Liquid pours: 4–8 mm wide × 2–4 mm deep, so it also works as a reservoir.
- **Volume** [K]: groove volume ≥ 1.5 × the expected excess, where excess = cavity volume × charge excess %. Use ≥ 2× for liquids because the pour volume is less repeatable.
- **Exits**: add **radial grooves** from the overflow to the mold exterior (for example 3–6 mm wide × 1 mm deep, every 50–100 mm) so high-viscosity compounds and air can leave [S4][K]. For liquids, the radial exits double as vents and must leave the mold at or above the PL.
- **Viscosity rule**: low viscosity calls for a wide land plus a modest groove. High viscosity calls for a narrow land plus a deep groove and radial exits [S4].
- Keep the groove continuous around the cavity unless a critical surface forbids it. Never route a groove across a critical surface.

## 7. Charge (shot) amount

- **Rubber preform**: 2–10 % heavier than the part [S4], or 3–8 % (×1.03–1.08) [S2].
  - Preform mass tolerance about ±2 % [S5].
  - About 15 % overfill gave about 12 % flash in one case study [S5].
  - Flash of 5–15 % of part mass is typical [S5].
  - Use an elliptical preform cut to length so air escapes as the mold closes [S4].
- **Positive and landed-positive molds**: exact charge (weighed) [S7].
- **Liquid pour** [K]:
  - Charge = cavity volume × density × (1 + 3–8 %), plus the pour-cup or sprue volume.
  - Pour time for all cavities must be no more than about 50 % of pot life.
  - When pour time exceeds that, split the shot into batches, or pour the cavities in a defined sequence and record it on the setup sheet.
- **Computation**: charge per cavity = part volume (from the model, after shrink scaling) × density (from the material lookup). Show the result in g and lb on the setup sheet, with a ± tolerance.

## 8. Vents, bleeders and air traps

1. **Find air traps in the pour or charge orientation.** Any closed local high point of the cavity, relative to gravity during the pour or the closing direction, traps air. Examples: the tops of bosses on the lid side, inverted pockets, the undersides of ribs.
   - Liquid fills from the bottom up. Air collects at the last-filled high points.
   - Rubber traps air under the charge [S1] and where flow fronts meet.
2. **Put a vent at every high point and at every last-to-fill point** [S18][S31][S3].
   - Vents exit **above** the pour level so the resin does not leak out of them [S31-Edinburgh].
   - Liquid casting: vents at the top, pour low (underpour) where possible so bubbles rise into the vents [S31].
   - A cavity is full when resin shows at the top of every vent [S18].
3. **Vent sizes**:
   - Rubber: vent grooves 0.1–0.2 mm deep, venting each cavity to atmosphere [S5]. Tire molds use 0.1–0.6 mm vent slots [S-vent patents].
   - Liquid PU and resin [K]: flat PL vents 0.025–0.05 mm deep for below 500 cP, 0.05–0.10 mm for 500–5,000 cP, 0.10–0.20 mm for above 5,000 cP. Each is 3–6 mm wide, with a 1.5–3 mm land, then relieved to at least 0.5 mm deep to the outside.
   - Round bleeder or riser holes 0.25–1.5 mm (0.010–0.060 in) diameter for urethane at 10,000–50,000 cP [S-urethane vent patent]. Smaller holes for lower viscosity, or a riser tube that is cut off.
   - Foam-mold vents (for comparison only) are 0.13–0.51 mm [S24].
   - Flash can never be thicker than a vent that is the only leak path. Check vent depth against flash in tryout [S24].
4. **Bumping (breathing)** for rubber and thermosets: open the press slightly once or more just before final closure to release air and gases [S3][S7]. Put the bump schedule on the setup sheet. Vacuum molding is the alternative for difficult parts [S3].
5. Add fillet radii at corners so air can escape [S9]. Avoid recesses that are not aligned with the PL [S9].
6. **Tilted pour** [K]: if a high point cannot be vented without hitting a critical surface, tilt the mold 5–15° during the pour so the trap becomes a vented corner, and add a tilt wedge or foot to the base. Record the tilt on the setup sheet.
7. Degas liquid systems before the pour, for example under 29 inHg vacuum [S18]. About 3 min is typical for batches up to 1 kg [S-Triathane]. A low-viscosity mix entraps fewer bubbles [S-LANXESS].

## 9. Pour and charge strategy

- **Pour into the base only** [K]: the default for class A. The base holds the liquid. The lid is a flat or shallow form that closes onto the land and pushes the excess into the overflow.
  - Lid-side features must be shallow or vented, or air is trapped under them.
- **Pour into both halves** [K]: when the lid has deep features. Pre-fill the lid features (by partial pour or brush coat) and invert the lid only after gel, or close with the lid features vented to the top.
  - This needs a lid with its own seal and vents. Mark which half is poured first, and the gel wait.
- **Pour into the lid** [K]: when the deep side of the part must be up. The lid becomes the "base" on the press, so the handling rules swap (heavier half on the bottom).
- **Pour angle** [K]: pour slowly, in a thin stream from 150–300 mm height, into the lowest corner of the cavity, letting the liquid flow across. Never pour onto a high detail [S18][S31-forum].
  - Pour into one spot per cavity so the flow front pushes air ahead of it toward the vents.
  - For hot-cast PU, preheat the mold before the pour: preheating helps flow and demold time [S-Triathane]. An example process preheats the aluminium mold at about 100 °C [S28].
- **Rubber placement**: the charge goes in the lower half for easy loading [S7]. Put it where flow paths to all extremities are about equal. Special preform shapes are needed for parts with uneven flow lengths [S7].

## 10. Press, pressure and forces

- **Rubber tonnage**: F = p × A_projected. p is 8–12 MPa for rubber compression [S4]. ASTM D3182 cites 700–3,000 psi (4.8–20.7 MPa) [S5].
- **Thermoset tonnage**: p = 13.8–20.7 MPa (2,000–3,000 psi) on the projected area **plus the land area** of each cavity, plus 4.1 MPa (600 psi) per 25 mm of depth beyond 25 mm. Preheated material can cut this by 50 % or more [S7].
- **Liquid pour**: the cavity pressure is small. The press force F_press goes into the stops and the land. Size the stops so their bearing stress ≤ the allowable stress at temperature (see 14). Bearing stress on stops = F_press / A_stops.
- **Rule 10.1** [K]: the tool must check press capacity against the required force × 1.25. If the user's "press pressure" input is the hydraulic gauge pressure, ask for the ram area (or the press tonnage rating) to convert it to force. Then compare:
  - cavity pressure (rubber)
  - stop bearing stress (all classes)
- **Rule 10.2** [K]: put the press load centre (the centroid of the projected cavity plus land area) on the press centreline within ±10 % of the platen size. Off-centre loads tilt the platen and cause uneven flash.
- **Rule 10.3** [K]: the mold footprint, including handles and overflow exits, must fit inside the heated platen area minus about 25 mm per side (the platen edges run cooler). The closed mold height must fit the press daylight with room to load the charge.

## 11. Lid alignment and landing

- **Pins and bushings**: guide (leader) pins run in bushings with a close sliding fit, about 0.04–0.06 mm (0.0015–0.0025 in) total clearance [S15]. Steel pins run in steel bushings. Never run a steel pin directly in aluminium (it wears and galls) [K].
- **Engagement first**: pins engage their bushings at least 12 mm (½ in) before any core, plug or lid feature enters the cavity or reaches a fragile edge [S15]. Pins should protect the cores if the mold is misaligned [S15].
- **Count and position** [K]:
  - 2 pins minimum for small molds, 4 for lids larger than about 300 mm or heavier than the one-person lift limit.
  - Place them at diagonally opposite corners, as far apart as possible, outside the overflow.
  - Offset one pin by at least 5 mm, or use a different pin diameter, so the lid cannot go on rotated 180°.
- **Thermal growth** [S16][C]:
  - Aluminium plates with steel pins and bushings grow more than the pins at temperature. This misaligns them and can damage the mold [S16].
  - For a steel lid on an aluminium base (or the reverse), the mismatch across 300 mm of pin spacing at a 150 K rise is (23.6 − 12.0)e-6 × 150 × 300 ≈ **0.52 mm** [C]. Pins will bind.
  - Rule: lid and base of the same material, heated to the same temperature. With mixed materials, or a large mold: use one round pin plus one diamond (relieved) pin, or a round pin plus a slot aligned on the line between the pins. Then let a central tapered lock or side interlocks set the cavity registration.
  - Leader pins can also misalign when the two halves run at different temperatures [S-PT].
- **Fine registration** [K]: when cavity mismatch must be below about 0.05 mm, add tapered (conical or wedge) locks or side interlocks near the cavities. Pins only rough-align the lid; the locks set the final position.
- **Keyed pattern (low cost)** [K]: matching truncated pyramids or cones (male on the base, female on the lid, 10–15° taper, asymmetric layout) machined into the halves outside the overflow. Good for low-cycle aluminium molds. Add hardened inserts where the cycle life is high.
- **Lid stops and landing** [K]:
  - At least 3 stops (4 for rectangular molds), placed symmetrically about the cavity centroid and outside the overflow.
  - Hardened steel pads (40–50 HRC), replaceable and shimmable, ground to set the closed height.
  - Total stop area must keep bearing stress ≤ 1/3 of the yield at temperature of the weaker material.
  - Liquid molds may land metal-to-metal on the whole lapped land instead. Then the land plus the stops share the load.

## 12. Multi-piece base on the press work area

The base is split into pieces when it exceeds the per-plate weight or bounding-box limit.

1. **Locate every piece twice** [K]:
   - Each piece gets 2 dowels (one round, one diamond or slot) into a common **subplate** or carrier, or into its neighbour.
   - Hardened dowels with steel bushings or a press fit in steel. In aluminium, use pressed steel bushings.
   - Keep the dowel spacing as large as the piece allows.
2. **Bolting** [K]:
   - Socket-head cap screws (class 12.9 / ASTM A574) at a spacing of 4–6 × d along joints that hold cavity pressure.
   - In aluminium, thread into steel helical or key-locking inserts (see 14.4). Engagement is about 1.5 × d [S29][K].
   - Size the joint bolts so the preload is at least 2× the side force F_side = p_cavity × (projected side area of the cavity walls carried by that joint) [C].
   - Torque values go on the setup sheet.
3. **Joints are seal lines** [K]: a joint between base pieces that the liquid or compound can reach must either be a designed PL (drafted, ground, gap ≤ 0.01–0.02 mm for liquids, ≤ 0.05 mm for rubber) or lie outside the overflow. Never put a joint through a critical surface.
4. **Splits go in dead steel** [K]: prefer a frame (chase) with cavity inserts bolted into pockets over slicing a cavity in two. Leave at least 15 mm between the cavity and any joint.
5. **Heat transfer** [K]: each piece must sit flat on the platen (flatness ≤ 0.05 mm), with no insulating gaps, and each needs its own thermocouple check (see 15). Pieces on a subplate: the subplate must be ground parallel within 0.02 mm.
6. **Holding on the press** [K]: give the base clamp ledges or slots (for example 15–20 mm wide × 15 mm thick ledges on two opposite sides) for strap clamps, or bolt it to a carrier plate with handles. Many heat presses have no T-slots: ask.
7. **Assembly order**: number the pieces, engrave matching IDs across each joint, and give the assembly order and torques on the setup sheet.

## 13. Plate splitting under weight and bounding-box limits; structural sizing

### 13.1 Weight and envelope

- Mass = volume × density: aluminium 2.70 g/cm³ (0.0975 lb/in³), steel 7.85 g/cm³ (0.284 lb/in³) [K].
- Aluminium weighs about one third as much as steel [S16-why].
- Bounding box per plate = machine travel limit minus the fixture allowance. The user enters this, but warn if they leave no margin. Use a default allowance of 25 mm per axis [K].
- **Splitting algorithm** (proposed order of preference) [K]:
  1. Fit within limits by using aluminium, or a thinner plate if the structure allows.
  2. Use a frame plus cavity inserts.
  3. Split along straight planar lines between cavity rows, in dead steel, at least 15 mm from cavities.
  4. Split each cavity at an existing PL or along a feature edge (only with user approval).
  - Every resulting piece must:
    - weigh no more than the per-plate limit, and fit the bounding box
    - be machinable in 1–2 setups on 3 axes (no undercut from the machining direction)
    - have room for 2 dowels, its bolts, 2 pry slots and its marking.
- **Total base weight ≤ the base limit, and lid weight ≤ the lid limit.** The lid is lifted every cycle, so it gets the strictest limit (see 17).

### 13.2 Structural rules for the plate thickness and bolt calculation

This section defines the criteria. The numbers come from the user's simulation-validated calculator.

1. **Stiffness governs, not strength** [K]: under the press force (and the cavity pressure for rubber), deflection at the land must be ≤ 1/2 of the flash-gap target. That is ≤ 0.005–0.012 mm for liquids and ≤ 0.02 mm for rubber.
   - Treat the lid as a plate loaded by the platen and supported on the stops and land, with Roark-type formulas, or by FEA.
2. **Strength** [K]: von Mises stress ≤ yield at temperature / 2. For high cycle life, also stay below the fatigue strength at the target cycles divided by 1.5.
   - 7075-T6 fatigue strength is about 159 MPa at 5×10⁸ cycles. 6061-T6 is about 97 MPa (ASM handbook values) [K].
3. **Stops and lands**: bearing stress ≤ 1/3 of the yield at temperature [K].
4. **Bolts**: preload ≥ 2 × the separating force on the joint. Bolt stress ≤ 0.7 × proof. Thread stripping must be checked in aluminium (or use inserts) [K].
5. **Rubber cavity walls**: wall between cavity and edge or overflow t ≥ the value that keeps wall bending stress below the allowable under p_cavity. A practical floor is 10 mm for steel and 15 mm for aluminium [K].
6. Feed every check into the simulator hook (section 22). Record the load case, the material at temperature, the result and the margin.

## 14. Mold material, inserts and cycle life

### 14.1 Choice

- **Aluminium**:
  - Prototypes and low-to-mid volume. Fast machining (about 4× faster than mold steel, 2–3× faster than P20) and fast heating (heat moves up to 7× faster than in steel) [S16-why].
  - Softer than steel, so clamping pressure should be as low as possible [S6].
  - Use 7075-T6 ("aircraft" grade). It machines cleanly and can be welded and EDM'd [S16-why]. It wears better than 6061 or cast aluminium [S16].
  - Cast tooling plate (MIC-6, ATP-5) for flat, stress-free lids [K].
- **Steel**:
  - Production rubber tools: hot-rolled steel. Avoid cold-rolled steel, which warps at mold temperature [S6].
  - P20 for prototype rubber tools under about 5,000 parts. Hardened H13 for 5,000–500,000. Stainless S136 (420 type) for abrasive or medical work [S2].
  - Tool steel hardened to about 58–60 HRC for long-life rubber cavities [S4].
- **Cycle-life reference**:
  - Injection molds: QC-10 aluminium averages 10,000–25,000 shots, P20 steel 50,000–100,000 [S17].
  - Well-maintained aluminium injection tools have reached 500,000+ [S16-why].
  - Low-pressure PU pours are much gentler than injection molding. The real limits are land and edge wear, pin and bushing wear, thread wear and handling damage (prying with screwdrivers) [S16-why][K].

### 14.2 Map the "cycle life" input to a design [K]

| Cycle life | Construction |
|---|---|
| ≤ 500 | Aluminium throughout. Keyed alignment allowed. Tapped aluminium threads acceptable for rarely used holes. |
| 500–10,000 | Aluminium with steel bushings and pins, hardened stops, thread inserts in every repeatedly used hole, steel inserts at fragile or worn features. |
| 10,000–100,000 | Steel for the cavity areas, or aluminium with hard coat plus replaceable steel cutoff inserts. Hardened stops and locks. |
| > 100,000 | Hardened steel (H13 or S136 type). Replaceable wear parts throughout. |

### 14.3 Aluminium temperature limit [K]

- 6061-T6 and 7075-T6 get their strength from artificial ageing at about 120–177 °C. Long service at or above that range over-ages them and lowers their strength and hardness.
- Rule: aluminium molds run continuously at about 150 °C or below. Above that (most rubber cures at 160–200 °C), recommend steel. If the user insists, use steel cavity inserts and stops, and derate the aluminium yield to its elevated-temperature value in the structural calc.
- High-strength aluminium starts to anneal at injection temperatures around 260 °C (500 °F) [S16].

### 14.4 Steel inserts in aluminium molds

Use them for fragile features [K]:
- core pins or bosses with height > 2 × diameter, or diameter < 3 mm (Minnesota Rubber limits holes to height ≤ 2 × diameter and diameter ≥ 1.27 mm because cavity pressure bends pins [S1])
- mold walls thinner than 3 mm between features
- sharp cutoff or pinch-off edges that carry closing load
- PL cutoff areas that wear
- areas exposed to pry or cleaning abuse

**Fit at temperature** [S26][C]: aluminium loses grip on a steel insert as it heats. For a 25 mm insert at a 150 K rise, the diametral interference falls by about 11.6e-6 × 150 × 25 ≈ 0.044 mm.
- Rule: retain inserts mechanically, with a shoulder from below or a bolt or clamp. A press fit only locates the insert.
- Keep the room-temperature fit as a light locational fit. Aluminium also cracks under heavy interference: use 30–50 % less interference than for steel [S-thermal search].
- Leave the PL face of the insert flush to +0.01 mm and grind it with the plate.

### 14.5 Threaded inserts and bushings

- Every tapped hole in aluminium that is reused gets a steel helical (Helicoil) or key-locking insert [K]:
  - lid bolts, jack screws, eyebolts and hoist rings, clamp bolts, insert retaining screws
  - insert length 1.5–2 × d [S29][K]
- Every guide-pin and dowel hole in aluminium that sees repeated engagement gets a hardened steel bushing [K].

### 14.6 Coatings

- Hard anodize on aluminium adds about 0.1 mm (0.004 in), about half of it above the original surface. Allow for it when machining. It chips at 90° and sharper corners [S16-why].
- PTFE-nickel coatings can be stripped and reapplied [S16-why].
- For rubber: electroless nickel only with peroxide-cured materials, not sulfur-cured. Film is about 4–6 µm. Hard chrome is cheap for simple shapes but poor for deep engraving. Teflon composite gives the best release [S3].

## 15. Heating, thermocouples and heat-up

- **Heat source**: heat-press molds are heated by the platens. Large molds may be gun-drilled for hot oil or fitted with cartridge heaters [S27][S7].
- **Uniformity**: more than 5 °C variation across the platen causes undercure [S5]. Target ±3 °C across the cavities [K].
- **Thermocouple wells** [K]:
  - At least one in the lid and one in the base, plus one per separate base piece.
  - 3.2 mm (1/8 in) or 6 mm drilled holes, ending 6–12 mm from the cavity surface at the thickest part section.
  - Enter from a side face. Do not put them in a lifting or clamping zone.
  - Engrave a label at each well ("TC-L1", "TC-B2").
- **Heat-up** [C]:
  - Thermal diffusivity: aluminium 6061 ≈ 167 / (2,700 × 896) ≈ 6.9×10⁻⁵ m²/s. P20 steel ≈ 29 / (7,850 × 460) ≈ 8×10⁻⁶ m²/s. Aluminium soaks through about 8× faster for the same thickness.
  - Soak time scales with thickness² / diffusivity. Doubling plate thickness quadruples the soak time.
  - The setup sheet gives a preheat time to be verified with the TCs (for example "until TC-L1 and TC-B1 are within 3 °C of set point, then 10 min").
- **Cure time**: halves for about every 10 °C (18 °F) rise in temperature [S10]. Use this to convert supplier cure times to the user's press temperature, within the material's limits (reversion when too hot [S10]).
- **Moisture** [K]: PU is moisture-sensitive. Molds must be dry and preheated above 100 °C or blown dry. Do not use water-cooled channels in PU pour molds.

## 16. Demolding

- **Pry slots** on every separable plate [S25]:
  - Typically 20–30 mm wide × 3–5 mm deep, at two opposite corners of each PL and each removable insert [K].
  - Hardened, or with a steel insert in aluminium molds, to stop damage from prying [K].
  - Never pry on the land or the PL edge [S16-why].
- **Jack screws** [K]: 2–4 tapped holes (M8–M12) through the lid, symmetric, pushing on hardened pads in the base, so the lid lifts square without prying. Add extraction tapped holes (M5–M8) in every removable insert.
- **Knockouts** [K]: through-holes from the base underside for knockout pins or push rods, either to the part (rigid parts) or under a removable cavity insert that is pushed up with the part.
  - Pins ≥ 6 mm diameter on elastomers so they don't punch the part.
  - The press usually has no ejector, so plan an arbor-press or bench knockout fixture and list it on the setup sheet.
  - Automatic compression molds use top and bottom knockouts [S7].
- **Air poppets / air ejection** [K]: for soft PU or rubber cups and deep cavities, add an air valve (poppet) at the bottom of deep pockets. Shop air at about 6 bar breaks the vacuum seal.
  - Three-plate rubber molds blow parts out with air [S1].
  - The poppet face must sit flush and be on a non-critical surface (it leaves a witness ring).
- **Release** [S10][S27][S19]: release must cover every contact surface evenly [S27].
  - Too much release causes poor flow and knit lines in rubber [S10], and gloss that transfers to later castings [S19].
  - Prefer a semi-permanent release or a PTFE-nickel coating for production [K].

## 17. Lifting and handling (every mold piece and the lid)

- **Limits**:
  - NIOSH Revised Lifting Equation load constant: **23 kg (51 lb)**, the maximum under ideal conditions. Each task factor reduces it. The lifting index (weight / RWL) should be ≤ 1 [S20].
  - UK HSE guideline weights: 25 kg men / 16 kg women, held close to the body at waist height. Much less held at arm's length or from floor level [S21].
  - Hot, two-handed lifts at press height with gloves are not ideal. Default one-person limit: **15–23 kg (35–50 lb)**. The lid limit is the user's "max lid weight" input [K].
- **Handles** [K]:
  - Above about 5 kg, every piece gets 2 handles on opposite sides, on the line through the centre of gravity.
  - Handles are bolted into steel thread inserts.
  - They must sit outside the platen footprint or below the platen plane, so the press cannot crush them.
  - Heat-insulated or long enough that grips stay below about 50 °C.
- **Eyebolt and hoist-ring holes** [S22][K]: every piece over the one-person limit gets tapped lifting holes on the top face, over the centre of gravity (2 or 4 holes).
  - DIN 580 C15 eyebolt axial working load limit: M8 140 kg, M10 230 kg, M12 340 kg, M16 700 kg, M20 1,200 kg [S22]. Grade 80 versions carry more. Angled or side loads lower the rating [S22].
  - Prefer swivel hoist rings.
  - Thread engagement ≥ 1.5 × d in steel and ≥ 2 × d in aluminium, or use steel inserts [K].
  - Engrave the weight of each piece on it in kg and lb [K].
- **Lid above the limit**: offer a hinged or counterbalanced lid, a lid hoist, a lid split, or a lighter material.

## 18. Surface finish and release

- **SPI grades** [S23]:

| Grade | Method | Ra |
|---|---|---|
| A-1 | 6000-grit diamond | 0.012–0.025 µm |
| A-2 | 3000-grit diamond | 0.025–0.05 µm |
| B-1 | 600 paper | ~0.05–0.15 µm |
| B-2 | 400 paper | ~0.10–0.30 µm |
| C-1 | 600 stone | ~0.35–1.5 µm |
| D-1 | glass bead | ~0.8–1.0 µm (sources disagree) |

- **Rubber cavity finishes** [S2]: mirror Ra 0.2–0.4 µm, standard polish 0.8–1.6 µm, semi-matte or as-machined 3.2–6.3 µm.
- **Cast PU** reproduces any texture or machined finish [S9]. A light bead blast (D-1/D-2) releases better and hides flow marks on elastomers. Clear resins need A or B finishes [K].
- **Default** [K]: polish only to the finish the drawing asks for. Higher polish costs more and may release worse for elastomers (vacuum sticking). Specify the finish per face group on the drawing.

## 19. Machinability (each mold piece)

- **Internal vertical corner radius** ≥ 1/3 of the cavity depth. Example: 12 mm deep → R ≥ 5 mm, so an 8 mm cutter runs [S14].
  - Make the corner radius ≥ 1.1 × the cutter radius so the cutter does not engage the full corner [K].
  - Floor radius 0.5–1 mm or 0 [S14].
- **Depth**: cavity depth ≤ 4× the cutter diameter (2–3× is best). A 12 mm end mill reaches about 25 mm [S14]. Deeper needs long-reach tools, EDM or a split.
- **Minimum metal wall**: > 0.8 mm (0.5 mm possible) [S14]. Mold walls that carry pressure need much more (see 13.2).
- **Holes**: depth ≤ 4 × d (up to 10 × d at higher cost) [S14]. Tapped depth ≤ 3 × d, with an unthreaded relief ≥ 0.5 × d at the bottom of blind holes [S14].
- **Small features**: aspect ratio ≤ 4:1 [S14].
- **Engraved text**: sans-serif, ≥ 20 pt [S14]. Practically ≥ 5 mm high and 0.2–0.5 mm deep [K].
- **Stock**: blanks ≥ 3 mm larger per side than the finished piece. Design to ≥ 3 mm under a standard blank size [S14].
  - Hardened steel: rough to within 0.3 mm, then finish by EDM or grinding to about ±0.005 mm [S2].
  - Use standard plate thicknesses [K].
- **3-axis rule** [K]: every piece must be fully machinable from at most 2 opposite setups along the pull axis, with no undercut from either. Otherwise split it or make the feature an insert.
- **Default tolerance** when none is given: ±0.125 mm [S14]. Land, stop and PL faces get flatness and parallelism tolerances (for example 0.01–0.02 mm) [K].

## 20. Cavity spacing and multi-cavity layout

- **Minimum steel between cavities** [K]: ≥ 2 × land width + overflow groove width + ≥ 3 mm. In practice ≥ 10–15 mm in aluminium and ≥ 8–12 mm in steel, more for high cavity pressure.
- **Cavity to plate edge** ≥ 20–25 mm, plus room for pins, stops, bolts, pry slots and TC wells [K].
- **Layout**:
  - Staggered (hexagonal) nesting fits more round cavities than a square grid [S6].
  - Balance the layout about the press centre (Rule 10.2) and keep it symmetric for uniform heat.
  - Keep the same flow and vent geometry per cavity so the cavities fill equally.
  - Number the cavities 1..n in a reading order that is engraved on the plate [K].
- **Cavity count**: limited by the press force (Σ projected area × p ≤ 0.8 × press capacity), the platen heated area, the weight and bounding-box limits, and the pour time (≤ ½ pot life for liquids) [S7][K].
- **Prototype rule**: single-cavity two-part molds are typical up to about 200 pieces; 2–9 cavities are common [S1].

## 21. Marking (engraving)

All marking goes on non-cavity faces unless the drawing allows a mark on the part [K]:

- **Mold**: mold ID and revision, part number, part material and shrink used, mold material.
- **Each piece**: weight in kg and lb, and "this side up".
- **Matching**: IDs across every joint (A1↔A1), dowel and pin IDs.
- **Cavities**: numbers next to each cavity on the PL face, outside the land. Inside the cavity only if the drawing allows a part marking.
- **Heating**: TC well labels.
- **Lifting**: lift points ("LIFT", with the hole size and working load limit).
- **Date code**: an optional date-code wheel insert (a rotatable arrow plug) in the cavity, only where the drawing permits.
- **Size**: characters ≥ 5 mm high and 0.2–0.5 mm deep, sans-serif [S14][K].

## 22. Hooks for the later MCP simulators (machinability and process/demolding)

Design so another tool can check the result without re-deriving it [K]:

- **Body names and roles**: name every solid body by role and ID, for example `BASE-1`, `BASE-2`, `LID`, `INSERT-C3-PIN1`, `STOP-1`, `PART-C1`. Put the role, material, mass and cavity number in body custom properties or the cut-list.
- Store the **design parameter set** as JSON in a file next to the part (and as custom properties):
  - process class, materials and their lookup values, shrink applied
  - press force, temperature and pressure
  - charge per cavity, pour orientation and tilt, PL definition
  - vent, overflow and land dimensions
  - stop area, pin layout
  - per-piece mass and bounding box
  - structural calc inputs and results with margins
  - cycle life and the construction tier chosen
- **Named reference geometry**: pour points, vent points, high points and air traps (sketch points), the PL as a named sketch or split line, the critical-surface face list (persistent IDs), the lift points and the centre of gravity of each piece.
- **Per-piece export**: STEP or Parasolid of each machined piece, in its machining orientation, with the 3-axis pull direction. That is the machinability simulator's input.
- **Exports for the process and demolding simulator**: the cavity and part body, pull direction, PL, vents, overflow, material process data, temperatures, and the demold sequence.
- **Check status**: a field per check (`not run / pass / fail / waived`) that each simulator updates, and that the setup sheet shows.

## 23. Setup sheet (for the operator)

Include all of the following [K, with items from S5, S7, S10]:

1. Mold ID, revision, part number, a picture of the assembled mold and the cavity map.
2. Piece list with weight (kg/lb), lift method (one person, two people, hoist) and lift-point sizes.
3. Assembly order, dowel and pin IDs, bolt sizes and torques, stop shims.
4. Press settings:
   - platen temperature set point
   - the force or gauge pressure (with the conversion)
   - closed height and daylight
   - load-centre mark
5. Preheat: time and the TC readings needed before the pour or charge.
6. Material:
   - name and lot fields, mix ratio, material and mix temperatures, degas time and vacuum, pot life
   - charge weight per cavity and total, with tolerance
   - preform shape and size (rubber)
7. Pour or charge sequence:
   - which half, the cavity order, pour point per cavity, mold tilt
   - wait-for-gel before closing (liquids)
   - bump schedule (rubber)
8. Cure time at temperature, plus the halving-per-10-°C note for adjustments.
9. Demold sequence: jack screws, pry slots, knockouts or air, part handling while hot, inserts to remove and reload.
10. Release agent and how often to apply it. Cleaning: blow out the overflow and vents every cycle [S7].
11. Post-cure (for example FKM 220 °C 24 h, silicone 200 °C 4 h [S3]; cast PU per TDS).
12. First-article and inspection checks: critical dimensions, flash limits (T-class), visual defects.
13. Safety: hot surfaces, pinch points, isocyanate or MOCA handling per the material SDS, gloves, lifting.
14. Maintenance: check pin, bushing and stop wear every N cycles. Track the cycle count against the cycle-life target.

## 24. Process data seeds for the part-material lookup tables

These are starting values for the editable fields, and every row needs supplier confirmation. The tool should label the fields "typical – replace with your TDS".

| Family / subtype | Shrink % | Mix or compound viscosity | Mold °C | Pressure | Cure / demold | Notes |
|---|---|---|---|---|---|---|
| Cast PU, RT-cure resin (for example Smooth-Cast 300) | 1.0 | 80 cP | RT; some grades must not exceed 49 °C [S19] | low | demold minutes to hours (Smooth-Cast 300: about 10 min work time / 30 min demold [S-MIT]) | [S18] |
| Cast PU, hot-cast TDI or MDI elastomer | ~1–2 (TDS) | ~500–3,000 cP at process temperature [K] | ~80–115 (often ~100) [S28][K] | low | demold ~15–60 min; post-cure often ~16 h at ~100 °C [K][S28] | moisture-sensitive |
| Millable PU | 1.5–2.5 | Mooney 35–45 (for injection) | 140–165 (sulfur), 149–182 (peroxide) | 8–12 MPa | 7–60 min (sulfur), 2–20 min (peroxide) | [S10] |
| NBR | 1.5–3.5 | – | 150–180 | 8–12 MPa | 3–15 min | [S11][S3][S5] |
| EPDM | 1.9–3.5 | – | 150–180 | 8–12 MPa | 3–15 min | [S11] |
| CR | 1.0–3.0 | – | 150–170 | 8–12 MPa | – | [S11][K] |
| Silicone HCR | 2.0–5.0 | – | 160–200 | 8–12 MPa | 3–10 min, post-cure 200 °C 4 h | [S11][S5][S3] |
| FKM | 2.0–4.5 | – | 170–190 | 8–12 MPa | post-cure 220 °C 24 h | [S11][S3] |
| Thermoset BMC / phenolic | ~0.1–0.8 [K] | bulk factor ≤ 3:1 | 141–204 | 13.8–20.7 MPa (+4.1 MPa per 25 mm of depth over 25 mm) | 30 s–2 min for 3–13 mm sections when preheated | [S7] |

## 25. Questions the agent must ask before building (in this order)

1. Confirm the part material (family and subtype) and its TDS values: shrink, viscosity, pot life, mold temperature range, cure and post-cure. The lookup values are typical only.
2. Confirm the critical surfaces where no PL, flash, vent or sprue may go. Show the overlay, ask "anything else?", and ask the allowed flash class elsewhere (for example T.063 tear trim or T.032).
3. Ask about the press:
   - platen size and heated area
   - daylight (opening) and stroke
   - force rating, or the ram area if the "pressure" input is a gauge pressure
   - T-slots or clamping method
   - whether it has an ejector
   - platen temperature uniformity
4. Ask about the pour or charge method and orientation:
   - which half is filled, by hand or machine
   - batch size versus pot life
   - whether degassing is available
   - whether the part may be poured tilted
5. Confirm the critical dimensions and tolerances, and which of them cross the PL (closure dimensions).
6. Ask about inserts or bonded metal in the part, and how they are held in the cavity.
7. Ask how parts come out (by hand, air, knockout fixture) and the deflash method (tear trim, cut, cryogenic, tumble).
8. Ask for the machine-shop limits (travel, 3- or 5-axis, smallest cutter, EDM available) if the bounding box input does not cover them.
9. Ask for the lift method available (one person, two people, hoist) and who handles the lid every cycle.
10. Ask for the parts per cycle and cycles per day, to sanity-check the cycle life and cavity count against the press time and pot life.
11. Ask for the surface finish per face (SPI) and texture.
12. Ask about the mold marking content and the date code (is the date code allowed on the part?).

## 26. Important things the request did not mention (prioritized)

1. **Press envelope and force basis.** Platen size, heated area, daylight, stroke and tonnage are missing. "Press pressure" is ambiguous: it could be the hydraulic gauge pressure, the pressure on the part, or the force. Without these, no tonnage, stop or fit check is possible (10.1–10.3).
2. **Process class split (liquid pour vs compound compression).** Land, vent, overflow, pressure, alignment and structure rules differ by roughly an order of magnitude between the two (section 0). The family lookup must set the class.
3. **Aluminium at temperature.** Over-ageing above about 150 °C, strength derating, and CTE mismatch with steel pins, bushings and inserts (0.5 mm over 300 mm at 150 K) (11, 14.3, 14.4). It can also invalidate "aluminium with steel inserts" for hot rubber cures.
4. **Charge amount, overflow volume and pot life.** Charge excess %, overflow capacity, and pour time compared with pot life for multi-cavity liquid molds (6, 7, 20).
5. **Lid stops and closed-height control.** These carry the press force, protect the land and set the flash thickness. Plate deflection must stay below the flash gap; that is the real criterion for plate thickness (1.1, 11, 13.2).
6. **Shrink compensation, closure dimensions and flash class.** Tolerances depend on where the PL is (fixed versus closure) and on the RMA T-class or ISO 3302-1 class. The drawing may need these added (2, 4, 5).
7. **Venting and air traps.** These depend on the pour orientation or tilt, and need defined vent depths per viscosity band and vents exiting above the pour level (8). The request mentions air traps but not that the pour orientation drives them.
8. **Demold method per part stiffness.** Jack screws, pry slots, knockouts, air poppets, and a knockout fixture because heat presses usually lack ejectors (16).
9. **Operator safety at temperature.** Hot handling, crush-proof handle placement, eyebolt ratings and engraved weights. Isocyanate or MOCA exposure and moisture control for PU (15, 17, 23).
10. **Heating and thermocouples.** Uniformity within ±5 °C, contact flatness for multi-piece bases, TC well positions, and preheat time. Cure time halves per 10 °C (15).
11. **Alignment strategy.** Pin engagement before cavity entry, an anti-rotation offset, round-plus-diamond pins, and tapered locks for fine registration (11).
12. **Base subplate, joints and clamping.** How base pieces are located (dowels), sealed, bolted and clamped on the press. Joints must not cross cavities or critical surfaces (12).
13. **Release, finish and coatings.** SPI finish per face, release-agent compatibility, plating compatible with the cure system, and hard-anodize growth (14.6, 18).
14. **Maintenance and wear parts.** Replaceable stops, bushings, cutoff inserts and thread inserts. Cleaning access to the overflow and vents. A cycle counter on the setup sheet (14.2, 23).
15. **Inserts or overmolded hardware in the part** (if any): hold-down pins, a minimum 0.5 mm rubber cover over the insert, and shutting off flow on horizontal lands, not vertical faces [S1].
16. **Post-cure effects.** Post-cure changes dimensions and shrink, so it belongs in the shrink choice (4, 24).
17. **Machining-shop capability and stock.** Standard plate sizes, 3-axis pull per piece, minimum cutter and corner radius, EDM need, and stock allowance (19).
18. **Marking and traceability.** Mold ID, revision, cavity numbers, piece weights, matching IDs, date code (21).
19. **Simulator hooks and data persistence.** Named bodies and reference geometry, a JSON parameter set, per-piece exports, and a check-status field (22).
20. **Cost and lead-time targets.** Cost drives the aluminium-versus-steel choice, the cavity count and the insert strategy. Ask for a budget or target if one exists.

## Sources

- [S1] Minnesota Rubber & QMR Plastics, "Designing Rubber Components", Section 2 (2003), via https://allsealsinc.com/02_Designing_Rubber.pdf. Covers corners and edges, holes (height ≤ 2d, d ≥ 1.27 mm), cavity pressure > 500 bar, the fixed vs closure tolerance chart, PL relocation, three-plate molds, overflow grooves, gates, insert cover ≥ 0.51 mm, and prototype cavity counts.
- [S2] Senses Mold, "Custom Rubber Compression Mold": https://senses-mold.com/molding-tool/rubber-mold/compression-mold/. Covers land width 1.5–3 mm, clearance 0.02–0.05 mm, flash < 0.1 mm, draft, preform +3–8 %, shrink, steels, Ra, and machining allowance.
- [S3] "Why are rubber molds designed to intentionally produce flash", note.com: https://note.com/3n7no_tech_pr/n/n006cd33c9d9f?hl=en. Covers shrink by silicone type, pinch-off and overflow, the O-ring PL, plating compatibility, post-cure, venting and bumping.
- [S4] "Rubber Molding and its Types": https://niketlachake.github.io/Rubber%20Molding%20and%20Methods.html. Covers flash about 0.05 mm, land vs viscosity, radial grooves, preform +2–10 %, 8–12 MPa, shrink 1.5–1.8 %, and HRC 60.
- [S5] Engelhardt, "Rubber Compression Molding: Process, Tolerances & Cost (2026)": https://meitu-engelhardt.com/rubber-compression-molding-guide/. Covers vents 0.1–0.2 mm, flash %, preform ±2 %, platen ΔT > 5 °C undercure, ASTM D3182, ISO 3302-1 classes, and PL tolerance additions.
- [S6] Echo Supply, "Basics of Rubber Compression Molding Tools": https://www.echosupply.com/blog/basics-of-rubber-compression-molding-tools/. Covers hot-rolled vs cold-rolled steel, aluminium with low clamp pressure, staggered cavity nesting, and cavity lips.
- [S7] D. Murali Manohar, "Module IV – Compression Molding" (follows the SPI Plastics Engineering Handbook): https://crescent.education/wp-content/uploads/2019/02/MODULE-IV-%E2%80%93-COMPRESSION-MOLDING-PROCESSES.pdf. Covers mold types, 13.8–20.7 MPa on projected plus land area, +4.1 MPa per inch of depth, the breathe cycle, knockouts and temperatures.
- [S8] Polymer Science Learning Center, "Compression Molding": https://pslc.ws/macrog/mpm/composit/fiber/process/compress.htm.
- [S9] Mearthane, "5 Design Tips for Cast Molding with Thermoset Polyurethanes": https://mearthane.com/5-design-tips-for-cast-molding-with-thermoset-polyurethanes/.
- [S10] TSE Industries, "Molding of Millathane Millable Urethane Rubber", Tips 2-2 Rev 1 (2021): https://www.tse-industries.com/wp-content/uploads/Millathane-Molding-Tips2-2-Rev1.pdf. Covers shrink 1.5–2.5 %, cure temperatures and times, cure time halving per 10 °C, and release overuse.
- [S11] Clippard, material properties chart (shrink by elastomer): https://www.clippard.com/cms/node/583.
- [S12] Apple Rubber, "Hot topics for engineers" (shrink, sulfur vs peroxide): https://www.applerubber.com/hot-topics-for-engineers/?p=1803.
- [S13] RMA flash extension classes, as reproduced by https://www.canyoncomponents.com/rma-tolerances-for-o-rings-extrusions-molded-parts- and https://www.elastoproxy.com/rubber-part-tolerances/. Verify against the RMA/ARPM Rubber Handbook.
- [S14] Hubs, "Reducing CNC machining costs: 13 design tips": https://www.hubs.com/knowledge-base/reducing-cnc-machining-costs-13-design-tips/.
- [S15] Plastics Technology, "What you need to know about leader pins and bushings": https://www.ptonline.com/articles/what-you-need-to-know-about-leader-pins-and-bushings.
- [S16] MoldMaking Technology, "How to expand the use of aluminum injection molds": https://www.moldmakingtechnology.com/articles/how-to-expand-the-use-of-aluminum-injection-molds. [S16-why] "Why offer aluminum molds for production" (2002): https://www.moldmakingtechnology.com/articles/why-offer-aluminum-molds-for-production.
- [S17] Xcentric Mold, "Aluminum molds vs steel molds": https://xcentricmold.com/aluminum-molds-vs-steel-molds/.
- [S18] Smooth-On, "How to vent a silicone mold": https://www.smooth-on.com/tutorials/venting-silicone-mold-eliminate-bubbles-finished-casting/. Smooth-Cast 300 Series technical bulletin: https://www.smooth-on.com/tb/files/Smooth-Cast_300q,_300,_305___310.pdf.
- [S19] Volatile Free Inc. TDS (VFI-4286 heated-mold warning, release gloss): https://volatilefree.com/wp-content/uploads/2024/04/TDS_4286.pdf.
- [S20] NIOSH, "Applications Manual for the Revised NIOSH Lifting Equation" (Pub. 94-110): https://www.cdc.gov/niosh/docs/94-110/. Summary: https://ergo-plus.com/niosh-lifting-equation-example/.
- [S21] HSE, "Manual handling at work" (INDG143): https://www.hse.gov.uk/pubns/indg143.pdf. Not opened in this research; values were confirmed through secondary sources.
- [S22] DIN 580 eyebolt working load limits, for example https://gsproducts.co.uk/bzp-steel-din580-eyebolts-lifting and https://www.gsproducts.co.uk/grade-80-lifting-eye-bolts.
- [S23] SPI finish tables: https://www.xometry.com/blog/choosing-the-right-finish-for-your-injection-molded, https://boxumold.com/comparisons/surface-roughness-vs-polish-level, and the DME Micro-Finish guide.
- [S24] US 5,587,183 (PL vents 0.05–0.76 mm, preferred 0.13–0.51 mm, for PU foam): https://patents.google.com/patent/US5587183. MoldMaking Technology, "The many causes of flash": https://www.moldmakingtechnology.com/articles/the-many-causes-of-flash.
- [S25] Texas Injection Molding, Mold Design Review Checklist (pry slots on all plates): https://texasinjectionmolding.com/wp-content/uploads/2020/03/Mold-Design-Review-Checklist.pdf.
- [S26] FirstMold, "Thermal conductivity vs thermal expansion" (CTE: steel about 12, aluminium about 23.6 µm/m·K): https://firstmold.com/tips/thermal-conductivity-and-thermal-expansion/.
- [S27] Gallagher Corp., "Cast Polyurethane Manufacturer" (release coverage, hot-oil heated large molds): https://gallaghercorp.com/wp-content/uploads/2017/03/WP-124-Cast-Polyurethane-Manufacturer-1.pdf.
- [S28] US 5,324,560, "Method of molding a polyurethane wear ring" (preheated aluminium mold, about 100 °C, 15 min pre-cure): https://image-ppubs.uspto.gov/dirsearch-public/print/downloadPdf/5324560.
- [S29] AMESweb Helicoil length calculator (insert lengths 1D–3D, ASME B18.29.1): https://amesweb.info/Screws/Helicoil_Insert_Length_Selection.aspx.
- [S30] Xometry Urethane Casting Design Guide v2.0 (draft 3–5° for soft tools, ribs 0.25–0.5°, tolerance): https://cdn2.hubspot.net/hubfs/340051/Design_Guides/Xometry_DesignGuide_UrethaneCasting.pdf.
- [S31] Make: "Primer: Moldmaking" (underpour, vents at top): https://makezine.com/projects/make-08/primer-moldmaking/. University of Edinburgh uCreate, "Making silicone moulds" (vents at high points, exiting above the pour): https://www.ucreate.is.ed.ac.uk/node/2453.
- [S-T-seal] Chinese journal case study, overflow groove about 1 mm from the cavity on a landing-gear T-seal: https://search.napstic.cn/literature/periodical/010mjgy202403021.
- [S-Triathane] Crosslink Triathane handling guide (degas about 3 min per 1 kg, mold preheat): https://crosslinktech.com/downloads/pdf/TriathaneHandlingGuide.pdf.
- [S-LANXESS] LANXESS Adiprene LF (low-free TDI, lower viscosity means fewer bubbles): https://lanxess.com/en/products-and-brands/brands/adiprene/adiprene-tdi-and-lf-tdi.
- [S-PT] Plastics Technology, leader-pin misalignment from mold temperature (same family as S15).
- [S-Fictiv] Fictiv, "Ultimate Guide to Compression Molding" (undercuts need sliders; keep the PL off visible areas, especially on flash molds): https://fictiv.com/wp-content/uploads/2024/05/Fictiv-Ultimate-Guide-to-Compression-Molding.pdf.
- [S-makerverse] Makerverse, "Design basics for vacuum casting" (1–2° draft): https://www.makerverse.com/resources/casting/design-basics-for-vacuum-casting/.
- [S-MIT] MIT Fab class page (Smooth-Cast 300: 10 min work time / 30 min demold): https://fab.cba.mit.edu/classes/863.25/people/EghosaOhenhen/week10.html.
- [S-PU patents] US 8,188,208 and US 10,766,171 (PU shrink 1–4 %, 0.5–0.9 % for specific systems), found via patent search.
- [S-urethane vent patent] A patent found via the cast-urethane search: vent diameters 0.010–0.060 in suit urethane at 10,000–50,000 cP. The patent number was not captured; re-verify it before quoting the number in TOOL.md.
- [S-vent patents] Tire and mold venting patents (vent slots 0.1–0.6 mm), for example https://patents.justia.com/patent/7811078.
- [K] items: mold-shop practice and handbook knowledge (for example ASM Handbook aluminium temper and fatigue data, VDI 2230 bolting practice, Roark plate formulas). These were not confirmed by a fetched source in this pass. Validate them with the user's structural simulations and a mold maker before treating them as hard rules.

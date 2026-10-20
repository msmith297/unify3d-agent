# Independent reference for the worked examples E1-E11 (design 3.19, re-derived with the critique corrections).
# Writes tools/builtin/design-compression-mold/data/validation/examples.json, which the C# tests (tests/SwAgent.Tests/Mold)
# reproduce. Nothing here calls the C# calculator: the formulas are written out again from design section 3 + critique.
# It reads only the shipped data tables and the coefficient CSV (shared inputs, not shared logic).
#
# Hand checks (critique H2: E11 was "defined with the reference implementation" - its numbers are derived by hand first,
# below, and this script asserts the computed values equal them):
#   E11 base 30 x 14 x 2.5 in (762 x 355.6 x 63.5 mm) 6061, 2 rows x 3 columns of 6 x 4 x 1 in cavities at U = -9, 0, 9 in
#   and V = +-3 in; limits 23 kg and 600 x 400 x 150 mm (+3 mm per side, +1.5 mm on T).
#   Candidate cuts (>= 15 mm from a cavity and >= 0.25 in clear of the 0.625 in moat band = 22.225 mm): U = +-4.5 in
#   (window [-130.175, -98.425] mm), V = 0 (window [-3.175, 3.175] mm).
#   - V cut alone: 762 mm long pieces > 600 mm: box fails. One U cut: the 19.5 in piece = 682.5 - 96 = 586.5 in³ = 25.95 kg: fails.
#   - U cuts at +-4.5 in: ends 10.5 x 14 x 2.5 = 367.5 - 48 = 319.5 in³ = 5,235.7 cm³ = 14.136 kg; middle 9 x 14 x 2.5 = 315 - 48
#     = 267 in³ = 4,375.3 cm³ = 11.813 kg -> option 1 (3 pieces).
#   - one U cut + V cut: 10.5 x 7 in = 159.75 in³ = 7.068 kg and 19.5 x 7 in = 293.25 in³ = 12.975 kg -> option 2 (4 pieces).
#   - both U cuts + V cut: 6 pieces, heaviest 7.068 kg -> option 3.
#   Subplate: max(12, 9.5 + 12) = 21.5 mm -> 0.875 in (22.225 mm; 1 in stock); 30 x 14 in is longer than 600 mm, so it splits
#   at U = 0 (staggered between the base joints): 15 x 14 x 0.875 in = 183.75 in³ = 3,011.1 cm³ = 8.130 kg each.
import csv, json, math, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
DATA = os.path.join(ROOT, "tools", "builtin", "design-compression-mold", "data")
CSV = os.path.join(ROOT, "src", "SwAgent", "Mold", "Structural", "coefficients.csv")
OUT = os.path.join(DATA, "validation", "examples.json")

PSI = 0.006894757293168
IN = 25.4
LBF = 4.4482216152605
USTON = 8.896443230521

mats = {r["id"]: r for r in json.load(open(os.path.join(DATA, "materials.json")))["rows"]}
mm = json.load(open(os.path.join(DATA, "mold-materials.json")))
grades = {g["id"]: g for g in mm["mold_materials"]}
fast = {f["size"]: f for f in mm["fasteners_inch"] + mm["fasteners_metric"]}

# ---------------------------------------------------------------- materials at temperature (S10-S11)
TEMPS = [20, 100, 150, 200, 250]
def lin(xs, ys, x):
    if x <= xs[0]: return ys[0]
    for i in range(len(xs) - 1):
        if x <= xs[i + 1]: return ys[i] + (ys[i + 1] - ys[i]) * (x - xs[i]) / (xs[i + 1] - xs[i])
    return ys[-1]
def at(gid, T):
    g = grades[gid]; r = g["retained"]
    f = lambda k: lin(TEMPS, [1.0] + r[k], T)
    return dict(E=g["E_GPa"] * 1000 * f("E"), Sy=g["yield_MPa"] * f("yield_long_10000h"), Su=g["uts_MPa"] * f("uts_long_10000h"),
                Su20=g["uts_MPa"], Sy20=g["yield_MPa"], fam=g["family"], g=g, nu=g["poisson"], rho=g["density_kg_m3"], cte=g["cte_per_K"])

# ---------------------------------------------------------------- fatigue (S15-S18)
def sa_al(a, N):
    pts = sorted((float(k), v) for k, v in a["g"]["fatigue_MPa"].items() if k[0].isdigit())
    if N <= pts[0][0]:
        A, B, SA, SB = 1e3, pts[0][0], 0.9 * a["Su20"], pts[0][1]
        SN = SA * (SB / SA) ** (math.log10(max(N, 1e3) / A) / math.log10(B / A))
    elif N >= pts[-1][0]: SN = pts[-1][1]
    else:
        for (n1, s1), (n2, s2) in zip(pts, pts[1:]):
            if n1 <= N <= n2: SN = s1 * (s2 / s1) ** (math.log10(N / n1) / math.log10(n2 / n1)); break
    return 0.9 * SN * a["Su"] / a["Su20"]
def sa_st(a, N):
    su = a["Su20"]; ka = 4.51 * su ** -0.265; Se = 0.5 * min(su, 1400) * ka * 0.8 * 0.868
    f = 0.9 if su < 500 else (0.86 if su < 1100 else 0.80)
    S = Se if N >= 1e6 else f * su * (Se / (f * su)) ** (math.log10(max(N, 1e3) / 1e3) / 3)
    return S * a["Su"] / su
def fat(a, N, kf):
    Sa = sa_al(a, N) if a["fam"] == "aluminium" else sa_st(a, N)
    smax = min(2 * Sa * a["Su"] / (a["Su"] + Sa), a["Sy"])
    return dict(Sa=Sa, smax=smax, fat=smax / (kf * 1.5), static=a["Sy"] / 2)

# ---------------------------------------------------------------- coefficients (bilinear in aspect and tau²; strip above walls)
# Mirrors the C# reader's rules (not its code): full-load rows (the largest loadFrac), nu interpolated linearly between the
# tabulated values, walls above the last L/h = max(last row, strip with shear), no clamp to the strip inside the table,
# and the table's interpolation allowance (header "# allowance:", else 1.01 for a swept table, 1.0 for the provisional one).
TAB = {}
HEAD = [l for l in open(CSV) if l.startswith("#")]
PROVISIONAL = any(l.startswith("# provisional") for l in HEAD)
ALLOW = next((float(l.split(":", 1)[1]) for l in HEAD if l.startswith("# allowance:")), 1.0 if PROVISIONAL else 1.01)
_rows = [l.strip().split(",") for l in open(CSV) if not l.startswith("#") and not l.startswith("bc,") and l.strip()]
_full = {}
for c in _rows: _full[c[0]] = max(_full.get(c[0], 0.0), float(c[4]))
for c in _rows:
    if float(c[4]) != _full[c[0]]: continue
    TAB.setdefault(c[0], {}).setdefault(float(c[3]), {}).setdefault(float(c[1]), []).append((float(c[2]), float(c[5]), float(c[6])))
def strip(tau, nu=0.3): return 12 * (1 - nu * nu) / 8 + (1 + nu) / (5 / 6) * tau * tau, 3.0
def in_tau(rows, tau):
    rows = sorted(rows); t2 = tau * tau
    if t2 <= rows[0][0] ** 2: return rows[0][1], rows[0][2]
    for (ta, aa, ba), (tb, ab, bb) in zip(rows, rows[1:]):
        if t2 <= tb * tb:
            w = (t2 - ta * ta) / (tb * tb - ta * ta); return aa + (ab - aa) * w, ba + (bb - ba) * w
    (ta, aa, ba), (tb, ab, bb) = rows[-2], rows[-1]
    return ab + max(0, (ab - aa) / (tb * tb - ta * ta)) * (t2 - tb * tb), bb
def coef_nu(bc, aspect, tau, nu):
    t = TAB[bc][nu]; wall = bc.startswith("Wall")
    if bc in ("SS4", "C4") and aspect < 1: aspect = 1 / aspect
    asp = sorted(t)
    if wall and aspect > asp[-1]:
        last = in_tau(t[asp[-1]], tau); s = strip(tau, nu)
        return max(last[0], s[0]), max(last[1], s[1])
    a0 = max([x for x in asp if x <= aspect] or [asp[0]]); a1 = min([x for x in asp if x >= aspect] or [asp[-1]])
    r0 = in_tau(t[a0], tau); r1 = in_tau(t[a1], tau); w = (aspect - a0) / (a1 - a0) if a1 > a0 else 0
    return r0[0] + (r1[0] - r0[0]) * w, r0[1] + (r1[1] - r0[1]) * w
def coef(bc, aspect, tau, nu=0.33):
    nus = sorted(TAB[bc]); n = min(max(nu, nus[0]), nus[-1])
    lo = max(x for x in nus if x <= n); hi = min(x for x in nus if x >= n)
    a = coef_nu(bc, aspect, tau, lo)
    if hi > lo:
        b = coef_nu(bc, aspect, tau, hi); w = (n - lo) / (hi - lo)
        a = (a[0] + (b[0] - a[0]) * w, a[1] + (b[1] - a[1]) * w)
    return a[0] * ALLOW, a[1] * ALLOW

# ---------------------------------------------------------------- walls (S21-S23)
def wall(p, h, L, b, E, delta, allow, root=1.2, nu=0.33):
    w = 0.3 * h
    for _ in range(200):
        wn = (root * coef("Wall-C", L / h, w / h, nu)[0] * p * h ** 4 / (E * delta)) ** (1 / 3)
        if abs(wn - w) / w < 1e-10: w = wn; break
        w = wn
    sig = lambda x: coef("Wall-C", L / h, x / h, nu)[1] * p * h * h / x ** 2 + p * b / (2 * x)
    ws = w
    for _ in range(200):
        if sig(ws) <= allow * (1 + 1e-12): break
        wn = ws * math.sqrt(sig(ws) / allow)
        if abs(wn - ws) / ws < 1e-12: break
        ws = wn
    return w, ws, sig

def ceil_step(x, step): return math.ceil(x / step - 1e-9) * step
def round_half_up(x): return math.floor(x + 0.5)

examples = []

# ---------------------------------------------------------------- E1 / E7 press (class B, land not counted)
ram8 = math.pi * 8 ** 2 / 4 * IN * IN
cav = 6 * 4 * IN * IN
F1 = 3000 * PSI * ram8
examples.append({"id": "E1", "title": "Class B press: 8.00 in ram, 3,000 psi gauge, 2 cavities 6 x 4 in, land not counted",
    "kind": "press", "input": {"class": "B", "ramArea_mm2": ram8, "gauge_MPa": 3000 * PSI, "cavities": 2, "area_mm2": cav, "perimeter_mm": 20 * IN, "land_mm": 0},
    "expected": {"ramArea_mm2": ram8, "force_N": F1, "pLC1_MPa": F1 / (2 * cav), "pLC1_psi": F1 / (2 * cav) / PSI,
                 "gaugeFor1000psiOnPart_psi": 1000 * 2 * cav / ram8, "pLC1On12inRam_psi": 3000 * math.pi * 36 / 48},
    "hand": {"ramArea_in2": 50.27, "force_lbf": 150796, "force_kN": 670.8, "pLC1_psi": 3142, "gauge_psi": 955, "ram12_psi": 7069},
    "tolerancePct": 0.5})
examples.append({"id": "E7", "title": "Press 'on the part' 1,000 psi, 48 in², 8 in ram", "kind": "press-onpart",
    "input": {"class": "B", "ramArea_mm2": ram8, "onPart_MPa": 1000 * PSI, "cavities": 2, "area_mm2": cav, "perimeter_mm": 20 * IN, "land_mm": 0},
    "expected": {"gaugeForInput_psi": 1000 * 2 * cav / ram8}, "hand": {"gauge_psi": 955}, "tolerancePct": 0.5})

# ---------------------------------------------------------------- E2 class A, the user's typical job (full size action)
row = mats["pu-tdi-ether-90a"]
S = 0.015; T = 100.0; dT = T - 20
a6 = at("al_6061_t651", T)
s = 1 / (1 - S) * (1 + 12e-6 * dT) / (1 + a6["cte"] * dT)
V = 24 * IN ** 3
A = 6 * 4 * IN * IN * s * s; P = 20 * IN * s
pu, pv, h = 6 * IN * s, 4 * IN * s, 1 * IN * s
lip, W, R, clr, dowel, inset, key = 0.125 * IN, 0.5 * IN, 0.25 * IN, 0.25 * IN, 0.5 * IN, 0.625 * IN, 0.25 * IN
charge = V * row["sg"]["typ"] / 1000 * (1 + row["flashAllowancePct"]["typ"] / 100)
shot = 2 * charge * 1.05
vreq = max(row["overflowPctOfPartVolume"]["typ"] * V, 2 * row["flashAllowancePct"]["typ"] * V) / 100
ram = 50 * USTON * 1000 / (3000 * PSI)
F = 2000 * PSI * ram
Aproj = 2 * (A + P * lip)
pLC1 = max(1.5 * row["moldingPressurePsi"]["max"] * PSI, 0.35)
px = F / Aproj
Freq = 1.25 * pLC1 * Aproj
gset = Freq / ram
dgap = 0.020 - 0.010
dwall = 0.25 * 0.125
fa = fat(a6, 5000, 1 + 0.8 * 1.5)
allow = min(fa["fat"], fa["static"])
# corner dowel clear of the moat's R0.25 outer corner (sharp cavity corners), side band + clearance
rr = R + clr + dowel / 2; c0 = lip + W - R
aa = (key + math.sqrt(2 * rr * rr - key * key)) / 2
edge = max(lip + W + clr, aa + inset + c0)
gap = 1.5 * IN
wV = wall(pLC1, h, pu, pv, a6["E"], dwall, allow, nu=a6["nu"])    # walls facing +-V (long, L/h ~ 6)
wU = wall(pLC1, h, pv, pu, a6["E"], dwall, allow, nu=a6["nu"])    # walls facing +-U
L = ceil_step(2 * pu + max(gap, max(wU[0], wU[1])) + 2 * max(edge, max(wU[0], wU[1])), 0.5 * IN)
Wd = ceil_step(pv + 2 * max(edge, max(wV[0], wV[1])), 0.5 * IN)
edgeU = (L - (2 * pu + gap)) / 2; edgeV = (Wd - pv) / 2
tfloor = max(12, 0.25 * h, 0.25 * IN + 12)
tbase = ceil_step(max(tfloor + h, 0.05 * max(L, Wd), 12), 0.125 * IN)
tlid = ceil_step(max(0.05 * max(L, Wd), 12, 0.25 * IN + 12), 0.125 * IN)   # S28 + the lid's own TC well
rho = 2700e-9
mbase = (L * Wd * tbase - 2 * V * s ** 3) * rho
mlid = L * Wd * tlid * rho
drawnU, drawnV = min(edgeU, gap), edgeV
def at_drawn(Lw, b, w):
    al, be = coef("Wall-C", Lw / h, w / h, a6["nu"])
    return be * pLC1 * h * h / w ** 2 + pLC1 * b / (2 * w), 1.2 * al * pLC1 * h ** 4 / (a6["E"] * w ** 3)
sigV, defV = at_drawn(pu, pv, drawnV)
sigU, defU = at_drawn(pv, pu, drawnU)
capV = a6["Sy"] / (3 * h * h / drawnV ** 2 + pv / (2 * drawnV))
capU = a6["Sy"] / (3 * h * h / drawnU ** 2 + pu / (2 * drawnU))
moat = (P + 2 * math.pi * (lip + W / 2)) * W
band = lip + W; pitch = 2 * IN
def runners():
    tot = 0
    for cu in (-1, 1):
        for du, dv in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            side = (pv if du else pu) + 2 * band - 2 * R
            n = max(1, round_half_up(side / pitch))
            if du: ln = edgeU - band if du == cu else gap - 2 * band
            else: ln = edgeV - band
            tot += n * W * max(0, ln)
    return tot
Abear = L * Wd - 2 * (A + moat) - runners()
sbear = F / Abear
gbear = Abear * a6["Sy"] / (2 * ram)
gwall = min(capU, capV) * Aproj / ram
gmax = min(3000 * PSI, gbear, gwall)
examples.append({"id": "E2", "title": "Class A, the user's typical job: TDI-ether 90A, 2 cavities 6 x 4 x 1 in, 6061 at 100 C, 50 US ton at 3,000 psi run at 2,000 psi gauge, N = 5,000, sharp floor corner",
    "kind": "design",
    "design": {
        "units": {"display": "imperial"},
        "layout": {"patternRequest": "2x1"},
        "inputs": {"press_pressure": 2000 * PSI, "press_pressure_is": "Gauge reading", "press_rated_force": 50 * USTON, "press_rated_gauge": 3000 * PSI,
                   "press_temp": 100, "cavities": 2, "cycle_life": 5000, "mold_material": "Aluminium + steel inserts", "mold_grade": "al_6061_t651",
                   "part_grade": "pu-tdi-ether-90a", "shrink_basis": "Supplier datasheet (steel mold)",
                   "max_plate_mass": 23, "max_plate_size": [600, 400, 150]},
        "part": {"volume_mm3": V, "projectedArea_mm2": 6 * 4 * IN * IN, "perimeter_mm": 20 * IN, "box_mm": [6 * IN, 4 * IN, 1 * IN], "floorRadius_mm": 0}},
    "expected": {
        "shrink.scale[0]": s, "shrink.effectivePct": (1 - 1 / s) * 100,
        "process.charge.perCavity_g": charge, "process.charge.shot_g": shot, "process.overflowRequired_mm3": vreq,
        "press.ramArea_mm2": ram, "press.force_kN": F / 1000, "press.aProj_mm2": Aproj, "press.pLC1_MPa": pLC1, "press.pLC1x_MPa": px,
        "press.fReq_kN": Freq / 1000, "press.gauge.recommended_MPa": gset,
        "structural.inputs.deltaGap_mm": dgap, "structural.inputs.deltaWall_mm": dwall,
        "wall.V-B.w_struct_mm": max(wV[0], wV[1]), "wall.U-B.w_struct_mm": max(wU[0], wU[1]),
        "wall.V-B.w_drawn_mm": drawnV, "wall.U-B.w_drawn_mm": drawnU, "wall.V-B.sigma_MPa": sigV, "wall.U-B.sigma_MPa": sigU,
        "wall.V-B.allow_MPa": allow, "wall.V-B.Sa_MPa": fa["Sa"], "wall.V-B.sigmaMaxR0_MPa": fa["smax"],
        "layout.footprint_mm[0]": L, "layout.footprint_mm[1]": Wd,
        "plates.base.t_mm": tbase, "plates.lid.t_mm": tlid, "pieces.BASE-1.mass_kg": mbase, "pieces.LID-1.mass_kg": mlid,
        "bearing.stress_MPa": sbear, "bearing.gaugeMax_MPa": gbear, "gauge.max_MPa": gmax, "lid.limit_kg": 11.0},
    "hand": {"A_ram_in2": 33.33, "F_lbf": 66667, "pLC1_psi": 225, "E_GPa": 66.8, "Sy_MPa": 262.2, "Su_MPa": 291.4, "Sa_MPa": 186.4, "smax_MPa": 227.4, "Kf": 2.2,
             "edge_in": 1.6404, "footprint_in": [17.0, 7.5], "base_in": 1.75, "lid_in": 0.875, "floor_rule_mm": 18.35, "charge_g": 454.2},
    "notes": "The user's side-by-side layout (2 columns x 1 row) is requested; left to itself the layout picks 1 x 2 (9.5 x 13 in, a smaller footprint). Re-derived (critique A4, A9, A11, A5): the long wall is L/h ~ 6 and uses its own row (not the L/h = 4 row); walls are limited by 0.25 x the default +-0.125 mm width tolerance; the gap budget is 0.020 - 0.010 mm land flatness; areas use the scaled cavity; the shrink is on the datasheet basis given here (the 90A row now defaults to the hot-cavity basis).",
    "tolerancePct": 0.5, "tableDependentTolerancePct": 2.0,
    "tableDependent": ["wall.V-B.w_struct_mm", "wall.U-B.w_struct_mm", "wall.V-B.sigma_MPa", "wall.U-B.sigma_MPa"]})

# ---------------------------------------------------------------- E3 class B wall (L/h = 6, per structural 5.3)
a150 = at("al_6061_t651", 150); p20 = at("st_p20", 150)
f3 = fat(a150, 1e5, 1.5)
w0 = wall(10.3, 25, 150, 0, a150["E"], 0.05, 1e9, root=1.0, nu=a150["nu"])[0]
w1, ws1, sig1 = wall(10.3, 25, 150, 0, a150["E"], 0.05, min(f3["fat"], f3["static"]), nu=a150["nu"])
pa = fat(p20, 1e5, 1.5); pb = fat(p20, 1e6, 1.5)
wp, wsp, sigp = wall(10.3, 25, 150, 0, p20["E"], 0.05, min(pa["fat"], pa["static"]), nu=p20["nu"])
wp6, wsp6, _ = wall(10.3, 25, 150, 0, p20["E"], 0.05, min(pb["fat"], pb["static"]), nu=p20["nu"])
examples.append({"id": "E3", "title": "Class B wall: p = 10.3 MPa, h = 25 mm, L = 150 mm (L/h = 6), delta = 0.05 mm, K_f = 1.5, membrane off, N = 1e5",
    "kind": "wall", "input": {"p": 10.3, "h": 25, "L": 150, "bPerp": 0, "delta": 0.05, "kf": 1.5, "N": 1e5},
    "expected": {"Al.E_MPa": a150["E"], "Al.Sy_MPa": a150["Sy"], "Al.wDefl_noRoot_mm": w0, "Al.wDefl_mm": w1, "Al.allowFatigue_MPa": f3["fat"], "Al.wStruct_mm": max(w1, ws1),
                 "P20.wDefl_mm": wp, "P20.sigmaAtDefl_MPa": coef("Wall-C", 6, wp / 25, p20["nu"])[1] * 10.3 * 625 / wp ** 2,
                 "P20.allow1e5_MPa": min(pa["fat"], pa["static"]), "P20.wStruct1e5_mm": max(wp, wsp), "P20.allow1e6_MPa": min(pb["fat"], pb["static"]), "P20.wStruct1e6_mm": max(wp6, wsp6)},
    "flags": {"Al.stocky": max(w1, ws1) / 25 > 0.5},
    "notes": "Re-derived for the L/h = 6 row (critique A4). Before: the L/h = 4 row gave 13.3 / 16.9 mm (Al) and 8.65 mm at 232 MPa against 231 MPa (P20, critique H1: fails by 0.4% -> 8.66 mm). The aluminium wall is still stocky (w/h > 0.5): prefer P20 or a 3D check.",
    "tolerancePct": 1.0, "tableDependentTolerancePct": 2.0})

# ---------------------------------------------------------------- E4 bolted frame corner (critique A1: scatter + hot proof)
def joint(size, grip_in, P_lbf, frac, n_eval=True, T=100.0, dT=100.0, Em=10e6 * PSI):
    f = fast[size]; d = f["d_in"] * IN; At = f["tensile_stress_area_in2"] * IN * IN; Fp = At * f["proof_stress_ksi"] * 6.894757293168
    l = grip_in * IN; kb = At * 205000 / l; km = Em * d * 0.79670 * math.exp(0.63816 * d / l); C = kb / (kb + km)
    dF = (23.6e-6 - 11.7e-6) * dT * l * kb * km / (kb + km)
    kT = lin(TEMPS, [1.0, 0.95, 0.92, 0.89, 0.86], T); FpT = kT * Fp
    P = P_lbf * LBF; Fi = frac * Fp; Fmax = 1.25 * Fi; Fmin = 0.75 * 0.9 * Fi
    hot = Fmax + dF + C * P
    return dict(Fp=Fp, C=C, dF=dF, kT=kT, hot=hot, hotPctHotProof=100 * hot / FpT, n0=Fmin / (P * (1 - C)), nL=(FpT - Fmax) / (C * P),
                fhi_hot=(0.9 * FpT - dF - C * P) / (1.25 * Fp), flo_sep=2 * P * (1 - C) / (0.75 * 0.9 * Fp), fhi_nL=(FpT - 1.5 * C * P) / (1.25 * Fp))
P4 = 56500 / 2 / 4
e4 = joint("1/2-13", 2.0, P4, 0.60)
e4b = joint("1/2-13", 2.0, 56500 / 2 / 6, 0.60)
e4c = joint("5/8-11", 2.5, P4, 0.60)
examples.append({"id": "E4", "title": "Bolted frame corner: 1/2-13 A574, grip 2.0 in, aluminium joint (E 10e6 psi), dT = 100 K (k_T at 100 C), 28.25 kip per corner",
    "kind": "bolt", "input": {"size": "1/2-13", "grip_mm": 2.0 * IN, "P_N_per4": P4 * LBF, "memberE_MPa": 10e6 * PSI, "tempC": 100, "deltaT": 100, "scatter": 0.25},
    "expected": {"Fp_N": e4["Fp"], "C": e4["C"], "dF_N": e4["dF"], "at060.hotPctHotProof": e4["hotPctHotProof"], "at060.n0": e4["n0"],
                 "four.windowExists": min(e4["fhi_hot"], e4["fhi_nL"]) >= e4["flo_sep"],
                 "six.fractionHi": min(e4b["fhi_hot"], e4b["fhi_nL"]), "six.fractionLo": e4b["flo_sep"],
                 "five8x4.fractionHi": min(e4c["fhi_hot"], e4c["fhi_nL"]), "five8x4.fractionLo": e4c["flo_sep"]},
    "hand": {"design_nominal": "0.60 F_p: hot 88% F_p, n0 2.19 (passes) - ignores scatter", "critique": "+-25% scatter, k_T 0.95: 108% of hot proof, n0 1.64: fails",
             "first_passing": "6 x 1/2-13 at about 0.49 F_p, or 4 x 5/8-11 at about 0.47 F_p"},
    "notes": "Bolt E = 205 GPa (C = 0.311; the design used 29e6 psi, C = 0.306). The head bearing with an F436 washer also passes in the window.",
    "tolerancePct": 0.5})

# ---------------------------------------------------------------- E5 M10 12.9 in 6061, 20 -> 150 C (no external load)
f10 = fast["M10"]; At10 = f10["tensile_stress_area_mm2"]; Fp10 = At10 * 970
E150 = 68900 * 0.93
kb5 = At10 * 205000 / 50; km5 = E150 * 10 * 0.79670 * math.exp(0.63816 * 10 / 50)
dF5 = (23.6e-6 - 11.7e-6) * 130 * 50 * kb5 * km5 / (kb5 + km5)
kT5 = 0.92
hot060 = 1.25 * 0.60 * Fp10 + dF5
fhi_hot5 = (0.9 * kT5 * Fp10 - dF5) / (1.25 * Fp10)
# head bearing (S51): ISO 7089 washer OD 20, t 2 under a 16 mm head: d_eff = min(20, 16 + 2 x 2) = 20; hole 11 mm
Ahead = math.pi / 4 * (20 ** 2 - 11 ** 2)
fhi_head5 = (0.9 * 310 * 0.75 * Ahead - dF5) / (1.25 * Fp10)
fhi5 = min(fhi_hot5, fhi_head5, 0.60)
examples.append({"id": "E5", "title": "M10 12.9, grip 50 mm, 6061 plates, 20 -> 150 C", "kind": "bolt-thermal",
    "input": {"size": "M10", "grip_mm": 50, "memberE_MPa": E150, "tempC": 150, "deltaT": 130, "memberSu_MPa": 310 * 0.75},
    "expected": {"kb_N_mm": kb5, "km_N_mm": km5, "dF_N": dF5, "at060.hot_N": hot060, "at060.hotPctProof": 100 * hot060 / Fp10,
                 "at060.hotPctHotProof": 100 * hot060 / (kT5 * Fp10), "fractionHi": fhi5, "fractionHiHot": fhi_hot5, "fractionHiHead": fhi_head5,
                 "chosen": math.floor(fhi5 * 200) / 200},
    "hand": {"kb_kN_mm": 237.8, "km_kN_mm": 580, "dF_kN": 13.05, "design": "0.60 F_p nominal: 83% - passed; with +25% scatter 98% of F_p = 107% of hot proof: fails"},
    "notes": "Critique A1: the aluminium-joint preload drops to the highest fraction that passes (here the washer bearing on hot 6061 governs, about 0.47 F_p).",
    "tolerancePct": 0.5})

# ---------------------------------------------------------------- E6 E8 E9 E10
examples.append({"id": "E6", "title": "Shrink 1.5%, datasheet (steel) basis, aluminium cavity, 170 C", "kind": "shrink",
    "input": {"pct": 1.5, "basis": "Supplier datasheet (steel mold)", "tempC": 170, "alphaCavity": 2.36e-5},
    "expected": {"scale": 1 / 0.985 * (1 + 12e-6 * 150) / (1 + 2.36e-5 * 150), "effectivePct": (1 - 0.985 * (1 + 2.36e-5 * 150) / (1 + 12e-6 * 150)) * 100,
                 "hotCavityAt100": 1 / (0.985 * (1 + 2.36e-5 * 80)), "measured": 1 / 0.985, "v1": 1.015},
    "hand": {"scale": 1.013468, "effectivePct": 1.33, "hotCavityAt100": 1.013315}, "tolerancePct": 0.01})
examples.append({"id": "E8", "title": "Thread engagement, 6061 at 150 C, 12.9 screw", "kind": "thread",
    "input": {"size": "M10", "grade": "al_6061_t651", "tempC": 150},
    "expected": {"LeOverD": 0.5 * 1220 / (310 * 0.75), "insert": True, "helicalNeedD": 0.5 * 1220 / (310 * 0.75) / 1.2, "helicalLengthD": 2.5},
    "hand": {"LeOverD": 2.62}, "notes": "Re-checked at the helical insert's 1.2 d shear diameter: 2.18 d needed, so the 2.5 d stock length (2 d is not enough).",
    "tolerancePct": 0.5})
t = 20.0
for _ in range(200):
    tn = (coef("SS4", 1.0, t / 100, 0.3)[0] * 10 * 100 ** 4 / (66000 * 0.025)) ** (1 / 3)
    if abs(tn - t) < 1e-10: break
    t = tn
examples.append({"id": "E9", "title": "T16 thickness: SS4, a/b = 1, p = 10 MPa, b = 100 mm, E = 66 GPa, delta = 0.025 mm", "kind": "span",
    "input": {"bc": "SS4", "aspect": 1, "b": 100, "p": 10, "E": 66000, "delta": 0.025}, "expected": {"t_mm": t}, "hand": {"t_mm": 35.4},
    "tolerancePct": 0.5, "tableDependentTolerancePct": 1.0})
examples.append({"id": "E10", "title": "Steel insert 25 mm in aluminium, dT = 140 K (alpha 23.6e-6 / 12.8e-6)", "kind": "fit",
    "input": {"d": 25, "alphaHub": 23.6e-6, "alphaInsert": 12.8e-6, "dT": 140},
    "expected": {"interference_mm": 25 * (23.6e-6 - 12.8e-6) * 140 / (1 + 23.6e-6 * 140)}, "hand": {"interference_mm": 0.0377}, "tolerancePct": 0.5})

# ---------------------------------------------------------------- E11 split (hand-derived above; asserted here)
d11 = dict(L=30 * IN, W=14 * IN, T=2.5 * IN)
cvol = 6 * 4 * 1 * IN ** 3
def mass(lu, lv, ncav, t=2.5 * IN): return (lu * lv * t - ncav * cvol) * 2700e-9
m_end = mass(10.5 * IN, 14 * IN, 2); m_mid = mass(9 * IN, 14 * IN, 2)
m4a = mass(10.5 * IN, 7 * IN, 1); m4b = mass(19.5 * IN, 7 * IN, 2); m6mid = mass(9 * IN, 7 * IN, 1)
m_sub = 15 * IN * 14 * IN * 0.875 * IN * 2700e-9
assert abs(m_end - 14.136) < 0.001 and abs(m_mid - 11.813) < 0.001, (m_end, m_mid)
assert abs(m4a - 7.068) < 0.001 and abs(m4b - 12.975) < 0.001, (m4a, m4b)
assert abs(m_sub - 8.130) < 0.001, m_sub
assert mass(19.5 * IN, 14 * IN, 4) > 23   # one U cut fails on mass
examples.append({"id": "E11", "title": "Split: base 30 x 14 x 2.5 in, 6061, 2 rows x 3 columns of 6 x 4 x 1 in cavities (U = -9, 0, 9 in; V = +-3 in), m_plate 23 kg, 600 x 400 x 150 mm",
    "kind": "split",
    "input": {"L": d11["L"], "W": d11["W"], "T": d11["T"], "maxMass": 23, "maxBox": [600, 400, 150],
              "cavities": [{"id": "C%d" % (k + 1), "u": u * IN, "v": v * IN, "su": 6 * IN, "sv": 4 * IN, "depth": IN}
                           for k, (u, v) in enumerate([(-9, 3), (0, 3), (9, 3), (-9, -3), (0, -3), (9, -3)])]},
    "expected": {"options": 3,
                 "opt1.pieces": 3, "opt1.cutsU": [-4.5 * IN, 4.5 * IN], "opt1.cutsV": [], "opt1.masses": [m_end, m_mid, m_end],
                 "opt1.subplate_t": 0.875 * IN, "opt1.subplate_cutsU": [0.0], "opt1.subplate_mass": m_sub,
                 "opt2.pieces": 4, "opt2.heaviest": m4b, "opt3.pieces": 6, "opt3.heaviest": m4a},
    "hand": {"opt1": "3 pieces 14.136 / 11.813 / 14.136 kg (cuts at U = +-4.5 in)", "subplate": "0.875 in, split at U = 0: 2 x 8.130 kg",
             "rejected": "V cut alone: 762 mm long, box fails; one U cut: 25.95 kg piece"},
    "notes": "Critique H2: derived by hand (see the header of this script), not with the implementation under test.",
    "tolerancePct": 0.1})

doc = {"schema": "unify3d.mold-examples/1", "generated": "2026-10-19", "generator": "tools/dev/mold-data/examples_check.py",
       "coefficients": open(CSV).read().split("\n")[2].strip("# ").strip(),
       "note": "Expected values come from this independent script; 'hand' holds the hand-derived or published figures they were checked against.",
       "examples": examples}
text = json.dumps(doc, indent=1) + "\n"
if "--check" in sys.argv:
    same = open(OUT, encoding="utf-8").read() == text
    print("examples.json", "matches" if same else "DIFFERS from the generator")
    sys.exit(0 if same else 1)
os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w", encoding="utf-8", newline="\n") as f:
    f.write(text)
for e in examples:
    print(e["id"], {k: (round(v, 5) if isinstance(v, float) else v) for k, v in e["expected"].items()})

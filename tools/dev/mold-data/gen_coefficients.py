# Provisional plate and wall coefficient table for the compression-mold calculator (src/SwAgent/Mold/Structural/coefficients.csv).
#
# This is the research prototype (proto_plate.py: MITC4 Mindlin plate, dense numpy solve, 12 elements on the short side)
# run over the shapes the calculator needs until Track A2's C# sweep (tools/dev/MoldSweep) regenerates the table with the
# full grid, h-refinement and the Roark/Navier gates. Same columns, so the calculator does not change when it is replaced.
#
#   alpha = w_max * E * t^3 / (q * ref^4)      beta = 6 * M_max / (q * ref^2)     (ref = short side; h for walls)
#   wMaxX, wMaxY = where w_max sits, as fractions of the plate's x (long side / wall length) and y (short side / height)
#
# Walls: length L along x, height h along y; bottom (y=0) clamped, top (y=h) free; sides clamped (Wall-C, pocket milled
# from solid) or simply supported (Wall-S, bolted frame corners). Rows go to L/h = 12 (critique A4): above the last row the
# calculator uses the cantilever strip with shear, alpha = 12(1-nu^2)/8 + (1+nu)/kappa*tau^2, beta = 3.
# Plates: SS4 (hard simply supported), C4 (clamped), Rails (two opposite edges simply supported along x, the others free).
#
# Run: python -I gen_coefficients.py > ..\..\..\src\SwAgent\Mold\Structural\coefficients.csv   (needs numpy; ~2 min)
#      python -I gen_coefficients.py --check        regenerates and compares the data section and the header hash with
#                                                   the shipped CSV (needs numpy, ~2 min); exit 1 when they differ
#      python -I gen_coefficients.py --check-hash   only checks that the shipped header's sha256 matches its data
#                                                   section (no numpy; also valid for Track A2's replacement table)
import sys, os, hashlib, datetime
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
SHIPPED = os.path.normpath(os.path.join(HERE, "..", "..", "..", "src", "SwAgent", "Mold", "Structural", "coefficients.csv"))
np = None
plate = None


def _numpy():
    # numpy and the prototype are only needed to regenerate, not for the hash check
    global np, plate
    if np is None:
        import numpy
        import proto_plate
        np, plate = numpy, proto_plate

NU = 0.3
TAUS = [0.02, 0.1, 0.2, 0.3, 0.5]


def coeffs(a, b, t, edges, nshort=12, ref=None, cap=96):
    _numpy()
    ref = ref or min(a, b)
    nx = min(cap, max(2, int(round(nshort * a / min(a, b)))))
    ny = min(cap, max(2, int(round(nshort * b / min(a, b)))))
    w, Mx, My, Mxy = plate.solve_plate(a, b, t, 1.0, NU, 1.0, nx, ny, edges)
    R = plate.LAST_R
    hx, hy = R[3], R[4]
    aw = np.abs(w)
    j, i = np.unravel_index(np.argmax(aw), aw.shape)
    Mint = max(np.abs(Mx).max(), np.abs(My).max())
    # Clamped-edge moments from the nodal reactions (averaging Gauss values under-reads them by 12-40%).
    Medge = 0.0
    for key, bc in edges.items():
        if bc != 'C':
            continue
        if key == 'x0': Medge = max(Medge, np.abs(R[1][1:-1, 0]).max() / hy)
        if key == 'xa': Medge = max(Medge, np.abs(R[1][1:-1, -1]).max() / hy)
        if key == 'y0': Medge = max(Medge, np.abs(R[2][0, 1:-1]).max() / hx)
        if key == 'yb': Medge = max(Medge, np.abs(R[2][-1, 1:-1]).max() / hx)
    M = max(Mint, Medge)
    return aw.max() * t ** 3 / ref ** 4, 6 * M / ref ** 2, i / nx, j / ny


def rows():
    out = []
    plates = {'SS4': {'x0': 'S', 'xa': 'S', 'y0': 'S', 'yb': 'S'},
              'C4': {'x0': 'C', 'xa': 'C', 'y0': 'C', 'yb': 'C'},
              'Rails': {'x0': 'F', 'xa': 'F', 'y0': 'S', 'yb': 'S'}}
    for name, ed in plates.items():
        for r in [1.0, 1.2, 1.4, 1.6, 2.0, 3.0, 4.0]:
            for tau in TAUS:
                al, be, wx, wy = coeffs(r, 1.0, tau, ed)
                out.append((name, r, tau, al, be, wx, wy))
    for sides, name in (('C', 'Wall-C'), ('S', 'Wall-S')):
        ed = {'x0': sides, 'xa': sides, 'y0': 'C', 'yb': 'F'}
        for r in [0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 12.0]:
            for tau in TAUS:
                al, be, wx, wy = coeffs(r, 1.0, tau, ed, ref=1.0)
                out.append((name, r, tau, al, be, wx, wy))
    return out


def data_text():
    body = ["bc,aspect,tau,nu,loadFrac,alpha,beta,wMaxX,wMaxY"]
    for (bc, r, tau, al, be, wx, wy) in rows():
        body.append("%s,%g,%g,%g,%g,%.5f,%.4f,%.3f,%.3f" % (bc, r, tau, NU, 1.0, al, be, wx, wy))
    return "\n".join(body) + "\n"


def split_shipped(path=SHIPPED):
    """(header sha256, data section, provisional?) of a coefficients.csv; the data section is every non-comment line,
    each ending in a newline - the convention the C# reader and its test hash."""
    with open(path, encoding="ascii") as f:
        lines = f.read().splitlines()
    sha = next((l.split(":", 1)[1].strip() for l in lines if l.startswith("# sha256:")), None)
    data = "".join(l + "\n" for l in lines if l.strip() and not l.startswith("#"))
    return sha, data, any(l.startswith("# provisional") for l in lines)


def check(full):
    sha, data, provisional = split_shipped()
    actual = hashlib.sha256(data.encode("ascii")).hexdigest()
    ok = sha == actual
    print("coefficients.csv: header sha256 %s the data section (%s)" % ("matches" if ok else "DOES NOT match", actual))
    if full:
        if not provisional:
            print("the shipped table is not the provisional one (Track A2 replaced it): only the hash is checked")
        else:
            same = data_text() == data
            print("regenerated data section %s the shipped one" % ("matches" if same else "DIFFERS from"))
            ok = ok and same
    return 0 if ok else 1


def main():
    if "--check-hash" in sys.argv:
        sys.exit(check(False))
    if "--check" in sys.argv:
        sys.exit(check(True))
    text = data_text()
    sha = hashlib.sha256(text.encode("ascii")).hexdigest()
    head = ["# provisional",
            "# generator: tools/dev/mold-data/gen_coefficients.py (proto MITC4, 12 elements on the short side, nu 0.3, full load only)",
            "# version: 0.1.0-provisional",
            "# date: " + datetime.date.today().isoformat(),
            "# sha256: " + sha,
            "# replaced by tools/dev/MoldSweep coefficients (Track A2) with the same columns"]
    sys.stdout.write("\n".join(head) + "\n" + text)


if __name__ == "__main__":
    main()

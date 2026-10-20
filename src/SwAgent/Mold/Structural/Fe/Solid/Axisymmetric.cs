using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SwAgent.Mold.Structural.Fe.Solid
{
    /// <summary>
    /// Axisymmetric linear-elastic solid on a structured r-z grid (bilinear quads, 2x2 Gauss, strains [er, ez, etheta, grz]),
    /// with an optional unilateral contact plane: nodes on z = 0 rest on a frictionless rigid plane (which is also the mirror plane
    /// of a symmetric two-member joint) and lift off where the contact would pull. Optionally two bodies stacked at the grid line
    /// Zs[InterfaceK] (a plate on a tapped base: the nodes there are doubled and held together by a frictionless unilateral
    /// contact), void cells (a clearance hole), stiffer cells (a rigid washer) and an axial load on the bore (the engaged
    /// thread). Banded LDL^T. Forces in N for the full ring (2 pi).
    /// Used for the bolted-joint member stiffness checks against Wileman and the VDI 2230 cone (through-bolted and tapped).
    /// </summary>
    public sealed class AxisymmetricModel
    {
        public double[] Rs, Zs;
        public double E = 1, Nu = 0.3;
        /// <summary>Pressures on the top face z = Zs[last] between radii (r1, r2), pushing -z.</summary>
        public List<(double R1, double R2, double P)> TopPressures = new List<(double, double, double)>();
        /// <summary>Pressure on the inner face r = Rs[0] pushing +r (Lame check).</summary>
        public double InnerPressure;
        /// <summary>z = 0 plane: none, bonded (uz = 0 everywhere) or unilateral contact.</summary>
        public BottomCondition Bottom = BottomCondition.Contact;
        /// <summary>Hold uz = 0 on the top face too (plane-strain slab for the Lame check).</summary>
        public bool TopFixedZ;
        /// <summary>A rigid flat punch on the top face between radii (r1, r2): uz = -Depth there (penalty springs), radial motion free.</summary>
        public (double R1, double R2, double Depth)? TopPunch;
        public int MaxContactPasses = 30;

        /// <summary>Grid index k of the line Zs[k] where a lower and an upper body meet in frictionless unilateral contact (-1: one body).</summary>
        public int InterfaceK = -1;
        /// <summary>Whether the cell with centre (r, z) is material (null: all).</summary>
        public Func<double, double, bool> Solid;
        /// <summary>Young's modulus multiplier of the cell with centre (r, z) (null: 1), e.g. a stiff washer under a bolt head.</summary>
        public Func<double, double, double> Stiffness;
        /// <summary>Axial forces (N, +z) spread uniformly over the bore r = Rs[0] between Z1 and Z2: an engaged thread pulling on its body.</summary>
        public List<(double Z1, double Z2, double F)> BoreLoads = new List<(double, double, double)>();
        /// <summary>Nodes (grid i, k; the lower body's node at the interface) held at uz = 0: removes the axial rigid-body motion of a self-equilibrated model.</summary>
        public List<(int I, int K)> PinZ = new List<(int, int)>();
    }

    public enum BottomCondition { None, Bonded, Contact }

    public sealed class AxisymmetricResult
    {
        public double[] Rs, Zs;
        /// <summary>Displacements per grid node (the upper body's at the interface line); NaN where no material.</summary>
        public double[,] Ur, Uz;
        /// <summary>The lower body's uz on the interface line (NaN where none, or when there is no interface).</summary>
        public double[] UzInterfaceLower;
        public int Passes;
        /// <summary>Outer radius of the contact zone around the hole on z = 0 (where the plates first open, moving outward).</summary>
        public double ContactRadius;
        /// <summary>Outer radius of the closed part of the body interface around the hole (when there is one).</summary>
        public double InterfaceContactRadius;
        public double ContactForce, AppliedForce;
        /// <summary>Force the rigid punch pushes with (N), when there is one.</summary>
        public double PunchForce;

        /// <summary>Area-weighted mean uz on the top face between r1 and r2 (the displacement conjugate to a uniform pressure there).</summary>
        public double MeanTopUz(double r1, double r2)
        {
            int k = Zs.Length - 1;
            double num = 0, den = 0;
            for (int i = 0; i + 1 < Rs.Length; i++)
            {
                double a = Math.Max(r1, Rs[i]), b = Math.Min(r2, Rs[i + 1]);
                if (b <= a) continue;
                // linear uz over the element, weight r dr (quadratic: two Gauss points are exact)
                double h = Rs[i + 1] - Rs[i];
                Func<double, double> uz = r => Uz[i, k] + (Uz[i + 1, k] - Uz[i, k]) * (r - Rs[i]) / h;
                num += AxisymmetricSolver.Gauss2(a, b, r => uz(r) * r);
                den += (b * b - a * a) / 2;
            }
            return num / den;
        }

        /// <summary>Length-weighted mean uz on the bore r = Rs[0] between z1 and z2 (the displacement conjugate to a uniform BoreLoad).</summary>
        public double MeanBoreUz(double z1, double z2)
        {
            double num = 0, den = 0;
            for (int k = 0; k + 1 < Zs.Length; k++)
            {
                double a = Math.Max(z1, Zs[k]), b = Math.Min(z2, Zs[k + 1]);
                if (b <= a) continue;
                // below the interface the segment's top node is the lower body's (Uz holds the upper body's there)
                double lo = Uz[0, k], hi = k + 1 == InterfaceK && UzInterfaceLower != null ? UzInterfaceLower[0] : Uz[0, k + 1];
                double h = Zs[k + 1] - Zs[k];
                num += (b - a) * (lo + (hi - lo) * ((a + b) / 2 - Zs[k]) / h);
                den += b - a;
            }
            return num / den;
        }

        internal int InterfaceK = -1;
    }

    public static class AxisymmetricSolver
    {
        public static AxisymmetricResult Solve(AxisymmetricModel m, CancellationToken ct = default)
        {
            var rs = m.Rs; var zs = m.Zs;
            int nr = rs.Length, nz = zs.Length;
            if (rs[0] <= 0) throw new ArgumentException("Inner radius must be positive (a bolt hole).");
            int kI = m.InterfaceK;
            if (kI == 0 || kI >= nz - 1) throw new ArgumentException("InterfaceK must be an inner grid line (or -1).");
            bool split = kI > 0;
            int stride = nz + (split ? 1 : 0);
            // node of grid point (i, k) for the body above (upper) or below the interface; z fastest (the grid is taller in r)
            int Node(int i, int k, bool upper) => i * stride + k + (split && (k > kI || (k == kI && upper)) ? 1 : 0);
            int nNodes = nr * stride, ndof = 2 * nNodes;
            double e = m.E, nu = m.Nu, f = e / ((1 + nu) * (1 - 2 * nu));
            var d = new double[4, 4]
            {
                { f * (1 - nu), f * nu, f * nu, 0 },
                { f * nu, f * (1 - nu), f * nu, 0 },
                { f * nu, f * nu, f * (1 - nu), 0 },
                { 0, 0, 0, f * (1 - 2 * nu) / 2 },
            };

            // cells: material, stiffness scale, nodes
            var cells = new List<(int i, int k, int[] dofs, double scale)>();
            var used = new bool[nNodes];
            for (int i = 0; i + 1 < nr; i++)
                for (int k = 0; k + 1 < nz; k++)
                {
                    double rc = (rs[i] + rs[i + 1]) / 2, zc = (zs[k] + zs[k + 1]) / 2;
                    if (m.Solid != null && !m.Solid(rc, zc)) continue;
                    bool upper = split && k >= kI;
                    int[] n = { Node(i, k, upper), Node(i + 1, k, upper), Node(i + 1, k + 1, upper), Node(i, k + 1, upper) };
                    var dofs = new int[8];
                    for (int a = 0; a < 4; a++) { dofs[2 * a] = 2 * n[a]; dofs[2 * a + 1] = 2 * n[a] + 1; used[n[a]] = true; }
                    cells.Add((i, k, dofs, m.Stiffness?.Invoke(rc, zc) ?? 1.0));
                }
            if (cells.Count == 0) throw new InvalidOperationException("No material cells.");

            var load = new double[ndof];
            int top = nz - 1;
            foreach (var (r1, r2, p) in m.TopPressures)
                for (int i = 0; i + 1 < nr; i++)
                {
                    double a = Math.Max(r1, rs[i]), b = Math.Min(r2, rs[i + 1]);
                    if (b <= a || !used[Node(i, top, true)] || !used[Node(i + 1, top, true)]) continue;
                    double h = rs[i + 1] - rs[i];
                    // integral of N_i r dr and N_(i+1) r dr over [a, b], N linear on [rs[i], rs[i+1]]
                    double I(Func<double, double> g) => Gauss2(a, b, r => g(r) * r); // quadratic integrand: exact
                    double fi = I(r => (rs[i + 1] - r) / h), fj = I(r => (r - rs[i]) / h);
                    load[2 * Node(i, top, true) + 1] -= 2 * Math.PI * p * fi;
                    load[2 * Node(i + 1, top, true) + 1] -= 2 * Math.PI * p * fj;
                }
            if (m.InnerPressure != 0)
                for (int k = 0; k + 1 < nz; k++)
                {
                    bool upper = split && k >= kI;
                    double h = zs[k + 1] - zs[k], fr = 2 * Math.PI * rs[0] * m.InnerPressure * h / 2;
                    load[2 * Node(0, k, upper)] += fr; load[2 * Node(0, k + 1, upper)] += fr;
                }
            foreach (var (z1, z2, force) in m.BoreLoads)
            {
                // uniform axial traction on the bore between z1 and z2 (cells on the bore that are material), consistent nodal loads
                double len = 0;
                var segs = new List<(int k, double a, double b)>();
                for (int k = 0; k + 1 < nz; k++)
                {
                    double a = Math.Max(z1, zs[k]), b = Math.Min(z2, zs[k + 1]);
                    if (b <= a || (m.Solid != null && !m.Solid((rs[0] + rs[1]) / 2, (zs[k] + zs[k + 1]) / 2))) continue;
                    segs.Add((k, a, b)); len += b - a;
                }
                if (len <= 0) throw new ArgumentException("Bore load range has no material on the bore.");
                double q = force / len; // N per unit length of bore
                foreach (var (k, a, b) in segs)
                {
                    bool upper = split && k >= kI;
                    double h = zs[k + 1] - zs[k];
                    double wk = ((zs[k + 1] - a) + (zs[k + 1] - b)) / 2 / h, wk1 = 1 - wk; // N_k and N_(k+1) at the segment's midpoint (linear: exact)
                    load[2 * Node(0, k, upper) + 1] += q * (b - a) * wk;
                    load[2 * Node(0, k + 1, upper) + 1] += q * (b - a) * wk1;
                }
            }

            // rigid punch: stiff springs pulling the top nodes under it to uz = -Depth
            var spring = new double[ndof];
            if (m.TopPunch is var punch && punch.HasValue)
            {
                double kp = 1e7 * e * (rs[nr - 1] - rs[0]);
                for (int i = 0; i < nr; i++)
                    if (rs[i] >= punch.Value.R1 - 1e-12 && rs[i] <= punch.Value.R2 + 1e-12 && used[Node(i, top, true)])
                    {
                        int dz = 2 * Node(i, top, true) + 1;
                        spring[dz] = kp; load[dz] -= kp * punch.Value.Depth;
                    }
            }

            // interface contact: a stiff two-node spring on uz per radius where both bodies have the node
            var pairs = new List<(int i, int lo, int up, double k)>();
            if (split)
                for (int i = 0; i < nr; i++)
                {
                    int lo = Node(i, kI, false), up = Node(i, kI, true);
                    if (!used[lo] || !used[up]) continue;
                    double trib = ((i > 0 ? rs[i] - rs[i - 1] : 0) + (i < nr - 1 ? rs[i + 1] - rs[i] : 0)) / 2;
                    double hz = Math.Min(zs[kI] - zs[kI - 1], zs[kI + 1] - zs[kI]);
                    pairs.Add((i, 2 * lo + 1, 2 * up + 1, 1e4 * e * 2 * Math.PI * rs[i] * trib / hz));
                }
            var pairOn = pairs.Select(_ => true).ToArray();

            var fixedZ0 = new bool[nr];
            if (m.Bottom != BottomCondition.None) for (int i = 0; i < nr; i++) fixedZ0[i] = true;
            var res = new AxisymmetricResult { Rs = rs, Zs = zs, InterfaceK = kI, AppliedForce = -load.Where((v, idx) => idx % 2 == 1 && spring[idx] == 0 && v < 0).Sum() };
            double[] u = null;
            double[] ku = null;
            for (int pass = 1; pass <= Math.Max(1, m.MaxContactPasses); pass++)
            {
                res.Passes = pass;
                var fix = new bool[ndof];
                for (int n = 0; n < nNodes; n++) if (!used[n]) fix[2 * n] = fix[2 * n + 1] = true;
                for (int i = 0; i < nr; i++) if (fixedZ0[i]) fix[2 * Node(i, 0, false) + 1] = true;
                if (m.TopFixedZ) for (int i = 0; i < nr; i++) fix[2 * Node(i, top, true) + 1] = true;
                foreach (var (pi, pk) in m.PinZ) fix[2 * Node(pi, pk, false) + 1] = true;
                if (m.Bottom == BottomCondition.None && !m.TopFixedZ && m.PinZ.Count == 0 && !m.TopPunch.HasValue)
                    throw new InvalidOperationException("Axial rigid-body motion is free.");
                var links = pairs.Where((p, q) => pairOn[q]).Select(p => (p.lo, p.up, p.k)).ToList();
                u = SolveOnce(m, d, cells, ndof, load, fix, spring, links, ct, out ku);
                if (m.TopPunch.HasValue)
                {
                    double pf = 0;
                    for (int q = 0; q < ndof; q++) if (spring[q] > 0) pf += spring[q] * (u[q] + m.TopPunch.Value.Depth);
                    res.PunchForce = pf; res.AppliedForce = pf;
                }
                bool changed = false;
                if (m.Bottom == BottomCondition.Contact)
                    for (int i = 0; i < nr; i++)
                    {
                        int dz = 2 * Node(i, 0, false) + 1;
                        if (!used[Node(i, 0, false)]) continue;
                        if (fixedZ0[i])
                        {
                            double reaction = ku[dz] - load[dz]; // support force on the member along +z
                            if (reaction < -1e-12 * res.AppliedForce) { fixedZ0[i] = false; changed = true; }
                        }
                        else if (u[dz] < 0) { fixedZ0[i] = true; changed = true; }
                    }
                double uMax = pairs.Count > 0 ? u.Max(v => Math.Abs(v)) : 0;
                for (int q = 0; q < pairs.Count; q++)
                {
                    // closed: the spring must push the bodies apart (upper above lower would mean tension); open: no overlap
                    double gap = u[pairs[q].up] - u[pairs[q].lo];
                    if (pairOn[q] && gap > 1e-12 * uMax) { pairOn[q] = false; changed = true; }
                    else if (!pairOn[q] && gap < 0) { pairOn[q] = true; changed = true; }
                }
                if (!changed) break;
            }
            res.Ur = new double[nr, nz]; res.Uz = new double[nr, nz];
            for (int i = 0; i < nr; i++)
                for (int k = 0; k < nz; k++)
                {
                    int n = Node(i, k, true);
                    if (!used[n] && split && k == kI) n = Node(i, k, false);
                    res.Ur[i, k] = used[n] ? u[2 * n] : double.NaN; res.Uz[i, k] = used[n] ? u[2 * n + 1] : double.NaN;
                }
            if (split)
            {
                res.UzInterfaceLower = new double[nr];
                for (int i = 0; i < nr; i++) { int n = Node(i, kI, false); res.UzInterfaceLower[i] = used[n] ? u[2 * n + 1] : double.NaN; }
                bool closed = true;
                for (int q = 0; q < pairs.Count; q++)
                {
                    if (!pairOn[q]) { closed = false; continue; }
                    if (closed) res.InterfaceContactRadius = rs[pairs[q].i];
                }
            }
            bool inner = true;
            for (int i = 0; i < nr; i++)
            {
                if (!fixedZ0[i] || m.Bottom == BottomCondition.None || !used[Node(i, 0, false)]) { inner = false; continue; }
                int dz = 2 * Node(i, 0, false) + 1;
                res.ContactForce += ku[dz] - load[dz];
                if (inner) res.ContactRadius = rs[i];
            }
            return res;
        }

        static double[] SolveOnce(AxisymmetricModel m, double[,] d, List<(int i, int k, int[] dofs, double scale)> cells, int ndof, double[] load, bool[] fix, double[] spring,
            List<(int lo, int up, double k)> links, CancellationToken ct, out double[] ku)
        {
            var rs = m.Rs; var zs = m.Zs;
            var eq = new int[ndof];
            int neq = 0;
            for (int q = 0; q < ndof; q++) eq[q] = fix[q] ? -1 : neq++;
            if (neq == 0) throw new InvalidOperationException("Every DOF is fixed.");
            int band = 0;
            foreach (var c in cells)
            {
                int lo = int.MaxValue, hi = -1;
                foreach (var q in c.dofs) if (eq[q] >= 0) { lo = Math.Min(lo, eq[q]); hi = Math.Max(hi, eq[q]); }
                if (hi >= 0) band = Math.Max(band, hi - lo);
            }
            foreach (var l in links) if (eq[l.lo] >= 0 && eq[l.up] >= 0) band = Math.Max(band, Math.Abs(eq[l.up] - eq[l.lo]));
            var k8 = new BandedLdlt(neq, band);
            var elems = new List<(int[] dofs, double[] ke)>();
            foreach (var c in cells)
            {
                var ke = Element(rs[c.i], rs[c.i + 1], zs[c.k], zs[c.k + 1], d);
                if (c.scale != 1) for (int q = 0; q < 64; q++) ke[q] *= c.scale;
                elems.Add((c.dofs, ke));
                for (int r = 0; r < 8; r++)
                {
                    int er = eq[c.dofs[r]];
                    if (er < 0) continue;
                    for (int cc = r; cc < 8; cc++)
                    {
                        int ec = eq[c.dofs[cc]];
                        if (ec < 0) continue;
                        k8.Add(er, ec, ke[r * 8 + cc]);
                    }
                }
            }
            for (int q = 0; q < ndof; q++) if (spring[q] != 0 && eq[q] >= 0) k8.Add(eq[q], eq[q], spring[q]);
            foreach (var l in links)
            {
                int a = eq[l.lo], b = eq[l.up];
                if (a >= 0) k8.Add(a, a, l.k);
                if (b >= 0) k8.Add(b, b, l.k);
                if (a >= 0 && b >= 0) k8.Add(a, b, -l.k);
            }
            var rhs = new double[neq];
            for (int q = 0; q < ndof; q++) if (eq[q] >= 0) rhs[eq[q]] = load[q];
            k8.Factor(ct);
            var x = k8.Solve(rhs);
            var u = new double[ndof];
            for (int q = 0; q < ndof; q++) if (eq[q] >= 0) u[q] = x[eq[q]];
            ku = new double[ndof];
            foreach (var (dofs, ke) in elems)
                for (int r = 0; r < 8; r++)
                {
                    double s = 0;
                    for (int c = 0; c < 8; c++) s += ke[r * 8 + c] * u[dofs[c]];
                    ku[dofs[r]] += s;
                }
            for (int q = 0; q < ndof; q++) ku[q] += spring[q] * u[q];
            foreach (var l in links) { double s = l.k * (u[l.up] - u[l.lo]); ku[l.up] += s; ku[l.lo] -= s; }
            return u;
        }

        /// <summary>8 x 8 ring element stiffness (includes 2 pi), nodes (r1,z1), (r2,z1), (r2,z2), (r1,z2).</summary>
        internal static double[] Element(double r1, double r2, double z1, double z2, double[,] d)
        {
            double hr = r2 - r1, hz = z2 - z1, g = 1 / Math.Sqrt(3);
            int[] xi = { -1, 1, 1, -1 }, et = { -1, -1, 1, 1 };
            var ke = new double[64];
            for (int p = 0; p < 4; p++)
            {
                double s = xi[p] * g, t = et[p] * g;
                double r = r1 + (s + 1) / 2 * hr;
                var b = new double[4, 8];
                for (int a = 0; a < 4; a++)
                {
                    double n = 0.25 * (1 + s * xi[a]) * (1 + t * et[a]);
                    double dr = 0.25 * xi[a] * (1 + t * et[a]) * 2 / hr, dz = 0.25 * et[a] * (1 + s * xi[a]) * 2 / hz;
                    b[0, 2 * a] = dr; b[1, 2 * a + 1] = dz; b[2, 2 * a] = n / r; b[3, 2 * a] = dz; b[3, 2 * a + 1] = dr;
                }
                double w = 2 * Math.PI * r * hr * hz / 4;
                for (int i = 0; i < 8; i++)
                    for (int j = 0; j < 8; j++)
                    {
                        double v = 0;
                        for (int q = 0; q < 4; q++)
                        {
                            if (b[q, i] == 0) continue;
                            for (int q2 = 0; q2 < 4; q2++) v += b[q, i] * d[q, q2] * b[q2, j];
                        }
                        ke[i * 8 + j] += v * w;
                    }
            }
            return ke;
        }

        internal static double Gauss2(double a, double b, Func<double, double> f)
        {
            double c = (a + b) / 2, h = (b - a) / 2, g = h / Math.Sqrt(3);
            return h * (f(c - g) + f(c + g));
        }

        /// <summary>Grid from a to b with the first cell h0 and cells growing by a ratio (geometric grading).</summary>
        public static double[] Graded(double a, double b, double h0, double ratio, double hMax)
        {
            var list = new List<double> { a };
            double h = h0, x = a;
            while (x + h < b - 1e-9 * (b - a)) { x += h; list.Add(x); h = Math.Min(hMax, h * ratio); }
            if (b - list[list.Count - 1] < 0.3 * h && list.Count > 1) list[list.Count - 1] = b; else list.Add(b);
            return list.ToArray();
        }

        /// <summary>Uniform grid lines through the breakpoints with spacing about h.</summary>
        public static double[] Lines(double h, params double[] bp) => SolidModel.Grid(h, bp);
    }
}

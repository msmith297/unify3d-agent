using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SwAgent.Mold.Structural.Fe.Solid
{
    /// <summary>An axis-aligned box used to pick nodes, faces or cells (inclusive, with a small tolerance).</summary>
    public struct Box3
    {
        public double X1, X2, Y1, Y2, Z1, Z2;
        public Box3(double x1, double x2, double y1, double y2, double z1, double z2) { X1 = x1; X2 = x2; Y1 = y1; Y2 = y2; Z1 = z1; Z2 = z2; }
        public bool Contains(double x, double y, double z, double tol)
            => x >= X1 - tol && x <= X2 + tol && y >= Y1 - tol && y <= Y2 + tol && z >= Z1 - tol && z <= Z2 + tol;
        public static Box3 All => new Box3(double.MinValue, double.MaxValue, double.MinValue, double.MaxValue, double.MinValue, double.MaxValue);
    }

    /// <summary>Zero displacement of the chosen components at every node in the box.</summary>
    public sealed class SolidSupport
    {
        public Box3 Region;
        public bool Ux, Uy, Uz;
        public SolidSupport(Box3 region, bool ux, bool uy, bool uz) { Region = region; Ux = ux; Uy = uy; Uz = uz; }
    }

    /// <summary>
    /// Load on the exposed faces (bordering a void cell or the outside) whose centre lies in Region and whose outward normal is
    /// Face (0..5 = -x, +x, -y, +y, -z, +z; -1 = any). Pressure pushes into the solid; Traction (force per area, evaluated at
    /// the face's nodes and integrated consistently, exact for linear fields) is added to it.
    /// </summary>
    public sealed class SolidSurfaceLoad
    {
        public Box3 Region;
        public int Face = -1;
        public double Pressure;
        public Func<double, double, double, double[]> Traction;
    }

    /// <summary>
    /// Linear-elastic 3D solid on a rectangular grid of cells Xs x Ys x Zs; Solid(xc, yc, zc) says whether the cell with that centre
    /// is material (pockets and steps are voids). Hex8 elements with incompatible modes, supports eliminated, PCG with IC(0).
    /// Used to validate the plate tables (wall root, thick plates); not part of the tool's fast path.
    /// </summary>
    public sealed class SolidModel
    {
        public double[] Xs, Ys, Zs;
        public Func<double, double, double, bool> Solid = (x, y, z) => true;
        public double E = 1, Nu = 0.3;
        public List<SolidSupport> Supports = new List<SolidSupport>();
        public List<SolidSurfaceLoad> Loads = new List<SolidSurfaceLoad>();
        public double Tolerance = 1e-9;
        public int MaxIterations = 50000;

        public static double[] Grid(double length, int n) => Enumerable.Range(0, n + 1).Select(i => length * i / n).ToArray();

        /// <summary>Grid lines through the given breakpoints with about h between lines.</summary>
        public static double[] Grid(double h, params double[] breakpoints)
        {
            var bp = breakpoints.Distinct().OrderBy(v => v).ToArray();
            var xs = new List<double> { bp[0] };
            for (int s = 0; s + 1 < bp.Length; s++)
            {
                double len = bp[s + 1] - bp[s];
                if (len <= 0) continue;
                int n = Math.Max(1, (int)Math.Round(len / h));
                for (int k = 1; k <= n; k++) xs.Add(k == n ? bp[s + 1] : bp[s] + len * k / n);
            }
            return xs.ToArray();
        }
    }

    public sealed class SolidResult
    {
        public double[] Xs, Ys, Zs;
        /// <summary>Displacements per grid node [i, j, k, component]; NaN where no material.</summary>
        public double[,,,] U;
        public int Dofs, Iterations;
        public double RelativeResidual;
        public bool Converged;
        public string Preconditioner;
        public TimeSpan Elapsed;
        /// <summary>Sum of the applied nodal loads and of the support reactions, per component (equilibrium check).</summary>
        public double[] AppliedTotal = new double[3], ReactionTotal = new double[3];

        public double At(int i, int j, int k, int c) => U[i, j, k, c];

        public int Index(double[] g, double v)
        {
            int best = 0;
            for (int i = 1; i < g.Length; i++) if (Math.Abs(g[i] - v) < Math.Abs(g[best] - v)) best = i;
            return best;
        }

        /// <summary>Displacement component c at the grid node nearest (x, y, z).</summary>
        public double Nearest(double x, double y, double z, int c) => U[Index(Xs, x), Index(Ys, y), Index(Zs, z), c];
    }

    public static class SolidSolver
    {
        public static SolidResult Solve(SolidModel m, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            double[] xs = m.Xs, ys = m.Ys, zs = m.Zs;
            int nx = xs.Length - 1, ny = ys.Length - 1, nz = zs.Length - 1;
            int NX = nx + 1, NY = ny + 1, NZ = nz + 1;
            double size = Math.Max(xs[nx] - xs[0], Math.Max(ys[ny] - ys[0], zs[nz] - zs[0])), tol = 1e-9 * size;
            int Node(int i, int j, int k) => (i * NY + j) * NZ + k;
            int nNodes = NX * NY * NZ;

            var cell = new bool[nx, ny, nz];
            var used = new bool[nNodes];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    for (int k = 0; k < nz; k++)
                        if (m.Solid((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2, (zs[k] + zs[k + 1]) / 2))
                        {
                            cell[i, j, k] = true;
                            for (int c = 0; c < 8; c++) used[Node(i + (Hex8.Xi[c] + 1) / 2, j + (Hex8.Et[c] + 1) / 2, k + (Hex8.Ze[c] + 1) / 2)] = true;
                        }

            // supports and equation numbers
            var fix = new bool[3 * nNodes];
            for (int i = 0; i < NX; i++)
                for (int j = 0; j < NY; j++)
                    for (int k = 0; k < NZ; k++)
                    {
                        int n = Node(i, j, k);
                        if (!used[n]) continue;
                        foreach (var s in m.Supports)
                            if (s.Region.Contains(xs[i], ys[j], zs[k], tol))
                            {
                                if (s.Ux) fix[3 * n] = true;
                                if (s.Uy) fix[3 * n + 1] = true;
                                if (s.Uz) fix[3 * n + 2] = true;
                            }
                    }
            var eq = new int[3 * nNodes];
            int neq = 0;
            for (int n = 0; n < nNodes; n++)
                for (int c = 0; c < 3; c++) eq[3 * n + c] = used[n] && !fix[3 * n + c] ? neq++ : -1;
            if (neq == 0) throw new InvalidOperationException("Every DOF is fixed.");

            // pattern: node neighbours sharing a cell
            var nb = new int[nNodes]; // 27-bit mask of neighbour offsets
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    for (int k = 0; k < nz; k++)
                    {
                        if (!cell[i, j, k]) continue;
                        for (int ca = 0; ca < 8; ca++)
                        {
                            int ia = (Hex8.Xi[ca] + 1) / 2, ja = (Hex8.Et[ca] + 1) / 2, ka = (Hex8.Ze[ca] + 1) / 2;
                            int na = Node(i + ia, j + ja, k + ka);
                            for (int cb = 0; cb < 8; cb++)
                            {
                                int di = (Hex8.Xi[cb] + 1) / 2 - ia, dj = (Hex8.Et[cb] + 1) / 2 - ja, dk = (Hex8.Ze[cb] + 1) / 2 - ka;
                                nb[na] |= 1 << ((di + 1) * 9 + (dj + 1) * 3 + (dk + 1));
                            }
                        }
                    }
            var rowPtr = new int[neq + 1];
            var cols = new List<int>(neq * 81);
            int row = 0;
            for (int n = 0; n < nNodes; n++)
            {
                if (!used[n]) continue;
                int i = n / (NY * NZ), j = n / NZ % NY, k = n % NZ;
                for (int c = 0; c < 3; c++)
                {
                    if (eq[3 * n + c] < 0) continue;
                    for (int bit = 0; bit < 27; bit++)
                    {
                        if ((nb[n] & (1 << bit)) == 0) continue;
                        int nn = Node(i + bit / 9 - 1, j + bit / 3 % 3 - 1, k + bit % 3 - 1);
                        for (int d = 0; d < 3; d++) if (eq[3 * nn + d] >= 0) cols.Add(eq[3 * nn + d]);
                    }
                    rowPtr[++row] = cols.Count;
                }
            }
            var a = new Csr(neq, rowPtr, cols.ToArray());

            // assembly
            var kCache = new Dictionary<(long, long, long), double[]>();
            var ndofs = new int[24];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    for (int k = 0; k < nz; k++)
                    {
                        if (!cell[i, j, k]) continue;
                        double hx = xs[i + 1] - xs[i], hy = ys[j + 1] - ys[j], hz = zs[k + 1] - zs[k];
                        var key = ((long)Math.Round(hx / size * 1e10), (long)Math.Round(hy / size * 1e10), (long)Math.Round(hz / size * 1e10));
                        if (!kCache.TryGetValue(key, out var ke)) kCache[key] = ke = Hex8.Stiffness(hx, hy, hz, m.E, m.Nu, out _);
                        for (int c = 0; c < 8; c++)
                        {
                            int n = Node(i + (Hex8.Xi[c] + 1) / 2, j + (Hex8.Et[c] + 1) / 2, k + (Hex8.Ze[c] + 1) / 2);
                            for (int d = 0; d < 3; d++) ndofs[3 * c + d] = eq[3 * n + d];
                        }
                        for (int r = 0; r < 24; r++)
                        {
                            int er = ndofs[r];
                            if (er < 0) continue;
                            for (int s = 0; s < 24; s++)
                            {
                                int es = ndofs[s];
                                if (es < 0) continue;
                                a.Val[a.Find(er, es)] += ke[r * 24 + s];
                            }
                        }
                    }

            // loads
            var fFull = new double[3 * nNodes];
            foreach (var load in m.Loads) ApplySurfaceLoad(load, cell, xs, ys, zs, tol, (i, j, k) => Node(i, j, k), fFull);
            var rhs = new double[neq];
            for (int d = 0; d < 3 * nNodes; d++) if (eq[d] >= 0) rhs[eq[d]] = fFull[d];

            var sol = Pcg.Solve(a, rhs, m.Tolerance, m.MaxIterations, ct);
            var res = new SolidResult
            {
                Xs = xs, Ys = ys, Zs = zs, U = new double[NX, NY, NZ, 3], Dofs = neq, Iterations = sol.Iterations,
                RelativeResidual = sol.RelativeResidual, Converged = sol.Converged, Preconditioner = sol.Preconditioner,
            };
            var uFull = new double[3 * nNodes];
            for (int n = 0; n < nNodes; n++)
            {
                int i = n / (NY * NZ), j = n / NZ % NY, k = n % NZ;
                for (int c = 0; c < 3; c++)
                {
                    double v = !used[n] ? double.NaN : eq[3 * n + c] >= 0 ? sol.X[eq[3 * n + c]] : 0;
                    res.U[i, j, k, c] = v;
                    if (used[n]) uFull[3 * n + c] = v;
                }
            }

            // reactions: K u - f at the supported DOFs, element by element
            var ku = new double[3 * nNodes];
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    for (int k = 0; k < nz; k++)
                    {
                        if (!cell[i, j, k]) continue;
                        double hx = xs[i + 1] - xs[i], hy = ys[j + 1] - ys[j], hz = zs[k + 1] - zs[k];
                        var ke = kCache[((long)Math.Round(hx / size * 1e10), (long)Math.Round(hy / size * 1e10), (long)Math.Round(hz / size * 1e10))];
                        var gd = new int[24];
                        for (int c = 0; c < 8; c++)
                        {
                            int n = Node(i + (Hex8.Xi[c] + 1) / 2, j + (Hex8.Et[c] + 1) / 2, k + (Hex8.Ze[c] + 1) / 2);
                            for (int d = 0; d < 3; d++) gd[3 * c + d] = 3 * n + d;
                        }
                        for (int r = 0; r < 24; r++)
                        {
                            double s = 0;
                            for (int q = 0; q < 24; q++) s += ke[r * 24 + q] * uFull[gd[q]];
                            ku[gd[r]] += s;
                        }
                    }
            for (int d = 0; d < 3 * nNodes; d++)
            {
                res.AppliedTotal[d % 3] += fFull[d];
                if (fix[d] && used[d / 3]) res.ReactionTotal[d % 3] += ku[d] - fFull[d];
            }
            res.Elapsed = sw.Elapsed;
            return res;
        }

        // Face node offsets (i, j, k) for each face direction, in a cyclic order around the face.
        static readonly int[][,] FaceNodes =
        {
            new[,] { { 0, 0, 0 }, { 0, 1, 0 }, { 0, 1, 1 }, { 0, 0, 1 } }, // -x
            new[,] { { 1, 0, 0 }, { 1, 1, 0 }, { 1, 1, 1 }, { 1, 0, 1 } }, // +x
            new[,] { { 0, 0, 0 }, { 1, 0, 0 }, { 1, 0, 1 }, { 0, 0, 1 } }, // -y
            new[,] { { 0, 1, 0 }, { 1, 1, 0 }, { 1, 1, 1 }, { 0, 1, 1 } }, // +y
            new[,] { { 0, 0, 0 }, { 1, 0, 0 }, { 1, 1, 0 }, { 0, 1, 0 } }, // -z
            new[,] { { 0, 0, 1 }, { 1, 0, 1 }, { 1, 1, 1 }, { 0, 1, 1 } }, // +z
        };

        static void ApplySurfaceLoad(SolidSurfaceLoad load, bool[,,] cell, double[] xs, double[] ys, double[] zs, double tol, Func<int, int, int, int> node, double[] f)
        {
            int nx = xs.Length - 1, ny = ys.Length - 1, nz = zs.Length - 1;
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                    for (int k = 0; k < nz; k++)
                    {
                        if (!cell[i, j, k]) continue;
                        for (int face = 0; face < 6; face++)
                        {
                            if (load.Face >= 0 && load.Face != face) continue;
                            int axis = face / 2, dir = face % 2 == 0 ? -1 : 1;
                            int ni = i + (axis == 0 ? dir : 0), nj = j + (axis == 1 ? dir : 0), nk = k + (axis == 2 ? dir : 0);
                            bool exposed = ni < 0 || nj < 0 || nk < 0 || ni >= nx || nj >= ny || nk >= nz || !cell[ni, nj, nk];
                            if (!exposed) continue;
                            var fn = FaceNodes[face];
                            double cx = 0, cy = 0, cz = 0;
                            var px = new double[4]; var py = new double[4]; var pz = new double[4]; var ids = new int[4];
                            for (int c = 0; c < 4; c++)
                            {
                                int ii = i + fn[c, 0], jj = j + fn[c, 1], kk = k + fn[c, 2];
                                px[c] = xs[ii]; py[c] = ys[jj]; pz[c] = zs[kk]; ids[c] = node(ii, jj, kk);
                                cx += px[c] / 4; cy += py[c] / 4; cz += pz[c] / 4;
                            }
                            if (!load.Region.Contains(cx, cy, cz, tol)) continue;
                            double area = axis == 0 ? (ys[j + 1] - ys[j]) * (zs[k + 1] - zs[k]) : axis == 1 ? (xs[i + 1] - xs[i]) * (zs[k + 1] - zs[k]) : (xs[i + 1] - xs[i]) * (ys[j + 1] - ys[j]);
                            // nodal tractions: pressure along -outward normal plus the traction field
                            var t = new double[4, 3];
                            for (int c = 0; c < 4; c++)
                            {
                                t[c, axis] = -load.Pressure * dir;
                                if (load.Traction != null)
                                {
                                    var tv = load.Traction(px[c], py[c], pz[c]);
                                    for (int d = 0; d < 3; d++) t[c, d] += tv[d];
                                }
                            }
                            // consistent bilinear face: M = A/36 [4 2 1 2; 2 4 2 1; 1 2 4 2; 2 1 2 4] (cyclic node order)
                            for (int c = 0; c < 4; c++)
                                for (int e = 0; e < 4; e++)
                                {
                                    int dist = Math.Abs(c - e);
                                    double w = (dist == 0 ? 4 : dist == 2 ? 1 : 2) * area / 36;
                                    for (int d = 0; d < 3; d++) f[3 * ids[c] + d] += w * t[e, d];
                                }
                        }
                    }
        }
    }
}

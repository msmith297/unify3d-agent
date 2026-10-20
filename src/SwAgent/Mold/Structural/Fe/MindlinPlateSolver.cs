using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SwAgent.Mold.Structural.Fe
{
    /// <summary>
    /// Linear Mindlin plate solver: MITC4 on a rectangular grid, supports eliminated (not penalised: penalties hurt the
    /// conditioning of thin plates), banded LDL^T with the nodes numbered along the short side first, so the half-bandwidth
    /// is about 3 (n_short + 2). A 48 x 96 grid (about 14,000 DOF) solves in well under a second. Pure: no SolidWorks types.
    /// </summary>
    public static class MindlinPlateSolver
    {
        public static PlateResult Solve(PlateModel m, CancellationToken ct = default)
        {
            m.Validate();
            var sw = Stopwatch.StartNew();
            var fe = new Assembly(m);
            var res = fe.Run(ct);
            res.Elapsed = sw.Elapsed;
            return res;
        }

        sealed class Assembly
        {
            readonly PlateModel m;
            readonly double[] xs, ys;
            readonly int nxN, nyN, nn, ndof;
            readonly bool yFast;
            readonly bool[] fixedDof;
            readonly int[] eq;
            readonly int neq, band;
            readonly Dictionary<(long, long), double[]> kCache = new Dictionary<(long, long), double[]>();
            readonly double hRef;
            // unilateral state per (element, foundation index)
            readonly Dictionary<(int, int), bool> active = new Dictionary<(int, int), bool>();

            public Assembly(PlateModel model)
            {
                m = model;
                var lx = new List<double>(m.XLines);
                var ly = new List<double>(m.YLines);
                if (m.SnapToFeatures)
                {
                    foreach (var p in m.Pressures) { lx.Add(p.X1); lx.Add(p.X2); ly.Add(p.Y1); ly.Add(p.Y2); }
                    foreach (var r in m.Fixed) { lx.Add(r.X1); lx.Add(r.X2); ly.Add(r.Y1); ly.Add(r.Y2); }
                    foreach (var r in m.Foundations) { lx.Add(r.X1); lx.Add(r.X2); ly.Add(r.Y1); ly.Add(r.Y2); }
                    foreach (var l in m.Lines) (l.Axis == 'x' ? lx : ly).Add(l.Position);
                }
                double shortSide = Math.Min(m.A, m.B);
                hRef = m.ElementSize > 0 ? m.ElementSize : shortSide / Math.Max(1, m.ElementsOnShortSide);
                bool counts = m.Nx > 0 && m.Ny > 0;
                xs = GridLines.Build(m.A, lx, hRef, counts ? m.Nx : 0);
                ys = GridLines.Build(m.B, ly, hRef, counts ? m.Ny : 0);
                nxN = xs.Length; nyN = ys.Length; nn = nxN * nyN; ndof = 3 * nn;
                yFast = nyN <= nxN;

                fixedDof = new bool[ndof];
                ApplyEdge(m.X0, true, 0); ApplyEdge(m.XA, true, nxN - 1);
                ApplyEdge(m.Y0, false, 0); ApplyEdge(m.YB, false, nyN - 1);
                foreach (var l in m.Lines) ApplyLine(l);
                foreach (var r in m.Fixed)
                {
                    double tol = 1e-9 * Math.Max(m.A, m.B);
                    for (int i = 0; i < nxN; i++)
                        for (int j = 0; j < nyN; j++)
                            if (xs[i] >= r.X1 - tol && xs[i] <= r.X2 + tol && ys[j] >= r.Y1 - tol && ys[j] <= r.Y2 + tol)
                                fixedDof[3 * Node(i, j)] = true;
                }

                eq = new int[ndof];
                int c = 0;
                for (int d = 0; d < ndof; d++) eq[d] = fixedDof[d] ? -1 : c++;
                neq = c;
                if (neq == 0) throw new InvalidOperationException("Every DOF is fixed.");
                int bw = 0;
                for (int ex = 0; ex < nxN - 1; ex++)
                    for (int ey = 0; ey < nyN - 1; ey++)
                    {
                        int lo = int.MaxValue, hi = -1;
                        foreach (var d in ElementDofs(ex, ey)) { int e = eq[d]; if (e < 0) continue; lo = Math.Min(lo, e); hi = Math.Max(hi, e); }
                        if (hi >= 0) bw = Math.Max(bw, hi - lo);
                    }
                band = bw;
            }

            int Node(int i, int j) => yFast ? i * nyN + j : j * nxN + i;

            int[] ElementNodes(int ex, int ey) => new[] { Node(ex, ey), Node(ex + 1, ey), Node(ex + 1, ey + 1), Node(ex, ey + 1) };

            int[] ElementDofs(int ex, int ey)
            {
                var n = ElementNodes(ex, ey);
                var d = new int[12];
                for (int k = 0; k < 4; k++) { d[3 * k] = 3 * n[k]; d[3 * k + 1] = 3 * n[k] + 1; d[3 * k + 2] = 3 * n[k] + 2; }
                return d;
            }

            void ApplyEdge(EdgeBc bc, bool xEdge, int index)
            {
                int count = xEdge ? nyN : nxN;
                for (int k = 0; k < count; k++)
                {
                    int n = xEdge ? Node(index, k) : Node(k, index);
                    int normal = xEdge ? 1 : 2, tangential = xEdge ? 2 : 1;
                    switch (bc)
                    {
                        case EdgeBc.SimpleHard: fixedDof[3 * n] = true; fixedDof[3 * n + tangential] = true; break;
                        case EdgeBc.SimpleSoft: fixedDof[3 * n] = true; break;
                        case EdgeBc.Clamped: fixedDof[3 * n] = fixedDof[3 * n + 1] = fixedDof[3 * n + 2] = true; break;
                        case EdgeBc.Symmetric: fixedDof[3 * n + normal] = true; break;
                    }
                }
            }

            void ApplyLine(LineSupport l)
            {
                var g = l.Axis == 'x' ? xs : ys;
                var other = l.Axis == 'x' ? ys : xs;
                int best = 0;
                for (int k = 1; k < g.Length; k++) if (Math.Abs(g[k] - l.Position) < Math.Abs(g[best] - l.Position)) best = k;
                double tol = 1e-9 * Math.Max(m.A, m.B);
                for (int k = 0; k < other.Length; k++)
                {
                    if (other[k] < l.From - tol || other[k] > l.To + tol) continue;
                    int n = l.Axis == 'x' ? Node(best, k) : Node(k, best);
                    fixedDof[3 * n] = true;
                }
            }

            double[] ElementK(double hx, double hy)
            {
                var key = ((long)Math.Round(hx / hRef * 1e9), (long)Math.Round(hy / hRef * 1e9));
                if (!kCache.TryGetValue(key, out var k))
                    kCache[key] = k = Mitc4.Stiffness(hx, hy, m.T, m.E, m.Nu, m.ShearFactor);
                return k;
            }

            (int ex, int ey, double xi, double et) Locate(double x, double y)
            {
                int ex = PlateResult.Locate(xs, x), ey = PlateResult.Locate(ys, y);
                double xi = 2 * (x - xs[ex]) / (xs[ex + 1] - xs[ex]) - 1, et = 2 * (y - ys[ey]) / (ys[ey + 1] - ys[ey]) - 1;
                return (ex, ey, Math.Max(-1, Math.Min(1, xi)), Math.Max(-1, Math.Min(1, et)));
            }

            // Full (unreduced) contributions, element by element: used for assembly and for the reactions.
            void ForEachContribution(Action<int[], double[], int> addMatrix, double[] f)
            {
                for (int ex = 0; ex < nxN - 1; ex++)
                    for (int ey = 0; ey < nyN - 1; ey++)
                    {
                        double x0 = xs[ex], x1 = xs[ex + 1], y0 = ys[ey], y1 = ys[ey + 1];
                        var dofs = ElementDofs(ex, ey);
                        addMatrix(dofs, ElementK(x1 - x0, y1 - y0), 12);
                        var k16 = new double[16];
                        bool any = false;
                        for (int r = 0; r < m.Foundations.Count; r++)
                        {
                            var fr = m.Foundations[r];
                            if (fr.X2 <= x0 || fr.X1 >= x1 || fr.Y2 <= y0 || fr.Y1 >= y1) continue;
                            if (fr.Unilateral && active.TryGetValue((ElementIndex(ex, ey), r), out var on) && !on) continue;
                            Mitc4.FoundationMatrix(x0, x1, y0, y1, fr.X1, fr.X2, fr.Y1, fr.Y2, fr.K, k16);
                            any = true;
                        }
                        if (any) addMatrix(new[] { dofs[0], dofs[3], dofs[6], dofs[9] }, k16, 4);
                        if (f != null)
                        {
                            var f4 = new double[4];
                            foreach (var p in m.Pressures)
                            {
                                if (p.X2 <= x0 || p.X1 >= x1 || p.Y2 <= y0 || p.Y1 >= y1) continue;
                                Mitc4.PatchLoad(x0, x1, y0, y1, p.X1, p.X2, p.Y1, p.Y2, p.P, f4);
                            }
                            if (m.PressureField != null)
                            {
                                double q = m.PressureField((x0 + x1) / 2, (y0 + y1) / 2);
                                if (q != 0) for (int k = 0; k < 4; k++) f4[k] += q * (x1 - x0) * (y1 - y0) / 4;
                            }
                            for (int k = 0; k < 4; k++) f[dofs[3 * k]] += f4[k];
                        }
                    }
                foreach (var s in m.Springs)
                {
                    var (ex, ey, xi, et) = Locate(s.X, s.Y);
                    var dofs = ElementDofs(ex, ey);
                    var k16 = new double[16];
                    for (int i = 0; i < 4; i++)
                        for (int j = 0; j < 4; j++) k16[i * 4 + j] = s.K * Mitc4.N(i, xi, et) * Mitc4.N(j, xi, et);
                    addMatrix(new[] { dofs[0], dofs[3], dofs[6], dofs[9] }, k16, 4);
                }
                if (f != null)
                    foreach (var p in m.PointLoads)
                    {
                        var (ex, ey, xi, et) = Locate(p.X, p.Y);
                        var dofs = ElementDofs(ex, ey);
                        for (int i = 0; i < 4; i++) f[dofs[3 * i]] += p.P * Mitc4.N(i, xi, et);
                    }
            }

            int ElementIndex(int ex, int ey) => ex * (nyN - 1) + ey;

            double[] SolveOnce(CancellationToken ct, out double[] fFull)
            {
                var k = new BandedLdlt(neq, band);
                var f = new double[ndof];
                ForEachContribution((dofs, ke, n) =>
                {
                    for (int r = 0; r < n; r++)
                    {
                        int er = eq[dofs[r]];
                        if (er < 0) continue;
                        for (int c = r; c < n; c++)
                        {
                            int ec = eq[dofs[c]];
                            if (ec < 0) continue;
                            double v = ke[r * n + c];
                            if (v == 0) continue;
                            if (r == c) k.Add(er, er, v);
                            else if (er == ec) k.Add(er, er, 2 * v);
                            else k.Add(er, ec, v);
                        }
                    }
                }, f);
                var rhs = new double[neq];
                for (int d = 0; d < ndof; d++) if (eq[d] >= 0) rhs[eq[d]] = f[d];
                k.Factor(ct);
                var x = k.Solve(rhs);
                var u = new double[ndof];
                for (int d = 0; d < ndof; d++) if (eq[d] >= 0) u[d] = x[eq[d]];
                fFull = f;
                return u;
            }

            public PlateResult Run(CancellationToken ct)
            {
                bool unilateral = m.Foundations.Any(r => r.Unilateral);
                double[] u = null, f = null;
                int passes = 0;
                double contact = 1;
                while (true)
                {
                    passes++;
                    u = SolveOnce(ct, out f);
                    if (!unilateral || passes >= m.MaxPasses) break;
                    bool changed = false;
                    double onArea = 0, allArea = 0;
                    for (int ex = 0; ex < nxN - 1; ex++)
                        for (int ey = 0; ey < nyN - 1; ey++)
                        {
                            double x0 = xs[ex], x1 = xs[ex + 1], y0 = ys[ey], y1 = ys[ey + 1];
                            var nodes = ElementNodes(ex, ey);
                            double wMean = nodes.Average(n => u[3 * n]);
                            for (int r = 0; r < m.Foundations.Count; r++)
                            {
                                var fr = m.Foundations[r];
                                if (!fr.Unilateral || fr.X2 <= x0 || fr.X1 >= x1 || fr.Y2 <= y0 || fr.Y1 >= y1) continue;
                                double area = (Math.Min(x1, fr.X2) - Math.Max(x0, fr.X1)) * (Math.Min(y1, fr.Y2) - Math.Max(y0, fr.Y1));
                                bool want = wMean > 0;
                                var key = (ElementIndex(ex, ey), r);
                                bool was = !active.TryGetValue(key, out var a) || a;
                                if (want != was) changed = true;
                                active[key] = want;
                                allArea += area; if (want) onArea += area;
                            }
                        }
                    contact = allArea > 0 ? onArea / allArea : 1;
                    if (!changed) break;
                }
                return Recover(u, f, passes, contact);
            }

            PlateResult Recover(double[] u, double[] f, int passes, double contact)
            {
                var res = new PlateResult
                {
                    Xs = xs, Ys = ys, Dofs = neq, Band = band, Passes = passes, ContactFraction = contact, Thickness = m.T,
                    W = new double[nxN, nyN], Mx = new double[nxN, nyN], My = new double[nxN, nyN], Mxy = new double[nxN, nyN],
                    MPrincipal = new double[nxN, nyN], Reactions = new double[nxN, nyN],
                };
                for (int i = 0; i < nxN; i++)
                    for (int j = 0; j < nyN; j++) res.W[i, j] = u[3 * Node(i, j)];

                // Reactions R = K u - f (all contributions), and the forces of foundations and springs on the plate.
                var ku = new double[ndof];
                ForEachContribution((dofs, ke, n) =>
                {
                    for (int r = 0; r < n; r++)
                    {
                        double s = 0;
                        for (int c = 0; c < n; c++) s += ke[r * n + c] * u[dofs[c]];
                        ku[dofs[r]] += s;
                    }
                }, null);
                double found = 0, spr = 0, foundMx = 0, foundMy = 0;
                // foundation force = sum over w rows of K_found u; recomputed separately for the report
                for (int ex = 0; ex < nxN - 1; ex++)
                    for (int ey = 0; ey < nyN - 1; ey++)
                    {
                        double x0 = xs[ex], x1 = xs[ex + 1], y0 = ys[ey], y1 = ys[ey + 1];
                        var k16 = new double[16];
                        bool any = false;
                        for (int r = 0; r < m.Foundations.Count; r++)
                        {
                            var fr = m.Foundations[r];
                            if (fr.X2 <= x0 || fr.X1 >= x1 || fr.Y2 <= y0 || fr.Y1 >= y1) continue;
                            if (fr.Unilateral && active.TryGetValue((ElementIndex(ex, ey), r), out var on) && !on) continue;
                            Mitc4.FoundationMatrix(x0, x1, y0, y1, fr.X1, fr.X2, fr.Y1, fr.Y2, fr.K, k16);
                            any = true;
                        }
                        if (!any) continue;
                        var nodes = ElementNodes(ex, ey);
                        double[] nx = { x0, x1, x1, x0 }, ny = { y0, y0, y1, y1 };
                        for (int a = 0; a < 4; a++)
                        {
                            double s = 0;
                            for (int b = 0; b < 4; b++) s += k16[a * 4 + b] * u[3 * nodes[b]];
                            found -= s; foundMx -= s * nx[a]; foundMy -= s * ny[a];
                        }
                    }
                double sprMx = 0, sprMy = 0;
                foreach (var s in m.Springs)
                {
                    double wv = res.WAt(s.X, s.Y);
                    spr -= s.K * wv; sprMx -= s.K * wv * s.X; sprMy -= s.K * wv * s.Y;
                }
                res.FoundationForce = found; res.SpringForce = spr;
                res.OtherMomentX = foundMx + sprMx; res.OtherMomentY = foundMy + sprMy;
                var rr = new double[ndof];
                for (int i = 0; i < nxN; i++)
                    for (int j = 0; j < nyN; j++)
                    {
                        int n = Node(i, j);
                        res.AppliedLoad += f[3 * n];
                        res.AppliedMomentX += f[3 * n] * xs[i]; res.AppliedMomentY += f[3 * n] * ys[j];
                        for (int k = 0; k < 3; k++) if (fixedDof[3 * n + k]) rr[3 * n + k] = ku[3 * n + k] - f[3 * n + k];
                        res.Reactions[i, j] = rr[3 * n];
                        res.SupportForce += rr[3 * n];
                        res.SupportMomentX += rr[3 * n] * xs[i]; res.SupportMomentY += rr[3 * n] * ys[j];
                    }

                RecoverMoments(u, rr, res);
                double wBest = 0, mBest = -1;
                for (int i = 0; i < nxN; i++)
                    for (int j = 0; j < nyN; j++)
                    {
                        if (Math.Abs(res.W[i, j]) > Math.Abs(wBest)) { wBest = res.W[i, j]; res.WMaxAt = (xs[i], ys[j]); }
                        double mp = PlateResult.Principal(res.Mx[i, j], res.My[i, j], res.Mxy[i, j]);
                        res.MPrincipal[i, j] = mp;
                        if (mp > mBest) { mBest = mp; res.SigmaMaxAt = (xs[i], ys[j]); }
                    }
                res.WMax = wBest; res.MMax = mBest; res.SigmaMax = 6 * mBest / (m.T * m.T);
                return res;
            }

            void RecoverMoments(double[] u, double[] rr, PlateResult res)
            {
                double d = m.D, nu = m.Nu;
                var sum = new double[nxN, nyN, 3];
                var cnt = new int[nxN, nyN];
                // free-edge nodes: the first Gauss row's values, extrapolated along the edge only
                var edgeSum = new double[nxN, nyN, 3];
                var edgeCnt = new int[nxN, nyN];
                double s3 = Math.Sqrt(3);
                bool freeX0 = m.X0 == EdgeBc.Free, freeXA = m.XA == EdgeBc.Free, freeY0 = m.Y0 == EdgeBc.Free, freeYB = m.YB == EdgeBc.Free;
                for (int ex = 0; ex < nxN - 1; ex++)
                    for (int ey = 0; ey < nyN - 1; ey++)
                    {
                        double hx = xs[ex + 1] - xs[ex], hy = ys[ey + 1] - ys[ey];
                        var dofs = ElementDofs(ex, ey);
                        var mg = new double[4, 3];
                        for (int g = 0; g < 4; g++)
                        {
                            var (xi, et) = Mitc4.Gauss(g);
                            var bb = Mitc4.Bb(xi, et, hx, hy);
                            double k0 = 0, k1 = 0, k2 = 0;
                            for (int c = 0; c < 12; c++)
                            {
                                double uc = u[dofs[c]];
                                if (uc == 0) continue;
                                k0 += bb[0, c] * uc; k1 += bb[1, c] * uc; k2 += bb[2, c] * uc;
                            }
                            mg[g, 0] = -d * (k0 + nu * k1);
                            mg[g, 1] = -d * (nu * k0 + k1);
                            mg[g, 2] = -d * (1 - nu) / 2 * k2;
                        }
                        int[] ni = { ex, ex + 1, ex + 1, ex }, nj = { ey, ey, ey + 1, ey + 1 };
                        for (int k = 0; k < 4; k++)
                        {
                            double xiN = Mitc4.XiN[k] * s3, etN = Mitc4.EtN[k] * s3;
                            for (int c = 0; c < 3; c++)
                            {
                                double v = 0;
                                for (int g = 0; g < 4; g++) v += Mitc4.N(g, xiN, etN) * mg[g, c];
                                sum[ni[k], nj[k], c] += v;
                            }
                            cnt[ni[k], nj[k]]++;
                        }
                        // Gauss rows next to free edges: pairs (g0, g3) at xi = -1/sqrt3, (g1, g2) at +1/sqrt3, (g0, g1) at eta = -, (g3, g2) at eta = +.
                        if (freeX0 && ex == 0) EdgeRow(edgeSum, edgeCnt, mg, 0, 3, ex, ey, ey + 1, true, s3);
                        if (freeXA && ex == nxN - 2) EdgeRow(edgeSum, edgeCnt, mg, 1, 2, ex + 1, ey, ey + 1, true, s3);
                        if (freeY0 && ey == 0) EdgeRow(edgeSum, edgeCnt, mg, 0, 1, ey, ex, ex + 1, false, s3);
                        if (freeYB && ey == nyN - 2) EdgeRow(edgeSum, edgeCnt, mg, 3, 2, ey + 1, ex, ex + 1, false, s3);
                    }
                for (int i = 0; i < nxN; i++)
                    for (int j = 0; j < nyN; j++)
                    {
                        if (edgeCnt[i, j] > 0)
                            for (int c = 0; c < 3; c++) sum[i, j, c] = edgeSum[i, j, c] / edgeCnt[i, j];
                        else
                            for (int c = 0; c < 3; c++) sum[i, j, c] /= cnt[i, j];
                        res.Mx[i, j] = sum[i, j, 0]; res.My[i, j] = sum[i, j, 1]; res.Mxy[i, j] = sum[i, j, 2];
                    }

                // natural conditions: Mn = 0 on free and simply supported edges; Mnt = 0 on free edges as well
                ZeroEdge(m.X0, true, 0, res); ZeroEdge(m.XA, true, nxN - 1, res);
                ZeroEdge(m.Y0, false, 0, res); ZeroEdge(m.YB, false, nyN - 1, res);

                // clamped edges: the normal moment from the nodal rotation reactions (Gauss extrapolation under-reads it by 12-40%)
                ClampedEdge(m.X0, true, 0, +1, rr, res); ClampedEdge(m.XA, true, nxN - 1, -1, rr, res);
                ClampedEdge(m.Y0, false, 0, +1, rr, res); ClampedEdge(m.YB, false, nyN - 1, -1, rr, res);

                res.EdgeMoments["x0"] = Enumerable.Range(0, nyN).Select(j => res.Mx[0, j]).ToArray();
                res.EdgeMoments["xa"] = Enumerable.Range(0, nyN).Select(j => res.Mx[nxN - 1, j]).ToArray();
                res.EdgeMoments["y0"] = Enumerable.Range(0, nxN).Select(i => res.My[i, 0]).ToArray();
                res.EdgeMoments["yb"] = Enumerable.Range(0, nxN).Select(i => res.My[i, nyN - 1]).ToArray();
            }

            // Values on a Gauss row (points ga, gb along the edge) extrapolated linearly to the row's two edge nodes.
            static void EdgeRow(double[,,] sum, int[,] cnt, double[,] mg, int ga, int gb, int fixedIndex, int k0, int k1, bool xEdge, double s3)
            {
                for (int c = 0; c < 3; c++)
                {
                    double va = mg[ga, c], vb = mg[gb, c];
                    // Gauss points at s = -1/sqrt3 (ga) and +1/sqrt3 (gb); nodes at s = -1 and +1
                    double atK0 = va * (1 + s3) / 2 + vb * (1 - s3) / 2;
                    double atK1 = va * (1 - s3) / 2 + vb * (1 + s3) / 2;
                    if (xEdge) { sum[fixedIndex, k0, c] += atK0; sum[fixedIndex, k1, c] += atK1; }
                    else { sum[k0, fixedIndex, c] += atK0; sum[k1, fixedIndex, c] += atK1; }
                }
                if (xEdge) { cnt[fixedIndex, k0]++; cnt[fixedIndex, k1]++; }
                else { cnt[k0, fixedIndex]++; cnt[k1, fixedIndex]++; }
            }

            void ZeroEdge(EdgeBc bc, bool xEdge, int index, PlateResult res)
            {
                if (bc != EdgeBc.Free && bc != EdgeBc.SimpleHard && bc != EdgeBc.SimpleSoft) return;
                int count = xEdge ? nyN : nxN;
                for (int k = 0; k < count; k++)
                {
                    int i = xEdge ? index : k, j = xEdge ? k : index;
                    if (xEdge) res.Mx[i, j] = 0; else res.My[i, j] = 0;
                    if (bc != EdgeBc.SimpleHard) res.Mxy[i, j] = 0;
                }
            }

            void ClampedEdge(EdgeBc bc, bool xEdge, int index, int sign, double[] rr, PlateResult res)
            {
                if (bc != EdgeBc.Clamped) return;
                var along = xEdge ? ys : xs;
                int count = along.Length;
                for (int k = 0; k < count; k++)
                {
                    int i = xEdge ? index : k, j = xEdge ? k : index;
                    double trib = ((k > 0 ? along[k] - along[k - 1] : 0) + (k < count - 1 ? along[k + 1] - along[k] : 0)) / 2;
                    int n = Node(i, j);
                    double mn = sign * rr[3 * n + (xEdge ? 1 : 2)] / trib;
                    if (xEdge) { res.Mx[i, j] = mn; res.My[i, j] = m.Nu * mn; }
                    else { res.My[i, j] = mn; res.Mx[i, j] = m.Nu * mn; }
                }
            }
        }
    }
}

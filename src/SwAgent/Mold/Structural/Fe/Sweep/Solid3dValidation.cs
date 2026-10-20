using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Mold.Structural.Fe.Solid;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>
    /// Sweep 3 (critique G1): the automated 3D cross-check. A linear-elastic Hex8 solid model checks what plate theory cannot see:
    ///  1. the wall-root factor: a cavity pocket milled into a block on the platen (quarter model), wall-top deflection against
    ///     the Wall-C plate (clamped root) of the same wall, on three meshes (h/12, h/16, h/24) extrapolated to the converged ratio; the
    ///     same pocket with a rigid floor, which splits the ratio into root compliance and 3D wall effects; and the wall's root
    ///     moment at mid-length against the plate's;
    ///  2. thick plates (t/b 0.1-0.5, simply supported diaphragm and clamped) against the Mindlin plate;
    ///  3. the pillar cell (a plate on rigid square heads, frictionless) against the Pillar rows: deflection (the pillar-head
    ///     factor) and the top-surface stress over the heads against beta;
    ///  4. bolted joints (axisymmetric FE) against Wileman, the 30-degree frustum and the VDI 2230 cone (through-bolted and tapped),
    ///     plus the VDI 2230 worked example end to end.
    /// Everything is dimensionless (E = 1, p = 1, h or b = 1) except the joints, which use mm and MPa.
    /// </summary>
    public static class Solid3dValidation
    {
        public const string Version = "a2-2";
        /// <summary>The calibrated values this run produced last (used when solid3d-summary.json is missing); MoldSweep reads the file.</summary>
        public const double DefaultWallRootSlope = 2.8, DefaultPillarHeadFactor = 1.2;

        public sealed class WallCase
        {
            public string Name;
            public double L, B, W, Floor, Nu = 0.33;
            public WallCase(string name, double l, double b, double w, double floor) { Name = name; L = l; B = b; W = w; Floor = floor; }
        }

        /// <summary>The pockets: L and B the cavity's length and width, W the wall, Floor the floor under it, all over the depth h = 1.</summary>
        public static readonly WallCase[] WallCases =
        {
            new WallCase("short square pocket", 1, 1, 0.3, 0.5),
            new WallCase("L/h 4, thin steel-like wall (w/h 0.1)", 4, 4, 0.1, 0.5) { Nu = 0.29 },
            new WallCase("L/h 4, w/h 0.15", 4, 4, 0.15, 0.5),
            new WallCase("L/h 2, thin wall", 2, 2, 0.25, 0.5),
            new WallCase("L/h 2, thick wall", 2, 2, 0.5, 0.5),
            new WallCase("L/h 3, half-width pocket", 3, 1.5, 0.35, 0.5),
            new WallCase("L/h 4, thin wall", 4, 4, 0.25, 0.5),
            new WallCase("L/h 4, wall 0.4 h", 4, 4, 0.4, 0.5),
            new WallCase("L/h 4, thick floor", 4, 4, 0.25, 1.0),
            new WallCase("L/h 4, floor at the 0.25 h minimum", 4, 4, 0.25, 0.25),
            new WallCase("L/h 6, wall 0.3 h", 6, 3, 0.3, 0.5),
            new WallCase("E2 cavity (6 x 4 x 1 in, wall 0.364 in, floor 0.75 in)", 6, 4, 0.364, 0.75),
            new WallCase("E3-like class B wall (w/h 0.53)", 6, 3, 0.53, 0.5),
            new WallCase("L/h 8, narrow pocket", 8, 2, 0.25, 0.5),
        };

        /// <summary>Pillar cells (aspect a/b, tau) for the 3D check; nu 0.3.</summary>
        public static readonly (double Aspect, double Tau)[] PillarCases =
        {
            (1, 0.05), (1, 0.1), (1, 0.15), (1, 0.2), (1, 0.25), (1, 0.3), (1, 0.5), (2, 0.1), (2, 0.2), (2, 0.3), (0.5, 0.1), (0.5, 0.2), (4, 0.1), (1, 0.02),
        };

        public static double[] Segments(params (double Length, int N)[] segs)
        {
            var xs = new List<double> { 0 };
            double x = 0;
            foreach (var (len, n) in segs)
            {
                if (len <= 0) continue;
                for (int k = 1; k <= n; k++) xs.Add(k == n ? x + len : x + len * k / n);
                x += len;
            }
            return xs.ToArray();
        }

        static int Count(double len, double h, int min) => Math.Max(min, (int)Math.Ceiling(len / h - 1e-9));

        static Box3 Plane(char axis, double v) => axis == 'x' ? new Box3(v, v, double.MinValue, double.MaxValue, double.MinValue, double.MaxValue)
            : axis == 'y' ? new Box3(double.MinValue, double.MaxValue, v, v, double.MinValue, double.MaxValue)
            : new Box3(double.MinValue, double.MaxValue, double.MinValue, double.MaxValue, v, v);

        /// <summary>Richardson extrapolation of a value computed at element sizes h1 > h2, convergence order p (2 by default).</summary>
        public static double Extrapolate(double h1, double v1, double h2, double v2, double p = 2) => v2 + (v2 - v1) / (Math.Pow(h1 / h2, p) - 1);

        /// <summary>
        /// Observed convergence order from three meshes h1 > h2 > h3: the p for which (v2 - v1) / (v3 - v2) = (h1^p - h2^p) /
        /// (h2^p - h3^p) (bisection on 0.3-6; NaN when the sequence does not converge monotonically).
        /// </summary>
        public static double ObservedOrder(double h1, double v1, double h2, double v2, double h3, double v3)
        {
            double d1 = v2 - v1, d2 = v3 - v2;
            if (d1 == 0 || d2 == 0 || Math.Sign(d1) != Math.Sign(d2)) return double.NaN;
            double target = d1 / d2;
            double F(double p) => (Math.Pow(h1, p) - Math.Pow(h2, p)) / (Math.Pow(h2, p) - Math.Pow(h3, p)) - target;
            double lo = 0.3, hi = 6;
            if (F(lo) * F(hi) > 0) return double.NaN;
            for (int k = 0; k < 60; k++) { double m = (lo + hi) / 2; if (F(lo) * F(m) <= 0) hi = m; else lo = m; }
            return (lo + hi) / 2;
        }

        /// <summary>Wall pocket at about element size he: the uniform refinement level k = round(1 / (4 he)) (see the other overload).</summary>
        public static (double W, double Root, SolidResult R) WallPocket(WallCase c, double he, bool rigidFloor)
            => WallPocket(c, Math.Max(1, (int)Math.Round(1 / (4 * he))), rigidFloor);

        /// <summary>
        /// Quarter model of a pocket (depth 1) in a block on a frictionless platen; returns the long wall's outward deflection at
        /// mid-length and mid-thickness, at the top (W) and at the root, level with the floor (Root). Mesh: every segment (half
        /// pocket, half wall, floor, depth) gets k x its count at element size 1/4, so the levels k = 3, 4, 6 (h/12, h/16, h/24)
        /// are uniform refinements of one another, as Richardson extrapolation needs (rounding each level's counts on its own
        /// made the wall's through-thickness count jump irregularly between levels).
        /// </summary>
        public static (double W, double Root, SolidResult R) WallPocket(WallCase c, int k, bool rigidFloor)
        {
            double l2 = c.L / 2, b2 = c.B / 2, w = c.W, tf = c.Floor, h = 1;
            int N(double len) => k * Count(len, 0.25, 1);
            int nw = N(w / 2);
            var m = new SolidModel
            {
                Xs = Segments((l2, N(l2)), (w / 2, nw), (w / 2, nw)),
                Ys = Segments((b2, N(b2)), (w / 2, nw), (w / 2, nw)),
                Zs = Segments((tf, N(tf)), (h, N(h))),
                E = 1, Nu = c.Nu, Tolerance = 1e-9,
            };
            m.Solid = (x, y, z) => !(x < l2 && y < b2 && z > tf);
            m.Supports.Add(new SolidSupport(Plane('x', 0), true, false, false));
            m.Supports.Add(new SolidSupport(Plane('y', 0), false, true, false));
            m.Supports.Add(new SolidSupport(Plane('z', 0), false, false, true));
            if (rigidFloor) m.Supports.Add(new SolidSupport(new Box3(double.MinValue, double.MaxValue, double.MinValue, double.MaxValue, 0, tf), true, true, true));
            m.Loads.Add(new SolidSurfaceLoad { Region = new Box3(0, l2, 0, b2, tf, tf + h), Pressure = 1 });
            var r = SolidSolver.Solve(m);
            return (r.Nearest(0, b2 + w / 2, tf + h, 1), r.Nearest(0, b2 + w / 2, tf, 1), r);
        }

        /// <summary>
        /// The long wall's bending moment per unit length at its root (level with the floor) at mid-length: sigma_zz integrated
        /// over the wall thickness about its mid-plane (2-point Gauss per cell). The stress at the inner root corner is singular;
        /// its integral is not.
        /// </summary>
        public static double WallRootMoment(WallCase c, SolidResult r)
        {
            double b2 = c.B / 2, yc = b2 + c.W / 2, tf = c.Floor, g = 1 / Math.Sqrt(3);
            int k = Array.FindIndex(r.Zs, z => Math.Abs(z - tf) < 1e-9);
            double m = 0;
            for (int j = 0; j + 1 < r.Ys.Length; j++)
            {
                if (r.Ys[j] < b2 - 1e-9 || r.Ys[j + 1] > b2 + c.W + 1e-9) continue;
                double hy = r.Ys[j + 1] - r.Ys[j];
                foreach (var et in new[] { -g, g })
                {
                    double y = r.Ys[j] + (et + 1) / 2 * hy;
                    m += r.Stress(0, j, k, -1, et, -1)[2] * (y - yc) * hy / 2;
                }
            }
            return Math.Abs(m);
        }

        /// <summary>The Wall-C plate of the same wall (clamped root and sides, free top): its largest deflection and its root moment at mid-length.</summary>
        public static (double W, double RootMoment) WallPlate(WallCase c)
        {
            var m = PlateModel.Rect(c.L, 1, c.W, 1, c.Nu, EdgeBc.Clamped, EdgeBc.Clamped, EdgeBc.Clamped, EdgeBc.Free).Uniform(1);
            m.ElementSize = Math.Min(c.L, 1) / 48; m.XLines.Add(c.L / 2);
            var r = MindlinPlateSolver.Solve(m);
            int i = Array.FindIndex(r.Xs, x => Math.Abs(x - c.L / 2) < 1e-9);
            return (Math.Abs(r.WMax), Math.Abs(r.EdgeMoments["y0"][i]));
        }

        /// <summary>Quarter of a plate a x b x t on diaphragm (SS) or clamped side faces, pressure on top: centre mid-surface deflection.</summary>
        public static (double W, SolidResult R) PlateBlock(double a, double b, double t, bool clamped, double nu, double he)
        {
            int nz = Math.Max(4, 2 * (int)Math.Ceiling(t / 2 / he));
            var m = new SolidModel
            {
                Xs = Segments((a / 2, Count(a / 2, he, 4))),
                Ys = Segments((b / 2, Count(b / 2, he, 4))),
                Zs = Segments((t, nz)),
                E = 1, Nu = nu, Tolerance = 1e-9,
            };
            m.Supports.Add(new SolidSupport(Plane('x', a / 2), true, false, false));
            m.Supports.Add(new SolidSupport(Plane('y', b / 2), false, true, false));
            m.Supports.Add(new SolidSupport(Plane('x', 0), clamped, true, true));
            m.Supports.Add(new SolidSupport(Plane('y', 0), true, clamped, true));
            m.Loads.Add(new SolidSurfaceLoad { Region = Plane('z', t), Face = 5, Pressure = 1 });
            var r = SolidSolver.Solve(m);
            return (-r.Nearest(a / 2, b / 2, t / 2, 2), r);
        }

        public static double PlateMindlin(double a, double b, double t, bool clamped, double nu)
        {
            var e = clamped ? EdgeBc.Clamped : EdgeBc.SimpleHard;
            var m = PlateModel.Rect(a, b, t, 1, nu, e, e, e, e).Uniform(1);
            m.ElementSize = Math.Min(a, b) / 48; m.XLines.Add(a / 2); m.YLines.Add(b / 2);
            var r = MindlinPlateSolver.Solve(m);
            return r.WAt(a / 2, b / 2);
        }

        /// <summary>
        /// Quarter pillar cell in 3D (a/2 x 1/2 x tau, E = 1, p = 1 on top): the plate rests on a rigid square head 0.1 x 0.1 at the
        /// corner (uz = 0 on the bottom face there, frictionless), every side a symmetry plane. Returns alpha (mid-surface deflection
        /// of the cell centre relative to the head centre, x tau^3) and beta of the largest top-surface principal stress (x tau^2).
        /// </summary>
        public static (double Alpha, double TopBeta, (double X, double Y) At, SolidResult R) PillarCell(double aspect, double tau, double nu, double he)
        {
            double a2 = aspect / 2, b2 = 0.5, h0 = CoefficientSweep.PillarHead / 2;
            int nz = Math.Max(4, (int)Math.Ceiling(tau / he));
            var m = new SolidModel
            {
                Xs = Segments((Math.Min(h0, a2), Count(Math.Min(h0, a2), he, 2)), (a2 - h0, Count(a2 - h0, he, 2))),
                Ys = Segments((h0, Count(h0, he, 2)), (b2 - h0, Count(b2 - h0, he, 2))),
                Zs = Segments((tau, nz)),
                E = 1, Nu = nu, Tolerance = 1e-10,
            };
            m.Supports.Add(new SolidSupport(Plane('x', 0), true, false, false));
            m.Supports.Add(new SolidSupport(Plane('x', a2), true, false, false));
            m.Supports.Add(new SolidSupport(Plane('y', 0), false, true, false));
            m.Supports.Add(new SolidSupport(Plane('y', b2), false, true, false));
            m.Supports.Add(new SolidSupport(new Box3(0, h0, 0, h0, 0, 0), false, false, true));
            m.Loads.Add(new SolidSurfaceLoad { Region = Plane('z', tau), Face = 5, Pressure = 1 });
            var r = SolidSolver.Solve(m);
            double w = r.Nearest(0, 0, tau / 2, 2) - r.Nearest(a2, b2, tau / 2, 2);
            int kTop = r.Zs.Length - 2;
            double best = 0; (double, double) at = (0, 0);
            for (int i = 0; i + 1 < r.Xs.Length; i++)
                for (int j = 0; j + 1 < r.Ys.Length; j++)
                    foreach (var (xi, et) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
                    {
                        var s = r.Stress(i, j, kTop, xi, et, 1);
                        double c = (s[0] + s[1]) / 2, rr = Math.Sqrt((s[0] - s[1]) * (s[0] - s[1]) / 4 + s[3] * s[3]);
                        double pm = Math.Max(Math.Abs(c + rr), Math.Abs(c - rr));
                        if (pm > best) { best = pm; at = (xi < 0 ? r.Xs[i] : r.Xs[i + 1], et < 0 ? r.Ys[j] : r.Ys[j + 1]); }
                    }
            return (w * tau * tau * tau, best * tau * tau, at, r);
        }

        public static JObject Run(bool quick = false, Action<string> log = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // wall meshes: refinement levels of the 1/4 base (h/12, h/16, h/24; quick h/8, h/12, h/16)
            int k0 = quick ? 2 : 3, k1 = quick ? 3 : 4, k2 = quick ? 4 : 6;
            double coarsest = 1.0 / (4 * k0), coarse = 1.0 / (4 * k1), fine = 1.0 / (4 * k2);
            bool passed = true;
            var notes = new JArray();
            var ic = CultureInfo.InvariantCulture;

            // 1. wall root: two meshes, extrapolated
            var wallRows = new JArray();
            var ratios = new List<double>();
            double worstCorrection = 0, worstSpread = 0, slope = 0, slopeRel = 0, worstRootMoment = 0;
            foreach (var c in WallCases)
            {
                var (plate, plateRoot) = WallPlate(c);
                var (w0, _, _) = WallPocket(c, k0, false);
                var (wc, _, rc) = WallPocket(c, k1, false);
                var (wf, rootF, rf) = WallPocket(c, k2, false);
                var (wr, _, rr) = WallPocket(c, k1, true);
                // the ratio rises with refinement at about first order (the root is a re-entrant corner, its stress singular):
                // take the observed order from the three meshes (held to 0.75-2; a lower order extrapolates further, so the
                // floor errs high), extrapolate the two finest meshes with it, and gate the size of that step
                double rCoarsest = w0 / plate, rCoarse = wc / plate, rFine = wf / plate;
                double order = ObservedOrder(coarsest, rCoarsest, coarse, rCoarse, fine, rFine);
                double pUsed = double.IsNaN(order) ? 0.75 : Math.Max(0.75, Math.Min(2, order));
                double ratio = Extrapolate(coarse, rCoarse, fine, rFine, pUsed);
                double ratioCoarse = Extrapolate(coarsest, rCoarsest, coarse, rCoarse, pUsed);
                double correction = Math.Abs(ratio / rFine - 1), spread = Math.Abs(ratio / ratioCoarse - 1);
                worstSpread = Math.Max(worstSpread, correction);
                if (double.IsNaN(order)) { passed = false; notes.Add($"wall case '{c.Name}' does not converge monotonically ({rCoarsest:F4}, {rCoarse:F4}, {rFine:F4})"); }
                double rel = (wf - rootF) / plate;
                double mRoot = WallRootMoment(c, rf), mRootCoarse = WallRootMoment(c, rc), rootRatio = mRoot / plateRoot;
                worstCorrection = Math.Max(worstCorrection, correction);
                worstRootMoment = Math.Max(worstRootMoment, rootRatio);
                ratios.Add(ratio);
                slope = Math.Max(slope, (ratio - 1) / c.W);
                slopeRel = Math.Max(slopeRel, (rel - 1) / c.W);
                if (!rf.Converged || !rc.Converged || !rr.Converged) { passed = false; notes.Add($"wall case '{c.Name}' did not converge"); }
                wallRows.Add(new JObject
                {
                    ["name"] = c.Name, ["L_over_h"] = c.L, ["B_over_h"] = c.B, ["w_over_h"] = c.W, ["floor_over_h"] = c.Floor, ["nu"] = c.Nu,
                    ["plate_alpha"] = R(plate * c.W * c.W * c.W, 5), ["solid_alpha"] = R(ratio * plate * c.W * c.W * c.W, 5),
                    ["ratio_coarsest"] = R(rCoarsest, 4), ["ratio_coarse"] = R(rCoarse, 4), ["ratio_fine"] = R(rFine, 4), ["ratio"] = R(ratio, 4),
                    ["observed_order"] = R(order, 2), ["order_used"] = R(pUsed, 2), ["richardson_correction"] = R(correction, 4),
                    ["extrapolated_from_coarser_meshes"] = R(ratioCoarse, 4), ["extrapolation_spread"] = R(spread, 4),
                    ["ratio_relative_to_root"] = R(rel, 4), ["ratio_rigid_floor"] = R(wr / plate, 4), ["root_part"] = R(wc / wr, 4),
                    ["slope"] = R((ratio - 1) / c.W, 3),
                    ["root_moment_plate"] = R(plateRoot, 4), ["root_moment_solid"] = R(mRoot, 4), ["root_moment_solid_coarse"] = R(mRootCoarse, 4), ["root_moment_ratio"] = R(rootRatio, 4),
                    ["dofs"] = rf.Dofs, ["pcg_iterations"] = rf.Iterations,
                });
                log?.Invoke($"  wall {c.Name}: 3D / plate {rCoarsest:F3} -> {rCoarse:F3} -> {rFine:F3} -> {ratio:F3} (order {order:F2}, step {correction * 100:F2}%); top minus root {rel:F3}; rigid floor {wr / plate:F3}; root moment 3D / plate {rootRatio:F3}; {rf.Dofs} dofs");
            }
            double maxRatio = ratios.Max();
            double recommended = Math.Ceiling(maxRatio * 20 - 1e-9) / 20; // up to the next 0.05
            double recSlope = Math.Ceiling(slope * 10 - 1e-9) / 10;
            if (worstSpread > 0.03) { passed = false; notes.Add($"wall models: the extrapolation adds {worstSpread * 100:F2}% to the finest mesh's ratio (gate 3%: the finest mesh is too coarse)"); }
            if (worstRootMoment > 1.05) { passed = false; notes.Add($"a 3D root moment exceeds the plate's by {(worstRootMoment - 1) * 100:F1}% (gate 5%): Wall-C beta would be unconservative"); }

            // 2. thick plates
            var plateRows = new JArray();
            double worstThick = 0, maxThickRatio = 0;
            foreach (bool clamped in new[] { false, true })
                foreach (var (ab, tau) in new[] { (1.0, 0.1), (1.0, 0.3), (1.0, 0.5), (2.0, 0.3), (2.0, 0.5) })
                {
                    double mind = PlateMindlin(ab, 1, tau, clamped, 0.3);
                    var (s3, r3) = PlateBlock(ab, 1, tau, clamped, 0.3, fine);
                    double ratio = s3 / mind;
                    if (tau >= 0.3) { worstThick = Math.Max(worstThick, Math.Abs(ratio - 1)); maxThickRatio = Math.Max(maxThickRatio, ratio); }
                    if (!r3.Converged) { passed = false; notes.Add($"plate {ab} {tau} did not converge"); }
                    plateRows.Add(new JObject
                    {
                        ["bc"] = clamped ? "C4 (side faces fixed)" : "SS4 (diaphragm: w and tangential u held over the side faces)",
                        ["a_over_b"] = ab, ["tau"] = tau, ["nu"] = 0.3,
                        ["mindlin_alpha"] = R(mind * tau * tau * tau, 5), ["solid_alpha"] = R(s3 * tau * tau * tau, 5), ["ratio"] = R(ratio, 4), ["dofs"] = r3.Dofs,
                    });
                    log?.Invoke($"  plate {(clamped ? "C4" : "SS4")} a/b {ab} tau {tau}: 3D / Mindlin {ratio:F3}");
                }
            if (maxThickRatio > 1.01) { passed = false; notes.Add($"a thick plate (tau >= 0.3) deflects {(maxThickRatio - 1) * 100:F1}% more in 3D than the Mindlin table (gate 1%)"); }

            // 3. pillar cell
            var pillarRows = new JArray();
            double pCoarse = quick ? 1.0 / 40 : 1.0 / 60, pFine = quick ? 1.0 / 60 : 1.0 / 100;
            double maxPillar = 0, worstPillarBeta = 0, worstPillarBetaThin = 0;
            foreach (var (asp, tau) in PillarCases)
            {
                var row = CoefficientSweep.Compute("Pillar", asp, tau, 0.3, 1, 96);
                var c3 = PillarCell(asp, tau, 0.3, pCoarse);
                var f3 = PillarCell(asp, tau, 0.3, pFine);
                double aRatio = Extrapolate(pCoarse, c3.Alpha, pFine, f3.Alpha) / row.Alpha;
                double bRatio = f3.TopBeta / row.Beta;
                if (!c3.R.Converged || !f3.R.Converged) { passed = false; notes.Add($"pillar cell {asp} {tau} did not converge"); }
                maxPillar = Math.Max(maxPillar, aRatio);
                if (tau >= 0.1) worstPillarBeta = Math.Max(worstPillarBeta, bRatio); else worstPillarBetaThin = Math.Max(worstPillarBetaThin, bRatio);
                pillarRows.Add(new JObject
                {
                    ["a_over_b"] = asp, ["tau"] = tau, ["nu"] = 0.3,
                    ["plate_alpha"] = R(row.Alpha, 5), ["solid_alpha_coarse"] = R(c3.Alpha, 5), ["solid_alpha_fine"] = R(f3.Alpha, 5), ["alpha_ratio"] = R(aRatio, 4),
                    ["table_beta"] = R(row.Beta, 4), ["solid_top_beta"] = R(f3.TopBeta, 4), ["solid_top_beta_coarse"] = R(c3.TopBeta, 4), ["beta_ratio"] = R(bRatio, 4),
                    ["solid_top_peak_at"] = new JArray(R(f3.At.X, 3), R(f3.At.Y, 3)), ["dofs"] = f3.R.Dofs,
                });
                log?.Invoke($"  pillar a/b {asp} tau {tau}: 3D / plate alpha {aRatio:F3}; 3D top-surface peak / table beta {bRatio:F3} at ({f3.At.X:F3}, {f3.At.Y:F3})");
            }
            double pillarFactor = Math.Ceiling(maxPillar * 20 - 1e-9) / 20;
            if (worstPillarBeta > 1.0) { passed = false; notes.Add($"pillar cell: the 3D top-surface stress exceeds the Pillar beta by {(worstPillarBeta - 1) * 100:F1}% at tau >= 0.1"); }

            // 4. bolted joints
            var boltRows = new JArray();
            double worstRigid = 0, worstPress = 0;
            foreach (var (mat, e, nu, wc) in new[] { ("steel", 205000.0, 0.291, JointStiffness.WilemanSteel), ("aluminium", 68900.0, 0.334, JointStiffness.WilemanAluminium) })
                foreach (var dl in new[] { 0.1, 0.25, 0.5, 1.0, 1.5 })
                {
                    const double d = 10;
                    double l = d / dl;
                    var rigid = BoltJointFe.MemberStiffness(e, nu, d, l, rigidHead: true, refine: quick ? 1 : 2);
                    var press = BoltJointFe.MemberStiffness(e, nu, d, l, refine: quick ? 1 : 2);
                    double wil = JointStiffness.Wileman(e, d, l, wc), fr = JointStiffness.FrustumMembers(e, d, l);
                    worstRigid = Math.Max(worstRigid, Math.Abs(rigid.Km / wil - 1));
                    worstPress = Math.Max(worstPress, Math.Abs(press.Km / wil - 1));
                    boltRows.Add(new JObject
                    {
                        ["material"] = mat, ["d_over_l"] = dl, ["nu"] = nu,
                        ["fe_rigid_head"] = R(rigid.Km / (e * d), 4), ["fe_uniform_pressure"] = R(press.Km / (e * d), 4),
                        ["wileman"] = R(wil / (e * d), 4), ["frustum_30deg"] = R(fr / (e * d), 4),
                        ["fe_rigid_over_wileman"] = R(rigid.Km / wil, 4), ["fe_pressure_over_wileman"] = R(press.Km / wil, 4),
                        ["contact_radius_over_d"] = R(press.ContactRadius / d, 3),
                    });
                    log?.Invoke($"  joint {mat} d/l {dl}: k/(E d) FE rigid {rigid.Km / (e * d):F3}, FE pressure {press.Km / (e * d):F3}, Wileman {wil / (e * d):F3}, frustum {fr / (e * d):F3}");
                }
            if (worstRigid > 0.15) { passed = false; notes.Add($"rigid-head FE differs from Wileman by {worstRigid * 100:F0}%"); }

            // VDI 2230 cone (steel, M10-like: d_W 16, d_h 11) against the FE at plate widths inside the cone fits' range
            var vdiRows = new JArray();
            double worstVdi = 0;
            var dsv = new List<double>(); var esv = new List<double>(); var esvPlatesOnly = new List<double>();
            const double vd = 10, vdw = 16, vdh = 11, ve = 205000, vnu = 0.29;
            foreach (var joint in new[] { Vdi2230.Joint.ThroughBolted, Vdi2230.Joint.Tapped })
                foreach (var betaL in new[] { 0.5, 1.0, 2.0, 4.0 })
                    foreach (var y in new[] { 2.0, 3.0, 5.0, 8.0 })
                    {
                        double lK = betaL * vdw, dA = y * vdw;
                        double deltaP = Vdi2230.PlateResilience(joint, ve, lK, vdw, vdh, dA, out double tanPhi, out double dAGr);
                        // the tapped FE measures the approach of head and engaged thread, which also holds the base's deformation
                        // around the thread: VDI books that under delta_M = 0.33 d / (E A_N), so the reference is delta_P + delta_M
                        double deltaM = joint == Vdi2230.Joint.Tapped ? 0.33 * vd / (ve * Math.PI * vd * vd / 4) : 0;
                        double kVdi = 1 / (deltaP + deltaM);
                        double kFe = joint == Vdi2230.Joint.ThroughBolted
                            ? BoltJointFe.MemberStiffness(ve, vnu, vd, lK, vdw, vdh, dA / 2, quick ? 1 : 2, rigidHead: true).Km
                            : BoltJointFe.TappedMemberStiffness(ve, vnu, vd, lK, vdw, vdh, dA, refine: quick ? 1 : 2).Km;
                        double ratio = kFe / kVdi;
                        worstVdi = Math.Max(worstVdi, Math.Abs(ratio - 1));
                        if (joint == Vdi2230.Joint.ThroughBolted) dsv.Add(ratio); else { esv.Add(ratio); esvPlatesOnly.Add(1 / deltaP / kFe); }
                        vdiRows.Add(new JObject
                        {
                            ["joint"] = joint == Vdi2230.Joint.ThroughBolted ? "through-bolted (DSV), two equal plates, rigid head; reference 1 / delta_P" : "tapped (ESV), plate on a tapped base, stiff washer, thread engaged 1 d; reference 1 / (delta_P + delta_M)",
                            ["beta_L"] = betaL, ["y"] = y, ["tan_phi"] = R(tanPhi, 4), ["phi_deg"] = R(Math.Atan(tanPhi) * 180 / Math.PI, 1),
                            ["full_cone"] = dA >= dAGr, ["k_vdi_plates_over_Ed"] = R(1 / deltaP / (ve * vd), 4), ["k_vdi_over_Ed"] = R(kVdi / (ve * vd), 4), ["k_fe_over_Ed"] = R(kFe / (ve * vd), 4), ["fe_over_vdi"] = R(ratio, 4),
                        });
                        log?.Invoke($"  VDI {joint} beta_L {betaL} y {y}: phi {Math.Atan(tanPhi) * 180 / Math.PI:F1} deg, k FE / VDI {ratio:F3}{(dA >= dAGr ? "" : " (cone + sleeve)")}");
                    }
            if (worstVdi > 0.30) { passed = false; notes.Add($"the VDI 2230 cone differs from the FE by {worstVdi * 100:F0}% (gate 30%)"); }
            var example = VdiExample();

            // what a softer joint does to the E4 corner (1/2-13, grip 2 in, aluminium, dT 100 K): k_m enters C and the thermal gain
            double at = 0.1419, kb = at * 29e6 / 2.0, kmW = JointStiffness.Wileman(10e6, 0.5, 2.0, JointStiffness.WilemanAluminium);
            var pressE4 = boltRows.Cast<JObject>().First(o => (string)o["material"] == "aluminium" && (double)o["d_over_l"] == 0.25);
            double kmFe = kmW * (double)pressE4["fe_pressure_over_wileman"];
            double C(double km) => kb / (kb + km);
            double DF(double km) => (23.6 - 11.7) * 1e-6 * 100 * 2.0 * kb * km / (kb + km);

            var summary = new JObject
            {
                ["schema"] = "unify3d.mold-solid3d/2",
                ["generator"] = "MoldSweep solid3d, SwAgent.Mold.Structural.Fe.Sweep.Solid3dValidation " + Version + (quick ? " (quick meshes)" : ""),
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["solver"] = "Linear-elastic 8-node bricks with incompatible modes (Wilson-Taylor), PCG with IC(0); axisymmetric bilinear rings with frictionless unilateral contact for the joints",
                ["wallRoot"] = new JObject
                {
                    ["what"] = "Ratio of the 3D wall-top deflection (pocket milled in a block resting on a frictionless platen; mid-length, mid-thickness) to the Wall-C plate deflection (root and sides clamped, top free) of the same wall, at element sizes h/12, h/16 and h/24, uniform refinements of one base mesh, extrapolated from the two finest with the convergence order observed over all three (Richardson; the ratio rises with refinement at about first order because the root is a re-entrant corner; the order used is held to 0.75-2). Gate: the extrapolation adds at most 3% to the h/24 ratio. This is the factor S21 multiplies alpha by.",
                    ["cases"] = WallCases.Length, ["maxRatio"] = R(maxRatio, 3), ["meanRatio"] = R(ratios.Average(), 3), ["minRatio"] = R(ratios.Min(), 3),
                    ["recommendedFactor"] = recommended, ["designDefault"] = 1.2,
                    ["recommendedFormula"] = $"wallRootFactor(w/h) = 1 + {recSlope.ToString(ic)} w/h (envelope of every case, extrapolated meshes, rounded up to 0.1; the constant 1.2 holds only below w/h = {(0.2 / recSlope).ToString("F2", ic)})",
                    ["recommendedSlope"] = recSlope, ["largestSlope"] = R(slope, 3), ["slopeTopMinusRoot"] = R(slopeRel, 3),
                    ["finding"] = "The plate wall itself is right (rigid-floor ratios near 1 for L/h >= 3); what the clamped-root plate misses is the rotation of the root in the block (the gear-tooth 'foundation' effect, O'Donnell), which grows in proportion to w/h, plus the floor's stretch for wide pockets. Use a w/h-dependent factor, not 1.2. The factor applies to deflection only: the 3D root moment at mid-length stays at or below the plate's (a compliant root sheds moment to the sides), so Wall-C beta is not raised.",
                    ["source"] = "3D solid FE, " + DateTime.Now.ToString("yyyy-MM-dd"),
                    ["meshes"] = $"h/{1 / coarsest:F0}, h/{1 / coarse:F0} and h/{1 / fine:F0}",
                    ["worstRichardsonCorrection"] = R(worstCorrection, 4),
                    ["worstExtrapolationStep"] = R(worstSpread, 4), ["gateExtrapolationStep"] = 0.03,
                    ["largestRootMomentRatio"] = R(worstRootMoment, 4), ["gateRootMomentRatio"] = 1.05,
                    ["rows"] = wallRows,
                },
                ["thickPlates"] = new JObject
                {
                    ["what"] = "Centre mid-surface deflection of the 3D plate over the Mindlin (MITC4) plate with the same supports: tests the tau = 0.5 cut-off. Gate: 3D / Mindlin <= 1.01 at tau >= 0.3 (the table must not under-read a thick plate).",
                    ["worstDeviationAtTau0_3Plus"] = R(worstThick, 4), ["largestRatioAtTau0_3Plus"] = R(maxThickRatio, 4), ["gate"] = 1.01,
                    ["rows"] = plateRows,
                },
                ["pillarCell"] = new JObject
                {
                    ["what"] = "Periodic pillar cell (a x b, rigid square heads 0.2 b, frictionless) in 3D against the Pillar rows (nu 0.3, 96 elements). alpha_ratio: 3D deflection of the cell centre relative to the head (mid-surface; meshes b/60 and b/100, extrapolated) over the plate's with w-only heads. beta_ratio: the largest 3D top-surface principal stress (on or over the head) over the tabulated beta.",
                    ["pillarHeadFactor"] = pillarFactor, ["largestAlphaRatio"] = R(maxPillar, 4),
                    ["recommended"] = $"Multiply Pillar alpha by {pillarFactor.ToString("0.00", ic)} (the plate misses the bearing compression over the head and the slip of the plate on it)",
                    ["largestBetaRatioTau0_1Plus"] = R(worstPillarBeta, 4), ["largestBetaRatioThin"] = R(worstPillarBetaThin, 4),
                    ["betaFinding"] = "At t/b >= 0.1 the tabulated beta (hogging on the clamped head face, 0.05 b from the corner) covers the 3D top-surface peak. Thinner plates peak over the head corner, above beta (the plate-theory corner singularity, reached at about t from the corner): radius or chamfer the head corners, apply K_f >= that ratio, or keep t/b >= 0.1 over rigid heads.",
                    ["rows"] = pillarRows,
                },
                ["boltJoint"] = new JObject
                {
                    ["what"] = "Member stiffness k_m / (E d) by axisymmetric FE: two identical through-bolted plates (hole d, washer face 1.5 d, wide plates) against Wileman (Shigley eq. 8-23) and the 30-degree frustum (eq. 8-20); and the VDI 2230 cone (DSV and ESV) against the FE at y = D_A/d_W 2-8 (the cone fits' range), including the cone-plus-sleeve case.",
                    ["worstRigidHeadVsWileman"] = R(worstRigid, 4), ["worstUniformPressureVsWileman"] = R(worstPress, 4),
                    ["rows"] = boltRows,
                    ["vdi2230"] = new JObject
                    {
                        ["source"] = "VDI 2230 Part 1 (2015) cone model as reproduced by the worked example of Hochschule Anhalt, Uebung Maschinenelemente, Schraubenverbindungen: Tragfaehigkeitsnachweis nach VDI 2230 (S. Voigt), Berechnungsbeispiel Hydraulikzylinder",
                        ["workedExample"] = example,
                        ["worstFeOverVdi"] = R(worstVdi, 4), ["gate"] = 0.30,
                        ["finding"] = $"Through-bolted: the FE lands at {dsv.Min():F2}-{dsv.Max():F2} x the VDI cone (VDI is the stiffer for wide plates, y 5-8, where its cone angle reaches 32-35 degrees). Tapped: the FE's approach of head and thread contains the base's deformation around the thread, which VDI books under delta_M, so it is compared with delta_P + delta_M (0.33 d / E A_N): {esv.Min():F2}-{esv.Max():F2}; VDI's delta_P alone is {esvPlatesOnly.Min():F1}-{esvPlatesOnly.Max():F1} x stiffer than that approach. Wileman (two equal plates) does not describe a tapped joint: for the mold plates (critique H5) use VDI's ESV cone with delta_M.",
                        ["rows"] = vdiRows,
                    },
                    ["e4Sensitivity"] = new JObject
                    {
                        ["case"] = "E4: 1/2-13, grip 2 in, aluminium joint, dT 100 K",
                        ["C_wileman"] = R(C(kmW), 4), ["C_fe_pressure"] = R(C(kmFe), 4),
                        ["thermalGain_lbf_wileman"] = R(DF(kmW), 0), ["thermalGain_lbf_fe_pressure"] = R(DF(kmFe), 0),
                        ["note"] = "A softer joint than Wileman raises C (more of P reaches the bolt) and lowers the thermal preload gain; Wileman is the conservative choice for the hot-load and thermal checks and slightly unconservative for C P.",
                    },
                },
                ["notes"] = notes,
                ["elapsed_s"] = R(sw.Elapsed.TotalSeconds, 0),
                ["passed"] = passed,
            };
            return summary;
        }

        /// <summary>
        /// The VDI 2230 worked example end to end (Hochschule Anhalt, Berechnungsbeispiel Hydraulikzylinder): ISO 4762 M12 x 60
        /// socket head screw in the piston rod's tapped hole, l_K 42 mm (shank 24, free thread 18), d_h 13.5, d_3 9.85, mean bearing
        /// diameter (17.23 + 25) / 2 = 21.11 mm, D_A 80 mm, E 210 GPa; the plates are treated as a through-bolted cone (small bearing
        /// face at the interface). F_A 24.9 kN, n 0.3, F_KRmin 1 kN, f_Z 8 um, alpha_A 1.7 (indicating torque wrench).
        /// The source prints delta_S 2.95e-6 mm/N on its bolt slide but its F_Z of 2.475 kN implies 2.88e-6, which is what the
        /// sections listed there give; the published results below use the latter.
        /// </summary>
        public static JObject VdiExample()
        {
            const double e = 210000, lK = 42, dwm = 21.11, dh = 13.5, dA = 80, d = 12, d3 = 9.85;
            double deltaP = Vdi2230.PlateResilience(Vdi2230.Joint.ThroughBolted, e, lK, dwm, dh, dA, out double tanPhi, out double dAGr);
            double deltaS = Vdi2230.BoltResilience(Vdi2230.Joint.Tapped, e, d, d3, 24, 18, socketHead: true);
            double phiK = Vdi2230.LoadFactor(deltaS, deltaP), phiN = Vdi2230.LoadFactor(deltaS, deltaP, 0.3);
            double fZ = Vdi2230.EmbeddingLoss(8e-3, deltaS, deltaP);
            // the source rounds Phi_K to 0.11 before Phi_n = 0.033
            var band = Vdi2230.PreloadBand(1000, 24900, 0.3 * Math.Round(phiK, 2), fZ, 1.7);
            JObject Pair(double ours, double published) => new JObject { ["computed"] = R(ours, 6), ["published"] = published, ["deviation"] = R(ours / published - 1, 4) };
            return new JObject
            {
                ["tan_phi"] = Pair(tanPhi, 0.566),
                ["D_A_Gr_mm"] = Pair(dAGr, 44.9),
                ["delta_P_mm_per_N_e6"] = Pair(deltaP * 1e6, 0.3546),
                ["delta_S_mm_per_N_e6"] = Pair(deltaS * 1e6, 2.878),
                ["Phi_K"] = Pair(phiK, 0.11),
                ["Phi_n"] = Pair(phiN, 0.033),
                ["F_Z_N"] = Pair(fZ, 2475),
                ["F_Mmin_kN"] = Pair(band.Min / 1000, 27.6),
                ["F_Mmax_kN"] = Pair(band.Max / 1000, 46.9),
            };
        }

        static double R(double v, int digits) => Math.Round(v, digits);
    }
}

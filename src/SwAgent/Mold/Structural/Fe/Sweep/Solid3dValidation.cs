using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Mold.Structural.Fe.Solid;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>
    /// Sweep 3 (critique G1): the automated 3D cross-check. A linear-elastic Hex8 solid model checks what plate theory cannot see:
    ///  1. the wall-root factor: a cavity pocket milled into a block on the platen (quarter model), wall-top deflection against
    ///     the Wall-C plate (clamped root) of the same wall; and the same pocket with a rigid floor, which splits the ratio into
    ///     root compliance and 3D wall effects;
    ///  2. thick plates (t/b 0.1-0.5, simply supported diaphragm and clamped) against the Mindlin plate;
    ///  3. bolted-joint member stiffness (axisymmetric FE) against Wileman, the 30-degree frustum and the VDI 2230 cone.
    /// Everything is dimensionless (E = 1, p = 1, h or b = 1) except the joint, which uses mm and MPa.
    /// </summary>
    public static class Solid3dValidation
    {
        public const string Version = "a2-1";

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

        /// <summary>
        /// Quarter model of a pocket (depth 1) in a block on a frictionless platen; returns the long wall's outward deflection at
        /// mid-length and mid-thickness, at the top (W) and at the root, level with the floor (Root).
        /// </summary>
        public static (double W, double Root, SolidResult R) WallPocket(WallCase c, double he, bool rigidFloor)
        {
            double l2 = c.L / 2, b2 = c.B / 2, w = c.W, tf = c.Floor, h = 1;
            int nw = Count(w / 2, he, 2);
            var m = new SolidModel
            {
                Xs = Segments((l2, Count(l2, he, 2)), (w / 2, nw), (w / 2, nw)),
                Ys = Segments((b2, Count(b2, he, 2)), (w / 2, nw), (w / 2, nw)),
                Zs = Segments((tf, Count(tf, he, 3)), (h, Count(h, he, 4))),
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

        /// <summary>The Wall-C plate of the same wall (clamped root and sides, free top): its largest deflection.</summary>
        public static double WallPlate(WallCase c)
        {
            var m = PlateModel.Rect(c.L, 1, c.W, 1, c.Nu, EdgeBc.Clamped, EdgeBc.Clamped, EdgeBc.Clamped, EdgeBc.Free).Uniform(1);
            m.ElementSize = Math.Min(c.L, 1) / 48; m.XLines.Add(c.L / 2);
            return Math.Abs(MindlinPlateSolver.Solve(m).WMax);
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

        public static JObject Run(bool quick = false, Action<string> log = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double fine = quick ? 1.0 / 8 : 1.0 / 12, coarse = quick ? 1.0 / 6 : 1.0 / 8;
            bool passed = true;
            var notes = new JArray();

            // 1. wall root
            var wallRows = new JArray();
            var ratios = new List<double>();
            double worstMesh = 0, slope = 0, slopeRel = 0;
            foreach (var c in WallCases)
            {
                double plate = WallPlate(c);
                var (wf, rootF, rf) = WallPocket(c, fine, false);
                var (wc, _, _) = WallPocket(c, coarse, false);
                var (wr, _, rr) = WallPocket(c, fine, true);
                double ratio = wf / plate, rel = (wf - rootF) / plate, mesh = Math.Abs(wf / wc - 1);
                worstMesh = Math.Max(worstMesh, mesh);
                ratios.Add(ratio);
                slope = Math.Max(slope, (ratio - 1) / c.W);
                slopeRel = Math.Max(slopeRel, (rel - 1) / c.W);
                if (!rf.Converged || !rr.Converged) { passed = false; notes.Add($"wall case '{c.Name}' did not converge"); }
                wallRows.Add(new JObject
                {
                    ["name"] = c.Name, ["L_over_h"] = c.L, ["B_over_h"] = c.B, ["w_over_h"] = c.W, ["floor_over_h"] = c.Floor, ["nu"] = c.Nu,
                    ["plate_alpha"] = R(plate * c.W * c.W * c.W, 5), ["solid_alpha"] = R(wf * c.W * c.W * c.W, 5),
                    ["ratio"] = R(ratio, 4), ["ratio_relative_to_root"] = R(rel, 4), ["ratio_rigid_floor"] = R(wr / plate, 4), ["root_part"] = R(wf / wr, 4),
                    ["slope"] = R((ratio - 1) / c.W, 3),
                    ["mesh_change"] = R(mesh, 4), ["dofs"] = rf.Dofs, ["pcg_iterations"] = rf.Iterations,
                });
                log?.Invoke($"  wall {c.Name}: 3D / plate {ratio:F3} (top minus root {rel:F3}; rigid floor {wr / plate:F3}, root part {wf / wr:F3}); (ratio-1)/(w/h) {(ratio - 1) / c.W:F2}; mesh change {mesh * 100:F1}%; {rf.Dofs} dofs");
            }
            double maxRatio = ratios.Max();
            double recommended = Math.Ceiling(maxRatio * 20 - 1e-9) / 20; // up to the next 0.05
            double recSlope = Math.Ceiling(slope * 10 - 1e-9) / 10;
            if (worstMesh > 0.03) { passed = false; notes.Add($"wall models changed {worstMesh * 100:F1}% between meshes"); }

            // 2. thick plates
            var plateRows = new JArray();
            double worstThick = 0;
            foreach (bool clamped in new[] { false, true })
                foreach (var (ab, tau) in new[] { (1.0, 0.1), (1.0, 0.3), (1.0, 0.5), (2.0, 0.3), (2.0, 0.5) })
                {
                    double mind = PlateMindlin(ab, 1, tau, clamped, 0.3);
                    var (s3, r3) = PlateBlock(ab, 1, tau, clamped, 0.3, fine / 2);
                    double ratio = s3 / mind;
                    if (tau >= 0.3) worstThick = Math.Max(worstThick, Math.Abs(ratio - 1));
                    if (!r3.Converged) { passed = false; notes.Add($"plate {ab} {tau} did not converge"); }
                    plateRows.Add(new JObject
                    {
                        ["bc"] = clamped ? "C4 (side faces fixed)" : "SS4 (diaphragm: w and tangential u held over the side faces)",
                        ["a_over_b"] = ab, ["tau"] = tau, ["nu"] = 0.3,
                        ["mindlin_alpha"] = R(mind * tau * tau * tau, 5), ["solid_alpha"] = R(s3 * tau * tau * tau, 5), ["ratio"] = R(ratio, 4), ["dofs"] = r3.Dofs,
                    });
                    log?.Invoke($"  plate {(clamped ? "C4" : "SS4")} a/b {ab} tau {tau}: 3D / Mindlin {ratio:F3}");
                }

            // 3. bolted joint member stiffness
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
                    double vdi = JointStiffness.VdiCone(e, l, 1.5 * d, d, 2 * Math.Max(10 * d, 0.75 * d + 1.5 * l), out double phi);
                    worstRigid = Math.Max(worstRigid, Math.Abs(rigid.Km / wil - 1));
                    worstPress = Math.Max(worstPress, Math.Abs(press.Km / wil - 1));
                    boltRows.Add(new JObject
                    {
                        ["material"] = mat, ["d_over_l"] = dl, ["nu"] = nu,
                        ["fe_rigid_head"] = R(rigid.Km / (e * d), 4), ["fe_uniform_pressure"] = R(press.Km / (e * d), 4),
                        ["wileman"] = R(wil / (e * d), 4), ["frustum_30deg"] = R(fr / (e * d), 4), ["vdi_cone"] = R(vdi / (e * d), 4), ["vdi_cone_angle_deg"] = R(phi, 1),
                        ["fe_rigid_over_wileman"] = R(rigid.Km / wil, 4), ["fe_pressure_over_wileman"] = R(press.Km / wil, 4),
                        ["contact_radius_over_d"] = R(press.ContactRadius / d, 3),
                    });
                    log?.Invoke($"  joint {mat} d/l {dl}: k/(E d) FE rigid {rigid.Km / (e * d):F3}, FE pressure {press.Km / (e * d):F3}, Wileman {wil / (e * d):F3}, frustum {fr / (e * d):F3}, VDI {vdi / (e * d):F3}");
                }
            if (worstRigid > 0.15) { passed = false; notes.Add($"rigid-head FE differs from Wileman by {worstRigid * 100:F0}%"); }

            // what a softer joint does to the E4 corner (1/2-13, grip 2 in, aluminium, dT 100 K): k_m enters C and the thermal gain
            double at = 0.1419, kb = at * 29e6 / 2.0, kmW = JointStiffness.Wileman(10e6, 0.5, 2.0, JointStiffness.WilemanAluminium);
            var pressE4 = boltRows.Cast<JObject>().First(o => (string)o["material"] == "aluminium" && (double)o["d_over_l"] == 0.25);
            double kmFe = kmW * (double)pressE4["fe_pressure_over_wileman"];
            double C(double km) => kb / (kb + km);
            double DF(double km) => (23.6 - 11.7) * 1e-6 * 100 * 2.0 * kb * km / (kb + km);

            var summary = new JObject
            {
                ["schema"] = "unify3d.mold-solid3d/1",
                ["generator"] = "MoldSweep solid3d, SwAgent.Mold.Structural.Fe.Sweep.Solid3dValidation " + Version + (quick ? " (quick meshes)" : ""),
                ["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
                ["solver"] = "Linear-elastic 8-node bricks with incompatible modes (Wilson-Taylor), PCG with IC(0); axisymmetric bilinear rings with frictionless unilateral contact for the joint",
                ["wallRoot"] = new JObject
                {
                    ["what"] = "Ratio of the 3D wall-top deflection (pocket milled in a block resting on a frictionless platen; mid-length, mid-thickness) to the Wall-C plate deflection (root and sides clamped, top free) of the same wall. This is the factor S21 multiplies alpha by.",
                    ["cases"] = WallCases.Length, ["maxRatio"] = R(maxRatio, 3), ["meanRatio"] = R(ratios.Average(), 3), ["minRatio"] = R(ratios.Min(), 3),
                    ["recommendedFactor"] = recommended, ["designDefault"] = 1.2,
                    ["recommendedFormula"] = $"wallRootFactor(w/h) = 1 + {recSlope.ToString(System.Globalization.CultureInfo.InvariantCulture)} w/h (envelope of every case; the constant 1.2 holds only below w/h = {(0.2 / recSlope).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)})",
                    ["recommendedSlope"] = recSlope, ["slopeTopMinusRoot"] = R(slopeRel, 3),
                    ["finding"] = "The plate wall itself is right (rigid-floor ratios near 1 for L/h >= 3); what the clamped-root plate misses is the rotation of the root in the block (the gear-tooth 'foundation' effect, O'Donnell), which grows in proportion to w/h, plus the floor's stretch for wide pockets. Use a w/h-dependent factor, not 1.2.",
                    ["source"] = "3D solid FE, " + DateTime.Now.ToString("yyyy-MM-dd"),
                    ["worstMeshChange"] = R(worstMesh, 4),
                    ["rows"] = wallRows,
                },
                ["thickPlates"] = new JObject
                {
                    ["what"] = "Centre mid-surface deflection of the 3D plate over the Mindlin (MITC4) plate with the same supports: tests the tau = 0.5 cut-off.",
                    ["worstDeviationAtTau0_3Plus"] = R(worstThick, 4),
                    ["rows"] = plateRows,
                },
                ["boltJoint"] = new JObject
                {
                    ["what"] = "Member stiffness k_m / (E d) of two identical through-bolted plates (hole d, washer face 1.5 d, wide plates), axisymmetric FE with the mid-plane free to open, against Wileman (Shigley eq. 8-23), the 30-degree frustum (eq. 8-20) and the VDI 2230 cone (formula as recalled; verify).",
                    ["worstRigidHeadVsWileman"] = R(worstRigid, 4), ["worstUniformPressureVsWileman"] = R(worstPress, 4),
                    ["rows"] = boltRows,
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

        static double R(double v, int digits) => Math.Round(v, digits);
    }
}

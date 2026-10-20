using System;
using System.Linq;
using SwAgent.Mold.Structural.Fe;
using SwAgent.Mold.Structural.Fe.Solid;
using Xunit;
using Xunit.Abstractions;

namespace SwAgent.Tests.Mold
{
    /// <summary>The 3D validation solver (Hex8 with incompatible modes, PCG with IC(0)) and the axisymmetric joint model.</summary>
    public class FeSolidTests
    {
        readonly ITestOutputHelper _out;
        public FeSolidTests(ITestOutputHelper output) { _out = output; }

        static Box3 Plane(char axis, double v) => axis == 'x' ? new Box3(v, v, double.MinValue, double.MaxValue, double.MinValue, double.MaxValue)
            : axis == 'y' ? new Box3(double.MinValue, double.MaxValue, v, v, double.MinValue, double.MaxValue)
            : new Box3(double.MinValue, double.MaxValue, double.MinValue, double.MaxValue, v, v);

        [Fact]
        public void Patch_test_uniform_tension_is_exact_on_an_uneven_grid()
        {
            const double e = 210, nu = 0.3, s = 1.7;
            var m = new SolidModel
            {
                Xs = new[] { 0, 0.3, 1.1, 2.0 }, Ys = new[] { 0, 0.4, 1.0 }, Zs = new[] { 0, 0.7, 0.9, 1.5 }, E = e, Nu = nu, Tolerance = 1e-13,
            };
            m.Supports.Add(new SolidSupport(Plane('x', 0), true, false, false));
            m.Supports.Add(new SolidSupport(Plane('y', 0), false, true, false));
            m.Supports.Add(new SolidSupport(Plane('z', 0), false, false, true));
            m.Loads.Add(new SolidSurfaceLoad { Region = Plane('x', 2.0), Face = 1, Traction = (x, y, z) => new[] { s, 0, 0 } });
            var r = SolidSolver.Solve(m);
            for (int i = 0; i < m.Xs.Length; i++)
                for (int j = 0; j < m.Ys.Length; j++)
                    for (int k = 0; k < m.Zs.Length; k++)
                    {
                        Assert.Equal(s * m.Xs[i] / e, r.U[i, j, k, 0], 10);
                        Assert.Equal(-nu * s * m.Ys[j] / e, r.U[i, j, k, 1], 10);
                        Assert.Equal(-nu * s * m.Zs[k] / e, r.U[i, j, k, 2], 10);
                    }
            for (int c = 0; c < 3; c++) Assert.True(Math.Abs(r.AppliedTotal[c] + r.ReactionTotal[c]) < 1e-9, "equilibrium");
        }

        [Fact]
        public void Pure_bending_is_exact_with_one_element_per_half_depth()
        {
            // Saint-Venant pure bending: w(L) = M L^2 / (2 E I) on the neutral axis; Hex8 with incompatible modes is exact here.
            const double e = 1000, nu = 0.3, L = 10, b = 1, h = 1, sigma0 = 6; // traction sx = sigma0 (z - h/2) / (h/2)
            var m = new SolidModel { Xs = SolidModel.Grid(L, 10), Ys = SolidModel.Grid(b, 1), Zs = SolidModel.Grid(h, 2), E = e, Nu = nu, Tolerance = 1e-13 };
            m.Supports.Add(new SolidSupport(Plane('x', 0), true, false, false));
            m.Supports.Add(new SolidSupport(Plane('y', 0), false, true, false));
            m.Supports.Add(new SolidSupport(new Box3(0, 0, 0, 0, h / 2, h / 2), false, false, true));
            m.Loads.Add(new SolidSurfaceLoad { Region = Plane('x', L), Face = 1, Traction = (x, y, z) => new[] { sigma0 * (z - h / 2) / (h / 2), 0, 0 } });
            var r = SolidSolver.Solve(m);
            double moment = sigma0 * b * h * h / 6, inertia = b * h * h * h / 12;
            double exact = -moment * L * L / (2 * e * inertia); // tension at the top (+z) bends the tip down
            Assert.Equal(exact, r.U[10, 0, 1, 2], 6);
        }

        [Fact]
        public void Cantilever_with_end_shear_matches_timoshenko_beam()
        {
            const double e = 1000, nu = 0.3, L = 10, b = 1, h = 1, p = 1;
            var m = new SolidModel { Xs = SolidModel.Grid(L, 20), Ys = SolidModel.Grid(b, 2), Zs = SolidModel.Grid(h, 4), E = e, Nu = nu, Tolerance = 1e-12 };
            m.Supports.Add(new SolidSupport(Plane('x', 0), true, true, true));
            // uniform shear traction on the end face, total P (a linear-field traction integrates exactly)
            m.Loads.Add(new SolidSurfaceLoad { Region = Plane('x', L), Face = 1, Traction = (x, y, z) => new[] { 0, 0, -p / (b * h) } });
            var r = SolidSolver.Solve(m);
            Assert.Equal(-p, r.AppliedTotal[2], 12);
            double inertia = b * h * h * h / 12, g = e / (2 * (1 + nu));
            double beam = p * L * L * L / (3 * e * inertia) + p * L / (5.0 / 6 * g * b * h);
            double tip = -r.U[20, 1, 2, 2];
            _out.WriteLine($"tip {tip:F5} beam {beam:F5} ({tip / beam - 1:P2}); {r.Iterations} PCG iterations, {r.Preconditioner}");
            Assert.InRange(tip / beam, 0.97, 1.01); // a fully clamped end face is a little stiffer than the beam's root
            for (int c = 0; c < 3; c++) Assert.True(Math.Abs(r.AppliedTotal[c] + r.ReactionTotal[c]) < 1e-7 * p, "equilibrium");
        }

        [Fact]
        public void Thick_simply_supported_plate_is_close_to_fsdt()
        {
            // quarter of a square plate a = 1, t = 0.1, diaphragm supports (w = 0 and tangential u = 0 on the side faces), top pressure
            const double a = 1, t = 0.1, e = 1, nu = 0.3;
            var m = new SolidModel { Xs = SolidModel.Grid(a / 2, 12), Ys = SolidModel.Grid(a / 2, 12), Zs = SolidModel.Grid(t, 2), E = e, Nu = nu, Tolerance = 1e-10 };
            m.Supports.Add(new SolidSupport(Plane('x', 0), false, true, true));      // side face x = 0: diaphragm (v = w = 0)
            m.Supports.Add(new SolidSupport(Plane('y', 0), true, false, true));      // side face y = 0
            m.Supports.Add(new SolidSupport(Plane('x', a / 2), true, false, false)); // symmetry
            m.Supports.Add(new SolidSupport(Plane('y', a / 2), false, true, false));
            m.Loads.Add(new SolidSurfaceLoad { Region = Plane('z', t), Face = 5, Pressure = 1 });
            var r = SolidSolver.Solve(m);
            double w3d = -r.U[12, 12, 1, 2];
            double fsdt = Navier.SimplySupported(a, a, t, e, nu, 1, a / 2, a / 2).W;
            _out.WriteLine($"3D {w3d:G6} FSDT {fsdt:G6} ratio {w3d / fsdt:F4}; {r.Dofs} dofs, {r.Iterations} iterations ({r.Preconditioner})");
            Assert.True(r.Converged);
            Assert.InRange(w3d / fsdt, 0.97, 1.03);
        }

        [Fact]
        public void Pcg_matches_the_direct_banded_solution()
        {
            // a small 1D-chain-like SPD system in CSR and in banded form
            int n = 60;
            var rowPtr = new int[n + 1];
            var cols = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++)
            {
                for (int j = Math.Max(0, i - 3); j <= Math.Min(n - 1, i + 3); j++) cols.Add(j);
                rowPtr[i + 1] = cols.Count;
            }
            var a = new Csr(n, rowPtr, cols.ToArray());
            var band = new BandedLdlt(n, 3);
            for (int i = 0; i < n; i++)
                for (int k = a.RowPtr[i]; k < a.RowPtr[i + 1]; k++)
                {
                    int j = a.Col[k];
                    double v = i == j ? 10 + i % 3 : -1.0 / (1 + Math.Abs(i - j) + (i + j) % 2);
                    a.Val[k] = v;
                    if (j >= i) band.Add(i, j, v);
                }
            var b = Enumerable.Range(0, n).Select(i => Math.Cos(i)).ToArray();
            var pcg = Pcg.Solve(a, b, 1e-13);
            var direct = band.Solve(b);
            Assert.True(pcg.Converged);
            for (int i = 0; i < n; i++) Assert.Equal(direct[i], pcg.X[i], 10);
            Assert.Equal("IC(0)", pcg.Preconditioner);
        }

        [Fact]
        public void Axisymmetric_uniform_compression_is_exact()
        {
            const double e = 70000, nu = 0.33, p = 50;
            var m = new AxisymmetricModel { Rs = new[] { 2.0, 2.5, 4.0, 7.0 }, Zs = new[] { 0, 1.0, 3.0 }, E = e, Nu = nu, Bottom = BottomCondition.Bonded };
            m.TopPressures.Add((2.0, 7.0, p));
            var r = AxisymmetricSolver.Solve(m);
            // uz = -p z / E; ur = nu p r / E (frictionless ends: the bonded bottom only holds uz)
            for (int i = 0; i < m.Rs.Length; i++)
                for (int k = 0; k < m.Zs.Length; k++)
                {
                    Assert.Equal(-p * m.Zs[k] / e, r.Uz[i, k], 10);
                    Assert.Equal(nu * p * m.Rs[i] / e, r.Ur[i, k], 10);
                }
            Assert.Equal(p * Math.PI * (49 - 4), r.ContactForce, 6);
        }

        [Fact]
        public void Axisymmetric_thick_cylinder_matches_lame()
        {
            // plane strain (uz = 0 top and bottom), internal pressure: ur(a) = (1 + nu) p a^2 / (E (b^2 - a^2)) [(1 - 2 nu) a + b^2 / a]
            const double e = 200000, nu = 0.3, p = 100, ra = 10, rb = 30;
            var m = new AxisymmetricModel { Rs = AxisymmetricSolver.Lines(0.25, ra, rb), Zs = new[] { 0, 1.0 }, E = e, Nu = nu, Bottom = BottomCondition.Bonded, TopFixedZ = true, InnerPressure = p };
            var r = AxisymmetricSolver.Solve(m);
            double exact = (1 + nu) * p * ra * ra / (e * (rb * rb - ra * ra)) * ((1 - 2 * nu) * ra + rb * rb / ra);
            Assert.InRange(r.Ur[0, 0] / exact, 0.998, 1.002);
        }

        [Fact]
        public void Bolted_joint_member_stiffness_is_near_wileman()
        {
            // d/l = 0.5 aluminium. Through a rigid head the FE lands within a few percent of Wileman's fit (and of the 30-degree
            // frustum); a uniform pressure under the head is about 15% softer. The plates open just outside the pressure cone.
            const double e = 70000, nu = 0.334, d = 10, l = 20;
            var rigid = BoltJointFe.MemberStiffness(e, nu, d, l, rigidHead: true);
            var press = BoltJointFe.MemberStiffness(e, nu, d, l);
            double wil = JointStiffness.Wileman(e, d, l, JointStiffness.WilemanAluminium);
            double cone = JointStiffness.FrustumMembers(e, d, l);
            _out.WriteLine($"FE rigid head {rigid.Km:F0} N/mm, uniform pressure {press.Km:F0}, Wileman {wil:F0}, 30-degree frustum {cone:F0}; contact to r = {press.ContactRadius:F1} mm in {press.Passes} passes");
            Assert.InRange(rigid.Km / wil, 0.93, 1.07);
            Assert.InRange(press.Km / rigid.Km, 0.80, 0.92);
            double coneEdge = 0.75 * d + l / 2 * Math.Tan(Math.PI / 6);
            Assert.InRange(press.ContactRadius, 0.9 * coneEdge, 1.5 * coneEdge);
        }

        [Fact]
        public void Two_bodies_in_contact_compress_like_one()
        {
            // a column of two stacked bodies (interface at z = 1) under a uniform top pressure, resting on a frictionless plane:
            // the contact transmits the load and the displacements are the one-body solution, uz = -p z / E
            const double e = 70000, nu = 0.33, p = 50;
            var m = new AxisymmetricModel { Rs = new[] { 2.0, 2.5, 4.0, 7.0 }, Zs = new[] { 0, 0.5, 1.0, 2.0, 3.0 }, E = e, Nu = nu, Bottom = BottomCondition.Bonded, InterfaceK = 2 };
            m.TopPressures.Add((2.0, 7.0, p));
            var r = AxisymmetricSolver.Solve(m);
            for (int i = 0; i < m.Rs.Length; i++)
            {
                // the contact is a stiff penalty: it adds about p h / (1e4 E) across the interface
                for (int k = 0; k < m.Zs.Length; k++) Assert.True(Math.Abs(r.Uz[i, k] + p * m.Zs[k] / e) < 1e-3 * p / e, $"uz at {i},{k}");
                Assert.True(Math.Abs(r.UzInterfaceLower[i] + p * 1.0 / e) < 1e-3 * p / e);
            }
            Assert.Equal(m.Rs[m.Rs.Length - 1], r.InterfaceContactRadius);
        }

        [Fact]
        public void Vdi2230_worked_example_is_reproduced_end_to_end()
        {
            // Hochschule Anhalt, VDI 2230 exercise "Hydraulikzylinder": cone, plate and bolt resilience, load factor, embedding
            // loss and the assembly preload band, all within 0.5% of the published figures
            var ex = SwAgent.Mold.Structural.Fe.Sweep.Solid3dValidation.VdiExample();
            foreach (var p in ex.Properties())
            {
                _out.WriteLine($"{p.Name}: {p.Value["computed"]} vs {p.Value["published"]}");
                Assert.InRange((double)p.Value["deviation"], -0.005, 0.005);
            }
            // the cone-plus-sleeve branch joins the full cone at D_A = D_A,Gr
            double full = Vdi2230.PlateResilience(Vdi2230.Joint.ThroughBolted, 210000, 42, 21.11, 13.5, 80, out double tanPhi, out double gr);
            double atGr = Vdi2230.PlateResilience(Vdi2230.Joint.ThroughBolted, 210000, 42, 21.11, 13.5, gr - 1e-9, out _, out _);
            double below = Vdi2230.PlateResilience(Vdi2230.Joint.ThroughBolted, 210000, 42, 21.11, 13.5, 30, out _, out _);
            Assert.True(below > full, "a narrower plate is softer");
            Assert.True(Math.Abs(atGr - Vdi2230.PlateResilience(Vdi2230.Joint.ThroughBolted, 210000, 42, 21.11, 13.5, gr + 1e-9, out _, out _)) < 0.02 * atGr);
        }

        [Fact]
        public void Vdi2230_cone_against_the_axisymmetric_fe_through_bolted_and_tapped()
        {
            // M10-like, d_W 16, d_h 11, steel, l_K = d_W, D_A = 3 d_W. Through-bolted: two equal plates, rigid head, against
            // 1 / delta_P. Tapped: plate on a tapped base, the FE's head-to-thread approach against delta_P + delta_M (the base's
            // deformation around the thread is booked under delta_M = 0.33 d / E A_N).
            const double e = 205000, nu = 0.29, d = 10, dw = 16, dh = 11, lK = 16, dA = 48;
            double dsv = BoltJointFe.MemberStiffness(e, nu, d, lK, dw, dh, dA / 2, 1, rigidHead: true).Km;
            double dpD = Vdi2230.PlateResilience(Vdi2230.Joint.ThroughBolted, e, lK, dw, dh, dA, out _, out _);
            var tapped = BoltJointFe.TappedMemberStiffness(e, nu, d, lK, dw, dh, dA);
            double dpE = Vdi2230.PlateResilience(Vdi2230.Joint.Tapped, e, lK, dw, dh, dA, out _, out _);
            double dM = 0.33 * d / (e * Math.PI * d * d / 4);
            _out.WriteLine($"DSV FE / VDI {dsv * dpD:F3}; ESV FE / VDI(P+M) {tapped.Km * (dpE + dM):F3}, FE / VDI(P) {tapped.Km * dpE:F3}; plate lifts off the base at r = {tapped.ContactRadius:F1} mm");
            Assert.InRange(dsv * dpD, 0.90, 1.10);
            Assert.InRange(tapped.Km * (dpE + dM), 0.85, 1.25);
            Assert.True(tapped.Km * dpE < 0.8, "the tapped joint's approach is softer than the plate cone alone");
            Assert.InRange(tapped.ContactRadius, dw / 2, dA / 2);
        }
    }
}

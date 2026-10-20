using System;
using System.Linq;

namespace SwAgent.Mold.Structural.Fe.Solid
{
    /// <summary>
    /// Member stiffness of a through-bolted joint of two identical plates by axisymmetric FE, the set-up Wileman et al. (1991)
    /// used for their fit: hole diameter dh, washer-face (head) diameter dw loaded by a uniform pressure, total grip l = two plates
    /// of l/2, plates wide enough not to matter (outer radius Rout). The mid-plane is a frictionless contact plane (the other
    /// plate is the mirror image), so the plates may open beyond the pressure cone. k_m = F / (2 delta), delta = the mean axial
    /// displacement under the washer face.
    /// </summary>
    public static class BoltJointFe
    {
        public sealed class Result
        {
            public double Km, ContactRadius, Delta;
            public int Passes, Dofs;
        }

        /// <param name="rigidHead">Load through a rigid flat head (uniform displacement under the washer face) instead of a uniform pressure.</param>
        /// <param name="bonded">Hold the mid-plane closed everywhere instead of letting the plates open outside the cone.</param>
        public static Result MemberStiffness(double e, double nu, double d, double l, double dw = 0, double dh = 0, double rOut = 0, int refine = 1,
            bool rigidHead = false, bool bonded = false)
        {
            if (dw <= 0) dw = 1.5 * d;
            if (dh <= 0) dh = d;
            if (rOut <= 0) rOut = Math.Max(10 * d, dw / 2 + 1.5 * l);
            double ri = dh / 2, rw = dw / 2, t = l / 2;
            double h = d / (16.0 * refine);
            // fine and uniform from the hole to past the washer edge, then graded out to Rout
            var near = AxisymmetricSolver.Lines(h, ri, rw, rw + Math.Max(t, d));
            var far = AxisymmetricSolver.Graded(near[near.Length - 1], rOut, h, 1.08, Math.Max(h, t / (6.0 * refine)));
            var rs = near.Concat(far.Skip(1)).ToArray();
            var zs = AxisymmetricSolver.Lines(Math.Min(h * 1.5, t / (12.0 * refine)), 0, t);
            const double p = 1.0;
            var m = new AxisymmetricModel { Rs = rs, Zs = zs, E = e, Nu = nu, Bottom = bonded ? BottomCondition.Bonded : BottomCondition.Contact };
            double force, delta;
            if (rigidHead)
            {
                delta = 1e-3 * t;
                m.TopPunch = (ri, rw, delta);
                var r = AxisymmetricSolver.Solve(m);
                force = r.PunchForce;
                return new Result { Km = force / (2 * delta), ContactRadius = r.ContactRadius, Delta = delta, Passes = r.Passes, Dofs = 2 * rs.Length * zs.Length };
            }
            m.TopPressures.Add((ri, rw, p));
            var rp = AxisymmetricSolver.Solve(m);
            force = p * Math.PI * (rw * rw - ri * ri);
            delta = -rp.MeanTopUz(ri, rw);
            return new Result { Km = force / (2 * delta), ContactRadius = rp.ContactRadius, Delta = delta, Passes = rp.Passes, Dofs = 2 * rs.Length * zs.Length };
        }
    }
}

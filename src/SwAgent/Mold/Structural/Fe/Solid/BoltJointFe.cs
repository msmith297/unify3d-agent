using System;
using System.Linq;

namespace SwAgent.Mold.Structural.Fe.Solid
{
    /// <summary>
    /// Member stiffness of bolted joints by axisymmetric FE.
    ///  - MemberStiffness: a through-bolted joint of two identical plates (VDI 2230 "DSV"), the set-up Wileman et al. (1991) used
    ///    for their fit: hole diameter dh, washer-face (head) diameter dw loaded by a uniform pressure or a rigid flat head, total
    ///    grip l = two plates of l/2, outer radius rOut. The mid-plane is a frictionless contact plane (the other plate is the
    ///    mirror image), so the plates may open beyond the pressure cone. k_m = F / (2 delta), delta = the head's mean axial
    ///    displacement.
    ///  - TappedMemberStiffness: a plate of thickness lK clamped by a screw in a tapped base of the same material ("ESV", how mold
    ///    plates are bolted): the plate rests on the base through a frictionless unilateral contact, the head bears through a
    ///    stiff washer, and the engaged thread pulls the base's bore upward over the engagement length. k_m = F / (approach of the
    ///    head and the engaged thread).
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
            if (!(rOut > rw)) throw new ArgumentException("The plates must reach past the washer face (rOut > dw/2).");
            double h = d / (16.0 * refine);
            // fine and uniform from the hole to past the washer edge (or to the rim of a narrow plate), then graded out to Rout
            double nearEnd = Math.Min(rOut, rw + Math.Max(t, d));
            var near = AxisymmetricSolver.Lines(h, ri, rw, nearEnd);
            var rs = nearEnd < rOut
                ? near.Concat(AxisymmetricSolver.Graded(nearEnd, rOut, h, 1.08, Math.Max(h, t / (6.0 * refine))).Skip(1)).ToArray()
                : near;
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

        /// <summary>
        /// Tapped joint (ESV): plate lK thick with a clearance hole dh, outer diameter dA, on a base of the same diameter and
        /// material with a tapped bore of diameter d (the thread's pull acts on the bore wall, uniformly over the engagement length
        /// just below the interface) and engagement + 2 d deep. The head bears through a washer 1000 x stiffer than the plate
        /// (a rigid head, but load-controlled so the force path through the thread stays the real one). ContactRadius is where the
        /// plate lifts off the base.
        /// </summary>
        public static Result TappedMemberStiffness(double e, double nu, double d, double lK, double dw, double dh, double dA, double engagement = 0, int refine = 1)
        {
            if (engagement <= 0) engagement = d;
            double r0 = d / 2, ri = dh / 2, rw = dw / 2, rOut = dA / 2;
            if (!(ri > r0) || !(rw > ri) || !(rOut > rw)) throw new ArgumentException("Need d < dh < dw < dA.");
            double h = d / (16.0 * refine);
            double zb = engagement + 2 * d, tw = 0.25 * d, zTop = zb + lK + tw;
            double nearEnd = Math.Min(rOut, rw + Math.Max(lK, d));
            var near = AxisymmetricSolver.Lines(h, r0, ri, rw, nearEnd);
            var rs = nearEnd < rOut
                ? near.Concat(AxisymmetricSolver.Graded(nearEnd, rOut, h, 1.08, Math.Max(h, lK / (6.0 * refine))).Skip(1)).ToArray()
                : near;
            double hz = Math.Min(h * 1.5, lK / (12.0 * refine));
            var zs = AxisymmetricSolver.Lines(hz, 0, zb - engagement, zb, zb + lK, zTop);
            int kI = Array.FindIndex(zs, z => Math.Abs(z - zb) < 1e-9 * zTop);
            const double p = 1.0;
            double force = p * Math.PI * (rw * rw - ri * ri);
            var m = new AxisymmetricModel
            {
                Rs = rs, Zs = zs, E = e, Nu = nu, Bottom = BottomCondition.None, InterfaceK = kI,
                // base: everything outside the bore; plate: outside the clearance hole; washer: between dh and dw above the plate
                Solid = (r, z) => z < zb ? true : z < zb + lK ? r > ri : r > ri && r < rw,
                Stiffness = (r, z) => z > zb + lK ? 1000 : 1,
            };
            m.TopPressures.Add((ri, rw, p));
            m.BoreLoads.Add((zb - engagement, zb, force));
            m.PinZ.Add((rs.Length - 1, 0));
            var res = AxisymmetricSolver.Solve(m);
            double delta = res.MeanBoreUz(zb - engagement, zb) - res.MeanTopUz(ri, rw);
            return new Result { Km = force / delta, ContactRadius = res.InterfaceContactRadius, Delta = delta, Passes = res.Passes, Dofs = 2 * rs.Length * (zs.Length + 1) };
        }
    }
}

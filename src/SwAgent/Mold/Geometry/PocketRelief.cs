using System;
using System.Collections.Generic;

namespace SwAgent.Mold.Geometry
{
    /// <summary>
    /// Insert pockets (critique B7): an end mill leaves a radius equal to its own in every inside corner, so a pocket split
    /// at the insert's sharp outline won't take the insert without EDM. Two remedies: a dog-bone relief (a circle through the
    /// sharp corner, centred on the bisector out in the steel, radius >= 1.1 x the cutter's) or a pocket corner radius
    /// R >= 1.1 x the cutter's with a matching chamfer on the insert. Pure (GeometryPocketReliefTests).
    /// </summary>
    public static class PocketRelief
    {
        /// <summary>The relief radius for a cutter: 1.1 x its radius (rounded up to 0.01 mm).</summary>
        public static double ReliefRadius(double cutterRadius) => Math.Ceiling(cutterRadius * 1.1 / 1e-5) * 1e-5;

        /// <summary>
        /// Dog-bone centres for a pocket outline (2D, either orientation): at every corner that turns toward the pocket's
        /// inside (a convex corner of the pocket, where the mill leaves its radius), the centre sits on the corner's
        /// bisector at distance r outside the pocket, so the circle of radius r passes through the sharp corner.
        /// `sharp[i]` false skips corner i (already rounded enough).
        /// </summary>
        public static List<double[]> DogBones(IList<double[]> pocket, double r, IList<bool> sharp = null)
        {
            var n = pocket.Count;
            var result = new List<double[]>();
            if (n < 3 || r <= 0) return result;
            var sg = FlashLayout.Area(pocket) > 0 ? 1 : -1;
            for (var i = 0; i < n; i++)
            {
                if (sharp != null && !sharp[i]) continue;
                var prev = pocket[(i + n - 1) % n];
                var cur = pocket[i];
                var next = pocket[(i + 1) % n];
                var turn = FlashLayout.Cross(FlashLayout.Sub(cur, prev), FlashLayout.Sub(next, cur)) * sg;
                if (turn <= 1e-12) continue; // a reflex corner (the steel pokes into the pocket): the mill reaches it
                var a = FlashLayout.Unit(FlashLayout.Sub(prev, cur));
                var b = FlashLayout.Unit(FlashLayout.Sub(next, cur));
                var into = FlashLayout.Add(a, b); // points into the pocket
                if (FlashLayout.Len(into) < 1e-12) continue;
                var outward = FlashLayout.Mul(FlashLayout.Unit(into), -r);
                result.Add(FlashLayout.Add(cur, outward));
            }
            return result;
        }

        /// <summary>
        /// The 45 deg chamfer (leg length) an insert's corner needs to clear a pocket corner radius R at an inside angle
        /// (radians, pi/2 for a square corner): the arc intrudes R (1/sin(a/2) - 1) along the bisector; a chamfer with legs L
        /// removes L cos(a/2) of it. Plus 0.1 mm so the insert doesn't ride on the arc.
        /// </summary>
        public static double InsertChamfer(double pocketRadius, double insideAngle = Math.PI / 2)
        {
            var half = insideAngle / 2;
            var intrusion = pocketRadius * (1 / Math.Sin(half) - 1);
            return intrusion / Math.Cos(half) + 0.0001;
        }
    }
}

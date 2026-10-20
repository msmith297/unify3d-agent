using System;
using System.Globalization;
using System.Text;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>
    /// The derived table the user sees (design 3.20, data/validation/sizing-table.csv): thickness needed against pressure,
    /// span, material and deflection limit, from the coefficient table and the fixed point. Plates span 50-300 mm; cavity walls
    /// use their loaded height (cavity depth) 10-60 mm. Plates past t/b 0.5 read "add support" (shorten the span); walls past
    /// w/h 0.5 read "out of range" (S24: a steel insert, a keyed wall or a 3D check, not a thicker plate-theory wall).
    /// </summary>
    public static class SizingTable
    {
        public static readonly (string Name, double E, double Nu)[] Materials = { ("6061-T6 at 120 C", 66000, 0.33), ("P20 at 120 C", 200000, 0.29) };
        public static readonly double[] Pressures = { 5, 10, 20, 35 };
        public static readonly double[] Spans = { 50, 100, 150, 200, 250, 300 };
        public static readonly double[] Depths = { 10, 20, 30, 40, 50, 60 };
        public static readonly double[] Deltas = { 0.012, 0.020, 0.025, 0.05 };
        /// <summary>(bc, aspect, what the span is).</summary>
        public static readonly (string Bc, double Aspect, string Span)[] Cases =
        {
            ("SS4", 1.0, "panel side b (square, simply supported all round)"),
            ("SS4", 2.0, "short side b (a = 2 b)"),
            ("C4", 1.0, "panel side b (square, clamped all round)"),
            ("C4", 2.0, "short side b (a = 2 b)"),
            ("Rails", 4.0, "rail spacing b (plate 4 b long)"),
            ("Wall-C", 4.0, "cavity depth h (loaded height; wall L = 4 h); t is the wall thickness w"),
            ("Wall-C", 100.0, "cavity depth h (loaded height; long wall: strip with shear); t is the wall thickness w"),
        };

        /// <summary>The CSV text. wallRootSlope c gives the wall-root factor 1 + c w/h (the 3D calibration, solid3d-summary.json).</summary>
        public static string Build(CoefficientTable table, double wallRootSlope = Solid3dValidation.DefaultWallRootSlope)
        {
            Func<double, double> root = wh => 1 + wallRootSlope * wh;
            var sb = new StringBuilder();
            sb.Append("# Required thickness t (mm) for a deflection limit: t = [f alpha(t/b) p b^4 / (E delta)]^(1/3), fixed point from coefficients.csv").Append('\n');
            sb.Append($"# f = 1 for plates; for walls the 3D-calibrated wall-root factor f = 1 + {wallRootSlope.ToString(CultureInfo.InvariantCulture)} w/h. sigma = beta p b^2 / t^2 at that t (walls: bending only, no membrane term). Plates over t/b = 0.5: add support (pillars, rails), do not thicken. Walls over w/h = 0.5: out of range (S24: steel, a keyed wall or a 3D check).").Append('\n');
            sb.Append("# coefficients sha256: ").Append(table.Sha256 ?? "?").Append('\n');
            sb.Append("bc,aspect,span_is,material,E_MPa,nu,delta_mm,span_mm,p_MPa,t_mm,t_over_b,sigma_MPa,note\n");
            foreach (var c in Cases)
            {
                bool wall = CoefficientSweep.IsWall(c.Bc);
                foreach (var mat in Materials)
                    foreach (var d in Deltas)
                        foreach (var b in wall ? Depths : Spans)
                            foreach (var p in Pressures)
                            {
                                double t = table.ThicknessForDeflection(c.Bc, c.Aspect, b, p, mat.E, mat.Nu, d, 1, tau => wall ? root(tau) : 1);
                                double tau = t / b;
                                var (_, beta) = table.Get(c.Bc, c.Aspect, Math.Min(tau, 0.5), mat.Nu);
                                bool over = tau > 0.5;
                                sb.Append(string.Join(",", c.Bc, N(c.Aspect), "\"" + c.Span + "\"", "\"" + mat.Name + "\"", N(mat.E), N(mat.Nu), N(d), N(b), N(p),
                                    over ? "" : t.ToString("F1", CultureInfo.InvariantCulture), tau.ToString("F3", CultureInfo.InvariantCulture),
                                    over ? "" : (beta * p * b * b / (t * t)).ToString("F0", CultureInfo.InvariantCulture),
                                    over ? (wall ? "out of range: steel, keyed wall or 3D check" : "add support") : "")).Append('\n');
                            }
            }
            return sb.ToString();
        }

        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

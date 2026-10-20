using System;
using System.Globalization;
using System.Text;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>
    /// The derived table the user sees (design 3.20, data/validation/sizing-table.csv): thickness needed against pressure,
    /// span, material and deflection limit, from the coefficient table and the fixed point. Cells with t/b above 0.5 read
    /// "add support" (plate theory does not apply; shorten the span instead).
    /// </summary>
    public static class SizingTable
    {
        public static readonly (string Name, double E, double Nu)[] Materials = { ("6061-T6 at 120 C", 66000, 0.33), ("P20 at 120 C", 200000, 0.29) };
        public static readonly double[] Pressures = { 5, 10, 20, 35 };
        public static readonly double[] Spans = { 50, 100, 150, 200, 250, 300 };
        public static readonly double[] Deltas = { 0.012, 0.020, 0.025, 0.05 };
        /// <summary>(bc, aspect, what the span is).</summary>
        public static readonly (string Bc, double Aspect, string Span)[] Cases =
        {
            ("SS4", 1.0, "panel side b (square, simply supported all round)"),
            ("SS4", 2.0, "short side b (a = 2 b)"),
            ("C4", 1.0, "panel side b (square, clamped all round)"),
            ("C4", 2.0, "short side b (a = 2 b)"),
            ("Rails", 4.0, "rail spacing b (plate 4 b long)"),
            ("Wall-C", 4.0, "loaded height h (wall L = 4 h); wall-root factor applied"),
            ("Wall-C", 100.0, "loaded height h (long wall: strip with shear); wall-root factor applied"),
        };

        public static string Build(CoefficientTable table, double wallRootFactor = 1.2)
        {
            var sb = new StringBuilder();
            sb.Append("# Required thickness t (mm) for a deflection limit: t = [f alpha(t/b) p b^4 / (E delta)]^(1/3), fixed point from coefficients.csv").Append('\n');
            sb.Append($"# f = 1 for plates, {wallRootFactor.ToString(CultureInfo.InvariantCulture)} (wall-root factor) for walls. sigma = beta p b^2 / t^2 at that t. Over t/b = 0.5: add support (pillars, rails), do not thicken.").Append('\n');
            sb.Append("# coefficients sha256: ").Append(table.Sha256 ?? "?").Append('\n');
            sb.Append("bc,aspect,span_is,material,E_MPa,nu,delta_mm,span_mm,p_MPa,t_mm,t_over_b,sigma_MPa,note\n");
            foreach (var c in Cases)
                foreach (var mat in Materials)
                    foreach (var d in Deltas)
                        foreach (var b in Spans)
                            foreach (var p in Pressures)
                            {
                                double f = CoefficientSweep.IsWall(c.Bc) ? wallRootFactor : 1;
                                double t = table.ThicknessForDeflection(c.Bc, c.Aspect, b, p, mat.E, mat.Nu, d, 1, f);
                                double tau = t / b;
                                var (_, beta) = table.Get(c.Bc, c.Aspect, Math.Min(tau, 0.5), mat.Nu);
                                bool over = tau > 0.5;
                                sb.Append(string.Join(",", c.Bc, N(c.Aspect), "\"" + c.Span + "\"", "\"" + mat.Name + "\"", N(mat.E), N(mat.Nu), N(d), N(b), N(p),
                                    over ? "" : t.ToString("F1", CultureInfo.InvariantCulture), tau.ToString("F3", CultureInfo.InvariantCulture),
                                    over ? "" : (beta * p * b * b / (t * t)).ToString("F0", CultureInfo.InvariantCulture), over ? "add support" : "")).Append('\n');
                            }
            return sb.ToString();
        }

        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

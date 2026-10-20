using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SwAgent.Mold
{
    /// <summary>
    /// Thick-plate (Mindlin) coefficients from the embedded Mold.coefficients.csv (design 2.4):
    /// w_max = alpha p b^4/(E t^3), sigma_max = beta p b^2/t^2, with b the short side (h for walls).
    /// Columns: bc, aspect, tau, nu, loadFrac, alpha, beta, wMaxX, wMaxY. Interpolation is bilinear in (aspect, tau²) - tau² is
    /// the natural variable because FSDT deflection is linear in tau² for simply supported plates.
    ///
    /// Boundary sets: SS4, C4 (symmetric plates: aspect = long/short, so a/b and b/a are the same), Rails, C2S2, Overhang,
    /// Pillar (aspect as the set defines it, clamped to the table), Wall-C, Wall-S (walls; aspect = L/h, below 1 allowed).
    /// Wall coefficients are the running maximum over L/h (a longer wall is never sized thinner: the A4 monotonic rule);
    /// above the last tabulated L/h they are max(that envelope, the cantilever strip with shear) (critique A4, Track A2):
    /// alpha = 12(1-nu²)/8 + (1+nu)/kappa tau², beta = 3. Rows are not clamped to the strip: thick walls overshoot it by up to
    /// about 1% near L/h 6-8 (a real Mindlin effect, Track A2), and the envelope keeps that overshoot for longer walls. Below the first tau the first row is used; above the last tau
    /// alpha is extended linearly in tau² (results there are flagged by the tauMax gate anyway). nu is interpolated linearly
    /// between the tabulated values (clamped), loadFrac likewise. The interpolated alpha and beta are multiplied by the
    /// table's interpolation allowance (Track A2: 1.01 covers the worst under-read between rows), read from a header line
    /// "# allowance: x"; without one, 1.01 for a swept table and 1.0 for the provisional one.
    /// </summary>
    public static class Coefficients
    {
        private sealed class Row
        {
            public string Bc;
            public double Aspect, Tau, Nu, LoadFrac, Alpha, Beta, WX, WY;
        }

        private static readonly Lazy<Table> _table = new Lazy<Table>(() => Table.Load());

        private sealed class Table
        {
            public List<Row> Rows = new List<Row>();
            public string Version = "unknown", Sha = "", Header = "";
            public bool Provisional;
            public double? AllowanceHeader;
            public double Allowance => AllowanceHeader ?? (Provisional ? 1.0 : 1.01);

            public static Table Load()
            {
                using (var s = typeof(Coefficients).Assembly.GetManifestResourceStream("Mold.coefficients.csv"))
                {
                    if (s == null) throw new InvalidOperationException("The embedded Mold.coefficients.csv is missing from the add-in.");
                    using (var rd = new StreamReader(s)) return Parse(rd.ReadToEnd());
                }
            }

            public static Table Parse(string text)
            {
                var t = new Table();
                var head = new StringBuilder();
                var body = new StringBuilder();
                foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
                {
                    var line = raw.TrimEnd();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("#"))
                    {
                        head.AppendLine(line);
                        var l = line.TrimStart('#').Trim();
                        if (l.StartsWith("provisional", StringComparison.OrdinalIgnoreCase)) t.Provisional = true;
                        if (l.StartsWith("version:", StringComparison.OrdinalIgnoreCase)) t.Version = l.Substring(8).Trim();
                        else if (l.StartsWith("generator:", StringComparison.OrdinalIgnoreCase) && t.Version == "unknown") t.Version = l.Substring(10).Trim().Split(';')[0].Trim();
                        if (l.StartsWith("allowance:", StringComparison.OrdinalIgnoreCase) && double.TryParse(l.Substring(10).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var al)) t.AllowanceHeader = al;
                        if (l.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) t.Sha = l.Substring(7).Trim();
                        continue;
                    }
                    body.Append(line).Append('\n');
                    if (line.StartsWith("bc,", StringComparison.OrdinalIgnoreCase)) continue;
                    var c = line.Split(',');
                    if (c.Length < 9) continue;
                    double P(int i) => double.Parse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture);
                    t.Rows.Add(new Row { Bc = c[0].Trim(), Aspect = P(1), Tau = P(2), Nu = P(3), LoadFrac = P(4), Alpha = P(5), Beta = P(6), WX = P(7), WY = P(8) });
                }
                t.Header = head.ToString();
                t.BodySha = Hex(SHA256.Create().ComputeHash(Encoding.ASCII.GetBytes(body.ToString())));
                if (t.Version == "unknown" && t.Sha.Length >= 12) t.Version = "sha-" + t.Sha.Substring(0, 12);
                return t;
            }

            public string BodySha;
        }

        private static string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

        /// <summary>Generator version from the CSV header ("0.1.0-provisional" until Track A2's sweep replaces it).</summary>
        public static string Version => _table.Value.Version;

        /// <summary>SHA-256 recorded in the header, and the one computed over the data lines (they must match).</summary>
        public static string HeaderSha => _table.Value.Sha;
        public static string DataSha => _table.Value.BodySha;
        public static bool Provisional => _table.Value.Provisional;
        /// <summary>The factor on interpolated alpha and beta (see the class comment).</summary>
        public static double Allowance => _table.Value.Allowance;
        public static IReadOnlyList<string> BoundarySets => _table.Value.Rows.Select(r => r.Bc).Distinct().ToList();

        /// <summary>Cantilever strip with shear (kappa 5/6): the L/h -> infinity limit of the wall sets.</summary>
        public static (double Alpha, double Beta) Strip(double tau, double nu) =>
            (12 * (1 - nu * nu) / 8 + (1 + nu) / (5.0 / 6.0) * tau * tau, 3.0);

        /// <summary>
        /// One-way strip of span b (the limit of a very long plate): simply supported (SS4, Rails) alpha = 5/384 x 12(1-nu²)
        /// + (1+nu)/(4 kappa) tau², beta = 0.75; clamped (C4) alpha = 1/384 x 12(1-nu²) + (1+nu)/(4 kappa) tau², beta = 0.5.
        /// </summary>
        public static (double Alpha, double Beta) OneWay(string bc, double tau, double nu)
        {
            // an overhang tends to the cantilever strip; clamped long edges (C4, C2S2) to the clamped strip; the rest to the
            // simply supported strip
            if (string.Equals(bc, "Overhang", StringComparison.OrdinalIgnoreCase)) return Strip(tau, nu);
            var shear = (1 + nu) / (4 * (5.0 / 6.0)) * tau * tau;
            return string.Equals(bc, "C4", StringComparison.OrdinalIgnoreCase) || string.Equals(bc, "C2S2", StringComparison.OrdinalIgnoreCase)
                ? (12 * (1 - nu * nu) / 384 + shear, 0.5)
                : (5 * 12 * (1 - nu * nu) / 384 + shear, 0.75);
        }

        public static bool IsWall(string bc) => bc.StartsWith("Wall", StringComparison.OrdinalIgnoreCase);

        /// <summary>Sets whose a/b and b/a are the same plate (all four edges alike).</summary>
        public static bool IsSymmetric(string bc) => string.Equals(bc, "SS4", StringComparison.OrdinalIgnoreCase) || string.Equals(bc, "C4", StringComparison.OrdinalIgnoreCase);

        public static bool Has(string bc) => _table.Value.Rows.Any(r => string.Equals(r.Bc, bc, StringComparison.OrdinalIgnoreCase));

        public static (double Alpha, double Beta) Get(string bc, double aspect, double tOverB, double nu, double loadFrac)
        {
            var rows = _table.Value.Rows.Where(r => string.Equals(r.Bc, bc, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) throw new ArgumentException("No coefficients for boundary set '" + bc + "' (have " + string.Join(", ", BoundarySets) + ")");
            var wall = IsWall(bc);
            if (IsSymmetric(bc) && aspect < 1) aspect = 1 / aspect;   // a/b and b/a are the same plate

            // nu: linear between the tabulated values (clamped); per nu, loadFrac likewise
            var nus = rows.Select(r => r.Nu).Distinct().OrderBy(x => x).ToArray();
            var nuC = Math.Max(nus[0], Math.Min(nus[nus.Length - 1], nu));
            (double, double) AtNu(double n)
            {
                var set = rows.Where(r => r.Nu == n).ToList();
                var fracs = set.Select(r => r.LoadFrac).Distinct().OrderBy(x => x).ToArray();
                (double, double) AtFrac(double f) => Bilinear(set.Where(r => r.LoadFrac == f).ToList(), aspect, tOverB, wall, n);
                if (fracs.Length == 1 || loadFrac >= fracs[fracs.Length - 1]) return AtFrac(fracs[fracs.Length - 1]);
                if (loadFrac <= fracs[0]) return AtFrac(fracs[0]);
                var i = Array.FindLastIndex(fracs, f => f <= loadFrac);
                var a = AtFrac(fracs[i]);
                var b = AtFrac(fracs[i + 1]);
                var w = (loadFrac - fracs[i]) / (fracs[i + 1] - fracs[i]);
                return (a.Item1 + (b.Item1 - a.Item1) * w, a.Item2 + (b.Item2 - a.Item2) * w);
            }
            (double A, double B) res;
            var lo = nus.Where(x => x <= nuC).Max();
            var hi = nus.Where(x => x >= nuC).Min();
            if (hi <= lo) res = AtNu(lo);
            else
            {
                var a = AtNu(lo);
                var b = AtNu(hi);
                var w = (nuC - lo) / (hi - lo);
                res = (a.Item1 + (b.Item1 - a.Item1) * w, a.Item2 + (b.Item2 - a.Item2) * w);
            }
            var k = _table.Value.Allowance;
            return (res.A * k, res.B * k);
        }

        private static (double, double) Bilinear(List<Row> rows, double aspect, double tau, bool wall, double nu)
        {
            var aspects = rows.Select(r => r.Aspect).Distinct().OrderBy(x => x).ToArray();
            var strip = Strip(tau, nu);
            if (wall)
            {
                // Walls use the running maximum over L/h: thick walls overshoot the strip by up to about 1% near L/h 6-8
                // and then fall back to it (Track A2), but a longer wall must never be sized thinner than a shorter one
                // (A4 monotonic rule). Above the last row: the larger of that envelope and the strip with shear (A4).
                var env = (A: 0.0, B: 0.0);
                foreach (var a in aspects.Where(x => x <= aspect + 1e-12))
                {
                    var ra = InTau(rows.Where(r => r.Aspect == a).ToList(), tau);
                    env = (Math.Max(env.A, ra.Item1), Math.Max(env.B, ra.Item2));
                }
                if (aspect > aspects[aspects.Length - 1]) return (Math.Max(env.A, strip.Alpha), Math.Max(env.B, strip.Beta));
                var lerp = Lerp(rows, aspects, aspect, tau);
                return (Math.Max(env.A, lerp.Item1), Math.Max(env.B, lerp.Item2));
            }
            if (!wall && aspect > aspects[aspects.Length - 1])
            {
                // a long plate tends to the one-way strip across the short side; never less than the last row (same idea as A4)
                var last = InTau(rows.Where(r => r.Aspect == aspects[aspects.Length - 1]).ToList(), tau);
                var one = OneWay(rows[0].Bc, tau, nu);
                return (Math.Max(last.Item1, one.Alpha), Math.Max(last.Item2, one.Beta));
            }
            return Lerp(rows, aspects, aspect, tau);
        }

        private static (double, double) Lerp(List<Row> rows, double[] aspects, double aspect, double tau)
        {
            var a0 = aspects.Where(x => x <= aspect).DefaultIfEmpty(aspects[0]).Max();
            var a1 = aspects.Where(x => x >= aspect).DefaultIfEmpty(aspects[aspects.Length - 1]).Min();
            var r0 = InTau(rows.Where(r => r.Aspect == a0).ToList(), tau);
            var r1 = InTau(rows.Where(r => r.Aspect == a1).ToList(), tau);
            var w = a1 > a0 ? (aspect - a0) / (a1 - a0) : 0;
            return (r0.Item1 + (r1.Item1 - r0.Item1) * w, r0.Item2 + (r1.Item2 - r0.Item2) * w);
        }

        private static (double, double) InTau(List<Row> rows, double tau)
        {
            var o = rows.OrderBy(r => r.Tau).ToList();
            var t2 = tau * tau;
            if (t2 <= o[0].Tau * o[0].Tau) return (o[0].Alpha, o[0].Beta);
            for (var i = 0; i < o.Count - 1; i++)
            {
                var x0 = o[i].Tau * o[i].Tau;
                var x1 = o[i + 1].Tau * o[i + 1].Tau;
                if (t2 <= x1)
                {
                    var w = (t2 - x0) / (x1 - x0);
                    return (o[i].Alpha + (o[i + 1].Alpha - o[i].Alpha) * w, o[i].Beta + (o[i + 1].Beta - o[i].Beta) * w);
                }
            }
            // Above the last tau (stocky plates, flagged by the tauMax gate): alpha keeps rising with tau², so it is extended
            // linearly in tau² from the last two rows (conservative); beta stays at the last row.
            var last = o[o.Count - 1];
            var prev = o[o.Count - 2];
            var slope = (last.Alpha - prev.Alpha) / (last.Tau * last.Tau - prev.Tau * prev.Tau);
            return (last.Alpha + Math.Max(0, slope) * (t2 - last.Tau * last.Tau), last.Beta);
        }
    }
}

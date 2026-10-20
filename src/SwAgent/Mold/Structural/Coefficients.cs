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
    /// Boundary sets: SS4, C4, Rails (plates; aspect = long/short, so a/b and b/a are the same), Wall-C, Wall-S (walls;
    /// aspect = L/h, below 1 allowed). Walls above the last tabulated L/h use the cantilever strip with shear
    /// (critique A4): alpha = 12(1-nu²)/8 + (1+nu)/kappa tau², beta = 3; and no wall row is allowed above the strip (the strip
    /// is the L/h -> infinity limit; a mesh overshoot is clamped). Below the first tau the first row is used; above the last
    /// tau alpha is extended linearly in tau² (results there are flagged by the tauMax gate anyway). nu and loadFrac pick
    /// the nearest tabulated nu and interpolate loadFrac
    /// (clamped), until Track A2's full sweep fills those columns.
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
            var shear = (1 + nu) / (4 * (5.0 / 6.0)) * tau * tau;
            return string.Equals(bc, "C4", StringComparison.OrdinalIgnoreCase)
                ? (12 * (1 - nu * nu) / 384 + shear, 0.5)
                : (5 * 12 * (1 - nu * nu) / 384 + shear, 0.75);
        }

        public static bool IsWall(string bc) => bc.StartsWith("Wall", StringComparison.OrdinalIgnoreCase);

        public static (double Alpha, double Beta) Get(string bc, double aspect, double tOverB, double nu, double loadFrac)
        {
            var rows = _table.Value.Rows.Where(r => string.Equals(r.Bc, bc, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) throw new ArgumentException("No coefficients for boundary set '" + bc + "' (have " + string.Join(", ", BoundarySets) + ")");
            var wall = IsWall(bc);
            if (!wall && aspect < 1) aspect = 1 / aspect;   // a/b and b/a are the same plate

            // nearest tabulated nu, then loadFrac interpolation (clamped)
            var nuNear = rows.Select(r => r.Nu).Distinct().OrderBy(n => Math.Abs(n - nu)).First();
            rows = rows.Where(r => r.Nu == nuNear).ToList();
            var fracs = rows.Select(r => r.LoadFrac).Distinct().OrderBy(x => x).ToArray();
            // the strip limit uses the table's nu, so the wall coefficients stay monotonic in L/h across the last row
            (double, double) AtFrac(double f) => Bilinear(rows.Where(r => r.LoadFrac == f).ToList(), aspect, tOverB, wall, nuNear);
            (double A, double B) res;
            if (fracs.Length == 1 || loadFrac >= fracs[fracs.Length - 1]) res = AtFrac(fracs[fracs.Length - 1]);
            else if (loadFrac <= fracs[0]) res = AtFrac(fracs[0]);
            else
            {
                var i = Array.FindLastIndex(fracs, f => f <= loadFrac);
                var a = AtFrac(fracs[i]);
                var b = AtFrac(fracs[i + 1]);
                var w = (loadFrac - fracs[i]) / (fracs[i + 1] - fracs[i]);
                res = (a.Item1 + (b.Item1 - a.Item1) * w, a.Item2 + (b.Item2 - a.Item2) * w);
            }
            return res;
        }

        private static (double, double) Bilinear(List<Row> rows, double aspect, double tau, bool wall, double nu)
        {
            var aspects = rows.Select(r => r.Aspect).Distinct().OrderBy(x => x).ToArray();
            var strip = Strip(tau, nu);
            if (wall && aspect > aspects[aspects.Length - 1])
                return strip; // critique A4: above the last row the strip with shear, never the last row
            if (!wall && aspect > aspects[aspects.Length - 1])
            {
                // a long plate tends to the one-way strip across the short side; never less than the last row (same idea as A4)
                var last = InTau(rows.Where(r => r.Aspect == aspects[aspects.Length - 1]).ToList(), tau);
                var one = OneWay(rows[0].Bc, tau, nu);
                return (Math.Max(last.Item1, one.Alpha), Math.Max(last.Item2, one.Beta));
            }
            var a0 = aspects.Where(x => x <= aspect).DefaultIfEmpty(aspects[0]).Max();
            var a1 = aspects.Where(x => x >= aspect).DefaultIfEmpty(aspects[aspects.Length - 1]).Min();
            var r0 = InTau(rows.Where(r => r.Aspect == a0).ToList(), tau);
            var r1 = InTau(rows.Where(r => r.Aspect == a1).ToList(), tau);
            var w = a1 > a0 ? (aspect - a0) / (a1 - a0) : 0;
            var al = r0.Item1 + (r1.Item1 - r0.Item1) * w;
            var be = r0.Item2 + (r1.Item2 - r0.Item2) * w;
            if (wall)
            {
                al = Math.Min(al, strip.Alpha);
                be = Math.Min(be, strip.Beta);
            }
            return (al, be);
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

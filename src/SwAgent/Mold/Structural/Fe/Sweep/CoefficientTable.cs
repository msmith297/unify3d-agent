using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SwAgent.Mold.Structural.Fe.Sweep
{
    /// <summary>
    /// coefficients.csv in memory, for the sweep console, the sizing table and the Monte Carlo stub (the shipped reader is
    /// A1's SwAgent.Mold.Coefficients). Bilinear in (aspect, tau^2), linear in nu and in the loaded fraction. Walls beyond the
    /// last tabulated L/h use the cantilever strip with shear (critique A4); other sets hold their last row.
    /// </summary>
    public sealed class CoefficientTable
    {
        sealed class Block
        {
            public double[] Aspects, Taus;
            public double[,] Alpha, Beta;
        }

        readonly Dictionary<(string, double, double), Block> _blocks = new Dictionary<(string, double, double), Block>();
        readonly Dictionary<string, double[]> _nus = new Dictionary<string, double[]>();
        readonly Dictionary<string, double[]> _fracs = new Dictionary<string, double[]>();
        public string Sha256 { get; private set; }
        public List<string> Comments { get; } = new List<string>();
        public List<CoefficientRow> Rows { get; } = new List<CoefficientRow>();

        public static CoefficientTable Load(string path) => Parse(File.ReadAllText(path));

        public static CoefficientTable Parse(string text)
        {
            var t = new CoefficientTable();
            var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
            int header = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("#"))
                {
                    t.Comments.Add(lines[i]);
                    if (lines[i].StartsWith("# sha256: ")) t.Sha256 = lines[i].Substring(10).Trim();
                    continue;
                }
                header = i;
                break;
            }
            if (header < 0) throw new FormatException("coefficients.csv has no header.");
            var cols = lines[header].Split(',');
            int Col(string n) { int k = Array.IndexOf(cols, n); if (k < 0) throw new FormatException("Missing column " + n); return k; }
            int cBc = Col("bc"), cA = Col("aspect"), cT = Col("tau"), cN = Col("nu"), cL = Col("loadFrac"), cAl = Col("alpha"), cBe = Col("beta");
            int cX = Array.IndexOf(cols, "wMaxX"), cY = Array.IndexOf(cols, "wMaxY");
            double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
            for (int i = header + 1; i < lines.Count; i++)
            {
                if (lines[i].StartsWith("#")) continue;
                var f = lines[i].Split(',');
                t.Rows.Add(new CoefficientRow
                {
                    Bc = f[cBc], Aspect = D(f[cA]), Tau = D(f[cT]), Nu = D(f[cN]), LoadFrac = D(f[cL]), Alpha = D(f[cAl]), Beta = D(f[cBe]),
                    WMaxX = cX >= 0 ? D(f[cX]) : 0, WMaxY = cY >= 0 ? D(f[cY]) : 0,
                });
            }
            foreach (var g in t.Rows.GroupBy(r => (r.Bc, r.Nu, r.LoadFrac)))
            {
                var asp = g.Select(r => r.Aspect).Distinct().OrderBy(v => v).ToArray();
                var tau = g.Select(r => r.Tau).Distinct().OrderBy(v => v).ToArray();
                var b = new Block { Aspects = asp, Taus = tau, Alpha = new double[asp.Length, tau.Length], Beta = new double[asp.Length, tau.Length] };
                foreach (var row in g)
                {
                    int i = Array.IndexOf(asp, row.Aspect), j = Array.IndexOf(tau, row.Tau);
                    b.Alpha[i, j] = row.Alpha; b.Beta[i, j] = row.Beta;
                }
                t._blocks[g.Key] = b;
            }
            foreach (var g in t.Rows.GroupBy(r => r.Bc))
            {
                t._nus[g.Key] = g.Select(r => r.Nu).Distinct().OrderBy(v => v).ToArray();
                t._fracs[g.Key] = g.Select(r => r.LoadFrac).Distinct().OrderBy(v => v).ToArray();
            }
            return t;
        }

        public IEnumerable<string> BoundarySets => _nus.Keys;

        /// <summary>(alpha, beta) for w = alpha p b^4 / (E t^3) and sigma = beta p b^2 / t^2. tau is clamped to the table.</summary>
        public (double Alpha, double Beta) Get(string bc, double aspect, double tau, double nu, double loadFrac = 1)
        {
            if (!_nus.TryGetValue(bc, out var nus)) throw new ArgumentException("No rows for " + bc);
            var fr = _fracs[bc];
            var (n0, n1, wn) = Bracket(nus, nu);
            var (f0, f1, wf) = Bracket(fr, loadFrac);
            (double, double) At(double n, double f) => Bilinear(_blocks[(bc, n, f)], bc, aspect, tau, n);
            var a00 = At(n0, f0); var a01 = At(n0, f1); var a10 = At(n1, f0); var a11 = At(n1, f1);
            double Mix(double v00, double v01, double v10, double v11) => (1 - wn) * ((1 - wf) * v00 + wf * v01) + wn * ((1 - wf) * v10 + wf * v11);
            return (Mix(a00.Item1, a01.Item1, a10.Item1, a11.Item1), Mix(a00.Item2, a01.Item2, a10.Item2, a11.Item2));
        }

        static (double, double, double) Bracket(double[] xs, double x)
        {
            if (xs.Length == 1 || x <= xs[0]) return (xs[0], xs[0], 0);
            if (x >= xs[xs.Length - 1]) return (xs[xs.Length - 1], xs[xs.Length - 1], 0);
            int k = 0;
            while (xs[k + 1] < x) k++;
            return (xs[k], xs[k + 1], (x - xs[k]) / (xs[k + 1] - xs[k]));
        }

        static (double, double) Bilinear(Block b, string bc, double aspect, double tau, double nu)
        {
            double tauC = Math.Max(b.Taus[0], Math.Min(b.Taus[b.Taus.Length - 1], tau));
            if (CoefficientSweep.IsWall(bc) && aspect > b.Aspects[b.Aspects.Length - 1])
            {
                // beyond the last row: the strip with shear, or the last row where a thick wall sits slightly above the strip
                var strip = ClosedForm.CantileverStrip(nu, tauC);
                var last = Bilinear(b, bc, b.Aspects[b.Aspects.Length - 1], tau, nu);
                return (Math.Max(strip.Alpha, last.Item1), Math.Max(strip.Beta, last.Item2));
            }
            var (i0, i1, wa) = Index(b.Aspects, aspect);
            var t2 = b.Taus.Select(v => v * v).ToArray();
            var (j0, j1, wt) = Index(t2, tauC * tauC);
            double Bi(double[,] v) => (1 - wa) * ((1 - wt) * v[i0, j0] + wt * v[i0, j1]) + wa * ((1 - wt) * v[i1, j0] + wt * v[i1, j1]);
            return (Bi(b.Alpha), Bi(b.Beta));
        }

        static (int, int, double) Index(double[] xs, double x)
        {
            if (x <= xs[0]) return (0, 0, 0);
            if (x >= xs[xs.Length - 1]) return (xs.Length - 1, xs.Length - 1, 0);
            int k = 0;
            while (xs[k + 1] < x) k++;
            return (k, k + 1, (x - xs[k]) / (xs[k + 1] - xs[k]));
        }

        /// <summary>
        /// Thickness for a deflection limit by fixed point (S21/S29): t = [f alpha(t/b) p b^4 / (E delta)]^(1/3) from t0 = 0.2 b,
        /// f = 1 for plates or the wall-root factor for walls. Returns t and the converged tau; tau above 0.5 means "add support".
        /// </summary>
        public double ThicknessForDeflection(string bc, double aspect, double b, double p, double e, double nu, double delta, double loadFrac = 1, double factor = 1)
        {
            double t = (IsWallLike(bc) ? 0.3 : 0.2) * b;
            for (int k = 0; k < 200; k++)
            {
                var (alpha, _) = Get(bc, aspect, t / b, nu, loadFrac);
                double tn = Math.Pow(factor * alpha * p * Math.Pow(b, 4) / (e * delta), 1.0 / 3);
                if (Math.Abs(tn - t) < 1e-7 * t) return tn;
                t = tn;
            }
            return t;
        }

        static bool IsWallLike(string bc) => CoefficientSweep.IsWall(bc);
    }
}

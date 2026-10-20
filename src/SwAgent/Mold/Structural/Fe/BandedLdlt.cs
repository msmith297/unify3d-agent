using System;
using System.Threading;

namespace SwAgent.Mold.Structural.Fe
{
    /// <summary>
    /// Symmetric banded matrix with an in-place LDL^T factorisation (no pivoting; the stiffness matrices here are positive
    /// definite once supports are eliminated). Stored by columns: column j holds rows j - Band .. j contiguously, so both the
    /// factorisation and the two substitutions run over contiguous memory. Cost n * Band^2 / 2.
    /// </summary>
    public sealed class BandedLdlt
    {
        public int N { get; }
        public int Band { get; }
        readonly double[] _a;
        bool _factored;

        public BandedLdlt(int n, int band)
        {
            if (n <= 0) throw new ArgumentException("Empty system.");
            N = n; Band = Math.Max(0, Math.Min(band, n - 1));
            _a = new double[(long)n * (Band + 1)];
        }

        int Idx(int i, int j) => j * (Band + 1) + Band - (j - i);

        /// <summary>Adds v to A(i, j) and A(j, i). Entries outside the band throw.</summary>
        public void Add(int i, int j, double v)
        {
            if (i > j) { var t = i; i = j; j = t; }
            if (j - i > Band) throw new InvalidOperationException($"Entry ({i},{j}) is outside the band {Band}.");
            _a[Idx(i, j)] += v;
        }

        public double Get(int i, int j)
        {
            if (i > j) { var t = i; i = j; j = t; }
            return j - i > Band ? 0 : _a[Idx(i, j)];
        }

        /// <summary>A x for the unfactored matrix (used by tests and residual checks).</summary>
        public double[] Multiply(double[] x)
        {
            if (_factored) throw new InvalidOperationException("Matrix already factored.");
            var y = new double[N];
            for (int j = 0; j < N; j++)
            {
                int i0 = Math.Max(0, j - Band), c = j * (Band + 1) + Band - j;
                for (int i = i0; i < j; i++) { double v = _a[c + i]; y[i] += v * x[j]; y[j] += v * x[i]; }
                y[j] += _a[c + j] * x[j];
            }
            return y;
        }

        /// <summary>Crout LDL^T. Throws when a pivot vanishes (a mechanism: missing supports).</summary>
        public void Factor(CancellationToken ct = default)
        {
            if (_factored) return;
            int bw = Band;
            var w = new double[N];
            double maxDiag = 0;
            for (int j = 0; j < N; j++) maxDiag = Math.Max(maxDiag, Math.Abs(_a[Idx(j, j)]));
            for (int j = 0; j < N; j++)
            {
                if ((j & 1023) == 0) ct.ThrowIfCancellationRequested();
                int k0 = Math.Max(0, j - bw), cj = j * (bw + 1) + bw - j; // _a[cj + i] = A(i, j)
                double dj = _a[cj + j];
                for (int k = k0; k < j; k++)
                {
                    int ck = k * (bw + 1) + bw - k; // U(m, k) = _a[ck + m]
                    int m0 = Math.Max(k0, k - bw);
                    double s = _a[cj + k];
                    for (int m = m0; m < k; m++) s -= _a[ck + m] * w[m];
                    w[k] = s;
                }
                for (int k = k0; k < j; k++)
                {
                    double u = w[k] / _a[Idx(k, k)];
                    dj -= u * w[k];
                    _a[cj + k] = u;
                }
                if (!(Math.Abs(dj) > 1e-14 * maxDiag))
                    throw new InvalidOperationException($"Singular stiffness at equation {j}: the model is a mechanism (check the supports).");
                _a[cj + j] = dj;
            }
            _factored = true;
        }

        public double[] Solve(double[] b)
        {
            if (!_factored) Factor();
            int bw = Band;
            var x = (double[])b.Clone();
            for (int j = 0; j < N; j++)
            {
                int k0 = Math.Max(0, j - bw), cj = j * (bw + 1) + bw - j;
                double s = x[j];
                for (int k = k0; k < j; k++) s -= _a[cj + k] * x[k];
                x[j] = s;
            }
            for (int j = 0; j < N; j++) x[j] /= _a[Idx(j, j)];
            for (int j = N - 1; j >= 0; j--)
            {
                int k0 = Math.Max(0, j - bw), cj = j * (bw + 1) + bw - j;
                double xj = x[j];
                for (int k = k0; k < j; k++) x[k] -= _a[cj + k] * xj;
            }
            return x;
        }

        /// <summary>Number of negative pivots (the inertia; 0 for a positive definite matrix).</summary>
        public int NegativePivots()
        {
            if (!_factored) throw new InvalidOperationException("Factor first.");
            int n = 0;
            for (int j = 0; j < N; j++) if (_a[Idx(j, j)] < 0) n++;
            return n;
        }
    }
}

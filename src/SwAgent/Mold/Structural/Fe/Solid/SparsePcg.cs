using System;
using System.Threading;
using System.Threading.Tasks;

namespace SwAgent.Mold.Structural.Fe.Solid
{
    /// <summary>Symmetric sparse matrix in CSR form (full rows, columns sorted) for the 3D validation solver.</summary>
    public sealed class Csr
    {
        public readonly int N;
        public readonly int[] RowPtr, Col;
        public readonly double[] Val;

        public Csr(int n, int[] rowPtr, int[] col)
        {
            N = n; RowPtr = rowPtr; Col = col; Val = new double[col.Length];
        }

        /// <summary>Position of (r, c) in the pattern, or -1.</summary>
        public int Find(int r, int c)
        {
            int lo = RowPtr[r], hi = RowPtr[r + 1] - 1;
            while (lo <= hi)
            {
                int m = (lo + hi) >> 1;
                if (Col[m] == c) return m;
                if (Col[m] < c) lo = m + 1; else hi = m - 1;
            }
            return -1;
        }

        public void Multiply(double[] x, double[] y)
        {
            Parallel.For(0, (N + 4095) / 4096, blk =>
            {
                int r1 = Math.Min(N, (blk + 1) * 4096);
                for (int r = blk * 4096; r < r1; r++)
                {
                    double s = 0;
                    for (int k = RowPtr[r]; k < RowPtr[r + 1]; k++) s += Val[k] * x[Col[k]];
                    y[r] = s;
                }
            });
        }

        public double Diagonal(int r) { int k = Find(r, r); return k < 0 ? 0 : Val[k]; }
    }

    /// <summary>
    /// Preconditioned conjugate gradients with an incomplete Cholesky IC(0) preconditioner (no fill; the diagonal is shifted
    /// and the factorisation retried if a pivot breaks down, and plain Jacobi is the last resort).
    /// </summary>
    public static class Pcg
    {
        public sealed class Result
        {
            public double[] X;
            public int Iterations;
            public double RelativeResidual;
            public bool Converged;
            public string Preconditioner;
        }

        public static Result Solve(Csr a, double[] b, double tol = 1e-9, int maxIter = 20000, CancellationToken ct = default)
        {
            var pre = IncompleteCholesky.TryCreate(a, out string name);
            int n = a.N;
            var x = new double[n];
            var r = (double[])b.Clone();
            var z = new double[n];
            var p = new double[n];
            var q = new double[n];
            double bn = Math.Sqrt(Dot(b, b));
            var res = new Result { X = x, Preconditioner = name };
            if (bn == 0) { res.Converged = true; return res; }
            Apply(pre, a, r, z);
            Array.Copy(z, p, n);
            double rz = Dot(r, z);
            for (int it = 1; it <= maxIter; it++)
            {
                if ((it & 63) == 0) ct.ThrowIfCancellationRequested();
                a.Multiply(p, q);
                double alpha = rz / Dot(p, q);
                for (int i = 0; i < n; i++) { x[i] += alpha * p[i]; r[i] -= alpha * q[i]; }
                double rn = Math.Sqrt(Dot(r, r));
                res.Iterations = it;
                res.RelativeResidual = rn / bn;
                if (rn <= tol * bn) { res.Converged = true; break; }
                Apply(pre, a, r, z);
                double rzNew = Dot(r, z);
                double beta = rzNew / rz;
                rz = rzNew;
                for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
            }
            return res;
        }

        static void Apply(IncompleteCholesky pre, Csr a, double[] r, double[] z)
        {
            if (pre != null) { pre.Solve(r, z); return; }
            for (int i = 0; i < r.Length; i++) z[i] = r[i] / a.Diagonal(i);
        }

        static double Dot(double[] a, double[] b)
        {
            double s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }
    }

    /// <summary>IC(0): L L^T on the lower-triangle pattern of A.</summary>
    public sealed class IncompleteCholesky
    {
        readonly int n;
        readonly int[] ptr, col; // lower triangle rows, columns ascending, diagonal last
        readonly double[] val;

        IncompleteCholesky(int n, int[] ptr, int[] col, double[] val) { this.n = n; this.ptr = ptr; this.col = col; this.val = val; }

        public static IncompleteCholesky TryCreate(Csr a, out string name)
        {
            foreach (var shift in new[] { 0.0, 1e-3, 1e-2, 0.05, 0.2 })
            {
                var f = Factor(a, shift);
                if (f != null) { name = shift == 0 ? "IC(0)" : $"IC(0), diagonal shift {shift}"; return f; }
            }
            name = "Jacobi";
            return null;
        }

        static IncompleteCholesky Factor(Csr a, double shift)
        {
            int n = a.N;
            var ptr = new int[n + 1];
            for (int r = 0; r < n; r++)
            {
                int c = 0;
                for (int k = a.RowPtr[r]; k < a.RowPtr[r + 1]; k++) if (a.Col[k] <= r) c++;
                ptr[r + 1] = ptr[r] + c;
            }
            var col = new int[ptr[n]];
            var val = new double[ptr[n]];
            for (int r = 0; r < n; r++)
            {
                int w = ptr[r];
                for (int k = a.RowPtr[r]; k < a.RowPtr[r + 1]; k++)
                    if (a.Col[k] <= r) { col[w] = a.Col[k]; val[w] = a.Val[k] * (a.Col[k] == r ? 1 + shift : 1); w++; }
            }
            for (int i = 0; i < n; i++)
            {
                int i0 = ptr[i], i1 = ptr[i + 1] - 1; // i1 = diagonal
                if (col[i1] != i) return null;
                for (int p = i0; p < i1; p++)
                {
                    int k = col[p];
                    // L[i,k] = (A[i,k] - sum_{j<k} L[i,j] L[k,j]) / L[k,k]
                    double s = val[p];
                    int pi = i0, pk = ptr[k], k1 = ptr[k + 1] - 1;
                    while (pi < p && pk < k1)
                    {
                        int ci = col[pi], ck = col[pk];
                        if (ci == ck) { s -= val[pi] * val[pk]; pi++; pk++; }
                        else if (ci < ck) pi++; else pk++;
                    }
                    val[p] = s / val[k1];
                }
                double d = val[i1];
                for (int p = i0; p < i1; p++) d -= val[p] * val[p];
                if (!(d > 1e-12 * Math.Abs(val[i1]))) return null;
                val[i1] = Math.Sqrt(d);
            }
            return new IncompleteCholesky(n, ptr, col, val);
        }

        public void Solve(double[] r, double[] z)
        {
            // L y = r
            for (int i = 0; i < n; i++)
            {
                double s = r[i];
                int i1 = ptr[i + 1] - 1;
                for (int p = ptr[i]; p < i1; p++) s -= val[p] * z[col[p]];
                z[i] = s / val[i1];
            }
            // L^T z = y (column sweep)
            for (int i = n - 1; i >= 0; i--)
            {
                int i1 = ptr[i + 1] - 1;
                z[i] /= val[i1];
                double zi = z[i];
                for (int p = ptr[i]; p < i1; p++) z[col[p]] -= val[p] * zi;
            }
        }
    }
}

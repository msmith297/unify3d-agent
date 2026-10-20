using System;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold.Geometry
{
    /// <summary>Small 3D vector helpers (double[3]); no SolidWorks types.</summary>
    public static class V3
    {
        public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        public static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        public static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
        public static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        public static double[] Mul(double[] a, double k) => new[] { a[0] * k, a[1] * k, a[2] * k };
        public static double Len(double[] a) => Math.Sqrt(Dot(a, a));
        public static double Dist(double[] a, double[] b) => Len(Sub(a, b));

        public static double[] Unit(double[] a)
        {
            var l = Len(a);
            if (l < 1e-15) throw new ArgumentException("zero-length vector");
            return new[] { a[0] / l, a[1] / l, a[2] / l };
        }
    }

    /// <summary>
    /// The mold frame: W is the press and pull axis (+W toward the lid), U and V lie in the parting plane, right-handed
    /// (U x V = W). Coordinates are model meters unless a method says otherwise. Built from the Top Plane's normal (the
    /// 2nd RefPlane feature, never found by its language-dependent name), which is always a model axis; a pull direction
    /// given by the agent may be any direction.
    /// </summary>
    public sealed class MoldFrame
    {
        public MoldFrame(double[] origin, double[] u, double[] v, double[] w)
        {
            Origin = origin ?? new double[3];
            U = V3.Unit(u);
            V = V3.Unit(v);
            W = V3.Unit(w);
        }

        public double[] Origin { get; }
        public double[] U { get; }
        public double[] V { get; }
        public double[] W { get; }

        /// <summary>
        /// A frame whose W is the given direction. When W is (within 1e-9) a model axis, U and V are model axes too (U is
        /// the first model axis other than W's), so per-axis factors map one-to-one onto model X/Y/Z.
        /// </summary>
        public static MoldFrame FromNormal(double[] w, double[] origin = null)
        {
            w = V3.Unit(w);
            var k = AxisIndex(w);
            double[] u;
            if (k >= 0)
            {
                u = new double[3];
                u[k == 0 ? 1 : 0] = 1;
            }
            else
            {
                // Not axis-aligned: U = the model axis least parallel to W, made perpendicular.
                var ax = Math.Abs(w[0]) <= Math.Abs(w[1]) && Math.Abs(w[0]) <= Math.Abs(w[2]) ? 0 : Math.Abs(w[1]) <= Math.Abs(w[2]) ? 1 : 2;
                var e = new double[3];
                e[ax] = 1;
                u = V3.Unit(V3.Sub(e, V3.Mul(w, V3.Dot(e, w))));
            }
            var v = V3.Cross(w, u);
            return new MoldFrame(origin ?? new double[3], u, v, w);
        }

        /// <summary>The model axis (0, 1, 2) a unit vector lies along, or -1.</summary>
        public static int AxisIndex(double[] d)
        {
            for (var i = 0; i < 3; i++) if (Math.Abs(Math.Abs(d[i]) - 1) < 1e-9) return i;
            return -1;
        }

        /// <summary>True when U, V and W are all model axes (so a Scale feature's X/Y/Z factors can carry per-axis shrink).</summary>
        public bool AxisAligned => AxisIndex(U) >= 0 && AxisIndex(V) >= 0 && AxisIndex(W) >= 0;

        /// <summary>Model point -> (u, v, w).</summary>
        public double[] ToFrame(double[] p)
        {
            var d = V3.Sub(p, Origin);
            return new[] { V3.Dot(d, U), V3.Dot(d, V), V3.Dot(d, W) };
        }

        public double[] ToModel(double u, double v, double w) =>
            new[] { Origin[0] + U[0] * u + V[0] * v + W[0] * w, Origin[1] + U[1] * u + V[1] * v + W[1] * w, Origin[2] + U[2] * u + V[2] * v + W[2] * w };

        public double[] ToModel(double[] uvw) => ToModel(uvw[0], uvw[1], uvw[2]);

        public double[] DirToFrame(double[] d) => new[] { V3.Dot(d, U), V3.Dot(d, V), V3.Dot(d, W) };

        public double[] DirToModel(double[] d) => V3.Add(V3.Add(V3.Mul(U, d[0]), V3.Mul(V, d[1])), V3.Mul(W, d[2]));

        /// <summary>The same axes with another origin.</summary>
        public MoldFrame At(double[] origin) => new MoldFrame(origin, U, V, W);

        /// <summary>
        /// Per-axis shrink factors along U, V, W as model X, Y, Z factors (for a Scale feature about a coordinate system
        /// whose axes are the model axes). Only for axis-aligned frames; a uniform factor works for any frame.
        /// </summary>
        public double[] ModelScaleFactors(double su, double sv, double sw)
        {
            if (Math.Abs(su - sv) < 1e-12 && Math.Abs(sv - sw) < 1e-12) return new[] { su, su, su };
            if (!AxisAligned) throw new InvalidOperationException("Per-axis shrink needs the mold frame to lie along the model axes.");
            var s = new double[3];
            s[AxisIndex(U)] = su;
            s[AxisIndex(V)] = sv;
            s[AxisIndex(W)] = sw;
            return s;
        }

        public JObject ToJson(double scale = 1000) => new JObject
        {
            ["origin"] = new JArray(Math.Round(Origin[0] * scale, 4), Math.Round(Origin[1] * scale, 4), Math.Round(Origin[2] * scale, 4)),
            ["u"] = new JArray(Math.Round(U[0], 9), Math.Round(U[1], 9), Math.Round(U[2], 9)),
            ["v"] = new JArray(Math.Round(V[0], 9), Math.Round(V[1], 9), Math.Round(V[2], 9)),
            ["w"] = new JArray(Math.Round(W[0], 9), Math.Round(W[1], 9), Math.Round(W[2], 9)),
        };

        public override string ToString() =>
            $"U ({U[0]:0.###}, {U[1]:0.###}, {U[2]:0.###}), V ({V[0]:0.###}, {V[1]:0.###}, {V[2]:0.###}), W ({W[0]:0.###}, {W[1]:0.###}, {W[2]:0.###}), origin ({Origin[0] * 1000:0.###}, {Origin[1] * 1000:0.###}, {Origin[2] * 1000:0.###}) mm";
    }
}

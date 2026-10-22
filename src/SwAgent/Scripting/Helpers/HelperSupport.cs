using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SwAgent.Mold.Geometry;

namespace SwAgent.Scripting.Helpers
{
    /// <summary>
    /// Marks the documented overload of a built-in helper: its summary (one sentence) and the raw API members it replaces.
    /// HelperCatalog reads these, so the prompt index and the compile advisor always match the code.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property)]
    public sealed class HelperAttribute : System.Attribute
    {
        public HelperAttribute(string summary, params string[] wraps)
        {
            Summary = summary;
            Wraps = wraps ?? new string[0];
        }

        public string Summary { get; }
        public string[] Wraps { get; }
    }

    /// <summary>A built-in helper couldn't do its job; the message says what it measured and what to try.</summary>
    public sealed class SwHelperException : Exception
    {
        public SwHelperException(string message) : base(message)
        {
        }
    }

    /// <summary>A body's identity and shape before an operation: volume, centroid and its extent along six directions.</summary>
    internal sealed class BodyShape
    {
        public string Name;
        public double V;
        public double[] C;
        public double[] S; // support along RotationMath.Dirs
        public (double V, double X, double Y, double Z) Sig;

        public static BodyShape Take(IBody2 b, bool supports)
        {
            var s = new BodyShape { Name = SwGeo.SafeName(b), Sig = SwGeo.Signature(b) };
            s.V = s.Sig.V;
            s.C = new[] { s.Sig.X, s.Sig.Y, s.Sig.Z };
            if (supports) s.S = RotationMath.Dirs.Select(d => RotationMath.Support(b, d)).ToArray();
            return s;
        }
    }

    /// <summary>Rotation matrices and the measured check of a turn [pure math, except Support].</summary>
    internal static class RotationMath
    {
        /// <summary>Six generic directions (no symmetry with the model axes), used to compare a body's extent before and after a turn.</summary>
        public static readonly double[][] Dirs = new[] { new[] { 1, 0.3, 0.1 }, new[] { 0.2, 1, 0.5 }, new[] { 0.4, 0.1, 1 } }
            .Select(V3.Unit).SelectMany(d => new[] { d, V3.Mul(d, -1) }).ToArray();

        /// <summary>Right-hand rotation about model axis k (0 X, 1 Y, 2 Z) by angle (radians).</summary>
        public static double[,] Axis(int k, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            switch (k)
            {
                case 0: return new[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } };
                case 1: return new[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } };
                default: return new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } };
            }
        }

        /// <summary>Right-hand rotation about a unit axis by angle (Rodrigues).</summary>
        public static double[,] About(double[] axis, double angle)
        {
            var a = V3.Unit(axis);
            double c = Math.Cos(angle), s = Math.Sin(angle), t = 1 - c;
            double x = a[0], y = a[1], z = a[2];
            return new[,]
            {
                { t * x * x + c, t * x * y - s * z, t * x * z + s * y },
                { t * x * y + s * z, t * y * y + c, t * y * z - s * x },
                { t * x * z - s * y, t * y * z + s * x, t * z * z + c },
            };
        }

        public static double[] Apply(double[,] r, double[] p) => new[]
        {
            r[0, 0] * p[0] + r[0, 1] * p[1] + r[0, 2] * p[2],
            r[1, 0] * p[0] + r[1, 1] * p[1] + r[1, 2] * p[2],
            r[2, 0] * p[0] + r[2, 1] * p[1] + r[2, 2] * p[2],
        };

        public static double[,] Mul(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
            return r;
        }

        /// <summary>A point turned by r about centre c.</summary>
        public static double[] Turn(double[,] r, double[] c, double[] p) => V3.Add(c, Apply(r, V3.Sub(p, c)));

        /// <summary>
        /// Fixed-axis angles (radians) with r = Rz(z) * Ry(y) * Rx(x): turning about model X by x, then Y by y, then Z by z
        /// gives r. Near gimbal lock (|y| = 90 deg) x is 0.
        /// </summary>
        public static (double X, double Y, double Z) EulerXyz(double[,] r)
        {
            var sy = -r[2, 0];
            if (sy > 1) sy = 1;
            if (sy < -1) sy = -1;
            var y = Math.Asin(sy);
            if (Math.Abs(sy) < 1 - 1e-9) return (Math.Atan2(r[2, 1], r[2, 2]), y, Math.Atan2(r[1, 0], r[0, 0]));
            return (0, y, Math.Atan2(-r[0, 1], r[1, 1]));
        }

        /// <summary>
        /// Which InsertMoveCopyBody2 rotation slot (0 RotAngleX, 1 Y, 2 Z) and value to try next for a turn about model
        /// axis k by angle: a slot known (measured earlier) to turn about k, with its sign; else an untried slot, k's own
        /// first; null when every slot was tried. `map` holds what each measured slot did: value v in slot s turns about
        /// model axis map[s].Axis by map[s].Sign * v.
        /// </summary>
        public static (int Slot, double Value)? NextSlot(int k, double angle, IReadOnlyDictionary<int, (int Axis, int Sign)> map, ICollection<int> tried)
        {
            foreach (var kv in map.OrderBy(x => x.Key))
                if (kv.Value.Axis == k && !tried.Contains(kv.Key)) return (kv.Key, angle * kv.Value.Sign);
            foreach (var s in new[] { k, (k + 1) % 3, (k + 2) % 3 })
                if (!tried.Contains(s) && !map.ContainsKey(s)) return (s, angle);
            return null;
        }

        /// <summary>The model axis a unit vector lies along (0, 1, 2), or -1.</summary>
        public static int AxisIndex(double[] v)
        {
            var u = V3.Unit(v);
            for (var k = 0; k < 3; k++)
                if (Math.Abs(Math.Abs(u[k]) - 1) < 1e-9) return k;
            return -1;
        }

        /// <summary>"X"/"Y"/"Z" (or "-X"...) as a unit vector.</summary>
        public static double[] AxisVector(string axis)
        {
            var s = (axis ?? "").Trim().ToUpperInvariant();
            var sign = 1.0;
            if (s.StartsWith("-", StringComparison.Ordinal))
            {
                sign = -1;
                s = s.Substring(1);
            }
            else if (s.StartsWith("+", StringComparison.Ordinal)) s = s.Substring(1);
            var k = s == "X" ? 0 : s == "Y" ? 1 : s == "Z" ? 2 : -1;
            if (k < 0) throw new SwHelperException($"The axis must be \"X\", \"Y\" or \"Z\" (or a vector like new[]{{0,0,1}}), not \"{axis}\".");
            var v = new double[3];
            v[k] = sign;
            return v;
        }

        /// <summary>The body's extent along a unit direction: max of p . d over the body (its extreme point).</summary>
        public static double Support(IBody2 b, double[] d)
        {
            if (!b.GetExtremePoint(d[0], d[1], d[2], out var x, out var y, out var z))
                throw new SwHelperException($"Couldn't measure body '{SwGeo.SafeName(b)}' (GetExtremePoint failed).");
            return x * d[0] + y * d[1] + z * d[2];
        }

        /// <summary>
        /// The extent along d of a body turned by r about c, from its extents before: support(R d) after = support(d) - c.d + c.(R d).
        /// </summary>
        public static double TurnedSupport(double before, double[,] r, double[] c, double[] d) => before - V3.Dot(c, d) + V3.Dot(c, Apply(r, d));

        public static readonly double[,] Identity = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        /// <summary>Largest entry difference of two rotation matrices.</summary>
        public static double Diff(double[,] a, double[,] b)
        {
            var d = 0.0;
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                d = Math.Max(d, Math.Abs(a[i, j] - b[i, j]));
            return d;
        }

        /// <summary>Match with SolidWorks' own measurements of the bodies after (volume, centroid, extreme points).</summary>
        public static (double Error, int[] Pairing) Match(IList<BodyShape> before, IList<IBody2> after, double[,] r, double[] c, double[] t = null) =>
            Match<IBody2>(before, after, r, c, SwGeo.Volume, SwGeo.Centroid, Support, t);

        /// <summary>
        /// How far the bodies after a turn are from the bodies before turned by r about c and then moved by t (meters;
        /// infinity if the counts or volumes don't match). Each body before is paired with the unused body after nearest its
        /// expected centroid, then its six extents are compared - a turn about an axis through the centroid moves no
        /// centroid, the extents tell the direction. Returns the pairing (after index per before index).
        /// </summary>
        public static (double Error, int[] Pairing) Match<TBody>(IList<BodyShape> before, IList<TBody> after, double[,] r, double[] c, Func<TBody, double> volume, Func<TBody, double[]> centroid, Func<TBody, double[], double> support, double[] t = null)
        {
            t = t ?? new double[3];
            if (after.Count != before.Count) return (double.PositiveInfinity, null);
            var vols = after.Select(volume).ToArray();
            var cents = after.Select(centroid).ToArray();
            var used = new bool[after.Count];
            var pairing = new int[before.Count];
            var worst = 0.0;
            for (var i = 0; i < before.Count; i++)
            {
                var want = V3.Add(Turn(r, c, before[i].C), t);
                var best = -1;
                var bestD = double.PositiveInfinity;
                for (var j = 0; j < after.Count; j++)
                {
                    if (used[j] || cents[j] == null || !(Math.Abs(vols[j] / before[i].V - 1) < 1e-4)) continue;
                    var dist = V3.Dist(cents[j], want);
                    if (dist < bestD)
                    {
                        bestD = dist;
                        best = j;
                    }
                }
                if (best < 0) return (double.PositiveInfinity, null);
                used[best] = true;
                pairing[i] = best;
                worst = Math.Max(worst, bestD);
            }
            if (worst > 1e-4) return (worst, pairing); // positions already off: no need to measure extents
            for (var i = 0; i < before.Count; i++)
            {
                if (before[i].S == null) continue;
                for (var k = 0; k < Dirs.Length; k++)
                {
                    var rd = Apply(r, Dirs[k]);
                    var err = Math.Abs(support(after[pairing[i]], rd) - TurnedSupport(before[i].S[k], r, c, Dirs[k]) - V3.Dot(t, rd));
                    worst = Math.Max(worst, err);
                }
            }
            return (worst, pairing);
        }
    }
    /// <summary>Shared steps of the helpers [SolidWorks]: the part check, body bookkeeping across features, selection marks.</summary>
    internal static class HelperOps
    {
        public static readonly double[] Zero = new double[3];

        public static IModelDoc2 RequirePart(IModelDoc2 model, string what)
        {
            if (model == null) throw new SwHelperException($"{what}: no document is open for this chat.");
            if (!(model is IPartDoc) || model.GetType() != (int)SolidWorks.Interop.swconst.swDocumentTypes_e.swDocPART)
                throw new SwHelperException($"{what} works on the bodies of a part document; this is not a part.");
            return model;
        }

        /// <summary>Non-null bodies, each once (by name).</summary>
        public static List<IBody2> Distinct(IEnumerable<IBody2> bodies, string what)
        {
            var list = (bodies ?? Enumerable.Empty<IBody2>()).Where(b => b != null).GroupBy(SwGeo.SafeName).Select(g => g.First()).ToList();
            if (list.Count == 0) throw new SwHelperException($"{what}: no bodies given (null or an empty list).");
            return list;
        }

        /// <summary>Signatures of the solids not named in `involved` (they must come through an operation unchanged).</summary>
        public static List<(double V, double X, double Y, double Z)> Others(IModelDoc2 model, IEnumerable<IBody2> involved)
        {
            var names = new HashSet<string>(involved.Select(SwGeo.SafeName), StringComparer.Ordinal);
            return SwGeo.Solids(model).Where(b => !names.Contains(SwGeo.SafeName(b))).Select(SwGeo.Signature).ToList();
        }

        /// <summary>
        /// The bodies again after an undone try (deleting a feature rebuilds the part and old references can go stale),
        /// matched by volume and centroid, in the order of `shapes`.
        /// </summary>
        public static List<IBody2> Refind(IModelDoc2 model, IList<BodyShape> shapes, string what)
        {
            var pool = SwGeo.Solids(model);
            var result = new List<IBody2>();
            foreach (var s in shapes)
            {
                var k = pool.FindIndex(b => SwGeo.SameSignature(SwGeo.Signature(b), s.Sig));
                if (k < 0) throw new SwHelperException($"{what}: body '{s.Name}' wasn't found again after undoing a try - check the feature tree for a left-over feature.");
                result.Add(pool[k]);
                pool.RemoveAt(k);
            }
            return result;
        }

        /// <summary>Selection marks to try: the one that worked last first, then the documented ones.</summary>
        public static IEnumerable<int> Marks(int? learned, params int[] documented) =>
            (learned.HasValue ? new[] { learned.Value } : new int[0]).Concat(documented).Distinct();

        /// <summary>Select bodies with a mark (append); throws when one can't be selected.</summary>
        public static void SelectBodies(IModelDoc2 model, IEnumerable<IBody2> bodies, int mark, string what)
        {
            var data = ((ISelectionMgr)model.SelectionManager).CreateSelectData();
            data.Mark = mark;
            foreach (var b in bodies)
                if (!b.Select2(true, data)) throw new SwHelperException($"{what}: couldn't select body '{SwGeo.SafeName(b)}' (consumed by a later feature? get it again).");
        }

        /// <summary>A feature's rebuild error (not a warning), or null.</summary>
        public static string Error(IFeature f)
        {
            try
            {
                var code = f.GetErrorCode2(out var warning);
                return code != 0 && !warning ? ((SolidWorks.Interop.swconst.swFeatureError_e)code).ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        public static double Size(IEnumerable<BodyShape> shapes) =>
            shapes.Select(s => s.S == null ? 0 : s.S.Take(2).Sum()).DefaultIfEmpty(0).Max();

        public static string Mm(double m) => (m * 1000).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}

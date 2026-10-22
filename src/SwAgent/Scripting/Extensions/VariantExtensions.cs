using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SwAgent.Scripting.Helpers;

namespace SwAgent.Scripting.Extensions
{
    /// <summary>
    /// SolidWorks returns arrays as VARIANTs typed <c>object</c>: sometimes double[], sometimes object[] of boxed numbers,
    /// sometimes a typed data object (CurveParamData). These turn any of them into the array a cell wants, null-safe:
    /// <c>face.Normal.AsDoubles()</c>, <c>body.GetMassProperties(1).AsDoubles()[3]</c>,
    /// <c>part.GetBodies2(0, false).AsObjects&lt;IBody2&gt;()</c>.
    /// </summary>
    public static class VariantExtensions
    {
        /// <summary>
        /// Numbers as double[]: double[], float[], int[], object[] of boxed numbers, a single number, IMathPoint /
        /// IMathVector / IMathTransform (ArrayData), CurveParamData ([start xyz, end xyz, uMin, uMax] - the first eight
        /// values of GetCurveParams2). Null gives an empty array.
        /// </summary>
        [Helper("Any SolidWorks number array (object, object[] of boxed numbers, IMathPoint, CurveParamData) as double[]; null gives [].")]
        public static double[] AsDoubles(this object value)
        {
            switch (value)
            {
                case null:
                    return new double[0];
                case double[] d:
                    return d;
                case string s:
                    throw new InvalidCastException($"AsDoubles: \"{Short(s)}\" is text, not a number array.");
            }
            // SolidWorks objects first: an RCW can claim IEnumerable through IDispatch.
            if (IsCom(value))
            {
                if (value is IMathPoint p) return p.ArrayData.AsDoubles();
                if (value is IMathVector v) return v.ArrayData.AsDoubles();
                if (value is IMathTransform t) return t.ArrayData.AsDoubles();
                if (value is ICurveParamData c)
                    return c.StartPoint.AsDoubles().Take(3).Concat(c.EndPoint.AsDoubles().Take(3)).Concat(new[] { c.UMinValue, c.UMaxValue }).ToArray();
                throw new InvalidCastException($"AsDoubles: a {TypeName(value)} isn't a number array.");
            }
            switch (value)
            {
                case IEnumerable items:
                    return items.Cast<object>().Select((x, i) => ToDouble(x, i)).ToArray();
                case IConvertible single:
                    return new[] { ToDouble(single, 0) };
            }
            throw new InvalidCastException($"AsDoubles: a {TypeName(value)} isn't a number array.");
        }

        private static bool IsCom(object x) => x != null && System.Runtime.InteropServices.Marshal.IsComObject(x);

        /// <summary>Whole numbers as int[]: int[], short[], object[] of boxed integers, or doubles that are whole. Null gives an empty array.</summary>
        [Helper("Any SolidWorks integer array as int[]; null gives [].")]
        public static int[] AsInts(this object value)
        {
            switch (value)
            {
                case null:
                    return new int[0];
                case int[] i:
                    return i;
                case string s:
                    throw new InvalidCastException($"AsInts: \"{Short(s)}\" is text, not a number array.");
                case IEnumerable items when !IsCom(value):
                    return items.Cast<object>().Select((x, k) => ToInt(x, k)).ToArray();
                case IConvertible single:
                    return new[] { ToInt(single, 0) };
            }
            throw new InvalidCastException($"AsInts: a {TypeName(value)} isn't a number array.");
        }

        /// <summary>
        /// SolidWorks objects as T[] (e.g. <c>GetBodies2(...).AsObjects&lt;IBody2&gt;()</c>): object[] or a single object.
        /// Null and null items are skipped; an item that isn't a T throws (say AsObjects&lt;object&gt;() for mixed lists).
        /// </summary>
        [Helper("A SolidWorks object array as T[] (e.g. GetBodies2(...).AsObjects<IBody2>()); null gives [].")]
        public static T[] AsObjects<T>(this object value) where T : class
        {
            switch (value)
            {
                case null:
                    return new T[0];
                case T[] typed:
                    return typed.Where(x => x != null).ToArray();
                case T single when !(value is string) || typeof(T) == typeof(string):
                    return new[] { single };
                case IEnumerable items when !(value is string) && !IsCom(value):
                    return items.Cast<object>().Where(x => x != null).Select((x, i) => x as T ?? throw new InvalidCastException(
                        $"AsObjects<{typeof(T).Name}>: item {i} is a {TypeName(x)}, not a {typeof(T).Name}. Use AsObjects<object>() and test each item.")).ToArray();
            }
            throw new InvalidCastException($"AsObjects<{typeof(T).Name}>: a {TypeName(value)} isn't a {typeof(T).Name} or an array of them.");
        }

        /// <summary>Text items as string[] (object[] of strings, string[]); null gives an empty array.</summary>
        [Helper("A SolidWorks text array as string[]; null gives [].")]
        public static string[] AsStrings(this object value)
        {
            switch (value)
            {
                case null:
                    return new string[0];
                case string[] s:
                    return s;
                case string one:
                    return new[] { one };
                case IEnumerable items when !IsCom(value):
                    return items.Cast<object>().Select(x => x == null ? null : Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray();
            }
            return new[] { Convert.ToString(value, CultureInfo.InvariantCulture) };
        }

        private static double ToDouble(object x, int index)
        {
            if (x is IConvertible c && !(x is string))
            {
                try
                {
                    return c.ToDouble(CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
                {
                }
            }
            throw new InvalidCastException($"AsDoubles: item {index} is a {TypeName(x)}, not a number.");
        }

        private static int ToInt(object x, int index)
        {
            if (x is IConvertible c && !(x is string))
            {
                double d;
                try
                {
                    d = c.ToDouble(CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
                {
                    d = double.NaN;
                }
                if (!double.IsNaN(d) && Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) <= int.MaxValue) return (int)Math.Round(d);
                if (!double.IsNaN(d)) throw new InvalidCastException($"AsInts: item {index} is {d.ToString(CultureInfo.InvariantCulture)}, not a whole number (use AsDoubles()).");
            }
            throw new InvalidCastException($"AsInts: item {index} is a {TypeName(x)}, not a number.");
        }

        private static string Short(string s) => s.Length > 30 ? s.Substring(0, 30) + "..." : s;

        internal static string TypeName(object x)
        {
            if (x == null) return "null";
            if (System.Runtime.InteropServices.Marshal.IsComObject(x))
            {
                try
                {
                    return Microsoft.VisualBasic.Information.TypeName(x);
                }
                catch
                {
                    return "COM object";
                }
            }
            return x.GetType().Name;
        }
    }
}

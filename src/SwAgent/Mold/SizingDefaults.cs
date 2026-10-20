using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>The compiled copy of data/sizing-defaults.json (generated with it by tools/dev/mold-data/gen_defaults.py).</summary>
    internal static partial class SizingDefaults
    {
        public static JObject Compiled() => Si.ParseObject(BuiltinJson);
    }

    /// <summary>
    /// Read access to the sizing defaults with one call's overrides on top. An override key that names a defaults path
    /// ("sfStatic", "bolts.tighteningScatter") replaces that value for this call only; other override keys are the
    /// call's own parameters (support case, rail span, ...) and are read with <see cref="Param"/>.
    /// </summary>
    public sealed class Cfg
    {
        private readonly JObject _d;
        private readonly JObject _over;

        public Cfg(JObject defaults, JObject overrides = null)
        {
            _d = (JObject)(defaults ?? SizingDefaults.Compiled()).DeepClone();
            _over = overrides ?? new JObject();
            foreach (var p in _over.Properties())
            {
                var existing = _d.SelectToken(p.Name);
                if (existing != null && existing.Parent is JProperty jp) jp.Value = p.Value.DeepClone();
            }
        }

        public JObject Json => _d;

        public JToken T(string path) => _d.SelectToken(path);

        public double D(string path, double fallback)
        {
            var t = _d.SelectToken(path);
            return Si.Num(t) ?? fallback;
        }

        public double[] Arr(string path)
        {
            var t = _d.SelectToken(path) as JArray;
            return t?.Select(x => Si.Num(x) ?? double.NaN).ToArray() ?? new double[0];
        }

        /// <summary>A per-call parameter from the overrides (e.g. "support", "rail_span_mm"); null when not given.</summary>
        public JToken Param(string key) => _over[key];

        public double? ParamNum(string key) => Si.Num(_over[key]);

        public string ParamStr(string key) => _over[key]?.Type == JTokenType.String ? (string)_over[key] : _over[key]?.ToString();

        /// <summary>Linear interpolation in a table of x (ascending) and y, clamped at both ends.</summary>
        public static double Interp(double[] xs, double[] ys, double x)
        {
            if (xs.Length == 0) return double.NaN;
            if (x <= xs[0]) return ys[0];
            for (var i = 0; i < xs.Length - 1; i++)
                if (x <= xs[i + 1]) return ys[i] + (ys[i + 1] - ys[i]) * (x - xs[i]) / (xs[i + 1] - xs[i]);
            return ys[xs.Length - 1];
        }
    }
}

using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>
    /// Unit factors and a lenient number reader. Inside the calculator everything is N, mm, MPa (N/mm²), degC, K, kg, kN
    /// for forces in reports, cP and min (design section 3). Values arrive from the page already in SI-like units, but the
    /// agent may hand-write a design with "2000 psi" or {"value": 2, "unit": "in"}, so numbers with a known unit are
    /// converted here instead of being misread.
    /// </summary>
    public static class Si
    {
        public const double PsiToMPa = 0.006894757293168;
        public const double KsiToMPa = 6.894757293168;
        public const double InToMm = 25.4;
        public const double LbfToN = 4.4482216152605;
        public const double UsTonToKN = 8.896443230521;
        public const double TonneForceToKN = 9.80665;
        public const double LbToKg = 0.45359237;
        public const double FtLbfToNm = 1.3558179483314004;
        public const double BarToMPa = 0.1;
        public const double KgfCm2ToMPa = 0.0980665;
        public const double In3ToMm3 = 16387.064;

        public static double FToC(double f) => (f - 32.0) / 1.8;
        public static double CToF(double c) => c * 1.8 + 32.0;

        /// <summary>Converts a value in `unit` to the calculator's unit for that quantity; unknown units pass through.</summary>
        public static double ToCalc(double v, string unit)
        {
            switch ((unit ?? "").Trim().ToLowerInvariant())
            {
                case "psi": return v * PsiToMPa;
                case "ksi": return v * KsiToMPa;
                case "bar": return v * BarToMPa;
                case "kgf/cm2": case "kgf/cm²": case "kg/cm2": return v * KgfCm2ToMPa;
                case "pa": return v / 1e6;
                case "kpa": return v / 1e3;
                case "in": case "inch": case "\"": return v * InToMm;
                case "m": return v * 1000.0;
                case "cm": return v * 10.0;
                case "lb": case "lbs": return v * LbToKg;
                case "g": return v / 1000.0;
                case "lbf": return v * LbfToN / 1000.0;
                case "n": return v / 1000.0;
                case "us ton": case "ton": case "tons": case "short ton": return v * UsTonToKN;
                case "t": case "tf": case "tonne": case "tonnes": return v * TonneForceToKN;
                case "degf": case "°f": case "f": return FToC(v);
                case "k": return v - 273.15;
                case "pa*s": case "pa·s": case "pas": return v * 1000.0;
                case "s": return v / 60.0;
                case "h": return v * 60.0;
                case "rad": return v * 180.0 / Math.PI;
                default: return v; // mm, MPa, degC, kg, kN, cP, min, deg, %, none
            }
        }

        /// <summary>
        /// Reads a number in calculator units from a token that may be a number, a numeric string ("2000 psi"), or an
        /// object {value, unit} / {si, value?, display?}. `quantity` (pressure, length, length_cm, area, force, time,
        /// viscosity, angle; temperature, mass, percent and count are the same in SI and calculator units) says what an
        /// "si" value measures: whenever "si" is present it is converted by the quantity - also when the object carries a
        /// display `value` next to it, as Track B's payload entries do ({"value": "2000 psi (13.79 MPa)", "si": 13789514.6}).
        /// Without a quantity an "si" object is read from its value and unit (or the value text's own unit) instead, and the
        /// raw si number is only taken for a dimensionless value. Returns null for null/missing/"not set".
        /// </summary>
        public static double? Num(JToken t, string quantity)
        {
            if (t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined) return null;
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return (double)t;
                case JTokenType.Boolean:
                    return null;
                case JTokenType.String:
                    return ParseWithUnit((string)t, out _);
                case JTokenType.Object:
                    var o = (JObject)t;
                    var siTok = o["si"];
                    if (siTok != null && siTok.Type != JTokenType.Null)
                    {
                        var si = Num(siTok, null);
                        if (si == null) return null;
                        if (quantity != null) return FromSi(si.Value, quantity);
                        // no quantity: trust a value that says its unit, never the bare SI number of a dimensional value
                        var val = o["value"];
                        if (val != null && (val.Type == JTokenType.Integer || val.Type == JTokenType.Float) && o["unit"] != null)
                            return ToCalc((double)val, (string)o["unit"]);
                        if (val != null && val.Type == JTokenType.String && ParseWithUnit((string)val, out var known) is double pv && known)
                            return pv;
                        var disp = o["display"];
                        if (disp != null && disp.Type == JTokenType.String && ParseWithUnit((string)disp, out var known2) is double dv && known2)
                            return dv;
                        return si;
                    }
                    var inner = o["value"];
                    if (inner == null || inner.Type == JTokenType.Null) return null;
                    var n = Num(inner, null);
                    return n == null ? (double?)null : ToCalc(n.Value, (string)o["unit"]);
                default:
                    return null;
            }
        }

        public static double? Num(JToken t) => Num(t, null);

        /// <summary>A true SI number (Pa, m, m², N, s, Pa·s, rad; degC, kg, % unchanged) in the calculator unit of `quantity`.</summary>
        public static double FromSi(double si, string quantity)
        {
            switch (quantity)
            {
                case "pressure": return si / 1e6;
                case "length": return si * 1000.0;
                case "length_cm": return si * 100.0;
                case "area": return si * 1e6;
                case "force": return si / 1000.0;
                case "time": return si / 60.0;
                case "viscosity": return si * 1000.0;
                case "angle": return si * 180.0 / Math.PI;
                default: return si;   // temperature (degC), mass (kg), percent, count, dimensionless
            }
        }

        private static readonly string[] KnownUnits =
        {
            "psi", "ksi", "bar", "kgf/cm2", "kgf/cm²", "kg/cm2", "pa", "kpa", "mpa", "in", "inch", "\"", "m", "cm", "mm", "lb", "lbs",
            "g", "kg", "lbf", "n", "kn", "us ton", "ton", "tons", "short ton", "t", "tf", "tonne", "tonnes", "degf", "°f", "f", "degc",
            "°c", "c", "k", "pa*s", "pa·s", "pas", "cp", "s", "min", "h", "rad", "deg", "°", "%",
        };

        private static double? ParseWithUnit(string s, out bool knownUnit)
        {
            knownUnit = false;
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            var i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-' || s[i] == '+' || s[i] == 'e' || s[i] == 'E' || s[i] == ','))
            {
                // stop at an 'e' that starts a word (e.g. "5 each") - only accept it inside a number
                if ((s[i] == 'e' || s[i] == 'E') && (i == 0 || !char.IsDigit(s[i - 1]))) break;
                i++;
            }
            if (i == 0) return null;
            if (!double.TryParse(s.Substring(0, i).Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            // the unit is what follows the number up to a bracket: "2000 psi (13.79 MPa)" -> psi
            var unit = s.Substring(i);
            var br = unit.IndexOfAny(new[] { '(', '[' });
            if (br >= 0) unit = unit.Substring(0, br);
            unit = unit.Trim();
            if (unit.Length == 0) return v;
            knownUnit = Array.IndexOf(KnownUnits, unit.ToLowerInvariant()) >= 0;
            return ToCalc(v, unit);
        }

        /// <summary>The text after the leading number ("24 in" -> "in"; "24" -> "").</summary>
        private static string UnitText(string s)
        {
            s = (s ?? "").Trim();
            var i = 0;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-' || s[i] == '+' || s[i] == ',' || s[i] == 'e' || s[i] == 'E')) i++;
            return s.Substring(i).Trim();
        }

        /// <summary>Reads a length array (e.g. [610, 610] mm, or {"value":[24,24],"unit":"in"}) in mm; null when absent.</summary>
        public static double[] Lengths(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            string unit = null;
            if (t is JObject o)
            {
                unit = (string)o["unit"];
                t = o["si"] ?? o["value"];
                if (o["si"] != null) unit = "m";
                if (t == null) return null;
            }
            if (t is JArray a)
            {
                var r = new double[a.Count];
                for (var i = 0; i < a.Count; i++)
                {
                    var n = Num(a[i]);
                    if (n == null) return null;
                    r[i] = unit == null ? n.Value : ToCalc(n.Value, unit);
                }
                return r;
            }
            if (t.Type == JTokenType.String)
            {
                var text = (string)t;
                var br = text.IndexOfAny(new[] { '(', '[' });   // "24 x 24 in (610 x 610 mm)" -> the first reading
                if (br > 0) text = text.Substring(0, br);
                var parts = text.Split(new[] { 'x', 'X', ',', '×' }, StringSplitOptions.RemoveEmptyEntries);
                var r = new double[parts.Length];
                // "24 x 24 in": a unit written once at the end applies to every number
                var lastUnit = parts.Length > 0 ? UnitText(parts[parts.Length - 1]) : "";
                for (var i = 0; i < parts.Length; i++)
                {
                    var p = parts[i].Trim();
                    if (UnitText(p).Length == 0 && lastUnit.Length > 0) p += " " + lastUnit;
                    var n = ParseWithUnit(p, out _);
                    if (n == null) return null;
                    r[i] = n.Value;
                }
                return r;
            }
            return null;
        }

        /// <summary>
        /// Parses a JSON object keeping date-like strings as strings ("2026-10-19", "createdAt"), so a record or data file
        /// reads back exactly as it was written.
        /// </summary>
        public static JObject ParseObject(string text)
        {
            using (var sr = new System.IO.StringReader(text))
            using (var jr = new Newtonsoft.Json.JsonTextReader(sr) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None, FloatParseHandling = Newtonsoft.Json.FloatParseHandling.Double })
            {
                var t = JToken.ReadFrom(jr);
                return t as JObject ?? throw new Newtonsoft.Json.JsonException("Expected a JSON object.");
            }
        }

        /// <summary>Rounds for reports (never used inside the calculation).</summary>
        public static double R(double v, int digits = 3)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return v;
            if (v == 0) return 0;
            var mag = (int)Math.Floor(Math.Log10(Math.Abs(v)));
            var dec = Math.Max(0, Math.Min(10, digits - 1 - mag));
            return Math.Round(v, dec, MidpointRounding.AwayFromZero);
        }

        public static string F(double v, int digits = 3) => R(v, digits).ToString("0.##########", CultureInfo.InvariantCulture);
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.Tools
{
    /// <summary>
    /// One unit a value can be shown or stored in. display -> SI: si = v * ToSi + Offset; SI -> display: v = (si - Offset) / ToSi.
    /// "SI" is what ToolInputs.Number returns: m, rad, %, Pa, degC, kg, N, Pa*s, s (temperature stays in degC on purpose:
    /// an offset scale in K would invite mistakes).
    /// </summary>
    internal sealed class UnitDef
    {
        public string Quantity;

        /// <summary>Shown on the page ("°F", "Pa·s").</summary>
        public string Symbol;

        /// <summary>Written for the agent and in files ("degF", "Pa*s").</summary>
        public string Ascii;

        public double ToSi = 1;
        public double Offset;

        /// <summary>Number-box increments in this unit: arrows, Page Up/Down (fast), Alt+arrows (slow).</summary>
        public double Inc = 1, Fast = 10, Slow = 0.1;

        public string[] Aliases = new string[0];

        public double ToSiValue(double v) => v * ToSi + Offset;
        public double FromSi(double si) => (si - Offset) / ToSi;

        public bool Matches(string symbol)
        {
            var s = Units.Key(symbol);
            return s == Units.Key(Symbol) || s == Units.Key(Ascii) || Aliases.Any(a => Units.Key(a) == s);
        }

        public override string ToString() => Ascii;
    }

    /// <summary>
    /// Units of tool inputs (inputs.json "Unit"). Spec units are what Default, Min, Max and data files use; the page shows the
    /// document's system (IPS and imperial customs: psi, °F, lb, lbf, US ton; otherwise MPa, °C, kg, kN, t) unless a field
    /// offers its own UnitChoices. Length and angle keep their native SolidWorks boxes. Pure: only IsImperial(IModelDoc2)
    /// touches SolidWorks.
    /// </summary>
    internal static class Units
    {
        /// <summary>Units rendered as a unitless box that the page converts (SolidWorks has no pressure, temperature... box).</summary>
        public static readonly string[] Converted = { "pressure", "temperature", "mass", "force", "tonnage", "viscosity", "time" };

        public static readonly string[] Known = { "length", "angle", "percent", "none", "pressure", "temperature", "mass", "force", "tonnage", "viscosity", "time" };

        // Exact factors: 1 psi = 6894.757293168 Pa; 1 lb = 0.45359237 kg; 1 lbf = 4.4482216152605 N; 1 US ton-force = 2000 lbf;
        // 1 tonne-force = 9806.65 N; 1 kgf/cm² = 98066.5 Pa; °F = °C x 1.8 + 32.
        private const double Psi = 6894.757293168, Lbf = 4.4482216152605;

        private static readonly Dictionary<string, List<UnitDef>> Defs = new Dictionary<string, List<UnitDef>>
        {
            ["length"] = new List<UnitDef>
            {
                U("length", "mm", "mm", 0.001, 0, 1, 10, 0.1),
                U("length", "in", "in", 0.0254, 0, 0.1, 1, 0.01, "inch", "inches", "\""),
                U("length", "cm", "cm", 0.01, 0, 0.1, 1, 0.01),
                U("length", "m", "m", 1, 0, 0.01, 0.1, 0.001),
                U("length", "ft", "ft", 0.3048, 0, 0.1, 1, 0.01, "feet", "foot"),
            },
            ["angle"] = new List<UnitDef>
            {
                U("angle", "°", "deg", Math.PI / 180, 0, 1, 15, 0.1, "degree", "degrees"),
                U("angle", "rad", "rad", 1, 0, 0.01, 0.1, 0.001),
            },
            ["percent"] = new List<UnitDef> { U("percent", "%", "%", 1, 0, 0.1, 1, 0.01, "pct", "percent") },
            ["none"] = new List<UnitDef> { U("none", "", "", 1, 0, 1, 10, 0.1) },
            ["pressure"] = new List<UnitDef>
            {
                U("pressure", "MPa", "MPa", 1e6, 0, 0.1, 1, 0.01, "N/mm2", "N/mm²"),
                U("pressure", "psi", "psi", Psi, 0, 10, 100, 1, "lbf/in2", "lb/in2"),
                U("pressure", "bar", "bar", 1e5, 0, 1, 10, 0.1),
                U("pressure", "kgf/cm²", "kgf/cm2", 98066.5, 0, 1, 10, 0.1, "kg/cm2", "kg/cm²", "at"),
                U("pressure", "kPa", "kPa", 1e3, 0, 10, 100, 1),
                U("pressure", "Pa", "Pa", 1, 0, 1000, 10000, 100),
                U("pressure", "ksi", "ksi", Psi * 1000, 0, 1, 10, 0.1),
            },
            ["temperature"] = new List<UnitDef>
            {
                U("temperature", "°C", "degC", 1, 0, 1, 10, 0.5, "C", "celsius", "deg C"),
                U("temperature", "°F", "degF", 5.0 / 9.0, -32.0 * 5.0 / 9.0, 1, 10, 1, "F", "fahrenheit", "deg F"),
                U("temperature", "K", "K", 1, -273.15, 1, 10, 0.5, "kelvin"),
            },
            ["mass"] = new List<UnitDef>
            {
                U("mass", "kg", "kg", 1, 0, 0.5, 5, 0.1),
                U("mass", "lb", "lb", 0.45359237, 0, 1, 10, 0.1, "lbs", "lbm", "pound", "pounds"),
                U("mass", "g", "g", 0.001, 0, 10, 100, 1),
            },
            ["force"] = new List<UnitDef>
            {
                U("force", "kN", "kN", 1000, 0, 1, 10, 0.1),
                U("force", "lbf", "lbf", Lbf, 0, 10, 100, 1),
                U("force", "N", "N", 1, 0, 10, 100, 1),
                U("force", "kgf", "kgf", 9.80665, 0, 10, 100, 1),
            },
            ["tonnage"] = new List<UnitDef>
            {
                U("tonnage", "t", "t", 9806.65, 0, 1, 10, 0.5, "tf", "tonne", "tonnes", "tonne-force", "metric ton", "metric tons"),
                U("tonnage", "US ton", "US ton", 2000 * Lbf, 0, 1, 10, 0.5, "ton", "tons", "US tons", "short ton", "short tons", "uston"),
                U("tonnage", "kN", "kN", 1000, 0, 1, 10, 0.1),
                U("tonnage", "lbf", "lbf", Lbf, 0, 100, 1000, 10),
            },
            ["viscosity"] = new List<UnitDef>
            {
                U("viscosity", "cP", "cP", 0.001, 0, 100, 1000, 10, "mPa*s", "mPa·s", "mPa.s", "mPas", "centipoise"),
                U("viscosity", "Pa·s", "Pa*s", 1, 0, 0.1, 1, 0.01, "Pa.s", "Pas", "Pa s"),
                U("viscosity", "P", "P", 0.1, 0, 1, 10, 0.1, "poise"),
            },
            ["time"] = new List<UnitDef>
            {
                U("time", "min", "min", 60, 0, 1, 10, 0.5, "minute", "minutes"),
                U("time", "s", "s", 1, 0, 1, 10, 0.1, "sec", "second", "seconds"),
                U("time", "h", "h", 3600, 0, 0.5, 5, 0.1, "hr", "hour", "hours"),
            },
        };

        // Symbols (Ascii) per quantity: spec unit, metric display, imperial display, reference shown next to the value for the agent.
        private static readonly Dictionary<string, string[]> Roles = new Dictionary<string, string[]>
        {
            ["length"] = new[] { "mm", "mm", "in", "mm" },
            ["angle"] = new[] { "deg", "deg", "deg", "deg" },
            ["percent"] = new[] { "%", "%", "%", "%" },
            ["none"] = new[] { "", "", "", "" },
            ["pressure"] = new[] { "MPa", "MPa", "psi", "MPa" },
            ["temperature"] = new[] { "degC", "degC", "degF", "degC" },
            ["mass"] = new[] { "kg", "kg", "lb", "kg" },
            ["force"] = new[] { "kN", "kN", "lbf", "kN" },
            ["tonnage"] = new[] { "kN", "t", "US ton", "kN" },
            ["viscosity"] = new[] { "cP", "cP", "cP", "Pa*s" },
            ["time"] = new[] { "min", "min", "min", "min" },
        };

        private static UnitDef U(string q, string symbol, string ascii, double toSi, double offset, double inc, double fast, double slow, params string[] aliases) =>
            new UnitDef { Quantity = q, Symbol = symbol, Ascii = ascii, ToSi = toSi, Offset = offset, Inc = inc, Fast = fast, Slow = slow, Aliases = aliases };

        internal static string Key(string s) => (s ?? "").Trim().Replace("²", "2").Replace("·", "*").ToLowerInvariant();

        /// <summary>The quantity a spec "Unit" names: null means length (as it always has); unknown names render as plain numbers.</summary>
        public static string Quantity(string unit)
        {
            var u = (unit ?? "length").Trim().ToLowerInvariant();
            return Known.Contains(u) ? u : "none";
        }

        public static bool IsConverted(string unit) => Converted.Contains((unit ?? "").Trim().ToLowerInvariant());

        public static bool IsKnown(string unit) => Known.Contains((unit ?? "length").Trim().ToLowerInvariant());

        /// <summary>The unit the page shows for a quantity in the document's system.</summary>
        public static UnitDef For(string unit, bool imperial)
        {
            var q = Quantity(unit);
            return Find(q, Roles[q][imperial ? 2 : 1]);
        }

        /// <summary>The unit Default, Min, Max and data files use.</summary>
        public static UnitDef Spec(string unit)
        {
            var q = Quantity(unit);
            return Find(q, Roles[q][0]);
        }

        /// <summary>The unit shown to the agent next to the user's value ("2000 psi (13.79 MPa)").</summary>
        public static UnitDef Reference(string unit)
        {
            var q = Quantity(unit);
            return Find(q, Roles[q][3]);
        }

        public static double SpecToSi(string unit, double spec) => Spec(unit).ToSiValue(spec);

        public static double SiToSpec(string unit, double si) => Spec(unit).FromSi(si);

        /// <summary>A unit of a quantity by symbol or alias (case-insensitive); null when the quantity has no such unit.</summary>
        public static UnitDef Find(string quantity, string symbol)
        {
            if (symbol == null || !Defs.TryGetValue(Quantity(quantity), out var list)) return null;
            return list.FirstOrDefault(d => d.Matches(symbol));
        }

        /// <summary>A unit by symbol in any quantity (data written in another unit: "moldingPressurePsi.typ|psi").</summary>
        public static UnitDef FindAny(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            foreach (var q in Known)
            {
                var d = Find(q, symbol);
                if (d != null) return d;
            }
            return null;
        }

        /// <summary>
        /// The units a field offers ("UnitChoices": ["psi", "bar", "MPa", "kgf/cm2"]), in that order; unknown symbols are
        /// dropped. Empty when the field has none (it follows the document).
        /// </summary>
        public static List<UnitDef> Choices(string unit, IEnumerable<string> symbols)
        {
            var q = Quantity(unit);
            return (symbols ?? Enumerable.Empty<string>()).Select(s => Find(q, s)).Where(d => d != null).Distinct().ToList();
        }

        /// <summary>
        /// A number for people: up to <paramref name="significant"/> significant digits, never an exponent, no group
        /// separators, and integers are never rounded to zeros (444822 stays 444822).
        /// </summary>
        public static string Format(double v, int significant = 4)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return v.ToString(CultureInfo.InvariantCulture);
            if (v == 0) return "0";
            var magnitude = (int)Math.Floor(Math.Log10(Math.Abs(v)));
            var decimals = Math.Max(0, Math.Min(12, significant - 1 - magnitude));
            var r = Math.Round(v, decimals, MidpointRounding.AwayFromZero);
            if (r == 0) return "0";
            return r.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);
        }

        /// <summary>Round a display value to a few significant digits (defaults converted to another system: 13.79 MPa shows 2000 psi).</summary>
        public static double Nice(double v, int significant = 4)
        {
            if (v == 0 || double.IsNaN(v) || double.IsInfinity(v)) return v;
            var magnitude = (int)Math.Floor(Math.Log10(Math.Abs(v)));
            var decimals = significant - 1 - magnitude;
            if (decimals >= 0) return Math.Round(v, Math.Min(15, decimals), MidpointRounding.AwayFromZero);
            var scale = Math.Pow(10, -decimals);
            return Math.Round(v / scale, MidpointRounding.AwayFromZero) * scale;
        }

        /// <summary>"2000 psi" - a value in a unit, for captions.</summary>
        public static string Show(double si, UnitDef unit, int significant = 6, bool ascii = false)
        {
            var symbol = ascii ? unit.Ascii : unit.Symbol;
            return Format(unit.FromSi(si), significant) + (string.IsNullOrEmpty(symbol) ? "" : (symbol == "%" ? " %" : " " + symbol));
        }

        /// <summary>
        /// What the agent reads for a number: the value as the user saw it, then the reference figure when that differs:
        /// "2000 psi (13.79 MPa)", "212 degF (100 degC)", "1200 cP (1.2 Pa*s)", "50 US ton (444.8 kN)".
        /// </summary>
        public static string Describe(string unit, double si, UnitDef shown)
        {
            var reference = Reference(unit);
            shown = shown ?? reference;
            var text = Show(si, shown, 6, ascii: true);
            if (!ReferenceEquals(shown, reference) && shown.Ascii != reference.Ascii) text += " (" + Show(si, reference, 4, ascii: true) + ")";
            return text;
        }

        /// <summary>The same, in the document's system.</summary>
        public static string Describe(ToolInputSpec s, double si, bool imperial) => Describe(s?.Unit, si, For(s?.Unit, imperial));

        /// <summary>
        /// The payload text number inputs have always had (length "12 mm (0.012 m)", angle "5 deg", others the plain
        /// number), so existing tools' prompts stay as they were. Percent gains its "%".
        /// </summary>
        public static string Legacy(string unit, double v)
        {
            var u = (unit ?? "length").ToLowerInvariant();
            if (u == "length") return $"{(v * 1000).ToString("0.###", CultureInfo.InvariantCulture)} mm ({v.ToString("0.######", CultureInfo.InvariantCulture)} m)";
            if (u == "angle") return $"{(v * 180 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture)} deg";
            if (u == "percent") return v.ToString("0.######", CultureInfo.InvariantCulture) + " %";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Imperial or metric from the unit-system numbers: IPS is imperial; Custom is imperial when lengths are inches, feet,
        /// feet-inches, mil or micro-inch, or masses are pounds; MMGS, MKS and CGS are metric.
        /// </summary>
        public static bool IsImperial(int unitSystem, int linearUnit, int massUnit)
        {
            if (unitSystem == (int)swUnitSystem_e.swUnitSystem_IPS) return true;
            if (unitSystem != (int)swUnitSystem_e.swUnitSystem_Custom) return false;
            switch ((swLengthUnit_e)linearUnit)
            {
                case swLengthUnit_e.swINCHES:
                case swLengthUnit_e.swFEET:
                case swLengthUnit_e.swFEETINCHES:
                case swLengthUnit_e.swMIL:
                case swLengthUnit_e.swUIN:
                    return true;
            }
            return massUnit == (int)swUnitsMassPropMass_e.swUnitsMassPropMass_Pounds;
        }

        /// <summary>The document's system (same preference calls as SwModelLoader). Metric when it can't be read.</summary>
        public static bool IsImperial(IModelDoc2 model)
        {
            try
            {
                var ext = model?.Extension;
                if (ext == null) return false;
                var none = (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified;
                var system = ext.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, none);
                if (system != (int)swUnitSystem_e.swUnitSystem_Custom) return IsImperial(system, 0, 0);
                return IsImperial(system,
                    ext.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, none),
                    ext.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsMassPropMass, none));
            }
            catch
            {
                return false;
            }
        }
    }
}

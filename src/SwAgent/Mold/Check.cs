using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>Process class (design D4). The class decides lands, vents, pressures and the structural load cases.</summary>
    public enum ProcessClass { A_Pour, B_Compression, C_Positive }

    public static class ProcessClasses
    {
        public static ProcessClass From(string processType)
        {
            switch ((processType ?? "").Trim().ToLowerInvariant())
            {
                case "liquid_cast_hot":
                case "liquid_cast_rt":
                case "foam_pour":
                    return ProcessClass.A_Pour;
                case "rubber_compression":
                case "compound_compression":
                case "liquid_compression":
                    return ProcessClass.B_Compression;
                case "thermoplastic_compression":
                case "preform_sinter":
                    return ProcessClass.C_Positive;
                default:
                    throw new ArgumentException("Unknown processType '" + processType + "'");
            }
        }

        public static string Letter(ProcessClass c) => c == ProcessClass.A_Pour ? "A" : c == ProcessClass.B_Compression ? "B" : "C";
    }

    /// <summary>
    /// One entry of the record's checks[] (design 4.3): demand against capacity with a margin and a status. For an upper
    /// limit (stress, deflection, mass) margin = capacity/demand - 1; for a lower limit (separation factor, clearance,
    /// engagement) the achieved value is the demand and margin = demand/capacity - 1. Negative margin = fail.
    /// </summary>
    public static class Check
    {
        public static readonly string[] Types =
        {
            "deflection", "stress", "fatigue", "bearing", "boltSeparation", "boltLoad", "boltFatigue", "stripping", "fit",
            "weight", "box", "interference", "clearance", "draft", "undercut", "criticalSurface", "airTrap", "machinability",
            "fill", "demold", "cure", "press", "temperature", "thread", "buckling", "lifting", "process", "scope", "layout",
        };

        public static readonly string[] Statuses = { "pass", "warn", "fail", "not_run", "waived" };

        public static JObject Upper(string id, string type, double demand, double capacity, string unit, string method, string body = null, string loadCase = null, string note = null)
        {
            // a demand that is zero up to rounding (a centred load centre) has no finite margin
            var margin = demand <= 1e-9 * Math.Max(1.0, Math.Abs(capacity)) ? double.PositiveInfinity : capacity / demand - 1.0;
            return Make(id, type, demand, capacity, unit, margin, method, body, loadCase, note);
        }

        public static JObject Lower(string id, string type, double achieved, double minimum, string unit, string method, string body = null, string loadCase = null, string note = null)
        {
            var margin = minimum <= 0 ? double.PositiveInfinity : achieved / minimum - 1.0;
            return Make(id, type, achieved, minimum, unit, margin, method, body, loadCase, note);
        }

        /// <summary>An advisory or yes/no result (status given directly).</summary>
        public static JObject Status(string id, string type, string status, string method, string note, string body = null)
        {
            return new JObject
            {
                ["id"] = id, ["source"] = "mold_calc@" + MoldCalc.Version, ["type"] = type, ["body"] = body, ["loadCase"] = null,
                ["demand"] = null, ["capacity"] = null, ["unit"] = null, ["margin"] = null, ["status"] = status,
                ["method"] = method, ["stage"] = "3", ["note"] = note ?? "", ["waivedBy"] = null,
            };
        }

        private static JObject Make(string id, string type, double demand, double capacity, string unit, double margin, string method, string body, string loadCase, string note)
        {
            var finite = !double.IsInfinity(margin) && !double.IsNaN(margin);
            var status = !finite ? "pass" : margin >= -1e-9 ? "pass" : "fail";
            return new JObject
            {
                ["id"] = id, ["source"] = "mold_calc@" + MoldCalc.Version, ["type"] = type, ["body"] = body, ["loadCase"] = loadCase,
                ["demand"] = Si.R(demand, 4), ["capacity"] = Si.R(capacity, 4), ["unit"] = unit,
                ["margin"] = finite ? (JToken)Math.Round(margin, 4) : null, ["status"] = status,
                ["method"] = method, ["stage"] = "3", ["note"] = note ?? "", ["waivedBy"] = null,
            };
        }

        public static bool Fails(JObject c) => (string)c["status"] == "fail";

        /// <summary>Checks ordered lowest margin first (failing ones on top), as the report shows them.</summary>
        public static List<JObject> Ordered(IEnumerable<JObject> checks) =>
            checks.OrderBy(c => (string)c["status"] == "fail" ? 0 : (string)c["status"] == "warn" ? 1 : 2)
                  .ThenBy(c => c["margin"] == null || c["margin"].Type == JTokenType.Null ? double.MaxValue : (double)c["margin"])
                  .ThenBy(c => (string)c["id"], StringComparer.Ordinal).ToList();
    }
}

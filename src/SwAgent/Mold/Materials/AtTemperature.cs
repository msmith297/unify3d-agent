using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    /// <summary>A mold grade's properties at the press temperature on the long-term (10,000 h) basis (S10-S12).</summary>
    public sealed class GradeAtT
    {
        public string Id, Family, Short;
        public double TempC;
        /// <summary>MPa (N/mm²).</summary>
        public double E, Sy, Su, Sy20, Su20, E20;
        public double Nu, Density_kg_m3, Cte_perK;
        /// <summary>ok | ok-derated | limited | beyond-limit | blocked | no-data (above 250 C)</summary>
        public string Service;
        public string Note;
        public bool IsAluminium => Family == "aluminium";
        public JObject Row;
    }

    /// <summary>
    /// S10 f(T): linear over T = [20, 100, 150, 200, 250] C of [1, retained...]; f = 1 below 20 C.
    /// Critique A8: above 250 C there are no data, so every grade is refused there (the design extrapolated steels with the
    /// 250 C factors, which is the unsafe direction). S12 service status is by temperature; the stress-aware decision for
    /// "limited" temperatures is made by Sizing (TemperatureGate) once the governing stresses are known.
    /// </summary>
    public static class AtTemperature
    {
        public static readonly double[] Temps = { 20, 100, 150, 200, 250 };
        public const double MaxDataC = 250;

        public static double Factor(JArray retained, double tempC)
        {
            if (tempC <= 20) return 1.0;
            if (retained == null || retained.Count < 4) return 1.0;
            var ys = new[] { 1.0 }.Concat(retained.Select(x => (double)x)).ToArray();
            if (tempC >= MaxDataC) return ys[ys.Length - 1];
            return Cfg.Interp(Temps, ys, tempC);
        }

        public static GradeAtT Get(JObject grade, double tempC)
        {
            if (grade == null) throw new ArgumentNullException(nameof(grade));
            var ret = grade["retained"] as JObject;
            var fE = Factor((ret?["E"] as JArray) ?? (grade["E_retained"] as JArray), tempC);
            var fY = Factor(ret?["yield_long_10000h"] as JArray, tempC);
            var fU = Factor(ret?["uts_long_10000h"] as JArray, tempC);
            var g = new GradeAtT
            {
                Id = (string)grade["id"], Family = (string)grade["family"], Short = (string)grade["short"] ?? (string)grade["id"],
                TempC = tempC, Row = grade,
                E20 = (double)grade["E_GPa"] * 1000.0, Sy20 = (double)grade["yield_MPa"], Su20 = (double)grade["uts_MPa"],
                Nu = (double?)grade["poisson"] ?? 0.3, Density_kg_m3 = (double?)grade["density_kg_m3"] ?? 2700, Cte_perK = (double?)grade["cte_per_K"] ?? 2.36e-5,
            };
            g.E = g.E20 * fE;
            g.Sy = g.Sy20 * fY;
            g.Su = g.Su20 * fU;
            var ms = grade["max_service_C"] as JObject;
            var full = (double?)ms?["full_strength"] ?? double.MaxValue;
            var der = (double?)ms?["derated"] ?? double.MaxValue;
            var never = (double?)ms?["never_above"] ?? double.MaxValue;
            if (tempC > MaxDataC)
            {
                g.Service = "no-data";
                g.Note = "no strength data above " + MaxDataC + " C - refused (critique A8); use the supplier's hot data and a hand check";
            }
            else if (tempC <= full) g.Service = "ok";
            else if (tempC <= der)
            {
                g.Service = "ok-derated";
                g.Note = $"{g.Short} above {full} C: long-term (10,000 h) derated strength used";
            }
            else if (tempC <= never)
            {
                g.Service = "limited";
                g.Note = $"{g.Short} between {der} and {never} C: allowed only at low stress (<= 0.5 x the derated allowable) or with N <= 10,000 and your confirmation";
            }
            else if ((bool?)grade["agesDimensionally"] == true)
            {
                g.Service = "blocked";
                g.Note = $"{g.Short} above {never} C ages and changes size - choose another grade";
            }
            else
            {
                g.Service = "beyond-limit";
                g.Note = $"{g.Short} above its {never} C limit: overaging/softening - allowed only when every governing stress is <= 0.5 x the derated allowable";
            }
            return g;
        }
    }
}

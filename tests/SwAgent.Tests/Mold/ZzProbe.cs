using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    public class ZzProbe
    {
        private const string Out = @"C:\Users\msmit\AppData\Local\Temp\claude\C--Users-msmit-Documents-Claude-Code-SolidWorks-Agent-Plugin\2b455cdd-1c60-4ed3-9eb5-8eb641a4b994\scratchpad\a1fix\probe.txt";

        [Fact]
        public void Probe()
        {
            var sb = new StringBuilder();
            CalcResult Run(string a, JObject d, JObject q = null, JObject o = null) => MoldCalc.Run(a, d, q, o, Data);
            var d1 = E2Design();
            d1["inputs"]["max_lid_mass"] = 1.0;
            var r1 = Run("size", d1);
            sb.AppendLine("lid relax: " + r1.RecordPatch.SelectToken("structural.results.plates.lid").ToString(Formatting.None));
            sb.AppendLine(string.Join("\n", r1.Text.Split('\n').Where(l => l.Contains("A2.6") || l.Contains("WEIGHT-LID"))));

            foreach (var (psi, depth) in new[] { (3000.0, 100.0), (3000.0, 150.0), (3000.0, 200.0) })
            {
                var d = E2Design();
                d["inputs"]["cavities"] = 6;
                d["inputs"]["press_pressure"] = psi * Si.PsiToMPa;
                d["layout"] = new JObject { ["patternRequest"] = "3x2" };
                d["part"]["box_mm"] = new JArray(152.4, 101.6, depth);
                d["part"]["volume_mm3"] = 152.4 * 101.6 * depth;
                MoldRecord.DeepMerge(d, Run("size", d, null, new JObject { ["gap_mm"] = 51 }).RecordPatch);
                d["inputs"]["max_plate_mass"] = 45;
                d["inputs"]["max_plate_size"] = new JArray(900, 600, 300);
                var opts = (JArray)Run("split", d, new JObject { ["plate"] = "base" }).Json["options"];
                if (opts.Count == 0) { sb.AppendLine("no options at depth " + depth); continue; }
                var opt = (JObject)opts[0];
                MoldRecord.DeepMerge(d, new JObject { ["structural"] = new JObject { ["split"] = new JObject { ["base"] = opt } } });
                var r = Run("size", d, null, new JObject { ["bolts.minDiameter_mm"] = 12.7, ["bolts.maxRows"] = 1 });
                sb.AppendLine("==== split at " + psi + " psi depth " + depth);
                sb.AppendLine(r.Text);
                sb.AppendLine("lift: " + r.RecordPatch.SelectToken("hardware.lift").ToString(Formatting.None));
            }
            File.WriteAllText(Out, sb.ToString());
        }
    }
}

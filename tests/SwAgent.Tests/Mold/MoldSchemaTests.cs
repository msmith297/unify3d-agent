using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Json.Schema;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// Records the calculator, the tools and the agent write validate against Track E's data/mold.schema.json (draft 2020-12,
    /// JsonSchema.Net): the shipped sample, the E2 job, the 8-cavity rubber mold, a split class B lid and plates finished by
    /// the layout FE on rails and pillars.
    /// </summary>
    public class MoldSchemaTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mold-schema-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private static readonly Lazy<JsonSchema> _schema = new Lazy<JsonSchema>(() =>
            JsonSchema.FromText(File.ReadAllText(Path.Combine(DataDir, "mold.schema.json"))));

        /// <summary>The schema's errors for a record ("location: message"), empty when it validates.</summary>
        private static List<string> Errors(JObject record)
        {
            var result = _schema.Value.Evaluate(JsonNode.Parse(record.ToString()), new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (result.IsValid) return new List<string>();
            return result.Details.Where(d => d.Errors != null).SelectMany(d => d.Errors.Select(e => $"{d.InstanceLocation}: {e.Value} ({d.EvaluationPath})")).Distinct().Take(20).ToList();
        }

        private static void Valid(JObject record, string what)
        {
            var errors = Errors(record);
            Assert.True(errors.Count == 0, what + ":\n" + string.Join("\n", errors));
        }

        /// <summary>A record as mold_record init writes it, then each action's patch merged as mold_calc does.</summary>
        private MoldRecord Build(JObject design, string name, params (string Action, JObject Overrides)[] runs)
        {
            var rec = MoldRecord.Init(Path.Combine(_root, name + ".mold"), Path.Combine(_root, name + ".SLDPRT"), (JObject)design["inputs"], "1.0.0", "0.2.0");
            rec.Json["units"]["display"] = (string)design.SelectToken("units.display") ?? "imperial";
            rec.Merge(new JObject { ["part"] = design["part"].DeepClone(), ["layout"] = design["layout"]?.DeepClone() ?? new JObject() });
            foreach (var (action, overrides) in runs)
            {
                var res = MoldCalc.Run(action, design, null, overrides, Data);
                if (res.RecordPatch == null) continue;
                rec.Merge(res.RecordPatch);
                MoldRecord.DeepMerge(design, res.RecordPatch);
            }
            Assert.Empty(rec.Validate());
            return rec;
        }

        private static (string, JObject)[] All(JObject overrides = null) =>
            new[] { ("process", overrides), ("press", overrides), ("layout", overrides), ("size", overrides) };

        [Fact]
        public void The_shipped_sample_record_validates()
        {
            Valid(JObject.Parse(File.ReadAllText(Path.Combine(DataDir, "examples", "mold.sample.json"))), "mold.sample.json");
        }

        [Fact]
        public void The_E2_job_validates_with_the_agents_keys()
        {
            var rec = Build(E2Design(), "e2", All());
            rec.Merge(new JObject
            {
                ["status"] = "building",
                ["decisions"] = new JArray(new JObject { ["id"] = "DEC-1", ["topic"] = "parting line", ["choice"] = "PL1 at the top face", ["by"] = "user" }),
                ["simulations"] = new JArray(new JObject { ["id"] = "SIM-1", ["hook"] = "H1", ["status"] = "not_run", ["reason"] = "no process simulator configured" }),
                ["stages"] = new JArray(new JObject { ["id"] = "4.1", ["status"] = "done" }),
            });
            Valid(rec.Json, "E2");
            // a merge through the record's own path (the $replace directives are consumed, never stored)
            Assert.Null(rec.Json[MoldRecord.ReplaceKey]);
        }

        [Fact]
        public void The_rubber_mold_and_its_split_class_B_lid_validate()
        {
            var d = MoldReviewTests.Rubber();
            var rec = Build(d, "rubber", ("process", null), ("press", null), ("size", new JObject { ["gap_mm"] = 40 }));
            Valid(rec.Json, "rubber");
            var opts = (JArray)MoldCalc.Run("split", d, new JObject { ["plate"] = "lid" }, null, Data).Json["options"];
            rec.Merge(new JObject { [MoldRecord.ReplaceKey] = new JArray("structural.split.lid"), ["structural"] = new JObject { ["split"] = new JObject { ["lid"] = opts[0] } } });
            MoldRecord.DeepMerge(d, new JObject { ["structural"] = new JObject { ["split"] = new JObject { ["lid"] = opts[0] } } });
            var size = MoldCalc.Run("size", d, null, new JObject { ["gap_mm"] = 40 }, Data);
            rec.Merge(size.RecordPatch);
            Assert.Contains((JArray)rec.Json.SelectToken("hardware.bolts"), b => ((string)b["joint"]).StartsWith("LSUB/LID-"));
            Valid(rec.Json, "rubber with a split lid");
        }

        [Fact]
        public void Plates_finished_by_the_layout_FE_validate()
        {
            var e = E2Design();
            e["inputs"]["mold_support"] = "On rails";
            var rails = Build(e, "rails", All(new JObject { ["support"] = "B", ["rail_span_mm"] = 100, ["delta_allow_mm"] = 0.05 }));
            var span = rails.Json.SelectToken("structural.results.plates.span");
            Assert.True(span?["fe"] != null, span?.ToString() + string.Join(Environment.NewLine, rails.Json["checks"].Where(c => ((string)c["id"]).StartsWith("SPAN")).Select(c => c.ToString())));
            Assert.Equal(100.0, (double)rails.Json.SelectToken("structural.inputs.rail_span_mm"), 9);   // kept for check
            Valid(rails.Json, "rails");
            var p = E2Design();
            var pillars = Build(p, "pillars", All(new JObject { ["support"] = "E", ["pillars_mm"] = new JObject { ["u"] = new JArray(215.9), ["v"] = new JArray(95.25) }, ["delta_allow_mm"] = 0.05 }));
            Assert.Equal("Pillar", (string)pillars.Json.SelectToken("structural.results.plates.span.bc"));
            Valid(pillars.Json, "pillars");
        }

        [Fact]
        public void Many_cavities_list_partial_loading_without_counting_every_pattern()
        {
            var d = E2Design();
            d["inputs"]["cavities"] = 24;
            d["part"]["box_mm"] = new JArray(40, 30, 10);
            d["part"]["volume_mm3"] = 40 * 30 * 10;
            d["part"]["projectedArea_mm2"] = 40 * 30;
            d["part"]["perimeter_mm"] = 140;
            ((JObject)d["layout"]).Remove("patternRequest");
            ((JObject)d["inputs"]).Remove("max_plate_mass");
            ((JObject)d["inputs"]).Remove("max_plate_size");
            var rec = Build(d, "many", All());
            Assert.Contains(rec.Json.SelectToken("structural.results.partialLoading.rows"), r => r["of"].Type == JTokenType.String);
            Valid(rec.Json, "24 cavities");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;
using static SwAgent.Tests.Mold.MoldTestData;

namespace SwAgent.Tests.Mold
{
    /// <summary>The design record (mold.json, unify3d.mold/1): init, merge rules, validation, the phase summary, and the sheets.</summary>
    public class MoldRecordTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mold-record-" + Guid.NewGuid().ToString("N"), "part mold.mold");

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(_dir), true); } catch (IOException) { }
        }

        private MoldRecord NewRecord()
        {
            var inputs = (JObject)E2Design()["inputs"];
            return MoldRecord.Init(_dir, @"C:\Molds\part mold.SLDPRT", inputs, "1.0.0", "0.2.0");
        }

        [Fact]
        public void Init_creates_the_folders_and_a_valid_skeleton()
        {
            var r = NewRecord();
            Assert.True(File.Exists(Path.Combine(_dir, "mold.json")));
            foreach (var sub in MoldRecord.Subfolders) Assert.True(Directory.Exists(Path.Combine(_dir, sub)), sub);
            Assert.True(Guid.TryParse(r.Id, out _));
            Assert.Equal("planning", (string)r.Json["status"]);
            Assert.Equal("imperial", (string)r.Json.SelectToken("units.display"));
            Assert.Equal("unknown", (string)r.Json.SelectToken("materials.part.kinetics.status"));     // F2 reserved
            Assert.Empty(r.Validate());
            Assert.Throws<InvalidOperationException>(() => NewRecord());                                // never over an existing record
            var again = MoldRecord.Open(_dir);
            Assert.Equal(r.Id, again.Id);
            Assert.Equal(r.Json.ToString(), again.Json.ToString());
            Assert.Equal((string)r.Json["createdAt"], (string)again.Json["createdAt"]);                 // dates stay strings
            Assert.Throws<FileNotFoundException>(() => MoldRecord.Open(Path.Combine(_dir, "nope")));
        }

        [Fact]
        public void Merge_matches_arrays_by_id_merges_objects_and_replaces_other_arrays()
        {
            var r = NewRecord();
            r.Merge(new JObject
            {
                ["status"] = "sizing",
                ["layout"] = new JObject { ["pattern"] = "grid 2x1", ["footprint_mm"] = new JArray(431.8, 190.5) },
                ["checks"] = new JArray(new JObject { ["id"] = "A", ["type"] = "stress", ["status"] = "pass", ["margin"] = 0.5 }, new JObject { ["id"] = "B", ["type"] = "weight", ["status"] = "fail", ["margin"] = -0.1 }),
                ["stages"] = new JArray(new JObject { ["id"] = "4.1", ["status"] = "running" }),
            });
            var status = r.Merge(new JObject
            {
                ["layout"] = new JObject { ["footprint_mm"] = new JArray(441.8, 190.5), ["moats"] = "separate" },
                ["checks"] = new JArray(new JObject { ["id"] = "B", ["status"] = "pass", ["margin"] = 0.2 }, new JObject { ["id"] = "C", ["type"] = "bearing", ["status"] = "fail", ["margin"] = -0.3 }),
                ["stages"] = new JArray(new JObject { ["id"] = "4.1", ["status"] = "done", ["checkpoint"] = "20261019-153012-stage-4.1" }),
                ["id"] = Guid.NewGuid().ToString(),        // identity is never merged
                ["schema"] = "something/9",
            });
            Assert.Equal("grid 2x1", (string)r.Json.SelectToken("layout.pattern"));
            Assert.Equal(441.8, (double)r.Json.SelectToken("layout.footprint_mm[0]"));                 // plain array: replaced
            Assert.Equal("separate", (string)r.Json.SelectToken("layout.moats"));
            var checks = (JArray)r.Json["checks"];
            Assert.Equal(new[] { "A", "B", "C" }, checks.Select(c => (string)c["id"]));
            Assert.Equal("weight", (string)checks[1]["type"]);                                          // merged, not replaced
            Assert.Equal("pass", (string)checks[1]["status"]);
            Assert.Equal("done", (string)r.Json.SelectToken("stages[0].status"));
            Assert.Equal(MoldRecord.Schema, (string)r.Json["schema"]);
            Assert.StartsWith("merged; status sizing; 1 check fails: C", status);
            Assert.EndsWith("valid", status);
        }

        [Fact]
        public void Validation_lists_what_is_wrong()
        {
            var r = NewRecord();
            r.Json["status"] = "half-done";
            r.Json["checks"] = new JArray(
                new JObject { ["id"] = "X", ["type"] = "nonsense", ["status"] = "ok" },
                new JObject { ["type"] = "stress", ["status"] = "pass" },
                new JObject { ["id"] = "W", ["type"] = "weight", ["status"] = "waived" });
            r.Json["simulations"] = new JArray(new JObject { ["id"] = "SIM-1", ["hook"] = "H9", ["status"] = "maybe" });
            r.Json["shrink"] = new JObject { ["scale"] = new JArray(1.01, 1.01) };
            r.Json["bodies"] = new JArray(new JObject { ["id"] = "BASE-1" }, new JObject { ["id"] = "BASE-1" });
            var e = r.Validate();
            Assert.Contains(e, x => x.StartsWith("status"));
            Assert.Contains(e, x => x.Contains("unknown type nonsense"));
            Assert.Contains(e, x => x.Contains("checks.X: status"));
            Assert.Contains(e, x => x.Contains("checks[1] needs a string id"));
            Assert.Contains(e, x => x.Contains("waivedBy"));
            Assert.Contains(e, x => x.Contains("hook must be"));
            Assert.Contains(e, x => x.Contains("shrink.scale"));
            Assert.Contains(e, x => x.Contains("repeats id BASE-1"));
        }

        [Fact]
        public void A_full_calculator_run_merges_into_a_valid_record_with_a_short_summary()
        {
            var r = NewRecord();
            var design = E2Design();
            r.Merge(new JObject { ["part"] = design["part"].DeepClone(), ["layout"] = design["layout"].DeepClone() });   // what mold_part analyze and the plan write
            foreach (var action in new[] { "process", "press", "layout", "size" })
            {
                var res = MoldCalc.Run(action, design, null, null, Data);
                if (res.RecordPatch == null) continue;
                var status = r.Merge(res.RecordPatch);
                Assert.True(status.EndsWith("valid"), action + ": " + status);
                MoldRecord.DeepMerge(design, res.RecordPatch);
            }
            r.Merge(new JObject
            {
                ["decisions"] = new JArray(new JObject { ["id"] = "DEC-1", ["topic"] = "parting line", ["choice"] = "PL1 at the top face", ["by"] = "user" }),
                ["simulations"] = new JArray(new JObject { ["id"] = "SIM-1", ["hook"] = "H1", ["status"] = "not_run", ["reason"] = "no process simulator configured" }),
            });
            Assert.Empty(r.Validate());
            r.Save();
            var s = MoldRecord.Open(_dir).Summary();
            Assert.True(s.Length <= 6000, "summary " + s.Length);
            Assert.Contains("Gauge: recommended", s);
            Assert.Contains("DEC-1", s);
            Assert.Contains("SIM-1 H1 not_run", s);
            Assert.Contains("BASE-1", s);
            Assert.True(r.Summary(400).Length <= 400);
            Assert.Equal("grid 2x1", (string)r.Section("layout")["pattern"]);
            Assert.NotNull(r.Section("structural.results.walls"));
            // the record itself is a valid design for the calculator (check re-runs on it)
            var check = MoldCalc.Run("check", r.Json, null, null, Data);
            Assert.DoesNotContain(check.Checks, Check.Fails);
        }

        [Fact]
        public void Merge_sets_updatedAt_from_the_clock()
        {
            var old = MoldRecord.Clock;
            try
            {
                MoldRecord.Clock = () => new DateTime(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);
                var r = NewRecord();
                MoldRecord.Clock = () => new DateTime(2026, 10, 19, 21, 14, 3, DateTimeKind.Utc);
                r.Merge(new JObject { ["status"] = "building" });
                Assert.Equal("2026-10-19T20:00:00Z", (string)r.Json["createdAt"]);
                Assert.Equal("2026-10-19T21:14:03Z", (string)r.Json["updatedAt"]);
            }
            finally { MoldRecord.Clock = old; }
        }

        // ---------------------------------------------------------------- template language and the sheets

        [Fact]
        public void Template_fills_paths_units_loops_and_conditions()
        {
            var ctx = new JObject
            {
                ["name"] = "Mold <A&B>",
                ["mass"] = 7.75,
                ["p"] = 3.08,
                ["list"] = new JArray(new JObject { ["id"] = "B1", ["n"] = 2 }, new JObject { ["id"] = "B2", ["n"] = 3 }),
                ["empty"] = new JArray(),
                ["nested"] = new JObject { ["rows"] = new JArray(new JObject { ["items"] = new JArray("x", "y") }) },
                ["flag"] = false,
            };
            var unresolved = new List<string>();
            var html = Template.Render("<h1>{{name}}</h1>{{mass|kg}}; {{p|MPa}}; {{#each list}}[{{@number}}:{{id}} x{{n}} of {{name}}]{{/each}}" +
                                       "{{#if empty}}E{{else}}no rows{{/if}}{{#if flag}}F{{/if}}{{#each nested.rows}}{{#each items}}<{{this}}>{{/each}}{{/each}}{{missing.path}}",
                ctx, true, unresolved);
            Assert.Contains("<h1>Mold &lt;A&amp;B&gt;</h1>", html);
            Assert.Contains("17.09 lb (7.75 kg)", html);
            Assert.Contains("446.7 psi (3.08 MPa)", html);
            Assert.Contains("[1:B1 x2 of Mold &lt;A&amp;B&gt;][2:B2 x3 of Mold &lt;A&amp;B&gt;]", html);
            Assert.Contains("no rows", html);
            Assert.DoesNotContain("F", html.Replace("of", ""));
            Assert.Contains("<x><y>", html);
            Assert.Equal(new[] { "missing.path" }, unresolved);
            Assert.Contains("7.75 kg (17.09 lb)", Template.Render("{{mass|mass}}", ctx, false));
            Assert.Throws<FormatException>(() => Template.Render("{{#each list}}x", ctx, true));
        }

        private const string SetupTemplate = @"<html><body>
<h1>{{sheet.title}} - {{sheet.moldId}}</h1><p>{{sheet.materialName}} ({{sheet.confidence}}) - {{sheet.processLabel}}</p>
<table>{{#each sheet.pieces}}<tr><td>{{id}}</td><td>{{role}}</td><td>{{mass_kg|kg}}</td><td>{{lift}}</td></tr>{{/each}}</table>
<p>Press {{sheet.press.setPointC|degC}}; gauge {{sheet.press.gaugeRecommended_MPa|MPa}} (max {{sheet.press.gaugeMax_MPa|MPa}}, {{sheet.press.gaugeMaxBasis}}); {{sheet.press.conversion}}; closed {{sheet.press.closedHeight_mm|mm}}</p>
<table>{{#each sheet.partialLoading}}<tr><td>{{active}}</td><td>{{gauge_MPa|MPa}}</td><td>{{patterns}}</td></tr>{{/each}}</table><p>never fewer than {{sheet.neverFewerThan}}</p>
<p>Charge {{sheet.charge.perCavity_g|g}} per cavity, {{sheet.charge.shot_g|g}} per shot +-{{sheet.charge.tolerancePct}} %</p>
<p>Cure {{sheet.cure.minutes|min}}: {{sheet.cure.note}}</p>
{{#each sheet.bolts}}<p>{{count}} x {{size}}: {{torqueDry_Nm|Nm}}, preload {{preloadLow_kN|kN}} to {{preloadHigh_kN|kN}}</p>{{/each}}
<p>Dowels: {{sheet.dowelScheme}}. Lid {{sheet.lid.mass_kg|kg}} of {{sheet.lid.limit_kg|kg}} (index {{sheet.lid.liftingIndex}})</p>
<ul>{{#each sheet.slideIn}}<li>{{this}}</li>{{/each}}</ul><p>{{sheet.preheat}}</p><p>{{sheet.hazards}}</p>
<ul>{{#each sheet.maintenance}}<li>{{this}}</li>{{/each}}</ul><ul>{{#each sheet.firstArticle}}<li>{{this}}</li>{{/each}}</ul>
{{#if sheet.warnings}}<ul>{{#each sheet.warnings}}<li>{{id}} {{status}} {{note}}</li>{{/each}}</ul>{{else}}<p>No open warnings.</p>{{/if}}
<p>Shrink {{shrink.feature}}</p></body></html>";

        private const string MachiningTemplate = @"<html><body>{{#each machining.bodies}}<h2>{{id}} ({{role}}, {{material}})</h2>
<p>finished {{finished_mm}} mm, stock {{stock_mm|mm}}, blank {{blank_mm}} mm, finish {{finish}}</p>
<table>{{#each holes}}<tr><td>{{id}}</td><td>{{kind}}</td><td>{{qty}}</td><td>{{dia_mm}}</td><td>{{fit}}</td><td>{{limits}}</td></tr>{{/each}}</table>
<ul>{{#each flatness}}<li>{{face}} {{tol_mm|mm}} {{note}}</li>{{/each}}</ul>{{/each}}
<ul>{{#each machining.general}}<li>{{this}}</li>{{/each}}</ul>
<table>{{#each machining.bom}}<tr><td>{{item}}</td><td>{{size}}</td><td>{{qty}}</td><td>{{note}}</td></tr>{{/each}}</table></body></html>";

        private MoldRecord SizedRecord(JObject design)
        {
            var r = NewRecord();
            foreach (var action in new[] { "process", "press", "size" })
            {
                var res = MoldCalc.Run(action, design, null, null, Data);
                r.Merge(res.RecordPatch);
                MoldRecord.DeepMerge(design, res.RecordPatch);
            }
            r.Merge(new JObject { ["materials"] = new JObject { ["plates"] = new JObject { ["id"] = "al_6061_t651" } } });
            return r;
        }

        [Fact]
        public void Setup_sheet_resolves_every_placeholder_and_prints_both_unit_systems()
        {
            var r = SizedRecord(E2Design());
            var html = SetupSheet.Fill(r, SetupTemplate, Data, out var unresolved);
            Assert.Empty(unresolved);
            Assert.Contains("psi (", html);
            Assert.Contains("lb (", html);
            Assert.Contains("degF (100 degC)", html);
            Assert.Contains("TDI-polyether / MBOCA 90A", html);
            Assert.Contains("MBOCA", html);                                   // hazards from the row
            Assert.Contains("locating stop", html);                           // E4 slide-in hardware
            Assert.Contains("Datasheet demold time", html);                   // E6 cure note for class A
            Assert.Contains("BASE-1", html);
            var path = SetupSheet.Write(r, WriteTemplate("setup.html", SetupTemplate), Data);
            Assert.Equal(Path.Combine(_dir, "setup-sheet.html"), path);
            Assert.Equal("setup-sheet.html", (string)MoldRecord.Open(_dir).Json["setupSheet"]);
        }

        [Fact]
        public void Machining_sheet_and_hardware_list_for_a_split_base()
        {
            var d = E2Design();
            d["inputs"]["cavities"] = 6;
            d["layout"] = new JObject { ["patternRequest"] = "3x2" };
            d["part"]["box_mm"] = new JArray(152.4, 101.6, 50.8);
            d["part"]["volume_mm3"] = 152.4 * 101.6 * 50.8;
            var size = MoldCalc.Run("size", d, null, new JObject { ["gap_mm"] = 51 }, Data);
            MoldRecord.DeepMerge(d, size.RecordPatch);
            var opt = (JObject)MoldCalc.Run("split", d, new JObject { ["plate"] = "base" }, null, Data).Json["options"][0];
            d["structural"]["split"] = opt;
            var r = SizedRecord(d);
            var html = MachiningSheet.Fill(r, MachiningTemplate, Data, out var unresolved);
            Assert.Empty(unresolved);
            Assert.Contains("SUB-1", html);
            Assert.Contains("counterbored clearance (hardened washer)", html);
            Assert.Contains("tapped for insert (STI tap)", html);
            Assert.Contains("press (aluminium: hot-fit interference", html);  // dowel fits with limits (S60 column in aluminium)
            Assert.Contains("0.4990 - 0.4992 in", html);                      // 1/2 in press hole in aluminium (dowel table)
            Assert.Contains("lap flat", html);
            Assert.Contains("never proud", html);                             // A12 shut-off rule
            var bom = HardwareBom.Build(r.Json, Data);
            Assert.Contains(bom, x => ((string)x["item"]).StartsWith("SHCS") && ((string)x["size"]).Contains(" x "));
            Assert.Contains(bom, x => ((string)x["item"]).StartsWith("Hardened washer"));
            Assert.Contains(bom, x => ((string)x["item"]).StartsWith("Thread insert") && ((string)x["note"]).Contains("STI"));
            Assert.Contains(bom, x => ((string)x["item"]).StartsWith("Dowel pin"));
            var path = MachiningSheet.Write(r, WriteTemplate("mach.html", MachiningTemplate), Data);
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(Path.Combine(_dir, "hardware-bom.csv")));
            Assert.StartsWith("item,size,qty,note", File.ReadAllText(Path.Combine(_dir, "hardware-bom.csv")));
        }

        private string WriteTemplate(string name, string text)
        {
            var p = Path.Combine(Path.GetDirectoryName(_dir), name);
            File.WriteAllText(p, text);
            return p;
        }
    }
}

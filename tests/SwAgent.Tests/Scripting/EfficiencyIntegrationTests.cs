using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Scripting;
using SwAgent.Scripting.Helpers;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests.Scripting
{
    /// <summary>
    /// The three efficiency tracks working together through the real execute_csharp path (ScriptHost with its real, versioned
    /// ScriptOptions; no SolidWorks: Model is null, so the helper calls compile but are not reached): the interop extensions,
    /// the built-in helpers, a helper the agent saves itself, the compile advisor and the cell hints seeing all of them, the
    /// "that worked" nudge, and knowledge.index listing every source.
    /// </summary>
    [Collection("Cells")]
    public class EfficiencyIntegrationTests
    {
        /// <summary>A helper the agent might save after fumbling IBody2.GetFaceCount: an extension method plus a wrap.</summary>
        private const string BodyFacts = @"namespace SwAgent.UserHelpers
{
    public static class BodyFacts
    {
        /// <summary>Faces of a body; 0 for null.</summary>
        public static int FaceCount(this IBody2 body) => body == null ? 0 : body.GetFaceCount();
    }
}";

        private static string Text(ToolResult r) => KnowledgeTestEnv.Text(r);

        [Fact]
        public void Extensions_built_in_helpers_and_a_saved_helper_all_work_in_cells()
        {
            using (var env = new KnowledgeTestEnv())
            {
                // (a) The wrong-interface form from the chats compiles (built-in extension) and runs.
                var a = env.Cell("var f = Model == null ? null : Model.FeatureByName(\"Boss-Extrude1\"); f == null ? \"none\" : f.Name");
                Assert.False(a.IsError, Text(a));
                Assert.Contains("none", Text(a));

                // (b) Built-in helpers compile in a cell (not reached without a document).
                var b = env.Cell(@"if (Model != null)
{
    var body = Bodies.All()[0];
    Bodies.Rotate(body, ""Z"", 90);
    Bodies.Move(body, mm(10), 0, 0);
    Features.Cut(null, Features.ThroughAll);
    Sel.Select(Bodies.Last, 1);
    var v = body.Volume() + Model.Bodies().Count + new object[] { 1.0, 2.0 }.AsDoubles().Length;
}
""helpers compiled""");
                Assert.False(b.IsError, Text(b));
                Assert.Contains("helpers compiled", Text(b));

                // (c) Before the agent saves its helper, the extension form doesn't compile.
                const string useIt = "IBody2 body = null; body.FaceCount()";
                var before = env.Cell(useIt);
                Assert.True(before.IsError);
                Assert.Contains("CS1061", Text(before));

                // ... the agent saves a helper with an extension method (save_helper compiles it alone and with the others) ...
                var saved = env.Call("save_helper", new JObject
                {
                    ["name"] = "BodyFacts",
                    ["code"] = BodyFacts,
                    ["summary"] = "Faces of a body (0 for null)",
                    ["wraps"] = new JArray("IBody2.GetFaceCount"),
                });
                Assert.False(saved.IsError, Text(saved));
                Assert.Contains("IBody2.FaceCount(): int", Text(saved));

                // ... and from the next cell on the same form compiles and runs, with the built-in helpers still there.
                var after = env.Cell(useIt + "; Model == null ? \"no doc\" : Bodies.All().Count.ToString()");
                Assert.False(after.IsError, Text(after));
                Assert.Contains("no doc", Text(after));
                Assert.Contains("That worked after 1 failed try (CS1061)", Text(after)); // the nudge, after the failed cell above

                // A raw call the saved helper wraps: the result names it, ranked before any built-in helper.
                var raw = env.Cell("IBody2 body = null; body == null ? 0 : body.GetFaceCount()");
                Assert.False(raw.IsError, Text(raw));
                Assert.Contains("Helper: BodyFacts [user] does this in one call (instead of GetFaceCount)", Text(raw));
                // A cell that worked hears about a helper once per chat.
                Assert.DoesNotContain("Helper: BodyFacts", Text(env.Cell("IBody2 body = null; body == null ? 1 : body.GetFaceCount()")));

                // A compile error on that raw call: the advisor points at the saved helper (once, not again as a cell hint).
                var wrong = env.Cell("IBody2 body = null; body.GetFaceCount(1)");
                Assert.True(wrong.IsError);
                var wrongText = Text(wrong);
                Assert.Contains("--- hints ---", wrongText);
                Assert.Contains("BodyFacts does this in one checked call", wrongText);
                Assert.Contains("(user)", wrongText);
                Assert.DoesNotContain("Helper: BodyFacts", wrongText);
            }
        }

        [Fact]
        public void A_compile_error_on_a_call_a_built_in_helper_wraps_names_that_helper()
        {
            using (var env = new KnowledgeTestEnv())
            {
                // FeatureCut4 with the wrong number of arguments (the chats wrote its 27 by hand).
                var r = env.Cell("var fm = Model.FeatureManager; fm.FeatureCut4(true, false, false)");
                Assert.True(r.IsError);
                var text = Text(r);
                Assert.Contains("CS7036", text);
                Assert.Contains("Features.Cut does this in one checked call", text);
            }
        }

        [Fact]
        public void Knowledge_index_lists_the_agents_own_helpers_first_then_plugins_then_built_ins()
        {
            using (var env = new KnowledgeTestEnv())
            {
                Assert.False(env.Call("save_helper", new JObject { ["name"] = "BodyFacts", ["code"] = BodyFacts, ["summary"] = "Faces of a body", ["wraps"] = new JArray("IBody2.GetFaceCount") }).IsError);
                var index = KnowledgeIndex.Build(env.Helpers);
                var helpers = ((JArray)index["helpers"]).Select(h => ((string)h["name"], (string)h["source"])).ToList();
                Assert.Equal(("BodyFacts", "user"), helpers[0]);
                var builtIns = HelperCatalog.All.Select(h => h.Name).ToList();
                var builtInChars = ((JArray)index["helpers"]).Skip(1).Sum(h => ((string)h["line"]).Length + 3);
                Assert.True(helpers.Count == builtIns.Count + 1, $"{helpers.Count - 1} of {builtIns.Count} built-in helpers fit ({builtInChars} chars)");
                Assert.Equal(builtIns, helpers.Skip(1).Select(h => h.Item1).ToList()); // every built-in helper fits
                Assert.Equal(0, (int)index["omitted"]["helpers"]);
                var chars = ((JArray)index["helpers"]).Sum(h => ((string)h["line"]).Length + 3);
                Assert.True(chars <= KnowledgeIndex.MaxChars, chars.ToString());
                Assert.Contains((JArray)index["helpers"], h => ((string)h["line"]).StartsWith("Bodies.Rotate(IBody2 body, string axis, double degrees"));
            }

            // The merge order everywhere (advisor, hints, list_helpers, the index): yours, then plugins', then built-in; one per name.
            var merged = HelperIndex.Merge(new[]
            {
                new HelperInfo("PlugMath", "PlugMath.Add(int a, int b): int", "adds", null, "plugin:mathy"),
                new HelperInfo("Mine", "Mine.Do(): int", "does", null, "user"),
            });
            Assert.Equal("Mine", merged[0].Name);
            Assert.Equal("PlugMath", merged[1].Name);
            Assert.Equal("built-in", merged[2].Source);
            Assert.Equal(HelperCatalog.All.Count + 2, merged.Count);
        }

        [Fact]
        public void Built_in_helper_classes_are_reserved_but_extension_receivers_are_not()
        {
            Assert.NotEmpty(HelperIndex.BuiltInNamed("Bodies"));
            Assert.NotEmpty(HelperIndex.BuiltInNamed("Features"));
            Assert.NotEmpty(HelperIndex.BuiltInNamed("Sel"));
            Assert.Empty(HelperIndex.BuiltInNamed("body")); // "body.Volume" shows a placeholder receiver, not a class
            Assert.Empty(HelperIndex.BuiltInNamed("x"));
            var reserved = UserHelpers.ReservedNames();
            Assert.Contains("Bodies", reserved.Keys);
            Assert.DoesNotContain("x", reserved.Keys);

            using (var env = new KnowledgeTestEnv())
            {
                var taken = env.Call("save_helper", new JObject { ["name"] = "Sel", ["code"] = BodyFacts.Replace("BodyFacts", "Sel"), ["summary"] = "x" });
                Assert.True(taken.IsError);
                Assert.Contains("built-in", Text(taken));

                // An extension the built-in SwAgent.Scripting.Extensions already has would make every call ambiguous: refused.
                const string dup = @"namespace SwAgent.UserHelpers
{
    public static class Volumes
    {
        public static double Volume(this IBody2 body) => 0;
    }
}";
                var clash = env.Call("save_helper", new JObject { ["name"] = "Volumes", ["code"] = dup, ["summary"] = "x" });
                Assert.True(clash.IsError, Text(clash));
            }
        }
    }
}

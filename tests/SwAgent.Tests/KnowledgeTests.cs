using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Plugins;
using SwAgent.Scripting;
using SwAgent.Tests.Plugins;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// Cells run on one static crash guard (CrashGuard's step depth) and the built-in helper index is static: these tests run
    /// one at a time, with the crash guard's own tests.
    /// </summary>
    [CollectionDefinition("Cells", DisableParallelization = true)]
    public class CellsCollection
    {
    }

    /// <summary>A throw-away knowledge store, helpers bin and script host under the test data folder (SWAGENT_DATA_DIR).</summary>
    internal sealed class KnowledgeTestEnv : IDisposable
    {
        public readonly string Dir;
        public readonly KnowledgeStore Store;
        public readonly UserHelpers Helpers;
        public readonly ToolRegistry Registry = new ToolRegistry();
        public readonly ScriptHost Host;
        public readonly KnowledgeTools Tools;
        public readonly ToolContext Ctx;

        public KnowledgeTestEnv(Func<PluginManager> plugins = null)
        {
            AssemblyResolver.Install();
            Dir = Path.Combine(Paths.LocalDir, "knowledge-tests", Guid.NewGuid().ToString("N").Substring(0, 10));
            Store = new KnowledgeStore(Path.Combine(Dir, "agent-knowledge"));
            Helpers = new UserHelpers(Store, plugins ?? (() => null), Path.Combine(Dir, "helpers-bin"));
            Host = new ScriptHost(Registry, Helpers);
            Tools = new KnowledgeTools(Host);
            Registry.RegisterAll(Host);
            Registry.RegisterAll(Tools);
            Ctx = new ToolContext(null, null, "thread-" + Guid.NewGuid().ToString("N").Substring(0, 6), Registry);
        }

        public ToolResult Cell(string code) =>
            Host.Execute(Ctx, new ScriptHost.ExecuteInput { Title = "test", Kind = "check", Code = code }).GetAwaiter().GetResult();

        public ToolResult Call(string tool, JObject input) => Registry.InvokeAsync(tool, input, Ctx).GetAwaiter().GetResult();

        public static string Text(ToolResult r) => string.Join("\n", r.Content.Where(c => (string)c["type"] == "text").Select(c => (string)c["text"]));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, true);
            }
            catch
            {
                // a loaded helpers assembly stays locked until the test process ends
            }
        }
    }

    [Collection("Cells")]
    public class KnowledgeTests
    {
        private const string Calc = @"namespace SwAgent.UserHelpers
{
    public static class Calc
    {
        /// <summary>Doubles a number.</summary>
        public static int Twice(int x) => x * 2;
    }
}";

        private const string Broken = @"namespace SwAgent.UserHelpers
{
    public static class Broken
    {
        public static int Nope() => undefinedThing;
    }
}";

        /// <summary>The wrong-interface call from the user's chats: FeatureByName is on IPartDoc, not IModelDoc2.</summary>
        private const string FeatureLookup = @"namespace SwAgent.UserHelpers
{
    public static class FeatureLookup
    {
        public static IFeature FeatureByName(this IModelDoc2 model, string name) =>
            model is IPartDoc part ? part.FeatureByName(name) as IFeature
            : model is IAssemblyDoc asm ? asm.FeatureByName(name) as IFeature
            : null;
    }
}";

        [Fact]
        public void Notes_round_trip_update_by_topic_and_count_hits()
        {
            using (var env = new KnowledgeTestEnv())
            {
                var (n1, created) = env.Store.SaveNote("FeatureByName is on IPartDoc", "Model.FeatureByName -> CS1061", "((IPartDoc)Model).FeatureByName(name)", new[] { "IPartDoc.FeatureByName" });
                Assert.True(created);
                Assert.Equal("n1", n1.Id);
                var (n2, _) = env.Store.SaveNote("SelectData type", "new SelectData() -> CS1503", "Model.SelectionManager.CreateSelectData() as SelectData", new[] { "ISelectionMgr.CreateSelectData" });
                Assert.Equal("n2", n2.Id);

                // Same topic (any case): updated, not duplicated.
                var (again, createdAgain) = env.Store.SaveNote("featurebyname is on ipartdoc", "x", "IPartDoc.FeatureByName (assemblies: IAssemblyDoc.FeatureByName)", null);
                Assert.False(createdAgain);
                Assert.Equal("n1", again.Id);
                Assert.Equal(new[] { "IPartDoc.FeatureByName" }, again.Apis); // kept when none given

                // Read back from disk by a new store on the same folder.
                var reread = new KnowledgeStore(env.Store.Root);
                Assert.Equal(2, reread.Notes().Count);
                Assert.Equal("IPartDoc.FeatureByName (assemblies: IAssemblyDoc.FeatureByName)", reread.Notes().First(n => n.Id == "n1").Right);
                Assert.True(File.Exists(Path.Combine(env.Store.Root, "notes.json")));
                Assert.StartsWith(Paths.LocalDir, env.Store.Root); // under SWAGENT_DATA_DIR, never the user's folder

                Assert.Single(env.Store.FindNotes("selectdata"));
                env.Store.Hit(new[] { "n2" });
                Assert.Equal("n2", env.Store.FindNotes(null).First().Id); // most hits first
                Assert.Single(env.Store.NotesForMembers(new System.Collections.Generic.HashSet<string> { "FeatureByName" }));
                Assert.True(env.Store.DeleteNote("n2"));
                Assert.Single(env.Store.Notes());
            }
        }

        [Fact]
        public void Helper_records_keep_versions_and_two_crashes_disable_a_helper()
        {
            using (var env = new KnowledgeTestEnv())
            {
                env.Store.SaveHelper("Calc", Calc, "doubles", new[] { "Math.Pow" }, "Calc.Twice(int x): int");
                env.Store.SaveHelper("Calc", Calc.Replace("x * 2", "x + x"), "doubles", null, "Calc.Twice(int x): int");
                var r = env.Store.Helper("Calc");
                Assert.Equal(2, r.Version);
                Assert.True(File.Exists(Path.Combine(env.Store.HelpersDir, "Calc.v1.cs")));
                Assert.Contains("x + x", env.Store.HelperCode("Calc"));
                Assert.True(File.Exists(env.Store.HelpersFile));

                var note = new JObject { ["kind"] = "cell", ["title"] = "Cut the pocket", ["at"] = "2026-10-22T10:00", ["helpers"] = new JArray("Calc") };
                HelperCrashes.Record(note, env.Store);
                Assert.Null(note["helpersDisabled"]);
                Assert.False(env.Store.Helper("Calc").Disabled);
                HelperCrashes.Record(note, env.Store);
                Assert.True(env.Store.Helper("Calc").Disabled);
                Assert.Equal("Calc", (string)note["helpersDisabled"][0]["name"]);
                Assert.Contains("Cut the pocket", env.Store.Helper("Calc").DisabledReason);
                Assert.Empty(env.Store.EnabledUserHelpers());

                // Saving a fixed version enables it again.
                env.Store.SaveHelper("Calc", Calc, "doubles", null, null);
                Assert.False(env.Store.Helper("Calc").Disabled);
                Assert.Equal(0, env.Store.Helper("Calc").Crashes);

                Assert.True(env.Store.DeleteHelper("Calc"));
                Assert.Null(env.Store.Helper("Calc"));
                Assert.True(File.Exists(Path.Combine(env.Store.HelpersDir, "Calc.v3.cs"))); // the last version is kept
            }
        }

        [Fact]
        public void A_bad_helper_is_skipped_and_the_good_one_is_usable_from_a_cell()
        {
            using (var env = new KnowledgeTestEnv())
            {
                env.Store.SaveHelper("Calc", Calc, "doubles a number", null, null);
                env.Store.SaveHelper("Broken", Broken, "never builds", null, null);
                var build = env.Helpers.Current();
                Assert.NotNull(build.Find("Calc"));
                Assert.Null(build.Find("Broken"));
                Assert.Contains("undefinedThing", build.Skipped["Broken"]);
                Assert.Contains("undefinedThing", env.Store.Helper("Broken").LastError); // the record says why
                Assert.Null(env.Store.Helper("Calc").LastError);
                Assert.Equal("Calc.Twice(int x): int", build.Find("Calc").Signature);
                Assert.True(File.Exists(build.DllPath));
                Assert.StartsWith(Path.Combine(env.Dir, "helpers-bin"), build.DllPath);

                var result = env.Cell("Calc.Twice(21)");
                Assert.False(result.IsError, KnowledgeTestEnv.Text(result));
                Assert.Contains("42", KnowledgeTestEnv.Text(result));
                Assert.Equal(1, env.Store.Helper("Calc").Uses);
            }
        }

        [Fact]
        public void An_extension_method_helper_makes_the_wrong_interface_call_compile_and_work()
        {
            using (var env = new KnowledgeTestEnv())
            {
                const string cell = "IModelDoc2 doc = Model; var f = doc.FeatureByName(\"Boss-Extrude1\"); f == null ? \"no feature\" : \"found\"";
                var before = env.Host.CompilePreviewAsync(cell).GetAwaiter().GetResult();
                Assert.Contains("CS1061", before.Errors);

                var saved = env.Call("save_helper", new JObject
                {
                    ["name"] = "FeatureLookup",
                    ["code"] = FeatureLookup,
                    ["summary"] = "Model.FeatureByName for parts and assemblies",
                    ["wraps"] = new JArray("IPartDoc.FeatureByName"),
                });
                Assert.False(saved.IsError, KnowledgeTestEnv.Text(saved));
                Assert.Contains("IModelDoc2.FeatureByName(string name): IFeature", KnowledgeTestEnv.Text(saved));

                var after = env.Host.CompilePreviewAsync(cell).GetAwaiter().GetResult();
                Assert.Null(after.Errors);
                var run = env.Cell(cell); // no document in the test: the helper returns null instead of throwing
                Assert.False(run.IsError, KnowledgeTestEnv.Text(run));
                Assert.Contains("no feature", KnowledgeTestEnv.Text(run));
            }
        }

        [Fact]
        public void Save_helper_refuses_code_that_does_not_compile_and_taken_names()
        {
            var builtIn = HelperIndex.BuiltIn;
            HelperIndex.BuiltIn = new HelperIndex.Fixed(new[] { new HelperInfo("Bodies.Rotate", "Bodies.Rotate(IBody2 body, double[] axis, double deg): IBody2", "rotate a body", new[] { "IModelDocExtension.InsertMoveCopyBody2" }) });
            try
            {
                using (var env = new KnowledgeTestEnv())
                {
                    var bad = env.Call("save_helper", new JObject { ["name"] = "Broken", ["code"] = Broken, ["summary"] = "x" });
                    Assert.True(bad.IsError);
                    Assert.Contains("CS0103", KnowledgeTestEnv.Text(bad));
                    Assert.Null(env.Store.Helper("Broken")); // nothing saved
                    Assert.False(File.Exists(env.Store.HelperPath("Broken")));

                    var builtInName = env.Call("save_helper", new JObject { ["name"] = "Bodies", ["code"] = Calc.Replace("Calc", "Bodies"), ["summary"] = "x" });
                    Assert.True(builtInName.IsError);
                    Assert.Contains("built-in", KnowledgeTestEnv.Text(builtInName));

                    var globalName = env.Call("save_helper", new JObject { ["name"] = "Model", ["code"] = Calc.Replace("Calc", "Model"), ["summary"] = "x" });
                    Assert.True(globalName.IsError);

                    var clash = env.Call("save_helper", new JObject { ["name"] = "Math", ["code"] = Calc.Replace("Calc", "Math"), ["summary"] = "x" });
                    Assert.True(clash.IsError);
                    Assert.Contains("System.Math", KnowledgeTestEnv.Text(clash));

                    var noClass = env.Call("save_helper", new JObject { ["name"] = "Other", ["code"] = Calc, ["summary"] = "x" });
                    Assert.True(noClass.IsError);
                    Assert.Contains("public static class Other", KnowledgeTestEnv.Text(noClass));

                    // Two helpers with the same extension method would make every call ambiguous: the second is refused.
                    Assert.False(env.Call("save_helper", new JObject { ["name"] = "FeatureLookup", ["code"] = FeatureLookup, ["summary"] = "x" }).IsError);
                    var twice = env.Call("save_helper", new JObject { ["name"] = "FeatureLookup2", ["code"] = FeatureLookup.Replace("class FeatureLookup", "class FeatureLookup2"), ["summary"] = "x" });
                    Assert.True(twice.IsError);
                    Assert.Contains("ambiguous", KnowledgeTestEnv.Text(twice));

                    // A new version keeps the old file; delete_helper refuses what isn't the user's.
                    Assert.False(env.Call("save_helper", new JObject { ["name"] = "FeatureLookup", ["code"] = FeatureLookup + "\n// v2", ["summary"] = "x" }).IsError);
                    Assert.True(File.Exists(Path.Combine(env.Store.HelpersDir, "FeatureLookup.v1.cs")));
                    Assert.True(env.Call("delete_helper", new JObject { ["name"] = "Bodies" }).IsError);
                    Assert.False(env.Call("delete_helper", new JObject { ["name"] = "FeatureLookup" }).IsError);
                }
            }
            finally
            {
                HelperIndex.BuiltIn = builtIn;
            }
        }

        [Fact]
        public void A_cell_that_works_after_failed_ones_gets_the_nudge_once()
        {
            using (var env = new KnowledgeTestEnv())
            {
                var fail = env.Cell("Model.FeatureByName(\"x\")");
                Assert.True(fail.IsError);
                Assert.Contains("CS1061", KnowledgeTestEnv.Text(fail));
                var fail2 = env.Cell("throw new InvalidOperationException(\"nope\");");
                Assert.True(fail2.IsError);

                var ok = env.Cell("1 + 1");
                Assert.False(ok.IsError);
                var text = KnowledgeTestEnv.Text(ok);
                Assert.Contains("That worked after 2 failed tries (CS1061, InvalidOperationException)", text);
                Assert.Contains("save_api_note", text);
                Assert.Contains("save_helper", text);

                Assert.DoesNotContain("That worked", KnowledgeTestEnv.Text(env.Cell("2 + 2"))); // once per recovery

                // Only the last 4 cells count: five failures, then one that works, is "3 failed tries".
                for (var i = 0; i < 5; i++) env.Cell("Model.Nope()");
                Assert.Contains("That worked after 3 failed tries (CS1061)", KnowledgeTestEnv.Text(env.Cell("3")));

                // Tool macros (and test_helper cells) don't count: a macro that works after a failed cell gets no nudge.
                env.Cell("Model.Nope()");
                var macro = env.Host.RunMacroAsync(env.Ctx, "macro", "1", null).GetAwaiter().GetResult();
                Assert.DoesNotContain("That worked", KnowledgeTestEnv.Text(macro));
                Assert.Equal("That worked after 1 failed try. Make it permanent: save_api_note(what was wrong, what works) and, if the pattern can recur, " +
                             "save_helper with a robust helper (an extension method can make the failing form work too).", CellCoach.Nudge(1, null));
            }
        }

        [Fact]
        public void A_raw_call_a_helper_wraps_gets_a_hint_and_a_failing_one_its_api_note()
        {
            var builtIn = HelperIndex.BuiltIn;
            HelperIndex.BuiltIn = new HelperIndex.Fixed(new[] { new HelperInfo("Bodies.Rotate", "Bodies.Rotate(IBody2 body, double[] axis, double deg): IBody2", "rotate a body", new[] { "IModelDocExtension.InsertMoveCopyBody2" }) });
            try
            {
                using (var env = new KnowledgeTestEnv())
                {
                    env.Store.SaveNote("Move/copy body rotation sign", "positive angle about +Z turned clockwise", "angles are right-handed about the axis", new[] { "IModelDocExtension.InsertMoveCopyBody2" });
                    var r = env.Cell("Model.Extension.InsertMoveCopyBody2(1, 2)");
                    var text = KnowledgeTestEnv.Text(r);
                    Assert.True(r.IsError);
                    Assert.Contains("Helper: Bodies.Rotate does this in one call (instead of InsertMoveCopyBody2): Bodies.Rotate(IBody2 body, double[] axis, double deg): IBody2", text);
                    Assert.Contains("API note n1 (Move/copy body rotation sign): angles are right-handed about the axis", text);
                    Assert.Equal(1, env.Store.Notes()[0].Hits);
                }
            }
            finally
            {
                HelperIndex.BuiltIn = builtIn;
            }
        }

        [Fact]
        public void Options_are_rebuilt_when_helpers_change_and_variables_carry_over()
        {
            using (var env = new KnowledgeTestEnv())
            {
                var (before, b0) = env.Host.CurrentOptions();
                Assert.Null(b0.DllPath);
                Assert.DoesNotContain(UserHelpers.Namespace, before.Imports);
                Assert.Same(before, env.Host.CurrentOptions().Options); // unchanged: the same options

                Assert.False(env.Cell("var keep = 20; var list = new List<int> { 1, 2 }; class Local { } var local = new Local();").IsError);

                env.Store.SaveHelper("Calc", Calc, "doubles", null, null);
                var (after, b1) = env.Host.CurrentOptions();
                Assert.NotEqual(b0.Version, b1.Version);
                Assert.Contains(UserHelpers.Namespace, after.Imports);
                Assert.Contains(after.MetadataReferences.OfType<PortableExecutableReference>(), r => r.FilePath == b1.DllPath);

                var r = env.Cell("Calc.Twice(keep + list.Count)");
                var text = KnowledgeTestEnv.Text(r);
                Assert.False(r.IsError, text);
                Assert.Contains("44", text);
                Assert.Contains("Variables carried over: keep, list", text);
                Assert.Contains("Not carried (their type was declared in a cell): local", text);

                // A second version: a new assembly, and the cells use it (the old one isn't referenced any more).
                env.Store.SaveHelper("Calc", Calc.Replace("x * 2", "x * 3"), "triples", null, null);
                var b2 = env.Host.CurrentOptions().Helpers;
                Assert.NotEqual(b1.Version, b2.Version);
                Assert.NotEqual(b1.DllPath, b2.DllPath);
                var r2 = env.Cell("Calc.Twice(keep)");
                Assert.False(r2.IsError, KnowledgeTestEnv.Text(r2));
                Assert.Contains("60", KnowledgeTestEnv.Text(r2));
            }
        }

        [Fact]
        public void The_knowledge_index_lists_helpers_and_notes_within_its_budget()
        {
            var builtIn = HelperIndex.BuiltIn;
            HelperIndex.BuiltIn = new HelperIndex.Fixed(new[] { new HelperInfo("Bodies.Rotate", "Bodies.Rotate(IBody2 body, double[] axis, double deg): IBody2", "rotate a body", new[] { "InsertMoveCopyBody2" }) });
            try
            {
                using (var env = new KnowledgeTestEnv())
                {
                    env.Store.SaveHelper("Calc", Calc, "doubles a number", null, null);
                    for (var i = 0; i < 60; i++) env.Store.SaveNote("topic " + i, new string('w', 200), "right " + i + " " + new string('r', 200), new[] { "IFoo.Bar" + i });
                    env.Store.Hit(new[] { "n42" });
                    env.Store.UpdateHelpers(get => { });

                    var index = KnowledgeIndex.Build(env.Helpers);
                    Assert.Equal(new[] { "helpers", "disabled", "notes", "omitted" }, index.Properties().Select(p => p.Name));
                    var helpers = (JArray)index["helpers"];
                    Assert.Equal("Bodies.Rotate", (string)helpers[0]["name"]);
                    Assert.Equal("built-in", (string)helpers[0]["source"]);
                    Assert.Equal("Calc", (string)helpers[1]["name"]);
                    Assert.Equal("user", (string)helpers[1]["source"]);
                    Assert.Equal("Calc.Twice(int x): int - doubles a number", (string)helpers[1]["line"]);
                    var notes = (JArray)index["notes"];
                    Assert.Equal("n42", (string)notes[0]["id"]); // most hits first
                    foreach (var key in new[] { "id", "topic", "right", "wrong", "apis", "hits", "line" }) Assert.NotNull(notes[0][key]);
                    var chars = helpers.Concat(notes).Sum(x => ((string)x["line"]).Length + 3);
                    Assert.True(chars <= KnowledgeIndex.MaxChars, chars.ToString());
                    Assert.True((int)index["omitted"]["notes"] > 0);
                    Assert.Equal(0, (int)index["omitted"]["helpers"]);
                    Assert.Empty((JArray)index["disabled"]);
                }
            }
            finally
            {
                HelperIndex.BuiltIn = builtIn;
            }
        }

        [Fact]
        public void A_cell_records_the_helpers_it_calls_for_the_crash_guard()
        {
            using (var env = new KnowledgeTestEnv())
            {
                env.Store.SaveHelper("Calc", Calc, "doubles", null, null);
                CrashGuard.Recover();
                // The cell "crashes" SolidWorks: the guard's note survives, naming the helper.
                using (var outer = CrashGuard.Enter("tool", null, "test_helper", "{}"))
                {
                    Assert.False(env.Cell("Calc.Twice(1)").IsError);
                    // The cell's own step is nested (it keeps the outer note), but the helpers it called were added to it.
                    var note = JObject.Parse(File.ReadAllText(Path.Combine(Paths.LocalDir, "crash-guard.json")));
                    Assert.Equal("tool", (string)note["kind"]);
                    Assert.Equal(new[] { "Calc" }, ((JArray)note["helpers"]).Select(x => (string)x));
                }
                Assert.Null(CrashGuard.Recover());
            }
        }

        [Fact]
        public void Test_helper_runs_a_cell_and_records_the_result()
        {
            using (var env = new KnowledgeTestEnv())
            {
                Assert.False(env.Call("save_helper", new JObject { ["name"] = "Calc", ["code"] = Calc, ["summary"] = "doubles" }).IsError);
                var ok = env.Call("test_helper", new JObject { ["name"] = "Calc", ["code"] = "if (Calc.Twice(2) != 4) throw new Exception(\"wrong\"); \"fine\"" });
                Assert.False(ok.IsError, KnowledgeTestEnv.Text(ok));
                Assert.StartsWith("ok", env.Store.Helper("Calc").LastTest);
                var bad = env.Call("test_helper", new JObject { ["name"] = "Calc", ["code"] = "if (Calc.Twice(2) != 5) throw new Exception(\"wrong\");" });
                Assert.True(bad.IsError);
                Assert.StartsWith("failed", env.Store.Helper("Calc").LastTest);
                Assert.True(env.Call("test_helper", new JObject { ["name"] = "Nothing", ["code"] = "1" }).IsError);

                var list = KnowledgeTestEnv.Text(env.Call("list_helpers", new JObject()));
                Assert.Contains("[user] Calc.Twice(int x): int - doubles", list);
                var read = KnowledgeTestEnv.Text(env.Call("read_helper", new JObject { ["name"] = "Calc" }));
                Assert.Contains("public static int Twice", read);
                Assert.Contains("last test: failed", read);
            }
        }

        [Fact]
        public void The_knowledge_tools_never_wait_for_solidworks_except_test_helper()
        {
            foreach (var t in new[] { "save_api_note", "list_api_notes", "save_helper", "read_helper", "list_helpers", "delete_helper" })
                Assert.True(SwBusyGate.ModelFree(t), t);
            Assert.False(SwBusyGate.ModelFree("test_helper"));
            Assert.False(SwBusyGate.ModelFree("execute_csharp"));
        }
    }

    [Collection("Plugins")]
    public class PluginHelperTests
    {
        private const string PlugMath = @"// summary: adds numbers the plugin's way
// wraps: IMathUtility.Add
namespace SwAgent.UserHelpers
{
    public static class PlugMath
    {
        public static int Add(int a, int b) => a + b;
    }
}";

        [Fact]
        public void Plugin_helpers_need_the_users_approval_like_tool_code()
        {
            using (var env = new PluginTestEnv())
            using (var k = new KnowledgeTestEnv(() => env.Pm))
            {
                var src = env.AddPlugin("mathy");
                PluginTestEnv.Write(Path.Combine(src, "helpers", "PlugMath.cs"), PlugMath);
                env.Pm.AddMarketplace(env.Market);

                var m = PluginManifest.Load(src);
                Assert.True(m.HasToolCode); // helpers are code: they count in the trust hash
                Assert.Contains("mathy helpers for agent code (PlugMath.cs)", m.ToolCodeSummary());

                var ask = env.Pm.Install("mathy@test-market");
                Assert.False(ask.Ok);
                Assert.NotNull(ask.NeedsTrust);
                Assert.True(env.Pm.Install("mathy@test-market", ask.Hash).Ok);

                k.Helpers.Invalidate();
                var built = k.Helpers.Current().Find("PlugMath");
                Assert.NotNull(built);
                Assert.Equal("plugin:mathy", built.Source);
                Assert.Equal("adds numbers the plugin's way", built.Summary);
                Assert.Equal(new[] { "IMathUtility.Add" }, built.Wraps);
                var r = k.Cell("PlugMath.Add(2, 3)");
                Assert.False(r.IsError, KnowledgeTestEnv.Text(r));
                Assert.Contains("5", KnowledgeTestEnv.Text(r));

                // The user's helper can't take a plugin's name, nor delete it.
                var taken = k.Call("save_helper", new JObject { ["name"] = "PlugMath", ["code"] = PlugMath, ["summary"] = "x" });
                Assert.True(taken.IsError);
                Assert.Contains("plugin:mathy", KnowledgeTestEnv.Text(taken));
                Assert.True(k.Call("delete_helper", new JObject { ["name"] = "PlugMath" }).IsError);

                // Changed since the approval (a bad update): not compiled any more.
                File.AppendAllText(Path.Combine(env.Pm.Find("mathy").Root, "helpers", "PlugMath.cs"), "\n// and something else\n");
                env.Pm.Invalidate();
                Assert.False(env.Pm.IsTrusted(env.Pm.Find("mathy")));
                Assert.Null(k.Helpers.Current().Find("PlugMath"));
            }
        }
    }
}

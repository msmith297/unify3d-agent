using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Scripting;
using SwAgent.Scripting.Helpers;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests.Scripting
{
    /// <summary>
    /// Regressions for the agent-efficiency review (2026-10-22), headless: the wrong-interface hints name a path that works at
    /// run time, the selection extensions stop wrong receivers at compile time, the array hints name the element type, the
    /// catalog signatures compile as written, the degrees guard, the "that worked" nudge only for API lessons, the knowledge
    /// index's size and composition of the agent's helpers with the built-in ones.
    /// </summary>
    [Collection("Cells")]
    public class EfficiencyReviewTests
    {
        private static IReadOnlyList<string> HintsFor(string code)
        {
            var (errors, compilation) = ScriptCompile.Compile(code);
            Assert.True(errors.Count > 0, "expected a compile error for: " + code);
            return CompileAdvisor.Hints(errors, compilation);
        }

        private static string Text(ToolResult r) => KnowledgeTestEnv.Text(r);

        // ---------- members on another interface: the path that works, compiled ----------

        /// <summary>
        /// The document's sub-objects (Extension, FeatureManager, ...) are separate COM objects: a cast of Model to them compiles
        /// and throws InvalidCastException. The hint names the accessor, and the cell written from the hint compiles.
        /// </summary>
        [Theory]
        [InlineData("Model", "DeleteSelection2", "0", "Model.Extension.DeleteSelection2")]
        [InlineData("Model", "InsertMoveCopyBody2", "0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 1", "Model.FeatureManager.InsertMoveCopyBody2")]
        [InlineData("Model.Extension", "InsertMoveCopyBody2", "0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 1", "Model.FeatureManager.InsertMoveCopyBody2")]
        [InlineData("Model.FeatureManager", "DeleteSelection2", "0", "Model.Extension.DeleteSelection2")]
        [InlineData("Model.Extension", "EditRebuild3", "", "Model.EditRebuild3")]
        [InlineData("Model", "GetBodies2", "0, false", "((IPartDoc)Model).GetBodies2")]
        [InlineData("Model", "CreateCornerRectangle", "0, 0, 0, 0.01, 0.01, 0", "Model.SketchManager.CreateCornerRectangle")]
        [InlineData("feat", "GetSketchSegments", "", "((ISketch)feat.GetSpecificFeature2()).GetSketchSegments")]
        [InlineData("seg", "GetConstrainedStatus", "", "((ISketch)seg.GetSketch()).GetConstrainedStatus")]
        [InlineData("fm", "DeleteSelection2", "0", "fm.Document.Extension.DeleteSelection2")]
        public void Wrong_interface_hints_name_a_path_that_compiles(string receiver, string member, string args, string expected)
        {
            const string decls = "IFeature feat = null; ISketchSegment seg = null; IFeatureManager fm = null; ";
            var hints = HintsFor(decls + $"var r = {receiver}.{member}({args});");
            var hint = hints.FirstOrDefault(h => h.StartsWith(member + " is on ", StringComparison.Ordinal));
            Assert.True(hint != null, string.Join("\n", hints));
            Assert.Contains(expected + "(...)", hint);
            Assert.DoesNotContain("((IFeatureManager)", hint);
            Assert.DoesNotContain("((IModelDocExtension)", hint);
            var errors = ScriptCompile.Errors(decls + $"var r = {expected}({args});");
            Assert.True(errors.Count == 0, hint + "\n" + ScriptCompile.Format(errors));
        }

        [Fact]
        public void A_member_of_an_unrelated_object_gets_no_cast()
        {
            var hints = HintsFor("IFeature f = null; var box = f.GetBodyBox();");
            Assert.Contains(hints, h => h.StartsWith("GetBodyBox is on IBody2", StringComparison.Ordinal) && h.Contains("a different object"));
            Assert.DoesNotContain(hints, h => h.Contains("((IBody2)f)"));
            // Same object, other interface: the cast is right.
            Assert.Contains(HintsFor("IModelDoc2 m = Model; var x = m.GetBodies2(0, false);"), h => h.Contains("((IPartDoc)m).GetBodies2(...)"));
            Assert.Equal("((IEntity)face)", CompileAdvisor.Reach("IFace2", "face", "IEntity"));
            Assert.Equal("((IBody2)edge.GetBody())", CompileAdvisor.Reach("IEdge", "edge", "IBody2"));
            Assert.Equal("((IAssemblyDoc)comp.GetModelDoc2())", CompileAdvisor.Reach("IComponent2", "comp", "IAssemblyDoc"));
            Assert.Null(CompileAdvisor.Reach("ISelectionMgr", "sm", "IFeatureManager"));
        }

        // ---------- the selection extensions ----------

        [Fact]
        public void Selection_calls_on_Model_work_and_on_other_objects_stop_at_compile_time()
        {
            foreach (var ok in new[]
                     {
                         "var n = Model.GetSelectedObjectCount2(-1); var o = Model.GetSelectedObject6(1, -1); var t = Model.GetSelectedObjectType3(1, -1); var mk = Model.GetSelectedObjectMark(1);",
                         "var n = Model.SelectionManager.GetSelectedObjectCount2(-1); var o = Model.SelectionManager.GetSelectedObject6(1, -1); SelectData d = Model.SelectionManager.CreateSelectData(2);",
                         "SelectData d = ((SelectionMgr)Model.SelectionManager).CreateSelectData(2); var sm = Model.SelMgr(); SelectData d2 = sm.CreateSelectData(1); var n = sm.GetSelectedObjectCount2(-1);",
                         "ModelDoc2 doc = (ModelDoc2)swApp.ActiveDoc; var n = doc.GetSelectedObjectCount2(-1);",
                     })
            {
                var errors = ScriptCompile.Errors(ok);
                Assert.True(errors.Count == 0, ok + "\n" + ScriptCompile.Format(errors));
            }
            foreach (var wrong in new[] { "IBody2 b = null; var o = b.GetSelectedObject6(1, -1);", "IFeature f = null; var n = f.GetSelectedObjectCount2(-1);", "IFace2 face = null; var d = face.CreateSelectData(1);" })
            {
                var errors = ScriptCompile.Errors(wrong);
                Assert.Contains(errors, e => e.Id == "CS0619" && e.GetMessage().Contains("Model.SelMgr()"));
            }
        }

        // ---------- indexing an object-typed array ----------

        [Theory]
        [InlineData("IBody2 b = null; var fs = b.GetFaces(); var f = (IFace2)fs[0];", "fs.AsObjects<IFace2>()[i]")]
        [InlineData("var f0 = Model.FeatureManager.GetFeatures(true)[0];", ".AsObjects<IFeature>()[i]")]
        [InlineData("IBody2 b = null; var bb = b.GetBodyBox(); var x = bb[0];", "bb.AsDoubles()[i]")]
        [InlineData("object arr = null; IEdge e = (IEdge)arr[0];", "arr.AsObjects<IEdge>()[i]")]
        [InlineData("object arr = null; var e = arr[0];", "AsObjects<T>()[i] for objects with T the element interface")]
        public void Indexing_an_object_array_names_the_element_type(string code, string expected)
        {
            var hints = HintsFor(code);
            Assert.Contains(hints, h => h.Contains(expected));
            Assert.DoesNotContain(hints, h => h.Contains("AsObjects<IBody2>()[i]") && !expected.Contains("IBody2"));
        }

        [Fact]
        public void A_run_time_array_cast_gets_the_converter()
        {
            Assert.Contains("AsDoubles()", ScriptHost.ArrayCastHint(new InvalidCastException("Unable to cast object of type 'System.Object[]' to type 'System.Double[]'.")));
            Assert.Null(ScriptHost.ArrayCastHint(new InvalidCastException("Unable to cast object of type 'System.String' to type 'System.Int32'.")));
            Assert.Null(ScriptHost.ArrayCastHint(new InvalidOperationException("System.Object[]")));
            using (var env = new KnowledgeTestEnv())
            {
                var r = env.Cell("object o = new object[] { 1.0, 2.0, 3.0 }; var d = (double[])o;");
                Assert.True(r.IsError);
                Assert.Contains("x.AsDoubles()", Text(r));
            }
        }

        // ---------- the catalog's signatures ----------

        /// <summary>
        /// Every built-in helper called exactly as its catalog line writes it (a typed default for each parameter): it compiles.
        /// An enum parameter printed as int (Model.Bodies(int type = swBodyType_e.swSolidBody)) would fail here with CS1503.
        /// </summary>
        [Fact]
        public void Every_catalog_signature_compiles_as_a_call()
        {
            const string decls = "IBody2 body = null; IFeature feature = null; object x = null; ISelectionMgr selMgr = null; ";
            var problems = new List<string>();
            foreach (var h in HelperCatalog.All)
            {
                Assert.DoesNotMatch(new Regex(@"\bint \w+ = sw"), h.Signature);
                var m = Regex.Match(h.Signature, @"^(?<name>[\w.]+)(?<generic><T>)?\((?<args>.*)\)( -> .*)?$");
                Assert.True(m.Success, h.Signature);
                var args = m.Groups["args"].Value.Length == 0 ? new string[0] : SplitArgs(m.Groups["args"].Value);
                var call = m.Groups["name"].Value + (m.Groups["generic"].Success ? "<object>" : "") + "(" + string.Join(", ", args.Select(a =>
                {
                    var type = a.Replace("params ", "");
                    type = type.Substring(0, type.LastIndexOf(' ', type.Contains(" = ") ? type.IndexOf(" = ", StringComparison.Ordinal) - 1 : type.Length - 1));
                    return $"default({type})";
                })) + ")";
                var errors = ScriptCompile.Errors(decls + (h.Signature.Contains(" -> ") ? "var r = " : "") + call + ";");
                if (errors.Count > 0) problems.Add(h.Signature + " -> " + call + ": " + ScriptCompile.Format(errors));
            }
            Assert.True(problems.Count == 0, string.Join("\n", problems));
            Assert.Equal("Model.Bodies(swBodyType_e type = swBodyType_e.swSolidBody, bool visibleOnly = false) -> IList<IBody2>", HelperCatalog.Find("Model.Bodies").Signature);
            // The API habit with the int GetBodies2 takes works too.
            Assert.Empty(ScriptCompile.Errors("var a = Model.Bodies(1); var b = Model.Bodies((int)swBodyType_e.swSheetBody, true); var c = Model.Bodies(swBodyType_e.swSheetBody); var d = Model.Bodies();"));
        }

        private static string[] SplitArgs(string s)
        {
            var parts = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '<') depth++;
                else if (s[i] == '>') depth--;
                else if (s[i] == ',' && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start).Trim());
            return parts.ToArray();
        }

        // ---------- degrees, not deg() ----------

        [Fact]
        public void Helpers_refuse_radians_passed_as_degrees()
        {
            foreach (var bad in new[] { Math.PI / 2, -Math.PI / 4, Math.PI / 12, Math.PI, 2 * Math.PI })
                Assert.Contains("not deg(", Assert.Throws<SwHelperException>(() => HelperOps.RequireDegrees(bad, "Bodies.Rotate")).Message);
            foreach (var ok in new[] { 90.0, -45, 1.0, 0.5, 2.5, 7.5, 0 })
                HelperOps.RequireDegrees(ok, "Bodies.Rotate");
        }

        // ---------- the nudge: only for API lessons ----------

        /// <summary>The review's four sequences, through the real execute_csharp path (no SolidWorks: Model is null).</summary>
        [Fact]
        public void The_nudge_comes_only_after_an_api_failure_and_a_fix_that_calls_the_same_members()
        {
            using (var env = new KnowledgeTestEnv())
            {
                // (1) a typo, then the fixed cell: no nudge.
                Assert.True(env.Cell("var a = 1").IsError);
                Assert.DoesNotContain("That worked", Text(env.Cell("var a = 1;")));

                // (2) the cell's own exception, then another cell: no nudge.
                Assert.True(env.Cell("throw new InvalidOperationException(\"hole not found\");").IsError);
                Assert.DoesNotContain("That worked", Text(env.Cell("2 + 2")));

                // (3) a wrong-interface call, then an unrelated cell: no nudge.
                Assert.True(env.Cell("var bodies = Model.GetBodies2(0, false);").IsError);
                Assert.DoesNotContain("That worked", Text(env.Cell("SelectData d = null; d == null")));

                // ... then the fixed form of that call: the nudge, naming the member.
                var fixedCell = Text(env.Cell("var bodies = Model is IPartDoc part ? part.GetBodies2(0, false) : null; bodies == null"));
                Assert.Contains("That worked after 1 failed try (CS1061) with GetBodies2", fixedCell);
                Assert.Contains("If the failure was about the API", fixedCell);

                // (4) a failure, then the lesson saved, then a working cell: no nudge (it is settled).
                Assert.True(env.Cell("var c = Model.GetComponentByName(\"x\");").IsError);
                Assert.False(env.Call("save_api_note", new JObject { ["topic"] = "GetComponentByName is on IAssemblyDoc", ["right"] = "((IAssemblyDoc)Model).GetComponentByName(name)" }).IsError);
                Assert.DoesNotContain("That worked", Text(env.Cell("var c = Model is IAssemblyDoc asm ? asm.GetComponentByName(\"x\") : null; c == null")));
            }

            Assert.True(CellCoach.Learnable("Compilation failed (nothing ran):\n(1,5) CS1503: Argument 1: cannot convert from 'ISelectData' to 'SelectData'", "x"));
            Assert.False(CellCoach.Learnable("Compilation failed (nothing ran):\n(1,10) CS1002: ; expected", "var a = 1"));
            Assert.False(CellCoach.Learnable("Compilation failed (nothing ran):\n(1,1) CS0103: The name 'cavBoxes' does not exist", "cavBoxes = 1;"));
            Assert.True(CellCoach.Learnable("EXCEPTION after 3 ms\n--- exception ---\nSystem.Runtime.InteropServices.COMException: bad", "Model.X();"));
            Assert.False(CellCoach.Learnable("EXCEPTION\n--- exception ---\nSystem.InvalidCastException: mine", "throw new InvalidCastException(\"mine\");"));
            Assert.False(CellCoach.Learnable("EXCEPTION\n--- exception ---\nSystem.InvalidOperationException: x", "Run();"));
        }

        // ---------- the agent's helpers build on the built-in ones ----------

        [Fact]
        public void A_saved_helper_can_extend_Bodies_and_shows_as_cells_call_it()
        {
            const string code = @"namespace SwAgent.UserHelpers
{
    public static class TurnHelpers
    {
        /// <summary>Turn a body in place (about its own centroid).</summary>
        public static IBody2 TurnInPlace(this BodiesApi bodies, IBody2 body, string axis, double degrees) => bodies.Rotate(body, axis, degrees, body.Centroid());
    }
}";
            using (var env = new KnowledgeTestEnv())
            {
                var saved = env.Call("save_helper", new JObject { ["name"] = "TurnHelpers", ["code"] = code, ["summary"] = "turn a body in place, checked by Bodies.Rotate" });
                Assert.False(saved.IsError, Text(saved));
                Assert.Contains("Bodies.TurnInPlace(IBody2 body, string axis, double degrees) -> IBody2", Text(saved));
                var cell = env.Cell("Model == null ? \"no doc\" : Bodies.TurnInPlace(Bodies.All()[0], \"X\", 90).Name");
                Assert.False(cell.IsError, Text(cell));
                Assert.Contains("no doc", Text(cell));
            }
        }

        // ---------- the knowledge index ----------

        [Fact]
        public void The_index_lists_the_extensions_on_one_line_within_its_budget()
        {
            using (var env = new KnowledgeTestEnv())
            {
                var index = KnowledgeIndex.Build(env.Helpers);
                var entries = (JArray)index["helpers"];
                var extensions = entries.Single(h => (string)h["name"] == KnowledgeIndex.ExtensionsEntry);
                foreach (var name in new[] { "Model.FeatureByName(string name)", "Model.Bodies(swBodyType_e type", "x.AsDoubles()", "x.AsObjects<T>()", "body.Volume()" })
                    Assert.Contains(name, (string)extensions["line"]);
                Assert.DoesNotContain(entries, h => ((string)h["name"]).StartsWith("Model.", StringComparison.Ordinal));
                Assert.Equal(0, (int)index["omitted"]["helpers"]);
                var rotate = (string)entries.Single(h => (string)h["name"] == "Bodies.Rotate")["line"];
                Assert.Contains("body.Centroid(): in place", rotate); // the pivot is in the line, not cut off
                Assert.Contains("not deg()", rotate);
                Assert.Contains("Features.ThroughAll", (string)entries.Single(h => (string)h["name"] == "Features.Cut")["line"]);
                Assert.DoesNotContain(entries, h => ((string)h["line"]).EndsWith("...", StringComparison.Ordinal));
                Assert.True(entries.Sum(h => ((string)h["line"]).Length + 3) <= 5000);
            }
        }
    }
}

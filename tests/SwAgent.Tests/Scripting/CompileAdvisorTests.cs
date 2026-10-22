using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using SwAgent.Scripting;
using Xunit;

namespace SwAgent.Tests.Scripting
{
    /// <summary>The advisor's hints on real compile errors of cells (the patterns from the user's chats).</summary>
    public class CompileAdvisorTests
    {
        private static IReadOnlyList<string> HintsFor(string code, IEnumerable<HelperInfo> helpers = null)
        {
            var (errors, compilation) = ScriptCompile.Compile(code);
            Assert.True(errors.Count > 0, "expected a compile error for: " + code);
            return CompileAdvisor.Hints(errors, compilation, helpers);
        }

        private static string All(IEnumerable<string> hints) => string.Join("\n", hints);

        [Fact]
        public void Member_on_another_interface_names_it_with_the_cast()
        {
            var hints = HintsFor("IModelDoc2 m = Model; var x = m.GetBodies2(0, false);");
            Assert.Contains(hints, h => h.StartsWith("GetBodies2 is on IPartDoc", StringComparison.Ordinal) && h.Contains("((IPartDoc)m).GetBodies2(...)"));
        }

        [Fact]
        public void Member_on_a_sibling_interface_is_found()
        {
            var hints = HintsFor("ISketchSegment s = null; var st = s.GetConstrainedStatus();");
            Assert.Contains(hints, h => h.Contains("GetConstrainedStatus is on ISketch"));
        }

        [Fact]
        public void Near_miss_names_are_suggested()
        {
            Assert.Contains(HintsFor("IMathPoint p = null; var q = p.MultiTransform(null);"), h => h.Contains("MultiplyTransform"));
            var trans = HintsFor("IMoveCopyBodyFeatureData d = null; var x = d.TransX;");
            Assert.Contains(trans, h => h.Contains("TransformX"));
            Assert.DoesNotContain(trans, h => h.Contains("ITexture")); // another interface's TransX is no help here
            Assert.Contains(HintsFor("ICombineBodiesFeatureData cb = null; var bodies = cb.Bodies as object[];"), h => h.Contains("BodiesToCombine"));
            Assert.Contains(HintsFor("IRenderMaterial r = null; var a = r.MaterialID1;"), h => h.Contains("GetMaterialIds"));
            Assert.Contains(HintsFor("IIndentFeatureData d = null; var c = d.Cut;"), h => h.Contains("IsCut"));
        }

        [Fact]
        public void Method_groups_get_the_member_hints_or_the_call()
        {
            // An extension named Bodies exists (for IFeature), so the compiler reports a method group, not CS1061.
            Assert.Contains(HintsFor("ICombineBodiesFeatureData cb = null; object x = cb.Bodies;"), h => h.Contains("BodiesToCombine"));
            Assert.Contains(HintsFor("IBody2 b = null; int n = b.GetFaceCount;"), h => h.StartsWith("GetFaceCount is a method: call it, b.GetFaceCount(...)", StringComparison.Ordinal));
        }

        [Fact]
        public void Object_typed_returns_get_the_cast()
        {
            var hints = HintsFor("var mk = Model.SelectionManager.GetSelectedObjectsComponent4(1, -1);");
            Assert.Contains(hints, h => h.Contains("((ISelectionMgr)Model.SelectionManager).GetSelectedObjectsComponent4(...)") && h.Contains("typed object"));
        }

        [Fact]
        public void Interface_versus_class_arguments_get_the_cast()
        {
            var hints = HintsFor("ICallout c = null; Model.Extension.SelectByID2(\"x\", \"BODYFEATURE\", 0, 0, 0, false, 0, c, 0);");
            Assert.Contains(hints, h => h.Contains("Callout and ICallout are the same COM object") && h.Contains("(Callout)c"));
        }

        [Fact]
        public void Object_to_array_conversions_point_at_the_converters()
        {
            Assert.Contains(HintsFor("object o = null; double[] d = o;"), h => h.Contains("AsDoubles()"));
            Assert.Contains(HintsFor("object o = null; IBody2[] b = o;"), h => h.Contains("AsObjects<IBody2>()"));
            Assert.Contains(HintsFor("IBody2 b = null; var v = b.GetMassProperties(0)[3];"), h => h.Contains("AsDoubles()[i]"));
        }

        [Fact]
        public void A_data_object_cast_to_an_array_names_the_plain_sibling()
        {
            var hints = HintsFor("IEdge e = null; var cp = (double[])e.GetCurveParams3();");
            Assert.Contains(hints, h => h.Contains("CurveParamData is an object with properties (StartPoint, EndPoint") && h.Contains("e.GetCurveParams2().AsDoubles()"));
        }

        [Fact]
        public void A_wrapped_call_points_at_the_helper_and_the_real_signature()
        {
            var hints = HintsFor("var cut = Model.FeatureManager.FeatureCut4(true,false,false,0,0,0.01,0,false,false,false,false,0,0,false,false,false,false,false,false,false,false,true,false,false,false,0,0,false,false);");
            Assert.Contains(hints, h => h.StartsWith("Features.Cut does this in one checked call: Features.Cut(", StringComparison.Ordinal));
            Assert.Contains(hints, h => h.Contains("IFeatureManager.FeatureCut4 takes 27 arguments (Sd, Flip, Dir"));
        }

        [Fact]
        public void The_agents_own_helpers_come_first()
        {
            var mine = new HelperInfo
            {
                Name = "MyTurn", Signature = "MyTurn(IBody2 b, double deg) -> IBody2", Summary = "turns", Wraps = new[] { "IFeatureManager.InsertMoveCopyBody2" }, Source = "user",
            };
            var (errors, compilation) = ScriptCompile.Compile("var f = Model.FeatureManager.InsertMoveCopyBody2(0, 0, 0);");
            var old = CompileAdvisor.Extra;
            try
            {
                CompileAdvisor.Extra = new Index(mine);
                var text = CompileAdvisor.Advise(errors, compilation);
                Assert.Contains("--- hints ---", text);
                Assert.Contains("MyTurn does this in one checked call: MyTurn(IBody2 b, double deg) -> IBody2 (user)", text);
                Assert.DoesNotContain("Bodies.Move does this", text);
            }
            finally
            {
                CompileAdvisor.Extra = old;
            }
            // Without it, the built-in helper.
            Assert.Contains(CompileAdvisor.Hints(errors, compilation), h => h.StartsWith("Bodies.", StringComparison.Ordinal));
        }

        private sealed class Index : IHelperIndex
        {
            private readonly HelperInfo[] _all;

            public Index(params HelperInfo[] all)
            {
                _all = all;
            }

            public IEnumerable<HelperInfo> All() => _all;
        }

        [Fact]
        public void Ambiguous_names_and_syntax_slips_get_short_fixes()
        {
            Assert.Contains(HintsFor("var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);"), h => h.Contains("System.Environment"));
            Assert.Contains(HintsFor("IFeature f = null; if (f == null) \"FAILED\"; else { f.Name = \"x\"; }"), h => h.Contains("isn't a statement"));
            Assert.Contains(HintsFor("bool ok = true; var s = $\"{ok ? 1 : 0:F0}\";"), h => h.Contains("{(a ? b : c)}"));
            Assert.Contains(HintsFor("cavBoxes = new List<double[]>();"), h => h.Contains("var cavBoxes = ...;"));
        }

        [Fact]
        public void At_most_three_hints()
        {
            var hints = HintsFor("IMathPoint p = null; p.MultiTransform(null); IModelDoc2 m = Model; m.GetBodies2(0, false); ISketchSegment s = null; s.GetConstrainedStatus(); object o = null; double[] d = o;");
            Assert.Equal(CompileAdvisor.MaxHints, hints.Count);
        }

        [Fact]
        public void Advise_never_throws_and_is_empty_without_anything_useful()
        {
            Assert.Equal("", CompileAdvisor.Advise(new List<Diagnostic>(), null));
            var (errors, _) = ScriptCompile.Compile("var x = ;");
            Assert.NotNull(CompileAdvisor.Advise(errors, null));
            // without the compilation, message-only hints still work
            var (e2, _) = ScriptCompile.Compile("IMathPoint p = null; var q = p.MultiTransform(null);");
            Assert.Contains("MultiplyTransform", CompileAdvisor.Advise(e2, null));
        }

        [Fact]
        public void Similar_ranks_close_names_first()
        {
            Assert.Equal("TransformX", CompileAdvisor.Similar("TransX", new[] { "TransformX", "TransformY", "Copy", "RotationOriginZ" }).First());
            Assert.Empty(CompileAdvisor.Similar("Volume", new[] { "Copy", "Name" }));
        }
    }
}

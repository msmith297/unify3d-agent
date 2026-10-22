using System;
using System.Linq;
using SwAgent.Scripting.Extensions;
using Xunit;

namespace SwAgent.Tests.Scripting
{
    /// <summary>
    /// The obvious forms the agent wrote in the user's chats (and that failed to compile, 83 of 401 cells) now compile with
    /// the real script options; the variant converters behave on plain .NET arrays.
    /// </summary>
    public class ExtensionsTests
    {
        [Theory]
        // CS1061 'IModelDoc2' does not contain 'FeatureByName' (11 times in the chats)
        [InlineData("var f = (IFeature)Model.FeatureByName(\"Body-Move/Copy1\");")]
        [InlineData("var skf = Model.FeatureByName(\"Sketch4\") as IFeature;")]
        [InlineData("((IFeature)Model.FeatureByName(\"Combine1\")).Name = \"Trim\";")]
        // CS1061 'object' does not contain 'CreateSelectData' / 'GetSelectedObject6' (Model.SelectionManager is object)
        [InlineData("var sd = (ISelectData)Model.SelectionManager.CreateSelectData(); sd.Mark = 2;")]
        [InlineData("var fs = (IFeature)Model.SelectionManager.GetSelectedObject6(1, -1);")]
        [InlineData("var n = Model.SelectionManager.GetSelectedObjectCount2(-1); var t = Model.SelectionManager.GetSelectedObjectType3(1, -1); var mk = Model.SelectionManager.GetSelectedObjectMark(1);")]
        // CS1503 ISelectData -> SelectData (10 times)
        [InlineData("var sd = (ISelectData)((ISelectionMgr)Model.SelectionManager).CreateSelectData(); sd.Mark = 1; IBody2 b = null; b.Select2(false, sd);")]
        [InlineData("ISelectData isd = null; IEntity e = null; e.Select4(true, isd); ISketchSegment s = null; s.Select4(true, isd); ISketchPoint p = null; p.Select4(true, isd); IComponent2 c = null; c.Select4(true, isd, false);")]
        [InlineData("IFace2 face = null; face.Select4(true, null); IEdge edge = null; edge.Select4(true, Model.CreateSelectData(1)); IVertex v = null; ISelectData isd = null; v.Select4(true, isd);")]
        // CS1503 IModelDoc2 -> ModelDoc2 in AccessSelections
        [InlineData("IFeature f = null; var d = (IMoveCopyBodyFeatureData)f.GetDefinition(); d.AccessSelections(Model, null); d.ReleaseSelectionAccess();")]
        [InlineData("IFeature f = null; var d = (ISaveBodyFeatureData)f.GetDefinition(); d.AccessSelections(Model, null);")]
        // CS1503 IMathTransform -> MathTransform, ISketchRelation -> SketchRelation
        [InlineData("IBody2 b = null; IMathTransform xf = null; b.ApplyTransform(xf);")]
        [InlineData("ISketch sk = null; ISketchRelation rel0 = null; var ok = sk.RelationManager.DeleteRelation(rel0);")]
        // CS0030 CurveParamData -> double[]; CS0021 indexing object
        [InlineData("IEdge e = null; var cp = e.GetCurveParams3().AsDoubles(); var p2 = e.GetCurveParams2().AsDoubles();")]
        [InlineData("IBody2 b = null; var v = b.GetMassProperties(0).AsDoubles()[3]; var bx = b.GetBodyBox().AsDoubles();")]
        [InlineData("var bodies = ((IPartDoc)Model).GetBodies2(0, false).AsObjects<IBody2>(); var names = bodies.Select(b => b.Name).ToArray();")]
        [InlineData("object o = null; double[] d = o.AsDoubles(); int[] i = o.AsInts(); IBody2[] bs = o.AsObjects<IBody2>(); string[] ss = o.AsStrings();")]
        // CS1061 'IFeature' does not contain 'GetDisplayDimensions'
        [InlineData("IFeature f = null; foreach (var dd in (object[])f.GetDisplayDimensions() ?? new object[0]) { var dm = ((IDisplayDimension)dd).GetDimension2(0); }")]
        // Measuring and listing
        [InlineData("var all = Model.Bodies(); var sheets = Model.Bodies(swBodyType_e.swSheetBody, true); var b0 = Model.Body(\"x\"); double[] box = b0.Box(); double v = b0.Volume(); double[] c = b0.Centroid(); double a = b0.Area();")]
        [InlineData("var feats = Model.Features(); var lf = Model.LastFeature(); var fb = lf.Bodies(); var sm = Model.SelMgr(); SelectData s1 = sm.CreateSelectData(2); SelectData s2 = Model.CreateSelectData(1);")]
        public void Evidence_forms_compile(string code)
        {
            var errors = ScriptCompile.Errors(code);
            Assert.True(errors.Count == 0, code + "\n" + ScriptCompile.Format(errors));
        }

        [Fact]
        public void Instance_methods_still_win_over_the_extensions()
        {
            // The interop's own overloads must keep binding (existing macros pass SelectData / null / IPartDoc calls).
            var errors = ScriptCompile.Errors("IBody2 b = null; SelectData sd = null; b.Select2(true, sd); b.Select2(true, null); IEntity e = null; e.Select4(true, null);" +
                                              "var f = ((IPartDoc)Model).FeatureByName(\"x\"); var sm = (ISelectionMgr)Model.SelectionManager; var d = sm.CreateSelectData();");
            Assert.True(errors.Count == 0, ScriptCompile.Format(errors));
        }

        [Fact]
        public void A_cell_variable_named_like_a_helper_still_compiles()
        {
            var errors = ScriptCompile.Errors("var Bodies = new List<IBody2>(); var Sel = 1; var n = Bodies.Count + Sel;");
            Assert.True(errors.Count == 0, ScriptCompile.Format(errors));
        }

        [Fact]
        public void AsDoubles_reads_every_array_shape_SolidWorks_returns()
        {
            Assert.Empty(((object)null).AsDoubles());
            var d = new[] { 1.5, 2.0 };
            Assert.Same(d, ((object)d).AsDoubles());
            Assert.Equal(new[] { 1.0, 2.5, 3.0 }, ((object)new object[] { 1, 2.5, 3f }).AsDoubles());
            Assert.Equal(new[] { 4.0 }, ((object)4.0).AsDoubles());
            Assert.Equal(new[] { 1.0, 2.0 }, ((object)new[] { 1, 2 }).AsDoubles());
            Assert.Throws<InvalidCastException>(() => ((object)"1,2").AsDoubles());
            var ex = Assert.Throws<InvalidCastException>(() => ((object)new object[] { 1.0, "x" }).AsDoubles());
            Assert.Contains("item 1", ex.Message);
        }

        [Fact]
        public void AsInts_takes_whole_numbers_only()
        {
            Assert.Empty(((object)null).AsInts());
            Assert.Equal(new[] { 1, 2, 3 }, ((object)new object[] { 1, 2.0, (short)3 }).AsInts());
            Assert.Equal(new[] { 7 }, ((object)7).AsInts());
            var ex = Assert.Throws<InvalidCastException>(() => ((object)new[] { 1.5 }).AsInts());
            Assert.Contains("AsDoubles", ex.Message);
        }

        [Fact]
        public void AsObjects_skips_nulls_and_names_a_wrong_item()
        {
            Assert.Empty(((object)null).AsObjects<string>());
            Assert.Equal(new[] { "a", "b" }, ((object)new object[] { "a", null, "b" }).AsObjects<string>());
            Assert.Equal(new[] { "solo" }, ((object)"solo").AsObjects<string>());
            var ex = Assert.Throws<InvalidCastException>(() => ((object)new object[] { "a", 3 }).AsObjects<string>());
            Assert.Contains("item 1", ex.Message);
            Assert.Equal(2, ((object)new object[] { "a", 3 }).AsObjects<object>().Length);
        }

        [Fact]
        public void AsStrings_converts_items()
        {
            Assert.Empty(((object)null).AsStrings());
            Assert.Equal(new[] { "a", "1.5" }, ((object)new object[] { "a", 1.5 }).AsStrings());
        }

        [Fact]
        public void The_extensions_namespace_is_a_default_import()
        {
            Assert.Contains("SwAgent.Scripting.Extensions", ScriptCompile.Options.Imports);
        }
    }
}

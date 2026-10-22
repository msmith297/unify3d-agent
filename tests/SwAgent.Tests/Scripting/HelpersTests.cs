using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SolidWorks.Interop.sldworks;
using SwAgent.Scripting;
using SwAgent.Scripting.Helpers;
using Xunit;

namespace SwAgent.Tests.Scripting
{
    /// <summary>
    /// The built-in helpers headless: cells using every one compile with the real options, the catalog documents each
    /// one, every raw API member they wrap or call exists in the SolidWorks 2026 interop with the signature they use, and
    /// the turn identification works on synthetic bodies (the user's SolidWorks turned RotAngleX about Y).
    /// </summary>
    public class HelpersTests
    {
        [Theory]
        [InlineData("var a = Bodies.Named(\"a\"); a = Bodies.Move(a, mm(1), 0, 0); a = Bodies.Rotate(a, \"X\", 90); a = Bodies.Rotate(a, \"-z\", 45, new[] { 0.0, mm(5), 0 });" +
                    "var all = Bodies.Rotate(Bodies.All(), new[] { 1.0, 1, 0 }, 30); var moved = Bodies.Move(all, 0, 0, mm(2)); var tilted = Bodies.Rotate(Bodies.All(true), \"Y\", -90);")]
        [InlineData("var a = Bodies.Named(\"a\"); var copies = Bodies.Copy(a, mm(10), 0, 0, 3); var more = Bodies.Copy(copies, 0, mm(5), 0);" +
                    "var u = Bodies.Combine(a, Bodies.Named(\"b\")); u = Bodies.Combine(u, copies); u = Bodies.Combine(u, copies[0], copies[1]);" +
                    "u = Bodies.Subtract(u, copies, keepTools: true); u = Bodies.Subtract(u, Bodies.Named(\"c\")); u = Bodies.Intersect(u, copies); u = Bodies.Intersect(u, a);")]
        [InlineData("var a = Bodies.Named(\"a\"); var pcs = Bodies.Split(a, \"Z\", mm(5)); var p2 = Bodies.Split(a, Features.ByName(\"Top Plane\"));" +
                    "IFeature folder = Bodies.ToFolder(pcs, \"Pieces\"); folder = Bodies.ToFolder(a, \"Main\"); IList<IBody2> last = Bodies.Last; IFeature lf = Bodies.LastFeature;")]
        [InlineData("var e = Features.Extrude(\"Sketch1\", mm(10)); Features.Extrude(null, mm(5), reverse: true, merge: false); Features.Extrude(Model.LastFeature(), mm(4), midPlane: true);" +
                    "Features.Cut(\"Sketch2\", Features.ThroughAll); Features.Cut(Model.LastFeature(), mm(3), new[] { Bodies.Named(\"a\") }, reverse: false); Features.Cut(\"Sketch3\", mm(2), bothSides: true);" +
                    "Features.Revolve(\"Sketch4\"); Features.Revolve(\"Sketch4\", 90, null, false); Features.RevolveCut(\"Sketch5\", 180, null, Bodies.All());" +
                    "IEdge ed = null; Features.Fillet(new[] { ed }, mm(1)); Features.Fillet(ed, mm(1)); IList<string> errs = Features.Errors(); Features.Delete(e); IFeature l = Features.Last(); IFeature lf = Features.LastFeature;")]
        [InlineData("int n = Sel.Select(Bodies.Named(\"a\"), 2); n = Sel.Select(new object[] { Bodies.Named(\"a\"), Features.ByName(\"x\") }, 1, append: true); n = Sel.Select(\"Sketch1\");" +
                    "SelectData sd = Sel.Data(4); n = Sel.Count(); n = Sel.Count(2); object o = Sel.Get(1); IList<object> all = Sel.All(); Sel.Clear();")]
        public void Cells_using_the_helpers_compile(string code)
        {
            var errors = ScriptCompile.Errors(code);
            Assert.True(errors.Count == 0, code + "\n" + ScriptCompile.Format(errors));
        }

        private static readonly (string Prefix, Type Type)[] Apis = { ("Bodies", typeof(BodiesApi)), ("Features", typeof(FeaturesApi)), ("Sel", typeof(SelApi)) };

        [Fact]
        public void Every_helper_method_has_exactly_one_documented_overload()
        {
            foreach (var (prefix, type) in Apis)
            {
                foreach (var g in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).GroupBy(m => m.Name))
                {
                    var documented = g.Count(m => m.GetCustomAttribute<HelperAttribute>() != null);
                    Assert.True(documented == 1, $"{prefix}.{g.Key}: {documented} [Helper] overloads, expected 1");
                }
            }
        }

        [Fact]
        public void The_catalog_lists_every_helper_with_a_one_line_signature()
        {
            var all = HelperCatalog.All;
            Assert.Equal(all.Count, all.Select(h => h.Name).Distinct().Count());
            foreach (var h in all)
            {
                Assert.False(string.IsNullOrWhiteSpace(h.Summary), h.Name);
                Assert.StartsWith(h.Name, h.Signature.Replace("<T>", ""));
                Assert.DoesNotContain("\n", h.Signature);
                Assert.Equal("built-in", h.Source);
                Assert.NotNull(h.Wraps);
            }
            foreach (var name in new[] { "Bodies.Move", "Bodies.Rotate", "Bodies.Copy", "Bodies.Combine", "Bodies.Subtract", "Bodies.Intersect", "Bodies.Split", "Bodies.ToFolder",
                         "Features.Extrude", "Features.Cut", "Features.Revolve", "Features.RevolveCut", "Features.Fillet", "Sel.Select", "Sel.Data",
                         "Model.FeatureByName", "Model.CreateSelectData", "Model.Bodies", "body.Box", "body.Volume", "feature.Bodies", "x.AsDoubles", "x.AsObjects" })
                Assert.NotNull(HelperCatalog.Find(name));
            Assert.Equal("Bodies.Rotate(IBody2 body, string axis, double degrees, double[] about = null) -> IBody2", HelperCatalog.Find("Bodies.Rotate").Signature);
            Assert.Equal("x.AsObjects<T>() -> T[]", HelperCatalog.Find("x.AsObjects").Signature);
            Assert.Same(HelperCatalog.All, HelperCatalog.Index.All());
        }

        [Fact]
        public void Wrapped_members_exist_in_the_interop()
        {
            var asm = typeof(ISldWorks).Assembly;
            foreach (var h in HelperCatalog.All)
            {
                foreach (var w in h.Wraps)
                {
                    var dot = w.LastIndexOf('.');
                    Assert.True(dot > 0, $"{h.Name}: wrap '{w}' must be Interface.Member");
                    var type = asm.GetType("SolidWorks.Interop.sldworks." + w.Substring(0, dot));
                    Assert.True(type != null, $"{h.Name}: no interop type {w.Substring(0, dot)}");
                    var member = w.Substring(dot + 1);
                    Assert.True(type.GetMember(member).Length > 0, $"{h.Name}: {type.Name} has no {member}");
                }
            }
            Assert.Contains(HelperCatalog.Wrapping("FeatureCut4"), h => h.Name == "Features.Cut");
            Assert.Contains(HelperCatalog.Wrapping("IFeatureManager.InsertMoveCopyBody2"), h => h.Name == "Bodies.Rotate");
            Assert.Empty(HelperCatalog.Wrapping("IModelDoc2.InsertMoveCopyBody2"));
        }

        /// <summary>The raw calls the helpers make, with the parameter lists they were written against (named arguments compile only against these names).</summary>
        [Theory]
        [InlineData("IFeatureManager", "InsertMoveCopyBody2", "TransX,TransY,TransZ,TransDist,RotPointX,RotPointY,RotPointZ,RotAngleX,RotAngleY,RotAngleZ,BCopy,NumCopies")]
        [InlineData("IFeatureManager", "FeatureExtrusion3", "Sd,Flip,Dir,T1,T2,D1,D2,Dchk1,Dchk2,Ddir1,Ddir2,Dang1,Dang2,OffsetReverse1,OffsetReverse2,TranslateSurface1,TranslateSurface2,Merge,UseFeatScope,UseAutoSelect,T0,StartOffset,FlipStartOffset")]
        [InlineData("IFeatureManager", "FeatureCut4", "Sd,Flip,Dir,T1,T2,D1,D2,Dchk1,Dchk2,Ddir1,Ddir2,Dang1,Dang2,OffsetReverse1,OffsetReverse2,TranslateSurface1,TranslateSurface2,NormalCut,UseFeatScope,UseAutoSelect,AssemblyFeatureScope,AutoSelectComponents,PropagateFeatureToParts,T0,StartOffset,FlipStartOffset,OptimizeGeometry")]
        [InlineData("IFeatureManager", "FeatureRevolve2", "SingleDir,IsSolid,IsThin,IsCut,ReverseDir,BothDirectionUpToSameEntity,Dir1Type,Dir2Type,Dir1Angle,Dir2Angle,OffsetReverse1,OffsetReverse2,OffsetDistance1,OffsetDistance2,ThinType,ThinThickness1,ThinThickness2,Merge,UseFeatScope,UseAutoSelect")]
        [InlineData("IFeatureManager", "FeatureFillet3", "Options,R1,R2,Rho,Ftyp,OverflowType,ConicRhoType,Radii,Dist2Arr,RhoArr,SetBackDistances,PointRadiusArray,PointDist2Array,PointRhoArray")]
        [InlineData("IFeatureManager", "InsertCombineFeature", "OperationType,MainBody,ToolVar")]
        [InlineData("IFeatureManager", "PreSplitBody2", "")]
        [InlineData("IFeatureManager", "PostSplitBody2", "BodiesToMark,ConsumeCut,Origins,SavePaths,OverrideTemplateName")]
        [InlineData("IFeatureManager", "InsertRefPlane", "FirstConstraint,FirstConstraintAngleOrDistance,SecondConstraint,SecondConstraintAngleOrDistance,ThirdConstraint,ThirdConstraintAngleOrDistance")]
        [InlineData("IFeatureManager", "InsertSubFolder", "")]
        [InlineData("IBody2", "Select2", "Append,Data")]
        [InlineData("IBody2", "GetExtremePoint", "X,Y,Z,Outx,Outy,Outz")]
        [InlineData("IBody2", "Operations2", "OperationType,ToolBody,ErrorCode")]
        [InlineData("IEntity", "Select4", "Append,Data")]
        [InlineData("IFeature", "Select2", "Append,Mark")]
        [InlineData("ISelectionMgr", "SetSelectedObjectMark", "AtIndex,Mark,Action")]
        [InlineData("IModelDocExtension", "GetObjectByPersistReference3", "PersistId,ErrorCode")]
        public void The_interop_has_the_signatures_the_helpers_use(string type, string method, string parameters)
        {
            var t = typeof(ISldWorks).Assembly.GetType("SolidWorks.Interop.sldworks." + type);
            Assert.NotNull(t);
            var found = t.GetMethods().Where(m => m.Name == method).Select(m => string.Join(",", m.GetParameters().Select(p => p.Name))).ToList();
            Assert.Contains(parameters, found);
        }

        [Fact]
        public void Helpers_are_on_the_globals()
        {
            var g = typeof(ScriptGlobals);
            Assert.Equal(typeof(BodiesApi), g.GetProperty("Bodies")?.PropertyType);
            Assert.Equal(typeof(FeaturesApi), g.GetProperty("Features")?.PropertyType);
            Assert.Equal(typeof(SelApi), g.GetProperty("Sel")?.PropertyType);
        }

        // ---------- the turn identification (pure math) ----------

        /// <summary>A box body for the synthetic tests: its eight corners stand in for SolidWorks' extreme points.</summary>
        private sealed class FakeBody
        {
            public double[][] Corners;

            public static FakeBody Box(double x0, double y0, double z0, double x1, double y1, double z1) => new FakeBody
            {
                Corners = (from x in new[] { x0, x1 } from y in new[] { y0, y1 } from z in new[] { z0, z1 } select new[] { x, y, z }).ToArray(),
            };

            public FakeBody Turn(double[,] r, double[] c) => new FakeBody { Corners = Corners.Select(p => RotationMath.Turn(r, c, p)).ToArray() };
            public double Volume => 1; // boxes here only turn: volume is kept
            public double[] Centroid => Enumerable.Range(0, 3).Select(k => Corners.Average(p => p[k])).ToArray();
            public double Support(double[] d) => Corners.Max(p => p[0] * d[0] + p[1] * d[1] + p[2] * d[2]);

            public BodyShape Shape() => new BodyShape { V = Volume, C = Centroid, S = RotationMath.Dirs.Select(Support).ToArray() };
        }

        private static (double Error, int[] Pairing) Match(IList<FakeBody> before, IList<FakeBody> after, double[,] r, double[] c, double[] t = null) =>
            RotationMath.Match(before.Select(b => b.Shape()).ToList(), after, r, c, b => b.Volume, b => b.Centroid, (b, d) => b.Support(d), t);

        [Fact]
        public void Match_tells_the_turn_axis_and_sign_apart()
        {
            // The user's clip: x -1..31, y -7.77..7.77, z -4.22..2.55 (mm), turned +90 deg about X.
            var clip = FakeBody.Box(-0.001, -0.00777, -0.00422, 0.031, 0.00777, 0.00255);
            var c = new double[3];
            var turned = clip.Turn(RotationMath.Axis(0, Math.PI / 2), c);
            var hits = new List<(int Axis, int Sign)>();
            for (var a = 0; a < 3; a++)
            foreach (var s in new[] { 1, -1 })
                if (Match(new[] { clip }, new[] { turned }, RotationMath.Axis(a, s * Math.PI / 2), c).Error < 1e-9) hits.Add((a, s));
            Assert.Equal(new[] { (0, 1) }, hits);
        }

        [Fact]
        public void Match_sees_a_turn_about_the_centroid_by_the_extents()
        {
            // Centred on the origin: no candidate moves the centroid, only the extents differ.
            var plate = FakeBody.Box(-0.03, -0.01, -0.002, 0.03, 0.01, 0.002);
            var c = new double[3];
            var turned = plate.Turn(RotationMath.Axis(2, 0.3), c);
            Assert.True(Match(new[] { plate }, new[] { turned }, RotationMath.Axis(2, 0.3), c).Error < 1e-9);
            Assert.True(Match(new[] { plate }, new[] { turned }, RotationMath.Axis(2, -0.3), c).Error > 1e-4);
        }

        [Fact]
        public void Match_pairs_several_bodies_and_handles_a_point_and_a_move()
        {
            var a = FakeBody.Box(0, 0, 0, 0.01, 0.02, 0.03);
            var b = FakeBody.Box(0.05, 0, 0, 0.06, 0.01, 0.01);
            var p = new[] { 0.02, 0.01, 0.0 };
            var r = RotationMath.Axis(1, -Math.PI / 3);
            var after = new[] { b.Turn(r, p), a.Turn(r, p) }; // SolidWorks lists them in any order
            var (err, pairing) = Match(new[] { a, b }, after, r, p);
            Assert.True(err < 1e-9);
            Assert.Equal(new[] { 1, 0 }, pairing);
            // about the origin then moved by p - R p is the same turn about p
            var (err2, _) = Match(new[] { a, b }, after, r, new double[3], V3Sub(p, RotationMath.Apply(r, p)));
            Assert.True(err2 < 1e-9);
            Assert.True(double.IsInfinity(Match(new[] { a, b }, new[] { a }, r, p).Error));
        }

        private static double[] V3Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

        [Fact]
        public void Euler_angles_compose_back_to_the_turn()
        {
            var rng = new Random(7);
            for (var i = 0; i < 200; i++)
            {
                var axis = new[] { rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5 };
                var r = RotationMath.About(axis, (rng.NextDouble() - 0.5) * 2 * Math.PI);
                var (x, y, z) = RotationMath.EulerXyz(r);
                var back = RotationMath.Mul(RotationMath.Axis(2, z), RotationMath.Mul(RotationMath.Axis(1, y), RotationMath.Axis(0, x)));
                Assert.True(RotationMath.Diff(r, back) < 1e-9, $"case {i}");
            }
            // gimbal lock: a quarter turn about Y with a turn about Z
            var g = RotationMath.Mul(RotationMath.Axis(2, 0.4), RotationMath.Axis(1, Math.PI / 2));
            var (gx, gy, gz) = RotationMath.EulerXyz(g);
            Assert.True(RotationMath.Diff(g, RotationMath.Mul(RotationMath.Axis(2, gz), RotationMath.Mul(RotationMath.Axis(1, gy), RotationMath.Axis(0, gx)))) < 1e-9);
        }

        [Fact]
        public void Axis_turns_agree_with_the_general_formula()
        {
            for (var k = 0; k < 3; k++)
            {
                var e = new double[3];
                e[k] = 1;
                Assert.True(RotationMath.Diff(RotationMath.Axis(k, 0.7), RotationMath.About(e, 0.7)) < 1e-12);
            }
            Assert.Equal(new[] { 0.0, 0, -1 }, RotationMath.AxisVector("-z"));
            Assert.Throws<SwHelperException>(() => RotationMath.AxisVector("W"));
            Assert.Equal(1, RotationMath.AxisIndex(new[] { 0.0, -2, 0 }));
            Assert.Equal(-1, RotationMath.AxisIndex(new[] { 1.0, 1, 0 }));
        }

        /// <summary>
        /// The slot search against a simulated SolidWorks that behaves as the user's did (RotAngleX turned about +Y,
        /// RotAngleY about Z the other way, RotAngleZ about +X): the first turn about X costs three tries, every later
        /// turn about any measured axis one.
        /// </summary>
        [Fact]
        public void Slot_search_learns_the_rotation_slots()
        {
            var solidWorks = new Dictionary<int, (int Axis, int Sign)> { [0] = (1, 1), [1] = (2, -1), [2] = (0, 1) };
            var learned = new Dictionary<int, (int Axis, int Sign)>();

            int Turn(int k, double angle)
            {
                var tried = new HashSet<int>();
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    var next = RotationMath.NextSlot(k, angle, learned, tried);
                    Assert.True(next.HasValue);
                    var (slot, value) = next.Value;
                    tried.Add(slot);
                    var did = solidWorks[slot];
                    learned[slot] = did; // measured
                    if (did.Axis == k && Math.Abs(did.Sign * value - angle) < 1e-12) return attempt;
                }
                return -1;
            }

            Assert.Equal(3, Turn(0, Math.PI / 2));
            Assert.Equal(1, Turn(0, -Math.PI / 4));
            Assert.Equal(1, Turn(2, 0.5)); // RotAngleY with the sign flipped
            Assert.Equal(1, Turn(1, 1.0));
            Assert.Null(RotationMath.NextSlot(0, 1, learned, new HashSet<int> { 0, 1, 2 }));
        }
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Mold.Geometry;
using SwAgent.ModelFiles;
using SwAgent.UI;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>Face highlights (critique G3), pocket reliefs (B7) and the hidden-document gate (B6), headless.</summary>
    public class GeometryOverlayTests
    {
        /// <summary>A unit square in the z = 0 plane as two triangles.</summary>
        private static readonly float[] Square = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 1, 0, 0, 1, 0 };

        [Fact]
        public void Outline_of_a_face_is_its_boundary_not_the_diagonal()
        {
            var outline = SceneOverlay.TriangleOutline(Square);
            Assert.Equal(8, outline.Count); // 4 edges, 2 points each
            Assert.DoesNotContain(Enumerable.Range(0, 4), k =>
            {
                var a = outline[2 * k];
                var b = outline[2 * k + 1];
                return Math.Abs(Math.Abs(a[0] - b[0]) - 1) < 1e-9 && Math.Abs(Math.Abs(a[1] - b[1]) - 1) < 1e-9; // the shared diagonal
            });
        }

        [Fact]
        public void Closed_box_has_no_open_boundary_and_twelve_triangles()
        {
            var corners = Enumerable.Range(0, 8).Select(k => new double[] { k & 1, (k >> 1) & 1, (k >> 2) & 1 }).ToArray();
            var tris = SceneOverlay.BoxTriangles(corners);
            Assert.Equal(12 * 9, tris.Length);
            Assert.Empty(SceneOverlay.TriangleOutline(tris));
        }

        [Fact]
        public void Fill_budget_turns_later_items_into_outlines()
        {
            var big = new float[(SceneOverlay.MaxFilledTriangles - 10) * 9];
            for (var i = 0; i < big.Length; i += 9)
            {
                big[i + 3] = 1;
                big[i + 7] = 1;
            }
            var a = new SceneOverlay.Item { Type = "tris", Tris = big };
            var b = new SceneOverlay.Item { Type = "tris", Tris = Square.Concat(Square).Concat(Square).Concat(Square).Concat(Square).Concat(Square).ToArray() }; // 12 triangles: over
            var c = new SceneOverlay.Item { Type = "tris", Tris = Square }; // 2 triangles: still fits
            var filled = SceneOverlay.BudgetTriangles(new[] { a, b, c });
            Assert.False(a.OutlineOnly);
            Assert.True(b.OutlineOnly);
            Assert.False(c.OutlineOnly);
            Assert.Equal(SceneOverlay.MaxFilledTriangles - 8, filled);
            Assert.NotNull(b.Outline);
        }

        [Fact]
        public void Dog_bones_sit_on_the_bisectors_through_the_sharp_corners()
        {
            // A 40 x 20 mm pocket, counter-clockwise; cutter radius 3 mm -> relief radius 3.3 mm.
            var pocket = new[] { new[] { 0.0, 0 }, new[] { 0.04, 0 }, new[] { 0.04, 0.02 }, new[] { 0.0, 0.02 } };
            const double cutter = 0.003;
            var r = PocketRelief.ReliefRadius(cutter);
            Assert.Equal(0.0033, r, 9);
            var centres = PocketRelief.DogBones(pocket, r);
            Assert.Equal(4, centres.Count);
            var d = r / Math.Sqrt(2);
            Assert.Equal(d, centres[0][0], 9); // inside the pocket, on the 45 deg bisector
            Assert.Equal(d, centres[0][1], 9);
            Assert.All(centres.Select((c, i) => Math.Sqrt(Math.Pow(c[0] - pocket[i][0], 2) + Math.Pow(c[1] - pocket[i][1], 2))), x => Assert.Equal(r, x, 9));
            // The relief removes everything the cutter leaves in each corner: every point of the pocket within the
            // corner square (cutter x cutter) but outside the cutter's own circle is inside the relief circle.
            for (var i = 0; i < 4; i++)
            {
                var corner = pocket[i];
                var inward = new[] { Math.Sign(0.02 - corner[0]), Math.Sign(0.01 - corner[1]) };
                var mill = new[] { corner[0] + inward[0] * cutter, corner[1] + inward[1] * cutter };
                for (var a = 0; a <= 20; a++)
                    for (var b = 0; b <= 20; b++)
                    {
                        var p = new[] { corner[0] + inward[0] * cutter * a / 20, corner[1] + inward[1] * cutter * b / 20 };
                        if (Math.Sqrt(Math.Pow(p[0] - mill[0], 2) + Math.Pow(p[1] - mill[1], 2)) < cutter) continue; // the mill reaches it
                        var toCentre = Math.Sqrt(Math.Pow(p[0] - centres[i][0], 2) + Math.Pow(p[1] - centres[i][1], 2));
                        Assert.True(toCentre <= r + 1e-12, $"corner {i}: ({p[0]}, {p[1]}) is left by the cutter and not relieved");
                    }
            }
            // Clockwise order gives the same centres.
            var cw = Enumerable.Reverse(pocket).ToArray();
            Assert.Equal(4, PocketRelief.DogBones(cw, r).Count);
            // Corners already rounded enough are skipped.
            Assert.Equal(2, PocketRelief.DogBones(pocket, r, new[] { true, false, true, false }).Count);
        }

        [Fact]
        public void L_shaped_pocket_relieves_only_its_convex_corners()
        {
            // L: the reflex corner at (0.02, 0.01) is reached by the mill and gets no relief.
            var l = new[] { new[] { 0.0, 0 }, new[] { 0.04, 0 }, new[] { 0.04, 0.01 }, new[] { 0.02, 0.01 }, new[] { 0.02, 0.03 }, new[] { 0.0, 0.03 } };
            var c = PocketRelief.DogBones(l, 0.002);
            Assert.Equal(5, c.Count);
            Assert.DoesNotContain(c, p => Math.Abs(p[0] - 0.02) < 0.003 && Math.Abs(p[1] - 0.01) < 0.003);
        }

        [Fact]
        public void Insert_chamfer_clears_the_pocket_radius()
        {
            var r = 0.0033;
            var chamfer = PocketRelief.InsertChamfer(r);
            Assert.Equal(r * (2 - Math.Sqrt(2)) + 0.0001, chamfer, 9);
        }

        [Fact]
        public async Task Hidden_document_gate_is_shared_and_never_deadlocks_the_ui_thread()
        {
            Task<IDisposable> second;
            using (await HiddenDocuments.LockAsync(CancellationToken.None))
            {
                // Synchronous work between an async holder's slices goes ahead (it can't interleave with it on one thread).
                using (HiddenDocuments.Lock())
                {
                }
                second = HiddenDocuments.LockAsync(CancellationToken.None);
                Assert.False(second.IsCompleted); // async callers queue
                using (var cts = new CancellationTokenSource(50))
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HiddenDocuments.LockAsync(cts.Token));
            }
            // Released: the queued caller gets the gate, and gives it back.
            var held = await second;
            held.Dispose();
            held.Dispose(); // disposing twice releases once
            using (HiddenDocuments.Lock())
            {
                // A synchronous holder re-entered from a message pump: fail fast, never interleave two hidden opens.
                var busy = Assert.Throws<InvalidOperationException>(() => HiddenDocuments.Lock());
                Assert.Contains("try again", busy.Message);
            }
            using (await HiddenDocuments.LockAsync(CancellationToken.None))
            {
            }
            using (HiddenDocuments.Lock())
            {
            }
        }
    }
}

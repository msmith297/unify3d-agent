using SwAgent.UI;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>What the agent draws clears by itself once the model changes (SceneOverlay's stale state), except a kept drawing and check-only rebuilds.</summary>
    public class OverlayStaleTests
    {
        [Fact]
        public void A_model_change_makes_the_drawing_stale_and_a_safe_moment_clears_it()
        {
            SceneOverlay.ResetStale(keep: false);
            SceneOverlay.OnModelChanged();
            Assert.True(SceneOverlay.Stale);
            SceneOverlay.ClearIfStale();
            Assert.False(SceneOverlay.Stale);
        }

        [Fact]
        public void A_kept_drawing_survives_changes()
        {
            SceneOverlay.ResetStale(keep: true);
            SceneOverlay.OnModelChanged();
            Assert.False(SceneOverlay.Stale);
            SceneOverlay.ClearAll();
        }

        [Fact]
        public void A_check_only_rebuild_leaves_the_drawing()
        {
            SceneOverlay.ResetStale(keep: false);
            var result = SceneOverlay.QuietRebuild(() => SceneOverlay.OnModelChanged());
            Assert.Equal(0, result);
            Assert.False(SceneOverlay.Stale);
            SceneOverlay.OnModelChanged(); // the next real change still counts
            Assert.True(SceneOverlay.Stale);
            SceneOverlay.ClearAll();
        }
    }
}

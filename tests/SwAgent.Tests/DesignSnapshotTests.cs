using Newtonsoft.Json.Linq;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    public class DesignSnapshotTests
    {
        private static JObject Snap(string features, string bodies) =>
            JObject.Parse($"{{\"at\":\"t0\",\"features\":{features},\"bodies\":{bodies},\"insertedParts\":[],\"equations\":[]}}");

        [Fact]
        public void Diff_lists_what_a_new_customer_part_broke_in_tree_order()
        {
            var before = Snap(
                "[{\"index\":1,\"name\":\"Customer part\",\"type\":\"Stock\",\"insertedPart\":\"C:/a/rev1.SLDPRT\",\"faces\":40,\"faceArea\":0.1}," +
                " {\"index\":2,\"name\":\"Sketch3\",\"type\":\"ProfileFeature\",\"owner\":\"Cavity cut\",\"sketchOn\":{\"kind\":\"face\",\"feature\":\"Core plate\",\"normal\":[0,0,1],\"point\":[0,0,0.0381]}}," +
                " {\"index\":2,\"name\":\"Cavity cut\",\"type\":\"ICE\",\"faces\":12,\"faceArea\":0.02,\"bodies\":[\"Core\"],\"dims\":[{\"name\":\"D1@Cavity cut\",\"value\":0.0127}]}," +
                " {\"index\":3,\"name\":\"Flash moat\",\"type\":\"ICE\",\"faces\":24,\"faceArea\":0.01}]",
                "[{\"name\":\"Core\",\"volume\":0.0018,\"box\":[0,0,0,0.254,0.2032,0.0381],\"faces\":80}," +
                " {\"name\":\"Cavity\",\"volume\":0.0012,\"box\":[0,0,0.0381,0.254,0.2032,0.07],\"faces\":60}]");
            var after = Snap(
                "[{\"index\":1,\"name\":\"Customer part\",\"type\":\"Stock\",\"insertedPart\":\"C:/a/rev2.SLDPRT\",\"faces\":42,\"faceArea\":0.105}," +
                " {\"index\":2,\"name\":\"Sketch3\",\"type\":\"ProfileFeature\",\"owner\":\"Cavity cut\",\"sketchOn\":{\"kind\":\"face\",\"feature\":\"Core plate\",\"normal\":[0,0,1],\"point\":[0,0,0.0401]}}," +
                " {\"index\":2,\"name\":\"Cavity cut\",\"type\":\"ICE\",\"faces\":12,\"faceArea\":0.02,\"bodies\":[\"Core\"],\"dims\":[{\"name\":\"D1@Cavity cut\",\"value\":0.0127}]}," +
                " {\"index\":3,\"name\":\"Flash moat\",\"type\":\"ICE\",\"error\":2,\"errorName\":\"swFeatureErrorFeatureFailed\",\"warning\":false,\"faces\":0}]",
                "[{\"name\":\"Core\",\"volume\":0.0017,\"box\":[0,0,0,0.254,0.2032,0.0381],\"faces\":78}]");

            var report = DesignSnapshot.Diff(before, after);

            Assert.Contains("1 feature(s) with errors", report);
            Assert.Contains("inserted part C:/a/rev1.SLDPRT -> C:/a/rev2.SLDPRT", report);
            Assert.Contains("its face moved 2 mm along its normal", report);
            Assert.Contains("Flash moat [ICE]: ERROR swFeatureErrorFeatureFailed", report);
            Assert.Contains("produces no faces any more (had 24)", report);
            Assert.Contains("'Core': volume -5.6%", report);
            Assert.Contains("MISSING body 'Cavity'", report);
            Assert.DoesNotContain("Cavity cut [ICE]:", report); // unchanged features stay out of the list
            Assert.True(report.IndexOf("Customer part") < report.IndexOf("Flash moat"), "tree order");
        }
    }
}

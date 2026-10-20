using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Mold;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>Shared fixtures: the shipped data (copied next to the test assembly as builtin-tools), the examples file.</summary>
    internal static class MoldTestData
    {
        private static readonly Lazy<MoldData> _data = new Lazy<MoldData>(() => MoldData.Load(MoldData.DefaultToolDir));
        public static MoldData Data => _data.Value;

        public static string DataDir => Path.Combine(MoldData.DefaultToolDir, "data");

        public static JObject Examples => Si.ParseObject(File.ReadAllText(Path.Combine(DataDir, "validation", "examples.json")));

        public static JObject Example(string id) => Examples["examples"].OfType<JObject>().Single(e => (string)e["id"] == id);

        public static Cfg Cfg(JObject overrides = null) => new Cfg(Data.Defaults, overrides);

        /// <summary>The repository root (for the generator checks), or null when the tests run outside a checkout.</summary>
        public static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "tools", "dev", "mold-data"))) dir = dir.Parent;
            return dir?.FullName;
        }

        public static void Near(double expected, double actual, double tolPct, string what)
        {
            var tol = Math.Abs(expected) * tolPct / 100.0 + 1e-9;
            Assert.True(Math.Abs(actual - expected) <= tol, $"{what}: expected {expected:G8}, got {actual:G8} (tolerance {tolPct}%)");
        }

        /// <summary>A minimal class A design (the E2 job) with optional changes.</summary>
        public static JObject E2Design() => (JObject)Example("E2")["design"].DeepClone();
    }
}

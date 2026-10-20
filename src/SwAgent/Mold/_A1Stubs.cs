// ============================================================================================================
// DELETE AT INTEGRATION. Track D codes against Track A1's public API (contract.md, "Track A1 - Public API");
// these stubs only make Track D's worktree compile. A1's real classes replace every type in this file.
// ============================================================================================================
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold
{
    public sealed class MoldData
    {
        public static MoldData Load(string toolDir, string overrideDir = null) => throw new NotImplementedException("A1 stub: MoldData.Load");
        public IReadOnlyList<JObject> PartMaterials => throw new NotImplementedException("A1 stub");
        public JObject PartMaterial(string id) => throw new NotImplementedException("A1 stub");
        public IReadOnlyList<JObject> MoldGrades => throw new NotImplementedException("A1 stub");
        public JObject MoldGrade(string id) => throw new NotImplementedException("A1 stub");
        public JObject Fastener(string size) => throw new NotImplementedException("A1 stub");
        public JObject Shop => throw new NotImplementedException("A1 stub");
        public JObject Defaults => throw new NotImplementedException("A1 stub");
        public JObject Raw(string file) => throw new NotImplementedException("A1 stub");
    }

    public sealed class CalcResult
    {
        public string Text;
        public JObject Json;
        public JObject RecordPatch;
        public List<JObject> Checks;
    }

    public static class MoldCalc
    {
        public static CalcResult Run(string action, JObject design, JObject query, JObject overrides, MoldData data) => throw new NotImplementedException("A1 stub: MoldCalc.Run");
        public static readonly string Version = "0.0.0-stub";
    }

    public sealed class MoldRecord
    {
        public static MoldRecord Init(string folder, string moldPartPath, JObject inputs, string toolVersion, string addinVersion) => throw new NotImplementedException("A1 stub: MoldRecord.Init");
        public static MoldRecord Open(string folder) => throw new NotImplementedException("A1 stub: MoldRecord.Open");
        public string Folder => throw new NotImplementedException("A1 stub");
        public string Id => throw new NotImplementedException("A1 stub");
        public JObject Json => throw new NotImplementedException("A1 stub");
        public string Summary(int maxChars = 6000) => throw new NotImplementedException("A1 stub");
        public JToken Section(string key) => throw new NotImplementedException("A1 stub");
        public string Merge(JObject patch) => throw new NotImplementedException("A1 stub");
        public List<string> Validate() => throw new NotImplementedException("A1 stub");
        public void Save() => throw new NotImplementedException("A1 stub");
    }

    public static class SetupSheet
    {
        public static string Write(MoldRecord r, string templatePath, MoldData data) => throw new NotImplementedException("A1 stub: SetupSheet.Write");
    }

    public static class MachiningSheet
    {
        public static string Write(MoldRecord r, string templatePath, MoldData data) => throw new NotImplementedException("A1 stub: MachiningSheet.Write");
    }

    public static class Coefficients
    {
        public static (double Alpha, double Beta) Get(string bc, double aspect, double tOverB, double nu, double loadFrac) => throw new NotImplementedException("A1 stub");
        public static string Version => "stub";
    }
}

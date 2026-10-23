using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// The agent's SolidWorks API cheat sheet (ui/src/agent/cad/swApiCheatsheet.ts, written by tools/dev/ApiCheatSheet from
    /// members.json): every member exists on the interface it names in the interop the add-in loads, and the sheet's line is
    /// exactly what get_sw_api_type prints for it (for a member a helper covers, "use": its parameter count and return type) -
    /// so a SolidWorks or interop update that changes a signature fails here.
    /// </summary>
    public class ApiCheatSheetTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "tools", "dev", "ApiCheatSheet", "members.json"))) d = d.Parent;
            Assert.True(d != null, "tools/dev/ApiCheatSheet/members.json not found above the test folder");
            return d.FullName;
        }

        /// <summary>What get_sw_api_type prints for one interface or enum, one signature per line.</summary>
        private static List<string> TypeLines(string type, string filter)
        {
            var result = new ApiTools().GetApiType(null, new ApiTools.TypeInput { TypeName = type, MemberFilter = filter });
            Assert.False(result.IsError, $"get_sw_api_type {type}: " + result.Content.FirstOrDefault()?["text"]);
            return ((string)result.Content[0]["text"]).Split('\n').Skip(1).ToList();
        }

        [Fact]
        public void Every_member_is_in_the_interop_and_its_sheet_line_is_the_exact_signature()
        {
            var root = RepoRoot();
            var spec = JObject.Parse(File.ReadAllText(Path.Combine(root, "tools", "dev", "ApiCheatSheet", "members.json")));
            var sheet = File.ReadAllText(Path.Combine(root, "ui", "src", "agent", "cad", "swApiCheatsheet.ts")).Replace("\r\n", "\n").Split('\n');
            var problems = new List<string>();
            var count = 0;

            foreach (var group in (JArray)spec["groups"])
            {
                foreach (var entry in (JArray)group["members"])
                {
                    count++;
                    var name = entry.Type == JTokenType.String ? (string)entry : (string)entry["member"];
                    var dot = name.IndexOf('.');
                    if (dot < 0)
                    {
                        // swconst enum: "swX_e: A=1, B=2"
                        var values = TypeLines(name, null).Select(l => l.Substring(name.Length + 1).Replace(" = ", "=")).ToList();
                        var line = sheet.FirstOrDefault(l => l.StartsWith(name + ": ", StringComparison.Ordinal));
                        if (line == null) problems.Add(name + ": no line in the sheet");
                        else
                        {
                            foreach (var v in line.Substring(name.Length + 2).Split(new[] { ", " }, StringSplitOptions.None))
                            {
                                if (!values.Contains(v)) problems.Add($"{name}: {v} is not in the interop");
                            }
                        }
                        continue;
                    }

                    var iface = name.Substring(0, dot);
                    var member = name.Substring(dot + 1);
                    var signature = TypeLines(iface, member)
                        .FirstOrDefault(l => l.StartsWith(name + "(", StringComparison.Ordinal) || l.StartsWith(name + " {", StringComparison.Ordinal));
                    if (signature == null)
                    {
                        problems.Add($"{name}: not on {iface} in the interop");
                        continue;
                    }
                    if (entry.Type == JTokenType.Object && entry["use"] != null)
                    {
                        // A member a built-in helper covers: "IX.Member(N parameters) : T - use Helper; ..." with the real count and type.
                        var open = signature.IndexOf('(');
                        var close = signature.LastIndexOf(") : ", StringComparison.Ordinal);
                        var inner = signature.Substring(open + 1, close - open - 1);
                        var parameters = inner.Length == 0 ? 0 : inner.Split(',').Length;
                        var brief = $"{name}({parameters} parameter{(parameters == 1 ? "" : "s")}) : {signature.Substring(close + 4)} - use {((string)entry["use"]).Trim()};";
                        if (!sheet.Any(l => l.StartsWith(brief, StringComparison.Ordinal))) problems.Add($"{name}: the sheet doesn't have the brief line {brief}");
                        continue;
                    }
                    if (!sheet.Any(l => l == signature || l.StartsWith(signature + " - ", StringComparison.Ordinal)))
                        problems.Add($"{name}: the sheet doesn't have the exact signature {signature}");
                }
            }

            Assert.True(count >= 50, $"members.json lists only {count} members");
            Assert.True(problems.Count == 0, "Regenerate the sheet (tools/dev/ApiCheatSheet):\n" + string.Join("\n", problems));
        }
    }
}

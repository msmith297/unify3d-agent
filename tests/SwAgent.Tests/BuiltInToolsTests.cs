using System.IO;
using System.Linq;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>Built-in Unify3D tools ship as folders next to the add-in, load like the user's tools, and are read-only.</summary>
    public class BuiltInToolsTests
    {
        [Fact]
        public void Smart_rebuild_ships_as_a_tool_folder_with_its_page()
        {
            Assert.True(File.Exists(Path.Combine(UserTools.BuiltInRoot, "smart-rebuild", "TOOL.md")), "builtin-tools is copied next to the add-in");
            var spec = ToolSpec.Load("smart-rebuild");
            Assert.NotNull(spec);
            Assert.Equal("tool", spec.Kind);
            Assert.True(spec.Agent);
            Assert.Equal("always", spec.Confirm); // its PropertyManager page always opens
            Assert.Contains(spec.Inputs, i => i.Id == "change" && ToolSpec.Type(i) == "options");
            Assert.Contains(spec.Inputs, i => i.Id == "new_part" && ToolSpec.Type(i) == "files");
            Assert.True(UserTools.IsBuiltIn("smart-rebuild"));

            var listed = UserTools.ListTools().Single(t => (string)t["name"] == "smart-rebuild");
            Assert.True((bool)listed["builtIn"]);
        }

        [Fact]
        public void Built_in_tools_are_read_only_but_keep_the_users_settings()
        {
            var md = Path.Combine(UserTools.BuiltInRoot, "smart-rebuild", "TOOL.md");
            var before = File.ReadAllText(md);

            var tools = new UserTools();
            var save = tools.Save(null, new UserTools.SaveInput { Name = "smart-rebuild", Title = "Mine", Kind = "tool" });
            Assert.True(save.IsError);
            Assert.Throws<ToolException>(() => tools.Edit(null, new UserTools.EditInput { Name = "smart-rebuild", Append = "x" }));
            Assert.Throws<ToolException>(() => UserTools.Delete("smart-rebuild"));

            try
            {
                UserTools.SetValue("smart-rebuild", "intelligence", "high");
                Assert.Equal("high", ToolSpec.Load("smart-rebuild").Intelligence);
                Assert.Equal(before, File.ReadAllText(md)); // the shipped folder is untouched
            }
            finally
            {
                UserTools.SetValue("smart-rebuild", "intelligence", "max");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.Infrastructure;
using SwAgent.Scripting;
using SwAgent.Tools;
using Xunit;

namespace SwAgent.Tests
{
    public class ScriptingTests
    {
        [Fact]
        public void Inside_and_box_work_on_a_rectangle_with_a_notch()
        {
            var geo = new Geometry();
            var poly = new List<double[]>
            {
                new[] { 0.0, 0.0 }, new[] { 4.0, 0.0 }, new[] { 4.0, 3.0 }, new[] { 2.5, 3.0 }, new[] { 2.5, 2.0 }, new[] { 1.5, 2.0 },
                new[] { 1.5, 3.0 }, new[] { 0.0, 3.0 },
            };
            Assert.True(geo.Inside(new[] { 1.0, 1.0 }, poly));
            Assert.False(geo.Inside(new[] { 2.0, 2.5 }, poly)); // in the notch
            Assert.False(geo.Inside(new[] { 5.0, 1.0 }, poly));
            Assert.Equal(new[] { 0.0, 4.0, 0.0, 3.0 }, geo.Box(poly));
        }

        [Fact]
        public void Plane_frame_round_trips_points()
        {
            var n = new[] { 0.0, 1.0, 0.0 };
            var frame = new PlaneFrame(new[] { 0.0, 0.05, 0.0 }, new[] { 1.0, 0.0, 0.0 }, Geometry.Cross(n, new[] { 1.0, 0.0, 0.0 }), n);
            var p = frame.ToModel(0.1, -0.2, 0.003);
            var uv = frame.ToUV(p);
            Assert.Equal(0.1, uv[0], 9);
            Assert.Equal(-0.2, uv[1], 9);
            Assert.Equal(0.003, frame.Height(p), 9);
        }

        /// <summary>Every saved tool's macro and preview must compile with the add-in's script options (catches broken edits).</summary>
        [Fact]
        public void Saved_tool_scripts_compile()
        {
            AssemblyResolver.Install();
            var root = Environment.GetEnvironmentVariable("SWAGENT_TOOLS_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwAgent", "tools");
            if (!Directory.Exists(root)) return;
            var host = new ScriptHost(new ToolRegistry());
            var failures = new List<string>();
            foreach (var file in Directory.GetDirectories(root).SelectMany(d => new[] { "macro.cs", "preview.cs" }.Select(f => Path.Combine(d, f))).Where(File.Exists))
            {
                var (_, errors) = host.CompilePreviewAsync(File.ReadAllText(file)).GetAwaiter().GetResult();
                if (errors != null) failures.Add(file + ":\n" + errors);
            }
            Assert.True(failures.Count == 0, string.Join("\n\n", failures));
        }
    }
}

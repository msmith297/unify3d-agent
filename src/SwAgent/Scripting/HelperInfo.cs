using System.Collections.Generic;

namespace SwAgent.Scripting
{
    /// <summary>
    /// One helper a code cell can call (built in, from a plugin, or written by the agent itself), as the prompt index
    /// and the compile-error advisor see it.
    /// </summary>
    public sealed class HelperInfo
    {
        /// <summary>How a cell calls it, e.g. "Bodies.Rotate".</summary>
        public string Name { get; set; }

        /// <summary>One line: "Bodies.Rotate(IBody2 body, string axis, double degrees, double[] about = null) -> IBody2".</summary>
        public string Signature { get; set; }

        /// <summary>One sentence: what it does and how it checks itself.</summary>
        public string Summary { get; set; }

        /// <summary>The raw API members it replaces, "Interface.Member" (e.g. "IFeatureManager.InsertMoveCopyBody2").</summary>
        public string[] Wraps { get; set; } = new string[0];

        /// <summary>"built-in", "plugin:&lt;name&gt;" or "user".</summary>
        public string Source { get; set; } = "built-in";
    }

    /// <summary>A source of helpers (the built-in catalog, plugins, the agent's own).</summary>
    public interface IHelperIndex
    {
        IEnumerable<HelperInfo> All();
    }
}

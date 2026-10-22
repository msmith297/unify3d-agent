using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Scripting
{
    /// <summary>
    /// One helper the agent can call from an execute_csharp cell instead of writing the raw API calls itself: a built-in one
    /// (HelperCatalog: ScriptGlobals' Bodies, Features, Sel and the SwAgent.Scripting.Extensions methods), one a plugin ships
    /// (helpers/*.cs) or one the agent wrote itself (save_helper, %APPDATA%\SwAgent\agent-knowledge\helpers).
    /// </summary>
    public sealed class HelperInfo
    {
        public HelperInfo()
        {
        }

        public HelperInfo(string name, string signature, string summary, string[] wraps = null, string source = "built-in")
        {
            Name = name;
            Signature = signature;
            Summary = summary;
            Wraps = wraps ?? new string[0];
            Source = source;
        }

        /// <summary>"Bodies.Rotate" (built-in) or the helper's class name ("FeatureLookup").</summary>
        public string Name { get; set; }

        /// <summary>How it is called, e.g. "Bodies.Rotate(IBody2 body, double[] axis, double angle): IBody2".</summary>
        public string Signature { get; set; }

        /// <summary>One line: what it does and what it checks.</summary>
        public string Summary { get; set; }

        /// <summary>The raw API members it replaces ("InsertMoveCopyBody2" or "IModelDocExtension.InsertMoveCopyBody2").</summary>
        public string[] Wraps { get; set; } = new string[0];

        /// <summary>"built-in", "plugin:&lt;plugin name&gt;" or "user".</summary>
        public string Source { get; set; } = "built-in";

        /// <summary>The member names of Wraps ("IModelDocExtension.InsertMoveCopyBody2" -> "InsertMoveCopyBody2").</summary>
        internal IEnumerable<string> WrappedMembers() =>
            (Wraps ?? new string[0]).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).Select(w => w.Substring(w.LastIndexOf('.') + 1)).Where(w => w.Length > 0);
    }

    /// <summary>A list of helpers (the built-in catalog, or every helper the agent can call now).</summary>
    public interface IHelperIndex
    {
        IEnumerable<HelperInfo> All();
    }

    /// <summary>
    /// Where helpers come from, merged in one order everywhere (the prompt index, list_helpers, the compile advisor, the
    /// "Helper: X does this in one call" hints): the agent's own first, then the plugins', then the built-in catalog
    /// (HelperCatalog: Bodies.*, Features.*, Sel.* and the SwAgent.Scripting.Extensions methods). Tests can set BuiltIn.
    /// </summary>
    public static class HelperIndex
    {
        private static IHelperIndex _builtIn;

        public static IHelperIndex BuiltIn
        {
            get => _builtIn ?? Helpers.HelperCatalog.Index;
            set => _builtIn = value;
        }

        /// <summary>The agent's own and the plugins' helpers (in that order), then the built-in ones; one entry per name.</summary>
        public static List<HelperInfo> Merge(IEnumerable<HelperInfo> extra)
        {
            List<HelperInfo> builtIn;
            try
            {
                builtIn = (BuiltIn.All() ?? Enumerable.Empty<HelperInfo>()).ToList();
            }
            catch (Exception ex)
            {
                Log.Warn("Reading the built-in helper catalog failed: " + ex.Message);
                builtIn = new List<HelperInfo>();
            }
            var own = (extra ?? Enumerable.Empty<HelperInfo>()).Where(h => h != null).OrderBy(h => h.Source == "user" ? 0 : 1);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return own.Concat(builtIn).Where(h => !string.IsNullOrWhiteSpace(h?.Name) && seen.Add(h.Name)).ToList();
        }

        /// <summary>
        /// The class name a built-in helper takes from the names cells can use: "Bodies" for "Bodies.Rotate", "Model" for
        /// "Model.FeatureByName"; null for an extension shown on a placeholder receiver ("body.Volume", "x.AsDoubles").
        /// </summary>
        public static string ReservedName(HelperInfo h)
        {
            var first = (h?.Name ?? "").Split('.')[0];
            return first.Length > 0 && char.IsUpper(first[0]) ? first : null;
        }

        /// <summary>The built-in helpers called `name` or under the class `name` ("Bodies"): empty when the name is free.</summary>
        public static List<HelperInfo> BuiltInNamed(string name) =>
            string.IsNullOrEmpty(name) ? new List<HelperInfo>() : BuiltIn.All().Where(h => h.Name == name || ReservedName(h) == name).ToList();

        /// <summary>An index over a fixed list (tests, plugins).</summary>
        public sealed class Fixed : IHelperIndex
        {
            private readonly IReadOnlyList<HelperInfo> _list;

            public Fixed(IEnumerable<HelperInfo> list)
            {
                _list = (list ?? Enumerable.Empty<HelperInfo>()).Where(h => h != null).ToList();
            }

            public IEnumerable<HelperInfo> All() => _list;
        }
    }
}

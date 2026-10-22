using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// Where the built-in helpers come from. By default the static HelperCatalog (SwAgent.Scripting.HelperCatalog, track A),
    /// found by reflection: its first public static IEnumerable&lt;HelperInfo&gt; field or property. Empty when the add-in has
    /// none. Tests (or start-up code) can set another index.
    /// </summary>
    public static class HelperIndex
    {
        private static IHelperIndex _builtIn;

        public static IHelperIndex BuiltIn
        {
            get => _builtIn ?? (_builtIn = new CatalogIndex());
            set => _builtIn = value;
        }

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

        private sealed class CatalogIndex : IHelperIndex
        {
            private Func<IEnumerable<HelperInfo>> _read;

            public IEnumerable<HelperInfo> All()
            {
                if (_read == null) _read = Find();
                try
                {
                    return (_read() ?? Enumerable.Empty<HelperInfo>()).Where(h => h != null && !string.IsNullOrWhiteSpace(h.Name)).ToList();
                }
                catch (Exception ex)
                {
                    Log.Warn("Reading the built-in helper catalog failed: " + ex.Message);
                    return Enumerable.Empty<HelperInfo>();
                }
            }

            private static Func<IEnumerable<HelperInfo>> Find()
            {
                var type = typeof(HelperInfo).Assembly.GetType("SwAgent.Scripting.HelperCatalog", false);
                if (type == null) return () => Enumerable.Empty<HelperInfo>();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                foreach (var p in type.GetProperties(flags).Where(p => typeof(IEnumerable<HelperInfo>).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0))
                    return () => (IEnumerable<HelperInfo>)p.GetValue(null);
                foreach (var f in type.GetFields(flags).Where(f => typeof(IEnumerable<HelperInfo>).IsAssignableFrom(f.FieldType)))
                    return () => (IEnumerable<HelperInfo>)f.GetValue(null);
                return () => Enumerable.Empty<HelperInfo>();
            }
        }
    }
}

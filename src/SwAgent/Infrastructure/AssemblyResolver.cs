using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// An add-in has no app.config inside SLDWORKS.exe, so NuGet binding redirects never apply.
    /// When the runtime can't bind a dependency (Roslyn, System.Memory, Immutable, ...) we load
    /// whatever version ships next to SwAgent.dll, ignoring the requested version.
    /// </summary>
    internal static class AssemblyResolver
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

            // Already loaded under a different version? Reuse it.
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            if (loaded != null) return loaded;

            var dir = Path.GetDirectoryName(typeof(AssemblyResolver).Assembly.Location);
            var candidate = Path.Combine(dir, name + ".dll");
            if (!File.Exists(candidate)) return null;

            try
            {
                return Assembly.LoadFrom(candidate);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to resolve {args.Name} from {candidate}", ex);
                return null;
            }
        }
    }
}

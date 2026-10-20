using System;
using System.IO;

namespace SwAgent.Infrastructure
{
    /// <summary>Well-known folders. Everything per-user; nothing is written next to the dll.</summary>
    internal static class Paths
    {
        /// <summary>
        /// The folder SwAgent.dll was installed in (wwwroot, speech, builtin-tools live next to it). From the code base, not
        /// Location: when the assembly is shadow-copied (test runners, some hosts) Location is a temporary copy without them.
        /// </summary>
        public static string AddInDir { get; } = InstallDir();

        private static string InstallDir()
        {
            var asm = typeof(Paths).Assembly;
            try
            {
                var uri = new Uri(asm.CodeBase);
                if (uri.IsFile) return Path.GetDirectoryName(uri.LocalPath);
            }
            catch
            {
                // Fall back to Location.
            }
            return Path.GetDirectoryName(asm.Location);
        }

        public static string WebRoot => Path.Combine(AddInDir, "wwwroot");

        /// <summary>%APPDATA%\SwAgent: settings and the encrypted API key.</summary>
        public static string RoamingDir => Ensure(TestRoot != null ? Path.Combine(TestRoot, "Roaming") : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwAgent"));

        /// <summary>%LOCALAPPDATA%\SwAgent: logs, WebView2 profile, crash-recovery chat backups.</summary>
        public static string LocalDir => Ensure(TestRoot != null ? Path.Combine(TestRoot, "Local") : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwAgent"));

        /// <summary>
        /// Tests set SWAGENT_DATA_DIR (tests/SwAgent.Tests/test.runsettings) so they never read or delete the user's real
        /// crash record, logs or settings. A relative value goes under the temp folder.
        /// </summary>
        private static string TestRoot
        {
            get
            {
                var v = Environment.GetEnvironmentVariable("SWAGENT_DATA_DIR");
                if (string.IsNullOrWhiteSpace(v)) return null;
                return Path.IsPathRooted(v) ? v : Path.Combine(Path.GetTempPath(), v);
            }
        }

        public static string LogDir => Ensure(Path.Combine(LocalDir, "logs"));
        public static string WebViewDataDir => Ensure(Path.Combine(LocalDir, "WebView2"));
        public static string ChatBackupDir => Ensure(Path.Combine(LocalDir, "chat-backup"));
        public static string CaptureDir => Ensure(Path.Combine(LocalDir, "captures"));

        private static string Ensure(string dir)
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

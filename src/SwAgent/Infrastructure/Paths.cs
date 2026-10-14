using System;
using System.IO;

namespace SwAgent.Infrastructure
{
    /// <summary>Well-known folders. Everything per-user; nothing is written next to the dll.</summary>
    internal static class Paths
    {
        public static string AddInDir { get; } = Path.GetDirectoryName(typeof(Paths).Assembly.Location);

        public static string WebRoot => Path.Combine(AddInDir, "wwwroot");

        /// <summary>%APPDATA%\SwAgent: settings and the encrypted API key.</summary>
        public static string RoamingDir => Ensure(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwAgent"));

        /// <summary>%LOCALAPPDATA%\SwAgent: logs, WebView2 profile, crash-recovery chat backups.</summary>
        public static string LocalDir => Ensure(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwAgent"));

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

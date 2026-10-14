using System;
using System.IO;
using System.Linq;

namespace SwAgent.Infrastructure
{
    /// <summary>Tiny append-only file logger. Never throws.</summary>
    internal static class Log
    {
        private static readonly object Sync = new object();

        public static string CurrentFile => Path.Combine(Paths.LogDir, $"swagent-{DateTime.Now:yyyyMMdd}.log");

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);
        public static void Error(string message, Exception ex = null) => Write("ERROR", ex == null ? message : $"{message}\n{ex}");

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    File.AppendAllText(CurrentFile, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Logging must never break SolidWorks.
            }
        }

        /// <summary>Delete logs older than two weeks.</summary>
        public static void Prune()
        {
            try
            {
                foreach (var f in new DirectoryInfo(Paths.LogDir).GetFiles("swagent-*.log")
                             .Where(f => f.LastWriteTime < DateTime.Now.AddDays(-14)))
                {
                    f.Delete();
                }
            }
            catch
            {
            }
        }
    }
}

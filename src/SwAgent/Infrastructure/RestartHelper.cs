using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SwAgent.Infrastructure
{
    /// <summary>Starts SolidWorks again once this instance has exited, optionally reopening a document.</summary>
    internal static class RestartHelper
    {
        public static bool Launch(string reopenPath)
        {
            try
            {
                var me = Process.GetCurrentProcess();
                var exe = me.MainModule.FileName;
                var reopen = !string.IsNullOrEmpty(reopenPath) && File.Exists(reopenPath)
                    ? $" -ArgumentList @('\"{Quote(reopenPath)}\"')"
                    : "";
                var script = $"Wait-Process -Id {me.Id} -Timeout 600 -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800; " +
                             $"Start-Process -FilePath '{Quote(exe)}'{reopen}";
                // Encoded, so paths with spaces or quotes can't break the command line.
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -EncodedCommand " + encoded)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                Log.Info("Restart helper started" + (reopen.Length > 0 ? " (reopening " + Path.GetFileName(reopenPath) + ")" : ""));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Restart helper failed: " + ex.Message);
                return false;
            }
        }

        private static string Quote(string s) => s.Replace("'", "''");
    }
}

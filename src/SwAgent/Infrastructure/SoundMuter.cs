using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// Silences SolidWorks' "Rebuild Error" sound while the agent works. The agent often makes a feature fail and
    /// then fixes it; the user shouldn't hear every intermediate error.
    ///
    /// SolidWorks plays the sound through the Windows sound scheme (HKCU\AppEvents\Schemes\Apps\SLDWORKS), which is
    /// read when the sound is played, so blanking the event's .Current value mutes it. The original value is saved
    /// to disk first and restored when the agent stops - or on the next start-up if SolidWorks crashed meanwhile.
    /// </summary>
    internal static class SoundMuter
    {
        private const string SchemeKey = @"AppEvents\Schemes\Apps\SLDWORKS\";
        private static readonly string[] Events = { "SLDWORKS_RebuildError" };

        private static int _depth;
        private static Timer _restoreTimer;

        private static string BackupFile => Path.Combine(Paths.LocalDir, "muted-sounds.json");

        /// <summary>Agent started working: mute (nested calls are counted).</summary>
        public static void Begin()
        {
            _restoreTimer?.Stop();
            if (_depth++ > 0 || File.Exists(BackupFile)) return; // already muted
            try
            {
                var originals = new Dictionary<string, string>();
                foreach (var ev in Events)
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(SchemeKey + ev + @"\.Current", writable: true))
                    {
                        if (key == null) continue;
                        originals[ev] = key.GetValue("") as string ?? "";
                    }
                }
                File.WriteAllText(BackupFile, JsonConvert.SerializeObject(originals)); // backup before changing anything
                foreach (var ev in originals.Keys)
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(SchemeKey + ev + @"\.Current", writable: true))
                    {
                        key?.SetValue("", "");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Muting SolidWorks error sounds failed: " + ex.Message);
            }
        }

        /// <summary>Agent stopped: restore after a short delay (rebuild sounds can trail the last API call).</summary>
        public static void End()
        {
            if (_depth == 0) return;
            if (--_depth > 0) return;
            if (_restoreTimer == null)
            {
                _restoreTimer = new Timer { Interval = 2500 };
                _restoreTimer.Tick += (s, e) =>
                {
                    _restoreTimer.Stop();
                    if (_depth == 0) RestoreNow();
                };
            }
            _restoreTimer.Stop();
            _restoreTimer.Start();
        }

        /// <summary>Put the user's sounds back immediately (also used at start-up after a crash).</summary>
        public static void RestoreNow()
        {
            _depth = 0;
            _restoreTimer?.Stop();
            try
            {
                if (!File.Exists(BackupFile)) return;
                var originals = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(BackupFile)) ?? new Dictionary<string, string>();
                foreach (var pair in originals)
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(SchemeKey + pair.Key + @"\.Current", writable: true))
                    {
                        key?.SetValue("", pair.Value ?? "");
                    }
                }
                File.Delete(BackupFile);
            }
            catch (Exception ex)
            {
                Log.Warn("Restoring SolidWorks error sounds failed: " + ex.Message);
            }
        }
    }
}

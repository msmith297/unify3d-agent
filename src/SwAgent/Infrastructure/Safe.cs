using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security;
using Newtonsoft.Json.Linq;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// Code that SolidWorks or WinForms calls on its own - timer ticks, document and selection events - runs inside
    /// SolidWorks' message loop. An exception escaping from there isn't "an error in the add-in": it terminates the
    /// whole SolidWorks process. Every such handler goes through Safe.Run, which logs and swallows failures.
    /// </summary>
    internal static class Safe
    {
        // HandleProcessCorruptedStateExceptions: an access violation inside a SolidWorks call is caught here too (see
        // NativeFault) instead of closing SolidWorks; it blocks the agent's model tools until a restart.
        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static void Run(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                var fault = NativeFault.Fault(ex);
                if (fault != null) NativeFault.Record(what, fault);
                else Log.Warn($"{what} failed (ignored): {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static EventHandler Tick(string what, Action action) => (s, e) => Run(what, action);

        /// <summary>
        /// Last resort: an exception that still escaped somewhere is about to end SolidWorks. Record it so the next
        /// start can say what happened (CrashGuard reports it like a crashed tool step).
        /// </summary>
        public static void InstallLastChance()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    Log.Error("Unhandled exception in the add-in (SolidWorks will close)", ex);
                    File.WriteAllText(Path.Combine(Paths.LocalDir, "crash-guard.json"), new JObject
                    {
                        ["kind"] = "addin",
                        ["title"] = ex?.GetType().Name + ": " + ex?.Message,
                        ["code"] = ex?.ToString(),
                        ["at"] = DateTime.Now.ToString("o"),
                    }.ToString());
                }
                catch
                {
                    // Nothing more can be done at this point.
                }
            };
        }
    }
}

using System;
using SolidWorks.Interop.sldworks;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// Is SolidWorks in the middle of a command (a PropertyManager page, a dialog, an edit)? Work that runs on its own -
    /// timers, polls - must not read or change the model then: the model can be half-way through an operation, and a
    /// geometry call at that moment can crash SolidWorks in a way no try/catch can stop.
    /// </summary>
    /// <summary>
    /// The agent's tool calls wait while SolidWorks is in a command (review finding: a long unattended mold run fired
    /// RayIntersections and feature edits whenever the model answered, the pattern that crashed SolidWorks before). Tools
    /// that never touch the model run at once.
    /// </summary>
    internal static class SwBusyGate
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        /// <summary>Tools that never read or change the SolidWorks model (pure calculation, files, the API docs).</summary>
        public static readonly System.Collections.Generic.HashSet<string> ModelFreeTools = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            "search_sw_api", "get_sw_api_type", "mold_calc", "rebuild_journal", "list_user_tools", "read_user_tool", "save_user_tool",
            "edit_user_tool", "clear_overlay", "design_library", "list_open_documents", "get_selection",
        };

        public static bool ModelFree(string tool) => tool != null && ModelFreeTools.Contains(tool);

        /// <summary>Polls `busy` every 250 ms (yielding the UI thread); false when it is still busy after `timeout`.</summary>
        public static async System.Threading.Tasks.Task<bool> WaitAsync(System.Func<bool> busy, TimeSpan timeout, int pollMs = 250)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (busy())
            {
                if (watch.Elapsed >= timeout) return false;
                await System.Threading.Tasks.Task.Delay(pollMs);
            }
            return true;
        }
    }

    internal static class SwBusy
    {
        public static bool InCommand(ISldWorks sw)
        {
            try
            {
                sw.GetRunningCommandInfo(out var id, out var title, out var uiActive);
                return id > 0 || uiActive || !string.IsNullOrEmpty(title);
            }
            catch
            {
                return true; // when in doubt, wait
            }
        }
    }
}

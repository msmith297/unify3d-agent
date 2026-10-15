using SolidWorks.Interop.sldworks;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// Is SolidWorks in the middle of a command (a PropertyManager page, a dialog, an edit)? Work that runs on its own -
    /// timers, polls - must not read or change the model then: the model can be half-way through an operation, and a
    /// geometry call at that moment can crash SolidWorks in a way no try/catch can stop.
    /// </summary>
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

using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;

namespace SwAgent.Scripting
{
    /// <summary>
    /// While agent code runs, SolidWorks must not stop and wait for a click nobody will make: the dimension value box
    /// that AddDimension2 opens, the rebuild-errors dialog, the "this dimension overdefines the sketch" prompt and the
    /// "close the sketch?" prompt. They are switched off for the duration of a cell and the user's settings put back
    /// afterwards. (A Smart Rebuild once sat for 6.7 minutes on a dimension box no one could see.)
    /// </summary>
    internal sealed class QuietDialogs : IDisposable
    {
        private static readonly swUserPreferenceToggle_e[] Off =
        {
            swUserPreferenceToggle_e.swInputDimValOnCreate,
            swUserPreferenceToggle_e.swShowErrorsEveryRebuild,
            swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState,
            swUserPreferenceToggle_e.swSketchPromptToCloseSketch,
        };

        private readonly ISldWorks _sw;
        private readonly List<(swUserPreferenceToggle_e Pref, bool Was)> _saved = new List<(swUserPreferenceToggle_e, bool)>();

        public QuietDialogs(ISldWorks sw)
        {
            _sw = sw;
            if (sw == null) return;
            foreach (var pref in Off)
            {
                try
                {
                    var was = sw.GetUserPreferenceToggle((int)pref);
                    if (!was) continue;
                    sw.SetUserPreferenceToggle((int)pref, false);
                    _saved.Add((pref, was));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Couldn't switch off {pref} for the cell: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            foreach (var (pref, was) in _saved)
            {
                try
                {
                    _sw.SetUserPreferenceToggle((int)pref, was);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Couldn't restore {pref}: {ex.Message}");
                }
            }
            _saved.Clear();
        }
    }
}

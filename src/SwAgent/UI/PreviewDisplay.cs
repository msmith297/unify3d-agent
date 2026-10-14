using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SwAgent.UI
{
    /// <summary>
    /// Shows and hides a tool's temporary ("holographic") preview bodies. The only place that displays them, so the
    /// tool page and test_tool use exactly the same, known-safe calls.
    ///
    /// Temporary bodies belong to no part: setting appearance properties on them (IBody2.MaterialPropertyValues2)
    /// makes SolidWorks dereference a part that doesn't exist and crash with an access violation. Colour is passed
    /// to Display3 only.
    /// </summary>
    internal static class PreviewDisplay
    {
        public static List<IBody2> Show(IModelDoc2 model, IEnumerable<(IBody2 Body, int Color, double Transparency)> bodies)
        {
            var shown = new List<IBody2>();
            if (!(model is IPartDoc part)) return shown;
            foreach (var (body, color, _) in bodies)
            {
                if (body == null) continue;
                var r = (color >> 16) & 0xFF;
                var g = (color >> 8) & 0xFF;
                var b = color & 0xFF;
                body.Display3(part, (b << 16) | (g << 8) | r, (int)swTempBodySelectOptions_e.swTempBodySelectOptionNone);
                shown.Add(body); // keep a reference: a displayed temporary body must stay alive until hidden
            }
            if (shown.Count > 0) model.GraphicsRedraw2();
            return shown;
        }

        public static void Hide(IModelDoc2 model, List<IBody2> shown)
        {
            if (shown.Count == 0) return;
            foreach (var body in shown)
            {
                try
                {
                    body.Hide(model);
                }
                catch
                {
                    // Already gone (document closed).
                }
            }
            shown.Clear();
            try
            {
                model.GraphicsRedraw2();
            }
            catch
            {
                // Document closed.
            }
        }
    }
}

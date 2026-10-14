using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace SwAgent.Tools
{
    /// <summary>
    /// What a step did to the feature tree. Taken before and after every execute_csharp cell and undo, so the agent
    /// always knows which features it created (a cell can make more than the one it meant to, e.g. a stray sketch
    /// that SolidWorks extrudes) and whether an undo really removed them.
    /// </summary>
    internal static class FeatureDiff
    {
        private static readonly HashSet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HistoryFolder", "SelectionSetFolder", "SensorFolder", "DocsFolder", "DetailCabinet", "CommentsFolder",
            "FavoriteFolder", "InkMarkupFolder", "EnvFolder", "MBD3DViewFolder", "LiveSectionFolder", "SurfaceBodyFolder",
            "SolidBodyFolder", "MaterialFolder", "EqnFolder", "OriginProfileFeature",
        };

        /// <summary>Top-level features by id ("Name [Type]").</summary>
        public static Dictionary<int, string> Take(IModelDoc2 model)
        {
            var result = new Dictionary<int, string>();
            try
            {
                if (model == null || model.GetType() == (int)SolidWorks.Interop.swconst.swDocumentTypes_e.swDocDRAWING) return result;
                var feat = (IFeature)model.FirstFeature();
                while (feat != null)
                {
                    var type = feat.GetTypeName2();
                    if (!Folders.Contains(type)) result[feat.GetID()] = $"{feat.Name} [{type}]";
                    feat = (IFeature)feat.GetNextFeature();
                }
            }
            catch
            {
                // Diagnostics only; never fail a step because the tree couldn't be read.
            }
            return result;
        }

        /// <summary>"Feature tree: + Sketch1 [ProfileFeature], + Boss-Extrude1 [Extrusion]; - Fillet2 [Fillet]" or null.</summary>
        public static string Describe(Dictionary<int, string> before, Dictionary<int, string> after)
        {
            if (before == null || after == null) return null;
            var added = after.Where(p => !before.ContainsKey(p.Key)).Select(p => "+ " + p.Value).ToList();
            var removed = before.Where(p => !after.ContainsKey(p.Key)).Select(p => "- " + p.Value).ToList();
            var renamed = after.Where(p => before.TryGetValue(p.Key, out var old) && old != p.Value)
                .Select(p => $"~ {before[p.Key]} -> {p.Value}").ToList();
            var all = added.Concat(removed).Concat(renamed).ToList();
            return all.Count == 0 ? null : "Feature tree changes: " + string.Join(", ", all.Take(40)) + (all.Count > 40 ? $", ... ({all.Count} in total)" : "");
        }
    }
}

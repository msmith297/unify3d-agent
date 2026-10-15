using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>
    /// Every agent step that runs code in a document (execute_csharp cells, tool macros) is one SolidWorks undo
    /// step. The ledger remembers, per document, a fingerprint of the model before and after each of them, so "undo
    /// everything the agent did since this message" can press Ctrl+Z exactly until the model is back where it was -
    /// rather than guessing a count (cells that changed nothing may leave no undo step; the agent's own undo calls
    /// and the user's edits in between move the count too).
    /// </summary>
    internal static class UndoLedger
    {
        internal sealed class Entry
        {
            public string ToolUseId;
            public string Title;
            public string Before;
            public string After;
            public bool Changed => Before != After;
        }

        private static readonly ConditionalWeakTable<IModelDoc2, List<Entry>> Ledgers = new ConditionalWeakTable<IModelDoc2, List<Entry>>();

        private const int MaxEntries = 400;

        public static string Begin(IModelDoc2 model) => model == null ? null : Fingerprint(model);

        public static void Record(IModelDoc2 model, string toolUseId, string title, string before)
        {
            if (model == null || before == null) return;
            try
            {
                var list = Ledgers.GetOrCreateValue(model);
                list.Add(new Entry { ToolUseId = toolUseId, Title = title, Before = before, After = Fingerprint(model) });
                if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries);
            }
            catch (Exception ex)
            {
                Log.Warn("Undo ledger: " + ex.Message);
            }
        }

        /// <summary>The steps from the first of these tool calls to now, or null if none of them is in the ledger (e.g. after a restart).</summary>
        private static List<Entry> Since(IModelDoc2 model, ICollection<string> toolUseIds)
        {
            if (model == null || toolUseIds == null || toolUseIds.Count == 0) return null;
            if (!Ledgers.TryGetValue(model, out var list)) return null;
            var first = list.FindIndex(e => e.ToolUseId != null && toolUseIds.Contains(e.ToolUseId));
            return first < 0 ? null : list.Skip(first).ToList();
        }

        /// <summary>How many changes the agent made since then that can be undone, or -1 if that can't be done reliably.</summary>
        public static int Undoable(IModelDoc2 model, ICollection<string> toolUseIds)
        {
            var steps = Since(model, toolUseIds);
            if (steps == null) return -1;
            if (Fingerprint(model) == steps[0].Before) return 0; // already back there
            return Math.Max(1, steps.Count(e => e.Changed));
        }

        /// <summary>
        /// Undo one step at a time until the model matches its state before the first of these tool calls. Cells that
        /// changed nothing are undone along the way; it never undoes more steps
        /// than the agent recorded, so the user's earlier work is never touched.
        /// </summary>
        public static (bool Ok, int Steps, string Message) UndoTo(IModelDoc2 model, ICollection<string> toolUseIds)
        {
            var steps = Since(model, toolUseIds);
            if (steps == null) return (false, 0, "The agent's steps for that message aren't in this SolidWorks session's undo history (SolidWorks was restarted or the document reopened).");
            var target = steps[0].Before;
            var max = steps.Count; // an upper bound for the agent's own steps: never reach into what was there before
            var done = 0;
            var before = FeatureDiff.Take(model);
            while (Fingerprint(model) != target && done < max)
            {
                model.EditUndo2(1);
                done++;
            }
            model.GraphicsRedraw2();
            var reached = Fingerprint(model) == target;
            if (Ledgers.TryGetValue(model, out var list) && reached) list.RemoveRange(list.IndexOf(steps[0]), list.Count - list.IndexOf(steps[0]));
            var tree = FeatureDiff.Describe(before, FeatureDiff.Take(model));
            return reached
                ? (true, done, $"Undid {done} step{(done == 1 ? "" : "s")}. " + (tree ?? ""))
                : (false, done, $"Undid {done} steps but the model doesn't match its earlier state yet - check it, and use Ctrl+Z for the rest. " + (tree ?? ""));
        }

        /// <summary>
        /// What undo must restore: the feature tree (names, types, suppression), the bodies' size, custom properties
        /// and the part's material. Cheap enough to take before and after every cell.
        /// </summary>
        internal static string Fingerprint(IModelDoc2 model)
        {
            var sb = new StringBuilder();
            try
            {
                var feat = (IFeature)model.FirstFeature();
                while (feat != null)
                {
                    sb.Append(feat.GetID()).Append(':').Append(feat.Name).Append(':').Append(feat.GetTypeName2());
                    try
                    {
                        var suppressed = (bool[])feat.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null);
                        if (suppressed != null && suppressed.Length > 0 && suppressed[0]) sb.Append(":S");
                    }
                    catch
                    {
                        // Not every feature reports suppression.
                    }
                    sb.Append('|');
                    feat = (IFeature)feat.GetNextFeature();
                }

                if (model is IPartDoc part)
                {
                    var bodies = (object[])part.GetBodies2((int)swBodyType_e.swAllBodies, false) ?? new object[0];
                    sb.Append("#B").Append(bodies.Length);
                    foreach (IBody2 b in bodies)
                    {
                        var m = (double[])b.GetMassProperties(1.0);
                        if (m != null && m.Length > 4) sb.Append(';').Append(m[3].ToString("G6")).Append('/').Append(m[4].ToString("G6"));
                    }
                    sb.Append("#M").Append(part.GetMaterialPropertyName2("", out _));
                }

                var props = model.Extension.CustomPropertyManager[""];
                var names = (object[])props?.GetNames();
                if (names != null)
                {
                    sb.Append("#P");
                    foreach (string n in names)
                    {
                        props.Get6(n, false, out var val, out _, out _, out _);
                        sb.Append(n).Append('=').Append(val).Append(';');
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append("#ERR").Append(ex.GetType().Name);
            }
            return sb.ToString();
        }
    }
}

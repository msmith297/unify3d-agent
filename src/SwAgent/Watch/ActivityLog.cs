using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SwAgent.Watch
{
    internal sealed class WatchEntry
    {
        /// <summary>Stable id (entries can be merged or dropped, so positions change).</summary>
        public int Id;

        /// <summary>Seconds since recording started.</summary>
        public double T;

        /// <summary>add | edit | delete | rename | suppress | unsuppress | select | note.</summary>
        public string Kind;

        public int FeatureId = -1;
        public string Text;

        /// <summary>Small JPEG (base64) of the view right after the change, if one was taken.</summary>
        public string Thumb;

        /// <summary>Made while the agent was running (its own changes), not by the user.</summary>
        public bool ByAgent;

        internal FeatureChange Change;

        /// <summary>For a select entry, or what was selected just before a feature was added.</summary>
        internal string Selection;

        public JObject ToJson(bool withThumb = false)
        {
            var o = new JObject { ["id"] = Id, ["t"] = Math.Round(T, 1), ["kind"] = Kind, ["text"] = Text };
            if (ByAgent) o["byAgent"] = true;
            if (withThumb && Thumb != null) o["thumb"] = Thumb;
            return o;
        }
    }

    /// <summary>
    /// The compact, semantic record "Watch me" builds while the user works. Raw changes are merged into what a
    /// person would say they did: a feature added and then tweaked is one "Added" step with its final values, a
    /// parameter dragged through several values is one "10 -> 15 mm" edit, something added and deleted again
    /// disappears, and a selection is folded into the feature it was made for.
    /// </summary>
    internal sealed class ActivityLog
    {
        public const int MaxEntries = 300;

        /// <summary>Changes to the same feature closer together than this are merged.</summary>
        public static readonly double MergeSeconds = 45;

        private readonly List<WatchEntry> _entries = new List<WatchEntry>();
        private int _nextId = 1;

        public IReadOnlyList<WatchEntry> Entries => _entries;

        /// <summary>Entries dropped because the log was full.</summary>
        public int Dropped { get; private set; }

        /// <summary>Record a feature change. Returns the entry that now describes it, or null if it cancelled out.</summary>
        public WatchEntry AddChange(FeatureChange c, double t, bool byAgent = false)
        {
            var last = LastFor(c.Id);
            var recent = last != null && t - last.T <= MergeSeconds && last.ByAgent == byAgent;
            switch (c.Kind)
            {
                case "edit" when recent && last.Kind == "add":
                    last.Change = new FeatureChange { Kind = "add", After = c.After };
                    last.Text = AddText(last);
                    return last;
                case "edit" when recent && last.Kind == "edit":
                {
                    var merged = last.Change.Params.ToList();
                    foreach (var p in c.Params)
                    {
                        var i = merged.FindIndex(m => m.Key == p.Key);
                        if (i < 0) merged.Add(p);
                        else merged[i] = (p.Key, merged[i].From, p.To);
                    }
                    merged.RemoveAll(m => m.From == m.To);
                    if (merged.Count == 0)
                    {
                        _entries.Remove(last);
                        return null;
                    }
                    last.Change = new FeatureChange { Kind = "edit", Before = last.Change.Before, After = c.After, Params = merged };
                    last.Text = WatchDiff.Describe(last.Change);
                    return last;
                }
                case "rename" when recent && last.Kind == "add":
                    last.Change = new FeatureChange { Kind = "add", After = c.After };
                    last.Text = AddText(last);
                    return last;
                case "rename" when recent && last.Kind == "rename":
                    if (last.Change.Before.Name == c.After.Name)
                    {
                        _entries.Remove(last);
                        return null;
                    }
                    last.Change = new FeatureChange { Kind = "rename", Before = last.Change.Before, After = c.After };
                    last.Text = WatchDiff.Describe(last.Change);
                    return last;
                case "suppress" when recent && last.Kind == "unsuppress":
                case "unsuppress" when recent && last.Kind == "suppress":
                    _entries.Remove(last);
                    return null;
                case "delete" when _entries.Any(e => e.FeatureId == c.Id && e.Kind == "add" && e.ByAgent == byAgent):
                    // Added during the recording and deleted again (often an undo): it never happened.
                    _entries.RemoveAll(e => e.FeatureId == c.Id);
                    return null;
            }

            var entry = new WatchEntry { Kind = c.Kind, T = t, FeatureId = c.Id, Change = c, ByAgent = byAgent };
            if (c.Kind == "add" && _entries.Count > 0 && _entries[_entries.Count - 1].Kind == "select" && !byAgent)
            {
                // What the user had just selected is almost always what the feature was built on.
                var sel = _entries[_entries.Count - 1];
                entry.Selection = sel.Selection;
                _entries.Remove(sel);
            }
            entry.Text = c.Kind == "add" ? AddText(entry) : WatchDiff.Describe(c);
            return Append(entry);
        }

        /// <summary>Record the user's selection; consecutive selections keep only the last. Empty selections are ignored.</summary>
        public WatchEntry AddSelection(string summary, double t)
        {
            if (string.IsNullOrWhiteSpace(summary)) return null;
            var last = _entries.Count > 0 ? _entries[_entries.Count - 1] : null;
            if (last != null && last.Kind == "select")
            {
                if (last.Selection == summary) return last;
                last.Selection = summary;
                last.T = t;
                last.Text = "Selected " + summary;
                return last;
            }
            return Append(new WatchEntry { Kind = "select", T = t, Selection = summary, Text = "Selected " + summary });
        }

        /// <summary>Anything else worth knowing (configuration switched, rebuild errors...).</summary>
        public WatchEntry AddNote(string text, double t) => Append(new WatchEntry { Kind = "note", T = t, Text = text });

        public WatchEntry Find(int id) => _entries.FirstOrDefault(e => e.Id == id);

        public int Count(string kind) => _entries.Count(e => e.Kind == kind);

        /// <summary>Numbered lines ("3. [1:05] Added Fillet3 ..."), kept within maxChars by eliding the middle.</summary>
        public string ToText(int maxChars = 14000)
        {
            var lines = _entries.Select((e, i) => $"{i + 1}. [{Clock(e.T)}] {e.Text}{(e.ByAgent ? " (by the agent)" : "")}").ToList();
            if (Dropped > 0) lines.Insert(0, $"(the first {Dropped} steps were dropped - the log is limited to {MaxEntries})");
            var total = lines.Sum(l => l.Length + 1);
            if (total <= maxChars) return string.Join("\n", lines);

            const int head = 5;
            var sb = new StringBuilder();
            foreach (var l in lines.Take(head)) sb.AppendLine(l);
            var tail = new List<string>();
            var budget = maxChars - sb.Length - 60;
            for (var i = lines.Count - 1; i >= head && budget - (lines[i].Length + 1) > 0; i--)
            {
                tail.Insert(0, lines[i]);
                budget -= lines[i].Length + 1;
            }
            sb.AppendLine($"... ({lines.Count - head - tail.Count} steps omitted to keep the log short) ...");
            foreach (var l in tail) sb.AppendLine(l);
            return sb.ToString().TrimEnd();
        }

        internal static string Clock(double seconds)
        {
            var s = (int)Math.Max(0, seconds);
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }

        private static string AddText(WatchEntry e)
        {
            var text = WatchDiff.Describe(e.Change);
            return string.IsNullOrEmpty(e.Selection) ? text : $"{text} - with {e.Selection} selected";
        }

        private WatchEntry LastFor(int featureId)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].FeatureId == featureId) return _entries[i];
                if (_entries[i].Kind != "select") return null; // something else happened in between: don't merge across it
            }
            return null;
        }

        private WatchEntry Append(WatchEntry e)
        {
            e.Id = _nextId++;
            _entries.Add(e);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(0);
                Dropped++;
            }
            return e;
        }
    }

    /// <summary>The text the agent receives when the user stops "Watch me".</summary>
    internal static class WatchReport
    {
        public const int MaxChars = 20000;

        /// <param name="final">The feature tree when recording stopped; the features the log touches are listed with every parameter.</param>
        public static string Compose(string title, string docType, double seconds, ActivityLog log, IReadOnlyList<FeatureState> final, int featuresBefore)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Recorded with \"Watch me\" in {title}{(string.IsNullOrEmpty(docType) ? "" : $" ({docType})")} over {WatchFormat.Duration(seconds)}: " +
                          $"{log.Entries.Count} step{(log.Entries.Count == 1 ? "" : "s")}, feature tree {featuresBefore} -> {final?.Count ?? featuresBefore} features.");
            if (log.Entries.Count == 0)
            {
                sb.AppendLine("Nothing changed in the model while recording.");
                return sb.ToString().TrimEnd();
            }
            sb.AppendLine("Steps (lengths in mm, angles in degrees; dimension names like D1@Sketch2 are the SolidWorks full names):");
            sb.AppendLine(log.ToText(MaxChars * 2 / 3));

            var touched = new HashSet<int>(log.Entries.Where(e => e.FeatureId >= 0 && e.Kind != "delete").Select(e => e.FeatureId));
            var detail = (final ?? new FeatureState[0]).Where(f => touched.Contains(f.Id)).ToList();
            if (detail.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Final parameters of the features above:");
                var budget = MaxChars - sb.Length;
                foreach (var f in detail)
                {
                    var line = $"- {f.Label}{(string.IsNullOrEmpty(f.Parents) ? "" : $" <- {f.Parents}")}{(f.Suppressed ? " SUPPRESSED" : "")}" +
                               (f.Params.Count == 0 ? "" : ": " + string.Join("; ", f.Params.Select(p => $"{p.Key} {p.Value}")));
                    if (line.Length > budget)
                    {
                        sb.AppendLine("- ... (more features - call describe_model_history for the full recipe)");
                        break;
                    }
                    sb.AppendLine(line);
                    budget -= line.Length + 2;
                }
            }
            return sb.ToString().TrimEnd();
        }
    }

    internal static class WatchFormat
    {
        public static string Duration(double seconds)
        {
            var s = (int)Math.Round(seconds);
            return s < 60 ? $"{s} s" : s < 3600 ? $"{s / 60} min {s % 60} s" : $"{s / 3600} h {s / 60 % 60} min";
        }

        public static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

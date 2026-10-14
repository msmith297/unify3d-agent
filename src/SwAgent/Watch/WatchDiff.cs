using System;
using System.Collections.Generic;
using System.Linq;

namespace SwAgent.Watch
{
    /// <summary>One feature as "Watch me" sees it: identity, and its parameters as readable values ("radius" = "2 mm").</summary>
    internal sealed class FeatureState
    {
        public int Id;
        public string Name;

        /// <summary>SolidWorks type name (GetTypeName2), e.g. "Fillet".</summary>
        public string Type;

        /// <summary>Readable type, e.g. "Fillet", "Boss-Extrude", "Sketch".</summary>
        public string Kind;

        public bool Suppressed;

        /// <summary>What it is, in a few words: "constant radius 2 mm on 4 edges".</summary>
        public string Summary;

        /// <summary>Parent features ("Front Plane", "Boss-Extrude1"), comma-separated.</summary>
        public string Parents;

        /// <summary>Parameters in a stable order; values carry their units ("blind 10 mm", "2 mm", "6").</summary>
        public List<KeyValuePair<string, string>> Params = new List<KeyValuePair<string, string>>();

        public string Param(string key) => Params.FirstOrDefault(p => p.Key == key).Value;

        public string Label => string.IsNullOrEmpty(Kind) || Kind == Name ? Name : $"{Name} ({Kind})";
    }

    /// <summary>A change between two feature snapshots; a parameter change carries its old and new value.</summary>
    internal sealed class FeatureChange
    {
        public string Kind; // add | edit | delete | rename | suppress | unsuppress
        public FeatureState Before;
        public FeatureState After;
        public List<(string Key, string From, string To)> Params = new List<(string, string, string)>();

        public int Id => (After ?? Before).Id;
    }

    /// <summary>Compares two snapshots of the feature tree (by feature id, so renames are recognised).</summary>
    internal static class WatchDiff
    {
        public static List<FeatureChange> Diff(IReadOnlyList<FeatureState> before, IReadOnlyList<FeatureState> after)
        {
            var changes = new List<FeatureChange>();
            var old = new Dictionary<int, FeatureState>();
            foreach (var f in before ?? new FeatureState[0]) old[f.Id] = f;
            var now = new HashSet<int>((after ?? new FeatureState[0]).Select(f => f.Id));

            foreach (var f in after ?? new FeatureState[0])
            {
                if (!old.TryGetValue(f.Id, out var was))
                {
                    changes.Add(new FeatureChange { Kind = "add", After = f });
                    continue;
                }
                if (was.Name != f.Name) changes.Add(new FeatureChange { Kind = "rename", Before = was, After = f });
                if (was.Suppressed != f.Suppressed)
                {
                    changes.Add(new FeatureChange { Kind = f.Suppressed ? "suppress" : "unsuppress", Before = was, After = f });
                    continue; // a suppressed feature's parameters can't be read reliably
                }
                var edit = ParamChanges(was, f);
                if (edit.Count > 0) changes.Add(new FeatureChange { Kind = "edit", Before = was, After = f, Params = edit });
            }
            foreach (var f in before ?? new FeatureState[0])
            {
                if (!now.Contains(f.Id)) changes.Add(new FeatureChange { Kind = "delete", Before = f });
            }
            return changes;
        }

        internal static List<(string, string, string)> ParamChanges(FeatureState a, FeatureState b)
        {
            var result = new List<(string, string, string)>();
            var keys = a.Params.Select(p => p.Key).Concat(b.Params.Select(p => p.Key)).Distinct();
            foreach (var key in keys)
            {
                var from = a.Param(key);
                var to = b.Param(key);
                if (from != to) result.Add((key, from, to));
            }
            // A dimension that drives a named parameter shows the same change twice ("depth 10 -> 15 mm" and
            // "D1@Boss-Extrude1 10 -> 15 mm"): keep the readable one.
            return result.Where(c => !(c.Item1.Contains("@") && result.Any(o => !o.Item1.Contains("@") && EndsWith(o.Item2, c.Item2) && EndsWith(o.Item3, c.Item3))))
                .ToList();
        }

        private static bool EndsWith(string named, string dim) => named == dim || (named != null && dim != null && named.EndsWith(" " + dim, StringComparison.Ordinal));

        /// <summary>"Added Fillet3 (Fillet): constant radius 2 mm on 4 edges", "Edited Boss-Extrude1: depth 10 mm -> 15 mm", ...</summary>
        public static string Describe(FeatureChange c)
        {
            switch (c.Kind)
            {
                case "add":
                {
                    var f = c.After;
                    var text = $"Added {f.Label}";
                    if (!string.IsNullOrEmpty(f.Summary)) text += ": " + f.Summary;
                    if (!string.IsNullOrEmpty(f.Parents) && (f.Summary == null || f.Summary.IndexOf(f.Parents, StringComparison.Ordinal) < 0)) text += $" (from {f.Parents})";
                    return text;
                }
                case "edit":
                    return $"Edited {c.After.Label}: " + string.Join("; ", c.Params.Select(p => $"{p.Key} {p.From ?? "(none)"} -> {p.To ?? "(none)"}"));
                case "delete":
                    return $"Deleted {c.Before.Label}";
                case "rename":
                    return $"Renamed {c.Before.Name} -> {c.After.Name}";
                case "suppress":
                    return $"Suppressed {c.After.Label}";
                case "unsuppress":
                    return $"Unsuppressed {c.After.Label}";
                default:
                    return $"{c.Kind} {(c.After ?? c.Before).Label}";
            }
        }
    }

    /// <summary>Selections in a few words: "4 edges of Boss-Extrude1, 1 face of Cut-Extrude1".</summary>
    internal static class SelectionSummary
    {
        public static string Summarize(IEnumerable<(string Type, string Label, string Detail)> items)
        {
            var list = items?.ToList() ?? new List<(string, string, string)>();
            if (list.Count == 0) return null;
            // One or two entities: say exactly what they are (geometry included).
            if (list.Count <= 2)
            {
                return string.Join("; ", list.Select(i => $"{Lower(i.Type)}{(string.IsNullOrEmpty(i.Label) ? "" : " of " + i.Label)}" +
                                                         (string.IsNullOrEmpty(i.Detail) ? "" : $" ({i.Detail})")));
            }
            return string.Join(", ", list.GroupBy(i => (Lower(i.Type), i.Label ?? ""))
                .Select(g => $"{g.Count()} {Plural(g.Key.Item1, g.Count())}{(g.Key.Item2 == "" ? "" : " of " + g.Key.Item2)}"));
        }

        private static string Lower(string type) => (type ?? "entity").ToLowerInvariant();

        internal static string Plural(string word, int n)
        {
            if (n == 1) return word;
            if (word.EndsWith("vertex")) return word.Substring(0, word.Length - 2) + "ices";
            if (word.EndsWith("y") && !word.EndsWith("ey")) return word.Substring(0, word.Length - 1) + "ies"; // body -> bodies
            if (word.EndsWith("x") || word.EndsWith("s") || word.EndsWith("ch")) return word + "es";
            return word + "s";
        }
    }
}

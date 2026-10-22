using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SwAgent.Scripting
{
    /// <summary>
    /// The self-improving loop's prompts in execute_csharp results:
    /// <list type="bullet">
    /// <item>the nudge: a cell that works after failed ones (in the same chat, within the last 4 cells) asks the agent to save
    ///   what it learned as an API note and, when the pattern can recur, a helper;</item>
    /// <item>hints: a cell that calls a raw API member a helper wraps is told which helper does it in one call;</item>
    /// <item>notes: a failing cell that calls a member an API note is about gets that note next to its error.</item>
    /// </list>
    /// Pure: no SolidWorks, no files (the caller passes the helpers and notes).
    /// </summary>
    internal sealed class CellCoach
    {
        /// <summary>The cells looked at: this one and the 3 before it.</summary>
        public const int Window = 4;

        private sealed class Outcome
        {
            public bool Ok;
            public string What;
        }

        private readonly ConcurrentDictionary<string, List<Outcome>> _history = new ConcurrentDictionary<string, List<Outcome>>();

        /// <summary>
        /// Record a cell of a chat. failure: what went wrong ("CS1061", "COMException"), null when it worked. Returns the nudge
        /// when it worked after one or more failed cells within the window (once: the history starts again after it).
        /// </summary>
        public string Record(string thread, bool ok, string failure = null)
        {
            var list = _history.GetOrAdd(thread ?? "default", _ => new List<Outcome>());
            lock (list)
            {
                var before = list.Skip(Math.Max(0, list.Count - (Window - 1))).ToList();
                list.Add(new Outcome { Ok = ok, What = failure });
                while (list.Count > Window) list.RemoveAt(0);
                if (!ok) return null;
                var failed = before.Where(o => !o.Ok).ToList();
                if (failed.Count == 0) return null;
                list.Clear();
                return Nudge(failed.Count, failed.Select(f => f.What));
            }
        }

        /// <summary>Forget a chat's cells (new chat / reset).</summary>
        public void Reset(string thread)
        {
            if (thread == null) return;
            _history.TryRemove(thread, out _);
            _hinted.TryRemove(thread, out _);
        }

        internal static string Nudge(int failures, IEnumerable<string> what)
        {
            var kinds = (what ?? Enumerable.Empty<string>()).Where(w => !string.IsNullOrEmpty(w)).Distinct().Take(4).ToList();
            return $"That worked after {failures} failed {(failures == 1 ? "try" : "tries")}{(kinds.Count > 0 ? " (" + string.Join(", ", kinds) + ")" : "")}. " +
                   "Make it permanent: save_api_note(what was wrong, what works) and, if the pattern can recur, save_helper with a robust helper " +
                   "(an extension method can make the failing form work too).";
        }

        /// <summary>
        /// One line per helper that wraps a member the cell calls raw, e.g. "Bodies.Rotate does this in one call (instead of
        /// InsertMoveCopyBody2): Bodies.Rotate(IBody2 body, ...)". At most max lines; helpers in the given order (HelperIndex.Merge:
        /// the agent's own, the plugins', the built-in ones); helpers named in skip are left out.
        /// </summary>
        public static List<(string Helper, string Line)> Hints(ISet<string> rawCalls, IEnumerable<HelperInfo> helpers, int max = 3, ICollection<string> skip = null)
        {
            var lines = new List<(string, string)>();
            if (rawCalls == null || rawCalls.Count == 0) return lines;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in helpers ?? Enumerable.Empty<HelperInfo>())
            {
                if (h?.Name == null || !seen.Add(h.Name) || (skip != null && skip.Contains(h.Name))) continue;
                var members = h.WrappedMembers().Where(rawCalls.Contains).Distinct().ToList();
                if (members.Count == 0) continue;
                var sig = string.IsNullOrWhiteSpace(h.Signature) ? "" : ": " + h.Signature.Trim();
                var source = string.IsNullOrEmpty(h.Source) || h.Source == "built-in" ? "" : $" [{h.Source}]";
                lines.Add((h.Name, $"{h.Name}{source} does this in one call (instead of {string.Join(", ", members)}){sig}"));
                if (lines.Count >= max) break;
            }
            return lines;
        }

        private readonly ConcurrentDictionary<string, HashSet<string>> _hinted = new ConcurrentDictionary<string, HashSet<string>>();

        /// <summary>The helpers this chat's results already named (a cell that worked isn't told about them again).</summary>
        public ICollection<string> Hinted(string thread)
        {
            var set = _hinted.GetOrAdd(thread ?? "default", _ => new HashSet<string>(StringComparer.Ordinal));
            lock (set) return set.ToList();
        }

        public void MarkHinted(string thread, IEnumerable<string> helpers)
        {
            var set = _hinted.GetOrAdd(thread ?? "default", _ => new HashSet<string>(StringComparer.Ordinal));
            lock (set) foreach (var h in helpers ?? Enumerable.Empty<string>()) set.Add(h);
        }

        /// <summary>What went wrong, short: the compiler error ids, or the exception type.</summary>
        public static string FailureKind(string report)
        {
            if (string.IsNullOrEmpty(report)) return null;
            var ids = Regex.Matches(report, @"\b(CS\d{4})\b").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().Take(3).ToList();
            if (ids.Count > 0) return string.Join("/", ids);
            var ex = Regex.Match(report, @"^([A-Za-z0-9_.]+(Exception|Error)):", RegexOptions.Multiline);
            if (!ex.Success) return null;
            var name = ex.Groups[1].Value;
            return name.Substring(name.LastIndexOf('.') + 1);
        }
    }
}

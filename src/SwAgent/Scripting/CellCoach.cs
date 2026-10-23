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
    /// <item>the nudge: a cell that works after API-shaped failures (wrong interface or arguments, a COM error, a bad cast, a
    ///   helper that refused; in the same chat, within the last 4 cells) that called the same API members asks the agent to
    ///   save what it learned as an API note and, when the pattern can recur, a helper. Typos, the cell's own exceptions and
    ///   unrelated cells don't count, and saving a note or helper settles the failures before it;</item>
    /// <item>hints: a cell that calls a raw API member a helper wraps is told which helper does it in one call;</item>
    /// <item>notes: a failing cell that calls a member an API note is about gets that note next to its error.</item>
    /// </list>
    /// Pure: no SolidWorks, no files (the caller passes the helpers and notes).
    /// </summary>
    internal sealed class CellCoach
    {
        /// <summary>The cells looked at: this one and the 3 before it.</summary>
        public const int Window = 4;

        /// <summary>Compile errors that say the API was used wrong (member on another interface, arguments, conversions, array indexing).</summary>
        internal static readonly HashSet<string> ApiErrors = new HashSet<string>(StringComparer.Ordinal)
        {
            "CS1061", "CS1929", "CS0117", "CS1501", "CS7036", "CS1739", "CS1503", "CS0029", "CS0266", "CS0030", "CS0021", "CS0619", "CS1955",
        };

        /// <summary>Run-time failures that come from the API: a COM error, a bad cast, a null the API returned, a helper that refused.</summary>
        internal static readonly HashSet<string> ApiExceptions = new HashSet<string>(StringComparer.Ordinal)
        {
            "COMException", "InvalidCastException", "NullReferenceException", "SwHelperException", "MoldApiException", "ArgumentException",
        };

        /// <summary>Members every cell calls: sharing one of these doesn't make two cells about the same thing.</summary>
        private static readonly HashSet<string> Common = new HashSet<string>(StringComparer.Ordinal)
        {
            "ClearSelection2", "EditRebuild3", "ForceRebuild3", "GetTypeName2", "GetNextFeature", "FirstFeature", "GetSpecificFeature2",
            "AsDoubles", "AsObjects", "AsInts", "AsStrings", "Bodies", "Features", "LastFeature", "SelMgr", "Volume", "Box", "Centroid",
            "All", "Named", "ByName", "Last", "GetType",
        };

        private sealed class Outcome
        {
            public bool Ok;
            public string What;
            public bool Learnable;
            public HashSet<string> Api;
        }

        private readonly ConcurrentDictionary<string, List<Outcome>> _history = new ConcurrentDictionary<string, List<Outcome>>();

        /// <summary>
        /// Record a cell of a chat. failure: what went wrong ("CS1061", "COMException"), null when it worked; api: the API and
        /// helper members it calls (ScriptHost.Inspect); learnable: a failure about the API (Learnable). Returns the nudge when
        /// it worked after one or more learnable failed cells within the window that call one of the same members (once: the
        /// history starts again after it).
        /// </summary>
        public string Record(string thread, bool ok, string failure = null, ISet<string> api = null, bool learnable = true)
        {
            var list = _history.GetOrAdd(thread ?? "default", _ => new List<Outcome>());
            lock (list)
            {
                var before = list.Skip(Math.Max(0, list.Count - (Window - 1))).ToList();
                var calls = new HashSet<string>((api ?? Enumerable.Empty<string>()).Where(n => !Common.Contains(n)), StringComparer.Ordinal);
                list.Add(new Outcome { Ok = ok, What = failure, Learnable = !ok && learnable, Api = calls });
                while (list.Count > Window) list.RemoveAt(0);
                if (!ok) return null;
                var failed = before.Where(o => !o.Ok && o.Learnable && (api == null || o.Api.Overlaps(calls))).ToList();
                if (failed.Count == 0) return null;
                list.Clear();
                var shared = api == null ? new List<string>() : failed.SelectMany(f => f.Api).Where(calls.Contains).Distinct().Take(3).ToList();
                return Nudge(failed.Count, failed.Select(f => f.What), shared);
            }
        }

        /// <summary>The agent saved what it learned (an API note, a helper, a helper test): the failures before it are settled.</summary>
        public void Learned(string thread)
        {
            if (thread != null && _history.TryGetValue(thread, out var list))
                lock (list) list.Clear();
        }

        /// <summary>Forget a chat's cells (new chat / reset).</summary>
        public void Reset(string thread)
        {
            if (thread == null) return;
            _history.TryRemove(thread, out _);
            _hinted.TryRemove(thread, out _);
        }

        /// <summary>
        /// Whether a failed cell's report is about the API, so a fix is worth a note or a helper: an API-shaped compile error
        /// (ApiErrors; not syntax or a name typo), or an API-shaped exception (ApiExceptions) that the cell didn't throw itself.
        /// </summary>
        internal static bool Learnable(string report, string code)
        {
            if (string.IsNullOrEmpty(report)) return false;
            if (report.StartsWith("Compilation failed", StringComparison.Ordinal))
                return Regex.Matches(report, @"\b(CS\d{4})\b").Cast<Match>().Any(m => ApiErrors.Contains(m.Groups[1].Value));
            var kind = FailureKind(report);
            if (kind == null || !ApiExceptions.Contains(kind)) return false;
            return code == null || !Regex.IsMatch(code, @"\bthrow\s+new\s+(?:[\w.]+\.)?" + Regex.Escape(kind) + @"\b");
        }

        internal static string Nudge(int failures, IEnumerable<string> what, IEnumerable<string> members = null)
        {
            var kinds = (what ?? Enumerable.Empty<string>()).Where(w => !string.IsNullOrEmpty(w)).Distinct().Take(4).ToList();
            var about = (members ?? Enumerable.Empty<string>()).ToList();
            return $"That worked after {failures} failed {(failures == 1 ? "try" : "tries")}{(kinds.Count > 0 ? " (" + string.Join(", ", kinds) + ")" : "")}" +
                   $"{(about.Count > 0 ? " with " + string.Join(", ", about) : "")}. " +
                   "If the failure was about the API (not a typo or your own logic), keep the lesson: save_api_note(what was wrong, what works) and, " +
                   "if the pattern can recur, save_helper with a robust helper (an extension method can make the failing form work too).";
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

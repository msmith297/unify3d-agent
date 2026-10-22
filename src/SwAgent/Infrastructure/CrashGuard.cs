using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// A flight recorder for code that runs inside SolidWorks: tool previews, tool macros and the agent's C# cells.
    /// That code calls the SolidWorks API in-process, and a bad call can end in a native access violation that no
    /// .NET handler can catch - SolidWorks just closes. So before each such step we write down what is about to run,
    /// and clear it afterwards. If SolidWorks dies mid-step, the note is still there at the next start: we know
    /// exactly which tool and which part of it crashed, pause that part so clicking the tool can't crash again, and
    /// hand the code and details to the agent to find and fix the cause.
    /// </summary>
    internal static class CrashGuard
    {
        private static string FilePath => Path.Combine(Paths.LocalDir, "crash-guard.json");
        private static int _depth;
        private static JObject _note;

        /// <summary>
        /// About to run something risky. kind: "preview", "macro" or "cell". tool: the user tool's name (null for chat
        /// cells). code: what will run, so the agent can look at it if this is the last thing SolidWorks did.
        /// </summary>
        public static IDisposable Enter(string kind, string tool, string title, string code)
        {
            // Nested steps (a tool macro runs through the C# cell runner) keep the outermost description.
            if (_depth++ > 0) return new Exit();
            _note = new JObject
            {
                ["kind"] = kind,
                ["tool"] = tool,
                ["title"] = title,
                ["code"] = code?.Length > 20000 ? code.Substring(0, 20000) : code,
                ["at"] = DateTime.Now.ToString("o"),
            };
            try
            {
                File.WriteAllText(FilePath, _note.ToString());
            }
            catch
            {
                // The guard must never stop the work it guards.
            }
            return new Exit();
        }

        /// <summary>
        /// Add to what is running now (outermost step included), e.g. the helpers a cell calls: a crash record then names them.
        /// Values are merged into a list under key. Nothing happens when no step is running.
        /// </summary>
        public static void Annotate(string key, System.Collections.Generic.IEnumerable<string> values)
        {
            if (_depth <= 0 || _note == null) return;
            try
            {
                var list = _note[key] as JArray ?? new JArray();
                foreach (var v in values ?? Enumerable.Empty<string>())
                    if (!list.Any(x => (string)x == v)) list.Add(v);
                _note[key] = list;
                File.WriteAllText(FilePath, _note.ToString());
            }
            catch
            {
                // The guard must never stop the work it guards.
            }
        }

        private sealed class Exit : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                if (--_depth > 0) return;
                _note = null;
                try
                {
                    File.Delete(FilePath);
                }
                catch
                {
                    // Left over; overwritten by the next Enter.
                }
            }
        }

        /// <summary>
        /// At start-up: if a note survived, SolidWorks closed while that step ran. Returns what crashed (with the
        /// tail of our log) and removes the note, or null if the last session ended normally.
        /// </summary>
        public static JObject Recover()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var note = JObject.Parse(File.ReadAllText(FilePath));
                File.Delete(FilePath);
                note["logTail"] = LogTail(40);
                // The agent's helpers that code called: count the crash, and disable a helper involved in two.
                Scripting.HelperCrashes.Record(note);
                Log.Warn($"SolidWorks closed while running {note["kind"]} of {(string)note["tool"] ?? "a chat"}: {note["title"]}");
                return note;
            }
            catch (Exception ex)
            {
                Log.Warn("Reading the crash guard failed: " + ex.Message);
                return null;
            }
        }

        private static string LogTail(int lines)
        {
            try
            {
                var file = Directory.GetFiles(Paths.LogDir, "swagent-*.log").OrderBy(f => f).LastOrDefault();
                if (file == null) return null;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream))
                {
                    var all = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
                    return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines)));
                }
            }
            catch
            {
                return null;
            }
        }
    }
}

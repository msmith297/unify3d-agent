using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SwAgent.Infrastructure;

namespace SwAgent.Scripting
{
    /// <summary>A lesson the agent saved about the SolidWorks API (save_api_note): what went wrong and what works.</summary>
    internal sealed class ApiNote
    {
        public string Id;
        public string Topic;
        public string Wrong;
        public string Right;
        public string[] Apis = new string[0];
        public DateTime Created;
        public DateTime? Updated;

        /// <summary>How often it was useful: found by list_api_notes, or shown next to a failing cell that used its APIs.</summary>
        public int Hits;
    }

    /// <summary>
    /// What is known about a helper (helpers.json). User helpers have all of it; a plugin's helper only gets a record once it
    /// has crash or use state (its code and summary live in the plugin).
    /// </summary>
    internal sealed class HelperRecord
    {
        public string Name;

        /// <summary>"user" or "plugin:&lt;name&gt;".</summary>
        public string Source = "user";

        public string Signature;
        public string Summary;
        public string[] Wraps = new string[0];
        public DateTime Created;
        public DateTime Updated;

        /// <summary>Cells that called it.</summary>
        public int Uses;

        /// <summary>1 for the first save; each save keeps the previous file as &lt;name&gt;.v&lt;n&gt;.cs.</summary>
        public int Version;

        /// <summary>Left out of the helpers assembly (after crashes, or by hand). DisabledReason says why.</summary>
        public bool Disabled;

        public string DisabledReason;

        /// <summary>SolidWorks closed while a cell that called it ran (CrashGuard); 2 disables it.</summary>
        public int Crashes;

        /// <summary>Why the last build skipped it (compile errors, a name clash); null when it is in the assembly.</summary>
        public string LastError;

        /// <summary>The last test_helper run: "ok" or the first line of its error, with the time.</summary>
        public string LastTest;
    }

    /// <summary>
    /// The agent's own knowledge, per PC: %APPDATA%\SwAgent\agent-knowledge\notes.json (API notes) and helpers\ (one
    /// &lt;name&gt;.cs per helper, the previous versions as &lt;name&gt;.v&lt;n&gt;.cs, and helpers.json). Under Paths, so
    /// SWAGENT_DATA_DIR redirects it for tests. Files only: safe on any thread.
    /// </summary>
    internal sealed class KnowledgeStore
    {
        public const int CrashesToDisable = 2;

        /// <summary>A helper's name is its class name in namespace SwAgent.UserHelpers.</summary>
        public static readonly Regex NamePattern = new Regex("^[A-Z][A-Za-z0-9_]{2,47}$");

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        private static KnowledgeStore _instance;

        /// <summary>The add-in's store (under Paths). Tests may swap it.</summary>
        public static KnowledgeStore Instance
        {
            get => _instance ?? (_instance = new KnowledgeStore());
            internal set => _instance = value;
        }

        private readonly object _lock = new object();

        public KnowledgeStore(string root = null)
        {
            Root = root ?? Path.Combine(Paths.RoamingDir, "agent-knowledge");
        }

        public string Root { get; }
        public string NotesFile => Path.Combine(Root, "notes.json");
        public string HelpersDir => Path.Combine(Root, "helpers");
        public string HelpersFile => Path.Combine(HelpersDir, "helpers.json");

        /// <summary>API notes changed (saved, deleted). Raised on the calling thread.</summary>
        public event Action NotesChanged;

        /// <summary>User helpers changed (saved, deleted, disabled): the helpers assembly must be built again.</summary>
        public event Action HelpersChanged;

        private void RaiseChanged(bool helpers = false)
        {
            try
            {
                if (helpers) HelpersChanged?.Invoke();
                else NotesChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("A knowledge.changed handler failed", ex);
            }
        }

        // ---------------------------------------------------------------- API notes

        public List<ApiNote> Notes()
        {
            lock (_lock) return Read<NotesFileJson>(NotesFile).Notes;
        }

        /// <summary>
        /// Save a note; a note with the same topic (ignoring case) is updated instead of duplicated (its hits stay). Returns the
        /// note and whether it was new.
        /// </summary>
        public (ApiNote Note, bool Created) SaveNote(string topic, string wrong, string right, IEnumerable<string> apis)
        {
            topic = Clean(topic, 120);
            wrong = Clean(wrong, 1200);
            right = Clean(right, 1200);
            if (string.IsNullOrEmpty(topic)) throw new ArgumentException("topic is empty");
            if (string.IsNullOrEmpty(right)) throw new ArgumentException("right (what works) is empty");
            var apiList = (apis ?? Enumerable.Empty<string>()).Select(a => Clean(a, 120)).Where(a => !string.IsNullOrEmpty(a))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
            ApiNote note;
            bool created;
            lock (_lock)
            {
                var file = Read<NotesFileJson>(NotesFile);
                note = file.Notes.FirstOrDefault(n => string.Equals(n.Topic, topic, StringComparison.OrdinalIgnoreCase));
                created = note == null;
                if (created)
                {
                    note = new ApiNote { Id = NewNoteId(file.Notes), Topic = topic, Created = DateTime.UtcNow };
                    file.Notes.Add(note);
                }
                else
                {
                    note.Updated = DateTime.UtcNow;
                }
                note.Wrong = wrong;
                note.Right = right;
                note.Apis = apiList.Length > 0 || created ? apiList : note.Apis;
                Write(NotesFile, file);
            }
            RaiseChanged();
            return (note, created);
        }

        public bool DeleteNote(string id)
        {
            bool removed;
            lock (_lock)
            {
                var file = Read<NotesFileJson>(NotesFile);
                removed = file.Notes.RemoveAll(n => n.Id == id) > 0;
                if (removed) Write(NotesFile, file);
            }
            if (removed) RaiseChanged();
            return removed;
        }

        /// <summary>Count a use of these notes (list_api_notes found them, or they were shown next to a failing cell).</summary>
        public void Hit(IEnumerable<string> ids)
        {
            var set = new HashSet<string>(ids ?? Enumerable.Empty<string>());
            if (set.Count == 0) return;
            lock (_lock)
            {
                var file = Read<NotesFileJson>(NotesFile);
                foreach (var n in file.Notes.Where(n => set.Contains(n.Id))) n.Hits++;
                Write(NotesFile, file);
            }
        }

        /// <summary>Notes matching every word of the query (topic, wrong, right, apis); all notes for an empty query. Most hits first.</summary>
        public List<ApiNote> FindNotes(string query)
        {
            var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ', ',', ';', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return Notes()
                .Where(n =>
                {
                    var text = (n.Topic + " " + n.Wrong + " " + n.Right + " " + string.Join(" ", n.Apis ?? new string[0])).ToLowerInvariant();
                    return words.All(text.Contains);
                })
                .OrderByDescending(n => n.Hits).ThenByDescending(n => n.Updated ?? n.Created)
                .ToList();
        }

        /// <summary>Notes about API members the code calls (by member name: "IPartDoc.FeatureByName" matches ".FeatureByName(").</summary>
        public List<ApiNote> NotesForMembers(ISet<string> members)
        {
            if (members == null || members.Count == 0) return new List<ApiNote>();
            return Notes()
                .Where(n => (n.Apis ?? new string[0]).Select(a => a.Substring(a.LastIndexOf('.') + 1)).Any(members.Contains))
                .OrderByDescending(n => n.Hits)
                .ToList();
        }

        private static string NewNoteId(List<ApiNote> notes)
        {
            var max = notes.Select(n => n.Id != null && n.Id.StartsWith("n") && int.TryParse(n.Id.Substring(1), out var k) ? k : 0).DefaultIfEmpty(0).Max();
            return "n" + (max + 1);
        }

        // ---------------------------------------------------------------- helpers

        public List<HelperRecord> Helpers()
        {
            lock (_lock) return Read<HelpersFileJson>(HelpersFile).Helpers;
        }

        public HelperRecord Helper(string name) => Helpers().FirstOrDefault(h => h.Name == name);

        public string HelperPath(string name) => Path.Combine(HelpersDir, name + ".cs");

        /// <summary>The code of a user helper; null when there is none.</summary>
        public string HelperCode(string name)
        {
            var path = HelperPath(name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>
        /// The user helpers to build: each with a record and a file, not disabled. Name and code; the caller validates.
        /// </summary>
        public List<(HelperRecord Record, string Code)> EnabledUserHelpers()
        {
            var list = new List<(HelperRecord, string)>();
            foreach (var r in Helpers().Where(h => h.Source == "user" && !h.Disabled).OrderBy(h => h.Created).ThenBy(h => h.Name, StringComparer.Ordinal))
            {
                var code = HelperCode(r.Name);
                if (code != null) list.Add((r, code));
            }
            return list;
        }

        /// <summary>Save a user helper's code (the previous file is kept as &lt;name&gt;.v&lt;n&gt;.cs) and its record. Re-enables it.</summary>
        public HelperRecord SaveHelper(string name, string code, string summary, IEnumerable<string> wraps, string signature)
        {
            HelperRecord record;
            lock (_lock)
            {
                Directory.CreateDirectory(HelpersDir);
                var file = Read<HelpersFileJson>(HelpersFile);
                record = file.Helpers.FirstOrDefault(h => h.Name == name && h.Source == "user");
                var now = DateTime.UtcNow;
                var path = HelperPath(name);
                if (record == null)
                {
                    record = new HelperRecord { Name = name, Source = "user", Created = now, Version = 0 };
                    file.Helpers.Add(record);
                }
                if (File.Exists(path))
                {
                    var keep = Path.Combine(HelpersDir, $"{name}.v{Math.Max(1, record.Version)}.cs");
                    if (File.Exists(keep)) File.Delete(keep);
                    File.Copy(path, keep);
                }
                WriteText(path, code);
                record.Version = Math.Max(1, record.Version + 1);
                record.Updated = now;
                record.Summary = Clean(summary, 300);
                record.Wraps = (wraps ?? Enumerable.Empty<string>()).Select(w => Clean(w, 120)).Where(w => !string.IsNullOrEmpty(w)).Distinct().Take(16).ToArray();
                record.Signature = signature;
                record.Disabled = false;
                record.DisabledReason = null;
                record.Crashes = 0;
                record.LastError = null;
                record.LastTest = null;
                Write(HelpersFile, file);
            }
            RaiseChanged(helpers: true);
            return record;
        }

        /// <summary>Delete a user helper: its file becomes &lt;name&gt;.v&lt;n&gt;.cs (kept), its record goes. False when there is none.</summary>
        public bool DeleteHelper(string name)
        {
            lock (_lock)
            {
                var file = Read<HelpersFileJson>(HelpersFile);
                var record = file.Helpers.FirstOrDefault(h => h.Name == name && h.Source == "user");
                if (record == null) return false;
                var path = HelperPath(name);
                if (File.Exists(path))
                {
                    var keep = Path.Combine(HelpersDir, $"{name}.v{Math.Max(1, record.Version)}.cs");
                    if (File.Exists(keep)) File.Delete(keep);
                    File.Move(path, keep);
                }
                file.Helpers.Remove(record);
                Write(HelpersFile, file);
            }
            RaiseChanged(helpers: true);
            return true;
        }

        /// <summary>Change records (helpers.json) under the lock; creates a record for a plugin helper when needed. No event.</summary>
        public void UpdateHelpers(Action<Func<string, string, HelperRecord>> change)
        {
            lock (_lock)
            {
                var file = Read<HelpersFileJson>(HelpersFile);
                var dirty = false;
                change((name, source) =>
                {
                    var r = file.Helpers.FirstOrDefault(h => h.Name == name && (source == null || h.Source == source));
                    if (r == null && source != null && source != "user")
                    {
                        r = new HelperRecord { Name = name, Source = source, Created = DateTime.UtcNow, Updated = DateTime.UtcNow };
                        file.Helpers.Add(r);
                    }
                    dirty |= r != null;
                    return r;
                });
                if (dirty) Write(HelpersFile, file);
            }
        }

        /// <summary>Count the cells that called these helpers (by name).</summary>
        public void CountUses(IEnumerable<(string Name, string Source)> used)
        {
            var list = (used ?? Enumerable.Empty<(string, string)>()).ToList();
            if (list.Count == 0) return;
            UpdateHelpers(get =>
            {
                foreach (var (name, source) in list)
                {
                    var r = get(name, source);
                    if (r != null) r.Uses++;
                }
            });
        }

        /// <summary>
        /// SolidWorks closed while a cell that called these helpers ran (CrashGuard.Recover): count it, and disable a helper
        /// involved in CrashesToDisable crashes. Returns the helpers disabled now, with the reason.
        /// </summary>
        public List<(string Name, string Reason)> RecordCrash(IEnumerable<(string Name, string Source)> helpers, string what)
        {
            var disabled = new List<(string, string)>();
            var list = (helpers ?? Enumerable.Empty<(string, string)>()).ToList();
            if (list.Count == 0) return disabled;
            UpdateHelpers(get =>
            {
                foreach (var (name, source) in list)
                {
                    var r = get(name, source);
                    if (r == null) continue;
                    r.Crashes++;
                    if (r.Crashes >= CrashesToDisable && !r.Disabled)
                    {
                        r.Disabled = true;
                        r.DisabledReason = $"SolidWorks closed (crashed) {r.Crashes} times while a cell that called it ran (last: {what}). " +
                                           "Fix it and save_helper again (that enables it), or delete_helper.";
                        disabled.Add((name, r.DisabledReason));
                    }
                }
            });
            if (disabled.Count > 0) RaiseChanged(helpers: true);
            return disabled;
        }

        // ---------------------------------------------------------------- files

        private sealed class NotesFileJson
        {
            public List<ApiNote> Notes = new List<ApiNote>();
        }

        private sealed class HelpersFileJson
        {
            public List<HelperRecord> Helpers = new List<HelperRecord>();
        }

        private static T Read<T>(string path) where T : new()
        {
            try
            {
                if (!File.Exists(path)) return new T();
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), Json) ?? new T();
            }
            catch (Exception ex)
            {
                // A damaged file must not stop the agent: keep a copy for the user and start over.
                Log.Warn($"Reading {path} failed ({ex.Message}); keeping it as .bad and starting a new one.");
                try
                {
                    File.Copy(path, path + ".bad", true);
                }
                catch
                {
                    // best effort
                }
                return new T();
            }
        }

        private static void Write(string path, object value) => WriteText(path, JsonConvert.SerializeObject(value, Json));

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                AtomicFile.Replace(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        internal static string Clean(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max).TrimEnd() + "...";
        }
    }

    /// <summary>CrashGuard.Recover's hook: a crash record that names helpers counts against them.</summary>
    internal static class HelperCrashes
    {
        /// <summary>
        /// note["helpers"]: "Name" (user) or "plugin:x/Name". Counts the crash; helpers disabled now go to
        /// note["helpersDisabled"] ([{ name, reason }]) so the crash card tells the user and the agent.
        /// </summary>
        public static void Record(JObject note, KnowledgeStore store = null)
        {
            try
            {
                var keys = (note?["helpers"] as JArray)?.Select(k => (string)k).Where(k => !string.IsNullOrEmpty(k)).ToList();
                if (keys == null || keys.Count == 0) return;
                var helpers = keys.Select(k =>
                {
                    var slash = k.LastIndexOf('/');
                    return slash > 0 && k.StartsWith("plugin:", StringComparison.Ordinal) ? (k.Substring(slash + 1), k.Substring(0, slash)) : (k, "user");
                }).ToList();
                var what = $"{(string)note["title"] ?? (string)note["kind"]} at {(string)note["at"]}";
                var disabled = (store ?? KnowledgeStore.Instance).RecordCrash(helpers, what);
                if (disabled.Count > 0)
                    note["helpersDisabled"] = new JArray(disabled.Select(d => new JObject { ["name"] = d.Name, ["reason"] = d.Reason }));
            }
            catch (Exception ex)
            {
                Log.Warn("Recording the helpers of a crash failed: " + ex.Message);
            }
        }
    }
}

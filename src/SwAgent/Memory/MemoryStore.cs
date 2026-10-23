using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Tools;

namespace SwAgent.Memory
{
    /// <summary>One standing instruction: a short dated bullet, in the user's intent.</summary>
    internal sealed class MemoryNote
    {
        /// <summary>From the scope and the text (g/t/s + 6 hex): stable while the text is unchanged.</summary>
        public string Id;

        /// <summary>yyyy-MM-dd, or "" for a bullet written without a date.</summary>
        public string Date;

        public string Text;

        public JObject ToJson() => new JObject { ["id"] = Id, ["date"] = Date, ["text"] = Text };

        public string Line => $"- {(string.IsNullOrEmpty(Date) ? "" : Date + ": ")}{Text}";
    }

    /// <summary>
    /// Where standing instructions about one thing live (docs/agent-memory.md section 1): the agent's general memory, a tool,
    /// or a plugin skill. The user's own tools and local plugins' skills keep them in a "Standing instructions" section of
    /// their TOOL.md / SKILL.md; built-in and installed plugin tools and skills (read-only, replaced by installs) get an
    /// overlay file under agent-knowledge.
    /// </summary>
    internal sealed class MemoryScope
    {
        /// <summary>"general", "tool" or "skill".</summary>
        public string Kind;

        /// <summary>The tool ("name" or "plugin:tool") or the skill ("plugin:skill"); null for general.</summary>
        public string Name;

        public string File;

        /// <summary>The notes are a section of the tool's or skill's own instructions (else the whole file is notes).</summary>
        public bool InSection;

        /// <summary>For the user: "your general memory", "the Flash grooves tool's instructions", ...</summary>
        public string Label;

        public int Cap;

        public string Key => Kind == "general" ? "general" : Kind + ":" + Name;
    }

    /// <summary>
    /// The agent's memory of what the user told it (docs/agent-memory.md): general notes in agent-knowledge\memory.md,
    /// per-tool notes in agent-knowledge\tool-notes\&lt;tool&gt;.md (read-only tools) or the tool's TOOL.md, per-skill notes in
    /// agent-knowledge\skill-notes\&lt;plugin&gt;.&lt;skill&gt;.md (installed plugins) or the skill's SKILL.md. Under Paths, so
    /// SWAGENT_DATA_DIR redirects it for tests. Files only: safe on any thread.
    /// </summary>
    internal sealed class MemoryStore
    {
        /// <summary>General memory goes into every system prompt: about 1,500 tokens.</summary>
        public const int GeneralCap = 6000;

        /// <summary>A tool's or skill's notes ride along with its instructions.</summary>
        public const int ScopedCap = 3000;

        public const int NoteMax = 500;

        public const string SectionHeading = "## Standing instructions";
        private const string SectionIntro = "From the user. They win over the rest of these instructions.";
        private static readonly Regex SectionStart = new Regex(@"^##\s+Standing instructions\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex Bullet = new Regex(@"^\s*[-*]\s+(?:(\d{4}-\d{2}-\d{2}):\s*)?(.+?)\s*$");

        private static MemoryStore _instance;

        /// <summary>The add-in's store (under Paths). Tests may swap it.</summary>
        public static MemoryStore Instance
        {
            get => _instance ?? (_instance = new MemoryStore());
            internal set => _instance = value;
        }

        private readonly object _lock = new object();

        public MemoryStore(string root = null)
        {
            Root = root ?? Path.Combine(Paths.RoamingDir, "agent-knowledge");
        }

        public string Root { get; }
        public string GeneralFile => Path.Combine(Root, "memory.md");
        public string ToolNotesDir => Path.Combine(Root, "tool-notes");
        public string SkillNotesDir => Path.Combine(Root, "skill-notes");
        private string UndoFile => Path.Combine(Root, "memory-undo.json");

        /// <summary>
        /// A skill by "plugin:skill": its folder and whether its plugin is the user's own (local, editable). Null when there
        /// is no such enabled skill. Tests replace it.
        /// </summary>
        internal Func<string, (string Dir, bool Local)?> SkillLookup = DefaultSkillLookup;

        /// <summary>Notes were saved, changed or removed. Raised on the calling thread.</summary>
        public event Action Changed;

        private void RaiseChanged(MemoryScope scope)
        {
            try
            {
                Changed?.Invoke();
                if (scope?.Kind == "tool") UserTools.NotifyChanged(); // the tool list marks tools with notes
            }
            catch (Exception ex)
            {
                Log.Error("A memory.changed handler failed", ex);
            }
        }

        // ---------------------------------------------------------------- scopes

        /// <summary>
        /// The scope the agent named: "general", "tool:&lt;name&gt;" (a plugin's tool: "tool:&lt;plugin&gt;:&lt;tool&gt;") or
        /// "skill:&lt;plugin&gt;:&lt;skill&gt;". Throws ToolException for a tool or skill that doesn't exist.
        /// </summary>
        public MemoryScope Resolve(string scope)
        {
            var s = (scope ?? "").Trim();
            var lower = s.ToLowerInvariant();
            if (lower == "" || lower == "general" || lower == "memory" || lower == "global") return General();
            if (lower.StartsWith("tool:"))
            {
                var name = lower.Substring(5).Trim();
                var dir = UserTools.DirOf(name) ?? throw new ToolException($"No tool named '{name}'. list_user_tools shows them; plugin tools are \"tool:<plugin>:<tool>\".");
                return ToolScope(name, dir);
            }
            if (lower.StartsWith("skill:"))
            {
                var id = lower.Substring(6).Trim();
                if (id.IndexOf(':') <= 0) throw new ToolException("A skill scope is \"skill:<plugin>:<skill>\", as in the skills index.");
                var found = SkillLookup(id) ?? throw new ToolException($"No enabled skill '{id}'. The skills index lists them.");
                return found.Local
                    ? new MemoryScope { Kind = "skill", Name = id, File = Path.Combine(found.Dir, "SKILL.md"), InSection = true, Label = $"the {id} skill's instructions", Cap = ScopedCap }
                    : SkillOverlay(id);
            }
            throw new ToolException("scope must be \"general\", \"tool:<name>\" or \"skill:<plugin>:<skill>\".");
        }

        public MemoryScope General() =>
            new MemoryScope { Kind = "general", File = GeneralFile, Label = "your general memory", Cap = GeneralCap };

        /// <summary>A tool's notes: its own TOOL.md when it is the user's, else an overlay (the shipped folder stays untouched).</summary>
        public MemoryScope ToolScope(string name, string dir)
        {
            var title = TitleOf(dir) ?? name;
            if (dir != null && !UserTools.IsReadOnlyDir(dir))
                return new MemoryScope { Kind = "tool", Name = name, File = Path.Combine(dir, "TOOL.md"), InSection = true, Label = $"the {title} tool's instructions", Cap = ScopedCap };
            return new MemoryScope { Kind = "tool", Name = name, File = ToolOverlayPath(name), Label = $"your notes for the {title} tool", Cap = ScopedCap };
        }

        public string ToolOverlayPath(string tool) => Path.Combine(ToolNotesDir, UserTools.StorageKey(tool) + ".md");

        private MemoryScope SkillOverlay(string id) =>
            new MemoryScope { Kind = "skill", Name = id, File = Path.Combine(SkillNotesDir, id.Replace(':', '.') + ".md"), Label = $"your notes for the {id} skill", Cap = ScopedCap };

        private static string TitleOf(string dir)
        {
            try
            {
                var md = dir == null ? null : Path.Combine(dir, "TOOL.md");
                return md != null && System.IO.File.Exists(md) ? UserTools.FrontValue(UserTools.FrontMatter(System.IO.File.ReadAllText(md)), "title") : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static (string Dir, bool Local)? DefaultSkillLookup(string id)
        {
            try
            {
                var pm = Plugins.PluginManager.Instance;
                var skill = Plugins.SkillIndex.All(pm).FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
                if (skill == null) return null;
                return (skill.Dir, pm.Find(skill.Plugin)?.Local == true);
            }
            catch (Exception ex)
            {
                Log.Warn("Skill lookup for memory failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Every scope that holds notes: general, the overlays, the user's tools with a Standing instructions section, and
        /// local plugins' tools and skills with one.
        /// </summary>
        public List<MemoryScope> Scopes()
        {
            var list = new List<MemoryScope> { General() };
            if (Directory.Exists(ToolNotesDir))
                foreach (var f in Directory.GetFiles(ToolNotesDir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileNameWithoutExtension(f).Replace('+', ':');
                    var dir = UserTools.DirOf(name);
                    list.Add(new MemoryScope { Kind = "tool", Name = name, File = f, Label = $"your notes for the {TitleOf(dir) ?? name} tool", Cap = ScopedCap });
                }
            foreach (var dir in Directory.GetDirectories(UserTools.Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var md = Path.Combine(dir, "TOOL.md");
                if (HasSection(md)) list.Add(ToolScope(Path.GetFileName(dir), dir));
            }
            try
            {
                foreach (var p in Plugins.PluginManager.Instance.Installed().Where(p => p.Local && p.Manifest != null))
                {
                    foreach (var (tool, dir) in p.Manifest.Tools())
                        if (HasSection(Path.Combine(dir, "TOOL.md"))) list.Add(ToolScope(p.Name + ":" + tool, dir));
                    foreach (var s in Plugins.SkillIndex.For(p.Manifest))
                        if (HasSection(Path.Combine(s.Dir, "SKILL.md")))
                            list.Add(new MemoryScope { Kind = "skill", Name = s.Id, File = Path.Combine(s.Dir, "SKILL.md"), InSection = true, Label = $"the {s.Id} skill's instructions", Cap = ScopedCap });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Local plugins' notes unavailable: " + ex.Message);
            }
            if (Directory.Exists(SkillNotesDir))
                foreach (var f in Directory.GetFiles(SkillNotesDir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var id = Path.GetFileNameWithoutExtension(f);
                    var dot = id.IndexOf('.');
                    if (dot > 0) id = id.Substring(0, dot) + ":" + id.Substring(dot + 1);
                    list.Add(SkillOverlay(id));
                }
            return list;
        }

        private static bool HasSection(string md)
        {
            try
            {
                return System.IO.File.Exists(md) && System.IO.File.ReadAllText(md).Replace("\r\n", "\n").Split('\n').Any(l => SectionStart.IsMatch(l));
            }
            catch (IOException)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- notes

        public List<MemoryNote> Notes(MemoryScope scope)
        {
            lock (_lock) return Read(scope);
        }

        /// <summary>The notes of a tool (overlay or TOOL.md section), for markers and staging; empty when it has none.</summary>
        public List<MemoryNote> ToolNotes(string tool)
        {
            var name = (tool ?? "").Trim().ToLowerInvariant();
            var dir = UserTools.DirOf(name);
            return dir == null && !System.IO.File.Exists(ToolOverlayPath(name)) ? new List<MemoryNote>() : Notes(ToolScope(name, dir));
        }

        /// <summary>
        /// A tool's notes as a run stages them (ToolRunner payload "notes"): the overlay of a read-only tool, or the Standing
        /// instructions section of the user's own (a fork carries the original's): null when it has none.
        /// </summary>
        public string RunNotes(string tool)
        {
            var name = (tool ?? "").Trim().ToLowerInvariant();
            var dir = UserTools.DirOf(name);
            if (dir == null || UserTools.IsReadOnlyDir(dir)) return OverlayText(name, RunHeading);
            return HasSection(Path.Combine(dir, "TOOL.md")) ? Appendix(Notes(ToolScope(name, dir)), RunHeading) : null;
        }

        /// <summary>
        /// A read-only tool's notes as they are appended to its instructions (read_user_tool, tool-run staging); null when it
        /// has none. The user's own tools carry theirs in TOOL.md already.
        /// </summary>
        public string OverlayText(string tool) => OverlayText(tool, AppendixHeading);

        private string OverlayText(string tool, string heading)
        {
            var name = (tool ?? "").Trim().ToLowerInvariant();
            if (!System.IO.File.Exists(ToolOverlayPath(name))) return null;
            var dir = UserTools.DirOf(name);
            if (dir != null && !UserTools.IsReadOnlyDir(dir)) return null;
            return Appendix(Notes(ToolScope(name, dir)), heading);
        }

        /// <summary>A skill's overlay notes as use_skill appends them; null when it has none (local skills keep theirs in SKILL.md).</summary>
        public string SkillOverlayText(string skillId)
        {
            var scope = SkillOverlay((skillId ?? "").Trim().ToLowerInvariant());
            return System.IO.File.Exists(scope.File) ? Appendix(Notes(scope)) : null;
        }

        /// <summary>After a tool's or skill's text (read_user_tool, use_skill).</summary>
        private const string AppendixHeading = "Standing instructions from the user (they win over the text above):";

        /// <summary>First in a tool run's message, before anything of the tool's.</summary>
        private const string RunHeading = "Standing instructions from the user for this tool (they win over its TOOL.md):";

        private static string Appendix(List<MemoryNote> notes, string heading = AppendixHeading) =>
            notes.Count == 0 ? null : heading + "\n" + string.Join("\n", notes.Select(n => n.Line));

        /// <summary>The result of remember: the note, what it replaced, and a warning when the scope is nearly full.</summary>
        internal sealed class Saved
        {
            public MemoryScope Scope;
            public MemoryNote Note;
            public bool Existed;
            public List<(MemoryScope Scope, MemoryNote Note)> Replaced = new List<(MemoryScope, MemoryNote)>();
            public string Warning;
        }

        /// <summary>
        /// Save a note in a scope. `replaces` are ids of older notes (any scope) it supersedes: they are removed, and Undo puts
        /// them back. The same text again is not saved twice. Throws ToolException when the text is empty or too long, an id is
        /// unknown, or the scope would go over its cap (nothing is changed then).
        /// </summary>
        public Saved Remember(MemoryScope scope, string text, IEnumerable<string> replaces = null)
        {
            text = Clean(text);
            if (text.Length == 0) throw new ToolException("The note is empty.");
            if (text.Length > NoteMax) throw new ToolException($"Keep a note under {NoteMax} characters: the rule itself, not the story behind it.");
            var result = new Saved { Scope = scope };
            lock (_lock)
            {
                var notes = Read(scope);
                var same = notes.FirstOrDefault(n => string.Equals(n.Text, text, StringComparison.OrdinalIgnoreCase));
                if (same != null)
                {
                    result.Note = same;
                    result.Existed = true;
                    return result;
                }
                // the notes it replaces: found first, removed only when everything checks out
                var ids = (replaces ?? Enumerable.Empty<string>()).Select(i => (i ?? "").Trim()).Where(i => i.Length > 0).Distinct().ToList();
                var targets = new List<(MemoryScope Scope, MemoryNote Note)>();
                foreach (var id in ids)
                {
                    var hit = notes.FirstOrDefault(n => n.Id == id) is MemoryNote local ? (scope, local) : Find(id);
                    if (hit.Item2 == null) throw new ToolException($"No note with id {id} (nothing was saved). list_memory shows the ids.");
                    targets.Add(hit);
                }
                // where each replaced note was, so Undo puts it back in its place (general memory is in the prompt in order)
                var positions = new Dictionary<string, int>();
                foreach (var target in targets.Where(t => t.Scope.Key == scope.Key)) positions[target.Note.Id] = notes.FindIndex(n => n.Id == target.Note.Id);
                notes.RemoveAll(n => targets.Any(t => t.Scope.Key == scope.Key && t.Note.Id == n.Id));
                var note = new MemoryNote { Date = DateTime.Now.ToString("yyyy-MM-dd"), Text = text, Id = IdFor(scope, text) };
                var used = Used(notes) + note.Line.Length + 1;
                if (used > scope.Cap)
                    throw new ToolException($"{Capitalise(scope.Label)} is full ({Used(notes)} of {scope.Cap} characters; nothing was saved). Merge overlapping notes " +
                                            "into one with remember(replaces: [ids]), or ask the user which to drop.");
                notes.Add(note);
                Write(scope, notes);
                foreach (var other in targets.Where(t => t.Scope.Key != scope.Key).GroupBy(t => t.Scope.Key))
                {
                    var s = other.First().Scope;
                    var left = Read(s);
                    foreach (var target in other) positions[target.Note.Id] = left.FindIndex(n => n.Id == target.Note.Id);
                    left.RemoveAll(n => other.Any(t => t.Note.Id == n.Id));
                    Write(s, left);
                }
                result.Note = note;
                result.Replaced = targets;
                if (used > scope.Cap * 8 / 10)
                    result.Warning = $"{Capitalise(scope.Label)} is {used * 100 / scope.Cap}% full: merge overlapping notes soon (remember with replaces), and ask before dropping any.";
                Journal(note.Id, scope, targets, positions);
            }
            RaiseChanged(scope);
            foreach (var s in result.Replaced.Select(r => r.Scope).Where(s => s.Key != scope.Key && s.Kind == "tool")) RaiseChanged(s);
            return result;
        }

        /// <summary>Remove a note by id from whichever scope holds it; null when there is none.</summary>
        public (MemoryScope Scope, MemoryNote Note)? Forget(string id)
        {
            (MemoryScope Scope, MemoryNote Note) hit;
            lock (_lock)
            {
                hit = Find((id ?? "").Trim());
                if (hit.Note == null) return null;
                var notes = Read(hit.Scope);
                notes.RemoveAll(n => n.Id == hit.Note.Id);
                Write(hit.Scope, notes);
            }
            RaiseChanged(hit.Scope);
            return hit;
        }

        /// <summary>
        /// Undo a remember (the chat's chip): the note goes and the notes it replaced come back with their dates. False when the
        /// note is already gone and there was nothing to restore.
        /// </summary>
        public bool Undo(string id)
        {
            var any = false;
            var touched = new List<MemoryScope>();
            lock (_lock)
            {
                var hit = Find((id ?? "").Trim());
                if (hit.Note != null)
                {
                    var notes = Read(hit.Scope);
                    notes.RemoveAll(n => n.Id == hit.Note.Id);
                    Write(hit.Scope, notes);
                    touched.Add(hit.Scope);
                    any = true;
                }
                var journal = ReadJournal();
                var entry = journal.OfType<JObject>().LastOrDefault(e => (string)e["id"] == id);
                if (entry != null)
                {
                    // lowest position first, so each goes back where it was among the others
                    foreach (var r in (entry["replaced"] as JArray ?? new JArray()).OfType<JObject>().OrderBy(e => (int?)e["index"] ?? int.MaxValue))
                    {
                        MemoryScope scope;
                        try
                        {
                            scope = ScopeForKey((string)r["scope"], (string)r["file"]);
                        }
                        catch (ToolException)
                        {
                            continue; // the tool or skill is gone
                        }
                        var notes = Read(scope);
                        var text = (string)r["text"];
                        if (notes.Any(n => string.Equals(n.Text, text, StringComparison.OrdinalIgnoreCase))) continue;
                        var at = (int?)r["index"] ?? notes.Count;
                        notes.Insert(Math.Max(0, Math.Min(at, notes.Count)), new MemoryNote { Date = (string)r["date"] ?? "", Text = text, Id = IdFor(scope, text) });
                        Write(scope, notes);
                        touched.Add(scope);
                        any = true;
                    }
                    entry.Remove();
                    WriteText(UndoFile, journal.ToString());
                }
            }
            foreach (var s in touched.GroupBy(s => s.Key).Select(g => g.First())) RaiseChanged(s);
            return any;
        }

        /// <summary>Change a note's text (Options). Returns the note with its new id; null when there is no such note.</summary>
        public MemoryNote Edit(string id, string text)
        {
            text = Clean(text);
            if (text.Length == 0) throw new ToolException("The note is empty: delete it instead.");
            if (text.Length > NoteMax) throw new ToolException($"Keep a note under {NoteMax} characters.");
            MemoryNote note;
            MemoryScope scope;
            lock (_lock)
            {
                var hit = Find((id ?? "").Trim());
                if (hit.Note == null) return null;
                scope = hit.Scope;
                var notes = Read(scope);
                note = notes.First(n => n.Id == hit.Note.Id);
                if (Used(notes) - note.Text.Length + text.Length > scope.Cap) throw new ToolException($"{Capitalise(scope.Label)} would be over its limit of {scope.Cap} characters.");
                note.Text = text;
                note.Id = IdFor(scope, text);
                Write(scope, notes);
            }
            RaiseChanged(scope);
            return note;
        }

        /// <summary>A note by id, in any scope; (null, null) when none has it.</summary>
        public (MemoryScope Scope, MemoryNote Note) Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return (null, null);
            foreach (var scope in Scopes())
            {
                var note = Read(scope).FirstOrDefault(n => n.Id == id);
                if (note != null) return (scope, note);
            }
            return (null, null);
        }

        /// <summary>
        /// A tool's overlay notes are copied into its copy's TOOL.md (fork_tool): the copy is the user's, so they belong in it,
        /// and the original keeps them for when it is used again. Returns how many were copied.
        /// </summary>
        public int CopyToolNotes(string source, string forkName, string forkDir)
        {
            var from = Path.Combine(ToolNotesDir, UserTools.StorageKey(source) + ".md");
            List<MemoryNote> copying;
            lock (_lock)
            {
                if (!System.IO.File.Exists(from)) return 0;
                copying = Read(new MemoryScope { Kind = "tool", Name = source, File = from, Cap = ScopedCap });
                AddNotes(ToolScope(forkName, forkDir), copying);
            }
            return copying.Count;
        }

        /// <summary>Add notes with their dates (no cap: they were within one before), skipping texts the scope already has.</summary>
        public void AddNotes(MemoryScope scope, IEnumerable<MemoryNote> add)
        {
            lock (_lock)
            {
                var notes = Read(scope);
                foreach (var n in add.Where(m => notes.All(x => !string.Equals(x.Text, m.Text, StringComparison.OrdinalIgnoreCase))))
                    notes.Add(new MemoryNote { Date = n.Date, Text = n.Text, Id = IdFor(scope, n.Text) });
                Write(scope, notes);
            }
            RaiseChanged(scope);
        }

        /// <summary>A tool has standing instructions: an overlay (read-only tools) or a section in its own TOOL.md.</summary>
        public bool HasNotes(string name, string dir, bool readOnly) =>
            readOnly || dir == null ? System.IO.File.Exists(ToolOverlayPath(name)) : HasSection(Path.Combine(dir, "TOOL.md"));

        // ---------------------------------------------------------------- the panel

        /// <summary>
        /// memory.index, for the system prompt: the general notes in full, the skills with notes, how full general memory is.
        /// Tools with notes are marked in the tool list (hasNotes) instead.
        /// </summary>
        public JObject Index(bool saving = true)
        {
            var general = Notes(General());
            var skills = new JArray();
            foreach (var s in Scopes().Where(s => s.Kind == "skill"))
            {
                var n = Notes(s).Count;
                if (n > 0) skills.Add(new JObject { ["id"] = s.Name, ["count"] = n });
            }
            return new JObject
            {
                ["general"] = new JArray(general.Select(n => n.ToJson())),
                ["skills"] = skills,
                ["used"] = Used(general),
                ["cap"] = GeneralCap,
                ["saving"] = saving,
            };
        }

        /// <summary>memory.list, for Options: every scope with notes, general first.</summary>
        public JArray List()
        {
            var list = new JArray();
            foreach (var s in Scopes())
            {
                var notes = Notes(s);
                if (notes.Count == 0 && s.Kind != "general") continue;
                list.Add(new JObject
                {
                    ["scope"] = s.Key,
                    ["kind"] = s.Kind,
                    ["name"] = s.Name,
                    ["label"] = s.Label,
                    ["used"] = Used(notes),
                    ["cap"] = s.Cap,
                    ["notes"] = new JArray(notes.Select(n => n.ToJson())),
                });
            }
            return list;
        }

        // ---------------------------------------------------------------- files

        private MemoryScope ScopeForKey(string key, string file)
        {
            if (key == "general") return General();
            var scope = Resolve(key);
            // A note restored to the overlay it came from, even if the tool is the user's now (or the other way round).
            if (!string.IsNullOrEmpty(file) && !string.Equals(Path.GetFullPath(file), Path.GetFullPath(scope.File), StringComparison.OrdinalIgnoreCase) &&
                (file.StartsWith(Root, StringComparison.OrdinalIgnoreCase) || System.IO.File.Exists(file)))
                scope = new MemoryScope { Kind = scope.Kind, Name = scope.Name, File = file, InSection = !file.StartsWith(Root, StringComparison.OrdinalIgnoreCase), Label = scope.Label, Cap = scope.Cap };
            return scope;
        }

        private void Journal(string id, MemoryScope scope, List<(MemoryScope Scope, MemoryNote Note)> replaced, Dictionary<string, int> positions)
        {
            var journal = ReadJournal();
            journal.Add(new JObject
            {
                ["id"] = id,
                ["scope"] = scope.Key,
                ["replaced"] = new JArray(replaced.Select(r => new JObject
                {
                    ["scope"] = r.Scope.Key, ["file"] = r.Scope.File, ["date"] = r.Note.Date, ["text"] = r.Note.Text,
                    ["index"] = positions.TryGetValue(r.Note.Id, out var i) && i >= 0 ? (JToken)i : null,
                })),
            });
            while (journal.Count > 100) journal.RemoveAt(0);
            WriteText(UndoFile, journal.ToString());
        }

        private JArray ReadJournal()
        {
            try
            {
                return System.IO.File.Exists(UndoFile) ? JArray.Parse(System.IO.File.ReadAllText(UndoFile)) : new JArray();
            }
            catch (Exception ex)
            {
                // The next remember replaces it: keep a copy, as the knowledge store does.
                Log.Warn("memory-undo.json unreadable, keeping it as .bad and starting a new one: " + ex.Message);
                try
                {
                    System.IO.File.Copy(UndoFile, UndoFile + ".bad", true);
                }
                catch
                {
                    // best effort
                }
                return new JArray();
            }
        }

        private static List<MemoryNote> Read(MemoryScope scope)
        {
            var list = new List<MemoryNote>();
            if (!System.IO.File.Exists(scope.File)) return list;
            var lines = System.IO.File.ReadAllText(scope.File).Replace("\r\n", "\n").Split('\n');
            var (start, end) = scope.InSection ? SectionRange(lines) : (0, lines.Length);
            if (start < 0) return list;
            for (var i = start; i < end; i++)
            {
                var m = Bullet.Match(lines[i]);
                if (!m.Success) continue;
                var text = Clean(m.Groups[2].Value);
                if (text.Length == 0 || list.Any(n => n.Text == text)) continue;
                list.Add(new MemoryNote { Date = m.Groups[1].Value, Text = text, Id = IdFor(scope, text) });
            }
            return list;
        }

        /// <summary>The lines of the Standing instructions section (after its heading, up to the next # or ## heading); (-1, -1) when absent.</summary>
        private static (int Start, int End) SectionRange(string[] lines)
        {
            var at = Array.FindIndex(lines, l => SectionStart.IsMatch(l));
            if (at < 0) return (-1, -1);
            var end = Array.FindIndex(lines, at + 1, l => l.StartsWith("# ") || l.StartsWith("## "));
            return (at + 1, end < 0 ? lines.Length : end);
        }

        private static void Write(MemoryScope scope, List<MemoryNote> notes)
        {
            var bullets = notes.Select(n => n.Line).ToList();
            if (!scope.InSection)
            {
                if (bullets.Count == 0)
                {
                    if (System.IO.File.Exists(scope.File)) System.IO.File.Delete(scope.File);
                    return;
                }
                var head = scope.Kind == "general" ? "# What the user told the agent" : $"# Standing instructions for {scope.Key}";
                WriteText(scope.File, head + "\n\n" + string.Join("\n", bullets) + "\n");
                return;
            }
            if (!System.IO.File.Exists(scope.File)) throw new ToolException($"{scope.File} is missing.");
            var lines = System.IO.File.ReadAllText(scope.File).Replace("\r\n", "\n").Split('\n').ToList();
            var (start, end) = SectionRange(lines.ToArray());
            var section = bullets.Count == 0 ? new List<string>() : new[] { SectionHeading, "", SectionIntro, "" }.Concat(bullets).Concat(new[] { "" }).ToList();
            if (start >= 0)
            {
                lines.RemoveRange(start - 1, end - start + 1);
                lines.InsertRange(start - 1, section);
            }
            else if (section.Count > 0)
            {
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                lines.Add("");
                lines.AddRange(section);
            }
            var text = string.Join("\n", lines).TrimEnd() + "\n";
            WriteText(scope.File, text);
        }

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                System.IO.File.WriteAllText(tmp, text, new UTF8Encoding(false));
                AtomicFile.Replace(tmp, path);
            }
            finally
            {
                if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
            }
        }

        private static int Used(IEnumerable<MemoryNote> notes) => notes.Sum(n => n.Line.Length + 1);

        /// <summary>One line, no bullet or surrounding quotes.</summary>
        internal static string Clean(string s)
        {
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            s = Regex.Replace(s, @"^[-*]\s+", "");
            return s.Trim();
        }

        internal static string IdFor(MemoryScope scope, string text)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(scope.Key + "\n" + text.ToLowerInvariant()));
                return scope.Kind.Substring(0, 1) + string.Concat(hash.Take(3).Select(b => b.ToString("x2")));
            }
        }

        private static string Capitalise(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}

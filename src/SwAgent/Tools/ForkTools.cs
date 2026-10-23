using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Memory;

namespace SwAgent.Tools
{
    /// <summary>
    /// The user's own copy of a read-only tool (docs/agent-memory.md section 2a): fork_tool copies a built-in or plugin tool
    /// into %APPDATA%\SwAgent\tools\&lt;name&gt; under a slightly different name, and compare_fork shows what the original
    /// changed since and takes those changes on request. Files only (SwBusyGate.ModelFreeTools).
    /// </summary>
    public sealed class ForkTools
    {
        public sealed class ForkInput
        {
            [ToolParam("The built-in or plugin tool to copy (\"plugin:tool\" for a plugin's).", Required = true)]
            public string Source { get; set; }

            [ToolParam("The copy's id, confirmed with the user. Default \"<source>-custom\" (then -2, -3 ...).")]
            public string Name { get; set; }

            [ToolParam("The copy's title, confirmed with the user. Default \"<Title> (my version)\".")]
            public string Title { get; set; }

            [ToolParam("true keeps the original's button off the ribbon and palette (undo in Options or the palette). Ask once. With an existing " +
                       "copy's name, only sets this.")]
            public bool? HideOriginal { get; set; }
        }

        [AgentTool("fork_tool",
            "Make the user's own copy of a built-in or plugin (read-only) tool to change it: copies its whole folder, your notes on it and the " +
            "user's saved profiles under a new name and records the original, so its later updates can be offered. Confirm the name with the user first; then " +
            "change the copy with edit_user_tool.")]
        public ToolResult Fork(ToolContext ctx, ForkInput input)
        {
            var source = (input.Source ?? "").Trim().ToLowerInvariant();
            var sourceDir = UserTools.DirOf(source) ?? throw new ToolException($"No tool named '{source}'. list_user_tools shows them.");
            if (!UserTools.IsReadOnlyDir(sourceDir))
                throw new ToolException($"'{source}' is already the user's own: change it with edit_user_tool (or save a copy with save_user_tool).");
            var sourceFm = UserTools.EffectiveFrontMatter(source, sourceDir);
            var sourceTitle = UserTools.FrontValue(sourceFm, "title") ?? source;

            var name = (input.Name ?? "").Trim().ToLowerInvariant();
            if (name.Length > 0 && Directory.Exists(Path.Combine(UserTools.Root, name)) && ToolForks.SourceOfDir(Path.Combine(UserTools.Root, name)) == source)
            {
                // The copy exists: this call only hides or shows the original.
                if (input.HideOriginal == null) throw new ToolException($"'{name}' already is the user's copy of {sourceTitle}. Change it with edit_user_tool.");
                UserTools.SetValue(source, "hidden", input.HideOriginal == true ? "true" : null);
                return ToolResult.Ok(input.HideOriginal == true
                    ? $"The original {sourceTitle} is now hidden from the ribbon and palette (after a restart for the ribbon: offer_restart). The user can show it again in Options or the palette."
                    : $"The original {sourceTitle} shows again (on the ribbon after a restart: offer_restart).");
            }
            if (name.Length == 0) name = ToolForks.DefaultName(source);
            else
            {
                var problem = ToolForks.NameProblem(name);
                if (problem != null) throw new ToolException(problem);
            }
            var title = string.IsNullOrWhiteSpace(input.Title) ? sourceTitle + " (my version)" : Regex.Replace(input.Title, @"\s+", " ").Trim();

            var dir = Path.Combine(UserTools.Root, name);
            var version = ToolForks.VersionOf(source, sourceDir, sourceFm);
            try
            {
                ToolForks.CopyFolder(sourceDir, dir);
                var md = Path.Combine(dir, "TOOL.md");
                var settings = UserTools.BuiltInSettings(source);
                ToolForks.WriteText(md, ToolForks.Rewrite(File.ReadAllText(md), source, sourceTitle, name, title, $"{source}@{version}", settings));
            }
            catch
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Removing the half-made copy {dir} failed: {ex.Message}");
                }
                throw;
            }
            var notes = MemoryStore.Instance.CopyToolNotes(source, name, dir);
            ToolProfiles.Retarget(dir, name); // the profiles that came with the original now name the copy
            ToolProfiles.CopyAll(source, name, fresh: true); // the user's saved profiles and their default go with the copy (not a deleted tool's)
            ToolForks.WriteRecord(dir, source, version, ToolForks.Hashes(sourceDir), ToolForks.Hashes(dir));
            if (input.HideOriginal == true) UserTools.SetValue(source, "hidden", "true");
            Log.Info($"Forked tool '{source}' as '{name}'");
            UserTools.NotifyChanged();

            var kind = (UserTools.FrontValue(sourceFm, "kind") ?? "workflow") == "tool" ? "tool" : "workflow";
            var sb = new StringBuilder($"Made '{name}' ({title}): the user's own copy of {sourceTitle} ({source}@{version}), in {dir}.");
            if (notes > 0) sb.Append($" Your {notes} note{(notes == 1 ? "" : "s")} on the original {(notes == 1 ? "is" : "are")} copied into its Standing instructions.");
            var profiles = ToolProfiles.List(name).Where(p => !p.Locked).Select(p => p.Name).ToList();
            if (profiles.Count > 0) sb.Append($" The user's profiles of the original are copied too: {string.Join(", ", profiles)}.");
            sb.Append(kind == "tool"
                ? " Its ribbon button appears after a restart: call offer_restart. Until then it runs from the Workflows menu."
                : " It is in the Workflows menu now.");
            if (input.HideOriginal == true) sb.Append(" The original is hidden from the ribbon and palette.");
            else if (input.HideOriginal == null) sb.Append(" If you haven't, ask once whether to hide the original (fork_tool with this name and hide_original).");
            sb.Append(" Now make the user's change in the copy with edit_user_tool, and say in one line what you did and where.");
            return ToolResult.Ok(sb.ToString());
        }

        public sealed class CompareInput
        {
            [ToolParam("The user's copy (a tool made with fork_tool).", Required = true)]
            public string Name { get; set; }

            [ToolParam("Files to take from the original as they are now, e.g. [\"data/materials.json\"] (paths as listed). TOOL.md is rebuilt from the " +
                       "original with the copy's name, title and standing instructions: prefer merging it with edit_user_tool.")]
            public string[] Take { get; set; }

            [ToolParam("true when the copy is up to date with the original (after merging): stops the \"changed since you copied it\" notice.")]
            public bool? Done { get; set; }
        }

        [AgentTool("compare_fork",
            "For the user's copy of a tool (fork_tool): what its original changed since the copy was made and what the user changed, with the " +
            "text differences. Offer the original's changes; never apply them unasked.")]
        public ToolResult Compare(ToolContext ctx, CompareInput input)
        {
            var name = (input.Name ?? "").Trim().ToLowerInvariant();
            var dir = UserTools.DirOf(name);
            if (dir == null || UserTools.IsReadOnlyDir(dir)) throw new ToolException($"'{name}' isn't one of the user's tools.");
            var record = ToolForks.ReadRecord(dir) ?? throw new ToolException($"'{name}' isn't a copy made with fork_tool.");
            var source = (string)record["source"];
            var sourceDir = UserTools.DirOf(source) ?? throw new ToolException($"The original '{source}' isn't installed any more: the copy is on its own.");
            var sourceFm = UserTools.FrontMatter(File.ReadAllText(Path.Combine(sourceDir, "TOOL.md")));
            var sourceTitle = UserTools.FrontValue(sourceFm, "title") ?? source;

            var baseHashes = ToolForks.HashMap(record["base"]);
            var mineHashes = ToolForks.HashMap(record["mine"]);
            var theirs = ToolForks.Hashes(sourceDir);
            var ours = ToolForks.Hashes(dir);
            var sb = new StringBuilder();

            var taken = new List<string>();
            foreach (var path in (input.Take ?? new string[0]).Select(p => (p ?? "").Replace('\\', '/').Trim()).Where(p => p.Length > 0))
            {
                var key = theirs.Keys.FirstOrDefault(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase))
                          ?? throw new ToolException($"The original has no file '{path}' (nothing was taken).");
                var to = UserTools.SafePath(dir, key);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                if (key == "TOOL.md")
                {
                    var fm = UserTools.FrontMatter(File.ReadAllText(Path.Combine(dir, "TOOL.md")));
                    var notes = MemoryStore.Instance.Notes(MemoryStore.Instance.ToolScope(name, dir));
                    var text = ToolForks.Rewrite(File.ReadAllText(Path.Combine(sourceDir, "TOOL.md")), source, sourceTitle, name,
                        UserTools.FrontValue(fm, "title") ?? name, $"{source}@{ToolForks.VersionOf(source, sourceDir, sourceFm)}", UserTools.BuiltInSettings(source));
                    ToolForks.WriteText(to, text);
                    MemoryStore.Instance.AddNotes(MemoryStore.Instance.ToolScope(name, dir), notes);
                }
                else ToolForks.CopyFile(UserTools.SafePath(sourceDir, key), to);
                taken.Add(key);
            }
            if (taken.Count > 0)
            {
                ours = ToolForks.Hashes(dir, fresh: true);
                foreach (var k in taken)
                {
                    baseHashes[k] = theirs[k];
                    mineHashes[k] = ours[k];
                }
                ToolForks.WriteRecord(dir, source, (string)record["version"], baseHashes, mineHashes);
                sb.AppendLine($"Took from the original: {string.Join(", ", taken)}.");
            }
            if (input.Done == true)
            {
                var version = ToolForks.VersionOf(source, sourceDir, sourceFm);
                UserTools.SetValue(name, "forked_from", $"{source}@{version}");
                ToolForks.WriteRecord(dir, source, version, theirs, ToolForks.Hashes(dir, fresh: true));
                UserTools.NotifyChanged();
                return ToolResult.Ok(sb.Append($"Marked '{name}' as up to date with {sourceTitle} ({version}).").ToString());
            }
            if (taken.Count > 0) UserTools.NotifyChanged();

            var all = theirs.Keys.Union(baseHashes.Keys).Union(ours.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
            var theirChanges = new List<string>();
            var both = new List<string>();
            var mineOnly = new List<string>();
            foreach (var k in all)
            {
                theirs.TryGetValue(k, out var t);
                baseHashes.TryGetValue(k, out var b);
                ours.TryGetValue(k, out var o);
                mineHashes.TryGetValue(k, out var m);
                var theyChanged = t != b;
                var iChanged = o != m;
                if (theyChanged && iChanged) both.Add(k);
                else if (theyChanged) theirChanges.Add(k + (t == null ? " (removed in the original)" : b == null ? " (new in the original)" : ""));
                else if (iChanged) mineOnly.Add(k);
            }
            sb.AppendLine($"'{name}' is a copy of {sourceTitle} ({source}), copied at {(string)record["version"]}; the original is now at {ToolForks.VersionOf(source, sourceDir, sourceFm)}.");
            if (theirChanges.Count == 0 && both.Count == 0) sb.AppendLine("The original hasn't changed since it was copied.");
            if (theirChanges.Count > 0) sb.AppendLine("Changed only in the original (take them as they are): " + string.Join(", ", theirChanges));
            if (both.Count > 0) sb.AppendLine("Changed in both (merge by hand): " + string.Join(", ", both));
            if (mineOnly.Count > 0) sb.AppendLine("Changed only in the copy: " + string.Join(", ", mineOnly));
            var budget = 12000;
            foreach (var k in theirChanges.Select(c => c.Split(' ')[0]).Concat(both))
            {
                if (budget <= 0)
                {
                    sb.AppendLine("(More differences: read the files with read_user_tool.)");
                    break;
                }
                var a = Path.Combine(dir, k.Replace('/', '\\'));
                var b = Path.Combine(sourceDir, k.Replace('/', '\\'));
                if (!File.Exists(a) || !File.Exists(b) || !ToolForks.IsText(b)) continue;
                var diff = ToolForks.Diff(File.ReadAllText(a), File.ReadAllText(b), Math.Min(4000, budget));
                if (diff.Length == 0) continue;
                sb.AppendLine().AppendLine($"--- {k}: copy (-) vs original (+)").AppendLine(diff);
                budget -= diff.Length;
            }
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }

    /// <summary>
    /// What fork_tool keeps in the copy: front matter <c>forked_from: &lt;source&gt;@&lt;version&gt;</c> and .fork.json (the source,
    /// the version, the file hashes of the original and of the copy when copied), so the original's later changes can be told
    /// from the user's own.
    /// </summary>
    internal static class ToolForks
    {
        public const string RecordFile = ".fork.json";

        private static readonly Dictionary<string, (string Sig, Dictionary<string, string> Hashes)> Cache =
            new Dictionary<string, (string, Dictionary<string, string>)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The source tool of a fork folder (front matter forked_from), or null.</summary>
        public static string SourceOfDir(string dir)
        {
            try
            {
                var md = dir == null ? null : Path.Combine(dir, "TOOL.md");
                if (md == null || !File.Exists(md)) return null;
                var v = UserTools.FrontValue(UserTools.FrontMatter(File.ReadAllText(md)), "forked_from");
                if (string.IsNullOrEmpty(v)) return null;
                var at = v.LastIndexOf('@');
                return (at > 0 ? v.Substring(0, at) : v).Trim().ToLowerInvariant();
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <summary>The source of one of the user's tools that is a copy, or null.</summary>
        public static string SourceOf(string tool)
        {
            var dir = UserTools.DirOf(tool);
            return dir == null || UserTools.IsReadOnlyDir(dir) ? null : SourceOfDir(dir);
        }

        /// <summary>The original changed since the copy was made (or last marked up to date): its files differ from the recorded ones.</summary>
        public static bool SourceChanged(string dir)
        {
            try
            {
                var record = ReadRecord(dir);
                var source = (string)record?["source"];
                var sourceDir = source == null ? null : UserTools.DirOf(source);
                if (sourceDir == null || !UserTools.IsReadOnlyDir(sourceDir)) return false;
                var recorded = HashMap(record["base"]);
                var now = Hashes(sourceDir);
                return now.Count != recorded.Count || now.Any(kv => !recorded.TryGetValue(kv.Key, out var h) || h != kv.Value);
            }
            catch (Exception ex)
            {
                Log.Warn($"Comparing {dir} with its original failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>"&lt;tool&gt;-custom", then -custom-2, -3 ... until free.</summary>
        public static string DefaultName(string source)
        {
            var bare = source.Substring(source.IndexOf(':') + 1);
            var stem = bare.Length > 40 ? bare.Substring(0, 40).TrimEnd('-') : bare;
            for (var i = 1; ; i++)
            {
                var name = stem + "-custom" + (i == 1 ? "" : "-" + i);
                if (NameProblem(name) == null) return name;
            }
        }

        /// <summary>Why a name can't be used for a new copy, or null.</summary>
        public static string NameProblem(string name)
        {
            if (!Regex.IsMatch(name ?? "", "^[a-z0-9][a-z0-9-]{1,47}$")) return "Name must be 2-48 characters: lowercase letters, digits and dashes.";
            if (UserTools.ReservedNames.Contains(name)) return $"'{name}' is reserved. Choose another name.";
            if (File.Exists(Path.Combine(UserTools.BuiltInRoot, name, "TOOL.md"))) return $"'{name}' is a built-in tool's name. Choose another name.";
            if (Directory.Exists(Path.Combine(UserTools.Root, name))) return $"The user already has a tool named '{name}'. Choose another name.";
            return null;
        }

        /// <summary>The source's version: TOOL.md version, else its plugin's version, else the date it was last updated.</summary>
        public static string VersionOf(string source, string dir, IDictionary<string, string> fm)
        {
            var v = UserTools.FrontValue(fm, "version");
            if (!string.IsNullOrEmpty(v)) return v;
            if (UserTools.IsPluginTool(source))
            {
                try
                {
                    var p = Plugins.PluginManager.Instance.Find(source.Substring(0, source.IndexOf(':')));
                    if (!string.IsNullOrEmpty(p?.Record.Version)) return p.Record.Version;
                }
                catch (Exception ex)
                {
                    Log.Warn("Plugin version for a fork: " + ex.Message);
                }
            }
            var updated = UserTools.FrontValue(fm, "updated");
            if (updated != null && DateTime.TryParse(updated, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d))
                return d.ToString("yyyy-MM-dd");
            return File.GetLastWriteTimeUtc(Path.Combine(dir, "TOOL.md")).ToString("yyyy-MM-dd");
        }

        public static void CopyFolder(string from, string to)
        {
            if (Directory.Exists(to)) throw new ToolException($"{to} already exists.");
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(from.Length).TrimStart('\\', '/');
                if (rel.Equals(RecordFile, StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(file, dest);
            }
        }

        /// <summary>
        /// The copy's TOOL.md: name, title and forked_from set, a ribbon title marked as the user's, the user's settings for the
        /// original (category, intelligence ...) carried over, and the original's id and title in the text replaced by the copy's.
        /// </summary>
        public static string Rewrite(string text, string source, string sourceTitle, string name, string title, string forkedFrom, IDictionary<string, string> settings)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
            var end = lines.Count > 0 && lines[0].Trim() == "---" ? lines.FindIndex(1, l => l.Trim() == "---") : -1;
            var header = end > 0 ? lines.Take(end + 1).ToList() : new List<string> { "---", "---" };
            var body = string.Join("\n", end > 0 ? lines.Skip(end + 1) : lines);
            foreach (var kv in settings ?? new Dictionary<string, string>())
                if (!new[] { "hidden", "paused", "name", "title" }.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) UserTools.SetHeader(header, kv.Key, kv.Value);
            UserTools.SetHeader(header, "name", name);
            UserTools.SetHeader(header, "title", title);
            var ribbon = header.FindIndex(1, l => l.StartsWith("ribbon_title:", StringComparison.OrdinalIgnoreCase));
            if (ribbon > 0 && ribbon < header.Count - 1 && !header[ribbon].EndsWith("(mine)")) header[ribbon] = header[ribbon].TrimEnd() + " (mine)";
            UserTools.SetHeader(header, "forked_from", forkedFrom);
            var now = DateTime.UtcNow.ToString("o");
            UserTools.SetHeader(header, "created", now);
            UserTools.SetHeader(header, "updated", now);
            if (!string.IsNullOrEmpty(sourceTitle) && sourceTitle != title) body = body.Replace(sourceTitle, title);
            body = Regex.Replace(body, @"(?<![A-Za-z0-9:-])" + Regex.Escape(source) + @"(?![A-Za-z0-9-])", name);
            return string.Join("\n", header) + "\n" + body.TrimStart('\n').Insert(0, "\n");
        }

        // ---------- the record and the hashes ----------

        public static JObject ReadRecord(string dir)
        {
            var path = Path.Combine(dir, RecordFile);
            try
            {
                return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception ex)
            {
                Log.Warn($"{path} unreadable: {ex.Message}");
                return null;
            }
        }

        public static void WriteRecord(string dir, string source, string version, IDictionary<string, string> theirs, IDictionary<string, string> mine)
        {
            var record = new JObject
            {
                ["source"] = source,
                ["version"] = version,
                ["updated"] = DateTime.UtcNow.ToString("o"),
                ["base"] = JObject.FromObject(theirs),
                ["mine"] = JObject.FromObject(mine),
            };
            WriteText(Path.Combine(dir, RecordFile), record.ToString(Formatting.Indented));
        }

        /// <summary>Writes a file whole or not at all (AtomicFile): a TOOL.md also holds the user's Standing instructions.</summary>
        public static void WriteText(string path, string text) => Replace(path, tmp => File.WriteAllText(tmp, text, new UTF8Encoding(false)));

        /// <summary>Copies a file over another whole or not at all (AtomicFile).</summary>
        public static void CopyFile(string from, string path) => Replace(path, tmp => File.Copy(from, tmp, true));

        private static void Replace(string path, Action<string> write)
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                write(tmp);
                AtomicFile.Replace(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        public static Dictionary<string, string> HashMap(JToken t) =>
            (t as JObject)?.Properties().ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Relative path (forward slashes) -> content hash of every file of a tool folder but the record and icon.png (the panel
        /// renders that). Cached until a file is added, removed or written.
        /// </summary>
        public static Dictionary<string, string> Hashes(string dir, bool fresh = false)
        {
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => (Full: f, Rel: f.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/')))
                .Where(f => !f.Rel.Equals(RecordFile, StringComparison.OrdinalIgnoreCase) && !f.Rel.Equals("icon.png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase).ToList();
            var infos = files.Select(f => new FileInfo(f.Full)).ToList();
            var sig = $"{files.Count}|{infos.Sum(i => i.Length)}|{(infos.Count == 0 ? 0 : infos.Max(i => i.LastWriteTimeUtc.Ticks))}";
            lock (Cache)
            {
                if (!fresh && Cache.TryGetValue(dir, out var hit) && hit.Sig == sig) return new Dictionary<string, string>(hit.Hashes, StringComparer.OrdinalIgnoreCase);
            }
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var sha = SHA1.Create())
                foreach (var f in files)
                {
                    var bytes = File.ReadAllBytes(f.Full);
                    if (IsTextBytes(bytes)) bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n")); // a checkout's line endings aren't a change
                    map[f.Rel] = string.Concat(sha.ComputeHash(bytes).Take(10).Select(b => b.ToString("x2")));
                }
            lock (Cache) Cache[dir] = (sig, map);
            return new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsText(string path)
        {
            try
            {
                return new FileInfo(path).Length < 2_000_000 && IsTextBytes(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static bool IsTextBytes(byte[] bytes) => !bytes.Take(8000).Contains((byte)0);

        /// <summary>
        /// A short line diff of two texts: changed lines with "- " (a) and "+ " (b), one line of context, "..." between hunks;
        /// cut at maxChars. Empty when they are the same.
        /// </summary>
        public static string Diff(string a, string b, int maxChars)
        {
            var x = a.Replace("\r\n", "\n").Split('\n');
            var y = b.Replace("\r\n", "\n").Split('\n');
            if (string.Join("\n", x) == string.Join("\n", y)) return "";
            // trim the common head and tail, then an LCS table over the middle (small for edits; refused when huge)
            var head = 0;
            while (head < x.Length && head < y.Length && x[head] == y[head]) head++;
            var tail = 0;
            while (tail < x.Length - head && tail < y.Length - head && x[x.Length - 1 - tail] == y[y.Length - 1 - tail]) tail++;
            var n = x.Length - head - tail;
            var m = y.Length - head - tail;
            if ((long)n * m > 4_000_000) return $"(large change: {n} lines vs {m} lines from line {head + 1}; read both files)";
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = x[head + i] == y[head + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            var ops = new List<(char Op, string Line)>();
            if (head > 0) ops.Add((' ', x[head - 1]));
            int p = 0, q = 0;
            while (p < n || q < m)
            {
                if (p < n && q < m && x[head + p] == y[head + q])
                {
                    ops.Add((' ', x[head + p]));
                    p++;
                    q++;
                }
                else if (p < n && (q == m || lcs[p + 1, q] >= lcs[p, q + 1])) ops.Add(('-', x[head + p++]));
                else ops.Add(('+', y[head + q++]));
            }
            if (tail > 0) ops.Add((' ', x[x.Length - tail]));
            var sb = new StringBuilder();
            var gap = false;
            for (var i = 0; i < ops.Count; i++)
            {
                var near = ops[i].Op != ' ' || (i > 0 && ops[i - 1].Op != ' ') || (i + 1 < ops.Count && ops[i + 1].Op != ' ');
                if (!near)
                {
                    gap = true;
                    continue;
                }
                if (gap && sb.Length > 0) sb.AppendLine("  ...");
                gap = false;
                var line = $"{ops[i].Op} {(ops[i].Line.Length > 300 ? ops[i].Line.Substring(0, 300) + "..." : ops[i].Line)}";
                if (sb.Length + line.Length > maxChars)
                {
                    sb.AppendLine("  ... (cut)");
                    break;
                }
                sb.AppendLine(line);
            }
            return sb.ToString().TrimEnd();
        }
    }
}

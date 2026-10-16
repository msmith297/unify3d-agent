using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>
    /// Smart Rebuild's working tools beyond the snapshot: understand how a design is built before changing it
    /// (analyze_design), keep known-good states to fall back to instead of starting over (checkpoint), and remember the
    /// plan, results and lessons across attempts and resets (rebuild_journal).
    ///
    /// The first real test (a 226-feature, 19-body mold, 2 -> 3 cavities) failed because the agent edited early features
    /// that changed the number of bodies, which renumbered every later body and broke each feature that had picked bodies
    /// by name; it then had nothing to fall back to but a full reset, and the reset (closing and reopening the file from
    /// its own code) cut the conversation off from the document. These tools are the answers to each of those.
    /// </summary>
    public sealed class RebuildTools
    {
        // ---------- which files are Smart Rebuild working copies ----------

        private static string CopiesFile => Path.Combine(Directory.CreateDirectory(Path.Combine(Paths.LocalDir, "snapshots")).FullName, "working-copies.json");

        /// <summary>Remember that `copy` is a Smart Rebuild working copy of `original` (restoring checkpoints is only allowed on these).</summary>
        internal static void RegisterWorkingCopy(string copy, string original)
        {
            try
            {
                var all = ReadCopies();
                all[copy.ToLowerInvariant()] = original;
                File.WriteAllText(CopiesFile, all.ToString());
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't record the working copy: " + ex.Message);
            }
        }

        private static JObject ReadCopies()
        {
            try
            {
                return File.Exists(CopiesFile) ? JObject.Parse(File.ReadAllText(CopiesFile)) : new JObject();
            }
            catch
            {
                return new JObject();
            }
        }

        private static string OriginalOf(string path) => string.IsNullOrEmpty(path) ? null : (string)ReadCopies()[path.ToLowerInvariant()];

        /// <summary>Where a design's journal and checkpoints live: named after the original file, so every attempt (each new copy) shares them.</summary>
        internal static string DesignDir(IModelDoc2 model)
        {
            var path = model.GetPathName();
            var key = Path.GetFileNameWithoutExtension(OriginalOf(path) ?? path);
            if (string.IsNullOrEmpty(key)) key = model.GetTitle();
            key = new string(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return Directory.CreateDirectory(Path.Combine(Paths.LocalDir, "snapshots", "rebuild-" + key)).FullName;
        }

        // ---------- analyze_design ----------

        public sealed class AnalyzeInput
        {
            [ToolParam("Optional: a feature name - also report everything that depends on it (its blast radius).")]
            public string Feature { get; set; }
        }

        /// <summary>Feature types that pick bodies: when the number or order of bodies before them changes, they break.</summary>
        private static readonly HashSet<string> BodySelecting = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DeleteBody", "BodyDelete", "Combine", "CombineBodies", "MoveCopyBody", "BodyMoveCopy", "Punch", "Indent", "Split", "SplitBody",
            "BodyPattern", "LPattern", "CirPattern", "MirrorPattern", "MirrorSolid", "Shell", "Intersect", "SaveBodyFeat", "Scale",
        };

        [AgentTool("analyze_design",
            "Map how the open part is built before changing it: inserted parts, the skeleton (sketches and planes many features hang " +
            "off), where each body comes from, the features that pick bodies (they break when bodies before them are added, removed " +
            "or reordered - the classic multi-body trap) and, for a feature, everything that depends on it. Use it to plan a rebuild.")]
        public ToolResult Analyze(ToolContext ctx, AnalyzeInput input)
        {
            var model = ctx.RequireModel();
            var feats = new List<IFeature>();
            for (var f = (IFeature)model.FirstFeature(); f != null; f = (IFeature)f.GetNextFeature())
                if (!Skip.Contains(f.GetTypeName2())) feats.Add(f);
            var index = feats.Select((f, i) => (f.Name, i + 1)).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Item2);

            // Dependants, transitively (children of a feature and of its sub-features such as its sketch).
            var children = new Dictionary<string, List<string>>();
            foreach (var f in feats)
            {
                var names = new HashSet<string>();
                void Add(IFeature x)
                {
                    foreach (IFeature c in (object[])x.GetChildren() ?? new object[0]) if (c != null) names.Add(Top(c, index));
                }
                Add(f);
                for (var sub = (IFeature)f.GetFirstSubFeature(); sub != null; sub = (IFeature)sub.GetNextSubFeature()) Add(sub);
                names.Remove(f.Name);
                children[f.Name] = names.Where(index.ContainsKey).ToList();
            }
            int Radius(string name)
            {
                var seen = new HashSet<string>();
                var stack = new Stack<string>(children.TryGetValue(name, out var c) ? c : new List<string>());
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    if (!seen.Add(n)) continue;
                    if (children.TryGetValue(n, out var cc)) foreach (var x in cc) stack.Push(x);
                }
                return seen.Count;
            }

            var sb = new StringBuilder();
            var bodies = model is IPartDoc part ? ((object[])part.GetBodies2((int)swBodyType_e.swSolidBody, false) ?? new object[0]).Cast<IBody2>().ToList() : new List<IBody2>();
            sb.AppendLine($"Design anatomy: {feats.Count} features, {bodies.Count} solid bodies.");

            var inserted = feats.Where(f => f.GetDefinition() is IDerivedPartFeatureData).ToList();
            foreach (var f in inserted)
                sb.AppendLine($"Inserted part: #{index[f.Name]} {f.Name} <- {((IDerivedPartFeatureData)f.GetDefinition()).PathName} ({Radius(f.Name)} later features depend on it)");

            var skeleton = feats.Where(f => f.GetTypeName2() == "RefPlane" || f.GetTypeName2() == "ProfileFeature" || f.GetTypeName2() == "3DProfileFeature")
                .Select(f => (f, n: Radius(f.Name))).Where(x => x.n >= 5).OrderByDescending(x => x.n).Take(12).ToList();
            if (skeleton.Count > 0)
                sb.AppendLine("Skeleton (many features hang off these): " + string.Join(", ", skeleton.Select(x => $"#{index[x.f.Name]} {x.f.Name} ({x.n})")));

            // Where each body comes from: a "Feature[n]" name says it; otherwise the first feature that has faces on it.
            sb.AppendLine("Bodies and their origin:");
            var nameRx = new Regex(@"^(.*)\[\d+\]$");
            foreach (var b in bodies)
            {
                var m = nameRx.Match(b.Name);
                string origin = m.Success && index.ContainsKey(m.Groups[1].Value) ? m.Groups[1].Value : null;
                if (origin == null)
                {
                    foreach (var f in feats)
                    {
                        var faces = (object[])f.GetFaces();
                        if (faces != null && faces.Cast<IFace2>().Any(x => ((IBody2)x.GetBody())?.Name == b.Name)) { origin = f.Name; break; }
                    }
                }
                sb.AppendLine($"  {b.Name}{(m.Success ? " (auto-named: renumbers when bodies before it change)" : "")} <- {(origin == null ? "?" : $"#{index[origin]} {origin}")}");
            }

            var fragile = feats.Where(f => BodySelecting.Contains(f.GetTypeName2()) || Regex.IsMatch(f.Name, @"^(Body-|Combine|Indent|Split|Delete/Keep)", RegexOptions.IgnoreCase)).ToList();
            if (fragile.Count > 0)
            {
                sb.AppendLine($"Features that pick bodies ({fragile.Count}) - adding, removing or reordering bodies BEFORE them renumbers what they picked and breaks them:");
                sb.AppendLine("  " + string.Join(", ", fragile.Select(f => $"#{index[f.Name]} {f.Name} [{f.GetTypeName2()}]")));
                var last = fragile.Max(f => index[f.Name]);
                sb.AppendLine($"  The last one is #{last}: new bodies and body operations added AFTER it can't break any of them.");
            }

            var early = feats.Take(Math.Min(40, feats.Count)).Select(f => (f, n: Radius(f.Name))).Where(x => x.n >= 10).OrderByDescending(x => x.n).Take(8).ToList();
            if (early.Count > 0) sb.AppendLine("Biggest blast radius near the top: " + string.Join(", ", early.Select(x => $"#{index[x.f.Name]} {x.f.Name} -> {x.n} dependants")));

            if (!string.IsNullOrWhiteSpace(input.Feature))
            {
                var name = input.Feature.Trim();
                if (!index.ContainsKey(name)) sb.AppendLine($"No top-level feature named '{name}'.");
                else
                {
                    var seen = new HashSet<string>();
                    var stack = new Stack<string>(children[name]);
                    while (stack.Count > 0)
                    {
                        var n = stack.Pop();
                        if (!seen.Add(n)) continue;
                        foreach (var x in children[n]) stack.Push(x);
                    }
                    sb.AppendLine($"Depends on {name} (#{index[name]}): {seen.Count} features - " + string.Join(", ", seen.OrderBy(x => index[x]).Take(80).Select(x => $"#{index[x]} {x}")));
                }
            }
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        private static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HistoryFolder", "SelectionSetFolder", "SensorFolder", "DocsFolder", "DetailCabinet", "CommentsFolder", "FavoriteFolder",
            "InkMarkupFolder", "EnvFolder", "MBD3DViewFolder", "LiveSectionFolder", "SurfaceBodyFolder", "SolidBodyFolder", "MaterialFolder",
            "EqnFolder", "OriginProfileFeature",
        };

        /// <summary>The top-level feature a child belongs to (a sketch inside a cut counts as the cut).</summary>
        private static string Top(IFeature f, Dictionary<string, int> index)
        {
            if (index.ContainsKey(f.Name)) return f.Name;
            foreach (IFeature c in (object[])f.GetChildren() ?? new object[0]) if (c != null && index.ContainsKey(c.Name)) return c.Name;
            return f.Name;
        }

        // ---------- checkpoint ----------

        public sealed class CheckpointInput
        {
            [ToolParam("save | list | restore", Required = true)]
            public string Action { get; set; }

            [ToolParam("save: a short name for the state (e.g. \"after-plates\"); restore: the checkpoint to go back to.")]
            public string Name { get; set; }
        }

        [AgentTool("checkpoint",
            "Smart Rebuild checkpoints: save a copy of the working file at a known-good state, list them, or restore one - the file " +
            "goes back to that state and the conversation carries on with the reopened document. Save one before every risky stage; " +
            "when a stage goes wrong, restore the last good checkpoint instead of undoing in circles or starting over. Restoring only " +
            "works on Smart Rebuild working copies (never on the user's original). Never close or reopen documents in your own code.")]
        public ToolResult Checkpoint(ToolContext ctx, CheckpointInput input)
        {
            var model = ctx.RequireModel();
            var dir = Directory.CreateDirectory(Path.Combine(DesignDir(model), "checkpoints")).FullName;
            var ext = Path.GetExtension(model.GetPathName());
            if (string.IsNullOrEmpty(ext)) return ToolResult.Error("Save the document first - checkpoints are copies of its file.");
            var name = new string((input.Name ?? "").Trim().Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray()).Trim('-');
            switch ((input.Action ?? "").Trim().ToLowerInvariant())
            {
                case "save":
                {
                    if (name.Length == 0) return ToolResult.Error("Give the checkpoint a name.");
                    var stamp = DateTime.Now.ToString("HHmmss");
                    var path = Path.Combine(dir, $"{stamp}-{name}{ext}");
                    int errors = 0, warnings = 0;
                    var ok = model.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)(swSaveAsOptions_e.swSaveAsOptions_Copy | swSaveAsOptions_e.swSaveAsOptions_Silent), null, null, ref errors, ref warnings);
                    if (!ok) return ToolResult.Error($"Saving the checkpoint failed ({(swFileSaveError_e)errors}).");
                    var broken = 0;
                    for (var f = (IFeature)model.FirstFeature(); f != null; f = (IFeature)f.GetNextFeature())
                        if (f.GetErrorCode2(out var warn) != 0 && !warn) broken++;
                    return ToolResult.Ok($"Checkpoint '{stamp}-{name}' saved ({broken} feature(s) with errors at this point). The working file is unchanged.");
                }
                case "list":
                {
                    var files = Directory.GetFiles(dir, "*" + ext).OrderBy(f => f).ToList();
                    return ToolResult.Ok(files.Count == 0 ? "No checkpoints yet." : "Checkpoints (oldest first):\n" + string.Join("\n", files.Select(f => "  " + Path.GetFileNameWithoutExtension(f))));
                }
                case "restore":
                {
                    var path = model.GetPathName();
                    if (OriginalOf(path) == null)
                        return ToolResult.Error("Restoring replaces the open file with the checkpoint, so it only works on a Smart Rebuild working copy - this file isn't one.");
                    var match = Directory.GetFiles(dir, "*" + ext).Where(f => Path.GetFileNameWithoutExtension(f).EndsWith(name, StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(f) == input.Name)
                        .OrderBy(f => f).LastOrDefault();
                    if (match == null) return ToolResult.Error($"No checkpoint '{input.Name}'. Use checkpoint list.");
                    var sw = ctx.Sw;
                    var type = model.GetType();
                    var title = model.GetTitle();
                    sw.QuitDoc(title); // close without saving: the working copy goes back to the checkpoint
                    File.Copy(match, path, true);
                    int errors = 0, warnings = 0;
                    var reopened = sw.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings);
                    if (reopened == null) return ToolResult.Error($"The checkpoint was copied back but SolidWorks couldn't open the file ({(swFileLoadError_e)errors}). Open {path} by hand.");
                    int activateErrors = 0;
                    sw.ActivateDoc3(reopened.GetTitle(), false, (int)swRebuildOnActivation_e.swRebuildActiveDoc, ref activateErrors);
                    Log.Info($"Smart Rebuild: restored checkpoint {Path.GetFileName(match)} into {path}");
                    return ToolResult.Ok($"Restored '{Path.GetFileNameWithoutExtension(match)}': the working file is back to that state and reopened; this conversation " +
                                         "continues on it. Variables from earlier code cells refer to the closed document - get entities fresh.");
                }
                default:
                    return ToolResult.Error("action must be save, list or restore.");
            }
        }

        // ---------- rebuild_journal ----------

        public sealed class JournalInput
        {
            [ToolParam("read | append", Required = true)]
            public string Action { get; set; }

            [ToolParam("append: what to record - the plan, a stage's result, a decision, or a lesson (what failed and why, what to do instead).")]
            public string Text { get; set; }
        }

        [AgentTool("rebuild_journal",
            "The Smart Rebuild journal for this design, kept outside the file so it survives resets, restores and new attempts on a " +
            "fresh copy: read it first (earlier attempts' plans and lessons), append the plan once agreed, each stage's result, every " +
            "decision, and every lesson the moment you learn it.")]
        public ToolResult Journal(ToolContext ctx, JournalInput input)
        {
            var model = ctx.RequireModel();
            var path = Path.Combine(DesignDir(model), "journal.md");
            switch ((input.Action ?? "").Trim().ToLowerInvariant())
            {
                case "read":
                {
                    if (!File.Exists(path)) return ToolResult.Ok("The journal is empty - this is the first Smart Rebuild of this design.");
                    var text = File.ReadAllText(path);
                    return ToolResult.Ok(text.Length > 24000 ? "(earlier entries trimmed)\n" + text.Substring(text.Length - 24000) : text);
                }
                case "append":
                {
                    if (string.IsNullOrWhiteSpace(input.Text)) return ToolResult.Error("Nothing to append.");
                    File.AppendAllText(path, $"\n## {DateTime.Now:yyyy-MM-dd HH:mm} - {Path.GetFileName(model.GetPathName())}\n{input.Text.Trim()}\n");
                    return ToolResult.Ok("Recorded in the journal.");
                }
                default:
                    return ToolResult.Error("action must be read or append.");
            }
        }

        /// <summary>For the Smart Rebuild screen: is there an earlier attempt to continue? The journal's tail, or null.</summary>
        internal static JObject Status(IModelDoc2 model)
        {
            if (model == null) return new JObject { ["journal"] = false };
            var workingCopy = OriginalOf(model.GetPathName()) != null;
            var path = Path.Combine(DesignDir(model), "journal.md");
            if (!File.Exists(path)) return new JObject { ["journal"] = false, ["workingCopy"] = workingCopy };
            var text = File.ReadAllText(path);
            return new JObject
            {
                ["journal"] = true,
                ["workingCopy"] = workingCopy, // continuing on the original would edit it: the screen makes a copy first
                ["updated"] = File.GetLastWriteTime(path).ToString("o"),
                ["tail"] = text.Length > 1200 ? text.Substring(text.Length - 1200) : text,
            };
        }
    }
}

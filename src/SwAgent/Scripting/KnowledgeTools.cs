using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Tools;
using SwAgent.UI;

namespace SwAgent.Scripting
{
    /// <summary>
    /// The self-improving loop's agent tools: API notes (save_api_note, list_api_notes) and helpers the agent writes itself
    /// (save_helper, read_helper, list_helpers, delete_helper, test_helper). Everything but test_helper only reads and writes
    /// files (SwBusyGate.ModelFreeTools); test_helper runs a cell through the execute_csharp path, with its busy gate, undo
    /// step and crash guard.
    /// </summary>
    public sealed class KnowledgeTools
    {
        private readonly ScriptHost _scripts;

        public KnowledgeTools(ScriptHost scripts)
        {
            _scripts = scripts;
        }

        private UserHelpers Helpers => _scripts.Helpers;
        private KnowledgeStore Store => Helpers.Store;

        // ---------------------------------------------------------------- API notes

        public sealed class SaveNoteInput
        {
            [ToolParam("What the lesson is about, short and general, e.g. \"FeatureByName is on IPartDoc, not IModelDoc2\". The same topic updates the note.", Required = true)]
            public string Topic { get; set; }

            [ToolParam("What failed: the call or assumption and the error, e.g. \"Model.GetBodies2(0, false) -> CS1061\".")]
            public string Wrong { get; set; }

            [ToolParam("What works, generalised: the exact call, cast or argument order, e.g. \"((IPartDoc)Model).GetBodies2(0, false).AsObjects<IBody2>()\".", Required = true)]
            public string Right { get; set; }

            [ToolParam("The API members it is about, e.g. [\"IPartDoc.GetBodies2\"]: a failing cell that calls one is shown the note.")]
            public string[] Apis { get; set; }
        }

        [AgentTool("save_api_note",
            "Save a lesson about the SolidWorks API so every later chat gets it right the first time: what was wrong, what works, the members it is " +
            "about. Only real, reusable API lessons (not facts about this model or request). The most used notes are in your system prompt; a failing " +
            "cell that calls those members shows the note.")]
        public ToolResult SaveNote(ToolContext ctx, SaveNoteInput input)
        {
            var (note, created) = Store.SaveNote(input.Topic, input.Wrong, input.Right, input.Apis);
            _scripts.Learned(ctx?.ThreadId);
            return ToolResult.Ok($"{(created ? "Saved" : "Updated")} API note {note.Id}: {note.Topic}." +
                                 (note.Apis.Length == 0 ? " It names no API members, so failing cells won't show it: add apis if it is about specific calls." : ""));
        }

        public sealed class ListNotesInput
        {
            [ToolParam("Words to look for (all must match: topic, wrong, right, apis). Omit to list every note.")]
            public string Query { get; set; }
        }

        [AgentTool("list_api_notes", "The API notes you saved (save_api_note), most useful first: id, topic, what works and what didn't. Use a query to find the notes about a call before you write it.")]
        public ToolResult ListNotes(ToolContext ctx, ListNotesInput input)
        {
            var notes = Store.FindNotes(input?.Query);
            if (notes.Count == 0) return ToolResult.Ok(string.IsNullOrWhiteSpace(input?.Query) ? "No API notes saved yet." : "No API note matches that.");
            if (!string.IsNullOrWhiteSpace(input?.Query)) Store.Hit(notes.Take(20).Select(n => n.Id));
            var sb = new StringBuilder();
            foreach (var n in notes.Take(60))
            {
                sb.AppendLine($"{n.Id} {n.Topic} (used {n.Hits}x){(n.Apis?.Length > 0 ? " [" + string.Join(", ", n.Apis) + "]" : "")}");
                sb.AppendLine("  right: " + n.Right);
                if (!string.IsNullOrEmpty(n.Wrong)) sb.AppendLine("  wrong: " + n.Wrong);
            }
            if (notes.Count > 60) sb.AppendLine($"... {notes.Count - 60} more: narrow the query.");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        // ---------------------------------------------------------------- helpers

        public sealed class SaveHelperInput
        {
            [ToolParam("The helper's name = its class name, PascalCase, e.g. \"BodyMoves\". Saving it again makes a new version.", Required = true)]
            public string Name { get; set; }

            [ToolParam("The whole file: namespace SwAgent.UserHelpers { public static class <Name> { ... } } with public static methods; the cells' usings " +
                       "plus SwAgent.Scripting.Helpers. No cell globals (Model, swApp, mm/deg, Print, Bodies/Features/Sel): take IModelDoc2 model as a " +
                       "parameter, write meters and degrees. Build on the checked helpers through a BodiesApi/FeaturesApi/SelApi parameter or this-receiver " +
                       "(TurnInPlace(this BodiesApi bodies, IBody2 b, ...) is called Bodies.TurnInPlace(b, ...)). An extension method can make a failing " +
                       "form work (this IModelDoc2 model: Model.X(...)). It may not call your other helpers; other types go inside the class.", Required = true)]
            public string Code { get; set; }

            [ToolParam("One line for the helper index: what it does and checks.", Required = true)]
            public string Summary { get; set; }

            [ToolParam("Raw API members it replaces, e.g. [\"IAssemblyDoc.GetComponentByName\"]: a cell calling one is pointed here.")]
            public string[] Wraps { get; set; }
        }

        [AgentTool("save_helper",
            "Save a C# helper every execute_csharp cell (every chat) can call from now on, so a mistake you recovered from can't cost turns again: it " +
            "does the fiddly part (marks, signs, array casts, the right interface) robustly. Compiled first, alone and with the others: on errors nothing " +
            "is saved. Then test it with test_helper. Never write helpers that change the model in ways their name doesn't say.")]
        public async Task<ToolResult> SaveHelper(ToolContext ctx, SaveHelperInput input)
        {
            var name = (input.Name ?? "").Trim();
            var code = input.Code ?? "";
            var existing = Store.Helper(name);
            if (existing != null && existing.Source != "user")
                return ToolResult.Error($"{name} is a helper of {existing.Source}: plugin helpers are read-only. Save yours under another name.");
            var builtIn = HelperIndex.BuiltInNamed(name).FirstOrDefault();
            if (builtIn != null) return ToolResult.Error($"{name} is a built-in helper ({builtIn.Signature ?? builtIn.Name}): use it, or save yours under another name.");

            var (error, signature) = await Task.Run(() => Helpers.Check(name, code, input.Summary, input.Wraps));
            if (error != null) return ToolResult.Error($"Not saved: {name} {error}");

            var record = Store.SaveHelper(name, code, input.Summary, input.Wraps, signature);
            _scripts.Learned(ctx?.ThreadId);
            var build = await Task.Run(() => Helpers.Current());
            if (build.Find(name) == null)
            {
                build.Skipped.TryGetValue(name, out var why);
                return ToolResult.Error($"Saved {name} v{record.Version}, but the helpers build left it out: {why ?? "unknown reason (see the add-in log)"}. Fix it and save again.");
            }
            return ToolResult.Ok($"Saved helper {name} v{record.Version}: {signature}. Every execute_csharp cell can call it from the next cell on, in every chat " +
                                 $"(the script session restarts once, keeping your variables).{(record.Version > 1 ? $" The previous version is kept as {name}.v{record.Version - 1}.cs." : "")} " +
                                 "Now test it: test_helper with a short cell that calls it on this model and checks the result.");
        }

        public sealed class NameInput
        {
            [ToolParam("The helper's name.", Required = true)]
            public string Name { get; set; }
        }

        [AgentTool("read_helper", "A helper's code and details (yours, a plugin's, or a built-in one's signature): read it before changing it with save_helper, or to see exactly what it does.")]
        public async Task<ToolResult> ReadHelper(ToolContext ctx, NameInput input)
        {
            var name = (input.Name ?? "").Trim();
            var builtIn = HelperIndex.BuiltInNamed(name);
            if (builtIn.Count > 0)
                return ToolResult.Ok("Built-in helper (part of the add-in, read-only):\n" + string.Join("\n", builtIn.Select(h => $"{h.Signature ?? h.Name} - {h.Summary}" + (h.Wraps?.Length > 0 ? $" (wraps {string.Join(", ", h.Wraps)})" : ""))));
            // Tool calls run on the SolidWorks UI thread, and compiling the helpers can take seconds: off it.
            var build = await Task.Run(() => Helpers.Current());
            var built = build.Find(name);
            var record = Store.Helper(name);
            string code = null;
            if (built?.Source != null && built.Source != "user" && built.Path != null && File.Exists(built.Path)) code = File.ReadAllText(built.Path);
            else if (record?.Source == "user") code = Store.HelperCode(name);
            if (code == null) return ToolResult.Error($"No helper named {name}. list_helpers shows them.");
            var sb = new StringBuilder();
            sb.AppendLine($"{name} ({built?.Source ?? record?.Source ?? "user"}{(record?.Version > 0 ? $", v{record.Version}" : "")}{(record != null ? $", used {record.Uses}x" : "")})");
            if (built?.Signature != null || record?.Signature != null) sb.AppendLine("signature: " + (built?.Signature ?? record.Signature));
            if (!string.IsNullOrEmpty(built?.Summary ?? record?.Summary)) sb.AppendLine("summary: " + (built?.Summary ?? record.Summary));
            var wraps = built?.Wraps ?? record?.Wraps;
            if (wraps?.Length > 0) sb.AppendLine("wraps: " + string.Join(", ", wraps));
            if (record?.Disabled == true) sb.AppendLine("DISABLED: " + record.DisabledReason);
            if (build.Skipped.TryGetValue(name, out var skipped)) sb.AppendLine("NOT BUILT: " + skipped);
            if (!string.IsNullOrEmpty(record?.LastTest)) sb.AppendLine("last test: " + record.LastTest);
            if (built?.Source != null && built.Source != "user") sb.AppendLine("(a plugin's helper: read-only)");
            sb.AppendLine("--- code ---").Append(code.TrimEnd());
            return ToolResult.Ok(sb.ToString());
        }

        public sealed class ListHelpersInput
        {
            [ToolParam("Words to look for in the name, signature, summary or wraps. Omit to list all.")]
            public string Query { get; set; }
        }

        [AgentTool("list_helpers", "Every helper cells can call (built-in, plugins', yours) with its signature and summary, plus yours that are disabled or don't build and why.")]
        public async Task<ToolResult> ListHelpers(ToolContext ctx, ListHelpersInput input)
        {
            var build = await Task.Run(() => Helpers.Current());
            var words = (input?.Query ?? "").ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            bool Match(string text) => words.All((text ?? "").ToLowerInvariant().Contains);
            var records = Store.Helpers();
            var sb = new StringBuilder();
            foreach (var h in HelperIndex.Merge(build.Helpers.Select(b => b.Info())))
            {
                if (!Match(h.Name + " " + h.Signature + " " + h.Summary + " " + string.Join(" ", h.Wraps ?? new string[0]))) continue;
                var r = records.FirstOrDefault(x => x.Name == h.Name && x.Source == h.Source);
                sb.AppendLine($"[{h.Source}] {h.Signature ?? h.Name} - {h.Summary}{(r != null && r.Uses > 0 ? $" (used {r.Uses}x)" : "")}");
            }
            foreach (var r in records.Where(r => r.Disabled && Match(r.Name + " " + r.Summary)))
                sb.AppendLine($"[{r.Source}, disabled] {r.Name} - {r.DisabledReason}");
            foreach (var kv in build.Skipped.Where(kv => Match(kv.Key)))
                sb.AppendLine($"[not built] {kv.Key} - {kv.Value}");
            return ToolResult.Ok(sb.Length == 0 ? (words.Length > 0 ? "No helper matches that." : "No helpers yet.") : sb.ToString().TrimEnd());
        }

        [AgentTool("delete_helper", "Delete one of your helpers (plugin and built-in helpers can't be deleted). Its last version is kept as a file, so this can be undone by saving it again.")]
        public async Task<ToolResult> DeleteHelper(ToolContext ctx, NameInput input)
        {
            var name = (input.Name ?? "").Trim();
            var record = Store.Helper(name);
            if (record == null)
            {
                if (HelperIndex.BuiltInNamed(name).Count > 0) return ToolResult.Error($"{name} is built in: it can't be deleted.");
                var built = (await Task.Run(() => Helpers.Current())).Find(name);
                return ToolResult.Error(built != null ? $"{name} belongs to {built.Source}: plugin helpers can't be deleted (disable the plugin instead)." : $"No helper named {name}.");
            }
            if (record.Source != "user") return ToolResult.Error($"{name} belongs to {record.Source}: plugin helpers can't be deleted (disable the plugin instead).");
            Store.DeleteHelper(name);
            return ToolResult.Ok($"Deleted helper {name}. Cells can't call it from the next cell on (its last version is kept as {name}.v{Math.Max(1, record.Version)}.cs).");
        }

        public sealed class TestHelperInput
        {
            [ToolParam("The helper to test.", Required = true)]
            public string Name { get; set; }

            [ToolParam("A test cell (C#, like execute_csharp) that calls the helper on this model and checks the result by measurement. It runs as one undo step: undo it afterwards if it changed the model only to test.", Required = true)]
            public string Code { get; set; }

            [ToolParam("Capture an isometric screenshot after running (default false).")]
            public bool? CaptureAfter { get; set; }
        }

        [AgentTool("test_helper",
            "Test a saved helper: runs a test cell through the same path as execute_csharp (the busy gate, one undo step, the crash guard) and records " +
            "the result on the helper. Do this after every save_helper, before you rely on the helper.")]
        public async Task<ToolResult> TestHelper(ToolContext ctx, TestHelperInput input)
        {
            var name = (input.Name ?? "").Trim();
            var build = await Task.Run(() => Helpers.Current());
            var record = Store.Helper(name);
            if (build.Find(name) == null)
            {
                if (record?.Disabled == true) return ToolResult.Error($"{name} is disabled: {record.DisabledReason}");
                if (build.Skipped.TryGetValue(name, out var why)) return ToolResult.Error($"{name} isn't built: {why}");
                return ToolResult.Error($"No helper named {name}: save it with save_helper first.");
            }
            var result = await _scripts.ExecuteAsync(ctx, new ScriptHost.ExecuteInput
            {
                Title = "Test helper " + name,
                Kind = "change",
                Code = input.Code,
                CaptureAfter = input.CaptureAfter,
            }, learn: false);
            var first = (string)result.Content.FirstOrDefault(c => (string)c["type"] == "text")?["text"] ?? "";
            var outcome = result.IsError ? "failed: " + first.Split('\n').Skip(1).FirstOrDefault(l => l.Trim().Length > 0)?.Trim() : "ok";
            Safe.Run("Record the helper test", () => Store.UpdateHelpers(get =>
            {
                var r = get(name, record?.Source ?? build.Find(name).Source);
                if (r != null) r.LastTest = $"{outcome} ({DateTime.Now:yyyy-MM-dd HH:mm})";
            }));
            _scripts.Learned(ctx?.ThreadId);
            return result;
        }
    }

    /// <summary>
    /// What the panel puts in the system prompt (knowledge.index): every helper cells can call, one line each, and the most used
    /// API notes, together capped at about 2,600 tokens. The agent's own and the plugins' helpers come first (OwnShare), then
    /// the built-in Bodies./Features./Sel. helpers in short lines and the interop extensions as one line of signatures
    /// (BuiltInShare), then the notes in what is left (at least 2,400 characters).
    /// </summary>
    internal static class KnowledgeIndex
    {
        public const int MaxChars = 10400;
        private const int OwnShare = 3000;
        private const int BuiltInShare = 5000;

        /// <summary>The name of the index entry that lists the built-in interop extensions together.</summary>
        public const string ExtensionsEntry = "extensions";

        public static JObject Build(UserHelpers helpers, int maxChars = MaxChars)
        {
            var build = helpers.Current();
            var all = HelperIndex.Merge(build.Helpers.Select(b => b.Info()));
            var helperLines = new JArray();
            var used = 0;
            var own = 0;
            var builtInUsed = 0;
            var listed = 0;
            bool Add(string name, string source, string signature, string summary, string line, bool builtIn)
            {
                var cost = line.Length + 3;
                if (used + cost > maxChars) return false;
                if (builtIn ? builtInUsed + cost > BuiltInShare : own + cost > OwnShare) return true;
                if (builtIn) builtInUsed += cost;
                else own += cost;
                used += cost;
                helperLines.Add(new JObject { ["name"] = name, ["source"] = source, ["signature"] = signature, ["summary"] = summary, ["line"] = line });
                return true;
            }
            foreach (var h in all.Where(h => !SwAgent.Scripting.Helpers.HelperCatalog.IsExtension(h)))
            {
                var builtIn = h.Source == "built-in";
                // Built-in lines keep the summary's first clause: the helper's own errors say what it checked.
                var summary = builtIn ? FirstClause(h.Summary) : h.Summary?.Trim();
                var line = Line($"{(string.IsNullOrWhiteSpace(h.Signature) ? h.Name : h.Signature.Trim())}{(string.IsNullOrWhiteSpace(summary) ? "" : " - " + summary)}", builtIn ? 270 : 260);
                var before = helperLines.Count;
                if (!Add(h.Name, h.Source, h.Signature, h.Summary, line, builtIn)) break;
                if (helperLines.Count > before) listed++;
            }
            // The interop extensions: their names say what they do, so signatures only, on one line.
            var extensions = all.Where(SwAgent.Scripting.Helpers.HelperCatalog.IsExtension).ToList();
            var count = helperLines.Count;
            if (extensions.Count > 0 &&
                Add(ExtensionsEntry, "built-in", null, "the built-in interop extensions", Line("Interop extensions (x.As*: any SolidWorks array, null gives []): " +
                    string.Join("; ", extensions.Select(h => h.Signature ?? h.Name)), 1200), true) && helperLines.Count > count)
                listed += extensions.Count;
            var disabled = new JArray(helpers.Store.Helpers().Where(r => r.Disabled).Select(r => new JObject { ["name"] = r.Name, ["source"] = r.Source, ["reason"] = r.DisabledReason }));

            var notes = helpers.Store.Notes().OrderByDescending(n => n.Hits).ThenByDescending(n => n.Updated ?? n.Created).ToList();
            var noteLines = new JArray();
            foreach (var n in notes)
            {
                var line = Line($"{n.Topic}: {n.Right}{(string.IsNullOrEmpty(n.Wrong) ? "" : " (not: " + n.Wrong + ")")}", 320);
                if (used + line.Length + 3 > maxChars) break;
                used += line.Length + 3;
                noteLines.Add(new JObject { ["id"] = n.Id, ["topic"] = n.Topic, ["right"] = n.Right, ["wrong"] = n.Wrong, ["apis"] = new JArray(n.Apis ?? new string[0]), ["hits"] = n.Hits, ["line"] = line });
            }
            return new JObject
            {
                ["helpers"] = helperLines,
                ["disabled"] = disabled,
                ["notes"] = noteLines,
                ["omitted"] = new JObject { ["helpers"] = all.Count - listed, ["notes"] = notes.Count - noteLines.Count },
            };
        }

        /// <summary>"Move a body ... with one Move/Copy Body feature; checked: ..." -> the part before "; checked" or the first ". ".</summary>
        internal static string FirstClause(string s)
        {
            s = System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
            var cut = -1;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, @"(?<!\be\.g|\bi\.e)\. |; checked|\. Checked"))
            {
                cut = m.Index;
                break;
            }
            return cut > 0 ? s.Substring(0, cut).TrimEnd('.') + "." : s;
        }

        private static string Line(string s, int max)
        {
            s = System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 3).TrimEnd() + "...";
        }
    }

    /// <summary>
    /// The panel's knowledge RPCs: knowledge.index() -> { helpers: [{ name, source, signature, summary, line }], disabled:
    /// [{ name, source, reason }], notes: [{ id, topic, right, wrong, apis, hits, line }], omitted: { helpers, notes } }, and the
    /// event knowledge.changed (no data) when notes or helpers change - the panel then loads the index again.
    /// </summary>
    internal static class KnowledgeBridge
    {
        private static WebBridge _bridge;
        private static bool _subscribed;

        public static void Attach(WebBridge bridge)
        {
            _bridge = bridge;
            var helpers = UserHelpers.Instance;
            // Compiling the helpers can take seconds the first time: never on the UI thread.
            bridge.RegisterAsync("knowledge.index", async p => await Task.Run(() => (object)KnowledgeIndex.Build(helpers)));
            if (_subscribed) return; // the bridge is attached again when the panel reloads
            _subscribed = true;
            helpers.Changed += () => _bridge?.Emit("knowledge.changed", new JObject());
            helpers.Store.NotesChanged += () => _bridge?.Emit("knowledge.changed", new JObject());
        }
    }
}

using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Tools;
using SwAgent.UI;

namespace SwAgent.Memory
{
    /// <summary>
    /// Standing instructions (docs/agent-memory.md): remember, list_memory and forget. Files only (SwBusyGate.ModelFreeTools).
    /// The panel refuses them in turns a plugin or another agent started: only the local user's words are remembered.
    /// </summary>
    public sealed class MemoryTools
    {
        private readonly Func<MemoryStore> _store;

        public MemoryTools()
            : this(() => MemoryStore.Instance)
        {
        }

        internal MemoryTools(Func<MemoryStore> store)
        {
            _store = store;
        }

        /// <summary>Whether saving is on (tests replace it).</summary>
        internal Func<bool> Saving = SavingOn;

        /// <summary>Options "Save what I tell it to remember" (settings.json saveMemory, default on).</summary>
        internal static bool SavingOn()
        {
            var v = SettingsStore.LoadSettings()["saveMemory"];
            return v == null || v.Type != JTokenType.Boolean || (bool)v;
        }

        public sealed class RememberInput
        {
            [ToolParam("Where it applies: \"general\" (everything you do), \"tool:<name>\" (one tool or workflow; a plugin's tool is \"tool:<plugin>:<tool>\") " +
                       "or \"skill:<plugin>:<skill>\". The narrowest one that clearly fits.", Required = true)]
            public string Scope { get; set; }

            [ToolParam("The instruction as a short rule in the user's intent, generalised only as far as they meant, e.g. \"hide leftover part copies " +
                       "outside the cavities when the mold is done\".", Required = true)]
            public string Text { get; set; }

            [ToolParam("Ids of older notes this one replaces (it contradicts or merges them). Undo puts them back.")]
            public string[] Replaces { get; set; }

            [ToolParam("Tools only: when the instruction is to always use one of the tool's saved input profiles, its name. It becomes the tool's " +
                       "default instead of a note.")]
            public string Profile { get; set; }
        }

        [AgentTool("remember",
            "Save a standing instruction from the user (\"always\", \"never\", \"from now on\", or a correction plus \"do it like this\") so it applies " +
            "every time: in a tool's or skill's instructions, or your general memory. Only the local user's own words.")]
        public ToolResult Remember(ToolContext ctx, RememberInput input)
        {
            if (!Saving())
                return ToolResult.Error("The user turned off saving instructions (Options > What the agent remembers). Apply it in this chat only, and say so.");
            var store = _store();
            var scope = store.Resolve(input.Scope);
            var profile = string.IsNullOrWhiteSpace(input.Profile) ? null : input.Profile.Trim();
            if (profile != null)
            {
                // "always use Shop A for molds" sets the default profile, not a note (docs/tool-profiles.md)
                if (scope.Kind != "tool") return ToolResult.Error("profile is for a tool's scope (tool:<name>). Nothing was saved.");
                var found = ToolProfiles.Find(scope.Name, profile);
                if (found == null)
                {
                    var names = ToolProfiles.Names(scope.Name);
                    return ToolResult.Error($"The tool has no profile named \"{profile}\" ({(names.Count == 0 ? "it has none" : "its profiles: " + string.Join(", ", names))}). " +
                                            "Nothing was saved. Save the values with save_tool_profile (make_default: true) instead.");
                }
                ToolProfiles.SetDefault(scope.Name, found.Name);
                return ToolResult.Ok($"The \"{found.Name}\" profile is now the default of {scope.Name}: its inputs open with it (no note saved; the form's " +
                                     "Profile row or Options change it). Tell the user in one line.");
            }
            var saved = store.Remember(scope, input.Text, input.Replaces);
            if (saved.Existed) return ToolResult.Ok($"Already saved in {scope.Label}: {saved.Note.Text} (id {saved.Note.Id}).");
            var sb = new StringBuilder($"Saved to {scope.Label}: {saved.Note.Text} (id {saved.Note.Id}).");
            foreach (var r in saved.Replaced) sb.Append($" Replaced: {r.Note.Text}.");
            if (saved.Warning != null) sb.Append(' ').Append(saved.Warning);
            sb.Append(" Tell the user where it went in one line; the chat shows it with Undo.");
            return ToolResult.Ok(sb.ToString());
        }

        public sealed class ListInput
        {
            [ToolParam("\"general\", \"tool:<name>\" or \"skill:<plugin>:<skill>\". Omit to list everything.")]
            public string Scope { get; set; }
        }

        [AgentTool("list_memory", "The standing instructions the user gave you, with their ids: general memory and the notes on tools and skills.")]
        public ToolResult List(ToolContext ctx, ListInput input)
        {
            var store = _store();
            var scopes = string.IsNullOrWhiteSpace(input?.Scope) ? store.Scopes() : new System.Collections.Generic.List<MemoryScope> { store.Resolve(input.Scope) };
            var sb = new StringBuilder();
            foreach (var s in scopes)
            {
                var notes = store.Notes(s);
                if (notes.Count == 0) continue;
                sb.AppendLine($"{s.Key} ({s.Label}):");
                foreach (var n in notes) sb.AppendLine($"  {n.Id} {(string.IsNullOrEmpty(n.Date) ? "" : n.Date + ": ")}{n.Text}");
            }
            if (!Saving()) sb.AppendLine("(Saving new instructions is turned off in Options.)");
            return ToolResult.Ok(sb.Length == 0 ? "Nothing saved yet." : sb.ToString().TrimEnd());
        }

        public sealed class ForgetInput
        {
            [ToolParam("The note's id (list_memory).", Required = true)]
            public string Id { get; set; }
        }

        [AgentTool("forget", "Delete one standing instruction by its id, when the user asks or it no longer applies.")]
        public ToolResult Forget(ToolContext ctx, ForgetInput input)
        {
            var hit = _store().Forget(input.Id);
            return hit == null
                ? ToolResult.Error($"No note with id {input.Id}. list_memory shows the ids.")
                : ToolResult.Ok($"Deleted from {hit.Value.Scope.Label}: {hit.Value.Note.Text}");
        }
    }

    /// <summary>
    /// The panel's memory RPCs: memory.index() (the system prompt's part: general notes, skills with notes, how full),
    /// memory.list() (Options), memory.edit({ id, text }), memory.forget({ id }), memory.undo({ id }) (the chat's chip),
    /// userTools.setHidden({ name, hidden }) (a built-in or plugin tool kept off the ribbon and palette); the event
    /// memory.changed (no data) when notes change.
    /// </summary>
    internal static class MemoryBridge
    {
        private static WebBridge _bridge;
        private static bool _subscribed;

        public static void Attach(WebBridge bridge)
        {
            _bridge = bridge;
            var store = MemoryStore.Instance;
            bridge.Register("memory.index", p => store.Index(MemoryTools.SavingOn()));
            bridge.Register("memory.list", p => store.List());
            bridge.Register("memory.edit", p => (object)store.Edit((string)p["id"], (string)p["text"])?.ToJson());
            bridge.Register("memory.forget", p => store.Forget((string)p["id"]) != null);
            bridge.Register("memory.undo", p => store.Undo((string)p["id"]));
            bridge.Register("userTools.setHidden", p =>
            {
                var name = (string)p["name"];
                if (!UserTools.IsReadOnly(name)) throw new ToolException("Only built-in and plugin tools can be hidden; delete your own instead.");
                UserTools.SetValue(name, "hidden", (bool?)p["hidden"] == true ? "true" : null);
                return true;
            });
            if (_subscribed) return; // the bridge is attached again when the panel reloads
            _subscribed = true;
            store.Changed += () => _bridge?.Emit("memory.changed", new JObject());
        }
    }
}

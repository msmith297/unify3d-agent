using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.UI;

namespace SwAgent.Tools
{
    /// <summary>
    /// The panel's profile RPCs outside a tool form (Options > Tool profiles, /run --profile), all on the SolidWorks UI thread:
    ///   toolProfiles.tools -> [{ name, title, profiles, default }]: the tools with inputs
    ///   toolProfiles.list { tool } -> { tool, default, lastUsed, profiles: [{ name, locked, isDefault, updated, inputs }] }
    ///   toolProfiles.setDefault { tool, name | null } / rename { tool, name, newName } / duplicate { tool, name } /
    ///       delete { tool, name } / import { tool, content? } (no content: a file dialog) -> the list
    ///   toolProfiles.export { tool, name } -> { path } | null (a save dialog); toolProfiles.openFolder { tool }
    ///   toolProfiles.run { tool, profile } -> null: runs the tool with its inputs loaded from that profile
    /// Refusals are errors with a message for the user (ToolException).
    /// </summary>
    internal static class ProfilesBridge
    {
        public static void Register(WebBridge bridge, Func<IntPtr> owner)
        {
            bridge.Register("toolProfiles.tools", p => new JArray(UserTools.ListTools()
                .Where(t => (bool?)t["hasInputs"] == true)
                .Select(t => new JObject
                {
                    ["name"] = t["name"],
                    ["title"] = t["title"],
                    ["profiles"] = t["profiles"],
                    ["default"] = ToolProfiles.DefaultName((string)t["name"]),
                })));
            bridge.Register("toolProfiles.list", p => List(Tool(p)));
            bridge.Register("toolProfiles.setDefault", p =>
            {
                ToolProfiles.SetDefault(Tool(p), (string)p["name"]);
                return List(Tool(p));
            });
            bridge.Register("toolProfiles.rename", p =>
            {
                ToolProfiles.Rename(Tool(p), (string)p["name"], (string)p["newName"]);
                return List(Tool(p));
            });
            bridge.Register("toolProfiles.duplicate", p =>
            {
                ToolProfiles.Duplicate(Tool(p), (string)p["name"], (string)p["newName"]);
                return List(Tool(p));
            });
            bridge.Register("toolProfiles.delete", p =>
            {
                ToolProfiles.Delete(Tool(p), (string)p["name"]);
                return List(Tool(p));
            });
            bridge.Register("toolProfiles.import", p =>
            {
                var content = (string)p["content"] ?? PickFile(owner());
                if (content == null) return null;
                var added = ToolProfiles.Import(Tool(p), content, out var note);
                var list = List(Tool(p));
                list["added"] = added.Name;
                list["note"] = note;
                return list;
            });
            bridge.Register("toolProfiles.export", p =>
            {
                var path = Export(Tool(p), (string)p["name"], owner());
                return path == null ? null : new JObject { ["path"] = path };
            });
            bridge.Register("toolProfiles.openFolder", p =>
            {
                OpenFolder(Tool(p));
                return null;
            });
            bridge.Register("toolProfiles.run", p =>
            {
                var tool = Tool(p);
                var profile = (string)p["profile"];
                if (!string.IsNullOrWhiteSpace(profile) && ToolProfiles.Find(tool, profile) == null)
                    throw new ToolException($"There is no profile '{profile}' for {tool}. Its profiles: {string.Join(", ", ToolProfiles.Names(tool))}.");
                Run(tool, profile);
                return null;
            });
        }

        private static string Tool(JToken p)
        {
            var tool = ((string)p?["tool"] ?? "").Trim();
            if (tool.Length == 0 || UserTools.DirOf(tool) == null) throw new ToolException($"There is no tool '{tool}'.");
            return tool;
        }

        private static JObject List(string tool)
        {
            var def = ToolProfiles.DefaultName(tool);
            return new JObject
            {
                ["tool"] = tool,
                ["default"] = def,
                ["lastUsed"] = ToolProfiles.LastUsed(tool),
                ["profiles"] = new JArray(ToolProfiles.List(tool).Select(x => new JObject
                {
                    ["name"] = x.Name,
                    ["locked"] = x.Locked,
                    ["isDefault"] = ToolProfiles.SameName(x.Name, def),
                    ["updated"] = x.Updated,
                    ["inputs"] = x.Values.Count,
                })),
            };
        }

        /// <summary>
        /// Run the tool as the ribbon does, its inputs opening with this profile (null: as usual). Posted, so an agent tool call
        /// or RPC that asks for it returns first; the form then waits for the user's OK.
        /// </summary>
        public static void Run(string tool, string profile)
        {
            var controller = AgentAddIn.Instance?.Controller ?? throw new ToolException("Tools can't run right now (the add-in is starting or closing).");
            if (!string.IsNullOrWhiteSpace(profile)) ToolProfiles.Request(tool, profile.Trim());
            void Start() => Safe.Run($"Run tool {tool}", () => _ = controller.RunToolAsync(tool, null));
            var ui = SynchronizationContext.Current;
            if (ui != null) ui.Post(_ => Start(), null);
            else Start();
        }

        /// <summary>A save dialog, then the profile written there; the path, or null when cancelled.</summary>
        public static string Export(string tool, string name, IntPtr owner)
        {
            var profile = ToolProfiles.Find(tool, name) ?? throw new ToolException($"There is no profile '{name}' for this tool.");
            using (var dialog = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Export tool profile",
                Filter = "Tool profile (*.json)|*.json",
                FileName = ToolProfiles.Slug(profile.Name) + ".json",
                OverwritePrompt = true,
            })
            {
                if (dialog.ShowDialog(Window(owner)) != System.Windows.Forms.DialogResult.OK) return null;
                ToolProfiles.Export(tool, profile.Name, dialog.FileName);
                return dialog.FileName;
            }
        }

        /// <summary>An open dialog for a profile file; its text, or null when cancelled.</summary>
        public static string PickFile(IntPtr owner)
        {
            using (var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Load a tool profile",
                Filter = "Tool profile (*.json)|*.json",
                Multiselect = false,
            })
            {
                if (dialog.ShowDialog(Window(owner)) != System.Windows.Forms.DialogResult.OK) return null;
                var info = new FileInfo(dialog.FileName);
                if (info.Length > 1024 * 1024) throw new ToolException("That file is too big to be a tool profile.");
                return File.ReadAllText(dialog.FileName);
            }
        }

        /// <summary>The user's profiles of the tool in Explorer (created when there are none yet).</summary>
        public static void OpenFolder(string tool)
        {
            var dir = ToolProfiles.UserDir(tool);
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }

        private static System.Windows.Forms.IWin32Window Window(IntPtr owner) => owner == IntPtr.Zero ? null : new OwnerWindow(owner);

        private sealed class OwnerWindow : System.Windows.Forms.IWin32Window
        {
            public OwnerWindow(IntPtr handle) => Handle = handle;

            public IntPtr Handle { get; }
        }
    }
}

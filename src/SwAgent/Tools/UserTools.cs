using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>
    /// User-created tools: reusable procedures the user builds with the agent ("Add mounting holes",
    /// "Create compression mold", ...). Each tool is a folder in %APPDATA%\SwAgent\tools\{name}\ holding a TOOL.md
    /// (front matter + step-by-step instructions, verified code snippets, design rules) and optional reference
    /// files. Like a skill: the agent sees a one-line index of every tool and loads the full instructions
    /// with read_user_tool only when it needs them.
    /// </summary>
    public sealed class UserTools
    {
        public static readonly string[] Icons = { "macro", "part", "assembly", "drawing", "tree", "select", "camera", "rebuild", "gear", "book", "markup", "shield" };

        private static readonly Regex NamePattern = new Regex("^[a-z0-9][a-z0-9-]{1,47}$");

        public static string Root => Directory.CreateDirectory(Path.Combine(Paths.RoamingDir, "tools")).FullName;

        /// <summary>Raised after a tool is saved or deleted (the UI refreshes its palette and the agent's index).</summary>
        public static event Action Changed;

        public sealed class ToolFile
        {
            [ToolParam("Relative path inside the tool folder, e.g. \"reference/mold-base.cs\" or \"rules.md\".", Required = true)]
            public string Path { get; set; }

            [ToolParam("Full text content of the file.", Required = true)]
            public string Content { get; set; }
        }

        public sealed class NoInput
        {
        }

        public sealed class ReadInput
        {
            [ToolParam("Tool name (the id from the user tools index).", Required = true)]
            public string Name { get; set; }

            [ToolParam("Optional extra file inside the tool folder to read instead of TOOL.md.")]
            public string File { get; set; }
        }

        public sealed class SaveInput
        {
            [ToolParam("Id: lowercase letters, digits and dashes, e.g. \"add-mounting-holes\". Saving an existing id replaces that tool.", Required = true)]
            public string Name { get; set; }

            [ToolParam("Short title shown on the button / in the menu, e.g. \"Add mounting holes\".", Required = true)]
            public string Title { get; set; }

            [ToolParam("\"workflow\" (default): a larger, multi-step procedure run from the Workflows menu in the chat, e.g. building a whole mold. " +
                       "\"tool\": a small, fast command that extends manual modelling - a button on the Unify3D ribbon tab the user can put on a hotkey; " +
                       "give it a macro so it runs instantly.")]
            public string Kind { get; set; }

            [ToolParam("Tools only: the verified C# that does the whole job (same globals as execute_csharp: swApp, Model, Print, mm(), deg(), ...). " +
                       "It runs directly when the button is clicked - no AI round trip, one undo step - so it must read its inputs from the live " +
                       "selection (Model.SelectionManager) or use fixed defaults, never Ref(\"S1\"). For a wrong or missing selection call " +
                       "Fail(\"Select a planar face first\") (shown to the user in the status bar). End with a short result string, e.g. \"Added 4 holes\".")]
            public string Macro { get; set; }

            [ToolParam("Model for agent-run steps of this tool or workflow: claude-haiku-5-5 (fastest), claude-sonnet-5-5 (default for tools) or claude-opus-5-5. Omit for the default.")]
            public string Model { get; set; }

            [ToolParam("One or two sentences: what the tool does and what the user should select or provide before running it.", Required = true)]
            public string Description { get; set; }

            [ToolParam("Icon: macro, part, assembly, drawing, tree, select, camera, rebuild, gear, book, markup or shield (default macro).")]
            public string Icon { get; set; }

            [ToolParam("The full instructions in Markdown: inputs and how to get them (selection [S1], questions to ask), defaults, " +
                       "the procedure step by step with the exact API calls / C# snippets you verified, design rules and checks, " +
                       "and what to report. Write it so a future run can follow it without this conversation.", Required = true)]
            public string Instructions { get; set; }

            [ToolParam("Optional extra reference files saved alongside TOOL.md (templates, longer code, design tables).")]
            public ToolFile[] Files { get; set; }

            [ToolParam("An icon designed for this tool, as SVG markup: viewBox=\"0 0 16 16\", drawn to read clearly at 16-20 px next to SolidWorks' own " +
                       "commands - a simple pictogram of what the tool makes or does (e.g. a mold block with a cavity, a plate with holes), " +
                       "SolidWorks style: flat fills with subtle top-to-bottom gradients, 0.75-1 px darker outlines, steel greys plus one accent " +
                       "(blue #2F84D6, orange #E8912D, green #3AA655 or red #D94A38), no text, no thin details, transparent background. " +
                       "Shown on the tool's ribbon button and in the Tools menu.")]
            public string IconSvg { get; set; }
        }

        public sealed class Replacement
        {
            [ToolParam("Exact text currently in TOOL.md (must occur exactly once).", Required = true)]
            public string Find { get; set; }

            [ToolParam("Text to put in its place.", Required = true)]
            public string Replace { get; set; }
        }

        public sealed class EditInput
        {
            [ToolParam("Tool name (the id from the user tools index).", Required = true)]
            public string Name { get; set; }

            [ToolParam("Markdown to add to the end of the instructions, e.g. a new rule or note under its own heading.")]
            public string Append { get; set; }

            [ToolParam("Targeted changes to the existing instructions, applied in order.")]
            public Replacement[] Replacements { get; set; }

            [ToolParam("New one-line description (only if what the tool does changed).")]
            public string Description { get; set; }

            [ToolParam("New button title.")]
            public string Title { get; set; }

            [ToolParam("Tools only: the verified C# that does the whole job (same globals as execute_csharp: swApp, Model, Print, mm(), deg(), ...). " +
                       "It runs directly when the button is clicked - no AI round trip, one undo step - so it must read its inputs from the live " +
                       "selection (Model.SelectionManager) or use fixed defaults, never Ref(\"S1\"). For a wrong or missing selection call " +
                       "Fail(\"Select a planar face first\") (shown to the user in the status bar). End with a short result string, e.g. \"Added 4 holes\"." + " Replaces the tool's whole macro.")]
            public string Macro { get; set; }

            [ToolParam("An icon designed for this tool, as SVG markup: viewBox=\"0 0 16 16\", drawn to read clearly at 16-20 px next to SolidWorks' own " +
                       "commands - a simple pictogram of what the tool makes or does (e.g. a mold block with a cavity, a plate with holes), " +
                       "SolidWorks style: flat fills with subtle top-to-bottom gradients, 0.75-1 px darker outlines, steel greys plus one accent " +
                       "(blue #2F84D6, orange #E8912D, green #3AA655 or red #D94A38), no text, no thin details, transparent background. " +
                       "Shown on the tool's ribbon button and in the Tools menu.")]
            public string IconSvg { get; set; }
        }

        [AgentTool("list_user_tools", "List the user's saved workflows and tools with their descriptions.")]
        public ToolResult List(ToolContext ctx, NoInput input)
        {
            var tools = ListTools();
            return ToolResult.Ok(tools.Count == 0
                ? "The user has no saved tools yet."
                : string.Join("\n", tools.Select(t => $"- {t["name"]} ({t["kind"]}): {t["title"]} - {t["description"]}")));
        }

        [AgentTool("read_user_tool",
            "Load a workflow's or tool's full instructions (TOOL.md) and its file list, or one of its files (e.g. macro.cs). Read it before " +
            "running it, and before editing it.")]
        public ToolResult Read(ToolContext ctx, ReadInput input)
        {
            var dir = ToolDir(input.Name, mustExist: true);
            var target = string.IsNullOrWhiteSpace(input.File) ? Path.Combine(dir, "TOOL.md") : SafePath(dir, input.File);
            if (!File.Exists(target)) return ToolResult.Error($"No file '{input.File}' in tool '{input.Name}'.");
            var others = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(dir.Length + 1).Replace('\\', '/'))
                .Where(f => f != "TOOL.md")
                .ToList();
            var text = File.ReadAllText(target);
            if (others.Count > 0 && string.IsNullOrWhiteSpace(input.File)) text += "\n\n---\nExtra files in this tool: " + string.Join(", ", others);
            return ToolResult.Ok(text);
        }

        [AgentTool("save_user_tool",
            "Create or update one of the user's workflows or tools (kind). Only save when the user asked for one (or agreed to save one). " +
            "Before saving, settle with the user what the inputs are and what should be fixed vs. adjustable.")]
        public ToolResult Save(ToolContext ctx, SaveInput input)
        {
            var name = (input.Name ?? "").Trim().ToLowerInvariant();
            if (!NamePattern.IsMatch(name)) return ToolResult.Error("Name must be 2-48 characters: lowercase letters, digits and dashes.");
            var icon = Icons.Contains(input.Icon) ? input.Icon : "macro";
            var kind = string.Equals((input.Kind ?? "").Trim(), "tool", StringComparison.OrdinalIgnoreCase) ? "tool" : "workflow";
            var dir = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
            var md = Path.Combine(dir, "TOOL.md");
            var created = File.Exists(md) ? ReadFrontMatter(File.ReadAllText(md)).TryGetValue("created", out var c) ? c : Now() : Now();

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"name: {name}");
            sb.AppendLine($"title: {OneLine(input.Title)}");
            sb.AppendLine($"description: {OneLine(input.Description)}");
            sb.AppendLine($"kind: {kind}");
            sb.AppendLine($"icon: {icon}");
            if (!string.IsNullOrWhiteSpace(input.Model)) sb.AppendLine($"model: {OneLine(input.Model)}");
            sb.AppendLine($"created: {created}");
            sb.AppendLine($"updated: {Now()}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(input.Instructions.Trim()).AppendLine();
            File.WriteAllText(md, sb.ToString(), new UTF8Encoding(false));

            WriteIcon(dir, input.IconSvg, iconChanged: !string.IsNullOrWhiteSpace(input.IconSvg) || input.Icon != null);
            WriteMacro(dir, input.Macro);

            foreach (var f in input.Files ?? new ToolFile[0])
            {
                var path = SafePath(dir, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, f.Content ?? "", new UTF8Encoding(false));
            }
            Log.Info($"Saved user tool '{name}'");
            Changed?.Invoke();
            var hasIcon = File.Exists(Path.Combine(dir, "icon.svg"));
            if (kind == "tool")
            {
                var hasMacro = File.Exists(Path.Combine(dir, "macro.cs"));
                return ToolResult.Ok($"Saved tool '{name}' ({input.Title}). It is a button on the 'Unify3D' ribbon tab{(hasIcon ? " with its icon" : " (default icon - pass icon_svg)")}; " +
                                     "the user can assign it a hotkey (Tools > Customize > Keyboard, category Unify3D). " +
                                     (hasMacro ? "Clicking it runs the macro directly on the current selection." : "It has no macro yet, so each click asks the agent - add a verified macro to make it instant."));
            }
            return ToolResult.Ok($"Saved workflow '{name}' ({input.Title}). It is in the Workflows menu of the Unify3D chat panel{(hasIcon ? ", with its icon" : "")}.");
        }

        [AgentTool("edit_user_tool",
            "Make a targeted change to an existing workflow or tool - add a note or rule, replace specific passages, or replace a tool's macro - " +
            "without rewriting the rest. Prefer this over save_user_tool for small changes. Read it first.")]
        public ToolResult Edit(ToolContext ctx, EditInput input)
        {
            var dir = ToolDir(input.Name, mustExist: true);
            var md = Path.Combine(dir, "TOOL.md");
            if (!File.Exists(md)) throw new ToolException($"Tool '{input.Name}' has no TOOL.md.");
            if (string.IsNullOrWhiteSpace(input.Append) && (input.Replacements?.Length ?? 0) == 0 && string.IsNullOrWhiteSpace(input.Description) &&
                string.IsNullOrWhiteSpace(input.Title) && string.IsNullOrWhiteSpace(input.IconSvg) && string.IsNullOrWhiteSpace(input.Macro))
                throw new ToolException("Nothing to change: pass append, replacements, description, title or icon_svg.");
            if (!string.IsNullOrWhiteSpace(input.IconSvg)) WriteIcon(dir, input.IconSvg, iconChanged: true);
            WriteMacro(dir, input.Macro);

            var text = File.ReadAllText(md).Replace("\r\n", "\n");
            var lines = text.Split('\n');
            var end = lines.Length > 0 && lines[0].Trim() == "---" ? Array.FindIndex(lines, 1, l => l.Trim() == "---") : -1;
            var header = end > 0 ? lines.Take(end + 1).ToList() : new List<string>();
            var body = end > 0 ? string.Join("\n", lines.Skip(end + 1)) : text;

            foreach (var r in input.Replacements ?? new Replacement[0])
            {
                if (string.IsNullOrEmpty(r.Find)) throw new ToolException("A replacement has empty find text.");
                var find = r.Find.Replace("\r\n", "\n");
                var at = body.IndexOf(find, StringComparison.Ordinal);
                if (at < 0) throw new ToolException($"Text not found in the tool (nothing was changed): \"{Shorten(find)}\"");
                if (body.IndexOf(find, at + 1, StringComparison.Ordinal) >= 0) throw new ToolException($"Text occurs more than once; include more around it (nothing was changed): \"{Shorten(find)}\"");
                body = body.Substring(0, at) + (r.Replace ?? "").Replace("\r\n", "\n") + body.Substring(at + find.Length);
            }
            if (!string.IsNullOrWhiteSpace(input.Append)) body = body.TrimEnd() + "\n\n" + input.Append.Replace("\r\n", "\n").Trim() + "\n";

            if (header.Count > 0)
            {
                SetHeader(header, "updated", Now());
                if (!string.IsNullOrWhiteSpace(input.Description)) SetHeader(header, "description", OneLine(input.Description));
                if (!string.IsNullOrWhiteSpace(input.Title)) SetHeader(header, "title", OneLine(input.Title));
            }
            File.WriteAllText(md, (header.Count > 0 ? string.Join("\n", header) + "\n" : "") + body, new UTF8Encoding(false));
            Log.Info($"Edited user tool '{input.Name}'");
            Changed?.Invoke();
            return ToolResult.Ok($"Updated tool '{input.Name}' ({md}).");
        }

        /// <summary>Save (or replace) a tool's own icon. The PNG the ribbon uses is re-rendered by the panel.</summary>
        private static void WriteIcon(string dir, string svg, bool iconChanged)
        {
            if (!string.IsNullOrWhiteSpace(svg))
            {
                svg = svg.Trim();
                if (!svg.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || !svg.EndsWith("</svg>", StringComparison.OrdinalIgnoreCase))
                    throw new ToolException("icon_svg must be a single <svg ...>...</svg> element.");
                if (svg.Length > 20000) throw new ToolException("icon_svg is too large (keep it under 20,000 characters).");
                if (Regex.IsMatch(svg, @"<script|<foreignObject|\son\w+\s*=|(href|src)\s*=\s*[""'](?!#|data:image/)", RegexOptions.IgnoreCase))
                    throw new ToolException("icon_svg may only contain drawing elements (no scripts, event handlers or external links).");
                File.WriteAllText(Path.Combine(dir, "icon.svg"), svg, new UTF8Encoding(false));
            }
            if (iconChanged)
            {
                var png = Path.Combine(dir, "icon.png");
                if (File.Exists(png)) File.Delete(png);
            }
        }

        private static string ReadIconSvg(string dir)
        {
            var path = Path.Combine(dir, "icon.svg");
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteMacro(string dir, string macro)
        {
            if (string.IsNullOrWhiteSpace(macro)) return;
            File.WriteAllText(Path.Combine(dir, "macro.cs"), macro.Replace("\r\n", "\n").Trim() + "\n", new UTF8Encoding(false));
        }

        /// <summary>A tool's macro, or null if it has none.</summary>
        public static string ReadMacro(string name)
        {
            var path = Path.Combine(ToolDir(name, mustExist: true), "macro.cs");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>The panel rendered the tool's icon to PNG for the ribbon.</summary>
        public static void SetIconPng(string name, byte[] png)
        {
            var dir = ToolDir(name, mustExist: true);
            if (png == null || png.Length < 8 || png[0] != 0x89 || png[1] != (byte)'P') throw new ToolException("Not a PNG image.");
            File.WriteAllBytes(Path.Combine(dir, "icon.png"), png);
            Changed?.Invoke();
        }

        private static void SetHeader(List<string> header, string key, string value)
        {
            var i = header.FindIndex(1, l => l.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
            if (i > 0) header[i] = $"{key}: {value}";
            else header.Insert(header.Count - 1, $"{key}: {value}");
        }

        private static string Shorten(string s) => s.Length > 80 ? s.Substring(0, 80) + "..." : s;

        /// <summary>Index used by the UI (palette + the agent's system prompt).</summary>
        public static List<JObject> ListTools()
        {
            var result = new List<JObject>();
            foreach (var dir in Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var md = Path.Combine(dir, "TOOL.md");
                if (!File.Exists(md)) continue;
                try
                {
                    var fm = ReadFrontMatter(File.ReadAllText(md));
                    var name = Path.GetFileName(dir);
                    result.Add(new JObject
                    {
                        ["name"] = name,
                        ["title"] = fm.TryGetValue("title", out var t) ? t : name,
                        ["description"] = fm.TryGetValue("description", out var d) ? d : "",
                        ["icon"] = fm.TryGetValue("icon", out var i) && Icons.Contains(i) ? i : "macro",
                        ["kind"] = fm.TryGetValue("kind", out var k) && k == "tool" ? "tool" : "workflow",
                        ["hasMacro"] = File.Exists(Path.Combine(dir, "macro.cs")),
                        ["model"] = fm.TryGetValue("model", out var m) ? m : null,
                        ["updated"] = fm.TryGetValue("updated", out var u) ? u : File.GetLastWriteTimeUtc(md).ToString("o"),
                        ["iconSvg"] = ReadIconSvg(dir),
                        ["hasPng"] = File.Exists(Path.Combine(dir, "icon.png")),
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"Skipping unreadable tool {dir}: {ex.Message}");
                }
            }
            return result;
        }

        public static void Delete(string name)
        {
            var dir = ToolDir(name, mustExist: true);
            Directory.Delete(dir, recursive: true);
            Log.Info($"Deleted user tool '{name}'");
            Changed?.Invoke();
        }

        private static string ToolDir(string name, bool mustExist)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            if (!NamePattern.IsMatch(name)) throw new ToolException($"'{name}' is not a valid tool name.");
            var dir = Path.Combine(Root, name);
            if (mustExist && !Directory.Exists(dir)) throw new ToolException($"No tool named '{name}'. Use list_user_tools to see what exists.");
            return dir;
        }

        /// <summary>Resolve a relative path inside a tool folder; rejects anything that escapes it.</summary>
        private static string SafePath(string dir, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new ToolException("File paths must be relative to the tool folder.");
            var full = Path.GetFullPath(Path.Combine(dir, relative));
            if (!full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ToolException("File path escapes the tool folder.");
            return full;
        }

        private static Dictionary<string, string> ReadFrontMatter(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "---") return result;
            for (var i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0) result[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            return result;
        }

        private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

        private static string Now() => DateTime.UtcNow.ToString("o");
    }
}

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

        private const string CategoryHelp =
            "Optional category: tools that share one get their own box on the Unify3D ribbon tab and their own section in the Workflows menu, " +
            "e.g. \"Molding & Toolmaking\". Reuse an existing category name (see list_user_tools) rather than inventing a near-duplicate.";

        /// <summary>
        /// API steps per turn: the default, a long tool's default (front matter long_run without max_steps), and the range
        /// a tool's max_steps is clamped to (design 2.1.1; the panel's lib/toolRun.ts has the same numbers).
        /// </summary>
        public const int DefaultMaxSteps = 200, LongRunMaxSteps = 600, MaxStepsMin = 50, MaxStepsCap = 1000;

        /// <summary>A category as stored: one line, at most 48 characters; "" for none.</summary>
        public static string CleanCategory(string category)
        {
            var c = OneLine(category);
            return c.Length > 48 ? c.Substring(0, 48).TrimEnd() : c;
        }

        /// <summary>The tool's category from its (effective) front matter, or null when it has none.</summary>
        internal static string CategoryOf(IDictionary<string, string> fm) =>
            fm.TryGetValue("category", out var c) && !string.IsNullOrWhiteSpace(CleanCategory(c)) ? CleanCategory(c) : null;

        /// <summary>Front matter <c>long_run: true</c>: a long, phased tool (more steps per turn, a longer prompt cache, sticky run settings).</summary>
        internal static bool LongRunOf(IDictionary<string, string> fm) =>
            fm.TryGetValue("long_run", out var v) && new[] { "true", "yes", "1" }.Contains((v ?? "").Trim().ToLowerInvariant());

        /// <summary>
        /// API steps per turn for a tool run: <c>max_steps</c> (clamped to 50..1000), else 600 for a long tool, else null
        /// (the panel's default, 200). The one parser for this: ToolSpec should call it too.
        /// </summary>
        internal static int? MaxStepsOf(IDictionary<string, string> fm)
        {
            if (fm.TryGetValue("max_steps", out var v) && int.TryParse((v ?? "").Trim(), out var n)) return Math.Max(MaxStepsMin, Math.Min(MaxStepsCap, n));
            return LongRunOf(fm) ? LongRunMaxSteps : (int?)null;
        }

        /// <summary>Front matter <c>requires: a, b</c>: agent tools the tool needs from the add-in.</summary>
        internal static string[] RequiresOf(IDictionary<string, string> fm) =>
            fm.TryGetValue("requires", out var v) && v != null
                ? v.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToArray()
                : new string[0];

        /// <summary>
        /// Front matter "agent:" value for a run style: hardcoded -> false (macro only), agent -> true (the agent writes the
        /// code each run), hybrid -> hybrid (macro first; the agent takes over what it hands on, then improves it).
        /// Older names: instant = hardcoded, smart = agent, built / fallback = hybrid.
        /// </summary>
        private static string AgentValue(string runStyle, bool? agent, bool hasMacro)
        {
            switch ((runStyle ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace("-", ""))
            {
                case "hybrid":
                case "built":
                case "fallback":
                    return hasMacro ? "hybrid" : "true";
                case "hardcoded":
                case "instant":
                    return hasMacro ? "false" : "true";
                case "agent":
                case "agentintheloop":
                case "smart":
                    return "true";
            }
            return agent == false && hasMacro ? "false" : "true";
        }

        /// <summary>The user's own tools and workflows (%APPDATA%\SwAgent\tools): created and changed by the agent.</summary>
        public static string Root => Directory.CreateDirectory(Path.Combine(Paths.RoamingDir, "tools")).FullName;

        /// <summary>
        /// Built-in Unify3D tools: the same folder layout as the user's (TOOL.md, inputs.json, macro.cs, icons), shipped with
        /// the add-in in builtin-tools next to SwAgent.dll (source: tools/builtin in the repository). Read-only: they are
        /// replaced by every install, so the agent never edits them and their names can't be used for the user's tools.
        /// Per-PC settings for them (e.g. the intelligence level) go in the user's tools folder (builtin-settings.json).
        /// </summary>
        public static string BuiltInRoot => Path.Combine(Paths.AddInDir, "builtin-tools");

        private static string BuiltInSettingsPath => Path.Combine(Root, "builtin-settings.json");

        /// <summary>The folder of a tool: the user's, else a built-in one; null if there is none.</summary>
        public static string DirOf(string name)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            if (!NamePattern.IsMatch(name)) return null;
            var user = Path.Combine(Root, name);
            if (Directory.Exists(user) && File.Exists(Path.Combine(user, "TOOL.md"))) return user;
            var builtIn = Path.Combine(BuiltInRoot, name);
            return Directory.Exists(builtIn) && File.Exists(Path.Combine(builtIn, "TOOL.md")) ? builtIn : null;
        }

        /// <summary>A built-in Unify3D tool (shipped with the add-in, read-only).</summary>
        public static bool IsBuiltIn(string name)
        {
            var dir = DirOf(name);
            return dir != null && dir.StartsWith(BuiltInRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static bool BuiltInExists(string name) => File.Exists(Path.Combine(BuiltInRoot, name, "TOOL.md"));

        /// <summary>The user's settings for built-in tools: { name: { key: value } }.</summary>
        internal static Dictionary<string, string> BuiltInSettings(string name)
        {
            try
            {
                if (File.Exists(BuiltInSettingsPath) && JObject.Parse(File.ReadAllText(BuiltInSettingsPath))[name] is JObject o)
                    return o.Properties().ToDictionary(p => p.Name, p => (string)p.Value, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn("builtin-settings.json unreadable: " + ex.Message);
            }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Front matter of a tool with the user's settings applied (built-in tools keep those outside their folder).</summary>
        internal static Dictionary<string, string> EffectiveFrontMatter(string name, string dir)
        {
            var fm = ReadFrontMatter(File.ReadAllText(Path.Combine(dir, "TOOL.md")));
            if (dir.StartsWith(BuiltInRoot, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kv in BuiltInSettings(name)) fm[kv.Key] = kv.Value;
            }
            return fm;
        }

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

            [ToolParam("Workflows: model for runs, if it must differ from the chat's (claude-haiku-5-5, claude-sonnet-5-5, claude-opus-5-5). Tools use intelligence instead.")]
            public string Model { get; set; }

            [ToolParam("Tools: the inputs of its PropertyManager page (shown on the left like a native command). Omit for tools that just act on the selection.")]
            public ToolInputSpec[] Inputs { get; set; }

            [ToolParam("Tools: run style - hardcoded | agent | hybrid. hardcoded: the macro is the whole tool; a case it wasn't written for stops " +
                       "with its Fail message. agent: agent in the loop - the agent does the job each run from TOOL.md's skill, writing code from the " +
                       "context. hybrid: the macro runs first and does the job when it fits; for a case it can't handle it calls NeedsAgent(\"why\") " +
                       "and the agent takes over - adapts the code, finishes, then improves the macro so the tool learns.")]
            public string RunStyle { get; set; }

            [ToolParam("Older form of run_style: true = smart, false = instant. Prefer run_style.")]
            public bool? Agent { get; set; }

            [ToolParam("Tools with an agent: how much intelligence the job needs - fast (simple, mechanical), balanced (default), high (careful geometry work) or max (hardest reasoning). " +
                       "Maps to the user's provider (e.g. Claude Haiku / Sonnet / Opus); the user can change it in the tool's page.")]
            public string Intelligence { get; set; }

            [ToolParam("Tools: \"missing\" (default) skips the PropertyManager page when everything required is already selected and runs at once; " +
                       "\"always\" shows it every time (when the user normally adjusts values).")]
            public string Confirm { get; set; }

            [ToolParam("Tools with an agent: context captured silently for it - any of view (screenshot of the current view), selection, model " +
                       "(units, material, size), history (feature tree with parameters), history_full (also sketch geometry). Default view, selection, model, history. " +
                       "Prefer a little too much over too little.")]
            public string[] Context { get; set; }

            [ToolParam("Tools: C# that draws a live holographic preview while the page is open: build temporary bodies from Inputs " +
                       "(IModeler, IBody2.Copy2/Operations2...) and pass them to Preview(body). Runs on every input change, so it must be fast " +
                       "and must not change the model. Omit when a preview wouldn't be fast or useful.")]
            public string Preview { get; set; }

            [ToolParam("One or two sentences: what the tool does and what the user should select or provide before running it.", Required = true)]
            public string Description { get; set; }

            [ToolParam("Icon: macro, part, assembly, drawing, tree, select, camera, rebuild, gear, book, markup or shield (default macro).")]
            public string Icon { get; set; }

            [ToolParam(CategoryHelp)]
            public string Category { get; set; }

            [ToolParam("The full instructions in Markdown: inputs and how to get them (selection [S1], questions to ask), defaults, " +
                       "the procedure step by step with the exact API calls / C# snippets you verified, design rules and checks, " +
                       "and what to report. Write it so a future run can follow it without this conversation.", Required = true)]
            public string Instructions { get; set; }

            [ToolParam("Optional extra reference files saved alongside TOOL.md (templates, longer code, design tables).")]
            public ToolFile[] Files { get; set; }

            [ToolParam("An icon designed for this tool, as SVG markup: viewBox=\"0 0 16 16\", drawn to read clearly at 16-20 px next to SolidWorks' own " +
                       "commands - a simple pictogram of what the tool makes or does (e.g. a mold block with a cavity, a plate with holes), " +
                       "in the Unify3D look (like its brushed-metal U logo): brushed steel fills as top-to-bottom gradients (#F4F5F6 to #9AA0A6), " +
                       "graphite for dark parts and details (#55595F to #1C1F22), 0.75-1 px graphite outlines (#26292D), a small white highlight " +
                       "at most - monochrome metal, no blue or other colour accents, no text, no thin details, transparent background. " +
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

            [ToolParam(CategoryHelp + " An empty string removes the category.")]
            public string Category { get; set; }

            [ToolParam("Tools only: the verified C# that does the whole job (same globals as execute_csharp: swApp, Model, Print, mm(), deg(), ...). " +
                       "It runs directly when the button is clicked - no AI round trip, one undo step - so it must read its inputs from the live " +
                       "selection (Model.SelectionManager) or use fixed defaults, never Ref(\"S1\"). For a wrong or missing selection call " +
                       "Fail(\"Select a planar face first\") (shown to the user in the status bar). End with a short result string, e.g. \"Added 4 holes\"." + " Replaces the tool's whole macro.")]
            public string Macro { get; set; }

            [ToolParam("Tools: replace the inputs of its PropertyManager page (shown on the left like a native command). Omit for tools that just act on the selection.")]
            public ToolInputSpec[] Inputs { get; set; }

            [ToolParam("Tools: run style - hardcoded | agent | hybrid. hardcoded: the macro is the whole tool; a case it wasn't written for stops " +
                       "with its Fail message. agent: agent in the loop - the agent does the job each run from TOOL.md's skill, writing code from the " +
                       "context. hybrid: the macro runs first and does the job when it fits; for a case it can't handle it calls NeedsAgent(\"why\") " +
                       "and the agent takes over - adapts the code, finishes, then improves the macro so the tool learns.")]
            public string RunStyle { get; set; }

            [ToolParam("Older form of run_style: true = smart, false = instant. Prefer run_style.")]
            public bool? Agent { get; set; }

            [ToolParam("Tools with an agent: how much intelligence the job needs - fast (simple, mechanical), balanced (default), high (careful geometry work) or max (hardest reasoning). " +
                       "Maps to the user's provider (e.g. Claude Haiku / Sonnet / Opus); the user can change it in the tool's page.")]
            public string Intelligence { get; set; }

            [ToolParam("Tools: \"missing\" (default) skips the PropertyManager page when everything required is already selected and runs at once; " +
                       "\"always\" shows it every time (when the user normally adjusts values).")]
            public string Confirm { get; set; }

            [ToolParam("Tools with an agent: context captured silently for it - any of view (screenshot of the current view), selection, model " +
                       "(units, material, size), history (feature tree with parameters), history_full (also sketch geometry). Default view, selection, model, history. " +
                       "Prefer a little too much over too little.")]
            public string[] Context { get; set; }

            [ToolParam("Tools: C# that draws a live holographic preview while the page is open: build temporary bodies from Inputs " +
                       "(IModeler, IBody2.Copy2/Operations2...) and pass them to Preview(body). Runs on every input change, so it must be fast " +
                       "and must not change the model. Omit when a preview wouldn't be fast or useful.")]
            public string Preview { get; set; }

            [ToolParam("An icon designed for this tool, as SVG markup: viewBox=\"0 0 16 16\", drawn to read clearly at 16-20 px next to SolidWorks' own " +
                       "commands - a simple pictogram of what the tool makes or does (e.g. a mold block with a cavity, a plate with holes), " +
                       "in the Unify3D look (like its brushed-metal U logo): brushed steel fills as top-to-bottom gradients (#F4F5F6 to #9AA0A6), " +
                       "graphite for dark parts and details (#55595F to #1C1F22), 0.75-1 px graphite outlines (#26292D), a small white highlight " +
                       "at most - monochrome metal, no blue or other colour accents, no text, no thin details, transparent background. " +
                       "Shown on the tool's ribbon button and in the Tools menu.")]
            public string IconSvg { get; set; }
        }

        [AgentTool("list_user_tools", "List the user's saved workflows and tools with their descriptions.")]
        public ToolResult List(ToolContext ctx, NoInput input)
        {
            var tools = ListTools();
            return ToolResult.Ok(tools.Count == 0
                ? "The user has no saved tools yet."
                : string.Join("\n", tools.Select(t =>
                    $"- {t["name"]} ({((bool?)t["builtIn"] == true ? "built-in " : "")}{t["kind"]}{((string)t["category"] is string c ? ", " + c : "")}): {t["title"]} - {t["description"]}")));
        }

        [AgentTool("read_user_tool",
            "Load a workflow's or tool's full instructions (TOOL.md) and its file list, or one of its files (e.g. macro.cs). Read it before " +
            "running it, and before editing it.")]
        public ToolResult Read(ToolContext ctx, ReadInput input)
        {
            var dir = ToolDir(input.Name, mustExist: true, forWrite: false);
            var target = string.IsNullOrWhiteSpace(input.File) ? Path.Combine(dir, "TOOL.md") : SafePath(dir, input.File);
            if (!File.Exists(target)) return ToolResult.Error($"No file '{input.File}' in tool '{input.Name}'.");
            var others = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => f.Substring(dir.Length + 1).Replace('\\', '/'))
                .Where(f => f != "TOOL.md")
                .ToList();
            var text = File.ReadAllText(target);
            if (others.Count > 0 && string.IsNullOrWhiteSpace(input.File)) text += "\n\n---\nExtra files in this tool: " + string.Join(", ", others);
            if (IsBuiltIn(input.Name)) text += "\n\n(Built-in Unify3D tool: read-only. If the user wants it to work differently, save their own version under a new name.)";
            return ToolResult.Ok(text);
        }

        [AgentTool("save_user_tool",
            "Create or update one of the user's workflows or tools (kind). Only save when the user asked for one (or agreed to save one). " +
            "Before saving, settle with the user what the inputs are and what should be fixed vs. adjustable.")]
        public ToolResult Save(ToolContext ctx, SaveInput input)
        {
            var name = (input.Name ?? "").Trim().ToLowerInvariant();
            if (!NamePattern.IsMatch(name)) return ToolResult.Error("Name must be 2-48 characters: lowercase letters, digits and dashes.");
            if (BuiltInExists(name)) return ToolResult.Error($"'{name}' is a built-in Unify3D tool. Save the user's version under another name (e.g. \"{name}-custom\").");
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
            var category = CleanCategory(input.Category);
            if (category.Length > 0) sb.AppendLine($"category: {category}");
            if (!string.IsNullOrWhiteSpace(input.Model)) sb.AppendLine($"model: {OneLine(input.Model)}");
            if (kind == "tool")
            {
                sb.AppendLine($"agent: {AgentValue(input.RunStyle, input.Agent, !string.IsNullOrWhiteSpace(input.Macro))}");
                sb.AppendLine($"intelligence: {(ToolSpec.Tiers.Contains(input.Intelligence) ? input.Intelligence : "balanced")}");
                sb.AppendLine($"confirm: {(input.Confirm == "always" ? "always" : "missing")}");
                if (input.Context?.Length > 0) sb.AppendLine($"context: {string.Join(", ", input.Context.Select(c => c.Trim().ToLowerInvariant()))}");
            }
            sb.AppendLine($"created: {created}");
            sb.AppendLine($"updated: {Now()}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(input.Instructions.Trim()).AppendLine();
            File.WriteAllText(md, sb.ToString(), new UTF8Encoding(false));

            WriteIcon(dir, input.IconSvg, iconChanged: !string.IsNullOrWhiteSpace(input.IconSvg) || input.Icon != null);
            WriteMacro(dir, input.Macro);
            WritePreview(dir, input.Preview);
            ToolSpec.SaveInputs(dir, input.Inputs);

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
                string.IsNullOrWhiteSpace(input.Title) && string.IsNullOrWhiteSpace(input.IconSvg) && string.IsNullOrWhiteSpace(input.Macro) &&
                string.IsNullOrWhiteSpace(input.Preview) && input.Inputs == null && input.Agent == null && input.RunStyle == null && input.Intelligence == null &&
                input.Confirm == null && input.Context == null && input.Category == null)
                throw new ToolException("Nothing to change: pass append, replacements, description, title, category, icon_svg, macro, preview, inputs or a tool setting.");
            if (!string.IsNullOrWhiteSpace(input.IconSvg)) WriteIcon(dir, input.IconSvg, iconChanged: true);
            WriteMacro(dir, input.Macro);
            WritePreview(dir, input.Preview);
            if (input.Inputs != null) ToolSpec.SaveInputs(dir, input.Inputs);

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
                if (input.Category != null) SetHeader(header, "category", CleanCategory(input.Category));
                if (input.RunStyle != null || input.Agent != null) SetHeader(header, "agent", AgentValue(input.RunStyle, input.Agent, true));
                if (ToolSpec.Tiers.Contains(input.Intelligence)) SetHeader(header, "intelligence", input.Intelligence);
                if (input.Confirm == "always" || input.Confirm == "missing") SetHeader(header, "confirm", input.Confirm);
                if (input.Context?.Length > 0) SetHeader(header, "context", string.Join(", ", input.Context.Select(c => c.Trim().ToLowerInvariant())));
                // A new preview or macro replaces the one that crashed SolidWorks: un-pause it.
                var paused = header.FirstOrDefault(l => l.StartsWith("paused:", StringComparison.OrdinalIgnoreCase));
                if (paused != null)
                {
                    var left = paused.Substring(7).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(p => !(p == "preview" && !string.IsNullOrWhiteSpace(input.Preview)) && !(p == "macro" && !string.IsNullOrWhiteSpace(input.Macro)));
                    SetHeader(header, "paused", string.Join(", ", left));
                }
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

        private static void WritePreview(string dir, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            File.WriteAllText(Path.Combine(dir, "preview.cs"), code.Replace("\r\n", "\n").Trim() + "\n", new UTF8Encoding(false));
        }

        /// <summary>Front matter of a TOOL.md (public for ToolSpec).</summary>
        public static Dictionary<string, string> FrontMatter(string text) => ReadFrontMatter(text);

        /// <summary>
        /// Change one front-matter value of a saved tool (e.g. its intelligence, from the tool's page). For a built-in tool
        /// the value is the user's override in builtin-settings.json: null (reset), or the value the tool ships with,
        /// removes the override, so the shipped value - and any later change to it - applies again.
        /// </summary>
        public static void SetValue(string name, string key, string value)
        {
            var isCategory = string.Equals(key, "category", StringComparison.OrdinalIgnoreCase);
            if (IsBuiltIn(name))
            {
                var shipped = ReadFrontMatter(File.ReadAllText(Path.Combine(BuiltInRoot, name, "TOOL.md")));
                shipped.TryGetValue(key, out var original);
                var clean = value == null ? null : isCategory ? CleanCategory(value) : OneLine(value);
                var same = clean != null && (isCategory ? CleanCategory(original) == clean : original != null && OneLine(original) == clean);
                var all = File.Exists(BuiltInSettingsPath) ? JObject.Parse(File.ReadAllText(BuiltInSettingsPath)) : new JObject();
                var mine = all[name] as JObject ?? new JObject();
                if (clean == null || same) mine.Remove(key);
                else mine[key] = clean; // "" for a category: the user chose none
                if (mine.HasValues) all[name] = mine;
                else all.Remove(name);
                File.WriteAllText(BuiltInSettingsPath, all.ToString(Newtonsoft.Json.Formatting.Indented));
                Changed?.Invoke();
                return;
            }
            // Front matter is one "key: value" line each; a category from the panel is free text.
            value = isCategory ? CleanCategory(value) : OneLine(value);
            var md = Path.Combine(ToolDir(name, mustExist: true), "TOOL.md");
            var lines = File.ReadAllText(md).Replace("\r\n", "\n").Split('\n').ToList();
            var end = lines.Count > 0 && lines[0].Trim() == "---" ? lines.FindIndex(1, l => l.Trim() == "---") : -1;
            if (end < 0) return;
            var header = lines.Take(end + 1).ToList();
            SetHeader(header, key, value);
            File.WriteAllText(md, string.Join("\n", header.Concat(lines.Skip(end + 1))), new UTF8Encoding(false));
            Changed?.Invoke();
        }

        private static void WriteMacro(string dir, string macro)
        {
            if (string.IsNullOrWhiteSpace(macro)) return;
            File.WriteAllText(Path.Combine(dir, "macro.cs"), macro.Replace("\r\n", "\n").Trim() + "\n", new UTF8Encoding(false));
        }

        /// <summary>A tool's macro, or null if it has none.</summary>
        public static string ReadMacro(string name)
        {
            var path = Path.Combine(ToolDir(name, mustExist: true, forWrite: false), "macro.cs");
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
            if (string.IsNullOrEmpty(value) && string.Equals(key, "category", StringComparison.OrdinalIgnoreCase))
            {
                if (i > 0) header.RemoveAt(i); // no category: no empty line left behind
                return;
            }
            if (i > 0) header[i] = $"{key}: {value}";
            else header.Insert(header.Count - 1, $"{key}: {value}");
        }

        private static string Shorten(string s) => s.Length > 80 ? s.Substring(0, 80) + "..." : s;

        /// <summary>Index used by the UI (palette + the agent's system prompt).</summary>
        public static List<JObject> ListTools()
        {
            var result = new List<JObject>();
            var builtIn = Directory.Exists(BuiltInRoot) ? Directory.GetDirectories(BuiltInRoot) : new string[0];
            var dirs = builtIn.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .Concat(Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .Where(d => !BuiltInExists(Path.GetFileName(d)))); // a built-in name can't be a user tool
            foreach (var dir in dirs)
            {
                var md = Path.Combine(dir, "TOOL.md");
                if (!File.Exists(md)) continue;
                try
                {
                    var name = Path.GetFileName(dir);
                    var fm = EffectiveFrontMatter(name, dir);
                    result.Add(new JObject
                    {
                        ["name"] = name,
                        ["builtIn"] = dir.StartsWith(BuiltInRoot, StringComparison.OrdinalIgnoreCase),
                        ["title"] = fm.TryGetValue("title", out var t) ? t : name,
                        ["description"] = fm.TryGetValue("description", out var d) ? d : "",
                        ["icon"] = fm.TryGetValue("icon", out var i) && Icons.Contains(i) ? i : "macro",
                        ["kind"] = fm.TryGetValue("kind", out var k) && k == "tool" ? "tool" : "workflow",
                        ["hasMacro"] = File.Exists(Path.Combine(dir, "macro.cs")),
                        ["model"] = fm.TryGetValue("model", out var m) ? m : null,
                        ["agent"] = !fm.TryGetValue("agent", out var ag) || ag != "false" || !File.Exists(Path.Combine(dir, "macro.cs")),
                        ["runStyle"] = !File.Exists(Path.Combine(dir, "macro.cs")) ? "agent" : ag == "false" ? "hardcoded" : ag == "hybrid" || ag == "fallback" ? "hybrid" : "agent",
                        ["intelligence"] = fm.TryGetValue("intelligence", out var tier) && ToolSpec.Tiers.Contains(tier) ? tier : "balanced",
                        ["hasInputs"] = File.Exists(Path.Combine(dir, "inputs.json")),
                        ["hasPreview"] = File.Exists(Path.Combine(dir, "preview.cs")),
                        ["paused"] = fm.TryGetValue("paused", out var pz) ? pz : "",
                        ["updated"] = fm.TryGetValue("updated", out var u) ? u : File.GetLastWriteTimeUtc(md).ToString("o"),
                        ["iconSvg"] = ReadIconSvg(dir),
                        ["hasPng"] = File.Exists(Path.Combine(dir, "icon.png")),
                        // Grouping on the ribbon and in the Workflows menu, the ribbon's shorter button title, and long runs.
                        ["category"] = CategoryOf(fm),
                        // A built-in's shipped category, so the panel can offer "Reset to default" after the user changed it.
                        ["categoryDefault"] = dir.StartsWith(BuiltInRoot, StringComparison.OrdinalIgnoreCase)
                            ? CategoryOf(ReadFrontMatter(File.ReadAllText(md)))
                            : null,
                        ["ribbonTitle"] = fm.TryGetValue("ribbon_title", out var rt) && !string.IsNullOrWhiteSpace(rt) ? OneLine(rt) : null,
                        ["longRun"] = LongRunOf(fm),
                        ["maxSteps"] = MaxStepsOf(fm),
                        ["requires"] = new JArray(RequiresOf(fm)),
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

        private static string ToolDir(string name, bool mustExist, bool forWrite = true)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            if (!NamePattern.IsMatch(name)) throw new ToolException($"'{name}' is not a valid tool name.");
            if (IsBuiltIn(name))
            {
                if (forWrite)
                    throw new ToolException($"'{name}' is a built-in Unify3D tool and can't be changed or deleted. If the user wants it to work differently, " +
                                            "save their own version under a new name with save_user_tool.");
                return DirOf(name);
            }
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

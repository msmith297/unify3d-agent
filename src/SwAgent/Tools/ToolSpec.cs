using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>
    /// One input of a tool, shown as a native control in its PropertyManager page. Saved in the tool's inputs.json.
    /// </summary>
    public sealed class ToolInputSpec
    {
        [ToolParam("Identifier the macro / preview / agent use, e.g. \"faces\", \"offset\".", Required = true)]
        public string Id { get; set; }

        [ToolParam("selection | number | integer | checkbox | choice (dropdown) | options (radio buttons) | text (a note the user types or dictates) | " +
                   "files (images, PDFs or 3D models the user adds).", Required = true)]
        public string Type { get; set; }

        [ToolParam("Caption shown above the control, e.g. \"Faces to offset\".", Required = true)]
        public string Label { get; set; }

        [ToolParam("Tooltip with a short explanation.")]
        public string Tip { get; set; }

        [ToolParam("The tool can't run without it (default true for selection, false otherwise).")]
        public bool? Required { get; set; }

        [ToolParam("Default value: a number in the input's unit (mm or degrees), true/false, or the default choice.")]
        public string Default { get; set; }

        [ToolParam("number/integer: minimum, in the input's unit.")]
        public double? Min { get; set; }

        [ToolParam("number/integer: maximum, in the input's unit.")]
        public double? Max { get; set; }

        [ToolParam("number: length (entered in the document's units, default value in mm) | angle (degrees) | none | percent.")]
        public string Unit { get; set; }

        [ToolParam("selection: what can be picked - any of face, edge, vertex, feature, body, component, plane, sketch, sketch_segment, point.")]
        public string[] Filter { get; set; }

        [ToolParam("selection: allow several entities (default true). files: allow several files (default true).")]
        public bool? Multiple { get; set; }

        [ToolParam("choice / options: the entries.")]
        public string[] Choices { get; set; }

        [ToolParam("files: what may be added - any of image, pdf, model (STL, STEP, 3MF, IGES, SLDPRT...).")]
        public string[] Accept { get; set; }

        [ToolParam("Native SolidWorks picture next to the control: distance, depth, width, thickness, radius, diameter, angle, face, edge, vertex, " +
                   "face_edge, component, profile.")]
        public string Icon { get; set; }

        [ToolParam("Group box caption to put this input in (inputs with the same group share a box). Default: Selections / Parameters.")]
        public string Group { get; set; }
    }

    /// <summary>A saved tool as the runner sees it: TOOL.md front matter + inputs.json + macro.cs + preview.cs.</summary>
    internal sealed class ToolSpec
    {
        public string Name;
        public string Title;
        public string Description;
        public string Kind;

        /// <summary>An agent finishes the job (otherwise the macro is the whole tool).</summary>
        public bool Agent;

        /// <summary>
        /// "agent: fallback" (run style Built): the macro does the whole job and checks it; the agent only steps in when
        /// the macro fails or calls NeedsAgent(...). Agent is true too, so the agent is available for that.
        /// </summary>
        public bool AgentFallback;

        /// <summary>fast | balanced | high | max: how much intelligence the agent part needs.</summary>
        public string Intelligence;

        /// <summary>"missing": skip the PropertyManager page when everything required is already selected. "always": always show it.</summary>
        public string Confirm;

        /// <summary>Context captured for the agent: view, selection, model, history, history_full.</summary>
        public HashSet<string> Context;

        public List<ToolInputSpec> Inputs;

        /// <summary>Parts paused after SolidWorks closed while running them ("preview", "macro") - see CrashGuard.</summary>
        public HashSet<string> Paused;
        public string Macro;
        public string Preview;

        public static readonly string[] Tiers = { "fast", "balanced", "high", "max" };

        public static ToolSpec Load(string name)
        {
            var dir = Path.Combine(UserTools.Root, name);
            var md = Path.Combine(dir, "TOOL.md");
            if (!File.Exists(md)) return null;
            var fm = UserTools.FrontMatter(File.ReadAllText(md));
            string Get(string key, string fallback = null) => fm.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

            var spec = new ToolSpec
            {
                Name = name,
                Title = Get("title", name),
                Description = Get("description", ""),
                Kind = Get("kind", "workflow"),
                Macro = ReadOptional(Path.Combine(dir, "macro.cs")),
                Preview = ReadOptional(Path.Combine(dir, "preview.cs")),
                Inputs = LoadInputs(dir),
            };
            // A tool without a macro can only work through the agent.
            spec.Agent = spec.Macro == null || !string.Equals(Get("agent", "true"), "false", StringComparison.OrdinalIgnoreCase);
            spec.AgentFallback = spec.Macro != null && string.Equals(Get("agent", "true"), "fallback", StringComparison.OrdinalIgnoreCase);
            spec.Intelligence = Tiers.Contains(Get("intelligence")) ? Get("intelligence") : "balanced";
            spec.Confirm = Get("confirm", "missing") == "always" ? "always" : "missing";
            spec.Paused = new HashSet<string>((Get("paused", "") ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            spec.Context = new HashSet<string>((Get("context", "view,selection,model,history") ?? "")
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()));
            return spec;
        }

        public static List<ToolInputSpec> LoadInputs(string dir)
        {
            var path = Path.Combine(dir, "inputs.json");
            if (!File.Exists(path)) return new List<ToolInputSpec>();
            try
            {
                return JsonConvert.DeserializeObject<List<ToolInputSpec>>(File.ReadAllText(path)) ?? new List<ToolInputSpec>();
            }
            catch (Exception ex)
            {
                Log.Warn($"Bad inputs.json in {dir}: {ex.Message}");
                return new List<ToolInputSpec>();
            }
        }

        public static void SaveInputs(string dir, IEnumerable<ToolInputSpec> inputs)
        {
            var list = (inputs ?? Enumerable.Empty<ToolInputSpec>()).Where(i => !string.IsNullOrWhiteSpace(i?.Id)).ToList();
            var path = Path.Combine(dir, "inputs.json");
            if (list.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            File.WriteAllText(path, JsonConvert.SerializeObject(list, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        }

        public static bool IsRequired(ToolInputSpec i) => i.Required ?? Type(i) == "selection";

        public static string Type(ToolInputSpec i) => (i.Type ?? "").Trim().ToLowerInvariant();

        private static string ReadOptional(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    }
}

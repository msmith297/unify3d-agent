using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwAgent.Tools
{
    /// <summary>
    /// The agent's side of tool profiles: list_tool_profiles and save_tool_profile only read and write profile files
    /// (SwBusyGate.ModelFreeTools); run_tool starts a tool as the ribbon does, its inputs loaded from a profile.
    /// </summary>
    public sealed class ProfileTools
    {
        public sealed class ListInput
        {
            [ToolParam("The tool's name. Omit to list the profiles of every tool that has some.")]
            public string Tool { get; set; }
        }

        [AgentTool("list_tool_profiles", "The saved profiles (sets of input values) of a tool: names, which is the default, which come with the tool, and their values.")]
        public ToolResult List(ToolContext ctx, ListInput input)
        {
            var tool = input?.Tool?.Trim();
            if (string.IsNullOrEmpty(tool))
            {
                var lines = UserTools.ListTools()
                    .Select(t => (Name: (string)t["name"], Names: (t["profiles"] as JArray)?.Select(x => (string)x).ToList()))
                    .Where(t => t.Names != null && t.Names.Count > 0)
                    .Select(t => $"- {t.Name}: {string.Join(", ", t.Names)}" + (ToolProfiles.DefaultName(t.Name) is string d ? $" (default: {d})" : ""))
                    .ToList();
                return ToolResult.Ok(lines.Count == 0 ? "No tool has profiles yet." : string.Join("\n", lines));
            }
            if (UserTools.DirOf(tool) == null) return ToolResult.Error($"There is no tool '{tool}'. list_user_tools shows them.");
            var list = ToolProfiles.List(tool);
            if (list.Count == 0) return ToolResult.Ok($"{tool} has no profiles yet. save_tool_profile makes one.");
            var def = ToolProfiles.DefaultName(tool);
            var last = ToolProfiles.LastUsed(tool);
            var sb = new StringBuilder();
            foreach (var p in list)
            {
                var tags = new[]
                {
                    ToolProfiles.SameName(p.Name, def) ? "default" : null,
                    p.Locked ? "comes with the tool, locked" : null,
                    ToolProfiles.SameName(p.Name, last) ? "last used" : null,
                }.Where(x => x != null).ToList();
                var values = string.Join(", ", p.Values.Properties().Select(v => $"{v.Name}={(v.Value.Type == JTokenType.String ? (string)v.Value : v.Value.ToString(Formatting.None))}"));
                if (values.Length > 600) values = values.Substring(0, 600) + "...";
                sb.AppendLine($"- {p.Name}{(tags.Count > 0 ? " (" + string.Join(", ", tags) + ")" : "")}: {values}");
            }
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        public sealed class SaveInput
        {
            [ToolParam("The tool's name.", Required = true)]
            public string Tool { get; set; }

            [ToolParam("The profile's name, e.g. \"Shop A - PU on the 50 t press\". An existing profile of the user's with this name is updated.", Required = true)]
            public string Name { get; set; }

            [ToolParam("Input id -> value, only the inputs to set: numbers with their unit (\"2000 psi\", \"300 mm\", \"1.5 %\"), integers " +
                       "(\"10000\"), sizes (\"600 x 400 x 150 mm\"), checkboxes true/false, choices their entry or data row id, Optional inputs " +
                       "{\"on\": false} or {\"on\": true, \"value\": \"23 kg\"}. Merged over from_last_run's values.")]
            public JObject Values { get; set; }

            [ToolParam("Start from the values of the tool's latest run in this session (\"save these as my shop defaults\").")]
            public bool? FromLastRun { get; set; }

            [ToolParam("true: the tool opens with this profile from now on; false: it no longer does. With only name and make_default, an " +
                       "existing profile becomes (or stops being) the default.")]
            public bool? MakeDefault { get; set; }
        }

        [AgentTool("save_tool_profile",
            "Save a profile of a tool's input values (the user loads it at the top of the tool's form), or make a profile the default. Values " +
            "are checked against the tool's inputs: nothing is saved while one is wrong.")]
        public ToolResult Save(ToolContext ctx, SaveInput input)
        {
            var tool = (input.Tool ?? "").Trim();
            var spec = ToolSpec.Load(tool);
            if (spec == null) return ToolResult.Error($"There is no tool '{tool}'. list_user_tools shows them.");
            if (spec.Inputs.Count == 0) return ToolResult.Error($"{tool} has no inputs, so it has no profiles.");
            var existing = ToolProfiles.Find(tool, input.Name);
            var onlyDefault = input.Values == null && input.FromLastRun != true && input.MakeDefault != null;
            if (onlyDefault)
            {
                if (existing == null) return ToolResult.Error($"There is no profile '{input.Name}' for {tool}. Give values (or from_last_run) to make it.");
                return ToolResult.Ok(SetDefault(tool, existing.Name, input.MakeDefault == true));
            }

            var values = new JObject();
            if (input.FromLastRun == true)
            {
                if (!ToolProfiles.LastRun.TryGetValue(tool, out var last))
                    return ToolResult.Error($"{tool} hasn't run in this session, so there are no last values. Pass values instead.");
                values = (JObject)last.DeepClone();
            }
            foreach (var p in (input.Values ?? new JObject()).Properties()) values[p.Name] = p.Value.DeepClone();
            if (values.Count == 0) return ToolResult.Error("Nothing to save: pass values or from_last_run.");

            // Checked as loading would check them; a data choice is stored by its row id.
            var form = new ToolForm(spec, ToolData.For(spec), imperial: false);
            var check = ProfileValues.Apply(form, values);
            if (check.Warnings.Count > 0) return ToolResult.Error("Not saved:\n" + string.Join("\n", check.Warnings.Select(w => "- " + w)));
            var canonical = ProfileValues.Capture(form, ProfileValues.Mode.All);
            foreach (var f in form.Fields.Where(f => (f.Type == "choice" || f.Type == "options") && values[f.Id] != null && canonical[f.Id] != null))
                values[f.Id] = canonical[f.Id];

            if (existing != null && existing.Locked)
                return ToolResult.Error($"'{existing.Name}' comes with the tool and can't be changed. Save under another name.");
            var version = UserTools.FrontValue(UserTools.EffectiveFrontMatter(tool, spec.Dir), "updated");
            var saved = existing != null
                ? ToolProfiles.Update(tool, existing.Name, values, version)
                : ToolProfiles.Save(tool, input.Name, values, version);
            var text = $"{(existing != null ? "Updated" : "Saved")} the profile '{saved.Name}' of {tool} ({saved.Values.Count} inputs).";
            if (input.MakeDefault != null) text += " " + SetDefault(tool, saved.Name, input.MakeDefault == true);
            return ToolResult.Ok(text);
        }

        private static string SetDefault(string tool, string name, bool on)
        {
            if (on)
            {
                ToolProfiles.SetDefault(tool, name);
                return $"{tool} opens with '{name}' from now on.";
            }
            if (ToolProfiles.SameName(ToolProfiles.DefaultName(tool), name)) ToolProfiles.SetDefault(tool, null);
            return $"'{name}' is not the default of {tool}.";
        }

        public sealed class RunInput
        {
            [ToolParam("The tool's name (kind tool).", Required = true)]
            public string Tool { get; set; }

            [ToolParam("A profile to load its inputs from. Omit for the default profile (or the usual values).")]
            public string Profile { get; set; }
        }

        [AgentTool("run_tool",
            "Run one of the user's tools as its ribbon button does, with its inputs loaded from a profile: the inputs open for the user to check and " +
            "press OK (a tool whose selection is already complete runs at once). The run comes back to this chat as a new message: end your turn.")]
        public ToolResult Run(ToolContext ctx, RunInput input)
        {
            var tool = (input.Tool ?? "").Trim();
            var info = UserTools.ListTools().FirstOrDefault(t => string.Equals((string)t["name"], tool, StringComparison.OrdinalIgnoreCase));
            if (info == null) return ToolResult.Error($"There is no tool '{tool}'. list_user_tools shows them.");
            if ((string)info["kind"] != "tool") return ToolResult.Error($"'{tool}' is a workflow: read_user_tool and follow it instead.");
            var profile = input.Profile?.Trim();
            if (!string.IsNullOrEmpty(profile) && ToolProfiles.Find(tool, profile) == null)
            {
                var names = ToolProfiles.Names(tool);
                return ToolResult.Error($"There is no profile '{profile}' for {tool}." + (names.Count == 0 ? " It has none." : $" Its profiles: {string.Join(", ", names)}."));
            }
            ProfilesBridge.Run((string)info["name"], profile);
            return ToolResult.Ok($"Started {(string)info["title"]}" + (string.IsNullOrEmpty(profile) ? "" : $" with the profile '{profile}'") +
                                 ". Its inputs open for the user; the run comes back as a new message. End your turn now.");
        }
    }
}

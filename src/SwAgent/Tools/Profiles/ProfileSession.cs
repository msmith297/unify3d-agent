using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using SwAgent.Scripting;

namespace SwAgent.Tools
{
    /// <summary>
    /// The profile side of one open tool form (the panel's and the PropertyManager page's): which profile is loaded, what
    /// it set, what the user changed since, its warnings, and load / save / update on the form. Loading puts the tool
    /// defaults back first, then the profile's values over them. No SolidWorks types: tested headless.
    /// </summary>
    internal sealed class ProfileSession
    {
        public readonly ToolSpec Spec;
        public readonly bool Imperial;
        private readonly string _root;

        /// <summary>The profile loaded (null: tool defaults, or the values remembered from the last run).</summary>
        public ToolProfile Loaded { get; private set; }

        /// <summary>What loading the profile ignored or kept at the default (an outdated profile); cleared by the next profile action.</summary>
        public List<string> Warnings { get; private set; } = new List<string>();

        /// <summary>One line about the last profile action ("Loaded your default profile: Shop A").</summary>
        public string Note;

        /// <summary>Why the last profile action failed (a name in use, a locked profile); cleared by the next one.</summary>
        public string Error;

        /// <summary>The panel's files and picked entities; null on the page (it keeps them in its own controls).</summary>
        public DocumentInputs Document;

        private HashSet<string> _set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private JObject _baseline;
        private JObject _defaults;

        public ProfileSession(ToolSpec spec, bool imperial, string root = null)
        {
            Spec = spec;
            Imperial = imperial;
            _root = root;
        }

        private string Tool => Spec.Name;
        private string ToolDir => Spec.Dir;

        /// <summary>
        /// The form a tool opens with: the profile asked for (/run --profile, run_tool), else the default profile, over the tool
        /// defaults - keeping only the unit choices remembered on this PC; without one, today's form (defaults plus the values
        /// remembered from the last run). A profile that can't be read never stops the form. documentInputs: the form keeps
        /// files and selections (the panel) - a profile's are held until Attach hands over the form's own.
        /// </summary>
        public static ToolForm OpenForm(ToolSpec spec, bool imperial, string requested, out ProfileSession profiles, string rememberDir = null, string root = null,
            bool documentInputs = false)
        {
            profiles = new ProfileSession(spec, imperial, root);
            if (documentInputs) profiles.Document = profiles.Pending();
            string name = null;
            ToolProfile profile = null;
            try
            {
                name = string.IsNullOrWhiteSpace(requested) ? ToolProfiles.DefaultName(spec.Name, root) : requested.Trim();
                profile = name == null ? null : ToolProfiles.Find(spec.Name, name, root, spec.Dir);
            }
            catch (Exception ex)
            {
                Log.Warn($"Tool '{spec.Name}': reading its profiles failed: {ex.Message}");
            }
            if (profile == null)
            {
                if (name != null)
                    profiles.Note = requested != null
                        ? $"There is no profile '{name}' for this tool, so it opened as usual."
                        : $"Your default profile '{name}' no longer exists, so the tool opened as usual.";
                return ToolFormSession.CreateForm(spec, imperial, rememberDir);
            }
            ToolForm form;
            try
            {
                form = new ToolForm(spec, ToolData.For(spec), imperial, UnitsOnly(RememberStore.Load(spec.Name, rememberDir)));
            }
            catch (Exception ex)
            {
                Log.Error($"Tool '{spec.Name}': reading its page data failed", ex);
                return ToolFormSession.CreateForm(spec, imperial, rememberDir);
            }
            profiles.Apply(form, profile);
            profiles.Note = requested != null ? $"Loaded the profile {profile.Name}." : $"Loaded your default profile: {profile.Name}.";
            return form;
        }

        private Dictionary<string, List<string>> _pendingFiles;
        private Dictionary<string, List<ToolInputs.SelectedItem>> _pendingSelections;

        /// <summary>Holds a profile's files and selections until the form that keeps them exists.</summary>
        private DocumentInputs Pending()
        {
            _pendingFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _pendingSelections = new Dictionary<string, List<ToolInputs.SelectedItem>>(StringComparer.OrdinalIgnoreCase);
            return new DocumentInputs
            {
                Files = id => _pendingFiles.TryGetValue(id, out var l) ? l : new List<string>(),
                SetFiles = (id, l) => _pendingFiles[id] = l,
                Selected = id => _pendingSelections.TryGetValue(id, out var l) ? l : new List<ToolInputs.SelectedItem>(),
                SetSelection = (id, l) => _pendingSelections[id] = l,
            };
        }

        /// <summary>The form's own files and selections (the panel's session), taking what the opening profile held for them.</summary>
        public void Attach(DocumentInputs document)
        {
            Document = document;
            if (document == null || _pendingFiles == null) return;
            foreach (var kv in _pendingFiles) document.SetFiles(kv.Key, kv.Value);
            foreach (var kv in _pendingSelections) document.SetSelection(kv.Key, kv.Value);
            _pendingFiles = null;
            _pendingSelections = null;
        }

        /// <summary>Only the remembered unit choices ({ id: { unit } }): a profile brings its own values.</summary>
        private static JObject UnitsOnly(JObject remembered)
        {
            if (remembered == null) return null;
            var result = new JObject();
            foreach (var p in remembered.Properties())
                if (p.Value is JObject o && o["unit"] is JValue unit) result[p.Name] = new JObject { ["unit"] = unit.DeepClone() };
            return result;
        }

        /// <summary>The tool defaults as values (cached for the form's life).</summary>
        private JObject Defaults()
        {
            if (_defaults != null) return _defaults;
            try
            {
                _defaults = ProfileValues.Capture(new ToolForm(Spec, ToolData.For(Spec), Imperial), ProfileValues.Mode.All);
            }
            catch (Exception ex)
            {
                Log.Warn($"Tool '{Tool}': reading its defaults failed: {ex.Message}");
                _defaults = new JObject();
            }
            return _defaults;
        }

        private void Apply(ToolForm form, ToolProfile profile)
        {
            var result = ProfileValues.Apply(form, profile.Values, Document);
            Loaded = profile;
            _set = result.Applied;
            Warnings = result.Warnings;
            _baseline = ProfileValues.Capture(form, ProfileValues.Mode.All, Document);
        }

        // ---- actions ----

        /// <summary>Load a profile into the form (null or "(Tool defaults)": back to the tool defaults). Files and selections stay unless the profile has them.</summary>
        public void Load(ToolForm form, string name)
        {
            ToolProfile profile = null;
            if (!string.IsNullOrWhiteSpace(name) && !ToolProfiles.SameName(name, ToolProfiles.ToolDefaults))
                profile = ToolProfiles.Find(Tool, name, _root, ToolDir) ?? throw new ToolException($"There is no profile '{name}' for this tool.");
            ProfileValues.Apply(form, Defaults(), keepUnits: true);
            Warnings = new List<string>();
            if (profile == null)
            {
                Loaded = null;
                _set.Clear();
                _baseline = null;
                Note = "Back to the tool defaults.";
                return;
            }
            Apply(form, profile);
            Note = Warnings.Count == 0 ? $"Loaded the profile {profile.Name}." : $"Loaded the profile {profile.Name}, with changes:";
        }

        /// <summary>
        /// Save the form as a new profile. onlyChanged keeps the inputs that differ from the tool defaults; includeDocument adds
        /// the files and picked entities (a template of one job). The new profile counts as loaded.
        /// </summary>
        public ToolProfile Save(ToolForm form, string name, bool makeDefault = false, bool includeDocument = false, bool onlyChanged = false)
        {
            var doc = includeDocument ? Document : null;
            var values = ProfileValues.Capture(form, ProfileValues.Mode.Profile, doc);
            if (onlyChanged)
            {
                var defaults = Defaults();
                foreach (var f in form.Fields.Where(f => !ProfileValues.IsDocumentInput(f) && values[f.Id] != null && ProfileValues.Same(f, values[f.Id], defaults[f.Id])))
                    values.Remove(f.Id);
                if (values.Count == 0) throw new ToolException("Nothing to save: every input is at the tool default.");
            }
            var saved = ToolProfiles.Save(Tool, name, values, ToolVersion(), root: _root, toolDir: ToolDir);
            if (makeDefault) ToolProfiles.SetDefault(Tool, saved.Name, _root, ToolDir);
            Took(form, saved);
            Note = $"Saved the profile {saved.Name}" + (makeDefault ? " as your default." : ".");
            return saved;
        }

        /// <summary>Update the loaded profile with the form: the inputs it had, plus the ones changed since it was loaded.</summary>
        public ToolProfile Update(ToolForm form)
        {
            if (Loaded == null) throw new ToolException("No profile is loaded. Save a new one instead.");
            if (Loaded.Locked) throw new ToolException($"'{Loaded.Name}' comes with the tool and can't be changed. Save it under a new name.");
            var hadDocument = form.Fields.Any(f => ProfileValues.IsDocumentInput(f) && Loaded.Values[f.Id] != null);
            var doc = hadDocument ? Document : null;
            var all = ProfileValues.Capture(form, ProfileValues.Mode.All, doc);
            var mine = ProfileValues.Capture(form, ProfileValues.Mode.Profile, doc);
            var modified = new HashSet<string>(Modified(form), StringComparer.OrdinalIgnoreCase);
            var values = new JObject();
            foreach (var f in form.Fields)
            {
                if (values[f.Id] != null) continue;
                // An input the profile had keeps being saved; one changed since loading joins it unless it follows the table.
                var v = Loaded.Values[f.Id] != null ? all[f.Id] : modified.Contains(f.Id) ? mine[f.Id] : null;
                if (v != null) values[f.Id] = v;
            }
            var saved = ToolProfiles.Update(Tool, Loaded.Name, values, ToolVersion(), _root, ToolDir);
            Took(form, saved);
            Note = $"Updated the profile {saved.Name}.";
            return saved;
        }

        /// <summary>A profile just saved from the form is the loaded one, with nothing modified.</summary>
        private void Took(ToolForm form, ToolProfile saved)
        {
            Loaded = saved;
            _set = new HashSet<string>(saved.Values.Properties().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            Warnings = new List<string>();
            _baseline = ProfileValues.Capture(form, ProfileValues.Mode.All, Document);
        }

        /// <summary>
        /// One action of the Profile row: { action: "load", name } (null: the tool defaults), { action: "save", name,
        /// makeDefault, includeDocument, onlyChanged }, { action: "update" }, { action: "setDefault", name } (null: none),
        /// { action: "rename", name, newName }, { action: "duplicate", name, newName? }, { action: "delete", name },
        /// { action: "import", content } (a profile file's text: added, then loaded). ToolException carries the user's message.
        /// </summary>
        public void Act(ToolForm form, JObject p)
        {
            Error = null;
            var name = (string)p?["name"];
            switch ((string)p?["action"])
            {
                case "load":
                    Load(form, name);
                    break;
                case "save":
                    Save(form, name, (bool?)p["makeDefault"] == true, (bool?)p["includeDocument"] == true, (bool?)p["onlyChanged"] == true);
                    break;
                case "update":
                    Update(form);
                    break;
                case "setDefault":
                    ToolProfiles.SetDefault(Tool, name, _root, ToolDir);
                    Note = string.IsNullOrWhiteSpace(name) ? "The tool opens without a profile now." : $"The tool opens with {name} from now on.";
                    break;
                case "rename":
                {
                    var renamed = ToolProfiles.Rename(Tool, name, (string)p["newName"], _root, ToolDir);
                    if (Loaded != null && ToolProfiles.SameName(Loaded.Name, name)) Loaded = renamed;
                    Note = $"Renamed to {renamed.Name}.";
                    break;
                }
                case "duplicate":
                {
                    var copy = ToolProfiles.Duplicate(Tool, name, (string)p["newName"], _root, ToolDir);
                    Note = $"Saved a copy as {copy.Name}.";
                    break;
                }
                case "delete":
                    ToolProfiles.Delete(Tool, name, _root, ToolDir);
                    if (Loaded != null && ToolProfiles.SameName(Loaded.Name, name))
                    {
                        Loaded = null;
                        _set.Clear();
                        _baseline = null;
                    }
                    Note = $"Deleted the profile {name}.";
                    break;
                case "import":
                {
                    var imported = ToolProfiles.Import(Tool, (string)p["content"], out var note, _root, ToolDir);
                    Load(form, imported.Name);
                    Note = $"Added the profile {imported.Name} and loaded it." + (note == null ? "" : " " + note);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown profile action '{(string)p?["action"]}'.");
            }
        }

        private string ToolVersion()
        {
            try
            {
                return UserTools.FrontValue(UserTools.EffectiveFrontMatter(Tool, ToolDir), "updated");
            }
            catch (Exception ex)
            {
                Log.Warn($"Tool '{Tool}': reading its version failed: {ex.Message}");
                return null;
            }
        }

        // ---- state ----

        /// <summary>The inputs changed since the profile was loaded (none without one).</summary>
        public List<string> Modified(ToolForm form) =>
            Loaded == null || _baseline == null ? new List<string>() : ProfileValues.Differences(form, _baseline, ProfileValues.Capture(form, ProfileValues.Mode.All, Document));

        /// <summary>An input's marker: "edited" (changed since loading), "set" (the profile set it), else null.</summary>
        public string Mark(FormField f, ICollection<string> modified) =>
            Loaded == null ? null : modified.Contains(f.Id) ? "edited" : _set.Contains(f.Id) ? "set" : null;

        /// <summary>What the run records ({ name, modified: [ids], locked }), or null when no profile was loaded.</summary>
        public JObject Use(ToolForm form)
        {
            if (Loaded == null) return null;
            return new JObject { ["name"] = Loaded.Name, ["modified"] = new JArray(Modified(form)), ["locked"] = Loaded.Locked };
        }

        /// <summary>
        /// { name, locked, modified, isDefault, default, profiles: [{ name, locked, isDefault }], note, error, warnings } for the
        /// panel's Profile row.
        /// </summary>
        public JObject State(ToolForm form, ICollection<string> modified = null)
        {
            modified = modified ?? Modified(form);
            List<ToolProfile> list;
            string def;
            try
            {
                list = ToolProfiles.List(Tool, _root, ToolDir);
                def = ToolProfiles.DefaultName(Tool, _root);
            }
            catch (Exception ex)
            {
                Log.Warn($"Tool '{Tool}': listing its profiles failed: {ex.Message}");
                list = new List<ToolProfile>();
                def = null;
            }
            return new JObject
            {
                ["name"] = Loaded?.Name,
                ["locked"] = Loaded?.Locked == true,
                ["modified"] = modified.Count > 0,
                ["isDefault"] = Loaded != null && ToolProfiles.SameName(Loaded.Name, def),
                ["default"] = def,
                ["profiles"] = new JArray(list.Select(p => new JObject { ["name"] = p.Name, ["locked"] = p.Locked, ["isDefault"] = ToolProfiles.SameName(p.Name, def) })),
                ["note"] = Note,
                ["error"] = Error,
                ["warnings"] = Warnings.Count == 0 ? null : new JArray(Warnings),
            };
        }

        /// <summary>A run's record: the profile on its inputs, the values kept for save_tool_profile from_last_run, the last used profile.</summary>
        public void Finished(ToolForm form, ToolInputs inputs)
        {
            try
            {
                inputs.Profile = Use(form);
                ToolProfiles.LastRun[Tool] = ProfileValues.Capture(form, ProfileValues.Mode.Profile);
                if (Loaded != null) ToolProfiles.MarkUsed(Tool, Loaded.Name, _root);
            }
            catch (Exception ex)
            {
                Log.Warn($"Tool '{Tool}': recording its profile failed: {ex.Message}");
            }
        }
    }
}

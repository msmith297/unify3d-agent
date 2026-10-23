using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Infrastructure;
using SwAgent.Tools;

namespace SwAgent.UI
{
    /// <summary>
    /// The page's "Profile" group (docs/tool-profiles.md section 2): a dropdown of the tool's profiles (loading one puts the
    /// tool defaults back, then its values), a line saying which is loaded and whether it was changed, a name box with
    /// "Save as new profile" and "Make it the default", "Update profile" and "Set as default". Renaming, deleting and
    /// exporting are in the panel (Options > Tool profiles). The form logic is ProfileSession, as in the panel.
    /// </summary>
    public sealed partial class ToolPage
    {
        private readonly string _requestedProfile;
        private ProfileSession _profiles;

        private IPropertyManagerPageCombobox _profileBox;
        private IPropertyManagerPageLabel _profileLabel;
        private IPropertyManagerPageTextbox _profileName;
        private IPropertyManagerPageCheckbox _profileMakeDefault;
        private IPropertyManagerPageControl _profileUpdate;
        private int _profileBoxId = -1, _profileNameId = -1, _profileMakeDefaultId = -1, _profileSaveId = -1, _profileUpdateId = -1, _profileDefaultId = -1;

        /// <summary>The dropdown's entries: null for "(Tool defaults)", then profile names.</summary>
        private List<string> _profileItems = new List<string>();

        private void BuildProfileGroup()
        {
            if (_profiles == null || _form.Fields.All(f => f.Type == "selection" || f.Type == "info")) return;
            var group = Group(_nextId++, "Profile", (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded));
            var left = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge;
            _profileBoxId = _nextId++;
            _profileBox = (IPropertyManagerPageCombobox)Control(group, _profileBoxId, (short)swPropertyManagerPageControlType_e.swControlType_Combobox,
                "Profile", left, ControlOptions, "Saved settings for this tool. Loading one sets its values; you can still change any field.");
            FillProfileBox();
            _profileLabel = AddLabel(group, ProfileStateText());
            _profileUpdateId = _nextId++;
            _profileUpdate = (IPropertyManagerPageControl)Control(group, _profileUpdateId, (short)swPropertyManagerPageControlType_e.swControlType_Button,
                "Update profile", left, ControlOptions, "Save your changes into the loaded profile.");
            _profileDefaultId = _nextId++;
            Control(group, _profileDefaultId, (short)swPropertyManagerPageControlType_e.swControlType_Button, "Set as default", left, ControlOptions,
                "Open this tool with the profile picked above from now on ((Tool defaults): no profile).");
            AddLabel(group, "Name for a new profile");
            _profileNameId = _nextId++;
            _profileName = (IPropertyManagerPageTextbox)Control(group, _profileNameId, (short)swPropertyManagerPageControlType_e.swControlType_Textbox,
                "Name", left, ControlOptions, "For example: Shop A - PU on the 50 t press");
            _profileMakeDefaultId = _nextId++;
            _profileMakeDefault = (IPropertyManagerPageCheckbox)Control(group, _profileMakeDefaultId, (short)swPropertyManagerPageControlType_e.swControlType_Checkbox,
                "Make it the default", left, ControlOptions, "Open this tool with the new profile from now on.");
            _profileSaveId = _nextId++;
            Control(group, _profileSaveId, (short)swPropertyManagerPageControlType_e.swControlType_Button, "Save as new profile", left, ControlOptions,
                "Save the values on this page under the name above. It doesn't run the tool.");
            ShowProfileState();
            if (_profiles.Note != null || _profiles.Warnings.Count > 0) _pendingProfileMessage = ProfileMessage();
        }

        /// <summary>The note from opening with a profile, shown once the page is up (SetMessage3 before Show2 is replaced by the description).</summary>
        private string _pendingProfileMessage;

        private void FillProfileBox()
        {
            var def = ToolProfiles.DefaultName(_spec.Name);
            var list = ToolProfiles.List(_spec.Name);
            _profileItems = new List<string> { null }.Concat(list.Select(p => p.Name)).ToList();
            var texts = new[] { ToolProfiles.ToolDefaults }.Concat(list.Select(p => p.Name + (ToolProfiles.SameName(p.Name, def) ? " (default)" : "") + (p.Locked ? " (comes with the tool)" : ""))).ToArray();
            _profileBox.AddItems(texts);
            var at = _profiles.Loaded == null ? 0 : _profileItems.FindIndex(n => n != null && ToolProfiles.SameName(n, _profiles.Loaded.Name));
            _profileBox.CurrentSelection = (short)Math.Max(0, at);
        }

        /// <summary>A refill after a save or a new default; a SolidWorks version that can't refill a shown dropdown shows it on the next page.</summary>
        private void RefillProfileBox()
        {
            if (_profileBox == null) return;
            Quiet(() =>
            {
                try
                {
                    _profileBox.Clear();
                    FillProfileBox();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Tool page '{_spec.Name}': refilling the profile list failed: {ex.Message}");
                    Message("Saved. The profile list updates the next time you open the tool.");
                }
            });
        }

        private bool IsProfileControl(int id) => id >= 0 && (id == _profileNameId || id == _profileMakeDefaultId);

        private string ProfileStateText()
        {
            if (_profiles?.Loaded == null) return "No profile loaded";
            var modified = _profiles.Modified(_form).Count > 0;
            return _profiles.Loaded.Name + (modified ? " (modified)" : "") + (_profiles.Loaded.Locked ? " - comes with the tool" : "");
        }

        /// <summary>Which profile is loaded and whether the page differs from it; Update only for the user's own profile once changed.</summary>
        private void ShowProfileState()
        {
            if (_profileLabel == null) return;
            try
            {
                _profileLabel.Caption = ProfileStateText();
                _profileUpdate.Enabled = _profiles.Loaded != null && !_profiles.Loaded.Locked && _profiles.Modified(_form).Count > 0;
            }
            catch
            {
                // The page is closing.
            }
        }

        private string ProfileMessage()
        {
            var lines = new List<string>();
            if (_profiles.Error != null) lines.Add(_profiles.Error);
            else if (_profiles.Note != null) lines.Add(_profiles.Note);
            lines.AddRange(_profiles.Warnings);
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        private bool OnProfileCombo(int id, int item)
        {
            if (id != _profileBoxId || _profiles == null) return false;
            if (item < 0 || item >= _profileItems.Count) return true;
            ProfileAction(new JObject { ["action"] = "load", ["name"] = _profileItems[item] }, refill: false);
            return true;
        }

        private bool OnProfileButton(int id)
        {
            if (_profiles == null || id < 0) return false;
            if (id == _profileSaveId)
            {
                ProfileAction(new JObject { ["action"] = "save", ["name"] = _profileName?.Text, ["makeDefault"] = _profileMakeDefault?.Checked == true }, refill: true);
                return true;
            }
            if (id == _profileUpdateId)
            {
                ProfileAction(new JObject { ["action"] = "update" }, refill: false);
                return true;
            }
            if (id == _profileDefaultId)
            {
                var i = _profileBox?.CurrentSelection ?? -1;
                var name = i >= 0 && i < _profileItems.Count ? _profileItems[i] : null;
                ProfileAction(new JObject { ["action"] = "setDefault", ["name"] = name }, refill: true);
                return true;
            }
            return false;
        }

        /// <summary>A profile action on the page: read the controls first, act on the form, then show every value it set.</summary>
        private void ProfileAction(JObject action, bool refill)
        {
            try
            {
                Pull();
                _profiles.Act(_form, action);
            }
            catch (ToolException ex)
            {
                _profiles.Error = ex.Message;
            }
            catch (Exception ex)
            {
                Log.Warn($"Tool page '{_spec.Name}': profile action failed: {ex.Message}");
                _profiles.Error = "That didn't work: " + ex.Message;
            }
            if ((string)action["action"] == "load")
            {
                // Every value may have changed: write them all back (entries refill only where they changed).
                foreach (var f in _form.Fields) f.Changes |= FieldChange.Value | FieldChange.Visible | FieldChange.Enabled | FieldChange.Caption;
                Sync();
            }
            if (refill && _profiles.Error == null)
            {
                RefillProfileBox();
                if ((string)action["action"] == "save")
                    Quiet(() =>
                    {
                        _profileName.Text = "";
                        _profileMakeDefault.Checked = false;
                    });
            }
            Message(ProfileMessage());
            Changed();
        }
    }
}

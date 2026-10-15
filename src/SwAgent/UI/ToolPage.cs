using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Scripting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using SwAgent.Infrastructure;
using SwAgent.Scripting;
using SwAgent.Tools;
using Timer = System.Windows.Forms.Timer;

namespace SwAgent.UI
{
    /// <summary>
    /// A tool's PropertyManager page, built at run time from its inputs.json: native selection boxes, number boxes with
    /// units, checkboxes, dropdowns, option buttons, a note box and file lists - so a user tool feels exactly like a
    /// SolidWorks command. While it's open the tool's preview code draws temporary ("holographic") bodies that follow
    /// every change; if that ever gets slow the preview switches to on-demand (the eye button) instead of lagging.
    /// </summary>
    [ComVisible(true)]
    public sealed class ToolPage : IPropertyManagerPage2Handler9
    {
        private const int PreviewBudgetMs = 350;
        private static readonly string[] TierNames = { "Fast", "Balanced", "Max" };

        private readonly ISldWorks _sw;
        private readonly IModelDoc2 _model;
        private readonly ToolSpec _spec;
        private readonly List<SelectedEntity> _preselected;
        private readonly TaskCompletionSource<ToolInputs> _done = new TaskCompletionSource<ToolInputs>();
        private readonly SynchronizationContext _ui = SynchronizationContext.Current;

        private IPropertyManagerPage2 _page;

        /// <summary>
        /// Every group box and control of the page, held for as long as the page is open. If .NET's garbage collector
        /// releases one (labels and groups we don't otherwise touch), SolidWorks frees it while the page still uses
        /// it, and the next read of a control on the page crashes SolidWorks. A large allocation - such as compiling
        /// the preview two seconds after the page opens - is exactly when that collection happens.
        /// </summary>
        private readonly List<object> _keep = new List<object>();

        private IDisposable _guard;
        private readonly Dictionary<int, Field> _fields = new Dictionary<int, Field>();
        private readonly List<Field> _ordered = new List<Field>();
        private int _nextId = 10;
        private IPropertyManagerPageCombobox _tierBox;
        private int _tierBoxId = -1;
        private ToolInputs _result;

        private ScriptRunner<object> _previewRunner;
        private readonly Timer _previewTimer = new Timer { Interval = 120 };
        private bool _livePreview = true;
        private bool _previewRunning;
        private readonly List<IBody2> _shownBodies = new List<IBody2>();

        /// <summary>The user changed the tool's intelligence level in the page.</summary>
        public event Action<string> TierChanged;

        private sealed class Field
        {
            public ToolInputSpec Spec;
            public string Type;
            public int Id;
            public int Mark;
            public object Control;
            public List<IPropertyManagerPageOption> Options;
            public IPropertyManagerPageListbox FileList;
            public List<string> Files = new List<string>();
            public int AddButtonId;
            public int ClearButtonId;
        }

        /// <summary>Something that was selected before the tool started.</summary>
        internal sealed class SelectedEntity
        {
            public object Entity;
            public int Type;
            public IComponent2 Component;
        }

        internal ToolPage(ISldWorks sw, IModelDoc2 model, ToolSpec spec, List<SelectedEntity> preselected)
        {
            _sw = sw;
            _model = model;
            _spec = spec;
            _preselected = preselected;
            _previewTimer.Tick += (s, e) =>
            {
                _previewTimer.Stop();
                _ = RunPreviewAsync();
            };
        }

        /// <summary>Show the page; completes with the inputs on OK, or null on Cancel.</summary>
        internal Task<ToolInputs> ShowAsync(ScriptHost scripts)
        {
            var options = (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_LockedPage |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_PushpinButton |
                                swPropertyManagerPageOptions_e.swPropertyManagerOptions_CanEscapeCancel);
            if (_spec.Preview != null && !_spec.Paused.Contains("preview")) options |= (int)swPropertyManagerPageOptions_e.swPropertyManagerOptions_PreviewButton;

            var errors = 0;
            _page = (IPropertyManagerPage2)_sw.CreatePropertyManagerPage(_spec.Title, options, this, ref errors);
            if (_page == null) throw new InvalidOperationException($"SolidWorks couldn't create the tool's page (error {errors}).");
            if (!string.IsNullOrWhiteSpace(_spec.Description))
                _page.SetMessage3(_spec.Description, (int)swPropertyManagerPageMessageVisibility.swMessageBoxVisible,
                    (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand, _spec.Title);

            // From here until the page closes, a SolidWorks crash is attributed to this tool's page (see CrashGuard).
            _guard = CrashGuard.Enter("page", _spec.Name, _spec.Title, _spec.Preview ?? _spec.Macro);
            try
            {
                Build();
                _page.Show2(0);
            }
            catch
            {
                _guard.Dispose();
                _guard = null;
                throw;
            }
            Preselect();
            UpdateOkButton();

            if (_spec.Preview != null && _spec.Paused.Contains("preview"))
            {
                Message("The live preview is paused: SolidWorks closed while drawing it last time. Ask Unify3D to fix this tool.");
            }
            else if (_spec.Preview != null)
            {
                // Compile the preview off the UI thread while the user looks at the page.
                scripts.CompilePreviewAsync(_spec.Preview).ContinueWith(t =>
                {
                    if (t.Result.Errors != null) Message("Preview unavailable: " + t.Result.Errors.Split('\n')[0]);
                    else
                    {
                        _previewRunner = t.Result.Runner;
                        SchedulePreview();
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            return _done.Task;
        }

        // ---- building ----

        private void Build()
        {
            var groups = new Dictionary<string, IPropertyManagerPageGroup>();
            var groupId = 1;
            var nextMark = 2;
            foreach (var input in _spec.Inputs)
            {
                var type = ToolSpec.Type(input);
                var caption = string.IsNullOrWhiteSpace(input.Group) ? (type == "selection" ? "Selections" : type == "text" || type == "files" ? "Context" : "Parameters") : input.Group;
                if (!groups.TryGetValue(caption, out var group))
                {
                    group = (IPropertyManagerPageGroup)Group(groupId++, caption,
                        (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded));
                    groups[caption] = group;
                }
                var f = new Field { Spec = input, Type = type };
                AddField(group, f, ref nextMark);
                _ordered.Add(f);
            }

            if (_spec.Agent)
            {
                var group = (IPropertyManagerPageGroup)Group(groupId, "Unify3D Agent", (int)swAddGroupBoxOptions_e.swGroupBoxOptions_Visible);
                AddLabel(group, "Intelligence");
                var id = _nextId++;
                _tierBoxId = id;
                _tierBox = (IPropertyManagerPageCombobox)Control(group, id, (short)swPropertyManagerPageControlType_e.swControlType_Combobox, "Intelligence",
                    (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_Indent, ControlOptions,
                    "How much intelligence the agent uses for this tool: Fast (quickest), Balanced, or Max (hardest problems).");
                _tierBox.AddItems(TierNames);
                _tierBox.CurrentSelection = (short)Math.Max(0, Array.IndexOf(ToolSpec.Tiers, _spec.Intelligence));
            }
        }

        private IPropertyManagerPageGroup Group(int id, string caption, int options)
        {
            var group = (IPropertyManagerPageGroup)_page.AddGroupBox(id, caption, options);
            _keep.Add(group);
            return group;
        }

        private object Control(IPropertyManagerPageGroup group, int id, short type, string caption, short align, int options, string tip)
        {
            var control = group.AddControl2(id, type, caption, align, options, tip);
            _keep.Add(control);
            return control;
        }

        private static int ControlOptions => (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled);

        private void AddLabel(IPropertyManagerPageGroup group, string text)
        {
            Control(group, _nextId++, (short)swPropertyManagerPageControlType_e.swControlType_Label, text,
                (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, ControlOptions, "");
        }

        private void AddField(IPropertyManagerPageGroup group, Field f, ref int nextMark)
        {
            var s = f.Spec;
            var tip = s.Tip ?? s.Label ?? "";
            var indent = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_Indent;
            switch (f.Type)
            {
                case "selection":
                {
                    f.Id = _nextId++;
                    var box = (IPropertyManagerPageSelectionbox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox,
                        s.Label, indent, ControlOptions, tip);
                    var filters = SelectFilters(s.Filter);
                    if (filters.Length > 0) box.SetSelectionFilters(filters);
                    f.Mark = nextMark;
                    nextMark *= 2;
                    box.Mark = f.Mark;
                    box.SingleEntityOnly = s.Multiple == false;
                    box.Height = (short)(s.Multiple == false ? 15 : 50);
                    ((IPropertyManagerPageControl)box).SetStandardPictureLabel((int)PictureFor(s.Icon, s.Filter));
                    f.Control = box;
                    break;
                }
                case "number":
                case "integer":
                {
                    AddLabel(group, s.Label);
                    f.Id = _nextId++;
                    var box = (IPropertyManagerPageNumberbox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Numberbox,
                        s.Label, indent, ControlOptions, tip);
                    var unit = (s.Unit ?? "length").ToLowerInvariant();
                    if (f.Type == "integer")
                        box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_UnitlessInteger, s.Min ?? 0, s.Max ?? 1000, true, 1, 5, 1);
                    else if (unit == "angle")
                        box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Angle, Rad(s.Min ?? 0), Rad(s.Max ?? 360), true, Rad(1), Rad(15), Rad(0.1));
                    else if (unit == "length")
                        box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_Length, Mm(s.Min ?? 0), Mm(s.Max ?? 10000), true, Mm(1), Mm(10), Mm(0.1));
                    else
                        box.SetRange2((int)swNumberboxUnitType_e.swNumberBox_UnitlessDouble, s.Min ?? -1e9, s.Max ?? 1e9, true, 1, 10, 0.1);
                    box.Value = DefaultNumber(s, f.Type);
                    if (s.Icon != null || unit == "length" || unit == "angle")
                        ((IPropertyManagerPageControl)box).SetStandardPictureLabel((int)PictureFor(s.Icon ?? (unit == "angle" ? "angle" : "distance"), null));
                    f.Control = box;
                    break;
                }
                case "checkbox":
                {
                    f.Id = _nextId++;
                    var box = (IPropertyManagerPageCheckbox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Checkbox,
                        s.Label, (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, ControlOptions, tip);
                    box.Checked = string.Equals(s.Default, "true", StringComparison.OrdinalIgnoreCase);
                    f.Control = box;
                    break;
                }
                case "choice":
                {
                    AddLabel(group, s.Label);
                    f.Id = _nextId++;
                    var box = (IPropertyManagerPageCombobox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Combobox,
                        s.Label, indent, ControlOptions, tip);
                    var choices = s.Choices ?? new string[0];
                    if (choices.Length > 0) box.AddItems(choices);
                    box.CurrentSelection = (short)Math.Max(0, Array.IndexOf(choices, s.Default));
                    f.Control = box;
                    break;
                }
                case "options":
                {
                    AddLabel(group, s.Label);
                    f.Options = new List<IPropertyManagerPageOption>();
                    f.Id = _nextId;
                    var choices = s.Choices ?? new string[0];
                    for (var i = 0; i < choices.Length; i++)
                    {
                        var id = _nextId++;
                        var opt = (IPropertyManagerPageOption)Control(group, id, (short)swPropertyManagerPageControlType_e.swControlType_Option,
                            choices[i], indent, ControlOptions, tip);
                        opt.Checked = choices[i] == s.Default || (s.Default == null && i == 0);
                        f.Options.Add(opt);
                        _fields[id] = f;
                    }
                    return;
                }
                case "text":
                {
                    AddLabel(group, s.Label);
                    f.Id = _nextId++;
                    var box = (IPropertyManagerPageTextbox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Textbox,
                        s.Label, (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, ControlOptions,
                        (tip.Length > 0 ? tip + " " : "") + "(Win+H to dictate)");
                    box.Style = (int)swPropMgrPageTextBoxStyle_e.swPropMgrPageTextBoxStyle_Multiline;
                    box.Height = 50;
                    box.Text = s.Default ?? "";
                    f.Control = box;
                    break;
                }
                case "files":
                {
                    AddLabel(group, s.Label);
                    f.Id = _nextId++;
                    f.FileList = (IPropertyManagerPageListbox)Control(group, f.Id, (short)swPropertyManagerPageControlType_e.swControlType_Listbox,
                        s.Label, (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, ControlOptions, tip);
                    f.FileList.Height = 40;
                    f.AddButtonId = _nextId++;
                    Control(group, f.AddButtonId, (short)swPropertyManagerPageControlType_e.swControlType_Button, "Add file...",
                        (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, ControlOptions, "Add " + AcceptText(s.Accept));
                    f.ClearButtonId = _nextId++;
                    Control(group, f.ClearButtonId, (short)swPropertyManagerPageControlType_e.swControlType_Button, "Clear",
                        (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge, ControlOptions, "Remove all files");
                    _fields[f.AddButtonId] = f;
                    _fields[f.ClearButtonId] = f;
                    f.Control = f.FileList;
                    break;
                }
                default:
                    AddLabel(group, $"{s.Label} (unsupported input type '{s.Type}')");
                    return;
            }
            _fields[f.Id] = f;
        }

        /// <summary>Put what the user had already selected into the matching selection boxes.</summary>
        private void Preselect()
        {
            var boxes = _ordered.Where(f => f.Type == "selection").ToList();
            if (boxes.Count == 0 || _preselected.Count == 0) return;
            _model.ClearSelection2(true);
            var used = new HashSet<object>();
            foreach (var f in boxes)
            {
                var allowed = new HashSet<int>(SelectFilters(f.Spec.Filter));
                var matches = _preselected.Where(p => !used.Contains(p.Entity) && (allowed.Count == 0 || allowed.Contains(p.Type))).ToList();
                if (f.Spec.Multiple == false) matches = matches.Take(1).ToList();
                if (matches.Count == 0) continue;
                ((IPropertyManagerPageSelectionbox)f.Control).SetSelectionFocus();
                foreach (var m in matches)
                {
                    try
                    {
                        if (SelectionTools.Select(_model, m.Entity, true, f.Mark)) used.Add(m.Entity);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Preselect failed: " + ex.Message);
                    }
                }
            }
            // Leave the focus on the first box that still needs something.
            var next = boxes.FirstOrDefault(f => ((IPropertyManagerPageSelectionbox)f.Control).ItemCount == 0) ?? boxes[0];
            ((IPropertyManagerPageSelectionbox)next.Control).SetSelectionFocus();
        }

        // ---- values ----

        /// <summary>Read every control. withRefs adds persistent references (needed for the agent, skipped for previews).</summary>
        private ToolInputs Collect(bool withRefs)
        {
            var inputs = new ToolInputs();
            var sm = (ISelectionMgr)_model.SelectionManager;
            foreach (var f in _ordered)
            {
                var id = f.Spec.Id;
                switch (f.Type)
                {
                    case "selection":
                    {
                        var items = new List<ToolInputs.SelectedItem>();
                        var count = sm.GetSelectedObjectCount2(-1);
                        for (var i = 1; i <= count; i++)
                        {
                            if (sm.GetSelectedObjectMark(i) != f.Mark) continue;
                            var obj = sm.GetSelectedObject6(i, -1);
                            var item = new ToolInputs.SelectedItem { Entity = obj };
                            if (withRefs)
                            {
                                var comp = sm.GetSelectedObjectsComponent4(i, -1) as IComponent2;
                                item.Type = SelectionTools.Pretty((swSelectType_e)sm.GetSelectedObjectType3(i, -1));
                                item.Label = SelectionTools.Owner(obj, comp);
                                item.Detail = SelectionTools.Describe(obj, comp);
                                try
                                {
                                    if (_model.Extension.GetPersistReference3(obj) is byte[] bytes) item.Ref = Convert.ToBase64String(bytes);
                                }
                                catch
                                {
                                    // Some entities have no persistent id; the agent still gets the description.
                                }
                            }
                            items.Add(item);
                        }
                        inputs.SetSelection(id, items);
                        break;
                    }
                    case "number":
                    case "integer":
                        inputs.Set(id, ((IPropertyManagerPageNumberbox)f.Control).Value);
                        break;
                    case "checkbox":
                        inputs.Set(id, ((IPropertyManagerPageCheckbox)f.Control).Checked);
                        break;
                    case "choice":
                    {
                        var box = (IPropertyManagerPageCombobox)f.Control;
                        var choices = f.Spec.Choices ?? new string[0];
                        var i = box.CurrentSelection;
                        inputs.Set(id, i >= 0 && i < choices.Length ? choices[i] : null);
                        break;
                    }
                    case "options":
                    {
                        var choices = f.Spec.Choices ?? new string[0];
                        var i = f.Options.FindIndex(o => o.Checked);
                        inputs.Set(id, i >= 0 && i < choices.Length ? choices[i] : null);
                        break;
                    }
                    case "text":
                        inputs.Set(id, ((IPropertyManagerPageTextbox)f.Control).Text ?? "");
                        break;
                    case "files":
                        inputs.SetFiles(id, f.Files.ToList());
                        break;
                }
            }
            return inputs;
        }

        private bool RequiredSatisfied()
        {
            foreach (var f in _ordered.Where(x => ToolSpec.IsRequired(x.Spec)))
            {
                switch (f.Type)
                {
                    case "selection" when ((IPropertyManagerPageSelectionbox)f.Control).ItemCount == 0:
                    case "files" when f.Files.Count == 0:
                    case "text" when string.IsNullOrWhiteSpace(((IPropertyManagerPageTextbox)f.Control).Text):
                        return false;
                }
            }
            return true;
        }

        private void UpdateOkButton()
        {
            try
            {
                _page?.EnableButton((int)swPropertyManagerPageButtons_e.swPropertyManagerPageButton_Ok, RequiredSatisfied());
            }
            catch
            {
                // Older SolidWorks: the button just stays enabled.
            }
        }

        private void Changed()
        {
            UpdateOkButton();
            SchedulePreview();
        }

        // ---- preview ----

        private void SchedulePreview()
        {
            if (_previewRunner == null || !_livePreview) return;
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private async Task RunPreviewAsync()
        {
            if (_previewRunner == null || _previewRunning || _page == null) return;
            if (_model.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                Message("Preview is available in part documents.");
                return;
            }
            _previewRunning = true;
            var watch = Stopwatch.StartNew();
            try
            {
                HidePreview();
                if (!RequiredSatisfied()) return;
                // If SolidWorks dies in here, the next start knows it was this tool's preview and pauses it.
                using (CrashGuard.Enter("preview", _spec.Name, _spec.Title, _spec.Preview))
                {
                    var globals = new ScriptGlobals { swApp = _sw, Model = _model, Inputs = Collect(withRefs: false) };
                    await _previewRunner(globals);
                    _shownBodies.AddRange(PreviewDisplay.Show(_model, globals.PreviewBodies));
                }
                Message(null);
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException agg ? agg.InnerException ?? ex : ex;
                Message("Preview: " + (inner is MacroInputException ? inner.Message : inner.GetType().Name + ": " + inner.Message));
            }
            finally
            {
                _previewRunning = false;
                if (watch.ElapsedMilliseconds > PreviewBudgetMs && _livePreview)
                {
                    // Never let the preview make the page feel sluggish.
                    _livePreview = false;
                    Message("This preview is slow on this model, so it updates when you press the eye (Preview) button.");
                }
            }
        }

        private void HidePreview() => PreviewDisplay.Hide(_model, _shownBodies);

        private void Message(string text)
        {
            try
            {
                if (text == null)
                {
                    if (!string.IsNullOrWhiteSpace(_spec.Description))
                        _page.SetMessage3(_spec.Description, (int)swPropertyManagerPageMessageVisibility.swMessageBoxVisible,
                            (int)swPropertyManagerPageMessageExpanded.swMessageBoxMaintainExpandState, _spec.Title);
                    return;
                }
                _page.SetMessage3(text, (int)swPropertyManagerPageMessageVisibility.swImportantMessageBox,
                    (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand, _spec.Title);
            }
            catch
            {
                // Page closing.
            }
        }

        // ---- handler ----

        public void OnClose(int reason)
        {
            _previewTimer.Stop();
            if (reason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay)
            {
                try
                {
                    _result = Collect(withRefs: true);
                }
                catch (Exception ex)
                {
                    Log.Error("Reading the tool page failed", ex);
                }
            }
            HidePreview();
        }

        public void AfterClose()
        {
            var result = _result;
            _page = null;
            _keep.Clear();
            _guard?.Dispose();
            _guard = null;
            // Let SolidWorks finish closing the page before the tool starts working.
            if (_ui != null) _ui.Post(_ => _done.TrySetResult(result), null);
            else _done.TrySetResult(result);
        }

        public bool OnPreview()
        {
            _ = RunPreviewAsync();
            return true;
        }

        public void OnSelectionboxListChanged(int id, int count) => Changed();
        public void OnNumberboxChanged(int id, double value) => Changed();
        public void OnNumberBoxTrackingCompleted(int id, double value) => Changed();
        public void OnCheckboxCheck(int id, bool check) => Changed();
        public void OnOptionCheck(int id) => Changed();
        public void OnTextboxChanged(int id, string text) => UpdateOkButton();

        public void OnComboboxSelectionChanged(int id, int item)
        {
            if (id == _tierBoxId)
            {
                if (item >= 0 && item < ToolSpec.Tiers.Length) TierChanged?.Invoke(ToolSpec.Tiers[item]);
                return;
            }
            Changed();
        }

        public void OnButtonPress(int id)
        {
            if (!_fields.TryGetValue(id, out var f) || f.Type != "files") return;
            if (id == f.ClearButtonId)
            {
                f.Files.Clear();
                f.FileList.Clear();
            }
            else if (id == f.AddButtonId)
            {
                using (var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    Title = f.Spec.Label,
                    Multiselect = f.Spec.Multiple != false,
                    Filter = FileFilter(f.Spec.Accept),
                })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    foreach (var path in dialog.FileNames)
                    {
                        if (f.Spec.Multiple == false)
                        {
                            f.Files.Clear();
                            f.FileList.Clear();
                        }
                        if (f.Files.Contains(path)) continue;
                        f.Files.Add(path);
                        f.FileList.AddItems(new[] { Path.GetFileName(path) });
                    }
                }
            }
            Changed();
        }

        public void AfterActivation() { }
        public bool OnHelp() => false;
        public bool OnPreviousPage() => false;
        public bool OnNextPage() => false;
        public void OnWhatsNew() { }
        public void OnUndo() { }
        public void OnRedo() { }
        public bool OnTabClicked(int id) => true;
        public void OnGroupExpand(int id, bool expanded) { }
        public void OnGroupCheck(int id, bool check) { }
        public void OnComboboxEditChanged(int id, string text) { }
        public void OnListboxSelectionChanged(int id, int item) { }
        public void OnSelectionboxFocusChanged(int id) { }
        public void OnSelectionboxCalloutCreated(int id) { }
        public void OnSelectionboxCalloutDestroyed(int id) { }
        public bool OnSubmitSelection(int id, object selection, int selType, ref string itemText) => true;
        public int OnActiveXControlCreated(int id, bool status) => 0;
        public void OnSliderPositionChanged(int id, double value) { }
        public void OnSliderTrackingCompleted(int id, double value) { }
        public bool OnKeystroke(int wparam, int message, int lparam, int id) => false;
        public void OnPopupMenuItem(int id) { }
        public void OnPopupMenuItemUpdate(int id, ref int retval) { }
        public void OnGainedFocus(int id) { }
        public void OnLostFocus(int id) { }
        public int OnWindowFromHandleControlCreated(int id, bool status) => 0;
        public void OnListboxRMBUp(int id, int posX, int posY) { }

        // ---- running without the page ----

        /// <summary>
        /// When everything the tool needs is already selected (and it doesn't ask to always show its page), build the
        /// inputs from the selection and defaults so it can run straight away.
        /// </summary>
        internal static ToolInputs TryFromSelection(ToolSpec spec, IModelDoc2 model, List<SelectedEntity> preselected, bool forTest = false)
        {
            if (spec.Confirm == "always" && !forTest) return null;
            var inputs = new ToolInputs();
            var used = new HashSet<object>();
            foreach (var s in spec.Inputs)
            {
                var type = ToolSpec.Type(s);
                switch (type)
                {
                    case "selection":
                    {
                        var allowed = new HashSet<int>(SelectFilters(s.Filter));
                        var matches = preselected.Where(p => !used.Contains(p.Entity) && (allowed.Count == 0 || allowed.Contains(p.Type))).ToList();
                        if (s.Multiple == false) matches = matches.Take(1).ToList();
                        if (matches.Count == 0 && ToolSpec.IsRequired(s) && !forTest) return null;
                        var items = new List<ToolInputs.SelectedItem>();
                        foreach (var m in matches)
                        {
                            used.Add(m.Entity);
                            var item = new ToolInputs.SelectedItem
                            {
                                Entity = m.Entity,
                                Type = SelectionTools.Pretty((swSelectType_e)m.Type),
                                Label = SelectionTools.Owner(m.Entity, m.Component),
                                Detail = SelectionTools.Describe(m.Entity, m.Component),
                            };
                            try
                            {
                                if (model.Extension.GetPersistReference3(m.Entity) is byte[] bytes) item.Ref = Convert.ToBase64String(bytes);
                            }
                            catch
                            {
                                // No persistent id.
                            }
                            items.Add(item);
                        }
                        inputs.SetSelection(s.Id, items);
                        break;
                    }
                    case "number":
                    case "integer":
                        inputs.Set(s.Id, DefaultNumber(s, type));
                        break;
                    case "checkbox":
                        inputs.Set(s.Id, string.Equals(s.Default, "true", StringComparison.OrdinalIgnoreCase));
                        break;
                    case "choice":
                    case "options":
                        inputs.Set(s.Id, s.Default ?? s.Choices?.FirstOrDefault());
                        break;
                    case "text":
                    case "files":
                        if (ToolSpec.IsRequired(s) && !forTest) return null;
                        if (type == "text") inputs.Set(s.Id, s.Default ?? "");
                        else inputs.SetFiles(s.Id, new List<string>());
                        break;
                }
            }
            return inputs;
        }

        /// <summary>What's selected right now, in order.</summary>
        internal static List<SelectedEntity> CurrentSelection(IModelDoc2 model)
        {
            var list = new List<SelectedEntity>();
            if (!(model?.SelectionManager is ISelectionMgr sm)) return list;
            var count = sm.GetSelectedObjectCount2(-1);
            for (var i = 1; i <= count; i++)
            {
                try
                {
                    list.Add(new SelectedEntity
                    {
                        Entity = sm.GetSelectedObject6(i, -1),
                        Type = sm.GetSelectedObjectType3(i, -1),
                        Component = sm.GetSelectedObjectsComponent4(i, -1) as IComponent2,
                    });
                }
                catch
                {
                    // Skip entities that can't be read.
                }
            }
            return list;
        }

        // ---- helpers ----

        private static double Mm(double v) => v / 1000.0;
        private static double Rad(double v) => v * Math.PI / 180.0;

        private static double DefaultNumber(ToolInputSpec s, string type)
        {
            var value = double.TryParse(s.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (s.Min ?? 0);
            if (type == "integer") return Math.Round(value);
            var unit = (s.Unit ?? "length").ToLowerInvariant();
            return unit == "angle" ? Rad(value) : unit == "length" ? Mm(value) : value;
        }

        internal static int[] SelectFilters(string[] names)
        {
            var list = new List<swSelectType_e>();
            foreach (var n in names ?? new string[0])
            {
                switch ((n ?? "").Trim().ToLowerInvariant())
                {
                    case "face": list.Add(swSelectType_e.swSelFACES); break;
                    case "edge": list.Add(swSelectType_e.swSelEDGES); break;
                    case "vertex": list.Add(swSelectType_e.swSelVERTICES); break;
                    case "feature": list.Add(swSelectType_e.swSelBODYFEATURES); break;
                    case "body": list.Add(swSelectType_e.swSelSOLIDBODIES); list.Add(swSelectType_e.swSelSURFACEBODIES); break;
                    case "component": list.Add(swSelectType_e.swSelCOMPONENTS); break;
                    case "plane": list.Add(swSelectType_e.swSelDATUMPLANES); break;
                    case "sketch": list.Add(swSelectType_e.swSelSKETCHES); break;
                    case "sketch_segment": list.Add(swSelectType_e.swSelSKETCHSEGS); break;
                    case "point": list.Add(swSelectType_e.swSelDATUMPOINTS); list.Add(swSelectType_e.swSelSKETCHPOINTS); list.Add(swSelectType_e.swSelVERTICES); break;
                }
            }
            return list.Distinct().Select(x => (int)x).ToArray();
        }

        private static swControlBitmapLabelType_e PictureFor(string icon, string[] filter)
        {
            switch ((icon ?? "").Trim().ToLowerInvariant())
            {
                case "distance": return swControlBitmapLabelType_e.swBitmapLabel_LinearDistance;
                case "depth": return swControlBitmapLabelType_e.swBitmapLabel_Depth;
                case "width": return swControlBitmapLabelType_e.swBitmapLabel_Width;
                case "thickness": return swControlBitmapLabelType_e.swBitmapLabel_Thickness1;
                case "radius": return swControlBitmapLabelType_e.swBitmapLabel_Radius;
                case "diameter": return swControlBitmapLabelType_e.swBitmapLabel_Diameter;
                case "angle": return swControlBitmapLabelType_e.swBitmapLabel_AngularDistance;
                case "face": return swControlBitmapLabelType_e.swBitmapLabel_SelectFace;
                case "edge": return swControlBitmapLabelType_e.swBitmapLabel_SelectEdge;
                case "vertex": return swControlBitmapLabelType_e.swBitmapLabel_SelectVertex;
                case "face_edge": return swControlBitmapLabelType_e.swBitmapLabel_SelectFaceEdge;
                case "component": return swControlBitmapLabelType_e.swBitmapLabel_SelectComponent;
                case "profile": return swControlBitmapLabelType_e.swBitmapLabel_SelectProfile;
            }
            var f = (filter ?? new string[0]).Select(x => x.ToLowerInvariant()).ToList();
            if (f.Count == 1 && f[0] == "face") return swControlBitmapLabelType_e.swBitmapLabel_SelectFace;
            if (f.Count == 1 && f[0] == "edge") return swControlBitmapLabelType_e.swBitmapLabel_SelectEdge;
            if (f.Count == 1 && f[0] == "vertex") return swControlBitmapLabelType_e.swBitmapLabel_SelectVertex;
            if (f.Count == 1 && f[0] == "component") return swControlBitmapLabelType_e.swBitmapLabel_SelectComponent;
            if (f.Count == 2 && f.Contains("face") && f.Contains("edge")) return swControlBitmapLabelType_e.swBitmapLabel_SelectFaceEdge;
            return swControlBitmapLabelType_e.swBitmapLabel_SelectEdgeFaceVertex;
        }

        private static string AcceptText(string[] accept)
        {
            var a = (accept ?? new string[0]).Select(x => x.ToLowerInvariant()).ToList();
            if (a.Count == 0) return "files";
            return string.Join(", ", a.Select(x => x == "image" ? "images" : x == "pdf" ? "PDFs" : x == "model" ? "3D models" : x));
        }

        private static string FileFilter(string[] accept)
        {
            var a = (accept ?? new string[0]).Select(x => x.ToLowerInvariant()).ToList();
            var parts = new List<string>();
            if (a.Count == 0 || a.Contains("image")) parts.Add("Images (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp");
            if (a.Count == 0 || a.Contains("pdf")) parts.Add("PDF documents (*.pdf)|*.pdf");
            if (a.Count == 0 || a.Contains("model")) parts.Add("3D models (*.stl;*.step;*.stp;*.3mf;*.igs;*.iges;*.sldprt;*.sldasm;*.x_t)|*.stl;*.step;*.stp;*.3mf;*.igs;*.iges;*.sldprt;*.sldasm;*.x_t");
            parts.Add("All files (*.*)|*.*");
            return string.Join("|", parts);
        }
    }
}

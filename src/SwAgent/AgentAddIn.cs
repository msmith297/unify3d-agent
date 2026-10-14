using System;
using System.Runtime.InteropServices;
using SwAgent.Chat;
using SwAgent.Infrastructure;
using SwAgent.Scripting;
using SwAgent.Tools;
using SwAgent.UI;
using Xarial.XCad.Base.Attributes;
using Xarial.XCad.SolidWorks;
using Xarial.XCad.SolidWorks.UI;
using Xarial.XCad.UI;
using Xarial.XCad.UI.Commands;
using Xarial.XCad.UI.TaskPane;

namespace SwAgent
{
    /// <summary>
    /// The SolidWorks add-in. Loaded in-process by SLDWORKS.exe through COM (see scripts/register.ps1).
    /// </summary>
    [ComVisible(true)]
    [Guid("6D2B1C1E-4A0F-4F1E-9C7B-3E5A8D2F7B41")]
    [Title("Unify3D Agent")]
    [Summary("Unify3D Agent - AI design agent for SOLIDWORKS: chat about your model and let it build and edit it.")]
    [Icon(typeof(Icons), nameof(Icons.Agent))]
    public class AgentAddIn : SwAddInEx
    {
        static AgentAddIn()
        {
            // Must run before any Roslyn / System.Memory type is touched.
            AssemblyResolver.Install();
        }

        internal static AgentAddIn Instance { get; private set; }

        internal AgentController Controller { get; private set; }

        private ISwTaskPane<ChatPaneControl> _pane;
        private UserToolRibbon _toolRibbon;

        public override void OnConnect()
        {
            Instance = this;
            Log.Info($"Unify3D Agent {GetType().Assembly.GetName().Version} connecting to SolidWorks {Application.Sw.RevisionNumber()}");
            Log.Prune();
            ChatDocument.PruneBackups();
            SoundMuter.RestoreNow(); // if SolidWorks crashed while the agent was working

            try
            {
                var tools = new ToolRegistry();
                var scripts = new ScriptHost(tools);
                tools.RegisterAll(new ModelTools());
                tools.RegisterAll(new ViewTools());
                tools.RegisterAll(new ApiTools());
                tools.RegisterAll(new SelectionTools());
                tools.RegisterAll(new HistoryTools());
                tools.RegisterAll(new UserTools());
                tools.RegisterAll(new LibraryTools());
                tools.RegisterAll(scripts);
                Controller = new AgentController(Application, tools, scripts);

                Application.Documents.RegisterHandler(() => new ChatDocumentHandler());
                Application.Documents.DocumentActivated += doc => Controller.OnDocumentActivated(doc);

                CommandManager.AddCommandGroup<AgentCommands_e>().CommandClick += OnCommand;
                CommandManager.AddContextMenu<FaceContextCommands_e>().CommandClick += _ => AskAboutSelection();
                CommandManager.AddContextMenu<EdgeContextCommands_e>().CommandClick += _ => AskAboutSelection();
                CommandManager.AddContextMenu<FeatureContextCommands_e>().CommandClick += _ => AskAboutSelection();

                // The user's own tools get a ribbon tab of their own, rebuilt as they are saved or changed.
                _toolRibbon = new UserToolRibbon(((Xarial.XCad.SolidWorks.UI.Commands.ISwCommandManager)CommandManager).CmdMgr);
                _toolRibbon.Rebuild();
                UserTools.Changed += _toolRibbon.Invalidate;

                _pane = this.CreateTaskPaneWinForm<ChatPaneControl>(new TaskPaneSpec
                {
                    Title = "Unify3D Agent",
                    Tooltip = "Unify3D Agent",
                    Icon = new BaseImage(Icons.Agent),
                });
                if (_pane.IsControlCreated) InitPane(_pane.Control);
                else _pane.ControlCreated += InitPane;

                Log.Info("Unify3D Agent connected");
            }
            catch (Exception ex)
            {
                Log.Error("Unify3D Agent failed to start", ex);
                throw;
            }
        }

        private void InitPane(ChatPaneControl control)
        {
            var theme = Controller.ReadTheme();
            if (theme?["pmBg"] != null) control.SetBackground((string)theme["pmBg"]);
            Controller.ThemeChanged += t =>
            {
                if (t?["pmBg"] != null) control.SetBackground((string)t["pmBg"]);
            };
            control.BridgeReady += () => Controller.AttachBridge(control.Bridge);
            control.Initialize();
        }

        private void OnCommand(AgentCommands_e cmd)
        {
            ShowPane();
            switch (cmd)
            {
                case AgentCommands_e.OpenChat:
                    Controller.SendCommand("focus");
                    break;
                case AgentCommands_e.NewChat:
                    Controller.SendCommand("newChat");
                    break;
                case AgentCommands_e.AskAboutSelection:
                    AskAboutSelection();
                    break;
                case AgentCommands_e.AttachView:
                    Controller.SendCommand("attachView");
                    break;
                case AgentCommands_e.Settings:
                    Controller.SendCommand("openSettings");
                    break;
            }
        }

        /// <summary>Ribbon callback for an "Agent Tools" button (SolidWorks calls it by name; see UserToolRibbon).</summary>
        public void OnUserToolClick(string arg)
        {
            try
            {
                ShowPane();
                if (UserToolRibbon.IsNewTool(arg))
                {
                    Controller.SendCommand("newTool");
                    return;
                }
                var name = _toolRibbon?.ToolAt(arg);
                if (name != null) Controller.SendCommand("runTool", new { tool = name });
            }
            catch (Exception ex)
            {
                Log.Error("Running a tool from the ribbon failed", ex);
            }
        }

        /// <summary>Ribbon enable state: tools need an open document; "New Tool" is always available.</summary>
        public int OnUserToolEnable(string arg)
        {
            try
            {
                return UserToolRibbon.IsNewTool(arg) || Application.Sw.ActiveDoc != null ? 1 : 0;
            }
            catch
            {
                return 1;
            }
        }

        private void AskAboutSelection()
        {
            ShowPane();
            Controller.SendCommand("askAboutSelection");
        }

        private void ShowPane()
        {
            try
            {
                // An unpinned Task Pane collapses on the next click in the graphics area. Opening the Agent
                // from the ribbon is an explicit request to keep it visible.
                if (!Application.Sw.TaskPaneIsPinned) Application.Sw.TaskPaneIsPinned = true;
                _pane?.TaskPaneView?.ShowView();
            }
            catch (Exception ex)
            {
                Log.Warn("Could not show the task pane: " + ex.Message);
            }
        }

        public override void OnDisconnect()
        {
            Log.Info("Unify3D Agent disconnecting");
            SoundMuter.RestoreNow();
            Controller?.InstallPendingUpdateOnExit();
            if (_toolRibbon != null)
            {
                UserTools.Changed -= _toolRibbon.Invalidate;
                _toolRibbon.Dispose();
            }
            Controller?.Dispose();
            Instance = null;
        }
    }
}

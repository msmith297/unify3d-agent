using Xarial.XCad.Base.Attributes;
using Xarial.XCad.Features;
using Xarial.XCad.Geometry;
using Xarial.XCad.UI.Commands.Attributes;
using Xarial.XCad.UI.Commands.Enums;

namespace SwAgent
{
    /// <summary>The "Agent" ribbon tab (CommandManager) and Tools menu entries.</summary>
    [Title("Unify3D")]
    [Summary("AI agent for SolidWorks")]
    [Icon(typeof(Icons), nameof(Icons.Agent))]
    [CommandGroupInfo(1, "Agent")]
    public enum AgentCommands_e
    {
        [Title("Unify3D Agent")]
        [Summary("Show the Unify3D Agent panel")]
        [Icon(typeof(Icons), nameof(Icons.Agent))]
        [CommandItemInfo(true, true, WorkspaceTypes_e.All, true, RibbonTabTextDisplay_e.TextBelow)]
        OpenChat,

        [Title("New Chat")]
        [Summary("Start a new conversation for the active document")]
        [Icon(typeof(Icons), nameof(Icons.NewChat))]
        [CommandItemInfo(true, true, WorkspaceTypes_e.All, true, RibbonTabTextDisplay_e.TextBelow)]
        NewChat,

        [Title("Ask About Selection")]
        [Summary("Ask the agent about the selected faces, edges, features or components")]
        [Icon(typeof(Icons), nameof(Icons.Selection))]
        [CommandItemInfo(true, true, WorkspaceTypes_e.AllDocuments, true, RibbonTabTextDisplay_e.TextBelow)]
        AskAboutSelection,

        [Title("Attach View")]
        [Summary("Attach a screenshot of the graphics area to your next message")]
        [Icon(typeof(Icons), nameof(Icons.Camera))]
        [CommandItemInfo(true, true, WorkspaceTypes_e.AllDocuments, true, RibbonTabTextDisplay_e.TextBelow)]
        AttachView,

        [CommandSpacer]
        [Title("Settings")]
        [Summary("Model, API key and approval settings")]
        [Icon(typeof(Icons), nameof(Icons.Settings))]
        [CommandItemInfo(true, true, WorkspaceTypes_e.All, true, RibbonTabTextDisplay_e.TextBelow)]
        Settings,
    }

    /// <summary>Right-click on a face.</summary>
    [ContextMenuCommandGroupInfo(2, typeof(IXFace))]
    public enum FaceContextCommands_e
    {
        [Title("Ask Unify3D")]
        [Icon(typeof(Icons), nameof(Icons.Selection))]
        AskAgent,
    }

    /// <summary>Right-click on an edge.</summary>
    [ContextMenuCommandGroupInfo(3, typeof(IXEdge))]
    public enum EdgeContextCommands_e
    {
        [Title("Ask Unify3D")]
        [Icon(typeof(Icons), nameof(Icons.Selection))]
        AskAgent,
    }

    /// <summary>Right-click on a feature in the tree.</summary>
    [ContextMenuCommandGroupInfo(4, typeof(IXFeature))]
    public enum FeatureContextCommands_e
    {
        [Title("Ask Unify3D")]
        [Icon(typeof(Icons), nameof(Icons.Selection))]
        AskAgent,
    }
}

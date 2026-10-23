using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using Xarial.XCad.SolidWorks;
using Xarial.XCad.SolidWorks.Documents;

namespace SwAgent.Tools
{
    /// <summary>Everything a tool needs: the SolidWorks app, the document the chat belongs to, and the chat thread.</summary>
    public sealed class ToolContext
    {
        public ToolContext(ISwApplication app, ISwDocument doc, string threadId, ToolRegistry registry, IReadOnlyDictionary<string, string> refs = null)
        {
            Refs = refs ?? new Dictionary<string, string>();
            App = app;
            Doc = doc;
            ThreadId = threadId;
            Registry = registry;
        }

        public ISwApplication App { get; }

        public ISldWorks Sw => App?.Sw;

        /// <summary>The document this chat is bound to (or the active one for scratch chats). May be null.</summary>
        public ISwDocument Doc { get; }

        public IModelDoc2 Model => Doc?.Model;

        public string ThreadId { get; }

        /// <summary>The agent's tool_use id for this call, when it came from the agent (see UndoLedger).</summary>
        public string ToolUseId { get; set; }

        /// <summary>The tool or workflow whose run this call is part of (the panel's run, or a long run's follow-up), else null.</summary>
        public string RunTool { get; set; }

        /// <summary>Selection references from the chat ([S1] -> persistent-reference bytes, base64).</summary>
        public IReadOnlyDictionary<string, string> Refs { get; }

        public ToolRegistry Registry { get; }

        public IModelDoc2 RequireModel()
        {
            return Model ?? throw new ToolException("No SolidWorks document is open for this chat. Open or create a part, assembly or drawing first.");
        }
    }

    /// <summary>Thrown by tools for expected failures; reported to the model as a plain error message.</summary>
    public sealed class ToolException : System.Exception
    {
        public ToolException(string message) : base(message)
        {
        }
    }
}

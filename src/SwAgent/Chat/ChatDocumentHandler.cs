using System;
using SwAgent.Infrastructure;
using Xarial.XCad;
using Xarial.XCad.Documents;
using Xarial.XCad.Documents.Services;
using Xarial.XCad.SolidWorks.Documents;

namespace SwAgent.Chat
{
    /// <summary>
    /// Created by xCAD for every document SolidWorks loads (including hidden assembly components).
    /// Hooks third-party storage so the chat is read on open and written on every save.
    /// </summary>
    internal sealed class ChatDocumentHandler : IDocumentHandler
    {
        private IXDocument _doc;

        public ChatDocument Chat { get; private set; }

        public void Init(IXApplication app, IXDocument doc)
        {
            _doc = doc;
            Chat = new ChatDocument((ISwDocument)doc);
            doc.StreamReadAvailable += OnStreamRead;
            doc.StreamWriteAvailable += OnStreamWrite;
            doc.Selections.NewSelection += OnNewSelection;
            doc.Selections.ClearSelection += OnClearSelection;
            AgentAddIn.Instance?.Controller?.OnDocumentOpened(this);
        }

        private void OnStreamRead(IXDocument doc) => Chat.ReadFromFile(doc);

        private void OnStreamWrite(IXDocument doc)
        {
            Chat.WriteToFile(doc);
            AgentAddIn.Instance?.Controller?.OnDocumentSaved(this);
        }

        private void OnNewSelection(IXDocument doc, IXSelObject obj) => AgentAddIn.Instance?.Controller?.OnSelectionChanged(this);

        private void OnClearSelection(IXDocument doc) => AgentAddIn.Instance?.Controller?.OnSelectionChanged(this);

        public void Dispose()
        {
            try
            {
                if (_doc != null)
                {
                    _doc.StreamReadAvailable -= OnStreamRead;
                    _doc.StreamWriteAvailable -= OnStreamWrite;
                    _doc.Selections.NewSelection -= OnNewSelection;
                    _doc.Selections.ClearSelection -= OnClearSelection;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Detaching document events failed: " + ex.Message);
            }
            Chat?.OnClosed();
            AgentAddIn.Instance?.Controller?.OnDocumentClosed(this);
        }
    }
}

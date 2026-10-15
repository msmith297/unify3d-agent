**Nightly build - for testing.** Built from the development branch; expect rough edges. The automatic updater never offers nightly builds - install this one by hand, and install a regular release later to go back.

## New since v0.1.1

**Tools**
- Tools now have three styles: **Hard coded** (the macro does the job), **Agent in the loop** (the agent writes the code each run from the tool's skill) and **Hybrid** (the macro runs first, hands cases it can't handle to the agent, and the tool learns from them).
- A guided **New tool** screen (ribbon button, command search, Workflows menu).
- Tools get their own PropertyManager pages, live previews and four intelligence levels (Fast, Balanced, High, Max).
- Tool runs that need you jump to the question and bring the panel forward.

**Working with the agent**
- **3D overlays:** the agent can draw on your view - outlines, arrows, circles, dimensions and labels anchored to the model, faded where the model hides them.
- Follow-up messages queue while the agent works, with **Send now**.
- **Undo all** the agent's changes when you resend or edit a message.
- Shorter replies by default, with a Replies setting (Brief / Normal / Detailed).
- Answered questions fold away; dividers mark earlier sessions in the chat.
- The agent sees other views of the model off-screen without moving your camera.

**SolidWorks**
- **Save and restart** reopens your documents, views and unsent messages where you left off.
- A built-in SolidWorks knowledge base (multi-body features, rebuild pitfalls, configurations and more).
- Crash protection: a tool step that crashes SolidWorks is paused, and the agent can find and fix the cause.

using SwAgent.Infrastructure;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// The agent waits only for a command the user started (2026-10-20: the agent's own failed cut left SolidWorks reporting
    /// a command, and every later call was refused although nothing was open on screen).
    /// </summary>
    public class SwBusyGateTests
    {
        private static SwCommandState Sketch(string name) => new SwCommandState { Sketch = name };

        [Fact]
        public void State_the_agent_left_never_blocks_it()
        {
            var left = Sketch("Sketch6");
            Assert.True(left.Blocking);
            Assert.False(SwBusyGate.MustWait(Sketch("Sketch6"), left.Key));
        }

        [Fact]
        public void A_command_the_user_started_since_blocks()
        {
            var agentLeft = new SwCommandState().Key;
            Assert.True(SwBusyGate.MustWait(new SwCommandState { Id = 1234, Title = "Cut-Extrude", UiActive = true }, agentLeft));
            Assert.True(SwBusyGate.MustWait(Sketch("Sketch2"), agentLeft));
            Assert.True(SwBusyGate.MustWait(Sketch("Sketch7"), Sketch("Sketch6").Key));
        }

        [Fact]
        public void A_bare_command_id_or_an_unreadable_state_does_not_block()
        {
            Assert.False(new SwCommandState { Id = 3301 }.Blocking);
            Assert.True(new SwCommandState { Id = 3301 }.AnySignal); // timers still keep clear of it
            Assert.False(new SwCommandState { Error = "RPC failed" }.Blocking);
            Assert.False(SwBusyGate.MustWait(new SwCommandState { Id = 3301 }, null));
        }

        [Fact]
        public void The_refusal_says_what_is_open()
        {
            Assert.Equal("'Cut-Extrude' is open, sketch 'Sketch6' is open for editing, command 1234",
                new SwCommandState { Id = 1234, Title = "Cut-Extrude", UiActive = true, Sketch = "Sketch6" }.Describe());
            Assert.Equal("no command", new SwCommandState().Describe());
        }
    }
}

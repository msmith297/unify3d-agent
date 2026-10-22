using SwAgent.Infrastructure;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>The crash guard's note survives a "crash" (no dispose), keeps the outermost step, and is gone after a normal run.</summary>
    [Collection("Cells")]
    public class CrashGuardTests
    {
        [Fact]
        public void NormalRunLeavesNothing()
        {
            CrashGuard.Recover();
            using (CrashGuard.Enter("preview", "some-tool", "Some tool", "Preview(b);")) { }
            Assert.Null(CrashGuard.Recover());
        }

        [Fact]
        public void CrashLeavesTheOutermostStep()
        {
            CrashGuard.Recover();
            // A tool macro runs through the C# cell runner: the outer "macro" note must win.
            var outer = CrashGuard.Enter("macro", "parametric-block", "Parametric block", "var x = 1;");
            var inner = CrashGuard.Enter("cell", null, "Parametric block", "var x = 1;");
            // SolidWorks "dies" here: nothing is disposed. Next start:
            var note = CrashGuard.Recover();
            Assert.NotNull(note);
            Assert.Equal("macro", (string)note["kind"]);
            Assert.Equal("parametric-block", (string)note["tool"]);
            Assert.Equal("var x = 1;", (string)note["code"]);
            Assert.Null(CrashGuard.Recover()); // read once
            inner.Dispose();
            outer.Dispose();
        }
    }
}

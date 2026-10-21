using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// A SolidWorks API call that faults inside SolidWorks (an access violation in its native code) arrives as an
    /// AccessViolationException. .NET treats that as corrupted process state: ordinary catch blocks skip it, so it escapes
    /// to SolidWorks' message loop and SolidWorks closes - which is how mold_part's RayIntersections took SolidWorks down
    /// on 2026-10-20 (the dump showed the AccessViolationException passing straight through ToolRegistry's catch blocks).
    /// Run catches it here instead, notes that SolidWorks is no longer trustworthy, and turns it into a NativeFaultException
    /// that the caller handles like any other error. From then on the agent's model tools refuse to run (Tripped) until
    /// SolidWorks restarts: its memory may be damaged, so the user saves under a new name and restarts.
    /// </summary>
    internal static class NativeFault
    {
        /// <summary>What faulted and when ("mold_part at 22:11"), or null while SolidWorks has had no fault this session.</summary>
        public static string Tripped { get; private set; }

        public static string RestartAdvice =>
            $"SolidWorks hit an internal memory fault earlier ({Tripped}) and may be unstable. Nothing more was run on the model. " +
            "Ask the user to save their work with File > Save As under a new name (keep the original), then restart SolidWorks before continuing.";

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static T Run<T>(string what, Func<T> call)
        {
            try
            {
                return call();
            }
            catch (Exception ex)
            {
                var fault = Fault(ex);
                if (fault == null) throw;
                Record(what, fault);
                throw new NativeFaultException(what, fault);
            }
        }

        /// <summary>Note a fault caught elsewhere (Safe.Run): model tools are blocked until SolidWorks restarts.</summary>
        public static void Record(string what, Exception fault)
        {
            Tripped = $"{what} at {DateTime.Now:HH:mm}";
            Log.Error($"Native fault in {what} (SolidWorks may be unstable; model tools are blocked until restart)", fault);
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        public static void Run(string what, Action call) => Run<object>(what, () =>
        {
            call();
            return null;
        });

        /// <summary>The corrupted-state exception inside ex (reflection wraps it in TargetInvocationException), or null.</summary>
        internal static Exception Fault(Exception ex)
        {
            for (var e = ex; e != null; e = e is TargetInvocationException || e is AggregateException ? e.InnerException : null)
                if (e is AccessViolationException) return e;
            return null;
        }

        /// <summary>For tests: forget a fault.</summary>
        internal static void Reset() => Tripped = null;
    }

    /// <summary>An access violation inside a SolidWorks call, caught by NativeFault. Safe to catch and report.</summary>
    internal sealed class NativeFaultException : Exception
    {
        public NativeFaultException(string what, Exception inner)
            : base($"SolidWorks faulted inside {what} ({inner.Message}). {NativeFault.RestartAdvice}", inner)
        {
        }
    }
}

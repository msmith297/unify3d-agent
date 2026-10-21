using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SwAgent.Infrastructure;

namespace SwAgent.Plugins.Hooks
{
    /// <summary>
    /// A program a plugin runs: a hook command or an MCP server. They run outside SolidWorks and never touch it.
    ///
    /// Every one goes into the add-in's Job object (kill on close), so nothing outlives SolidWorks, not even after a crash;
    /// each also gets a job of its own, so a timeout or a disable kills its whole tree (on net48 Process.Kill ends only
    /// cmd.exe and leaves node and its PowerShell children running). Hook commands run through exactly one cmd.exe
    /// (/d /s /c: no AutoRun, quotes kept), so SolidWorks is the hook's grandparent; MCP servers that resolve to an .exe
    /// start directly, so SolidWorks is their parent (agent-bridge binds its hooks to its MCP peer by those pids).
    /// stdout and stderr are read as UTF-8 on their own threads, so a full pipe never stalls the program.
    /// </summary>
    internal sealed class PluginProcess : IDisposable
    {
        private IntPtr _job;
        private readonly OutputCapture _stdout;
        private readonly OutputCapture _stderr;
        private readonly TaskCompletionSource<bool> _exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _killed;

        private PluginProcess(Process process, bool captureStdout, int captureLimit)
        {
            Process = process;
            if (captureStdout) _stdout = new OutputCapture(process.StandardOutput, captureLimit, "stdout");
            _stderr = new OutputCapture(process.StandardError, captureLimit, "stderr");
        }

        public Process Process { get; }

        public int Id { get; private set; }

        /// <summary>Completes when the process has exited (not necessarily when its output is drained).</summary>
        public Task Exited => _exited.Task;

        public bool Killed => _killed != 0;

        /// <summary>What it wrote to stdout so far (null when the caller reads stdout itself, as the MCP client does).</summary>
        public string Stdout => _stdout?.Text;

        public string Stderr => _stderr.Text;

        public int? ExitCode
        {
            get
            {
                try
                {
                    return Process.HasExited ? Process.ExitCode : (int?)null;
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>A hook command line, run through one cmd.exe: cmd.exe /d /s /c "&lt;command&gt;".</summary>
        public static PluginProcess StartShell(string command, IDictionary<string, string> env, string cwd, int captureLimit = 1 << 20) =>
            Start(CmdExe, "/d /s /c \"" + command + "\"", env, cwd, true, captureLimit);

        /// <summary>
        /// A program and its arguments (an MCP server). An .exe starts directly; a .cmd/.bat (npx, ...) needs cmd.exe.
        /// `env` must hold PATH (ChildEnv.Base), which is where the command is looked up.
        /// </summary>
        public static PluginProcess StartProgram(string command, IEnumerable<string> args, IDictionary<string, string> env, string cwd, bool captureStdout)
        {
            env.TryGetValue("PATH", out var path);
            var resolved = ChildEnv.Resolve(command, path, cwd) ?? throw new FileNotFoundException($"'{command}' was not found on PATH.");
            var argLine = string.Join(" ", args.Select(QuoteArg));
            var ext = Path.GetExtension(resolved).ToLowerInvariant();
            if (ext == ".exe" || ext == ".com") return Start(resolved, argLine, env, cwd, captureStdout, 1 << 16);
            return Start(CmdExe, "/d /s /c \"" + QuoteArg(resolved) + (argLine.Length > 0 ? " " + argLine : "") + "\"", env, cwd, captureStdout, 1 << 16);
        }

        private static string CmdExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        private static PluginProcess Start(string file, string arguments, IDictionary<string, string> env, string cwd, bool captureStdout, int captureLimit)
        {
            var psi = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            psi.Environment.Clear();
            foreach (var kv in env)
            {
                if (kv.Value != null) psi.Environment[kv.Key] = kv.Value;
            }
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Start();
            var pp = new PluginProcess(p, captureStdout, captureLimit) { Id = p.Id };
            pp._job = Jobs.Adopt(p);
            p.Exited += (s, e) => pp._exited.TrySetResult(true);
            if (p.HasExited) pp._exited.TrySetResult(true);
            return pp;
        }

        /// <summary>Writes stdin as UTF-8 without a BOM and closes it (hook.mjs reads to EOF and JSON.parses it).</summary>
        public void WriteInputAndClose(string text)
        {
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(text ?? "");
                var stdin = Process.StandardInput.BaseStream;
                stdin.Write(bytes, 0, bytes.Length);
                stdin.Flush();
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
                // The program exited (or was killed) without reading its input.
            }
            finally
            {
                CloseInput();
            }
        }

        public void CloseInput()
        {
            try
            {
                Process.StandardInput.Close();
            }
            catch
            {
                // Already closed.
            }
        }

        /// <summary>Waits for the output to be read to the end, at most `max` (a detached grandchild can hold the pipe open).</summary>
        public Task DrainAsync(TimeSpan max)
        {
            var all = Task.WhenAll(new[] { _stdout?.Done, _stderr.Done }.Where(t => t != null));
            return Task.WhenAny(all, Task.Delay(max));
        }

        /// <summary>Ends the process and everything it started.</summary>
        public void Kill()
        {
            if (Interlocked.Exchange(ref _killed, 1) != 0) return;
            try
            {
                if (Process.HasExited && _job == IntPtr.Zero) return;
            }
            catch
            {
                // Fall through to the kill.
            }
            if (_job != IntPtr.Zero && Jobs.Terminate(_job)) return;
            // Not in a job of its own (assignment failed): kill the tree by pid.
            try
            {
                using (var kill = Process.Start(new ProcessStartInfo("taskkill.exe", $"/T /F /PID {Id}") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    kill?.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Killing plugin process {Id} failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            var job = Interlocked.Exchange(ref _job, IntPtr.Zero);
            if (job != IntPtr.Zero) Jobs.Close(job);
            try
            {
                Process.Dispose();
            }
            catch
            {
                // Best effort.
            }
        }

        /// <summary>One argument quoted the way CommandLineToArgvW (and node) splits it again.</summary>
        internal static string QuoteArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            if (arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            for (var i = 0; i < arg.Length; i++)
            {
                var backslashes = 0;
                while (i < arg.Length && arg[i] == '\\')
                {
                    backslashes++;
                    i++;
                }
                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
                else sb.Append('\\', backslashes).Append(arg[i]);
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Reads one output stream on its own thread, keeping the first `limit` characters and draining the rest.</summary>
        private sealed class OutputCapture
        {
            private readonly StringBuilder _text = new StringBuilder();
            private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public OutputCapture(StreamReader reader, int limit, string name)
            {
                new Thread(() =>
                {
                    var buffer = new char[4096];
                    try
                    {
                        int n;
                        while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            lock (_text)
                            {
                                if (_text.Length < limit) _text.Append(buffer, 0, Math.Min(n, limit - _text.Length));
                            }
                        }
                    }
                    catch
                    {
                        // The process was killed or the pipe closed.
                    }
                    _done.TrySetResult(true);
                }, 256 * 1024) { IsBackground = true, Name = "plugin-" + name }.Start();
            }

            public Task Done => _done.Task;

            public string Text
            {
                get
                {
                    lock (_text) return _text.ToString();
                }
            }
        }
    }

    /// <summary>The environment plugin programs start with, and where their commands are found.</summary>
    internal static class ChildEnv
    {
        /// <summary>
        /// This process's environment without secrets or another agent's session markers (a SolidWorks started from a
        /// Claude Code terminal must not hand its session to plugins), and with PATH re-read from the registry: SolidWorks'
        /// PATH is stale after the user installs Node while it runs.
        /// </summary>
        public static Dictionary<string, string> Base()
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            {
                var key = (string)e.Key;
                if (Hidden(key)) continue;
                env[key] = (string)e.Value;
            }
            env["PATH"] = FreshPath();
            return env;
        }

        private static bool Hidden(string key) =>
            key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
            || key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("CLAUDE_PLUGIN_", StringComparison.OrdinalIgnoreCase)
            || key.Equals("CLAUDE_PROJECT_DIR", StringComparison.OrdinalIgnoreCase)
            || key.Equals("SWAGENT_DATA_DIR", StringComparison.OrdinalIgnoreCase);

        /// <summary>The process PATH plus the machine and user PATH from the registry, without duplicates.</summary>
        public static string FreshPath()
        {
            var parts = new List<string>();
            foreach (var source in new[]
                     {
                         Environment.GetEnvironmentVariable("PATH"),
                         Read(EnvironmentVariableTarget.Machine),
                         Read(EnvironmentVariableTarget.User),
                     })
            {
                foreach (var dir in (source ?? "").Split(';'))
                {
                    var d = Environment.ExpandEnvironmentVariables(dir.Trim());
                    if (d.Length > 0 && !parts.Contains(d, StringComparer.OrdinalIgnoreCase)) parts.Add(d);
                }
            }
            return string.Join(";", parts);
        }

        private static string Read(EnvironmentVariableTarget target)
        {
            try
            {
                return Environment.GetEnvironmentVariable("PATH", target);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The full path of a command as cmd.exe would find it (PATHEXT), or null.</summary>
        public static string Resolve(string command, string path, string cwd)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = command.Trim().Trim('"');
            var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';').Where(e => e.Length > 0).ToList();
            IEnumerable<string> Candidates(string baseName) =>
                Path.HasExtension(baseName) ? new[] { baseName }.Concat(exts.Select(e => baseName + e)) : exts.Select(e => baseName + e);
            try
            {
                if (command.IndexOfAny(new[] { '\\', '/' }) >= 0 || Path.IsPathRooted(command))
                {
                    var full = Path.IsPathRooted(command) ? command : Path.Combine(cwd ?? "", command);
                    return Candidates(full).FirstOrDefault(File.Exists);
                }
                foreach (var dir in (path ?? "").Split(';').Where(d => d.Length > 0))
                {
                    var hit = Candidates(Path.Combine(dir, command)).FirstOrDefault(File.Exists);
                    if (hit != null) return hit;
                }
            }
            catch (ArgumentException)
            {
                // Illegal characters in a PATH entry or the command.
            }
            return null;
        }

        private static readonly Regex Placeholder = new Regex(@"\$\{([^}]+)\}", RegexOptions.Compiled);

        /// <summary>
        /// Replaces every ${NAME} with the variable's value (case-insensitive; unknown names become empty). cmd.exe doesn't
        /// expand ${...}, so the runtime does it before anything runs.
        /// </summary>
        public static string Expand(string text, IDictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("${", StringComparison.Ordinal) < 0) return text;
            return Placeholder.Replace(text, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : "");
        }
    }

    /// <summary>Win32 Job objects: the add-in's (kill on close) and one per process, nested in it.</summary>
    internal static class Jobs
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;

        private static readonly Lazy<IntPtr> AddInJob = new Lazy<IntPtr>(() =>
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = KillOnJobClose;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(info)))
                Log.Warn("Setting up the plugin process job failed: " + new Win32Exception().Message);
            return job; // never closed: closing it is what kills the plugin processes when SolidWorks exits
        });

        /// <summary>Puts a process in the add-in job and in a job of its own; returns that job (or zero when that failed).</summary>
        public static IntPtr Adopt(Process p)
        {
            IntPtr handle;
            try
            {
                handle = p.Handle;
            }
            catch
            {
                return IntPtr.Zero; // already gone
            }
            if (AddInJob.Value != IntPtr.Zero && !AssignProcessToJobObject(AddInJob.Value, handle))
                Log.Warn($"Plugin process {p.Id}: not in the add-in job ({new Win32Exception().Message})");
            var own = CreateJobObject(IntPtr.Zero, null);
            if (own == IntPtr.Zero) return IntPtr.Zero;
            if (AssignProcessToJobObject(own, handle)) return own;
            Log.Warn($"Plugin process {p.Id}: no job of its own ({new Win32Exception().Message}); killed by process tree instead");
            CloseHandle(own);
            return IntPtr.Zero;
        }

        public static bool Terminate(IntPtr job) => TerminateJobObject(job, 1);

        /// <summary>Kills every plugin process (SessionEnd, after the polite shutdown).</summary>
        public static void TerminateAll()
        {
            if (AddInJob.IsValueCreated && AddInJob.Value != IntPtr.Zero) TerminateJobObject(AddInJob.Value, 1);
        }

        public static void Close(IntPtr job) => CloseHandle(job);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

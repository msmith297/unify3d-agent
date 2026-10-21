using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SwAgent.Infrastructure;

namespace SwAgent.Plugins
{
    /// <summary>
    /// Finds programs (node, git) the way a new console would: SolidWorks' own PATH is the one it started with, so a Node
    /// or Git installed since then is only on the PATH stored in the registry (machine + user). Also the runtime's way to
    /// turn a hook's or MCP server's "node" into node.exe, so MCP servers start without a cmd.exe in between.
    /// </summary>
    internal static class ExecutableResolver
    {
        /// <summary>The PATH a new process would get: the registry's machine and user values, then this process's.</summary>
        public static string CurrentPath()
        {
            var parts = new List<string>();
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
                    parts.Add(k?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                using (var k = Registry.CurrentUser.OpenSubKey("Environment"))
                    parts.Add(k?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
            }
            catch (Exception ex)
            {
                Log.Warn("Reading PATH from the registry failed: " + ex.Message);
            }
            parts.Add(Environment.GetEnvironmentVariable("PATH"));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new List<string>();
            foreach (var d in parts.Where(p => !string.IsNullOrEmpty(p)).SelectMany(p => p.Split(';')))
            {
                var dir = Environment.ExpandEnvironmentVariables(d.Trim().Trim('"'));
                if (dir.Length > 0 && seen.Add(dir)) dirs.Add(dir);
            }
            return string.Join(";", dirs);
        }

        /// <summary>The full path of a command ("node", "git", "node.exe" or a path), searching CurrentPath() with PATHEXT; null if none.</summary>
        public static string Find(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = command.Trim().Trim('"');
            var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';').Where(e => e.Length > 0).ToList();
            IEnumerable<string> Candidates(string basePath) =>
                Path.HasExtension(basePath) ? new[] { basePath } : exts.Select(e => basePath + e.ToLowerInvariant());
            try
            {
                if (Path.IsPathRooted(command) || command.Contains("\\") || command.Contains("/"))
                    return Candidates(Path.GetFullPath(command)).FirstOrDefault(File.Exists);
                foreach (var dir in CurrentPath().Split(';'))
                {
                    try
                    {
                        var hit = Candidates(Path.Combine(dir, command)).FirstOrDefault(File.Exists);
                        if (hit != null) return hit;
                    }
                    catch (ArgumentException)
                    {
                        // A PATH entry with invalid characters.
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Looking for {command} failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>Tests: a fixed node version (null = run node --version).</summary>
        internal static Func<Version> NodeVersionOverride;

        /// <summary>The installed Node.js version (node --version), or null when node isn't found.</summary>
        public static Version NodeVersion()
        {
            if (NodeVersionOverride != null) return NodeVersionOverride();
            var node = Find("node");
            if (node == null) return null;
            try
            {
                var psi = new ProcessStartInfo(node, "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using (var p = Process.Start(psi))
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(10000))
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch
                        {
                            // Already gone.
                        }
                        return null;
                    }
                    var m = Regex.Match(output.Result, @"v?(\d+)\.(\d+)\.(\d+)");
                    return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("node --version failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>The program a command line starts: its first token, unquoted ("node" in node "x/hook.mjs").</summary>
        public static string FirstToken(string commandLine)
        {
            var s = (commandLine ?? "").TrimStart();
            if (s.StartsWith("\""))
            {
                var end = s.IndexOf('"', 1);
                return end > 0 ? s.Substring(1, end - 1) : s.Trim('"');
            }
            var space = s.IndexOfAny(new[] { ' ', '\t' });
            return space < 0 ? s : s.Substring(0, space);
        }
    }
}

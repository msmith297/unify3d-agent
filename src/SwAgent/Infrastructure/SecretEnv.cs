using System;
using System.Collections.Specialized;
using System.Linq;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// The AI provider secrets in this process's environment (ANTHROPIC_API_KEY, OPENAI_API_KEY, OPENAI_ORG_ID, ...): hidden
    /// from every plugin, hook, MCP and git process, and ${...} of them expands to "" (docs/providers/design.md 7.9).
    /// </summary>
    internal static class SecretEnv
    {
        // Provider key prefixes: hidden from every child process, and ${...} of them expands to "".
        public static readonly string[] ProviderPrefixes = { "ANTHROPIC_", "OPENAI_" };

        public static bool IsProviderSecret(string name) =>
            !string.IsNullOrEmpty(name) && ProviderPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        /// <summary>Removes every provider secret from a ProcessStartInfo's environment.</summary>
        public static void Scrub(StringDictionary env)
        {
            foreach (var key in env.Keys.Cast<string>().Where(IsProviderSecret).ToList()) env.Remove(key);
        }
    }
}

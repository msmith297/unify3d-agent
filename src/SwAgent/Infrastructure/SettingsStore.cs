using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// UI preferences live in %APPDATA%\SwAgent\settings.json as an opaque JSON object owned by the UI.
    /// The Anthropic API key is stored separately, encrypted with Windows DPAPI (current user only).
    /// </summary>
    internal static class SettingsStore
    {
        private static string SettingsFile => Path.Combine(Paths.RoamingDir, "settings.json");
        private static string KeyFile => Path.Combine(Paths.RoamingDir, "apikey.bin");
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SwAgent.ApiKey.v1");

        public static JObject LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile)) return JObject.Parse(File.ReadAllText(SettingsFile));
            }
            catch (Exception ex)
            {
                Log.Error("Could not read settings.json; using defaults", ex);
            }
            return new JObject();
        }

        public static void SaveSettings(JObject settings)
        {
            File.WriteAllText(SettingsFile, settings.ToString(Formatting.Indented));
        }

        /// <summary>The stored key, else the ANTHROPIC_API_KEY environment variable, else null.</summary>
        public static string GetApiKey()
        {
            try
            {
                if (File.Exists(KeyFile))
                {
                    var plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Entropy, DataProtectionScope.CurrentUser);
                    var key = Encoding.UTF8.GetString(plain);
                    if (!string.IsNullOrWhiteSpace(key)) return key;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not decrypt stored API key", ex);
            }
            var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            return string.IsNullOrWhiteSpace(env) ? null : env;
        }

        public static string ApiKeySource()
        {
            if (File.Exists(KeyFile)) return "stored";
            return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ? "none" : "environment";
        }

        public static void SetApiKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                if (File.Exists(KeyFile)) File.Delete(KeyFile);
                return;
            }
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(KeyFile, cipher);
        }
    }
}

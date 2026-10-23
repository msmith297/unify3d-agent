using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwAgent.Infrastructure
{
    /// <summary>Where one AI provider's key is kept: its DPAPI file under %APPDATA%\SwAgent and the environment fallback.</summary>
    internal sealed class ProviderKeyInfo
    {
        public string Id, FileName, Entropy, EnvVar;
    }

    /// <summary>The providers the panel can call (docs/providers/design.md 7.5).</summary>
    internal static class ProviderKeys
    {
        // Adding a provider: one line here, one in SecretEnv.ProviderPrefixes, one adapter in the panel.
        public static readonly ProviderKeyInfo[] All =
        {
            new ProviderKeyInfo { Id = "anthropic", FileName = "apikey.bin", Entropy = "SwAgent.ApiKey.v1", EnvVar = "ANTHROPIC_API_KEY" },
            new ProviderKeyInfo { Id = "openai", FileName = "apikey-openai.bin", Entropy = "SwAgent.ApiKey.openai.v1", EnvVar = "OPENAI_API_KEY" },
        };

        /// <summary>null or "" is Anthropic (older panels send no provider); an unknown id throws.</summary>
        public static ProviderKeyInfo Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return All[0];
            return All.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException($"Unknown AI provider '{id}'.");
        }
    }

    /// <summary>
    /// UI preferences live in %APPDATA%\SwAgent\settings.json as an opaque JSON object owned by the UI.
    /// Each AI provider's API key is stored separately, encrypted with Windows DPAPI (current user only): apikey.bin is
    /// Anthropic's (the same file, entropy and format as before providers), apikey-openai.bin OpenAI's.
    /// </summary>
    internal static class SettingsStore
    {
        private static string SettingsFile => Path.Combine(Paths.RoamingDir, "settings.json");
        private static string KeyFile(ProviderKeyInfo p) => Path.Combine(Paths.RoamingDir, p.FileName);

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
            WriteAtomic(SettingsFile, Encoding.UTF8.GetBytes(settings.ToString(Formatting.Indented)));
        }

        /// <summary>The provider's stored key, else its environment variable (e.g. ANTHROPIC_API_KEY), else null.</summary>
        public static string GetApiKey(string provider = null)
        {
            var p = ProviderKeys.Get(provider);
            try
            {
                if (File.Exists(KeyFile(p)))
                {
                    var plain = ProtectedData.Unprotect(File.ReadAllBytes(KeyFile(p)), Encoding.UTF8.GetBytes(p.Entropy), DataProtectionScope.CurrentUser);
                    var key = Encoding.UTF8.GetString(plain);
                    if (!string.IsNullOrWhiteSpace(key)) return key;
                }
            }
            catch (Exception ex)
            {
                // The exception says why (wrong user, damaged file); it never holds the key.
                Log.Error($"Could not decrypt stored API key ({p.Id})", ex);
            }
            var env = Environment.GetEnvironmentVariable(p.EnvVar);
            return string.IsNullOrWhiteSpace(env) ? null : env;
        }

        /// <summary>"stored", "environment" or "none".</summary>
        public static string ApiKeySource(string provider = null)
        {
            var p = ProviderKeys.Get(provider);
            if (File.Exists(KeyFile(p))) return "stored";
            return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(p.EnvVar)) ? "none" : "environment";
        }

        /// <summary>Every provider's source: { "anthropic": "stored", "openai": "none" }.</summary>
        public static JObject ApiKeySources()
        {
            var o = new JObject();
            foreach (var p in ProviderKeys.All) o[p.Id] = ApiKeySource(p.Id);
            return o;
        }

        /// <summary>app.getState's key fields: apiKeySource (Anthropic's, which older panels read) and apiKeySources.</summary>
        public static JObject KeyState() => new JObject { ["apiKeySource"] = ApiKeySource(), ["apiKeySources"] = ApiKeySources() };

        /// <summary>Saves the provider's key; "" (or null) deletes only that provider's file.</summary>
        public static void SetApiKey(string provider, string key)
        {
            var p = ProviderKeys.Get(provider);
            var file = KeyFile(p);
            if (string.IsNullOrWhiteSpace(key))
            {
                if (File.Exists(file)) File.Delete(file);
                return;
            }
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Encoding.UTF8.GetBytes(p.Entropy), DataProtectionScope.CurrentUser);
            WriteAtomic(file, cipher);
        }

        /// <summary>apiKey.get { provider? }: { key }. Without a provider: Anthropic, so older panels keep working.</summary>
        public static JObject ApiKeyGetRpc(JToken p) => new JObject { ["key"] = GetApiKey((string)p?["provider"]) };

        /// <summary>apiKey.set { provider?, key }: { source, sources } (source: that provider's).</summary>
        public static JObject ApiKeySetRpc(JToken p)
        {
            var provider = (string)p?["provider"];
            SetApiKey(provider, (string)p?["key"]);
            return new JObject { ["source"] = ApiKeySource(provider), ["sources"] = ApiKeySources() };
        }

        /// <summary>A temp file next to the target, then AtomicFile.Replace; the temp is deleted when that fails.</summary>
        private static void WriteAtomic(string path, byte[] bytes)
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, bytes);
                AtomicFile.Replace(tmp, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
                catch
                {
                    // Best effort: a leftover temp file holds only ciphertext or settings.
                }
            }
        }
    }
}

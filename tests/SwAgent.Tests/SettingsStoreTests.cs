using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using Xunit;

namespace SwAgent.Tests
{
    /// <summary>
    /// One DPAPI key file per AI provider (docs/providers/design.md 7.5, 7.6), all under SWAGENT_DATA_DIR: apikey.bin stays
    /// the Anthropic key exactly as before, apikey-openai.bin has its own entropy, the RPCs default to Anthropic.
    /// </summary>
    [Collection("Plugins")] // with the plugin tests that set OPENAI_API_KEY / ANTHROPIC_API_KEY in this process
    public class SettingsStoreTests : IDisposable
    {
        private const string AnthropicKey = "test-anthropic-key-not-real-0001";
        private const string OpenAiKey = "test-openai-key-not-real-0002";
        private static readonly string[] EnvVars = { "ANTHROPIC_API_KEY", "OPENAI_API_KEY" };
        private readonly string[] _savedEnv = new string[EnvVars.Length];

        private static string Roaming => Paths.RoamingDir;
        private static string AnthropicFile => Path.Combine(Roaming, "apikey.bin");
        private static string OpenAiFile => Path.Combine(Roaming, "apikey-openai.bin");

        public SettingsStoreTests()
        {
            Assert.Contains("SwAgent.Tests.Data", Roaming); // never the user's real %APPDATA%\SwAgent
            for (var i = 0; i < EnvVars.Length; i++)
            {
                _savedEnv[i] = Environment.GetEnvironmentVariable(EnvVars[i]);
                Environment.SetEnvironmentVariable(EnvVars[i], null);
            }
            Clean();
        }

        public void Dispose()
        {
            Clean();
            for (var i = 0; i < EnvVars.Length; i++) Environment.SetEnvironmentVariable(EnvVars[i], _savedEnv[i]);
        }

        private static void Clean()
        {
            foreach (var f in new[] { AnthropicFile, OpenAiFile }) File.Delete(f);
        }

        [Fact]
        public void Each_provider_round_trips_its_own_key()
        {
            SettingsStore.SetApiKey("anthropic", AnthropicKey);
            SettingsStore.SetApiKey("openai", "  " + OpenAiKey + "\n");
            Assert.Equal(AnthropicKey, SettingsStore.GetApiKey());
            Assert.Equal(AnthropicKey, SettingsStore.GetApiKey("anthropic"));
            Assert.Equal(OpenAiKey, SettingsStore.GetApiKey("openai"));
            Assert.True(File.Exists(AnthropicFile));
            Assert.True(File.Exists(OpenAiFile));
            Assert.DoesNotContain(OpenAiKey, Encoding.UTF8.GetString(File.ReadAllBytes(OpenAiFile)));
            Assert.Empty(Directory.GetFiles(Roaming, "apikey*.tmp"));

            // Replacing a key goes through AtomicFile and leaves no temp file either.
            SettingsStore.SetApiKey("openai", OpenAiKey + "-b");
            Assert.Equal(OpenAiKey + "-b", SettingsStore.GetApiKey("openai"));
            Assert.Empty(Directory.GetFiles(Roaming, "apikey*.tmp"));
        }

        [Fact]
        public void An_apikey_bin_from_before_providers_reads_as_the_Anthropic_key()
        {
            // Exactly what the add-in wrote before providers: DPAPI CurrentUser with entropy SwAgent.ApiKey.v1.
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(AnthropicKey), Encoding.UTF8.GetBytes("SwAgent.ApiKey.v1"), DataProtectionScope.CurrentUser);
            File.WriteAllBytes(AnthropicFile, cipher);
            Assert.Equal(AnthropicKey, SettingsStore.GetApiKey());
            Assert.Equal("stored", SettingsStore.ApiKeySource());
            Assert.Null(SettingsStore.GetApiKey("openai"));

            // And the other way round: what the new add-in writes, the old one reads.
            SettingsStore.SetApiKey("anthropic", AnthropicKey + "-new");
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(AnthropicFile), Encoding.UTF8.GetBytes("SwAgent.ApiKey.v1"), DataProtectionScope.CurrentUser);
            Assert.Equal(AnthropicKey + "-new", Encoding.UTF8.GetString(plain));
        }

        [Fact]
        public void The_OpenAI_file_has_its_own_entropy()
        {
            SettingsStore.SetApiKey("openai", OpenAiKey);
            var bytes = File.ReadAllBytes(OpenAiFile);
            Assert.ThrowsAny<CryptographicException>(() =>
                ProtectedData.Unprotect(bytes, Encoding.UTF8.GetBytes("SwAgent.ApiKey.v1"), DataProtectionScope.CurrentUser));
            var plain = ProtectedData.Unprotect(bytes, Encoding.UTF8.GetBytes("SwAgent.ApiKey.openai.v1"), DataProtectionScope.CurrentUser);
            Assert.Equal(OpenAiKey, Encoding.UTF8.GetString(plain));
        }

        [Fact]
        public void An_empty_key_deletes_only_that_providers_file()
        {
            SettingsStore.SetApiKey("anthropic", AnthropicKey);
            SettingsStore.SetApiKey("openai", OpenAiKey);
            SettingsStore.SetApiKey("openai", "");
            Assert.False(File.Exists(OpenAiFile));
            Assert.True(File.Exists(AnthropicFile));
            Assert.Equal(AnthropicKey, SettingsStore.GetApiKey());
            SettingsStore.SetApiKey(null, "");
            Assert.False(File.Exists(AnthropicFile));
            SettingsStore.SetApiKey("openai", ""); // nothing to delete: no error
        }

        [Fact]
        public void Sources_report_stored_environment_or_none()
        {
            Assert.Equal("none", SettingsStore.ApiKeySource("anthropic"));
            Assert.Equal("none", SettingsStore.ApiKeySource("openai"));

            Environment.SetEnvironmentVariable("OPENAI_API_KEY", OpenAiKey);
            Assert.Equal("environment", SettingsStore.ApiKeySource("openai"));
            Assert.Equal(OpenAiKey, SettingsStore.GetApiKey("openai"));
            Assert.Equal("none", SettingsStore.ApiKeySource()); // Anthropic doesn't read OpenAI's variable

            SettingsStore.SetApiKey("openai", OpenAiKey + "-stored");
            Assert.Equal("stored", SettingsStore.ApiKeySource("openai")); // a saved key wins over the environment
            Assert.Equal(OpenAiKey + "-stored", SettingsStore.GetApiKey("openai"));

            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", AnthropicKey);
            var sources = SettingsStore.ApiKeySources();
            Assert.Equal("environment", (string)sources["anthropic"]);
            Assert.Equal("stored", (string)sources["openai"]);
            Assert.Equal(ProviderKeys.All.Length, sources.Count);
        }

        [Fact]
        public void An_unknown_provider_throws()
        {
            Assert.Throws<ArgumentException>(() => ProviderKeys.Get("mistral"));
            Assert.Throws<ArgumentException>(() => SettingsStore.GetApiKey("mistral"));
            Assert.Throws<ArgumentException>(() => SettingsStore.SetApiKey("mistral", "x"));
            Assert.Throws<ArgumentException>(() => SettingsStore.ApiKeyGetRpc(new JObject { ["provider"] = "mistral" }));
            Assert.Equal("anthropic", ProviderKeys.Get(null).Id);
            Assert.Equal("anthropic", ProviderKeys.Get("").Id);
        }

        [Fact]
        public void Rpcs_default_to_Anthropic_and_getState_has_both_fields()
        {
            SettingsStore.SetApiKey("anthropic", AnthropicKey);
            Assert.Equal(AnthropicKey, (string)SettingsStore.ApiKeyGetRpc(null)["key"]);
            Assert.Equal(AnthropicKey, (string)SettingsStore.ApiKeyGetRpc(new JObject())["key"]);
            Assert.Null((string)SettingsStore.ApiKeyGetRpc(new JObject { ["provider"] = "openai" })["key"]);

            var set = SettingsStore.ApiKeySetRpc(new JObject { ["provider"] = "openai", ["key"] = OpenAiKey });
            Assert.Equal("stored", (string)set["source"]);
            Assert.Equal("stored", (string)set["sources"]["anthropic"]);
            Assert.Equal("stored", (string)set["sources"]["openai"]);
            Assert.Equal(OpenAiKey, (string)SettingsStore.ApiKeyGetRpc(new JObject { ["provider"] = "openai" })["key"]);
            Assert.DoesNotContain(OpenAiKey, set.ToString());

            // An older panel: { key } only, which is Anthropic's.
            var old = SettingsStore.ApiKeySetRpc(new JObject { ["key"] = "" });
            Assert.Equal("none", (string)old["source"]);
            Assert.Equal("stored", (string)old["sources"]["openai"]);

            var state = SettingsStore.KeyState();
            Assert.Equal("none", (string)state["apiKeySource"]);
            Assert.Equal("none", (string)state["apiKeySources"]["anthropic"]);
            Assert.Equal("stored", (string)state["apiKeySources"]["openai"]);
        }

        [Fact]
        public void Settings_are_written_atomically()
        {
            var file = Path.Combine(Roaming, "settings.json");
            var before = File.Exists(file) ? File.ReadAllText(file) : null;
            try
            {
                SettingsStore.SaveSettings(new JObject { ["defaultModel"] = "gpt-6.1-sol" });
                Assert.Equal("gpt-6.1-sol", (string)SettingsStore.LoadSettings()["defaultModel"]);
                Assert.Empty(Directory.GetFiles(Roaming, "settings.json*.tmp"));
            }
            finally
            {
                if (before == null) File.Delete(file);
                else File.WriteAllText(file, before);
            }
        }

        [Fact]
        public void Keys_never_reach_the_log()
        {
            SettingsStore.SetApiKey("anthropic", AnthropicKey);
            SettingsStore.SetApiKey("openai", OpenAiKey);
            File.WriteAllBytes(OpenAiFile, new byte[] { 1, 2, 3, 4 }); // damaged: decrypting it fails and is logged
            Assert.Null(SettingsStore.GetApiKey("openai"));
            Assert.Equal(AnthropicKey, SettingsStore.GetApiKey());

            var log = ReadShared(Log.CurrentFile);
            Assert.Contains("Could not decrypt stored API key (openai)", log);
            Assert.DoesNotContain(AnthropicKey, log);
            Assert.DoesNotContain(OpenAiKey, log);
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
            {
                return reader.ReadToEnd();
            }
        }
    }
}

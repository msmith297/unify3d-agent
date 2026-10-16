using System;
using System.Linq;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Voice
{
    /// <summary>
    /// Reads the agent's replies aloud with the voices built into Windows (offline, free). Speaking stops as soon as the
    /// user starts talking again or presses Stop.
    /// </summary>
    internal sealed class Speaker : IDisposable
    {
        private SpeechSynthesizer _synth;

        public bool Speaking => _synth != null && _synth.State == SynthesizerState.Speaking;

        public static JArray Voices()
        {
            try
            {
                using (var s = new SpeechSynthesizer())
                {
                    return new JArray(s.GetInstalledVoices().Where(v => v.Enabled).Select(v => new JObject
                    {
                        ["name"] = v.VoiceInfo.Name,
                        ["culture"] = v.VoiceInfo.Culture?.Name,
                        ["gender"] = v.VoiceInfo.Gender.ToString().ToLowerInvariant(),
                    }));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Listing voices failed: " + ex.Message);
                return new JArray();
            }
        }

        public void Speak(string text, string voice, double rate)
        {
            Stop();
            var spoken = ForSpeech(text);
            if (spoken.Length == 0) return;
            try
            {
                if (_synth == null)
                {
                    _synth = new SpeechSynthesizer();
                    _synth.SetOutputToDefaultAudioDevice();
                }
                if (!string.IsNullOrEmpty(voice))
                {
                    try
                    {
                        _synth.SelectVoice(voice);
                    }
                    catch
                    {
                        // That voice was uninstalled: keep the default.
                    }
                }
                _synth.Rate = Math.Max(-10, Math.Min(10, (int)Math.Round(rate)));
                _synth.SpeakAsync(spoken);
            }
            catch (Exception ex)
            {
                Log.Warn("Speaking the reply failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            try
            {
                _synth?.SpeakAsyncCancelAll();
            }
            catch
            {
                // Nothing to stop.
            }
        }

        /// <summary>Markdown to something worth hearing: no code, tables, links' URLs or formatting symbols.</summary>
        internal static string ForSpeech(string markdown)
        {
            var t = markdown ?? "";
            t = Regex.Replace(t, @"```[\s\S]*?```", " (code shown in the panel) ");
            t = Regex.Replace(t, @"`([^`]*)`", "$1");
            t = Regex.Replace(t, @"!\[[^\]]*\]\([^)]*\)", "");
            t = Regex.Replace(t, @"\[([^\]]+)\]\([^)]*\)", "$1");
            t = Regex.Replace(t, @"(?m)^\s*\|.*\|\s*$", "");
            t = Regex.Replace(t, @"(?m)^\s{0,3}#{1,6}\s*", "");
            t = Regex.Replace(t, @"(?m)^\s*[-*+]\s+", "");
            t = Regex.Replace(t, @"(?m)^\s*>\s?", "");
            t = Regex.Replace(t, @"\*\*|__|\*|_(?=\S)|(?<=\S)_|~~", "");
            t = Regex.Replace(t, @"\[(S\d+)\]", "$1");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t.Length > 1500 ? t.Substring(0, 1500) + " ... the rest is in the panel." : t;
        }

        public void Dispose()
        {
            Stop();
            _synth?.Dispose();
            _synth = null;
        }
    }
}

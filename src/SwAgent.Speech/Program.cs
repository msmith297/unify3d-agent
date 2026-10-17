using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Whisper.net;

namespace SwAgent.Speech
{
    /// <summary>
    /// Push-to-talk speech-to-text, run by the add-in as a child process. One JSON object per line:
    ///
    ///   in:  {"op":"load","model":"...ggml-base.en.bin","threads":4}
    ///        {"op":"start","id":1,"prompt":"vocabulary..."}   mic on at once
    ///        {"op":"stop","id":1}                              mic off, finish the transcript
    ///        {"op":"cancel","id":1}                            mic off, drop it
    ///        {"op":"quit"}
    ///   out: {"type":"loaded"} {"type":"started","id":1} {"type":"level","id":1,"v":0.2}
    ///        {"type":"partial","id":1,"text":"...","segments":[{"t0":0,"t1":1200,"text":"..."}]}
    ///        {"type":"final","id":1,"text":"...","segments":[...]} {"type":"error","message":"..."} {"type":"log","message":"..."}
    ///
    /// Times are milliseconds from the moment recording started. Whisper isn't a streaming model, so the words
    /// appear by re-transcribing the speech since the last pause about once a second; at each pause that stretch is
    /// committed and the next one starts, so every pass stays short.
    /// </summary>
    internal static class Program
    {
        private const int Rate = 16000;
        private static readonly object Out = new object();
        private static WhisperFactory _factory;
        private static int _threads = Math.Max(2, Math.Min(8, Environment.ProcessorCount / 2));
        private static Recording _rec;

        private static int Main()
        {
            Console.InputEncoding = Encoding.UTF8;
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(stdout);
            // The add-in going away (SolidWorks closed or crashed) closes stdin: exit with it.
            string line;
            while ((line = Console.In.ReadLine()) != null)
            {
                JObject msg;
                try
                {
                    msg = JObject.Parse(line);
                }
                catch
                {
                    continue;
                }
                try
                {
                    switch ((string)msg["op"])
                    {
                        case "load":
                            Load((string)msg["model"], (int?)msg["threads"]);
                            break;
                        case "start":
                            Start((int)msg["id"], (string)msg["prompt"], null);
                            break;
                        case "file":
                            // Tests: stream a WAV file through the same pipeline, in real time, as if it were the microphone.
                            Start((int)msg["id"], (string)msg["prompt"], (string)msg["path"]);
                            break;
                        case "stop":
                            _rec?.Stop((int)msg["id"], cancel: false);
                            break;
                        case "cancel":
                            _rec?.Stop((int)msg["id"], cancel: true);
                            break;
                        case "quit":
                            return 0;
                    }
                }
                catch (Exception ex)
                {
                    Send(new JObject { ["type"] = "error", ["id"] = msg["id"], ["message"] = ex.Message });
                }
            }
            return 0;
        }

        internal static void Send(JObject o)
        {
            var text = o.ToString(Formatting.None);
            lock (Out) Console.Out.WriteLine(text);
        }

        internal static void LogLine(string message) => Send(new JObject { ["type"] = "log", ["message"] = message });

        private static void Load(string model, int? threads)
        {
            if (!File.Exists(model)) throw new FileNotFoundException("Speech model not found: " + model);
            if (threads > 0) _threads = threads.Value;
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(model);
            // Warm up: the first pass allocates the model's buffers; do it now rather than on the first word.
            using (var p = _factory.CreateBuilder().WithLanguage("en").WithThreads(_threads).Build())
            {
                foreach (var _ in Run(p, new float[Rate / 2])) { }
            }
            Send(new JObject { ["type"] = "loaded" });
        }

        private static void Start(int id, string prompt, string wavPath)
        {
            if (_factory == null) throw new InvalidOperationException("The speech model isn't loaded.");
            _rec?.Stop(_rec.Id, cancel: true);
            var builder = _factory.CreateBuilder().WithLanguage("en").WithThreads(_threads).WithNoContext();
            if (!string.IsNullOrWhiteSpace(prompt)) builder = builder.WithPrompt(prompt);
            _rec = new Recording(id, builder.Build());
            if (wavPath != null) _rec.BeginFile(wavPath);
            else _rec.Begin();
            Send(new JObject { ["type"] = "started", ["id"] = id });
        }

        internal static IEnumerable<SegmentData> Run(WhisperProcessor p, float[] samples)
        {
            var list = new List<SegmentData>();
            Task.Run(async () =>
            {
                await foreach (var s in p.ProcessAsync(samples)) list.Add(s);
            }).GetAwaiter().GetResult();
            return list;
        }

        /// <summary>One press of the talk key: microphone capture plus the transcription passes.</summary>
        private sealed class Recording
        {
            private readonly WhisperProcessor _processor;
            private readonly List<float> _samples = new List<float>();
            private readonly List<JObject> _committed = new List<JObject>();
            private readonly object _lock = new object();
            private WaveInEvent _wave;
            private int _committedAt; // sample index where the uncommitted stretch starts
            private volatile bool _stopping;
            private volatile bool _cancelled;
            private Thread _worker;
            private readonly AutoResetEvent _wake = new AutoResetEvent(false);
            private DateTime _lastLevel;

            public Recording(int id, WhisperProcessor processor)
            {
                Id = id;
                _processor = processor;
            }

            public int Id { get; }

            public void Begin()
            {
                _wave = new WaveInEvent { WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 50, NumberOfBuffers = 4 };
                try
                {
                    _wave.DeviceNumber = -1; // WAVE_MAPPER: follow the Windows default microphone
                    _wave.DataAvailable += OnData;
                    _wave.StartRecording();
                }
                catch
                {
                    _wave.Dispose();
                    _wave = new WaveInEvent { WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 50, NumberOfBuffers = 4, DeviceNumber = 0 };
                    _wave.DataAvailable += OnData;
                    _wave.StartRecording(); // throws "no microphone" style errors up to Start
                }
                _worker = new Thread(Work) { IsBackground = true, Name = "transcribe" };
                _worker.Start();
            }

            public void Stop(int id, bool cancel)
            {
                if (id != Id || _stopping) return;
                _cancelled = cancel;
                _stopping = true;
                try
                {
                    _wave?.StopRecording();
                }
                catch
                {
                    // Already stopped.
                }
                _wake.Set();
            }

            public void BeginFile(string path)
            {
                float[] all;
                using (var reader = new AudioFileReader16k(path)) all = reader.Samples;
                new Thread(() =>
                {
                    const int chunk = Rate / 20;
                    var buf = new byte[chunk * 2];
                    for (var i = 0; i < all.Length && !_stopping; i += chunk)
                    {
                        var n = Math.Min(chunk, all.Length - i);
                        for (var j = 0; j < n; j++)
                        {
                            var v = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, all[i + j] * 32767));
                            buf[j * 2] = (byte)v;
                            buf[j * 2 + 1] = (byte)(v >> 8);
                        }
                        OnData(this, new WaveInEventArgs(buf, n * 2));
                        Thread.Sleep(50);
                    }
                    Stop(Id, cancel: false);
                }) { IsBackground = true }.Start();
                _worker = new Thread(Work) { IsBackground = true, Name = "transcribe" };
                _worker.Start();
            }

            private void OnData(object sender, WaveInEventArgs e)
            {
                var n = e.BytesRecorded / 2;
                double sum = 0;
                lock (_lock)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var v = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                        _samples.Add(v);
                        sum += v * v;
                    }
                }
                if (n > 0 && (DateTime.UtcNow - _lastLevel).TotalMilliseconds > 90)
                {
                    _lastLevel = DateTime.UtcNow;
                    Send(new JObject { ["type"] = "level", ["id"] = Id, ["v"] = Math.Round(Math.Min(1, Math.Sqrt(sum / n) * 6), 3) });
                }
            }

            private void Work()
            {
                try
                {
                    while (!_stopping)
                    {
                        _wake.WaitOne(700);
                        if (_stopping) break;
                        Pass(final: false);
                    }
                    if (_cancelled) return;
                    Pass(final: true);
                    Send(new JObject { ["type"] = "final", ["id"] = Id, ["text"] = Text(_committed), ["segments"] = new JArray(_committed) });
                }
                catch (Exception ex)
                {
                    Send(new JObject { ["type"] = "error", ["id"] = Id, ["message"] = "Transcription failed: " + ex.Message });
                }
                finally
                {
                    try
                    {
                        _wave?.Dispose();
                    }
                    catch
                    {
                        // Ignore.
                    }
                    _processor.Dispose();
                }
            }

            /// <summary>Transcribe the speech since the last commit; commit it at a pause (or when it gets long).</summary>
            private void Pass(bool final)
            {
                float[] pending;
                int start;
                lock (_lock)
                {
                    start = _committedAt;
                    pending = _samples.Skip(start).ToArray();
                }
                if (pending.Length < Rate / 3) return;

                var (lastVoice, anyVoice) = Voice(pending);
                if (!anyVoice)
                {
                    // Silence so far: nothing to say, and long silence needn't be transcribed again and again.
                    if (pending.Length > 3 * Rate) Commit(start + pending.Length - Rate / 2, null, start);
                    return;
                }

                // A pause after the speech: everything up to it can be committed.
                var trailingSilence = pending.Length - lastVoice;
                var commitAll = final || trailingSilence > Rate * 6 / 10 && pending.Length > Rate * 2 || pending.Length > Rate * 24;
                var segments = Run(_processor, Pad(pending)).Where(s => Clean(s.Text).Length > 0).ToList();
                if (_cancelled) return;

                var json = segments.Select(s => new JObject
                {
                    ["t0"] = (int)(start * 1000L / Rate + s.Start.TotalMilliseconds),
                    ["t1"] = (int)(start * 1000L / Rate + Math.Min(s.End.TotalMilliseconds, pending.Length * 1000.0 / Rate)),
                    ["text"] = Clean(s.Text),
                }).ToList();

                if (commitAll)
                {
                    Commit(start + (final ? pending.Length : Math.Min(pending.Length, lastVoice + Rate / 4)), json, start);
                    if (!final) Partial(new List<JObject>());
                }
                else if (pending.Length > Rate * 12 && segments.Count > 1)
                {
                    // Long speech without a pause: commit all but the last segment (it may still be cut mid-word).
                    var keep = segments[segments.Count - 1];
                    Commit(start + (int)(keep.Start.TotalMilliseconds * Rate / 1000), json.Take(json.Count - 1).ToList(), start);
                    Partial(json.Skip(json.Count - 1).ToList());
                }
                else
                {
                    Partial(json);
                }
            }

            private void Commit(int until, List<JObject> segments, int from)
            {
                if (segments != null) _committed.AddRange(segments);
                lock (_lock) _committedAt = Math.Max(_committedAt, Math.Min(until, _samples.Count));
            }

            private void Partial(List<JObject> tentative)
            {
                var all = _committed.Concat(tentative).ToList();
                Send(new JObject { ["type"] = "partial", ["id"] = Id, ["text"] = Text(all), ["segments"] = new JArray(all) });
            }

            private static string Text(IEnumerable<JObject> segments) =>
                Regex.Replace(string.Join(" ", segments.Select(s => (string)s["text"])), @"\s+", " ").Trim();

            /// <summary>Whisper wants at least a second of audio; pad short clips with silence.</summary>
            private static float[] Pad(float[] a)
            {
                if (a.Length >= Rate + Rate / 10) return a;
                var b = new float[Rate + Rate / 10];
                Array.Copy(a, b, a.Length);
                return b;
            }

            /// <summary>Simple energy detector: where the last speech is, and whether there is any.</summary>
            private static (int LastVoice, bool Any) Voice(float[] a)
            {
                const int frame = Rate / 50; // 20 ms
                var energies = new List<double>();
                for (var i = 0; i + frame <= a.Length; i += frame)
                {
                    double s = 0;
                    for (var j = i; j < i + frame; j++) s += a[j] * a[j];
                    energies.Add(Math.Sqrt(s / frame));
                }
                if (energies.Count == 0) return (0, false);
                // Noise floor from the quietest fifth; speech is well above it (and above an absolute minimum).
                var sorted = energies.OrderBy(x => x).ToList();
                var floor = sorted[sorted.Count / 5];
                var threshold = Math.Max(0.012, floor * 3.2);
                var last = -1;
                var count = 0;
                for (var k = 0; k < energies.Count; k++)
                {
                    if (energies[k] <= threshold) continue;
                    last = k;
                    count++;
                }
                // At least ~120 ms of voiced frames, so a click or a bump of the desk isn't "speech".
                return count >= 6 ? ((last + 1) * frame, true) : (0, false);
            }

            /// <summary>Drop Whisper's non-speech tokens ("[BLANK_AUDIO]", "(keyboard clicking)") and stray whitespace.</summary>
            private static string Clean(string text)
            {
                var t = Regex.Replace(text ?? "", @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
                return Regex.Replace(t, @"\s+", " ").Trim();
            }
        }
    }

    /// <summary>A WAV file as 16 kHz mono samples (tests).</summary>
    internal sealed class AudioFileReader16k : IDisposable
    {
        public AudioFileReader16k(string path)
        {
            using (var reader = new WaveFileReader(path))
            {
                ISampleProvider sp = reader.ToSampleProvider();
                if (sp.WaveFormat.Channels == 2) sp = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(sp);
                if (sp.WaveFormat.SampleRate != 16000) sp = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(sp, 16000);
                var list = new List<float>();
                var buf = new float[16000];
                int n;
                while ((n = sp.Read(buf, 0, buf.Length)) > 0) list.AddRange(buf.Take(n));
                Samples = list.ToArray();
            }
        }

        public float[] Samples { get; }

        public void Dispose()
        {
        }
    }
}

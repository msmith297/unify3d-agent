using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SwAgent.Voice
{
    /// <summary>
    /// One voice message being recorded: what the user says (one or more holds of the talk key) and what they do while
    /// talking - selections, model changes, the view once it settles, markup frames - on one clock, so the agent can
    /// tell what "this face" or "here" meant. Times are milliseconds from the first key press.
    /// </summary>
    internal sealed class VoiceSession
    {
        private readonly List<JObject> _segments = new List<JObject>();
        private readonly List<JObject> _events = new List<JObject>();
        private readonly Dictionary<int, int> _offsets = new Dictionary<int, int>();
        private readonly Dictionary<int, JArray> _partials = new Dictionary<int, JArray>();

        public VoiceSession(string docKey, IntPtr viewHwnd, DateTime started)
        {
            DocKey = docKey;
            ViewHwnd = viewHwnd;
            Started = started;
        }

        public string DocKey { get; }
        public IntPtr ViewHwnd { get; }
        public DateTime Started { get; }

        /// <summary>Recordings still being transcribed.</summary>
        public HashSet<int> Pending { get; } = new HashSet<int>();

        /// <summary>When the last view image was kept, and whether anything happened in the model since.</summary>
        public int LastViewAt { get; private set; } = -1;
        public bool ChangedSinceView { get; private set; }
        public byte[] LastViewThumb { get; set; }

        public string LastSelectionSig { get; set; } = "";
        public string ActiveSketch { get; set; }

        public int Now => (int)(DateTime.Now - Started).TotalMilliseconds;

        public int At(DateTime t) => Math.Max(0, (int)(t - Started).TotalMilliseconds);

        public void AddRecording(int id, DateTime pressed)
        {
            _offsets[id] = At(pressed);
            Pending.Add(id);
        }

        public bool Owns(int id) => _offsets.ContainsKey(id);

        public void SetPartial(int id, JArray segments) => _partials[id] = Shift(id, segments);

        public void SetFinal(int id, JArray segments)
        {
            _partials.Remove(id);
            _segments.AddRange(Shift(id, segments).OfType<JObject>());
            Pending.Remove(id);
        }

        private JArray Shift(int id, JArray segments)
        {
            var offset = _offsets.TryGetValue(id, out var o) ? o : 0;
            return new JArray(segments.OfType<JObject>().Select(s => new JObject
            {
                ["t0"] = ((int?)s["t0"] ?? 0) + offset,
                ["t1"] = ((int?)s["t1"] ?? 0) + offset,
                ["text"] = (string)s["text"],
            }));
        }

        /// <summary>Everything said so far, including words still being recognised.</summary>
        public string Text => string.Join(" ", _segments.Concat(_partials.Values.SelectMany(p => p.OfType<JObject>()))
            .OrderBy(s => (int)s["t0"]).Select(s => ((string)s["text"] ?? "").Trim()).Where(s => s.Length > 0));

        public bool HasSpeech => _segments.Any(s => !string.IsNullOrWhiteSpace((string)s["text"]));

        public int ImageCount => _events.Count(e => e["image"] != null);

        /// <summary>Something worth sending without words: a markup, a new selection, a feature added, deleted or renamed.</summary>
        public bool HasActions => _events.Any(e =>
        {
            var kind = (string)e["kind"];
            return kind == "markup" || kind == "added" || kind == "deleted" || kind == "renamed" || (kind == "select" && (int)e["t"] > 0);
        });

        /// <summary>Add an event; returns a short summary for the panel's live strip.</summary>
        public JObject Add(string kind, string summary, JObject extra = null, int? at = null)
        {
            var e = new JObject { ["t"] = at ?? Now, ["kind"] = kind, ["summary"] = summary };
            if (extra != null)
            {
                foreach (var p in extra.Properties()) e[p.Name] = p.Value;
            }
            _events.Add(e);
            if (kind == "view")
            {
                LastViewAt = (int)e["t"];
                ChangedSinceView = false;
            }
            else if (kind != "markup")
            {
                ChangedSinceView = true;
            }
            return e;
        }

        /// <summary>
        /// The message for the panel. Images are capped (each one costs the agent tokens): every markup frame is kept,
        /// views are thinned to the first, the last and evenly spaced ones in between.
        /// </summary>
        public JObject ToJson(int maxImages = 10)
        {
            var events = _events.OrderBy(e => (int)e["t"]).ToList();
            var markups = events.Where(e => (string)e["kind"] == "markup").ToList();
            var views = events.Where(e => (string)e["kind"] == "view").ToList();
            var room = Math.Max(2, maxImages - markups.Count);
            if (views.Count > room)
            {
                var keep = new HashSet<JObject>();
                for (var i = 0; i < room; i++) keep.Add(views[(int)Math.Round(i * (views.Count - 1) / (double)(room - 1))]);
                events = events.Where(e => (string)e["kind"] != "view" || keep.Contains(e)).ToList();
            }
            return new JObject
            {
                ["docKey"] = DocKey,
                ["durationMs"] = Now,
                ["text"] = Text,
                ["segments"] = new JArray(_segments.OrderBy(s => (int)s["t0"])),
                ["events"] = new JArray(events),
            };
        }

        /// <summary>The live strip: every event's summary and time (no image data).</summary>
        public JArray Summaries() => new JArray(_events.OrderBy(e => (int)e["t"]).Select(e => new JObject
        {
            ["t"] = e["t"],
            ["kind"] = e["kind"],
            ["summary"] = e["summary"],
        }));
    }
}

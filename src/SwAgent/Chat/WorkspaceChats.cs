using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;

namespace SwAgent.Chat
{
    /// <summary>
    /// The user's chats, independent of documents: one list for the whole workspace, so a chat keeps going while the
    /// agent (or the user) opens, creates and switches files. Each thread records the files it worked in.
    ///
    /// Stored in %LOCALAPPDATA%\SwAgent\chats: one gzip'd JSON file per thread (so a new message rewrites one chat,
    /// not all of them) plus index.json (the active thread, chats imported from files, the revision). The panel is the
    /// only writer: it sends the whole list, and only threads whose content changed are written, off the UI thread.
    ///
    /// Chats that older versions saved inside part files are imported by the panel when a file is opened; chats from
    /// the old "no document" chat are imported here once.
    /// </summary>
    internal sealed class WorkspaceChats
    {
        public const string Key = "workspace";

        private readonly string _dir;
        private readonly Dictionary<string, string> _hashes = new Dictionary<string, string>();
        private Task _writes = Task.CompletedTask; // saves run one after another, in order, off the UI thread
        private JObject _index;
        private JObject _data;

        public WorkspaceChats(string dir = null)
        {
            _dir = dir ?? Path.Combine(Paths.LocalDir, "chats");
            Directory.CreateDirectory(ThreadDir);
        }

        private string ThreadDir => Path.Combine(_dir, "threads");
        private string IndexPath => Path.Combine(_dir, "index.json");

        public long Revision => (long?)Data["revision"] ?? 0;

        public JObject Data
        {
            get
            {
                if (_data == null) Load();
                return _data;
            }
        }

        private void Load()
        {
            _index = ReadIndex();
            var threads = new List<JObject>();
            foreach (var f in new DirectoryInfo(ThreadDir).GetFiles("*.json.gz"))
            {
                try
                {
                    var json = Decompress(File.ReadAllBytes(f.FullName));
                    var t = JObject.Parse(json);
                    threads.Add(t);
                    _hashes[(string)t["id"] ?? Path.GetFileNameWithoutExtension(f.Name)] = Hash(t.ToString(Formatting.None));
                }
                catch (Exception ex)
                {
                    Log.Error($"Chat {f.Name} is unreadable; leaving it on disk", ex);
                }
            }
            if ((bool?)_index["importedNoDocChat"] != true)
            {
                threads.AddRange(ImportNoDocChat(threads));
                _index["importedNoDocChat"] = true;
            }
            _data = new JObject
            {
                ["schema"] = ChatDocument.SchemaVersion,
                ["docId"] = Key,
                ["revision"] = (long?)_index["revision"] ?? 0,
                ["threads"] = new JArray(threads.OrderByDescending(t => (string)t["updatedAt"] ?? "")),
                ["activeThreadId"] = _index["activeThreadId"],
                ["imported"] = _index["imported"] ?? new JArray(),
            };
            Log.Info($"Loaded {threads.Count} chats from {_dir}");
        }

        /// <summary>The chat that older versions kept when no document was open (chat-backup\scratch.json.gz).</summary>
        private IEnumerable<JObject> ImportNoDocChat(List<JObject> existing)
        {
            var path = Path.Combine(Paths.ChatBackupDir, ChatDocument.ScratchKey + ".json.gz");
            if (!File.Exists(path)) return Enumerable.Empty<JObject>();
            try
            {
                var old = JObject.Parse(Decompress(File.ReadAllBytes(path)));
                var have = new HashSet<string>(existing.Select(t => (string)t["id"]));
                var imported = (old["threads"] as JArray ?? new JArray()).OfType<JObject>().Where(t => !have.Contains((string)t["id"])).ToList();
                foreach (var t in imported) WriteThread(t);
                Log.Info($"Imported {imported.Count} chats from the old no-document chat");
                return imported;
            }
            catch (Exception ex)
            {
                Log.Error("Importing the old no-document chat failed", ex);
                return Enumerable.Empty<JObject>();
            }
        }

        /// <summary>The panel's latest state: write the threads that changed, delete the ones it removed.</summary>
        public void Update(JObject incoming)
        {
            var data = Data;
            var threads = (incoming["threads"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            data["threads"] = new JArray(threads);
            data["activeThreadId"] = incoming["activeThreadId"];
            data["imported"] = incoming["imported"] ?? data["imported"] ?? new JArray();
            data["revision"] = Revision + 1;

            var changed = new List<(string Id, string Json)>();
            var ids = new HashSet<string>();
            foreach (var t in threads)
            {
                var id = (string)t["id"];
                if (string.IsNullOrEmpty(id)) continue;
                ids.Add(id);
                var json = t.ToString(Formatting.None);
                var hash = Hash(json);
                if (_hashes.TryGetValue(id, out var old) && old == hash) continue;
                _hashes[id] = hash;
                changed.Add((id, json));
            }
            var removed = _hashes.Keys.Where(k => !ids.Contains(k)).ToList();
            foreach (var id in removed) _hashes.Remove(id);

            _index = new JObject
            {
                ["revision"] = data["revision"],
                ["activeThreadId"] = data["activeThreadId"],
                ["imported"] = data["imported"],
                ["importedNoDocChat"] = true,
            };
            var index = _index.ToString(Formatting.Indented);

            // Off the UI thread, chained so the newest version of a thread always lands last.
            lock (this)
            {
                _writes = _writes.ContinueWith(_ =>
                {
                    try
                    {
                        foreach (var (id, json) in changed) WriteAtomic(ThreadPath(id), Compress(json));
                        foreach (var id in removed)
                        {
                            var p = ThreadPath(id);
                            if (File.Exists(p)) File.Delete(p);
                        }
                        WriteAtomic(IndexPath, Encoding.UTF8.GetBytes(index));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Saving chats failed", ex);
                    }
                }, TaskScheduler.Default);
            }
        }

        /// <summary>Wait for pending writes (shutdown, tests).</summary>
        public void Flush()
        {
            Task pending;
            lock (this) pending = _writes;
            pending.Wait(10000);
        }

        private void WriteThread(JObject t)
        {
            var id = (string)t["id"];
            if (string.IsNullOrEmpty(id)) return;
            var json = t.ToString(Formatting.None);
            _hashes[id] = Hash(json);
            WriteAtomic(ThreadPath(id), Compress(json));
        }

        private string ThreadPath(string id)
        {
            var safe = string.Join("_", id.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(ThreadDir, safe + ".json.gz");
        }

        private JObject ReadIndex()
        {
            try
            {
                if (File.Exists(IndexPath)) return JObject.Parse(File.ReadAllText(IndexPath));
            }
            catch (Exception ex)
            {
                Log.Error("Chat index is unreadable; rebuilding it", ex);
            }
            return new JObject();
        }

        private static void WriteAtomic(string path, byte[] bytes)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private static string Hash(string s)
        {
            using (var sha = SHA1.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
            }
        }

        private static byte[] Compress(string json)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                {
                    var bytes = Encoding.UTF8.GetBytes(json);
                    gz.Write(bytes, 0, bytes.Length);
                }
                return ms.ToArray();
            }
        }

        private static string Decompress(byte[] bytes)
        {
            using (var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }
}

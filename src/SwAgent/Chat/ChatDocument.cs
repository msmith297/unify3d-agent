using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwAgent.Infrastructure;
using Xarial.XCad.Data.Enums;
using Xarial.XCad.Documents;
using Xarial.XCad.SolidWorks.Documents;

namespace SwAgent.Chat
{
    /// <summary>
    /// The chat history of one open SolidWorks document.
    ///
    /// Persistence model:
    ///  * The history is stored INSIDE the .sldprt/.sldasm/.slddrw as a third-party storage stream
    ///    (gzip'd JSON). It travels with the file, so renames, moves, copies, Pack and Go and PDM
    ///    check-ins all keep it. Nothing is keyed by file path.
    ///  * The data carries a stable docId (GUID) and a monotonically increasing revision.
    ///  * Every change is also written to %LOCALAPPDATA%\SwAgent\chat-backup\{docId}.json.gz so a crash,
    ///    or closing without saving, never loses the conversation. On the next open, a backup with a
    ///    higher revision than the file wins (and the UI is told it was recovered).
    ///  * SolidWorks drops third-party streams that aren't re-written on save, so the stream is written
    ///    on EVERY save. If the stored bytes couldn't be parsed, they are written back unchanged.
    /// </summary>
    internal sealed class ChatDocument
    {
        public const string StreamName = "SwAgent.Chat.v1";
        public const int SchemaVersion = 1;

        /// <summary>Keep only this many most-recent images per thread when writing to the file.</summary>
        private const int MaxImagesPerThreadInFile = 2;

        private byte[] _unparsedFileBytes;   // stream bytes we could not parse: preserved verbatim
        private byte[] _fileBytes;           // stream bytes read on open, parsed lazily
        private JObject _data;
        private long _persistedRevision = -1;

        public const string ScratchKey = "scratch";

        public ChatDocument(ISwDocument doc)
        {
            Doc = doc;
            Key = Guid.NewGuid().ToString("N");
        }

        private ChatDocument()
        {
            Key = ScratchKey;
        }

        /// <summary>A chat not tied to any file (no document open). Persisted only in %LOCALAPPDATA%.</summary>
        public static ChatDocument CreateScratch() => new ChatDocument();

        public bool IsScratch => Key == ScratchKey;

        private string Title => Doc == null ? "(no document)" : SafeTitle(Doc);

        /// <summary>Runtime-only key used by the UI to address this open document.</summary>
        public string Key { get; }

        public ISwDocument Doc { get; }

        /// <summary>True when the last load restored newer messages from the local crash backup.</summary>
        public bool Recovered { get; private set; }

        public string DocId => (string)Data["docId"];

        public long Revision => (long?)Data["revision"] ?? 0;

        /// <summary>The chat payload. Parsed on first access (component documents never pay the cost).</summary>
        public JObject Data
        {
            get
            {
                if (_data == null) Materialize();
                return _data;
            }
        }

        /// <summary>Called by the document handler when SolidWorks offers the file's stored streams.</summary>
        public void ReadFromFile(IXDocument doc)
        {
            try
            {
                using (var stream = doc.TryOpenStream(StreamName, AccessType_e.Read))
                {
                    if (stream == null) return;
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        _fileBytes = ms.ToArray();
                    }
                }
                _data = null; // re-materialize from the fresh bytes on next access
            }
            catch (Exception ex)
            {
                Log.Error($"Reading chat stream from '{doc.Title}' failed", ex);
            }
        }

        /// <summary>Called during every save. Must always write, or SolidWorks discards the stream.</summary>
        public void WriteToFile(IXDocument doc)
        {
            try
            {
                if (_data == null && _fileBytes == null)
                {
                    return; // never had a chat, nothing to keep
                }

                byte[] bytes;
                long revision;
                var data = Data; // materializes from the file bytes if needed
                if (_unparsedFileBytes != null && !HasThreads(data))
                {
                    // Unreadable (or newer-schema) stream and no new chat: write it back untouched.
                    bytes = _unparsedFileBytes;
                    revision = -1;
                }
                else if (!HasThreads(data) && _fileBytes == null)
                {
                    return; // an empty chat isn't worth a stream
                }
                else
                {
                    var forFile = (JObject)data.DeepClone();
                    StripOldImages(forFile);
                    bytes = Compress(forFile.ToString(Formatting.None));
                    revision = Revision;
                }

                using (var stream = doc.OpenStream(StreamName, AccessType_e.Write))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
                _persistedRevision = revision;
                Log.Info($"Saved chat into '{doc.Title}' ({bytes.Length:N0} bytes, revision {revision})");
            }
            catch (Exception ex)
            {
                Log.Error($"Writing chat stream into '{doc.Title}' failed", ex);
            }
        }

        /// <summary>Replace the chat payload with the UI's latest state. Bumps the revision and backs up.</summary>
        public void Update(JObject incoming)
        {
            var data = Data;
            data["threads"] = incoming["threads"] ?? new JArray();
            data["activeThreadId"] = incoming["activeThreadId"];
            data["revision"] = Revision + 1;
            data["updatedAt"] = DateTime.UtcNow.ToString("o");
            Recovered = false;
            WriteBackup();
        }

        /// <summary>Called when the document closes. Keep the backup only if it holds unsaved messages.</summary>
        public void OnClosed()
        {
            try
            {
                if (_data == null || IsScratch) return;
                if (Revision <= _persistedRevision || !HasThreads(_data))
                {
                    DeleteBackup();
                }
                else
                {
                    Log.Info($"'{Title}' closed with unsaved chat (revision {Revision}); keeping crash backup");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Chat close bookkeeping failed", ex);
            }
        }

        private void Materialize()
        {
            JObject fromFile = null;
            if (_fileBytes != null)
            {
                try
                {
                    fromFile = JObject.Parse(Decompress(_fileBytes));
                    if (((int?)fromFile["schema"] ?? 0) > SchemaVersion)
                    {
                        throw new InvalidDataException($"Chat schema {fromFile["schema"]} is newer than this add-in supports");
                    }
                    _persistedRevision = (long?)fromFile["revision"] ?? 0;
                }
                catch (Exception ex)
                {
                    Log.Error($"Chat stream in '{Title}' is unreadable; preserving it untouched", ex);
                    _unparsedFileBytes = _fileBytes;
                    fromFile = null;
                }
            }

            _data = fromFile ?? NewData(IsScratch ? ScratchKey : Guid.NewGuid().ToString());

            // A local backup with a newer revision means messages that never made it into the file.
            var backup = ReadBackup((string)_data["docId"]);
            if (backup != null && ((long?)backup["revision"] ?? 0) > ((long?)_data["revision"] ?? 0))
            {
                _data = backup;
                _unparsedFileBytes = null;
                Recovered = !IsScratch; // the scratch chat lives only in the backup, nothing to announce

                Log.Info($"Recovered unsaved chat for '{Title}' from local backup (revision {Revision})");
            }
        }

        private static JObject NewData(string docId) => new JObject
        {
            ["schema"] = SchemaVersion,
            ["docId"] = docId,
            ["revision"] = 0,
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            ["threads"] = new JArray(),
            ["activeThreadId"] = null,
        };

        private static bool HasThreads(JObject data) => data["threads"] is JArray a && a.Count > 0;

        /// <summary>
        /// Screenshots dominate the size. The file keeps the newest few per thread; older ones become a
        /// short text placeholder. (The live session keeps them all; the API tolerates the edited history
        /// because requests set thinking.block_binding.prefix_mismatch_behavior = "drop_block".)
        /// </summary>
        private static void StripOldImages(JObject data)
        {
            if (!(data["threads"] is JArray threads)) return;
            foreach (var thread in threads.OfType<JObject>())
            {
                var images = thread.SelectTokens("$.messages[*].content[*]")
                    .SelectMany<JToken, JObject>(block => (string)block["type"] == "tool_result" && block["content"] is JArray inner
                        ? (IEnumerable<JObject>)inner.Children<JObject>()
                        : new[] { block as JObject })
                    .Where(b => b != null && (string)b["type"] == "image")
                    .ToList();
                foreach (var img in images.Take(Math.Max(0, images.Count - MaxImagesPerThreadInFile)))
                {
                    img.Replace(new JObject { ["type"] = "text", ["text"] = "[Image omitted from saved history]" });
                }
            }
        }

        private string BackupPath(string docId) => Path.Combine(Paths.ChatBackupDir, docId + ".json.gz");

        private void WriteBackup()
        {
            try
            {
                File.WriteAllBytes(BackupPath(DocId), Compress(Data.ToString(Formatting.None)));
            }
            catch (Exception ex)
            {
                Log.Error("Writing chat backup failed", ex);
            }
        }

        private JObject ReadBackup(string docId)
        {
            try
            {
                var path = BackupPath(docId);
                return File.Exists(path) ? JObject.Parse(Decompress(File.ReadAllBytes(path))) : null;
            }
            catch (Exception ex)
            {
                Log.Error("Reading chat backup failed", ex);
                return null;
            }
        }

        private void DeleteBackup()
        {
            var path = BackupPath(DocId);
            if (File.Exists(path)) File.Delete(path);
        }

        private static string SafeTitle(ISwDocument doc)
        {
            try
            {
                return doc.Title;
            }
            catch
            {
                return "(closed document)";
            }
        }

        /// <summary>Remove crash backups nobody has claimed in 60 days.</summary>
        public static void PruneBackups()
        {
            try
            {
                foreach (var f in new DirectoryInfo(Paths.ChatBackupDir).GetFiles("*.json.gz")
                             .Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-60)))
                {
                    f.Delete();
                }
            }
            catch
            {
            }
        }

        private static byte[] Compress(string json)
        {
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
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

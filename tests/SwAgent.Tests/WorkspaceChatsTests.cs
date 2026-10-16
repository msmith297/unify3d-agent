using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwAgent.Chat;
using Xunit;

namespace SwAgent.Tests
{
    public class WorkspaceChatsTests
    {
        private static JObject Thread(string id, string text, string updated) => new JObject
        {
            ["id"] = id,
            ["title"] = text,
            ["updatedAt"] = updated,
            ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = text }),
            ["files"] = new JArray(new JObject { ["title"] = "Part1.SLDPRT" }),
        };

        [Fact]
        public void Chats_survive_a_restart_one_file_each_and_deleted_ones_go()
        {
            var dir = Path.Combine(Path.GetTempPath(), "u3d-chats-" + Guid.NewGuid().ToString("N"));
            try
            {
                var a = new WorkspaceChats(dir);
                Assert.Empty((JArray)a.Data["threads"]);
                a.Update(new JObject
                {
                    ["threads"] = new JArray(Thread("t1", "mouse", "2026-10-16T01:00:00Z"), Thread("t2", "bracket", "2026-10-16T02:00:00Z")),
                    ["activeThreadId"] = "t2",
                    ["imported"] = new JArray("old1"),
                });
                a.Flush();
                Assert.Equal(2, Directory.GetFiles(Path.Combine(dir, "threads"), "*.json.gz").Length);

                var b = new WorkspaceChats(dir);
                var threads = ((JArray)b.Data["threads"]).Cast<JObject>().ToList();
                Assert.Equal(new[] { "t2", "t1" }, threads.Select(t => (string)t["id"])); // newest first
                Assert.Equal("t2", (string)b.Data["activeThreadId"]);
                Assert.Equal("old1", (string)((JArray)b.Data["imported"])[0]);
                Assert.Equal("Part1.SLDPRT", (string)threads[1]["files"][0]["title"]);

                // Only the changed chat is rewritten; a removed chat's file is deleted.
                var t1File = Directory.GetFiles(Path.Combine(dir, "threads"), "t1.json.gz").Single();
                var stamp = File.GetLastWriteTimeUtc(t1File);
                System.Threading.Thread.Sleep(30);
                b.Update(new JObject { ["threads"] = new JArray(Thread("t1", "mouse", "2026-10-16T01:00:00Z")), ["activeThreadId"] = "t1" });
                b.Flush();
                Assert.Equal(stamp, File.GetLastWriteTimeUtc(t1File));
                Assert.False(File.Exists(Path.Combine(dir, "threads", "t2.json.gz")));
                Assert.Single((JArray)new WorkspaceChats(dir).Data["threads"]);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}

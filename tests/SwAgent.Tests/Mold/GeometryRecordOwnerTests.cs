using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SwAgent.Mold.Geometry;
using Xunit;

namespace SwAgent.Tests.Mold
{
    /// <summary>
    /// A copy of a mold (Save As, or Smart Rebuild's working copy) carries the mold's custom properties; it must not take
    /// over the original's record (critique B2, review). Renamed or moved molds keep theirs.
    /// </summary>
    public class GeometryRecordOwnerTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "swagent-owner-" + Guid.NewGuid().ToString("N"));

        public GeometryRecordOwnerTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch
            {
                // a temp folder
            }
        }

        private string Record(string folderName, JToken moldPart)
        {
            var folder = Directory.CreateDirectory(Path.Combine(_dir, folderName)).FullName;
            var j = new JObject { ["id"] = "x" };
            if (moldPart != null) j["files"] = new JObject { ["moldPart"] = moldPart };
            File.WriteAllText(Path.Combine(folder, "mold.json"), j.ToString());
            return folder;
        }

        private string Part(string name)
        {
            var p = Path.Combine(_dir, name);
            File.WriteAllText(p, "");
            return p;
        }

        [Fact]
        public void The_part_named_in_the_record_owns_it_and_a_copy_does_not()
        {
            var original = Part("Mold A.SLDPRT");
            var copy = Part("Mold A - rebuilt.SLDPRT");
            var folder = Record("Mold A.mold", original);
            Assert.True(RecordOwner.Owns(folder, original));
            Assert.False(RecordOwner.Owns(folder, copy));
            // {path} form and a relative path resolve the same way.
            var folder2 = Record("Mold B.mold", new JObject { ["path"] = "Mold A.SLDPRT" });
            Assert.Equal(Path.GetFullPath(original), RecordOwner.OwnerOf(folder2));
        }

        [Fact]
        public void A_renamed_or_moved_mold_keeps_its_record()
        {
            // Renamed: the part the record names is gone.
            var renamed = Part("Mold C new name.SLDPRT");
            var folder = Record("Mold C.mold", Path.Combine(_dir, "Mold C.SLDPRT"));
            Assert.True(RecordOwner.Owns(folder, renamed));
            // Moved with its record from another folder (the old one may still hold the original: a folder copy).
            var moved = Part("Mold D.SLDPRT");
            var old = Directory.CreateDirectory(Path.Combine(_dir, "old")).FullName;
            File.WriteAllText(Path.Combine(old, "Mold D.SLDPRT"), "");
            var folderD = Record("Mold D.mold", Path.Combine(old, "Mold D.SLDPRT"));
            Assert.True(RecordOwner.Owns(folderD, moved));
            // No owner written: whoever points at it.
            Assert.True(RecordOwner.Owns(Record("Mold E.mold", null), moved));
        }
    }
}

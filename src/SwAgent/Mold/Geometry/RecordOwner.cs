using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace SwAgent.Mold.Geometry
{
    /// <summary>
    /// Which part a mold record belongs to (critique B2, review). A Save As copy of a mold - by hand, or Smart Rebuild's
    /// working copy - carries the custom properties Unify3D.MoldRecord and Unify3D.MoldId, so by properties alone it would
    /// merge into the original's mold.json and share its checkpoints and journal. mold.json's files.moldPart names the
    /// part the record was made for; this decides whether the part at hand is that part. Pure (file system only;
    /// GeometryRecordOwnerTests).
    /// </summary>
    internal static class RecordOwner
    {
        /// <summary>The part path a record names (files.moldPart, a string or {path}), resolved against the record's folder; null if none.</summary>
        public static string OwnerOf(string recordFolder)
        {
            try
            {
                var file = Path.Combine(recordFolder, "mold.json");
                if (!File.Exists(file)) return null;
                var j = JObject.Parse(File.ReadAllText(file));
                var t = j.SelectToken("files.moldPart");
                var p = t?.Type == JTokenType.String ? (string)t : t?["path"]?.Type == JTokenType.String ? (string)t["path"] : null;
                if (string.IsNullOrWhiteSpace(p)) return null;
                var parent = Path.GetDirectoryName(Path.GetFullPath(recordFolder).TrimEnd('\\', '/'));
                return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(parent ?? "", p));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Whether the record in `recordFolder` belongs to the part at `partPath`: yes when it names this part, names no
        /// part, or names a part that no longer exists (the part was renamed or moved), or when the record and a part of
        /// the same file name were moved or copied together (a whole folder). No when it names another part that still
        /// exists: this part is a copy of that one and needs its own record.
        /// </summary>
        public static bool Owns(string recordFolder, string partPath)
        {
            if (string.IsNullOrEmpty(recordFolder) || string.IsNullOrEmpty(partPath)) return false;
            var owner = OwnerOf(recordFolder);
            if (owner == null) return true;
            var me = Path.GetFullPath(partPath);
            if (string.Equals(owner, me, StringComparison.OrdinalIgnoreCase)) return true;
            var recordParent = Path.GetDirectoryName(Path.GetFullPath(recordFolder).TrimEnd('\\', '/'));
            if (string.Equals(Path.GetFileName(owner), Path.GetFileName(me), StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetDirectoryName(owner), recordParent, StringComparison.OrdinalIgnoreCase))
                return true; // the record names its part's old folder: both were moved or copied together
            return !File.Exists(owner);
        }
    }
}

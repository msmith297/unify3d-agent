using System;
using System.IO;
using System.Threading;

namespace SwAgent.Infrastructure
{
    /// <summary>
    /// Puts a fully written temp file in place of the target (File.Replace, or a move when there is no target yet). A virus
    /// scanner, the search indexer, a sync client - or SolidWorks just after it closed the file - can hold the target for a
    /// moment ("Unable to remove the file to be replaced", seen 2026-10-20 and 2026-10-21): retry briefly instead of
    /// failing the save. The last attempt's error is thrown; the temp file is left for the caller to clean up.
    /// </summary>
    internal static class AtomicFile
    {
        public static void Replace(string tmp, string path)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(tmp, path, null);
                    else File.Move(tmp, path);
                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 8)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
        }
    }
}

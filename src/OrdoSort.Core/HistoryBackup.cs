using System.Globalization;

namespace OrdoSort.Core;

/// <summary>
/// Point-in-time safety net for the audit database. In replace mode the DB is
/// the ONLY link between a filed document and its original date/ID, and it has
/// no other redundancy — so keep a daily copy.
///
/// Call BEFORE opening the History connection, while the file is at rest.
/// </summary>
public static class HistoryBackup
{
    /// <summary>Copy the DB to <paramref name="backupDir"/> as
    /// history-YYYYMMDD.sqlite once per day, keeping the newest
    /// <paramref name="keep"/> copies. Never throws — a backup failure must
    /// never stop the app. Returns the backup path, or null if nothing to do.</summary>
    public static string? BackupDaily(string dbPath, string backupDir, DateTime today, int keep = 14)
    {
        try
        {
            if (!File.Exists(dbPath)) return null;
            Directory.CreateDirectory(backupDir);
            // Invariant: this is a real file name on disk, and the "keep
            // newest N" prune below relies on these names sorting
            // chronologically — a locale-shaped year would break both the
            // name and the sort.
            var dest = Path.Combine(backupDir,
                $"history-{today.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.sqlite");
            if (!File.Exists(dest))
                File.Copy(dbPath, dest);   // once per day; File.Copy won't overwrite

            // prune: keep the newest `keep` (names sort chronologically)
            var backups = Directory.GetFiles(backupDir, "history-*.sqlite")
                .OrderByDescending(f => f).ToList();
            foreach (var old in backups.Skip(keep))
            {
                try { File.Delete(old); } catch (IOException) { /* best effort */ }
            }
            return dest;
        }
        catch (Exception)
        {
            // A failed backup must never block startup — swallow everything
            // (bad path, permissions, IO, another process locking the file).
            return null;
        }
    }
}

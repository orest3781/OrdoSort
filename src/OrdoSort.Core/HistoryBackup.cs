using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OrdoSort.Core;

/// <summary>
/// Point-in-time safety net for the audit database. In replace mode the DB is
/// the ONLY link between a filed document and its original date/ID, and it has
/// no other redundancy — so keep a daily copy.
///
/// Call BEFORE opening the History connection, while the file is at rest.
/// "At rest" only holds for THIS station: other workstations sharing the DB
/// over SMB may be mid-commit, and journal_mode=TRUNCATE rewrites pages in
/// place — so the copy is taken through SQLite's online backup API (which
/// holds a read lock for a consistent snapshot), not a raw file copy.
/// </summary>
public static class HistoryBackup
{
    // Same lock wait History uses: another station's commit is short, so
    // waiting it out beats failing the backup.
    private const int BusyTimeoutSeconds = 30;

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
            // "Already backed up today" is judged by dest existing, so dest
            // must only ever appear as a complete, consistent copy — never a
            // torn or half-written one that would then stand all day.
            if (!File.Exists(dest))
                SnapshotInto(dbPath, dest);

            // prune: keep the newest `keep` (names sort chronologically).
            // A stale backup can be read-only (or otherwise access-denied)
            // as well as merely locked — File.Delete throws
            // UnauthorizedAccessException for that, not IOException, and
            // must be swallowed the same "best effort" way: today's copy
            // (dest, above) has already landed by the time this loop runs,
            // so letting either exception escape past this method's outer
            // catch would return null and light a false "backup failed"
            // banner over a copy that actually succeeded (Minor 2, final
            // review 2026-08-07).
            var backups = Directory.GetFiles(backupDir, "history-*.sqlite")
                .OrderByDescending(f => f).ToList();
            foreach (var old in backups.Skip(keep))
            {
                try { File.Delete(old); }
                catch (IOException) { /* best effort */ }
                catch (UnauthorizedAccessException) { /* best effort */ }
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

    /// <summary>Write a consistent snapshot of <paramref name="dbPath"/> to
    /// <paramref name="dest"/>: back up into a uniquely named temp file beside
    /// dest, then move it into place, so a failure part-way leaves no file at
    /// dest. Throws on failure (BackupDaily turns that into null).</summary>
    private static void SnapshotInto(string dbPath, string dest)
    {
        // Unique per attempt: several stations can back up into one shared
        // folder on the same day. The suffix keeps it out of the prune glob.
        var temp = $"{dest}.{Guid.NewGuid():N}.partial";
        try
        {
            // Pooling=false on both: a pooled connection keeps the file open
            // after Dispose, which would block the move below and hold the
            // shared DB open on the server.
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = dbPath,
                       Mode = SqliteOpenMode.ReadOnly,
                       DefaultTimeout = BusyTimeoutSeconds,
                       Pooling = false,
                   }.ToString()))
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                   {
                       DataSource = temp,
                       Pooling = false,
                   }.ToString()))
            {
                source.Open();
                using (var cmd = source.CreateCommand())
                {
                    // The backup API waits on the source's busy handler when
                    // another station holds the lock; without one it fails at
                    // once instead of waiting the way History does.
                    cmd.CommandText = $"PRAGMA busy_timeout={BusyTimeoutSeconds * 1000}";
                    cmd.ExecuteNonQuery();
                }
                target.Open();
                source.BackupDatabase(target);
            }

            try
            {
                File.Move(temp, dest);
            }
            catch (IOException) when (File.Exists(dest))
            {
                // Another station sharing this backup folder placed today's
                // copy between our check and our move. Its copy is complete by
                // the same temp-then-move rule, so today is covered.
                File.Delete(temp);
            }
        }
        catch
        {
            // Don't leave a stray partial behind. The original error is the
            // one that matters, so a failed cleanup must not replace it.
            try { File.Delete(temp); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
            throw;
        }
    }
}

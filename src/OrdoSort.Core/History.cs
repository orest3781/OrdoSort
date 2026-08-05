using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OrdoSort.Core;

/// <summary>
/// The durable audit trail: one SQLite row per commit.
///
/// NETWORK SAFETY: this database can live on an SMB share with several
/// workstations open at once. WAL is deliberately NOT used — it relies on
/// shared memory that does not work over a network filesystem and is the
/// documented way to corrupt a shared SQLite file. A rollback journal
/// (TRUNCATE) is network-safe; busy_timeout makes concurrent writers wait for
/// the lock instead of erroring.
/// </summary>
public sealed class History : IDisposable
{
    // Seconds a writer waits for the lock before giving up.
    private const int BusyTimeoutSeconds = 30;

    public static readonly string[] Columns =
    {
        "id", "ts_utc", "original_path", "original_name", "new_name",
        "name_entered", "naming_mode", "suffix_applied", "route_label",
        "route_path", "tagged", "collision_suffix", "reverted", "reverted_ts",
    };

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS history (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          ts_utc TEXT NOT NULL,
          original_path TEXT NOT NULL,
          original_name TEXT NOT NULL,
          new_name TEXT NOT NULL,
          name_entered TEXT NOT NULL,
          naming_mode TEXT NOT NULL,
          suffix_applied TEXT NOT NULL DEFAULT '',
          route_label TEXT NOT NULL,
          route_path TEXT NOT NULL,
          tagged INTEGER NOT NULL DEFAULT 0,
          collision_suffix TEXT NOT NULL DEFAULT '',
          reverted INTEGER NOT NULL DEFAULT 0,
          reverted_ts TEXT NOT NULL DEFAULT ''
        )
        """;

    public string Path { get; }
    private readonly SqliteConnection _conn;

    public History(string dbPath)
    {
        Path = dbPath;
        var parent = System.IO.Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = BusyTimeoutSeconds,   // busy-wait for the lock
            Pooling = false,                       // release the file on close
                                                   // — matters on a network share
        }.ToString());
        _conn.Open();
        Exec($"PRAGMA busy_timeout={BusyTimeoutSeconds * 1000}");
        Exec("PRAGMA journal_mode=TRUNCATE");      // NOT wal — see class doc
        Exec("PRAGMA synchronous=FULL");
        Exec(Schema);
        Migrate();
    }

    /// <summary>Invariant: this is the stored ts_utc value, read back by every
    /// station that opens this shared audit log — it must not shift shape
    /// with whatever locale a given station's Windows happens to have.</summary>
    public static string UtcNow() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    private void Migrate()
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(history)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        if (!cols.Contains("reverted_ts"))
            Exec("ALTER TABLE history ADD COLUMN reverted_ts TEXT NOT NULL DEFAULT ''");

        // Audit finding 5.1 (2026-08-04): the history table had no indexes at
        // all, and RankedNames() ran a full-table GROUP BY after every
        // commit, skip and undo, against a table this app never prunes.
        // Measured on a 100k-row fixture (2,000 distinct names, ~5%
        // reverted, ~3% blank name_entered): before, EXPLAIN QUERY PLAN
        // showed `SCAN history` plus two TEMP B-TREEs, and RankedNames()
        // took ~73ms best-of-5. A plain (non-partial) index on the same
        // three columns produced an almost identical-looking plan but was
        // *worse* than no index at all (~437ms) — it doesn't cover
        // `reverted`, so SQLite still paid a table lookup per index row.
        // This partial index's WHERE matches the query's WHERE exactly, so
        // SQLite can prove it applies, scan it alone as a covering index,
        // and never touch the table: RankedNames() dropped to ~17ms (4.3x).
        // Count() and Rows() are untouched by design (see progress ledger).
        // Write cost: LogCommit touches this index on every insert (it
        // always writes reverted = 0) — measured 8.494ms/call before vs
        // 8.536ms/call after, 1,000 real LogCommit calls, best of 3 — inside
        // the noise floor already imposed by synchronous=FULL's per-call
        // fsync. One-time cost of building this on an existing 100k-row
        // database — paid by ShellViewModel's synchronous constructor on the
        // first launch after upgrade — measured ~110-130ms: not perceptible
        // as a hang. Re-measure before assuming this still earns its keep if
        // the query, the table's shape, or the row count changes materially.
        Exec("""
            CREATE INDEX IF NOT EXISTS ix_history_ranked_names
                ON history(name_entered, ts_utc, id)
                WHERE reverted = 0 AND name_entered != ''
            """);
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public long LogCommit(
        string originalPath, string originalName, string newName,
        string nameEntered, string namingMode, string suffixApplied,
        string routeLabel, string routePath, bool tagged, string collisionSuffix)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (ts_utc, original_path, original_name, new_name,
              name_entered, naming_mode, suffix_applied, route_label, route_path,
              tagged, collision_suffix, reverted)
            VALUES ($ts, $op, $on, $nn, $ne, $nm, $sa, $rl, $rp, $tg, $cs, 0);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$ts", UtcNow());
        cmd.Parameters.AddWithValue("$op", originalPath);
        cmd.Parameters.AddWithValue("$on", originalName);
        cmd.Parameters.AddWithValue("$nn", newName);
        cmd.Parameters.AddWithValue("$ne", nameEntered);
        cmd.Parameters.AddWithValue("$nm", namingMode);
        cmd.Parameters.AddWithValue("$sa", suffixApplied);
        cmd.Parameters.AddWithValue("$rl", routeLabel);
        cmd.Parameters.AddWithValue("$rp", routePath);
        cmd.Parameters.AddWithValue("$tg", tagged ? 1 : 0);
        cmd.Parameters.AddWithValue("$cs", collisionSuffix);
        return (long)cmd.ExecuteScalar()!;
    }

    public void MarkReverted(long rowId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "UPDATE history SET reverted = 1, reverted_ts = $ts WHERE id = $id";
        cmd.Parameters.AddWithValue("$ts", UtcNow());
        cmd.Parameters.AddWithValue("$id", rowId);
        cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM history";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<IReadOnlyDictionary<string, object>> Rows(int? limit = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM history ORDER BY id DESC" +
                          (limit is { } n ? $" LIMIT {n}" : "");
        return Read(cmd);
    }

    private const string RankedNamesSql =
        "SELECT name_entered FROM history" +
        " WHERE name_entered != '' AND reverted = 0" +
        " GROUP BY name_entered" +
        " ORDER BY MAX(ts_utc) DESC, COUNT(*) DESC, MAX(id) DESC";

    /// <summary>Distinct committed names, most recently used first, then by how
    /// often — the autocomplete order. Blank and reverted rows don't count.</summary>
    public List<string> RankedNames()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = RankedNamesSql;
        var names = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    /// <summary>EXPLAIN QUERY PLAN for RankedNames()'s exact SQL — shares the
    /// constant with RankedNames() itself so the plan under test can never
    /// silently drift from the query actually run. Test-only.</summary>
    internal string ExplainRankedNames()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + RankedNamesSql;
        using var r = cmd.ExecuteReader();
        var detailCol = r.GetOrdinal("detail");
        var lines = new List<string>();
        while (r.Read()) lines.Add(r.GetString(detailCol));
        return string.Join("\n", lines);
    }

    /// <summary>Runs arbitrary SQL against the shared connection. Test-only —
    /// lets a test simulate an existing database (e.g. drop an index) without
    /// exposing the connection itself.</summary>
    internal void ExecForTests(string sql) => Exec(sql);

    /// <summary>Write the whole table to CSV (Excel-friendly BOM), chronological.
    /// Returns the row count. Cells that a spreadsheet would read as a formula
    /// (=, +, -, @, tab, CR) get a leading apostrophe so opening the file can't
    /// execute anything.</summary>
    public int ExportCsv(string dest)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM history ORDER BY id";
        var rows = Read(cmd);
        using var writer = new StreamWriter(dest, false, new System.Text.UTF8Encoding(true));
        writer.WriteLine(string.Join(",", Columns.Select(CsvField)));
        foreach (var row in rows)
            writer.WriteLine(string.Join(",",
                Columns.Select(c => CsvField(row.TryGetValue(c, out var v) ? v?.ToString() ?? "" : ""))));
        return rows.Count;
    }

    private static string CsvField(string value)
    {
        // formula-injection guard first
        if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0)
            value = "'" + value;
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            value = "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static List<IReadOnlyDictionary<string, object>> Read(SqliteCommand cmd)
    {
        var rows = new List<IReadOnlyDictionary<string, object>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new Dictionary<string, object>();
            for (var i = 0; i < r.FieldCount; i++)
                row[r.GetName(i)] = r.IsDBNull(i) ? "" : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Return the actual journal mode SQLite is using — "truncate"
    /// on a network share, never "wal". Exposed for the network-safety test.</summary>
    public string JournalMode()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        return (string)cmd.ExecuteScalar()!;
    }

    public void Dispose() => _conn.Dispose();
}

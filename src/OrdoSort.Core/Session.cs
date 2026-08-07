namespace OrdoSort.Core;

/// <summary>Session state: the queue walk, counts, undo stack, and history
/// writes. No UI — the whole processing loop is testable headless.</summary>
public sealed class Session
{
    public const string SkipLabel = "<skip>";
    public const string VanishedLabel = "<vanished>";
    public const int UndoDepth = 20;

    private sealed record UndoEntry(long RowId, int QueueIndex, string FiledPath,
        string OriginalPath, bool WasSkip);

    // Session is rebuilt from scratch by ShellViewModel.ApplySettings whenever
    // settings change, so both _cfg and the ctor-cached SessionMode are
    // effectively frozen for the life of one session.
    private readonly Config _cfg;
    private readonly History _history;
    private readonly LinkedList<UndoEntry> _undo = new();

    public string SessionMode { get; set; }
    public List<string> Queue { get; private set; } = new();
    public int Pos { get; private set; }
    public int Filed { get; private set; }
    public int Skipped { get; private set; }
    public int Vanished { get; private set; }
    public List<long> RowIds { get; } = new();

    public Session(Config cfg, History history)
    {
        _cfg = cfg;
        _history = history;
        SessionMode = cfg.NamingMode;
    }

    public void Start(IEnumerable<string> queue)
    {
        Queue = queue.ToList();
        Pos = Filed = Skipped = Vanished = 0;
        _undo.Clear();
        RowIds.Clear();
        SessionMode = _cfg.NamingMode;
    }

    // Audit 2.6b: this used to read Pos TWICE — `Pos < Queue.Count ?
    // Queue[Pos] : null` — and Pos++ happens on the commit thread (via
    // ShellViewModel's IWorkScheduler) while Current is read from the UI
    // thread (progress line, preview, autocomplete). If Pos++ landed
    // between the two reads, the bounds check above saw the OLD (in-range)
    // Pos while the indexer below saw the NEW (out-of-range) one, throwing
    // out of the UI thread at the last document in the queue.
    //
    // Queue itself is the identical hazard: it too was read twice (once for
    // .Count, once for the indexer), and Start() reassigns it wholesale.
    // There is no live path today where Start() races a read of Current —
    // every UI entry point that could reach Start() while a commit is live
    // is gated by ShellViewModel._busy — but that is a ViewModel-level
    // convention, not a guarantee Session makes about itself, and the fix
    // costs nothing. Reading both Pos and Queue once into locals makes
    // every use inside this getter see the same values no matter what
    // another thread does to either property in between.
    public string? Current
    {
        get
        {
            var pos = Pos;
            var queue = Queue;
            return pos < queue.Count ? queue[pos] : null;
        }
    }

    public bool Done => Current is null;
    public int Total => Queue.Count;
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Append newly arrived inbox files to the END of a running queue.
    /// Files already known are ignored. Returns how many were added.</summary>
    public int Extend(IEnumerable<string> paths)
    {
        var known = new HashSet<string>(Queue);
        var added = paths.Where(p => known.Add(p)).ToList();
        Queue.AddRange(added);
        return added.Count;
    }

    /// <summary>Write the audit row, or report why not. Never throws: the
    /// document has already moved by the time this runs, so a failure here must
    /// not abort the state update that records the move — only describe it.
    /// Returns the row id, or -1 with <paramref name="failure"/> set.</summary>
    private long TryLog(Func<long> write, out string? failure)
    {
        try
        {
            var rowId = write();
            RowIds.Add(rowId);
            failure = null;
            return rowId;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            return -1;
        }
    }

    private static AuditError AuditFailure(string newPath, string what, string failure) =>
        new(newPath, $"{what} was moved to:\n\n{newPath}\n\n" +
                     "…but the history database could not record it, so this " +
                     "document is missing from the audit trail:\n\n" + failure);

    public Commit.CommitOutcome CommitCurrent(string typedName, Route route)
    {
        var src = Current ?? throw new CommitError("No document is loaded.");
        var outcome = Commit.CommitFile(src, typedName, route, SessionMode);
        if (outcome.Vanished) { LogVanished(src); return outcome; }

        var result = outcome.NameResult!;
        var rowId = TryLog(() => _history.LogCommit(
            src, Path.GetFileName(src), result.Filename,
            Naming.IsBlankName(typedName) ? "" : typedName, result.ModeUsed,
            result.SuffixApplied, route.Label, route.Path, tagged: false,
            result.CollisionSuffix), out var failure);

        // the file is GONE from the inbox — advance regardless, or the next
        // press would find it missing and log it as <vanished>
        Push(new UndoEntry(rowId, Pos, outcome.NewPath!, src, false));
        Filed++;
        Pos++;
        if (failure is not null)
            throw AuditFailure(outcome.NewPath!, Path.GetFileName(src), failure);
        return outcome;
    }

    public Commit.SkipOutcome SkipCurrent()
    {
        var src = Current ?? throw new CommitError("No document is loaded.");
        var outcome = Commit.SkipFile(src, _cfg.Deferred);
        if (outcome.Vanished) { LogVanished(src); return outcome; }

        var rowId = TryLog(() => _history.LogCommit(
            src, Path.GetFileName(src), Path.GetFileName(outcome.NewPath!),
            "", SessionMode, "", SkipLabel, _cfg.Deferred, tagged: false,
            outcome.CollisionSuffix), out var failure);
        Push(new UndoEntry(rowId, Pos, outcome.NewPath!, src, true));
        Skipped++;
        Pos++;
        if (failure is not null)
            throw AuditFailure(outcome.NewPath!, Path.GetFileName(src), failure);
        return outcome;
    }

    public (string FiledPath, string OriginalPath) UndoLast()
    {
        if (_undo.Count == 0) throw new CommitError("Nothing to undo.");
        var entry = _undo.Last!.Value;
        Commit.UndoAction(entry.FiledPath, entry.OriginalPath);
        // the file is BACK — the counts must say so even if the audit write
        // fails, and an unrecorded commit (RowId -1) has no row to mark
        _undo.RemoveLast();
        if (entry.WasSkip) Skipped--; else Filed--;
        Pos = entry.QueueIndex;   // the restored file is current again
        string? failure = null;
        if (entry.RowId >= 0)
        {
            try { _history.MarkReverted(entry.RowId); }
            catch (Exception ex) { failure = $"{ex.GetType().Name}: {ex.Message}"; }
        }
        if (failure is not null)
            throw new AuditError(entry.OriginalPath,
                $"{Path.GetFileName(entry.FiledPath)} was moved back to:\n\n" +
                $"{entry.OriginalPath}\n\n…but the history database still shows " +
                "it as filed:\n\n" + failure);
        return (entry.FiledPath, entry.OriginalPath);
    }

    private void Push(UndoEntry e)
    {
        _undo.AddLast(e);
        while (_undo.Count > UndoDepth) _undo.RemoveFirst();
    }

    private void LogVanished(string src)
    {
        TryLog(() => _history.LogCommit(
            src, Path.GetFileName(src), Path.GetFileName(src), "", SessionMode,
            "", VanishedLabel, "", tagged: false, ""), out var failure);
        // advance either way — a ghost we can't log is still a ghost, and
        // stalling here would loop the same missing file forever
        Vanished++;
        Pos++;
        if (failure is not null)
            throw new AuditError(src,
                $"{Path.GetFileName(src)} was gone from the inbox before it " +
                "could be filed, and the history database could not record " +
                "that either:\n\n" + failure);
    }
}

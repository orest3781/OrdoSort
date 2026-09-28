namespace OrdoSort.Core;

/// <summary>
/// The commit pipeline: name -> move. Plus skip and undo. Files are only ever
/// MOVED — never deleted, never overwritten. Every failure raises CommitError
/// with a message fit for a dialog box, and the source stays put.
/// </summary>
public static class Commit
{
    public sealed record CommitOutcome(
        bool Vanished, string? NewPath, Naming.NameResult? NameResult);

    public sealed record SkipOutcome(
        bool Vanished, string? NewPath, string CollisionSuffix);

    /// <summary>File.Move with a last-instant collision guard. Windows won't
    /// overwrite on move, but check explicitly and treat 'exists' as a race.
    ///
    /// Across drives or shares File.Move copies then deletes, and Windows'
    /// copy lets a server copy between its own shares without the file
    /// crossing the network. A copy of its own through a ".partial" name
    /// (DW-01, 1.8.0) was 19 times slower share to share (13 MB: 0.16 s vs
    /// 2.9 s) and made every filing wait, so it was taken out (2026-09-28).</summary>
    private static void MoveNeverOverwrite(string src, string target)
    {
        if (Path.Exists(target))
            throw new FileExistsRace($"{Path.GetFileName(target)} appeared at the destination mid-commit");
        try
        {
            File.Move(src, target);   // .NET Move does not overwrite by default
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deleted in the instant after the caller checked it was there
            // (DW-46): the answer that check gives, not a raw "could not
            // find file".
            if (ex is FileNotFoundException && !File.Exists(src)
                && Directory.Exists(Path.GetDirectoryName(src)))
                throw new SourceGoneRace();
            throw new CommitError($"Couldn't move {Path.GetFileName(src)} to " +
                                  $"{Path.GetDirectoryName(target)}:\n{ex.Message}");
        }

        SurvivingSourceHookForTests?.Invoke(src);

        // File.Move is MoveFileExW with MOVEFILE_COPY_ALLOWED, documented: "If
        // the file is successfully copied to a different volume and the
        // original file is unable to be deleted, the function succeeds
        // leaving the source file intact." Inbox on one share and routes on
        // another is the normal deployment here, so a non-throwing return
        // above is NOT proof src is gone (2026-08 audit finding QC-03). Left
        // unchecked: the UI reports "Filed", Session logs the row and
        // advances, and the original sits in the inbox forever — worse,
        // UndoAction's own File.Exists(originalPath) guard further down this
        // file then reads "original still there" as "already exists again"
        // and refuses the undo, so the user is left with two copies and no
        // way back through the app. Re-check explicitly and refuse instead.
        //
        // Do NOT delete the destination copy to clean this up: it's the one
        // file that demonstrably wrote successfully, and deleting it on a
        // path that's already behaving abnormally risks destroying the only
        // good copy. Refusing loudly and leaving both copies in place for a
        // human to sort out is the correct behaviour here.
        if (File.Exists(src))
            throw new CommitError(leftBothCopies: true, message:
                // "moved", not "filed": this same message fires from
                // SkipFile too (the user clicked Skip, not File) AND from
                // UndoAction (:202) — where src is the FILED copy and
                // target is the inbox, the exact opposite of CommitFile/
                // SkipFile's roles. The wording below is deliberately
                // caller-neutral (paths, never "the inbox"/"the original")
                // for that reason: app-qc-2026-08-21 final review, finding
                // 2 — the old text called src "the original" and target
                // "the inbox" unconditionally, which was backwards on the
                // undo path and would send a reader who trusted the prose
                // over the paths to delete the wrong copy.
                $"{Path.GetFileName(src)} could not be moved cleanly. A copy " +
                $"reached {target}, but the copy at {src} could not be " +
                "removed — Windows allows a cross-volume move to report " +
                "success even when it can't delete the source. Two copies " +
                $"of this document now exist: one at {target}, and one " +
                $"still at {src}. This document has NOT been recorded as " +
                $"moved. Confirm the copy at {target} is correct, then " +
                $"remove the copy at {src} by hand.");
    }

    /// <summary>True when <paramref name="src"/> is really gone from a
    /// folder that is itself reachable. File.Exists also answers false for
    /// any I/O error, so on an inbox share that drops for a moment every
    /// document would otherwise read as vanished — logged as such, and
    /// skipped for the rest of the session though it never left. When the
    /// folder can't be reached either, that is an outage, not a vanished
    /// file, and it is reported as one.</summary>
    private static bool SourceVanished(string src)
    {
        if (File.Exists(src)) return false;
        var dir = Path.GetDirectoryName(src);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new CommitError($"The inbox folder is not reachable right now: " +
                                  $"{(string.IsNullOrEmpty(dir) ? "(unknown)" : dir)}. " +
                                  "Nothing was moved — try again once it is back.");
        return true;
    }

    /// <summary>A destination or set-aside folder that IS the document's own
    /// folder (the inbox, however it is spelled) would "move" it in place: the
    /// collision rule renamed it to "… (2).pdf", it was reported filed or set
    /// aside, and the rescan queued it again (Q2-03). Refused before any
    /// naming or moving.</summary>
    private static void RefuseItsOwnFolder(string src, string folder)
    {
        if (PathIdentity.Same(Path.GetDirectoryName(src), folder))
            throw new CommitError(
                $"{Path.GetFileName(src)} is already in {folder}, so it can't be moved there. " +
                "That folder is set as a destination or the set-aside folder; check it in Settings.");
    }

    public static CommitOutcome CommitFile(
        string src, string typedName, Route route, string globalMode)
    {
        if (SourceVanished(src))
            return new CommitOutcome(true, null, null);

        var destDir = route.Path ?? "";
        if (!Directory.Exists(destDir))
            throw new CommitError($"Destination folder is not available: " +
                                  $"{(destDir.Length > 0 ? destDir : "(not set)")}");
        RefuseItsOwnFolder(src, destDir);

        Naming.NameResult Build() => Naming.BuildTarget(
            Path.GetFileName(src), typedName, route.NamingMode, globalMode,
            route.Suffix, route.AppendSuffix,
            Collision.TakenIn(destDir));

        Naming.NameResult result;
        try { result = Build(); }
        catch (ArgumentException ex) { throw new CommitError(ex.Message); }

        CommitRaceHookForTests?.Invoke();
        try
        {
            try
            {
                MoveNeverOverwrite(src, Path.Combine(destDir, result.Filename));
            }
            catch (FileExistsRace)
            {
                // Collision race: something claimed the name after Build. Retry once.
                result = Build();
                try { MoveNeverOverwrite(src, Path.Combine(destDir, result.Filename)); }
                catch (FileExistsRace ex)
                {
                    throw new CommitError(ex.Message);
                }
            }
        }
        catch (SourceGoneRace) { return new CommitOutcome(true, null, null); }
        return new CommitOutcome(false, Path.Combine(destDir, result.Filename), result);
    }

    public static SkipOutcome SkipFile(string src, string deferredDir)
    {
        if (SourceVanished(src))
            return new SkipOutcome(true, null, "");
        if (string.IsNullOrWhiteSpace(deferredDir) || !Directory.Exists(deferredDir))
            throw new CommitError($"Set-aside folder is not available: " +
                                  $"{(string.IsNullOrWhiteSpace(deferredDir) ? "(not set)" : deferredDir)}");
        RefuseItsOwnFolder(src, deferredDir);

        // blank name + empty route == keep the original filename, collision-counted.
        // The original name itself can be refused (a device name): same
        // readable CommitError CommitFile gives (DW-15).
        Naming.NameResult result;
        try
        {
            result = Naming.BuildTarget(
                Path.GetFileName(src), "", null, Naming.ModeInsert, "", false,
                Collision.TakenIn(deferredDir));
        }
        catch (ArgumentException ex) { throw new CommitError(ex.Message); }
        SkipRaceHookForTests?.Invoke();
        try
        {
            MoveNeverOverwrite(src, Path.Combine(deferredDir, result.Filename));
        }
        catch (FileExistsRace ex)
        {
            // Same situation CommitFile's own catch (:67) and UndoAction's
            // own catch (:123) both guard against: something claimed this
            // name in the gap between Naming.BuildTarget's probe above and
            // MoveNeverOverwrite's own File.Exists guard firing a few
            // microseconds later. Without this catch, that private
            // exception type escaped OrdoSort.Core past
            // ShellViewModel.OnSkipAsync's CommitError/AuditError handlers
            // as an unhandled crash — even though no document was lost: the
            // guard fires before src is ever touched, same as here. Give the
            // caller the same actionable CommitError its two siblings
            // produce instead.
            throw new CommitError(ex.Message);
        }
        catch (SourceGoneRace) { return new SkipOutcome(true, null, ""); }
        return new SkipOutcome(false, Path.Combine(deferredDir, result.Filename),
            result.CollisionSuffix);
    }

    /// <summary>Test-only seam: when set, invoked immediately before the
    /// final move in <see cref="SkipFile"/> — right after Naming.BuildTarget
    /// has picked a name but before MoveNeverOverwrite's own guard checks
    /// it. Mirrors <see cref="RaceHookForTests"/> (UndoAction's own seam for
    /// the identical kind of race) but kept as a separate field: the two
    /// methods are exercised by different test classes, and a shared field
    /// would mean each class's set/clear sequence could stomp the other's
    /// mid-test whenever xUnit runs their classes in parallel — the same
    /// reason RaceHookForTests's own doc comment gives for why the classes
    /// that DO share it must share an xUnit collection instead. Production
    /// code never sets this.</summary>
    internal static Action? SkipRaceHookForTests;

    /// <summary>Test-only seam: the same as <see cref="SkipRaceHookForTests"/>,
    /// for <see cref="CommitFile"/>: invoked after the name is built, just
    /// before the move. Production code never sets this.</summary>
    internal static Action? CommitRaceHookForTests;

    /// <summary>Test-only seam: when set, invoked immediately before the final
    /// move in <see cref="UndoAction"/> — right after all three guards have
    /// passed. Lets a test deterministically reproduce the collision race
    /// MoveNeverOverwrite guards against (the original name reappearing in the
    /// instant between the guard and the move) instead of relying on real
    /// thread timing. Production code never sets this.</summary>
    internal static Action? RaceHookForTests;

    /// <summary>Test-only seam: when set, invoked inside MoveNeverOverwrite
    /// immediately after File.Move returns without throwing, before the
    /// File.Exists(src) recheck below decides whether the move actually took.
    /// Lets a test simulate the one Win32 branch nothing here can reproduce
    /// reliably on one machine — MoveFileExW's MOVEFILE_COPY_ALLOWED path,
    /// where a cross-volume move that can't delete its source still reports
    /// success (2026-08 audit finding QC-03) — by recreating src itself, the
    /// same "make the filesystem match the scenario, then let the real check
    /// run" approach RaceHookForTests/SkipRaceHookForTests use for their own
    /// races. Not split per caller the way those two are: this check has no
    /// caller-specific behaviour to protect, so it fires uniformly for every
    /// MoveNeverOverwrite call (CommitFile, SkipFile, UndoAction alike).
    /// It is handed the path being moved, so a test running beside others
    /// can act on its own file only. Production code never sets this.</summary>
    internal static Action<string>? SurvivingSourceHookForTests;

    /// <summary>Reverse one commit/skip: move the file back to its original
    /// name. Raises CommitError if the undo can't be done — the filed copy
    /// stays put.</summary>
    public static void UndoAction(string filedPath, string originalPath)
    {
        if (!File.Exists(filedPath))
            throw new CommitError($"Can't undo: {Path.GetFileName(filedPath)} is no longer there");
        if (Path.Exists(originalPath))   // a folder on the name blocks the move too (DW-18)
            throw new CommitError($"Can't undo: {Path.GetFileName(originalPath)} already exists again");
        var parent = Path.GetDirectoryName(originalPath);
        if (parent is null || !Directory.Exists(parent))
            throw new CommitError($"Can't undo: inbox folder is gone: {parent}");

        RaceHookForTests?.Invoke();
        try
        {
            MoveNeverOverwrite(filedPath, originalPath);
        }
        catch (FileExistsRace)
        {
            // Same situation the guard two lines up detects — the original
            // name is occupied — just caught a few microseconds later. Give
            // the user the identical actionable message instead of letting
            // this private type escape the assembly as an unhandled error.
            throw new CommitError($"Can't undo: {Path.GetFileName(originalPath)} already exists again");
        }
        catch (SourceGoneRace)
        {
            throw new CommitError($"Can't undo: {Path.GetFileName(filedPath)} is no longer there");
        }
    }

    private sealed class FileExistsRace : Exception
    {
        public FileExistsRace(string message) : base(message) { }
    }

    /// <summary>The file being moved was deleted after its caller checked
    /// it was there. Never leaves this class.</summary>
    private sealed class SourceGoneRace : Exception { }
}

public sealed class CommitError : Exception
{
    public CommitError(string message) : base(message) { }

    internal CommitError(bool leftBothCopies, string message) : base(message) =>
        LeftBothCopies = leftBothCopies;

    /// <summary>The move reached its target but the source could not be
    /// removed (a cross-volume move that could not delete its source), so the
    /// file now exists in both places.</summary>
    public bool LeftBothCopies { get; }
}

/// <summary>The document moved, but the audit row could not be written. Carries
/// where the document actually landed so the user can be told the truth — the
/// move is done and cannot be un-done by pretending it failed.</summary>
public sealed class AuditError : Exception
{
    public AuditError(string newPath, string message) : base(message) =>
        NewPath = newPath;

    internal AuditError(string newPath, string message, bool vanished) : this(newPath, message) =>
        Vanished = vanished;

    /// <summary>Where the document is now.</summary>
    public string NewPath { get; }

    /// <summary>Nothing moved: the document was already gone from the inbox,
    /// and <see cref="NewPath"/> is where it used to be. Only recording that
    /// failed (DW-16).</summary>
    public bool Vanished { get; }
}

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
    /// Unproven window (2026-08 audit finding 1.4, [U]): across volumes
    /// File.Move is copy-then-delete. Interrupted mid-copy — kill, power
    /// loss, disk full — it can leave a partial file sitting at `target`.
    /// On retry, Build()'s collision counter sees that name as taken and
    /// gives the real document the " (2)" suffix instead, leaving the
    /// partial holding the canonical name with nothing downstream aware
    /// it's incomplete. Whether Win32 MoveFileEx cleans up its own partial
    /// on a non-crash failure (e.g. disk full) is unverified here — proving
    /// it needs a disk-full or kill test across two volumes. Not fixed.</summary>
    private static void MoveNeverOverwrite(string src, string target)
    {
        if (File.Exists(target))
            throw new FileExistsRace($"{Path.GetFileName(target)} appeared at the destination mid-commit");
        try
        {
            File.Move(src, target);   // .NET Move does not overwrite by default
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CommitError($"Couldn't move {Path.GetFileName(src)} to " +
                                  $"{Path.GetDirectoryName(target)}:\n{ex.Message}");
        }
    }

    public static CommitOutcome CommitFile(
        string src, string typedName, Route route, string globalMode)
    {
        if (!File.Exists(src))
            return new CommitOutcome(true, null, null);

        var destDir = route.Path ?? "";
        if (!Directory.Exists(destDir))
            throw new CommitError($"Destination folder is not available: " +
                                  $"{(destDir.Length > 0 ? destDir : "(not set)")}");

        Naming.NameResult Build() => Naming.BuildTarget(
            Path.GetFileName(src), typedName, route.NamingMode, globalMode,
            route.Suffix, route.AppendSuffix,
            name => File.Exists(Path.Combine(destDir, name)));

        Naming.NameResult result;
        try { result = Build(); }
        catch (ArgumentException ex) { throw new CommitError(ex.Message); }

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
        return new CommitOutcome(false, Path.Combine(destDir, result.Filename), result);
    }

    public static SkipOutcome SkipFile(string src, string deferredDir)
    {
        if (!File.Exists(src))
            return new SkipOutcome(true, null, "");
        if (string.IsNullOrWhiteSpace(deferredDir) || !Directory.Exists(deferredDir))
            throw new CommitError($"Set-aside folder is not available: " +
                                  $"{(string.IsNullOrWhiteSpace(deferredDir) ? "(not set)" : deferredDir)}");

        // blank name + empty route == keep the original filename, collision-counted
        var result = Naming.BuildTarget(
            Path.GetFileName(src), "", null, Naming.ModeInsert, "", false,
            name => File.Exists(Path.Combine(deferredDir, name)));
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

    /// <summary>Test-only seam: when set, invoked immediately before the final
    /// move in <see cref="UndoAction"/> — right after all three guards have
    /// passed. Lets a test deterministically reproduce the collision race
    /// MoveNeverOverwrite guards against (the original name reappearing in the
    /// instant between the guard and the move) instead of relying on real
    /// thread timing. Production code never sets this.</summary>
    internal static Action? RaceHookForTests;

    /// <summary>Reverse one commit/skip: move the file back to its original
    /// name. Raises CommitError if the undo can't be done — the filed copy
    /// stays put.</summary>
    public static void UndoAction(string filedPath, string originalPath)
    {
        if (!File.Exists(filedPath))
            throw new CommitError($"Can't undo: {Path.GetFileName(filedPath)} is no longer there");
        if (File.Exists(originalPath))
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
    }

    private sealed class FileExistsRace : Exception
    {
        public FileExistsRace(string message) : base(message) { }
    }
}

public sealed class CommitError : Exception
{
    public CommitError(string message) : base(message) { }
}

/// <summary>The document moved, but the audit row could not be written. Carries
/// where the document actually landed so the user can be told the truth — the
/// move is done and cannot be un-done by pretending it failed.</summary>
public sealed class AuditError : Exception
{
    public AuditError(string newPath, string message) : base(message) =>
        NewPath = newPath;

    /// <summary>Where the document is now.</summary>
    public string NewPath { get; }
}

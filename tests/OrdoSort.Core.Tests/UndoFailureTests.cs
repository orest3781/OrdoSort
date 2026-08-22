using System.Reflection;
using System.Text.RegularExpressions;
using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Commit.UndoAction guards three ways an undo can fail after the
/// filing already happened: the filed copy is gone, the original name has
/// been reused, or the inbox folder itself vanished. In every case the filed
/// copy must stay exactly where it was — that is UndoAction's documented
/// promise. And because Session.UndoLast() calls UndoAction FIRST and only
/// mutates state (the undo stack, Filed/Skipped, Pos, MarkReverted) once it
/// returns without throwing, a failed undo must leave every one of those
/// exactly as it found them — most importantly, the history row must NOT be
/// marked reverted while the document is still filed. That surviving-state
/// property, not just "it throws", is what these tests pin.
///
/// Final-review M (2026-08-05): RaceAtTheFinalMomentIsReportedAsTheSameActionable
/// CommitError below sets the static, unsynchronized <see cref="Commit.RaceHookForTests"/>
/// seam. PipelineTests.SessionUndoRoundTrip and AuditFailureTests.
/// UndoStillWorksAfterAnUnrecordedCommit also drive Commit.UndoAction (the
/// method the hook lives inside), each from its own different, undeclared
/// xUnit collection — which by default runs concurrently with this one. A
/// grep for <c>UndoLast(</c>/<c>Commit.UndoAction(</c>/<c>RaceHookForTests</c>
/// across tests/ confirms those three classes — PipelineTests,
/// AuditFailureTests, and this one — are the complete set that touches this
/// seam. This is the exact same defect class as OrdoSort.Wpf.App._crashDir
/// (see OrdoSort.Wpf.Tests.CultureInvariantDatesTests's class doc), fixed the
/// same way: put all three classes in one shared collection (<see cref="Name"/>)
/// so xUnit's own "never run two classes in the same collection concurrently"
/// rule serializes them. No lock was added around the seam in Commit.cs itself
/// — a lock would still leave separate collections free to interleave their
/// set/invoke/clear sequences arbitrarily, just without a data race on the
/// field; only serializing the collections closes the window outright, and it
/// costs nothing outside these three classes (unlike disabling
/// parallelization suite-wide). No ICollectionFixture is declared: unlike the
/// WPF STA Application fixture, nothing here needs to be built once and
/// shared — each class already builds and tears down its own isolated temp
/// root per test.</summary>
[Collection(Name)]
public class UndoFailureTests : IDisposable
{
    public const string Name = "Commit undo-race collection";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ordoundo_" + Guid.NewGuid());
    private readonly string _inbox, _dest, _deferred, _cfgPath;

    public UndoFailureTests()
    {
        _inbox = Path.Combine(_root, "inbox");
        _dest = Path.Combine(_root, "dest");
        _deferred = Path.Combine(_root, "deferred");
        _cfgPath = Path.Combine(_root, "config.json");   // Deferred above is already absolute, so this base is never actually consulted
        foreach (var d in new[] { _inbox, _dest, _deferred }) Directory.CreateDirectory(d);
    }

    public void Dispose()
    {
        Commit.RaceHookForTests = null;
        Commit.SurvivingSourceHookForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var a = 0; ; a++)
        {
            try { Directory.Delete(_root, true); return; }
            catch (IOException) when (a < 10) { Thread.Sleep(50); }
        }
    }

    private string MakePdf(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        return p;
    }

    /// <summary>File names present under <paramref name="dir"/> (recursive),
    /// sorted — used to assert "nothing else moved or vanished" by comparing
    /// a directory's contents before and after a failed undo.</summary>
    private static string[] Listing(string dir) =>
        Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    // ---------------------------------------------------------------------
    // Commit.UndoAction, each of the three branches in isolation
    // ---------------------------------------------------------------------

    [Fact]
    public void FiledCopyGoneRaisesCommitErrorAndTouchesNothing()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");
        var outcome = Commit.CommitFile(src, "SMITH", new Route { Label = "R", Path = _dest }, "insert");
        var filedPath = outcome.NewPath!;
        File.Delete(filedPath);   // the filed copy disappears behind the app's back

        var inboxBefore = Listing(_inbox);
        var destBefore = Listing(_dest);

        var ex = Assert.Throws<CommitError>(() => Commit.UndoAction(filedPath, src));

        Assert.Contains(Path.GetFileName(filedPath), ex.Message);
        Assert.Contains("no longer there", ex.Message);
        Assert.Equal(inboxBefore, Listing(_inbox));
        Assert.Equal(destBefore, Listing(_dest));
    }

    [Fact]
    public void OriginalNameReusedRaisesCommitErrorAndLeavesBothCopiesAlone()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");
        var outcome = Commit.CommitFile(src, "SMITH", new Route { Label = "R", Path = _dest }, "insert");
        var filedPath = outcome.NewPath!;
        File.WriteAllBytes(src, new byte[] { 9, 9 });   // a new file lands at the old name

        var inboxBefore = Listing(_inbox);
        var destBefore = Listing(_dest);

        var ex = Assert.Throws<CommitError>(() => Commit.UndoAction(filedPath, src));

        Assert.Contains(Path.GetFileName(src), ex.Message);
        Assert.Contains("already exists again", ex.Message);
        Assert.True(File.Exists(filedPath));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(src));   // untouched, not overwritten
        Assert.Equal(inboxBefore, Listing(_inbox));
        Assert.Equal(destBefore, Listing(_dest));
    }

    [Fact]
    public void InboxFolderGoneRaisesCommitErrorAndLeavesFiledCopyAlone()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");
        var outcome = Commit.CommitFile(src, "SMITH", new Route { Label = "R", Path = _dest }, "insert");
        var filedPath = outcome.NewPath!;
        Directory.Delete(_inbox, true);   // the whole inbox folder is gone

        var destBefore = Listing(_dest);

        var ex = Assert.Throws<CommitError>(() => Commit.UndoAction(filedPath, src));

        Assert.Contains("inbox folder is gone", ex.Message);
        Assert.True(File.Exists(filedPath));
        Assert.Equal(destBefore, Listing(_dest));
        Assert.False(Directory.Exists(_inbox));
    }

    /// <summary>app-qc-2026-08-21 final review, finding 2 (Minor):
    /// MoveNeverOverwrite's post-move refusal message is shared by
    /// CommitFile, SkipFile AND UndoAction (see MoveNeverOverwrite's own
    /// comment above its throw), but used to hardcode "the inbox" and "the
    /// original" as if <c>src</c> were always the untouched inbox file --
    /// true for CommitFile/SkipFile, backwards on undo, where <c>src</c> is
    /// the FILED copy and <c>target</c> is the inbox. A reader following
    /// that old prose on an undo would delete the just-restored inbox copy
    /// and leave the filed one behind -- the one case where both copies are
    /// NOT interchangeable, since only the restored one is back where the
    /// rest of the app expects it. Same seam PipelineTests'
    /// CommitMoveThatSurvivesAtTheSourceRefusesLoudlyAndFileStays uses to
    /// reach this branch without a real second volume.</summary>
    [Fact]
    public void UndoMoveThatSurvivesAtTheSourceDoesNotCallTheFiledCopyTheInbox()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");
        var outcome = Commit.CommitFile(src, "SMITH", new Route { Label = "R", Path = _dest }, "insert");
        var filedPath = outcome.NewPath!;
        var filedBytes = File.ReadAllBytes(filedPath);
        // The real Win32 branch never deletes src (here, the filed copy) in
        // the first place; this recreates it right after the real
        // (same-volume) move actually deletes it, so MoveNeverOverwrite's
        // post-move check sees exactly what the cross-volume case would
        // leave behind.
        Commit.SurvivingSourceHookForTests = () => File.WriteAllBytes(filedPath, filedBytes);

        var ex = Assert.Throws<CommitError>(() => Commit.UndoAction(filedPath, src));

        Assert.Contains(Path.GetFileName(filedPath), ex.Message);
        Assert.Contains("could not be removed", ex.Message);
        // The actual bug: on this path src is the filed copy and target is
        // the inbox, so a message that role-labels either one as "the
        // inbox" or "the original" is describing the wrong file no matter
        // which noun it picks. Checked as phrases, not a bare "inbox"
        // substring -- src's own interpolated path legitimately contains
        // the folder name "inbox" (see _inbox above) regardless of wording.
        Assert.DoesNotContain("the inbox", ex.Message);
        Assert.DoesNotContain("original", ex.Message);
        Assert.True(File.Exists(filedPath));
        Assert.Equal(filedBytes, File.ReadAllBytes(filedPath));
        Assert.True(File.Exists(src));   // the restored copy also survived
    }

    // ---------------------------------------------------------------------
    // Session.UndoLast(): a failed undo must leave session state untouched
    // ---------------------------------------------------------------------

    private (Session Session, History History, long RowId) CommitOneForUndo(
        out string filedPath, out string src)
    {
        var cfg = new Config { Inbox = _inbox, Deferred = _deferred };
        var history = new History(Path.Combine(_root, "h_" + Guid.NewGuid() + ".sqlite"));
        var session = new Session(cfg, history, _cfgPath);
        src = MakePdf(_inbox, "20240115--111111.pdf");
        session.Start(new[] { src });
        var outcome = session.CommitCurrent("SMITH", new Route { Label = "R", Path = _dest });
        filedPath = outcome.NewPath!;
        return (session, history, session.RowIds[0]);
    }

    private static void AssertRowNotReverted(History history, long rowId)
    {
        var row = history.Rows().Single(r => Convert.ToInt64(r["id"]) == rowId);
        Assert.Equal(0L, Convert.ToInt64(row["reverted"]));
    }

    [Fact]
    public void FailedUndoFromMissingFiledCopyLeavesSessionStateAlone()
    {
        var (session, history, rowId) = CommitOneForUndo(out var filedPath, out _);
        using var _h = history;
        File.Delete(filedPath);

        Assert.Throws<CommitError>(() => session.UndoLast());

        Assert.True(session.CanUndo);        // the undo entry is still there
        Assert.Equal(1, session.Filed);      // counters unchanged
        Assert.Equal(0, session.Skipped);
        Assert.Equal(1, session.Pos);        // queue position unchanged
        AssertRowNotReverted(history, rowId);   // the log must not lie
    }

    [Fact]
    public void FailedUndoFromReusedOriginalNameLeavesSessionStateAlone()
    {
        var (session, history, rowId) = CommitOneForUndo(out _, out var src);
        using var _h = history;
        File.WriteAllBytes(src, new byte[] { 7 });

        Assert.Throws<CommitError>(() => session.UndoLast());

        Assert.True(session.CanUndo);
        Assert.Equal(1, session.Filed);
        Assert.Equal(0, session.Skipped);
        Assert.Equal(1, session.Pos);
        AssertRowNotReverted(history, rowId);
    }

    [Fact]
    public void FailedUndoFromVanishedInboxLeavesSessionStateAlone()
    {
        var (session, history, rowId) = CommitOneForUndo(out _, out _);
        using var _h = history;
        Directory.Delete(_inbox, true);

        Assert.Throws<CommitError>(() => session.UndoLast());

        Assert.True(session.CanUndo);
        Assert.Equal(1, session.Filed);
        Assert.Equal(0, session.Skipped);
        Assert.Equal(1, session.Pos);
        AssertRowNotReverted(history, rowId);
    }

    // ---------------------------------------------------------------------
    // Step 5: the FileExistsRace leak. MoveNeverOverwrite throws a private
    // nested FileExistsRace when the target appears at the very last
    // instant. CommitFile catches and retries it; UndoAction did not catch
    // it at all, so a race in the tiny window between the ":94" guard and
    // the move would let a private type escape OrdoSort.Core, and the user
    // would see the generic "didn't finish" dialog instead of the same
    // actionable "already exists again" message its sibling guard produces
    // two lines earlier. Commit.RaceHookForTests is a test-only seam (see
    // Commit.cs) that deterministically reproduces the race by recreating
    // the file in that exact window, rather than relying on real thread
    // timing, which this codebase has documented problems trusting.
    // ---------------------------------------------------------------------

    [Fact]
    public void RaceAtTheFinalMomentIsReportedAsTheSameActionableCommitError()
    {
        var src = MakePdf(_inbox, "20240115--111111.pdf");
        var outcome = Commit.CommitFile(src, "SMITH", new Route { Label = "R", Path = _dest }, "insert");
        var filedPath = outcome.NewPath!;

        // Recreate `src` exactly between UndoAction's own File.Exists(originalPath)
        // guard and its call to MoveNeverOverwrite — the collision
        // MoveNeverOverwrite's internal guard turns into FileExistsRace.
        Commit.RaceHookForTests = () => File.WriteAllBytes(src, new byte[] { 1 });

        var ex = Assert.Throws<CommitError>(() => Commit.UndoAction(filedPath, src));

        Assert.Contains(Path.GetFileName(src), ex.Message);
        Assert.Contains("already exists again", ex.Message);
        Assert.True(File.Exists(filedPath));   // the filed copy never moved
    }
}

/// <summary>Declares the shared collection <see cref="UndoFailureTests.Name"/>
/// names. Deliberately holds no <c>ICollectionFixture</c> — see UndoFailureTests's
/// class doc for why nothing here needs one.</summary>
[CollectionDefinition(UndoFailureTests.Name)]
public class UndoRaceCollection
{
}

/// <summary>Mirrors OrdoSort.Wpf.Tests.CrashDirTestCollectionMembershipTests:
/// this fix is xUnit collection membership, not a code path, so a
/// timing-based test would either always pass or be flaky, never a reliable
/// "fails without the fix" (a scheduler interleaving on the order of
/// microseconds can't be forced to reproduce on demand). This instead pins
/// the mechanism the fix actually relies on: every class that touches the
/// static <see cref="Commit.RaceHookForTests"/> seam —
/// <see cref="UndoFailureTests"/> (sets it), <see cref="PipelineTests"/>
/// (drives <see cref="Commit.UndoAction"/> from SessionUndoRoundTrip), and
/// <see cref="AuditFailureTests"/> (drives it from
/// UndoStillWorksAfterAnUnrecordedCommit) — must declare the SAME
/// <c>[Collection(...)]</c> name, since that, not anything about any one
/// class's own behavior, is what stops xUnit from ever running them
/// concurrently. Checked pairwise (not just "all equal one fixed string") so
/// that dropping the attribute from ANY one of the three — including
/// AuditFailureTests, the one the first fix pass missed — fails this test.
/// Pre-fix, none of the three classes had a [Collection] attribute at all, so
/// every name below was null and this failed; a future edit that drops any
/// one attribute or typos its name fails it again.
///
/// Task-4 fix-round-1 review (2026-08-22, QC-03) widened what this class
/// guards. <see cref="Commit.SurvivingSourceHookForTests"/> (the QC-03 test
/// seam) sits inside the shared private <c>Commit.MoveNeverOverwrite</c>
/// itself, not one call site, so it fires unconditionally for every caller —
/// <c>CommitFile</c>, <c>SkipFile</c>, and <c>UndoAction</c> alike — whether
/// or not that caller's own test ever touches a hook. That means the
/// invariant isn't "every class that sets a hook shares this collection" but
/// "every class that reaches <c>MoveNeverOverwrite</c> at all does" — a
/// stray, already-torn-down closure from a finished <see cref="PipelineTests"/>
/// test can otherwise fire mid-move in a completely unrelated class and throw
/// a wrong exception type into its assertion. A grep across tests/ for
/// <c>CommitFile(</c>/<c>SkipFile(</c>/<c>UndoAction(</c>/<c>CommitCurrent(</c>/
/// <c>SkipCurrent(</c>/<c>UndoLast(</c> confirms five classes reach it:
/// the original three above, plus <see cref="CommitSkipFileTests"/> and
/// <see cref="SessionDeferredResolutionTests"/> (both call
/// <c>Commit.SkipFile</c>/<c>Session.SkipCurrent</c> and, pre-fix, had no
/// <c>[Collection]</c> attribute at all). Same pairwise-anchored-to-
/// UndoFailureTests shape as the original three, for the same reason.
///
/// Task-4 fix-round-2 review (2026-08-22, QC-03): the five hand-written
/// tests above are exactly the trap this whole QC pass exists to close —
/// a hardcoded list of today's five classes catches nothing about a sixth
/// class a future change adds. <see cref="EveryClassThatReachesMoveNeverOverwriteSharesTheCollection"/>
/// below is the structural replacement: it derives its own class set by
/// reading every .cs file under tests/OrdoSort.Core.Tests off disk (same
/// repo-root walk as DataGridSizingCoverageTests/TextWrapCoverageTests in
/// OrdoSort.Wpf.Tests) instead of naming classes, so a new caller is a
/// candidate the moment it exists. The five tests above are kept anyway —
/// they're cheap, they name today's specific set for a reader who doesn't
/// want to trust a regex, and dropping them recovers nothing the new test
/// doesn't already cover more generally.</summary>
public class UndoRaceTestCollectionMembershipTests
{
    // Reads the [Collection("...")] name via CustomAttributeData's
    // constructor argument rather than CollectionAttribute.Name — robust
    // against exactly which xunit.core build resolves at compile time, and
    // it's the constructor argument, not a settled property, that xUnit's
    // own discovery reads to group classes into one collection.
    private static string? CollectionNameOf(Type t) =>
        t.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Xunit.CollectionAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    [Fact]
    public void UndoFailureTestsSharesPipelineTestsCollection()
    {
        var undoCollection = CollectionNameOf(typeof(UndoFailureTests));
        var pipelineCollection = CollectionNameOf(typeof(PipelineTests));

        Assert.NotNull(undoCollection);
        Assert.Equal(pipelineCollection, undoCollection);
    }

    [Fact]
    public void AuditFailureTestsSharesUndoFailureTestsCollection()
    {
        var undoCollection = CollectionNameOf(typeof(UndoFailureTests));
        var auditCollection = CollectionNameOf(typeof(AuditFailureTests));

        Assert.NotNull(undoCollection);
        Assert.Equal(undoCollection, auditCollection);
    }

    [Fact]
    public void AuditFailureTestsSharesPipelineTestsCollection()
    {
        var pipelineCollection = CollectionNameOf(typeof(PipelineTests));
        var auditCollection = CollectionNameOf(typeof(AuditFailureTests));

        Assert.NotNull(pipelineCollection);
        Assert.Equal(pipelineCollection, auditCollection);
    }

    [Fact]
    public void CommitSkipFileTestsSharesUndoFailureTestsCollection()
    {
        var undoCollection = CollectionNameOf(typeof(UndoFailureTests));
        var commitSkipCollection = CollectionNameOf(typeof(CommitSkipFileTests));

        Assert.NotNull(undoCollection);
        Assert.Equal(undoCollection, commitSkipCollection);
    }

    [Fact]
    public void SessionDeferredResolutionTestsSharesUndoFailureTestsCollection()
    {
        var undoCollection = CollectionNameOf(typeof(UndoFailureTests));
        var sessionDeferredCollection = CollectionNameOf(typeof(SessionDeferredResolutionTests));

        Assert.NotNull(undoCollection);
        Assert.Equal(undoCollection, sessionDeferredCollection);
    }

    // -----------------------------------------------------------------
    // Structural guard (2026-08-22, QC-03 fix-round-2): derives its own
    // class set instead of naming one, so a sixth class reaching
    // Commit.MoveNeverOverwrite is caught without anyone updating a list.
    // -----------------------------------------------------------------

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "couldn't find OrdoSort.sln walking up from " + AppContext.BaseDirectory +
            " — this test reads its own project's .cs sources off disk and needs the repo root.");
    }

    // A class declaration up to its opening brace. [^{]* covers a base/
    // interface list ("class Foo : IDisposable") without crossing into the
    // NEXT class — a base list can't itself contain a raw '{' in valid C#.
    private static readonly Regex ClassDeclaration =
        new(@"\bpublic\s+(?:sealed\s+)?class\s+(?<name>\w+)\b[^{]*\{", RegexOptions.Compiled);

    // The methods Commit.MoveNeverOverwrite sits behind: two direct
    // (Commit.CommitFile/SkipFile/UndoAction) and three via Session
    // (.CommitCurrent/.SkipCurrent/.UndoLast). Matches only a real call —
    // MethodName immediately followed by '(' — not a bare mention.
    private static readonly Regex SeamCall = new(
        @"\b(?:Commit\.CommitFile|Commit\.SkipFile|Commit\.UndoAction|" +
        @"\.CommitCurrent|\.SkipCurrent|\.UndoLast)\s*\(", RegexOptions.Compiled);

    // This file's own class docs above talk ABOUT those method names in
    // prose ("grep for CommitFile(/SkipFile(/…") — real risk of the scan
    // matching its own commentary rather than real calls. Strips block
    // comments, then truncates every line at its first "//" (which also
    // removes "///" doc comments, "//" being a prefix of it). Good enough
    // for this project's actual style: no file under tests/OrdoSort.Core.Tests
    // puts "//" inside a string literal on a line that also declares a
    // class or calls one of the seam methods.
    private static string StripComments(string source)
    {
        var noBlocks = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var lines = noBlocks.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var slashes = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (slashes >= 0) lines[i] = lines[i][..slashes];
        }
        return string.Join('\n', lines);
    }

    /// <summary>Every top-level class name under tests/OrdoSort.Core.Tests
    /// whose OWN body — not a sibling class living in the same file, like
    /// this one's own three neighbours in UndoFailureTests.cs — contains a
    /// real call to a method that reaches Commit.MoveNeverOverwrite. Reads
    /// source off disk rather than reflecting over compiled IL because the
    /// call site, not the compiled type, is what determines whether a class
    /// needs the collection — a class could reference Session without ever
    /// calling CommitCurrent/SkipCurrent/UndoLast on it.
    ///
    /// Attributes each call to the CLOSEST class declaration textually
    /// before it, by position, rather than brace-matching each class's own
    /// closing brace. Deliberately: this project's own top-level test
    /// classes are always siblings in a file, never nested (confirmed by
    /// reading every file this scan covers), so "nearest declaration above"
    /// already identifies the right owner. Brace-counting was the first
    /// attempt and it broke on real fixture data — ConfigSplitTests.cs
    /// deliberately writes the malformed JSON literal "{ not json" to prove
    /// Config.Load fails readably, a single un-matched '{' inside an
    /// ordinary string literal that a depth counter can't tell from real
    /// code without also tokenizing every other kind of C# string (verbatim,
    /// interpolated, raw). Position order sidesteps that class of problem
    /// entirely — it never needs to know where a class ends.</summary>
    private static List<string> ClassesReachingMoveNeverOverwrite()
    {
        var dir = Path.Combine(FindRepoRoot(), "tests", "OrdoSort.Core.Tests");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
        {
            var source = StripComments(File.ReadAllText(file));
            var classStarts = ClassDeclaration.Matches(source)
                .Select(m => (Index: m.Index, Name: m.Groups["name"].Value))
                .OrderBy(x => x.Index)
                .ToList();
            foreach (Match call in SeamCall.Matches(source))
            {
                var owner = classStarts.LastOrDefault(c => c.Index <= call.Index);
                if (owner.Name is not null)
                    found.Add(owner.Name);
            }
        }
        return found;
    }

    [Fact]
    public void EveryClassThatReachesMoveNeverOverwriteSharesTheCollection()
    {
        var classes = ClassesReachingMoveNeverOverwrite().Distinct(StringComparer.Ordinal).ToList();

        // Same "did the scan actually examine anything" floor as
        // DataGridSizingCoverageTests/TextWrapCoverageTests: a scan that
        // quietly matches nothing (a moved directory, a renamed method)
        // would make the loop below pass vacuously — exactly the QC-09
        // failure mode this whole pass exists to close. Five classes reach
        // the seam today (the same five the hand-written tests above name),
        // so 5 is a floor with zero slack, not headroom for growth: any drop
        // below it means the scan broke, not that call sites went away.
        Assert.True(classes.Count >= 5,
            $"only found {classes.Count} class(es) whose source calls a method that reaches " +
            "Commit.MoveNeverOverwrite (CommitFile/SkipFile/UndoAction/CommitCurrent/SkipCurrent/" +
            "UndoLast) under tests/OrdoSort.Core.Tests — the scan looks broken (moved directory? " +
            "renamed method?), not that the app genuinely shrank to that few call sites: " +
            string.Join(", ", classes));

        var assembly = typeof(UndoFailureTests).Assembly;
        var unserialized = classes
            .Select(name => (Name: name, Type: assembly.GetTypes()
                .FirstOrDefault(t => t.Namespace == "OrdoSort.Core.Tests" && t.Name == name)))
            .Select(x => (x.Name, x.Type, Collection: x.Type is null ? null : CollectionNameOf(x.Type)))
            .Where(x => x.Collection != UndoFailureTests.Name)
            .ToList();

        Assert.True(unserialized.Count == 0,
            "these classes call a method that reaches the shared Commit.MoveNeverOverwrite — where " +
            "the QC-03 seam Commit.SurvivingSourceHookForTests fires unconditionally for every " +
            "caller, armed or not — but do not declare [Collection(UndoFailureTests.Name)], so " +
            "xUnit can run them concurrently with PipelineTests (the class that arms that seam) and " +
            "let a stray, already-torn-down closure fire mid-move inside them: " +
            string.Join(", ", unserialized.Select(x =>
                x.Type is null ? $"{x.Name} (found calling the seam, but no compiled type by that " +
                                  "name in OrdoSort.Core.Tests — check for a typo in the class name)"
                               : $"{x.Name} (in \"{x.Collection ?? "<no [Collection] attribute>"}\")")));
    }
}

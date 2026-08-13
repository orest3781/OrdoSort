namespace OrdoSort.Core.Tests;

using System.Reflection;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

/// <summary>2026-08-06 audit finding 1.2[V]: across all of OrdoSort.Core there
/// are exactly three places that can delete or overwrite — a self-created
/// write probe (benign), backup pruning, and Unlock. Unlock's two gaps:
///
/// - the buffered path's `place` used to be `File.WriteAllBytes(target, ...)`.
///   `target` came from CollisionFree, so it was free AT CHECK TIME — but
///   WriteAllBytes truncates whatever is there NOW, and on the shared folders
///   this app targets another station can create that exact name in the gap.
/// - PlaceAndSwap's failure paths called RemoveQuietly(target) unconditionally,
///   deleting `target` even when this call never created it.
///
/// These tests force that gap deterministically via Unlock.RaceHookForTests
/// (same shape as Commit.RaceHookForTests / AtomicPlace.BeforeAttempt): it
/// fires with the exact CollisionFree-picked path right before `place` tries
/// its exclusive create, so a peer file can be planted in the precise window
/// instead of relying on real thread timing. Both the buffered path (the one
/// the audit named) and the streamed path (which the brief says to verify
/// rather than assume safe — File.Move's two-argument overload is already
/// create-only) are covered, since PlaceAndSwap's cleanup gating is shared
/// code between them.
///
/// Final review, Important 2 (2026-08-06): this class and
/// <see cref="UnlockTests"/> are the complete set (grepped across tests/)
/// that mutate the process-wide, unsynchronized
/// <see cref="Unlock.LargeFileThresholdBytes"/> — this class only in
/// <see cref="StreamedUnlockNeverOverwritesAPeerCreatedFile"/>, UnlockTests
/// in four places. Neither declared an xUnit [Collection] before this fix,
/// so each was its own implicit collection and xUnit ran them in parallel by
/// default. If one of UnlockTests' threshold=1 tests were mid-flight while
/// <see cref="BufferedUnlockNeverTruncatesAPeerCreatedFile"/> ran here — the
/// one test that exists specifically to guard finding 1.2's fix, the
/// FileMode.CreateNew gate that stops the buffered path truncating a peer's
/// file — that test would silently run the STREAMING path instead
/// (Unlock.UnlockPdf reads the shared static exactly once, at entry) while
/// still passing every one of its own assertions: both paths return "error"
/// with the source untouched and nothing archived. A regression of the
/// buffered path's create-only fix could go green on a lucky schedule, on
/// exactly the test meant to catch it. Same defect class, same fix, as
/// Commit.RaceHookForTests (see UndoFailureTests's class doc): put every
/// class that touches the shared static into one collection (<see
/// cref="Name"/>) so xUnit's own "never run two classes in the same
/// collection concurrently" rule serializes them — pinned by
/// <see cref="UnlockThresholdTestCollectionMembershipTests"/> rather than a
/// timing-based test, for the same reason UndoRaceTestCollectionMembershipTests
/// gives: a scheduler interleaving on the order of microseconds can't be
/// forced to reproduce on demand, so a test relying on it would either
/// always pass or be flaky. Belt-and-braces on top of that structural fix:
/// BufferedUnlockNeverTruncatesAPeerCreatedFile also asserts, from inside
/// the race hook itself, that no streaming-path temp file
/// ("ordosort_unlock_*.pdf" in Path.GetTempPath()) exists at that instant —
/// UnlockStreaming always writes and verifies that file BEFORE PlaceAndSwap
/// ever invokes the hook, so its absence is direct, positive proof that the
/// buffered path — not the streamed one — is what actually ran, not just an
/// inference from the collection attribute being present.</summary>
[Collection(Name)]
public class UnlockNeverOverwritesTests : IDisposable
{
    public const string Name = "Unlock large-file threshold collection";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "unlockrace_" + Guid.NewGuid());
    public UnlockNeverOverwritesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Unlock.RaceHookForTests = null;
        Directory.Delete(_dir, recursive: true);
    }

    private string MakeEncrypted(string name, string userPw = "secret")
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.SecuritySettings.UserPassword = userPw;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPw;
        doc.Save(path);
        return path;
    }

    private static bool NeedsPassword(string path)
    {
        try { using var _ = PdfReader.Open(path, PdfDocumentOpenMode.Import); return false; }
        catch { return true; }
    }

    /// <summary>(a) With a file already present at the name CollisionFree
    /// would pick, the buffered unlock must NOT truncate it — the pre-existing
    /// content must survive byte-for-byte. Empty suffix + same-folder dest is
    /// the swapInPlace case, so the picked name is deterministic:
    /// "&lt;stem&gt;.unlocking.tmp" beside the source — the race hook plants a
    /// marker there right before the exclusive create is attempted, simulating
    /// a peer station claiming that exact temp name in the window CollisionFree
    /// cannot close.</summary>
    [Fact]
    public void BufferedUnlockNeverTruncatesAPeerCreatedFile()
    {
        var src = MakeEncrypted("dup.pdf");
        var expectedTarget = Path.Combine(_dir, "dup.unlocking.tmp");
        var peerContent = new byte[] { 9, 9, 9, 9, 9 };

        Unlock.RaceHookForTests = t =>
        {
            // Teeth for the shared-collection fix above (final review,
            // Important 2): prove this run actually took the BUFFERED path
            // under test, not the streamed one. UnlockStreaming writes and
            // verifies its local temp file ("ordosort_unlock_*.pdf" in
            // Path.GetTempPath()) BEFORE PlaceAndSwap ever reaches this
            // hook, so if one existed here, LargeFileThresholdBytes was not
            // what this test assumes — exactly what an unguarded, still-
            // running UnlockTests could have caused. (Confirmed by
            // temporarily setting Unlock.LargeFileThresholdBytes = 1 above
            // this line: with that override in place, this assertion fails
            // every time, because the streaming path's temp file is
            // present when the hook fires.)
            Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "ordosort_unlock_*.pdf"));
            if (t == expectedTarget) File.WriteAllBytes(expectedTarget, peerContent);
        };

        var r = Unlock.UnlockPdf(src, "secret", suffix: "");

        Assert.Equal("error", r.Status);
        Assert.True(File.Exists(expectedTarget));
        Assert.Equal(peerContent, File.ReadAllBytes(expectedTarget));   // NOT truncated
        Assert.True(NeedsPassword(src));                                // original untouched
        Assert.False(Directory.Exists(Path.Combine(_dir,
            "locked_archive_" + DateTime.Now.ToString("yyyyMMdd"))));   // nothing archived
    }

    /// <summary>Verifies the streamed path too: item 2 of the brief says the
    /// large-file path already builds in a local temp and moves into place
    /// with File.Move, which does not overwrite — but says to verify that
    /// rather than assume it. This proves it: forced onto the streaming path
    /// via LargeFileThresholdBytes, a peer file planted at the picked target
    /// survives the attempted File.Move untouched.</summary>
    [Fact]
    public void StreamedUnlockNeverOverwritesAPeerCreatedFile()
    {
        var was = Unlock.LargeFileThresholdBytes;
        Unlock.LargeFileThresholdBytes = 1;   // every file takes the streaming path
        try
        {
            var src = MakeEncrypted("dup2.pdf");
            var expectedTarget = Path.Combine(_dir, "dup2.unlocking.tmp");
            var peerContent = new byte[] { 4, 5, 6, 7 };

            Unlock.RaceHookForTests = t =>
            {
                if (t == expectedTarget) File.WriteAllBytes(expectedTarget, peerContent);
            };

            var r = Unlock.UnlockPdf(src, "secret", suffix: "");

            Assert.Equal("error", r.Status);
            Assert.True(File.Exists(expectedTarget));
            Assert.Equal(peerContent, File.ReadAllBytes(expectedTarget));
            Assert.True(NeedsPassword(src));
        }
        finally { Unlock.LargeFileThresholdBytes = was; }
    }

    /// <summary>(b) When `place` fails and `target` was NOT created by this
    /// call, the pre-existing file must still be there afterwards — restated
    /// as an explicit "nothing else in the folder changed" check (a stronger
    /// form of (a) that also rules out any other file appearing or
    /// disappearing, e.g. an archive folder getting created despite the
    /// failure).</summary>
    [Fact]
    public void FailedPlaceLeavesTheFolderExactlyAsThePeerLeftIt()
    {
        var src = MakeEncrypted("dup3.pdf");
        var expectedTarget = Path.Combine(_dir, "dup3.unlocking.tmp");
        var peerContent = new byte[] { 1, 2, 3 };

        Unlock.RaceHookForTests = t =>
        {
            if (t == expectedTarget) File.WriteAllBytes(expectedTarget, peerContent);
        };

        Unlock.UnlockPdf(src, "secret", suffix: "");

        var listing = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "dup3.pdf", "dup3.unlocking.tmp" }, listing);
        Assert.Equal(peerContent, File.ReadAllBytes(expectedTarget));
    }

    /// <summary>2026-08-06 audit finding 1.3[A]: a crash between the two
    /// in-place moves leaves the intermediate file on disk under whatever
    /// name PlaceAndSwap picked. Before this fix that name was
    /// "&lt;stem&gt;.unlocking.pdf" — which both Scanner.Eligible (any
    /// non-insert mode matches ANY name ending ".pdf") and
    /// FolderMonitor.ParseFiletypes/TypeMatches (matches on
    /// Path.GetExtension, which returns ".pdf" for that name too) would
    /// treat as a real document. RaceHookForTests fires with the exact
    /// CollisionFree-picked path immediately before the exclusive create —
    /// this test lets the call proceed normally (no injected failure) and
    /// just captures that real name, so the assertion is tied to what
    /// production code actually picks rather than a name re-typed in the
    /// test.</summary>
    [Fact]
    public void TheInPlaceSwapIntermediateNameCannotReenterTheQueue()
    {
        var src = MakeEncrypted("escapee.pdf");
        string? captured = null;
        Unlock.RaceHookForTests = t => captured = t;
        try
        {
            var r = Unlock.UnlockPdf(src, "secret", suffix: "");
            Assert.True(r.Ok);   // the rename must not change the outcome
            Assert.True(r.InPlace);
            Assert.Equal(src, r.NewPath);
            Assert.True(File.Exists(r.ArchivedTo));   // locked original archived
        }
        finally { Unlock.RaceHookForTests = null; }

        Assert.NotNull(captured);
        var intermediateName = Path.GetFileName(captured!);

        // No scan mode treats the intermediate as something to file: every
        // non-insert mode matches ANY "*.pdf", and insert mode additionally
        // requires the name to end in ".pdf" (GeneratedRegex(@"^.+--.+\.pdf$"))
        // — this name doesn't, regardless of what the stem contains.
        foreach (var mode in Naming.Modes)
            Assert.False(Scanner.Eligible(intermediateName, mode),
                $"Scanner.Eligible(\"{intermediateName}\", \"{mode}\") should be false");

        // A watch-folder tile filtered to "pdf" — the ordinary inbox filter —
        // must not count a leftover intermediate file either.
        var watchDir = Path.Combine(_dir, "watch");
        Directory.CreateDirectory(watchDir);
        File.WriteAllText(Path.Combine(watchDir, intermediateName), "leftover");
        var wf = new WatchFolder { Label = "Inbox", Path = watchDir, Filetypes = "pdf" };
        var status = FolderMonitor.Status(wf, Array.Empty<string>());
        Assert.Equal(0, status.Count);
    }
}

/// <summary>Declares the shared collection <see cref="UnlockNeverOverwritesTests.Name"/>
/// names. No ICollectionFixture: each class already builds and tears down
/// its own isolated temp root per test, and Unlock.LargeFileThresholdBytes
/// is restored in a finally by every test that changes it — nothing needs
/// to be built once and shared. Same reasoning as
/// OrdoSort.Core.Tests.UndoRaceCollection.</summary>
[CollectionDefinition(UnlockNeverOverwritesTests.Name)]
public class UnlockThresholdCollection
{
}

/// <summary>Mirrors OrdoSort.Core.Tests.UndoRaceTestCollectionMembershipTests:
/// this fix is xUnit collection membership, not a code path, so a timing-
/// based test would either always pass or be flaky, never a reliable "fails
/// without the fix" (a scheduler interleaving on the order of microseconds
/// can't be forced to reproduce on demand). This instead pins the mechanism
/// the fix actually relies on: both classes that touch the static
/// <see cref="Unlock.LargeFileThresholdBytes"/> —
/// <see cref="UnlockNeverOverwritesTests"/> (sets it once) and
/// <see cref="UnlockTests"/> (sets it four times) — must declare the SAME
/// <c>[Collection(...)]</c> name, since that, not anything about either
/// class's own behavior, is what stops xUnit from ever running them
/// concurrently. A grep for <c>LargeFileThresholdBytes</c> across tests/
/// confirms these two are the complete set. Pre-fix, neither class had a
/// [Collection] attribute at all, so both names below were null and this
/// failed; a future edit that drops either attribute, typos its name, or
/// adds a third class that mutates the static without joining this
/// collection needs to be caught here too.</summary>
public class UnlockThresholdTestCollectionMembershipTests
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
    public void UnlockTestsSharesUnlockNeverOverwritesTestsCollection()
    {
        var neverOverwritesCollection = CollectionNameOf(typeof(UnlockNeverOverwritesTests));
        var unlockTestsCollection = CollectionNameOf(typeof(UnlockTests));

        Assert.NotNull(neverOverwritesCollection);
        Assert.Equal(neverOverwritesCollection, unlockTestsCollection);
    }

    /// <summary>A third member, joined for a DIFFERENT reason than the other
    /// two: UnlockProbeWritesNothingTests never touches the threshold, but it
    /// snapshots "ordosort_*" in Path.GetTempPath(), and the streaming unlock
    /// path the other two force writes "ordosort_unlock_&lt;guid&gt;.pdf" into
    /// exactly that directory. Run concurrently, one class's working file
    /// lands inside the other's before/after window and fails it on a write
    /// it never made.
    ///
    /// This was latent from the moment both classes existed and only surfaced
    /// when an unrelated new test class shifted the parallel schedule — which
    /// is precisely why it is pinned here rather than left to be rediscovered
    /// the next time the schedule moves.</summary>
    [Fact]
    public void UnlockProbeWritesNothingTestsSharesTheSameCollection()
    {
        var neverOverwritesCollection = CollectionNameOf(typeof(UnlockNeverOverwritesTests));
        var probeWritesNothingCollection = CollectionNameOf(typeof(UnlockProbeWritesNothingTests));

        Assert.NotNull(neverOverwritesCollection);
        Assert.Equal(neverOverwritesCollection, probeWritesNothingCollection);
    }
}

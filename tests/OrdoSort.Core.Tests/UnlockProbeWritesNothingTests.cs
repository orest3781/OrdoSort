using PdfSharp.Pdf;

namespace OrdoSort.Core.Tests;

/// <summary>Step 6 of the readiness-probe plan (2026-08-08): Unlock.ProbeReadiness
/// must never write, move or delete anything, anywhere. This app's whole
/// promise is that a document is either where it started or where it was
/// going -- a "read-only" probe that quietly drops a temp file breaks that
/// promise even if it later cleans up after itself, so a probe run must leave
/// BOTH the fixture directory (names, sizes, mtimes) AND the top level of
/// Path.GetTempPath() exactly as they were.
///
/// Teeth, verified by hand and reverted (see the task report for the exact
/// transcript): with `File.WriteAllBytes(Path.Combine(Path.GetTempPath(),
/// "ordosort_probe_teeth_" + Guid.NewGuid() + ".tmp"), sourceBytes);` inserted
/// at the top of Unlock.ProbeReadiness (right after sourceBytes is read),
/// NothingChangesInTheFixtureDirectoryOrTemp below failed immediately with
/// the new file listed in the temp-directory diff -- proof this test would
/// actually catch a probe that writes, not just a probe that happens not
/// to.</summary>
/// <summary>Joins the same collection as UnlockNeverOverwritesTests/UnlockTests
/// — not because this class touches Unlock.LargeFileThresholdBytes (it
/// doesn't), but because of what those two DO with it. Setting the threshold
/// to 1 forces the streaming unlock path, and that path writes its working
/// copy to "ordosort_unlock_&lt;guid&gt;.pdf" in Path.GetTempPath() itself
/// (Unlock.cs). This class snapshots "ordosort_*" in that same directory
/// before and after, so a streamed unlock running concurrently lands a file
/// inside the window and this test fails on someone else's write.
///
/// Observed for real: the failure surfaced when an unrelated new test class
/// shifted xUnit's parallel schedule, not from any change to unlock or to
/// this test — the interference had been latent since both classes were
/// written. The [Collection] attribute is the whole fix; see
/// UnlockThresholdTestCollectionMembershipTests, which pins it.</summary>
[Collection(UnlockNeverOverwritesTests.Name)]
public class UnlockProbeWritesNothingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "unlockprobewrites_" + Guid.NewGuid());
    public UnlockProbeWritesNothingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeEncrypted(string name, string userPw)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.SecuritySettings.UserPassword = userPw;
        doc.SecuritySettings.OwnerPassword = "owner-" + userPw;
        doc.Save(path);
        return path;
    }

    private string MakePlain(string name)
    {
        var path = Path.Combine(_dir, name);
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.Save(path);
        return path;
    }

    private string MakeDamaged(string name)
    {
        var path = Path.Combine(_dir, name);
        var garbage = new byte[512];
        new Random(1234).NextBytes(garbage);
        File.WriteAllBytes(path, garbage);
        return path;
    }

    /// <summary>Name, size and last-write-time for every file directly in
    /// <paramref name="dir"/> -- exactly what the plan asks to snapshot, and
    /// enough to catch a probe that overwrites a fixture in place (size or
    /// mtime would change) as well as one that adds or removes a file (the
    /// name set would change).</summary>
    private static (string Name, long Size, DateTime Mtime)[] Snapshot(string dir) =>
        Directory.GetFiles(dir)
            .Select(f => (Path.GetFileName(f)!, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void NothingChangesInTheFixtureDirectoryOrTemp()
    {
        var plain = MakePlain("plain.pdf");
        var readyFirst = MakeEncrypted("ready-first.pdf", "aaa");
        var readyLater = MakeEncrypted("ready-later.pdf", "ccc");
        var needsPassword = MakeEncrypted("needs.pdf", "zzz");
        var damaged = MakeDamaged("damaged.pdf");
        var held = MakeEncrypted("held.pdf", "secret");
        var missing = Path.Combine(_dir, "missing.pdf");   // deliberately never created

        var beforeFixtures = Snapshot(_dir);
        // top-level only, and narrowed to this repo's own "ordosort_" temp
        // naming convention (same as UnlockNeverOverwritesTests' teeth-test,
        // which checks specifically for "ordosort_unlock_*.pdf"). 2026-08-08
        // fix round: an EARLIER, unscoped "any new file anywhere in
        // Path.GetTempPath()" version of this check was genuinely flaky --
        // `dotnet test OrdoSort.sln` runs Wpf.Tests as a SEPARATE concurrent
        // process sharing the same %TEMP%, and several pre-existing,
        // unrelated Wpf.Tests classes (HistoryWindowXamlTests,
        // AutoFitColumnTests, DataGridStarColumnTests) create a SQLite file
        // directly at the top level of Path.GetTempPath() named
        // "ordo_test_history_<guid>.sqlite" -- caught mid-flight by the
        // wide check, reproduced for real. That name does not start with
        // "ordosort_" (different prefix: "ordo_test_"), so narrowing to
        // this repo's actual internal convention excludes that noise while
        // still catching anything OrdoSort's own code would plausibly write
        // if this probe leaked something — proven by the teeth-proof below,
        // whose forced breakage used the name "ordosort_probe_teeth_*".
        var tempBefore = Directory.GetFiles(Path.GetTempPath(), "ordosort_*")
            .Select(f => Path.GetFileName(f)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Unlock.ProbeReadiness(plain, new[] { "irrelevant" });
        Unlock.ProbeReadiness(readyFirst, new[] { "aaa", "bbb" });
        Unlock.ProbeReadiness(readyLater, new[] { "aaa", "bbb", "ccc" });
        Unlock.ProbeReadiness(needsPassword, new[] { "nope1", "nope2" });
        Unlock.ProbeReadiness(needsPassword, Array.Empty<string>());
        Unlock.ProbeReadiness(damaged, new[] { "whatever" });
        Unlock.ProbeReadiness(missing, new[] { "whatever" });
        using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.None))
            Unlock.ProbeReadiness(held, new[] { "secret" });

        var afterFixtures = Snapshot(_dir);
        Assert.Equal(beforeFixtures, afterFixtures);

        var tempAfter = Directory.GetFiles(Path.GetTempPath(), "ordosort_*")
            .Select(f => Path.GetFileName(f)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newTempFiles = tempAfter.Except(tempBefore).ToArray();
        Assert.Empty(newTempFiles);
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>
/// Anything a station WRITES must come out identical regardless of Windows'
/// locale setting, or two stations produce different names for the same
/// document. de-DE and th-TH are used rather than the more obvious "any
/// non-English culture" pick of ja-JP: verified empirically that .NET's
/// ja-JP defaults to GregorianCalendar, so a pure "yyyyMMdd" custom pattern
/// renders identically under ja-JP and the invariant culture — it would
/// never have caught this bug. th-TH's default calendar is
/// ThaiBuddhistCalendar (year 2569, not 2026), which actually forces a
/// different string out of a Calendar-driven custom pattern.
/// </summary>
// M3 (2026-08-03 final-review): CrashLogTimestampIsCultureInvariant below
// mutates the static OrdoSort.Wpf.App._crashDir seam — the exact same one
// CopyAndTerminologyTests.TheShellsUnexpectedErrorChannelReallyReachesCrashLog
// mutates. Undeclared, this class defaults to its own (class-named) xUnit
// collection, which runs in PARALLEL with CopyAndTerminologyTests' collection
// (no xunit.runner.json disables that) — an unlucky interleaving has one
// test's crash.log land in the other's temp dir. Joining the same shared
// collection every other static/process-wide-state test in this project
// already uses (see HighlightContrastFixture's class doc) is how this suite
// isolates exactly this class of seam; this class doesn't need the
// fixture's STA Application itself (none of these three tests touch WPF),
// so it isn't taken as a constructor parameter — only the collection's
// "never run two of my classes concurrently" guarantee is needed here.
[Collection(HighlightContrastTests.Name)]
public class CultureInvariantDatesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ordoculttest_").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakeFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static void UnderCulture(string culture, Action body)
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    /// <summary>BulkRenameViewModel's preview now computes off the UI thread
    /// through a debounced probe (Task 2, 2026-08-05 debounce pair — see
    /// DebouncedProbe/BulkRenameViewModel.Refresh), so Preview doesn't reflect
    /// a property set the instant the setter returns — poll for it, same
    /// shape as SettingsViewModelTests.WaitFor.</summary>
    private static void WaitFor(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }

    // ---- BulkRenameViewModel.cs:128 — the received-date stem that rebuilds
    // an actual review filename via BulkRename.Plan/Execute ----

    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void ReviewRebuildStemIsCultureInvariant(string culture) =>
        UnderCulture(culture, () =>
        {
            var path = MakeFile("SMITH_JOHN_01_15_2024.pdf");
            var vm = new BulkRenameViewModel();
            vm.AddFilesAsync(new[] { path });
            vm.ReviewMode = true;
            vm.ReceivedDate = new DateTime(2026, 8, 2);

            // Value conjoined into the wait (not just Count == 1): the
            // AddFiles-generation compute can still land after the
            // ReviewMode/ReceivedDate ones supersede it, satisfying a
            // count-only wait on the WRONG (pre-review-mode) preview and
            // making the strict Assert.Equal below intermittently fail. Every
            // other test in this suite already conjoins the value; finding 3
            // (final review, 2026-08-05 debounce pair) brings these two in
            // line.
            WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].NewName == "20260802-SMITH-JOHN.pdf",
                "the preview should eventually compute the review-mode rebuild");
            var row = Assert.Single(vm.Preview);
            Assert.Equal("20260802-SMITH-JOHN.pdf", row.NewName);
        });

    // ---- BulkRenameViewModel.cs:188 — the edit-box seed for a stray that
    // needs a name; becomes the filename unless the user changes it ----

    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void StrayEditSeedIsCultureInvariant(string culture) =>
        UnderCulture(culture, () =>
        {
            var path = MakeFile("whatever.pdf");   // doesn't match the review layout
            var vm = new BulkRenameViewModel();
            vm.AddFilesAsync(new[] { path });
            vm.ReviewMode = true;
            vm.ReceivedDate = new DateTime(2026, 8, 2);

            // Same value-conjoined wait as above, for the same reason: a
            // count-only wait can be satisfied by the AddFiles-generation
            // compute (EditSeed == the plain filename, NeedsName == false)
            // before the ReviewMode/ReceivedDate recompute supersedes it.
            WaitFor(() => vm.Preview.Count == 1 && vm.Preview[0].EditSeed == "20260802-",
                "the preview should eventually compute the stray's date-prefixed edit seed");
            var row = Assert.Single(vm.Preview);
            Assert.True(row.NeedsName);
            Assert.Equal("20260802-", row.EditSeed);
        });

    // ---- sweep find: App.xaml.cs LogCrash — the crash.log timestamp ----

    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void CrashLogTimestampIsCultureInvariant(string culture)
    {
        var prevDir = OrdoSort.Wpf.App._crashDir;
        OrdoSort.Wpf.App._crashDir = _dir;
        try
        {
            UnderCulture(culture, () =>
                OrdoSort.Wpf.App.LogCrash(new InvalidOperationException("boom")));

            var text = File.ReadAllText(Path.Combine(_dir, "crash.log"));
            Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]", text);
            Assert.StartsWith(
                "[" + DateTime.Now.Year.ToString(CultureInfo.InvariantCulture) + "-", text);
        }
        finally
        {
            OrdoSort.Wpf.App._crashDir = prevDir;
        }
    }
}

/// <summary>M3's actual fix is xUnit collection membership, not a code path —
/// the race it closes is a scheduler interleaving on the order of
/// milliseconds, which by construction can't be forced to reproduce on
/// demand (a timing-based test would either always pass or be flaky, never a
/// reliable "fails without the fix"). This instead pins the mechanism the fix
/// actually relies on: both classes that mutate the static
/// <see cref="OrdoSort.Wpf.App._crashDir"/> seam must declare the SAME
/// <c>[Collection(...)]</c> name, since that — not anything about either
/// class's own behavior — is what stops xUnit from ever running them
/// concurrently. Pre-fix, <see cref="CultureInvariantDatesTests"/> had no
/// [Collection] attribute at all, so <c>cultureCollection</c> below was null
/// and this failed; a future edit that drops the attribute or typos the name
/// fails it again.</summary>
public class CrashDirTestCollectionMembershipTests
{
    // Reads the [Collection("...")] name via CustomAttributeData's
    // constructor argument rather than CollectionAttribute.Name — robust
    // against exactly which xunit.core build resolves at compile time,
    // and it's the constructor argument, not a settled property, that
    // xUnit's own discovery reads to group classes into one collection.
    private static string? CollectionNameOf(Type t) =>
        t.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "Xunit.CollectionAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;

    [Fact]
    public void CultureInvariantDatesTestsSharesCopyAndTerminologyTestsCollection()
    {
        var cultureCollection = CollectionNameOf(typeof(CultureInvariantDatesTests));
        var copyCollection = CollectionNameOf(typeof(CopyAndTerminologyTests));

        Assert.NotNull(cultureCollection);
        Assert.Equal(copyCollection, cultureCollection);
    }
}

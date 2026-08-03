using System.Globalization;
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
            vm.AddFiles(new[] { path });
            vm.ReviewMode = true;
            vm.ReceivedDate = new DateTime(2026, 8, 2);

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
            vm.AddFiles(new[] { path });
            vm.ReviewMode = true;
            vm.ReceivedDate = new DateTime(2026, 8, 2);

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

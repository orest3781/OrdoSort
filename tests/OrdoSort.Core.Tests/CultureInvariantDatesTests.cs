using System.Globalization;

namespace OrdoSort.Core.Tests;

/// <summary>
/// Anything a station WRITES — a folder name, a filename stem, a printed
/// label, or an audit-log row — must come out identical no matter what
/// locale Windows is set to, or two stations produce different names for
/// the same document. That is the whole point of these tests: they don't
/// merely check the format LOOKS right, they check it is the SAME string
/// under every culture.
///
/// de-DE and th-TH are used rather than the more obvious "try a non-English
/// culture" pick of ja-JP: verified empirically (see task-4 report) that
/// .NET's ja-JP defaults to GregorianCalendar, so a pure "yyyyMMdd"-style
/// custom pattern renders IDENTICALLY under ja-JP and the invariant culture
/// — it would never have caught this bug. th-TH's default calendar is
/// ThaiBuddhistCalendar (year 2569, not 2026), which is what actually forces
/// a different string out of a Calendar-driven custom pattern.
/// </summary>
public class CultureInvariantDatesTests
{
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

    // ---- Unlock.cs:42 — the dated locked_archive folder name ----

    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void LockedArchiveFolderNameIsCultureInvariant(string culture) =>
        UnderCulture(culture, () =>
        {
            var folder = Unlock.ArchiveFolderFor(
                @"C:\docs\x.pdf", new DateTime(2026, 8, 2));
            Assert.Equal(Path.Combine(@"C:\docs", "locked_archive_20260802"), folder);
        });

    // ---- History.cs:72 — the stored audit-log timestamp ----

    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void AuditLogTimestampIsCultureInvariant(string culture) =>
        UnderCulture(culture, () =>
        {
            var ts = History.UtcNow();
            // yyyy-MM-ddTHH:mm:sszzz — always a plain 4-digit Gregorian year,
            // never a Buddhist-era year or anything else calendar-shaped
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$", ts);
            Assert.StartsWith(
                DateTime.UtcNow.Year.ToString(CultureInfo.InvariantCulture) + "-", ts);
        });

    // ---- BoxLabels.cs:217,220 — printed created/destruction dates ----

    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void BoxLabelDatesAreCultureInvariant(string culture) =>
        UnderCulture(culture, () =>
        {
            var item = new BoxLabels.Item("ABCD00000001",
                new DateTime(2026, 8, 2), new DateTime(2033, 8, 2));
            var drawing = BoxLabels.ComposeDrawing(item);
            var created = drawing.Texts.Single(t => t.Text.StartsWith("CREATED"));
            var destroy = drawing.Texts.Single(t => t.Text.StartsWith("DESTROY"));
            Assert.Equal("CREATED 2026-08-02", created.Text);
            Assert.Equal("DESTROY AFTER 2033-08-02", destroy.Text);
        });

    // ---- sweep find: HistoryBackup.cs — the daily backup's file name ----

    [Theory]
    [InlineData("de-DE")]
    [InlineData("th-TH")]
    public void HistoryBackupFileNameIsCultureInvariant(string culture) =>
        UnderCulture(culture, () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "bkpcultest_" + Guid.NewGuid());
            var backups = Path.Combine(dir, "backups");
            Directory.CreateDirectory(dir);
            try
            {
                var db = Path.Combine(dir, "history.sqlite");
                File.WriteAllText(db, "x");
                var dest = HistoryBackup.BackupDaily(db, backups, new DateTime(2026, 8, 2));
                Assert.Equal("history-20260802.sqlite", Path.GetFileName(dest));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        });
}

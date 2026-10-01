using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace OrdoSort.Wpf.Tests;

/// <summary>Photos and GIFs in the filing loop (spec
/// 2026-09-29-media-filing-loop), headless: real Session, History and temp
/// folders; the viewer, the date reader and the HEIC converter are fakes.</summary>
public class MediaLoopTests
{
    /// <summary>Hands out set dates and counts how often each file is read.</summary>
    private sealed class FakeDates : IDateTakenReader
    {
        private readonly Dictionary<string, DateTaken> _dates = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unreadableOnce = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The first read of this file fails, as on a share that
        /// drops for a moment; later reads get its set date.</summary>
        public FakeDates UnreadableOnce(string fileName)
        {
            _unreadableOnce.Add(fileName);
            return this;
        }

        public FakeDates Set(string fileName, DateTime date, DateTakenSource source = DateTakenSource.Metadata)
        {
            _dates[fileName] = new DateTaken(date, source);
            return this;
        }

        public DateTaken Read(string path)
        {
            var name = Path.GetFileName(path);
            Reads[name] = Reads.GetValueOrDefault(name) + 1;
            if (_unreadableOnce.Remove(name))
                return new DateTaken(new DateTime(2099, 1, 1), DateTakenSource.Unreadable);
            return _dates.TryGetValue(name, out var taken)
                ? taken
                : new DateTaken(new DateTime(2000, 1, 1), DateTakenSource.Modified);
        }
    }

    /// <summary>"Converts" a HEIC by pointing at a made-up JPEG path.</summary>
    private sealed class FakePreviews : IPreviewImages
    {
        public List<string> Prepared { get; } = new();

        public bool NeedsConversion(string path) =>
            path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase);

        public string Prepare(string path)
        {
            Prepared.Add(path);
            return Path.ChangeExtension(path, ".preview.jpg");
        }
    }

    private static ShellFixture Started(FakeDates dates, FakePreviews? previews = null,
        Action<Config>? tweak = null, params string[] files)
    {
        var fx = new ShellFixture(cfg =>
        {
            cfg.Media.Enabled = true;
            tweak?.Invoke(cfg);
        }, dates: dates, previews: previews ?? new FakePreviews());
        foreach (var file in files) fx.AddInboxFile(file, "photo");
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();
        return fx;
    }

    [Fact]
    public async Task APhotoIsNamedByItsDateAndFiledIntoTheMediaFolder()
    {
        var dates = new FakeDates().Set("IMG_4031.JPG", new DateTime(2026, 9, 29));
        using var fx = Started(dates, files: "IMG_4031.JPG");

        Assert.Equal("Taken 29 Sep 2026 · from the photo", fx.Shell.TakenLine);
        Assert.False(fx.Shell.TakenIsGuess);
        fx.Shell.TypedName = "SMITH KITCHEN";
        Assert.Equal("20260929-SMITH KITCHEN.jpg", fx.Shell.Preview);

        await fx.Shell.OnRouteAsync(0);

        Assert.True(File.Exists(Path.Combine(fx.RouteDir, "Media", "20260929-SMITH KITCHEN.jpg")));
        Assert.False(File.Exists(Path.Combine(fx.Inbox, "IMG_4031.JPG")));
        Assert.Equal(1, dates.Reads["IMG_4031.JPG"]);   // read once, shared by the screen and the move
    }

    [Fact]
    public async Task WithTheRouteFolderChosenAPhotoGoesStraightIntoIt()
    {
        var dates = new FakeDates().Set("a.png", new DateTime(2025, 1, 2));
        using var fx = Started(dates, tweak: cfg => cfg.Media.Folder = MediaSettings.FolderRoute, files: "a.png");

        fx.Shell.TypedName = "JONES";
        await fx.Shell.OnRouteAsync(0);

        Assert.True(File.Exists(Path.Combine(fx.RouteDir, "20250102-JONES.png")));
        Assert.False(Directory.Exists(Path.Combine(fx.RouteDir, "Media")));
    }

    [Fact]
    public async Task PdfsAndPhotosFileSideBySideInOneSession()
    {
        var dates = new FakeDates().Set("IMG_1.jpg", new DateTime(2026, 9, 29));
        using var fx = Started(dates, files: new[] { "20240115--111111.pdf", "IMG_1.jpg" });
        Assert.Equal("2 files left", fx.Shell.ProgressLine);
        Assert.Equal("", fx.Shell.TakenLine);             // a PDF has no date line

        fx.Shell.TypedName = "SMITH";
        await fx.Shell.OnRouteAsync(0);
        Assert.True(File.Exists(Path.Combine(fx.RouteDir, "20240115-SMITH-111111.pdf")));

        Assert.Equal("IMG_1.jpg", fx.Shell.CurrentFilename);
        Assert.Equal("Taken 29 Sep 2026 · from the photo", fx.Shell.TakenLine);
        fx.Shell.TypedName = "SMITH";
        await fx.Shell.OnRouteAsync(0);
        Assert.True(File.Exists(Path.Combine(fx.RouteDir, "Media", "20260929-SMITH.jpg")));
    }

    /// <summary>A photo is shown at its own size, and a session that starts
    /// on one still fits the pane to its first PDF.</summary>
    [Fact]
    public async Task APhotoIsShownWithoutThePdfZoomAndThePdfsStillFit()
    {
        var fx = new ShellFixture(cfg => cfg.Media.Enabled = true, dates: new FakeDates(), previews: new FakePreviews());
        using var _ = fx;
        fx.AddInboxFile("0001.jpg", "photo");                 // sorts first
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(792);
            page.Height = XUnit.FromPoint(612);
            doc.Save(Path.Combine(fx.Inbox, "20240115--111111.pdf"));
        }
        var fitted = new List<double>();
        fx.Shell.FitViewerToPage += fitted.Add;
        fx.Shell.Initialize();
        fx.Shell.StartProcessing();

        Assert.EndsWith("0001.jpg", fx.Viewer.Shown.Last());
        Assert.Null(fx.Viewer.ShownPages.Last());
        Assert.Equal(792d / 612d, Assert.Single(fitted), 3);

        await fx.Shell.OnRouteAsync(0);
        Assert.EndsWith("20240115--111111.pdf", fx.Viewer.Shown.Last());
        Assert.NotNull(fx.Viewer.ShownPages.Last());
    }

    /// <summary>A photo that couldn't be read when it came on screen is not
    /// filed under today: the move reads it again.</summary>
    [Fact]
    public async Task AFailedDateReadIsTriedAgainAtTheMove()
    {
        var dates = new FakeDates().Set("IMG_1.jpg", new DateTime(2024, 3, 17)).UnreadableOnce("IMG_1.jpg");
        using var fx = Started(dates, files: "IMG_1.jpg");
        Assert.StartsWith("Couldn't read the photo's date", fx.Shell.TakenLine);
        Assert.True(fx.Shell.TakenIsGuess);

        fx.Shell.TypedName = "SMITH";
        await fx.Shell.OnRouteAsync(0);

        Assert.True(File.Exists(Path.Combine(fx.RouteDir, "Media", "20240317-SMITH.jpg")));
    }

    [Fact]
    public async Task HistoryRecordsTheMediaFolderAPhotoWentInto()
    {
        var dates = new FakeDates().Set("IMG_1.jpg", new DateTime(2026, 9, 29));
        using var fx = Started(dates, files: "IMG_1.jpg");
        fx.Shell.TypedName = "SMITH";

        await fx.Shell.OnRouteAsync(0);

        using var history = new History(Path.Combine(fx.Dir, Config.DefaultHistoryDb));
        var csv = Path.Combine(fx.Dir, "export.csv");
        history.ExportCsv(csv);
        Assert.Contains(Path.Combine(fx.RouteDir, "Media"), File.ReadAllText(csv));
    }

    [Fact]
    public void AModifiedDateIsFlaggedAsAGuess()
    {
        var dates = new FakeDates().Set("dance.gif", new DateTime(2022, 5, 6), DateTakenSource.Modified);
        using var fx = Started(dates, files: "dance.gif");

        Assert.Equal("Taken 6 May 2022 · from the modified date", fx.Shell.TakenLine);
        Assert.True(fx.Shell.TakenIsGuess);
    }

    [Fact]
    public async Task ATypedDateOverridesTheCamerasClock()
    {
        var dates = new FakeDates().Set("IMG_1.jpg", new DateTime(2000, 1, 1));
        using var fx = Started(dates, files: "IMG_1.jpg");

        fx.Shell.TypedName = "20250704 BEACH";
        await fx.Shell.OnRouteAsync(0);

        Assert.True(File.Exists(Path.Combine(fx.RouteDir, "Media", "20250704-BEACH.jpg")));
    }

    [Fact]
    public void AHeicIsShownThroughItsPreviewNotItself()
    {
        var previews = new FakePreviews();
        using var fx = Started(new FakeDates(), previews, files: "IMG_9.HEIC");

        Assert.Single(previews.Prepared);
        Assert.EndsWith("IMG_9.preview.jpg", fx.Viewer.Shown.Last());
    }

    [Fact]
    public async Task ASetAsidePhotoKeepsItsNameAndExtension()
    {
        using var fx = Started(new FakeDates(), files: "IMG_1.jpg");

        await fx.Shell.OnSkipAsync();

        Assert.True(File.Exists(Path.Combine(fx.Deferred, "IMG_1.jpg")));
    }

    [Fact]
    public async Task UndoPutsAFiledPhotoBack()
    {
        var dates = new FakeDates().Set("IMG_1.jpg", new DateTime(2026, 9, 29));
        using var fx = Started(dates, files: "IMG_1.jpg");
        fx.Shell.TypedName = "SMITH";
        await fx.Shell.OnRouteAsync(0);

        await fx.Shell.OnUndoAsync();

        Assert.True(File.Exists(Path.Combine(fx.Inbox, "IMG_1.jpg")));
        Assert.False(File.Exists(Path.Combine(fx.RouteDir, "Media", "20260929-SMITH.jpg")));
    }

    [Fact]
    public void PhotosStayOutOfTheQueueUntilTurnedOn()
    {
        using var fx = new ShellFixture();
        fx.AddInboxFile("IMG_1.jpg", "photo");
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();

        Assert.Equal("PDF in the inbox", fx.Shell.CountCaption);
        Assert.Equal("1 other file ignored", fx.Shell.DetailLine);
    }

    [Fact]
    public void TheDashboardCountsFilesOnceMediaIsOn()
    {
        using var fx = new ShellFixture(cfg => cfg.Media.Enabled = true);
        fx.AddInboxFile("IMG_1.jpg", "photo");
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();

        Assert.Equal("files in the inbox", fx.Shell.CountCaption);
        Assert.Equal("", fx.Shell.DetailLine);
    }

    [Theory]
    [InlineData(DateTakenSource.Metadata, "Taken 3 Jan 2026 · from the photo")]
    [InlineData(DateTakenSource.FileName, "Taken 3 Jan 2026 · from the file name")]
    [InlineData(DateTakenSource.Modified, "Taken 3 Jan 2026 · from the modified date")]
    public void TheDateLineSaysWhereTheDateCameFrom(DateTakenSource source, string expected) =>
        Assert.Equal(expected, ShellViewModel.DescribeTaken(new DateTaken(new DateTime(2026, 1, 3), source)));
}

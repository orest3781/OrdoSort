using OrdoSort.Core;

namespace OrdoSort.Core.Tests;

/// <summary>Photos and GIFs in the filing loop (spec
/// 2026-09-29-media-filing-loop): which files count, the date each was
/// taken, and how each is named and filed. In the move-hook collection:
/// it calls Commit.CommitFile/SkipFile (UndoFailureTests.Name).</summary>
[Collection(UndoFailureTests.Name)]
public class MediaFilesTests
{
    private static MediaSettings On() => new() { Enabled = true };

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Media", name);

    // ---------------------------------------------------------- which files

    [Theory]
    [InlineData("IMG_4031.JPG", true)]
    [InlineData("kitchen.heic", true)]
    [InlineData("dance.gif", true)]
    [InlineData("scan.pdf", false)]
    [InlineData("clip.mov", false)]      // video comes with the VLC release
    [InlineData("notes.txt", false)]
    [InlineData("README", false)]
    public void OnlyListedTypesAreMedia(string name, bool expected) =>
        Assert.Equal(expected, MediaFiles.IsMedia(name, On()));

    [Theory]
    [InlineData("clip.MOV", MediaKind.Video)]
    [InlineData("clip.mp4", MediaKind.Video)]
    [InlineData("IMG_1.jpg", MediaKind.Photo)]
    [InlineData("scan.pdf", MediaKind.None)]
    public void VideosAreTheirOwnKind(string name, MediaKind expected) =>
        Assert.Equal(expected, MediaFiles.KindOf(name, new MediaSettings { Enabled = true, VideosEnabled = true }));

    [Fact]
    public void VideosAndPhotosAreTurnedOnSeparately()
    {
        var videosOnly = new MediaSettings { VideosEnabled = true };
        Assert.True(MediaFiles.IsMedia("clip.mov", videosOnly));
        Assert.False(MediaFiles.IsMedia("IMG_1.jpg", videosOnly));
        Assert.False(MediaFiles.IsMedia("clip.mov", On()));
    }

    [Fact]
    public void AVideoIsNamedAndFiledLikeAPhoto()
    {
        using var tmp = new TempDir();
        var clip = tmp.File("inbox/IMG_0042.MOV");
        var route = new Route { Label = "Smith", Path = tmp.Dir("smith") };
        var media = new MediaSettings { VideosEnabled = true };

        var target = MediaFiles.TargetFor(clip, media, () => new DateTaken(new DateTime(2023, 7, 4), DateTakenSource.Metadata));
        var outcome = Commit.CommitFile(clip, "BACKYARD", route, Naming.ModeInsert, target);

        Assert.Equal(Path.Combine(route.Path, "Media", "20230704-BACKYARD.mov"), outcome.NewPath);
    }

    [Fact]
    public void NothingIsMediaWhileItIsTurnedOff() =>
        Assert.False(MediaFiles.IsMedia("IMG_4031.jpg", new MediaSettings()));

    [Fact]
    public void TypedExtensionsAreCleanedUp() =>
        Assert.Equal(new[] { ".jpg", ".png", ".gif" }, MediaFiles.ParseExtensions(" jpg, .PNG ;gif jpg pdf ."));

    [Fact]
    public void AnExtensionListedWithoutItsDotStillCounts()
    {
        var media = new MediaSettings { Enabled = true, Extensions = new() { "JPG" } };
        Assert.True(MediaFiles.IsMedia(@"C:\inbox\a.jpg", media));
    }

    [Fact]
    public void AMediaFileIsPickedUpInInsertModeWithoutItsMarker()
    {
        Assert.True(Scanner.Eligible("IMG_4031.jpg", Naming.ModeInsert, On()));
        Assert.False(Scanner.Eligible("IMG_4031.jpg", Naming.ModeInsert));
        Assert.False(Scanner.Eligible("plain.pdf", Naming.ModeInsert, On()));
    }

    [Fact]
    public void TheScanQueuesPhotosAlongsidePdfs()
    {
        using var tmp = new TempDir();
        tmp.File("20240115--111.pdf");
        tmp.File("IMG_1.jpg");
        tmp.File("notes.txt");

        var scan = Scanner.Scan(tmp.Path, "name_asc", Naming.ModeInsert, On());

        Assert.Equal(new[] { "20240115--111.pdf", "IMG_1.jpg" }, scan.Matching.Select(Path.GetFileName).Order());
        Assert.Equal(1, scan.IgnoredCount);
    }

    // ---------------------------------------------------------- date in the name

    [Theory]
    [InlineData("IMG_20260929_123456", "20260929")]
    [InlineData("PXL_20260929_101112345", "20260929")]
    [InlineData("VID-20260929-WA0001", "20260929")]
    [InlineData("IMG-20260929-WA0003", "20260929")]
    [InlineData("Screenshot 2026-09-29 at 10.11.12", "20260929")]
    [InlineData("2026-09-29 10.11.12", "20260929")]
    [InlineData("IMG20260929123456", "20260929")]
    [InlineData("20260929_101112", "20260929")]
    public void ADateInTheFileNameIsFound(string name, string expected) =>
        Assert.Equal(expected, MediaFiles.FromFileName(name)?.ToString("yyyyMMdd"));

    [Theory]
    [InlineData("IMG_4031")]
    [InlineData("photo_12345678")]          // an ID, not a date
    [InlineData("invoice 20261399")]        // month 13
    [InlineData("scan 202609291234567")]    // too many digits
    [InlineData("1985-01-01 party")]        // before 1990
    [InlineData("2026-09_29")]              // mixed separators
    public void ThingsThatOnlyLookLikeDatesAreNot(string name) =>
        Assert.Null(MediaFiles.FromFileName(name));

    // ---------------------------------------------------------- date taken

    [Fact]
    public void TheCamerasOwnDateWins()
    {
        var taken = new DateTakenReader().Read(Fixture("taken-20240317.jpg"));

        Assert.Equal(DateTakenSource.Metadata, taken.Source);
        Assert.Equal("20240317", taken.Stamp);
    }

    /// <summary>Phone videos store the time in UTC: the date is the one
    /// where it was shot, not in Greenwich.</summary>
    [Fact]
    public void AVideosCreationTimeIsReadInLocalTime()
    {
        var taken = new DateTakenReader().Read(Fixture("taken-20230704.mov"));

        var expected = new DateTime(2023, 7, 4, 3, 30, 0, DateTimeKind.Utc).ToLocalTime().Date;
        Assert.Equal(DateTakenSource.Metadata, taken.Source);
        Assert.Equal(expected, taken.Date);
    }

    [Fact]
    public void WithNoCameraDateTheFileNameIsUsed()
    {
        using var tmp = new TempDir();
        var photo = Path.Combine(tmp.Path, "IMG_20250102_101010.png");
        File.Copy(Fixture("no-date.png"), photo);

        var taken = new DateTakenReader().Read(photo);

        Assert.Equal(DateTakenSource.FileName, taken.Source);
        Assert.Equal("20250102", taken.Stamp);
    }

    [Fact]
    public void WithNothingElseTheModifiedDateIsUsed()
    {
        using var tmp = new TempDir();
        var gif = Path.Combine(tmp.Path, "dance.gif");
        File.Copy(Fixture("animated.gif"), gif);
        File.SetLastWriteTime(gif, new DateTime(2022, 5, 6, 12, 0, 0));

        var taken = new DateTakenReader().Read(gif);

        Assert.Equal(DateTakenSource.Modified, taken.Source);
        Assert.Equal("20220506", taken.Stamp);
    }

    [Fact]
    public void AFileThatIsNotReallyAPhotoFallsBackQuietly()
    {
        using var tmp = new TempDir();
        var fake = tmp.File("broken.jpg", "not a jpeg at all");
        File.SetLastWriteTime(fake, new DateTime(2021, 2, 3));

        var taken = new DateTakenReader().Read(fake);

        Assert.Equal(DateTakenSource.Modified, taken.Source);
        Assert.Equal("20210203", taken.Stamp);
    }

    [Fact]
    public void AMissingFileGetsTodayMarkedUnreadableRatherThanAnError()
    {
        var taken = new DateTakenReader(() => new DateTime(2026, 9, 29)).Read(@"C:\nowhere\gone.jpg");

        Assert.Equal("20260929", taken.Stamp);
        Assert.Equal(DateTakenSource.Unreadable, taken.Source);
    }

    /// <summary>IFD0's DateTime is when the file was last edited: a re-saved
    /// photo keeps the date in its name instead.</summary>
    [Fact]
    public void AnEditDateInThePhotoDoesNotBeatTheFileName()
    {
        using var tmp = new TempDir();
        var photo = Path.Combine(tmp.Path, "IMG_20190704_101010.jpg");
        File.Copy(Fixture("edited-only.jpg"), photo);

        var taken = new DateTakenReader().Read(photo);

        Assert.Equal(DateTakenSource.FileName, taken.Source);
        Assert.Equal("20190704", taken.Stamp);
    }

    // ---------------------------------------------------------- naming

    private static Naming.NameResult Name(string original, string typed, string stamp = "20260929",
        Func<string, bool>? exists = null, string suffix = "", bool appendSuffix = false) =>
        Naming.BuildTarget(original, typed, null, Naming.ModeInsert, suffix, appendSuffix,
            exists ?? (_ => false), stamp);

    [Fact]
    public void APhotoIsNamedByDateTakenAndTypedName()
    {
        var result = Name("IMG_4031.HEIC", "SMITH KITCHEN");

        Assert.Equal("20260929-SMITH KITCHEN.heic", result.Filename);
        Assert.Equal(Naming.ModeDated, result.ModeUsed);
    }

    [Fact]
    public void ABlankNameKeepsTheOriginalName() =>
        Assert.Equal("IMG_4031.jpg", Name("IMG_4031.JPG", "  ").Filename);

    [Fact]
    public void TypingTheExtensionDoesNotDoubleIt() =>
        Assert.Equal("20260929-KITCHEN.jpg", Name("IMG_1.jpg", "KITCHEN.jpg").Filename);

    [Theory]
    [InlineData("20250101 SMITH", "20250101-SMITH.jpg")]
    [InlineData("20250101-SMITH", "20250101-SMITH.jpg")]
    [InlineData("20250101", "20250101.jpg")]
    [InlineData("20251399 SMITH", "20260929-20251399 SMITH.jpg")]   // not a real date: part of the name
    public void ATypedDateReplacesTheDateTaken(string typed, string expected) =>
        Assert.Equal(expected, Name("IMG_1.jpg", typed).Filename);

    [Fact]
    public void AClashGetsACounterBeforeTheExtension()
    {
        var taken = new HashSet<string> { "20260929-SMITH.jpg" };

        Assert.Equal("20260929-SMITH (2).jpg", Name("IMG_1.jpg", "SMITH", exists: taken.Contains).Filename);
    }

    [Fact]
    public void TheRouteSuffixStillApplies() =>
        Assert.Equal("20260929-SMITH_rx.jpg", Name("IMG_1.jpg", "SMITH", suffix: "_rx", appendSuffix: true).Filename);

    [Fact]
    public void AnIllegalNameIsRefusedAsForPdfs() =>
        Assert.Throws<ArgumentException>(() => Name("IMG_1.jpg", "SMITH:JOHN"));

    /// <summary>Without a date the old rule stands, extension kept: a photo
    /// set aside is no longer renamed "IMG_1.jpg.pdf".</summary>
    [Fact]
    public void ANonPdfKeepsItsExtensionEvenWithoutADate() =>
        Assert.Equal("IMG_1.jpg",
            Naming.BuildTarget("IMG_1.JPG", "", null, Naming.ModeInsert, "", false, _ => false).Filename);

    [Fact]
    public void PdfsAreNamedExactlyAsBefore()
    {
        Assert.Equal("20240115-SMITH-12345.pdf",
            Naming.BuildTarget("20240115--12345.PDF", "SMITH", null, Naming.ModeInsert, "", false, _ => false).Filename);
        Assert.Equal("SMITH.pdf",
            Naming.BuildTarget("scan.pdf", "SMITH.pdf", null, Naming.ModeReplace, "", false, _ => false).Filename);
    }

    // ---------------------------------------------------------- filing

    [Fact]
    public void ATargetIsOnlyMadeForMedia()
    {
        var reads = 0;
        DateTaken Taken() { reads++; return new DateTaken(new DateTime(2026, 9, 29), DateTakenSource.Metadata); }

        Assert.Null(MediaFiles.TargetFor("scan.pdf", On(), Taken));
        Assert.Equal(0, reads);
        Assert.Equal(new MediaTarget("20260929", "Media"), MediaFiles.TargetFor("a.jpg", On(), Taken));
        Assert.Equal(new MediaTarget("20260929", ""),
            MediaFiles.TargetFor("a.jpg", new MediaSettings { Enabled = true, Folder = MediaSettings.FolderRoute }, Taken));
    }

    [Fact]
    public void APhotoIsFiledIntoTheRoutesMediaFolder()
    {
        using var tmp = new TempDir();
        var inbox = tmp.Dir("inbox");
        var route = new Route { Label = "Smith", Path = tmp.Dir("smith") };
        var photo = Path.Combine(inbox, "IMG_1.JPG");
        File.WriteAllText(photo, "x");

        var outcome = Commit.CommitFile(photo, "KITCHEN", route, Naming.ModeInsert,
            new MediaTarget("20260929", MediaSettings.MediaFolderName));

        Assert.Equal(Path.Combine(route.Path, "Media", "20260929-KITCHEN.jpg"), outcome.NewPath);
        Assert.True(File.Exists(outcome.NewPath));
        Assert.False(File.Exists(photo));
    }

    [Fact]
    public void AMissingRouteFolderIsNotMadeForTheMediaFolder()
    {
        using var tmp = new TempDir();
        var photo = tmp.File("inbox/IMG_1.jpg");
        var route = new Route { Label = "Gone", Path = Path.Combine(tmp.Path, "not-there") };

        var error = Assert.Throws<CommitError>(() => Commit.CommitFile(photo, "KITCHEN", route, Naming.ModeInsert,
            new MediaTarget("20260929", MediaSettings.MediaFolderName)));

        Assert.Contains("not available", error.Message);
        Assert.False(Directory.Exists(route.Path));
        Assert.True(File.Exists(photo));
    }

    /// <summary>An inbox set as a destination is refused before its Media
    /// folder could make it look like somewhere else (Q2-03).</summary>
    [Fact]
    public void TheInboxAsADestinationIsRefusedForPhotosToo()
    {
        using var tmp = new TempDir();
        var inbox = tmp.Dir("inbox");
        var photo = Path.Combine(inbox, "IMG_1.jpg");
        File.WriteAllText(photo, "x");
        var route = new Route { Label = "Oops", Path = inbox };

        Assert.Throws<CommitError>(() => Commit.CommitFile(photo, "SMITH", route, Naming.ModeInsert,
            new MediaTarget("20260929", MediaSettings.MediaFolderName)));
        Assert.False(Directory.Exists(Path.Combine(inbox, "Media")));
        Assert.True(File.Exists(photo));
    }

    [Fact]
    public void ARefusedNameLeavesNoEmptyMediaFolder()
    {
        using var tmp = new TempDir();
        var photo = tmp.File("inbox/IMG_1.jpg");
        var route = new Route { Label = "Smith", Path = tmp.Dir("smith") };

        Assert.Throws<CommitError>(() => Commit.CommitFile(photo, "SMITH:JOHN", route, Naming.ModeInsert,
            new MediaTarget("20260929", MediaSettings.MediaFolderName)));
        Assert.False(Directory.Exists(Path.Combine(route.Path, "Media")));
    }

    [Fact]
    public void TypingOnlyThePhotosExtensionIsABlankName()
    {
        Assert.True(Naming.IsBlankName(".jpg", "IMG_1.JPG"));
        Assert.False(Naming.IsBlankName(".jpg", "scan.pdf"));
        Assert.True(Naming.IsBlankName(".pdf", "scan.pdf"));
    }

    [Fact]
    public void ASetAsidePhotoKeepsItsExtension()
    {
        using var tmp = new TempDir();
        var photo = tmp.File("inbox/IMG_1.jpg");
        var deferred = tmp.Dir("later");

        var outcome = Commit.SkipFile(photo, deferred);

        Assert.Equal(Path.Combine(deferred, "IMG_1.jpg"), outcome.NewPath);
    }

    // ---------------------------------------------------------- settings

    [Fact]
    public void MediaIsOffUntilTurnedOn()
    {
        var cfg = new Config();
        Assert.False(cfg.Media.VideosEnabled);
        Assert.Contains(".mov", cfg.Media.VideoExtensions);
        Assert.False(cfg.Media.Enabled);
        Assert.Equal(MediaSettings.FolderMedia, cfg.Media.Folder);
        Assert.Contains(".heic", cfg.Media.Extensions);
    }

    [Fact]
    public void AnUnknownMediaFolderIsRefusedOnLoad()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json", """{ "media": { "enabled": true, "folder": "sideways" } }""");

        var error = Assert.Throws<ConfigException>(() => Config.Load(path));
        Assert.Contains("media.folder", error.Message);
    }

    /// <summary>A hand-edited list is cleaned on load: "pdf" there would make
    /// every PDF a photo.</summary>
    [Fact]
    public void AHandEditedTypeListIsCleanedOnLoad()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json", """{ "media": { "enabled": true, "extensions": ["PDF", "JPG", " png "] } }""");

        var cfg = Config.Load(path);

        Assert.Equal(new[] { ".jpg", ".png" }, cfg.Media.Extensions);
        Assert.False(MediaFiles.IsMedia("scan.pdf", cfg.Media));
    }

    [Fact]
    public void AMediaNullInTheFileMeansTheDefaults()
    {
        using var tmp = new TempDir();
        var path = tmp.File("config.json", """{ "media": null }""");

        var cfg = Config.Load(path);

        Assert.False(cfg.Media.Enabled);
        Assert.Equal(MediaSettings.FolderMedia, cfg.Media.Folder);
    }
}

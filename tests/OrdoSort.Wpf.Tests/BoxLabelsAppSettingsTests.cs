using BoxLabelsApp.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>BoxLabels.exe has no config.json. The one thing it remembers is
/// which shared box-labels.json to open, in a file beside the exe the way
/// OrdoSort keeps its config beside itself.
///
/// Getting this wrong is not cosmetic: pointed at the wrong file, the app
/// issues box numbers from a store nobody else is using, and two stations
/// eventually print the same number onto two physical boxes. These are pure
/// functions over a temp folder — no window, no Application.</summary>
public sealed class BoxLabelsAppSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "boxlabels_settings_" + Guid.NewGuid().ToString("N"))).FullName;

    private string SettingsPath => LabelsFileSettings.PathIn(_dir);

    private static readonly Func<string, bool> Reachable = _ => true;
    private static readonly Func<string, bool> Unreachable = _ => false;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ------------------------------------------------------- the settings file

    [Fact]
    public void ARememberedFileComesBackOnTheNextLaunch()
    {
        var share = @"\\server\records\box-labels.json";

        LabelsFileSettings.Write(SettingsPath, share);

        Assert.Equal(share, LabelsFileSettings.Read(SettingsPath));
    }

    /// <summary>Nothing remembered is first run: the caller has to ask. An
    /// empty string is that signal — not an exception, and not a guessed
    /// default path, which would quietly start a private store.</summary>
    [Fact]
    public void WithNoSettingsFileThereIsNothingToGoOn()
    {
        Assert.False(File.Exists(SettingsPath));

        Assert.Equal("", LabelsFileSettings.Read(SettingsPath));
    }

    /// <summary>A damaged one-key file is treated as first run rather than
    /// thrown. The user can re-pick in one click; refusing to start would be a
    /// dead end for someone with no way to repair JSON.</summary>
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("{ \"box_labels_file\": \"   \" }")]
    public void ADamagedOrEmptySettingsFileIsTreatedAsFirstRun(string contents)
    {
        File.WriteAllText(SettingsPath, contents);

        Assert.Equal("", LabelsFileSettings.Read(SettingsPath));
    }

    [Fact]
    public void TheSettingsFileSitsBesideTheProgram()
    {
        Assert.Equal(Path.Combine(_dir, "box-labels-app.json"), LabelsFileSettings.PathIn(_dir));
    }

    /// <summary>The README tells people to hand-write this file when
    /// pre-pointing a station, so a bare "box-labels.json" will genuinely be
    /// typed into it. Resolved against the WORKING DIRECTORY that would name a
    /// different store depending on how the app was launched — a shortcut with
    /// a different "Start in" would quietly open a private one.</summary>
    [Fact]
    public void ARelativePathResolvesAgainstTheSettingsFileNotTheWorkingDirectory()
    {
        File.WriteAllText(SettingsPath, "{ \"box_labels_file\": \"box-labels.json\" }");

        var resolved = LabelsFileSettings.Read(SettingsPath);

        Assert.Equal(Path.Combine(_dir, "box-labels.json"), resolved);
        Assert.NotEqual(Path.GetFullPath("box-labels.json"), resolved);
    }

    // (an absolute path surviving the round trip unchanged is already covered
    // by ARememberedFileComesBackOnTheNextLaunch, above)

    // ----------------------------------------------------------- the decision

    [Fact]
    public void ARememberedReachableFileIsOpenedWithoutAsking()
    {
        LabelsFileSettings.Write(SettingsPath, @"\\server\records\box-labels.json");

        var d = LabelsFileSettings.Decide(Array.Empty<string>(), SettingsPath, Reachable);

        Assert.Equal(LabelsFilePrompt.None, d.Prompt);
        Assert.Equal(@"\\server\records\box-labels.json", d.Path);
    }

    [Fact]
    public void WithNothingRememberedTheUserIsAskedAndTheChoiceIsKept()
    {
        var d = LabelsFileSettings.Decide(Array.Empty<string>(), SettingsPath, Reachable);

        Assert.Equal(LabelsFilePrompt.FirstRun, d.Prompt);
        Assert.True(d.PersistChoice);
    }

    /// <summary>An unreachable share is reported, and whatever the user picks
    /// instead becomes the new remembered file — they have told us the store
    /// moved.</summary>
    [Fact]
    public void AnUnreachableRememberedFileIsReportedAndTheReplacementIsKept()
    {
        LabelsFileSettings.Write(SettingsPath, @"\\dead\share\box-labels.json");

        var d = LabelsFileSettings.Decide(Array.Empty<string>(), SettingsPath, Unreachable);

        Assert.Equal(LabelsFilePrompt.Unreachable, d.Prompt);
        Assert.Equal(@"\\dead\share\box-labels.json", d.Path);   // named in the message
        Assert.True(d.PersistChoice);
    }

    [Fact]
    public void TheCommandLineWinsOverWhatWasRemembered()
    {
        LabelsFileSettings.Write(SettingsPath, @"C:\remembered\box-labels.json");

        var d = LabelsFileSettings.Decide(
            new[] { "--file", @"\\other\share\box-labels.json" }, SettingsPath, Reachable);

        Assert.Equal(LabelsFilePrompt.None, d.Prompt);
        Assert.Equal(@"\\other\share\box-labels.json", d.Path);
    }

    /// <summary>The defect this whole function was extracted to make testable.
    ///
    /// --file is for running against a different store ONCE. When that path is
    /// unreachable the user is asked to pick another — and the old code then
    /// SAVED what they picked, silently repointing the app for every later
    /// double-click, which is the exact opposite of what --file promises.</summary>
    [Fact]
    public void AnUnreachableCommandLinePathNeverRepointsTheRememberedFile()
    {
        LabelsFileSettings.Write(SettingsPath, @"C:\remembered\box-labels.json");

        var d = LabelsFileSettings.Decide(
            new[] { "--file", @"\\dead\share\box-labels.json" }, SettingsPath, Unreachable);

        Assert.Equal(LabelsFilePrompt.Unreachable, d.Prompt);
        Assert.False(d.PersistChoice);
    }

    // -------------------------------------------------------- the command line

    public static IEnumerable<object[]> UnusableCommandLines() => new[]
    {
        new object[] { Array.Empty<string>() },
        new object[] { new[] { "--file" } },              // nothing after it
        new object[] { new[] { "--file", "   " } },       // blank value
        new object[] { new[] { "--other", "x" } },
    };

    [Theory]
    [MemberData(nameof(UnusableCommandLines))]
    public void AnUnusableCommandLineIsIgnored(string[] args)
    {
        Assert.Null(LabelsFileSettings.FromArgs(args));
    }

    // ------------------------------------------------------------ reachability

    [Fact]
    public void AFolderThatExistsIsReachable()
    {
        Assert.True(LabelsFileSettings.FolderReachable(Path.Combine(_dir, "box-labels.json")));
    }

    /// <summary>A missing FILE in a folder that exists is ordinary — the store
    /// creates it. Only a missing FOLDER means the share is gone.</summary>
    [Fact]
    public void AMissingFileInAnExistingFolderIsStillReachable()
    {
        var missing = Path.Combine(_dir, "never-written.json");
        Assert.False(File.Exists(missing));

        Assert.True(LabelsFileSettings.FolderReachable(missing));
    }

    [Fact]
    public void AFolderThatDoesNotExistIsNotReachable()
    {
        Assert.False(LabelsFileSettings.FolderReachable(
            Path.Combine(_dir, "no-such-folder", "box-labels.json")));
    }
}

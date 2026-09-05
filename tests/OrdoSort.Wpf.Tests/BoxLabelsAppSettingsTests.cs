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

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void ARememberedFileComesBackOnTheNextLaunch()
    {
        var share = @"\\server\records\box-labels.json";

        LabelsFileSettings.Write(SettingsPath, share);

        Assert.Equal(share, LabelsFileSettings.Read(SettingsPath));
        Assert.Equal(share, LabelsFileSettings.Resolve(Array.Empty<string>(), SettingsPath));
    }

    /// <summary>Nothing remembered is first run: the caller has to ask. An
    /// empty string is that signal — not an exception, and not a guessed
    /// default path, which would quietly start a private store.</summary>
    [Fact]
    public void WithNoSettingsFileThereIsNothingToGoOn()
    {
        Assert.False(File.Exists(SettingsPath));

        Assert.Equal("", LabelsFileSettings.Read(SettingsPath));
        Assert.Equal("", LabelsFileSettings.Resolve(Array.Empty<string>(), SettingsPath));
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
    public void TheCommandLineWinsOverWhatWasRemembered()
    {
        LabelsFileSettings.Write(SettingsPath, @"C:\remembered\box-labels.json");
        var args = new[] { "--file", @"\\other\share\box-labels.json" };

        Assert.Equal(@"\\other\share\box-labels.json",
            LabelsFileSettings.Resolve(args, SettingsPath));
    }

    /// <summary>--file is for running against a different store ONCE — a test
    /// copy, a second client's share. Persisting it would leave the app
    /// pointed somewhere the user never chose the next time they simply
    /// double-clicked it.</summary>
    [Fact]
    public void TheCommandLineDoesNotOverwriteWhatWasRemembered()
    {
        LabelsFileSettings.Write(SettingsPath, @"C:\remembered\box-labels.json");

        LabelsFileSettings.Resolve(new[] { "--file", @"\\other\share\box-labels.json" }, SettingsPath);

        Assert.Equal(@"C:\remembered\box-labels.json", LabelsFileSettings.Read(SettingsPath));
    }

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

    [Fact]
    public void TheSettingsFileSitsBesideTheProgram()
    {
        Assert.Equal(Path.Combine(_dir, "box-labels-app.json"), LabelsFileSettings.PathIn(_dir));
    }
}

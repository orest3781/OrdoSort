using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>WebView2 puts its browser profile beside the executable unless it
/// is told otherwise. A copy of the app on a network share then tries to
/// create that profile on the share, which fails — so a colleague launching it
/// from \\server\share got an error while the same build ran fine locally.
/// The profile has to live on the machine doing the running.</summary>
public class ViewerEnvironmentTests
{
    [Fact]
    public void TheBrowserProfileLivesOnTheLocalMachine()
    {
        var folder = WebViewPdfViewer.UserDataFolder;
        var localAppData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(localAppData, folder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheBrowserProfileIsNotBesideTheExecutable()
    {
        // the default, and the whole bug: an app folder that is read-only or on
        // a share cannot host a browser profile
        var folder = Path.GetFullPath(WebViewPdfViewer.UserDataFolder);
        var beside = Path.GetDirectoryName(
            Path.GetFullPath(typeof(WebViewPdfViewer).Assembly.Location))!;

        Assert.False(folder.StartsWith(beside, StringComparison.OrdinalIgnoreCase),
            $"the profile would sit beside the app at {folder}");
    }

    [Fact]
    public void TheBrowserProfileIsNeverAUncPath()
    {
        // even a writable share does not work: WebView2's profile has to be on
        // local storage, so a UNC path here would fail in a different way
        Assert.False(WebViewPdfViewer.UserDataFolder.StartsWith(@"\\"),
            "a UNC profile path fails even when the share is writable");
    }
}

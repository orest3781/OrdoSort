using System.Windows;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Nothing is asked of these dialogs — the window is only built, not
/// driven.</summary>
file sealed class SilentDialogs : ILabelDialogs
{
    public void Warn(string message, string title) { }
    public void Info(string message, string title) { }
    public bool Confirm(string message, string title) => false;
    public string? AskSaveFile(string filter, string suggestedName) => null;
}

/// <summary>The store bar names which box-labels.json is open and offers the
/// Change file… button. It exists because this app's whole risk is printing
/// from the wrong file, and that mistake cannot be spotted once numbers are on
/// physical boxes.
///
/// It must appear ONLY in the standalone. OrdoSort reaches the same window
/// from Tools, where the path is a config.json key edited on the Settings
/// page — a second way to change one setting is the thing being avoided, and
/// "OrdoSort is unchanged" is a promise this test keeps.</summary>
[Collection(HighlightContrastTests.Name)]
public class LabelStoreBarTests : IDisposable
{
    private readonly HighlightContrastFixture _fx;
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "boxlabels_bar_" + Guid.NewGuid().ToString("N"))).FullName;

    public LabelStoreBarTests(HighlightContrastFixture fx) => _fx = fx;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private LabelMakerViewModel Vm() =>
        new(null, Path.Combine(_dir, "box-labels.json"), new SilentDialogs(), "Box labels");

    [Fact]
    public void OrdoSortsWindowHasNoStoreBar() => _fx.Invoke(() =>
    {
        var window = new LabelMakerWindow(Vm(), "OrdoSort — Box labels", "OrdoSort — Print preview");
        try
        {
            Assert.Equal(Visibility.Collapsed, window.StoreBar.Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheStandaloneShowsTheStoreItIsPrintingFrom() => _fx.Invoke(() =>
    {
        var store = @"\\server\records\box-labels.json";
        var window = new LabelMakerWindow(Vm(), "Box Labels", "Box Labels — Print preview",
            standalone: true, storeBar: new LabelStoreBar(store, () => { }));
        try
        {
            Assert.Equal(Visibility.Visible, window.StoreBar.Visibility);
            Assert.Equal(store, window.StorePathText.Text);
            // the bar trims a long share path, so the whole thing has to be
            // recoverable on hover or it is not really shown at all
            Assert.Equal(store, window.StorePathText.ToolTip);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheChangeFileButtonRunsTheHostsAction() => _fx.Invoke(() =>
    {
        var clicked = 0;
        var window = new LabelMakerWindow(Vm(), "Box Labels", "Box Labels — Print preview",
            standalone: true, storeBar: new LabelStoreBar("x.json", () => clicked++));
        try
        {
            window.ChangeStoreButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            Assert.Equal(1, clicked);
        }
        finally { window.Close(); }
    });
}

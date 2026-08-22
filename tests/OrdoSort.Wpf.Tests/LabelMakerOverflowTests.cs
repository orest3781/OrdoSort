using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>A no-op IDialogService — same file-scoped duplicate as
/// FocusRingCoverageTests carries, for the same reason (the fixture's own
/// copy is `file`-scoped and invisible from here).</summary>
file sealed class NoDialogs : IDialogService
{
    public void Warn(string message, string title) { }
    public void Info(string message, string title) { }
    public bool Confirm(string message, string title) => true;
    public string? AskSaveFile(string filter, string suggestedName) => null;
    public string? AskOpenFile(string filter) => null;
    public string? AskFilePath(string filter, string suggestedName) => null;
    public string? BrowseFolder(string? startAt) => null;
}

/// <summary>2026-08-16 wrap audit, user report: text in the Box labels window
/// (LabelMakerWindow) ran off screen. The window mixes fixed-width columns
/// (170px client list, 140/60px field cells, Width=680/MinWidth=600) with a
/// user-configurable app font (Config.UiFontSize, 6–72, default 14), so
/// content that fits at 14px marches past the window edge as the font grows —
/// and a WPF Grid does not clip, so there is no visual hint beyond the text
/// vanishing off the right edge.
///
/// This suite renders the REAL window off-screen (per FocusRingCoverageTests'
/// philosophy: prove pixels/geometry, not properties) and asserts every
/// visible TextBlock, RadioButton and Button lands inside the window's
/// content bounds. Guaranteed range, deliberately bounded: the default font
/// at both the minimum (600) and default (680) widths, and 18px at the
/// default width — the sizes the Settings Text tab's own preset buttons
/// offer. 72px cannot be honoured by ANY fixed-width dialog and is out of
/// scope; past 18 the prose elements degrade by trimming/wrapping instead of
/// overflowing, which is what the fixes this suite pins actually changed.</summary>
[Collection(HighlightContrastTests.Name)]
public class LabelMakerOverflowTests
{
    private readonly HighlightContrastFixture _fx;
    public LabelMakerOverflowTests(HighlightContrastFixture fx) => _fx = fx;

    [Theory]
    [InlineData(14.0, 600.0)]
    [InlineData(14.0, 680.0)]
    [InlineData(18.0, 680.0)]
    public void EveryTextElementStaysInsideTheWindow(double fontSize, double width) => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var defaultFont = _fx.App.Resources["AppFontSize"];
        _fx.App.Resources["AppFontSize"] = fontSize;

        var boxLabelsPath = Path.Combine(Path.GetTempPath(), "ordo_test_boxlabels_" + Guid.NewGuid() + ".json");
        var vm = new LabelMakerViewModel(new Config(), boxLabelsPath, new NoDialogs());
        vm.Clients.Add(new LabelClientVm { Id = "TESTCLNT", DestroyDaysText = "45", NextNumberText = "00000001" });
        vm.Selected = vm.Clients[0];
        var window = new LabelMakerWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = width,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            OverflowProbe.PumpRender();

            var offenders = OverflowProbe.HorizontalEscapees((FrameworkElement)window.Content, out var examined);
            // 36 elements are judged here, so 10 is a floor with room to
            // spare — it is here to catch the probe going blind (QC-09), not
            // to pin the element count
            Assert.True(examined >= 10,
                $"the probe examined only {examined} elements — it is not measuring anything");
            Assert.True(offenders.Count == 0,
                $"font {fontSize}, width {width}: elements escape the window:\n  " +
                string.Join("\n  ", offenders));
        }
        finally
        {
            window.Close();
            _fx.App.Resources["AppFontSize"] = defaultFont;
            try { File.Delete(boxLabelsPath); } catch { /* best effort */ }
        }
    });

}

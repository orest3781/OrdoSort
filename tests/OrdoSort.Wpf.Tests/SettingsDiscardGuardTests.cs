using System.Windows;
using OrdoSort.Core;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>Settings holds seven tabs of editing — destination paths, hotkeys,
/// suffixes and colours; monitored folders with sections and filetypes; alert
/// terms; fonts and schemes — and Esc, Cancel, or the window's X threw all of
/// it away silently. Esc in particular is muscle memory here, because it is
/// safe in every other window in this app (2026-08-22 UI audit, UI-06).
///
/// <para><b>Why the dirty check compares content instead of listening for
/// changes.</b> The obvious implementation — subscribe to PropertyChanged on
/// the view model and its nested row view models, plus CollectionChanged on the
/// five collections — is wrong here for a specific reason:
/// <c>SelectedRoute</c> and <c>SelectedWatch</c> raise a burst of
/// PropertyChanged on every selection, and clicking through the destination
/// list to LOOK at each route is not an edit. An event-based flag calls that
/// dirty and prompts on the way out of a window where nothing was typed, which
/// trains people to hit "discard" reflexively — the exact habit this prompt
/// exists to interrupt.</para>
///
/// So dirtiness is <c>Serialize(BuildEditedConfig()) != snapshot-at-open</c>.
/// That is exact, immune to selection noise, and self-maintaining: it reuses
/// the same field mapping OK already uses, so a field added to the build is
/// compared automatically instead of needing a second hand-kept list — the same
/// reasoning TryBuildResult's own JSON-clone comment gives for not maintaining
/// a carry-through list.</summary>
public class SettingsDiscardGuardTests
{
    private static SettingsViewModel Vm(FakeDialogs? dialogs = null)
    {
        var cfg = new Config
        {
            Inbox = @"C:\inbox",
            Deferred = @"C:\deferred",
            MonitorTitle = "Needs attention",
            Routes = { new Route { Label = "Invoices", Path = @"C:\out", Color = "#2e7d32" },
                       new Route { Label = "Statements", Path = @"C:\out2", Color = "#1565c0" } },
        };
        return new SettingsViewModel(cfg, dialogs ?? new FakeDialogs(),
            directoryExists: _ => true, fileExists: _ => true,
            scheduler: new InlineWorkScheduler());
    }

    [Fact]
    public void AFreshlyOpenedEditorIsNotDirty() =>
        Assert.False(Vm().IsDirty, "Settings reported unsaved changes before anything was touched");

    [Fact]
    public void TypingInAScalarFieldMakesItDirty()
    {
        var vm = Vm();
        vm.MonitorTitle = "Something else";
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void EditingARouteRowMakesItDirty()
    {
        var vm = Vm();
        vm.Routes[0].Label = "Renamed";
        Assert.True(vm.IsDirty, "an edit inside a destination row was not noticed");
    }

    [Fact]
    public void AddingARouteMakesItDirty()
    {
        var vm = Vm();
        vm.AddRouteCommand.Execute(null);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void RemovingARouteMakesItDirty()
    {
        var vm = Vm();
        vm.SelectedRoute = vm.Routes[0];
        vm.RemoveRouteCommand.Execute(null);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void AddingAnAlertTermMakesItDirty()
    {
        var vm = Vm();
        vm.NewAlertText = "urgent";
        vm.AddAlertCommand.Execute(null);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void PuttingAValueBackTheWayItWasClearsDirty()
    {
        var vm = Vm();
        var was = vm.MonitorTitle;
        vm.MonitorTitle = "Something else";
        Assert.True(vm.IsDirty);
        vm.MonitorTitle = was;
        Assert.False(vm.IsDirty,
            "undoing the edit by hand still reported unsaved changes — the check is " +
            "tracking events rather than comparing content");
    }

    /// <summary>The false-positive guard, and the reason this is a content
    /// comparison. Clicking through the destination list to read each route
    /// raises PropertyChanged all over the view model and edits nothing.</summary>
    [Fact]
    public void MerelySelectingDifferentRowsIsNotAnEdit()
    {
        var vm = Vm();
        vm.SelectedRoute = vm.Routes[1];
        vm.SelectedRoute = vm.Routes[0];
        vm.SelectedRoute = vm.Routes[1];
        Assert.False(vm.IsDirty,
            "browsing the destination list reported unsaved changes — an event-based " +
            "dirty flag would do exactly this, and it trains people to dismiss the prompt");
    }

    /// <summary>Half-typed numbers are the state people are most likely to Esc
    /// out of, and the build throws on them (UiFontSizeText is int.Parse'd
    /// behind a validation gate OK runs first and this check does not). It must
    /// read as dirty rather than escaping as an exception.</summary>
    [Fact]
    public void AnInvalidHalfTypedValueCountsAsDirtyRatherThanThrowing()
    {
        var vm = Vm();
        vm.UiFontSizeText = "not a number";
        var dirty = Record.Exception(() => Assert.True(vm.IsDirty));
        Assert.Null(dirty);
    }
}

/// <summary>The window half of UI-06: the dirty check above only matters if
/// closing actually consults it. Covers all three ways out (Cancel/Esc both
/// route through IsCancel, and the X routes through the same Closing event),
/// and the two ways OK must NOT prompt.</summary>
[Collection(HighlightContrastTests.Name)]
public class SettingsDiscardWindowTests
{
    private readonly HighlightContrastFixture _fx;
    public SettingsDiscardWindowTests(HighlightContrastFixture fx) => _fx = fx;

    private static (SettingsWindow Window, SettingsViewModel Vm, FakeDialogs Dialogs) Open()
    {
        var dialogs = new FakeDialogs();
        var cfg = new Config
        {
            Inbox = @"C:\inbox", Deferred = @"C:\deferred", MonitorTitle = "Needs attention",
            Routes = { new Route { Label = "Invoices", Path = @"C:\out", Color = "#2e7d32" } },
        };
        var vm = new SettingsViewModel(cfg, dialogs,
            directoryExists: _ => true, fileExists: _ => true,
            scheduler: new InlineWorkScheduler());
        var window = new SettingsWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        window.UpdateLayout();
        return (window, vm, dialogs);
    }

    [Fact]
    public void ClosingWithUnsavedEditsAsksFirstAndStaysOpenOnNo() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (window, vm, dialogs) = Open();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            vm.MonitorTitle = "edited";
            dialogs.ConfirmAnswer = false;      // "no, take me back"

            window.Close();

            Assert.Single(dialogs.Confirms);
            Assert.False(closed,
                "Settings closed and discarded the edits even though the prompt was answered No");
        }
        finally { if (!closed) { dialogs.ConfirmAnswer = true; window.Close(); } }
    });

    [Fact]
    public void ClosingWithUnsavedEditsClosesOnYes() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (window, vm, dialogs) = Open();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            vm.MonitorTitle = "edited";
            dialogs.ConfirmAnswer = true;       // "yes, discard"

            window.Close();

            Assert.Single(dialogs.Confirms);
            Assert.True(closed, "answering Yes to the discard prompt did not close the window");
        }
        finally { if (!closed) window.Close(); }
    });

    [Fact]
    public void ClosingAnUntouchedEditorDoesNotAsk() => _fx.Invoke(() =>
    {
        ThemeManager.Apply(_fx.App, dark: false);
        var (window, _, dialogs) = Open();
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Close();
            Assert.Empty(dialogs.Confirms);
            Assert.True(closed);
        }
        finally { if (!closed) window.Close(); }
    });
}

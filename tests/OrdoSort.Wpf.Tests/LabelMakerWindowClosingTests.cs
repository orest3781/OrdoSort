using System.Windows;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>QC-07 fix-round-1 follow-up (code review, 2026-08-22): the
/// VM-level TryPersist tests in LabelMakerViewModelTests prove the RETURN
/// VALUE is correct, but QC-07's actual promise — the window stays open
/// instead of closing over a refused write — lives entirely in the two-line
/// Closing lambda in LabelMakerWindow.xaml.cs, and nothing drove that
/// directly. Per this repo's PROOF STANDARD for exactly this bug class
/// (ShutdownDuringCommitTests.cs:58-61, citing TriageWindowDisposalTests as
/// prior art): drive the REAL Closing/Closed events on a real window, not a
/// re-creation of their logic. TriageWindowDisposalTests already confirmed,
/// empirically against the installed WPF runtime, that Closed fires from a
/// plain Close() call even on a window that was never Show()n — so this
/// stays hermetic, no pumping or visible window required.</summary>
[Collection(HighlightContrastTests.Name)]
public class LabelMakerWindowClosingTests
{
    private readonly HighlightContrastFixture _fx;
    public LabelMakerWindowClosingTests(HighlightContrastFixture fx) => _fx = fx;

    [Fact]
    public void ClosingOverAnUnresolvedDuplicateIdKeepsTheWindowOpenWithTheEditIntact() => _fx.Invoke(() =>
    {
        var boxLabelsPath = Path.Combine(Path.GetTempPath(), "ordo_test_boxlabels_" + Guid.NewGuid() + ".json");
        BoxLabelStore.Mutate(boxLabelsPath, d =>
        {
            d.LabelClients.Add(new LabelClient { Id = "AAAA", DestroyDays = 30, NextNumber = 7 });
            d.LabelClients.Add(new LabelClient { Id = "BBBB", DestroyDays = 30, NextNumber = 10 });
            return 0;
        });
        var dialogs = new FakeDialogs();
        var vm = new LabelMakerViewModel(new Config(), boxLabelsPath, dialogs);
        var window = new LabelMakerWindow(vm)
        {
            Left = -20000, Top = 0, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var closed = false;
        window.Closed += (_, _) => closed = true;

        var collider = vm.Clients.Single(c => c.Id == "AAAA");
        collider.Id = "BBBB";   // collision: two rows on screen now say "BBBB"

        window.Close();   // the REAL Closing event, not a re-creation of its logic

        Assert.False(closed, "the window closed over an unresolved duplicate id — the " +
            "refusal's own warning promises \"nothing was saved,\" which is a lie if the " +
            "window (and every edit with it) is gone anyway");
        Assert.Equal(2, BoxLabelStore.Read(boxLabelsPath).LabelClients.Count);   // nothing landed
        Assert.Contains("share the id", Assert.Single(dialogs.Warnings).Message);

        collider.Id = "AAAB";   // fix the duplicate — the edit that had to survive to reach here
        window.Close();

        Assert.True(closed, "once the duplicate is fixed, the window must close normally");
        var stored = BoxLabelStore.Read(boxLabelsPath).LabelClients;
        Assert.Contains(stored, c => c.Id == "AAAB");
        Assert.Contains(stored, c => c.Id == "BBBB");
    });
}

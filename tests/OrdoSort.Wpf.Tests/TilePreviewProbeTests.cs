using System.Diagnostics;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 1 (2026-08-05 debounce pair, audit finding 5.4[A]):
/// RecomputeTilePreview used to call FolderMonitor.Status — Directory.Exists
/// plus a RECURSIVE Directory.EnumerateFiles when the watched folder is
/// marked Recursive — synchronously on the UI thread, for every keystroke in
/// EVERY WatchEditVm row, even ones that aren't SelectedWatch (whose result
/// is then simply thrown away, since RecomputeTilePreview only ever renders
/// SelectedWatch). This file pins both halves of the fix: the probe now runs
/// off the UI thread through DebouncedProbe (mirroring the seven existing
/// probes already in SettingsViewModel), and HookWatch returns early for a
/// row that isn't selected instead of probing and discarding the result.</summary>
public class TilePreviewProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordoset_tilepreview_" + Guid.NewGuid());
    private readonly FakeDialogs _dialogs = new();

    public TilePreviewProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Same shape as SettingsViewModelTests.WaitFor: the probe is
    /// debounced and off the UI thread, so "eventually correct" has to be
    /// polled for rather than asserted the instant a call returns.</summary>
    private static void WaitFor(Func<bool> condition, string because, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            bool result;
            try
            {
                result = condition();
            }
            // Fix round 2, item 2(b) — same fix as SettingsViewModelTests.WaitFor/
            // ToolViewModelTests.WaitFor's own copy: a predicate reading a
            // collection that a background thread is mid-mutating can throw
            // INSIDE the read rather than just observe a stale-but-valid
            // value. Both exceptions below are the SAME "not true yet"
            // outcome a plain false would be, so they are retried, not
            // surfaced. Nothing else is caught: a predicate that throws for
            // a REAL reason must still fail the test immediately.
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                result = false;
            }
            if (result) return;
            if (sw.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"condition never became true within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
        }
    }

    /// <summary>The mirror image of WaitFor: that one polls for a condition
    /// to become true; this one polls for a VALUE to settle down (stop
    /// changing) before it is safe to read as a baseline. Fix round 2, item
    /// 2(a): EditingANonSelectedWatchFolderNeverProbesAtAll used to bracket
    /// its measurement with two fixed Thread.Sleep(700) calls — under load,
    /// 700ms was not always enough for every setup probe to land, so a
    /// still-pending one could fire AFTER the fixed window ended and get
    /// miscounted as caused by whatever the test did next. Resetting the
    /// stability clock every time the value changes means this naturally
    /// adapts to load instead of racing a fixed guess against it.</summary>
    private static int WaitForStable(Func<int> value, string because, int stableForMs = 300, int timeoutMs = 5000)
    {
        var overall = Stopwatch.StartNew();
        var last = value();
        var sinceChange = Stopwatch.StartNew();
        while (sinceChange.ElapsedMilliseconds < stableForMs)
        {
            if (overall.ElapsedMilliseconds > timeoutMs)
                Assert.Fail($"value never stabilised within {timeoutMs}ms: {because}");
            Thread.Sleep(5);
            var current = value();
            if (current != last)
            {
                last = current;
                sinceChange.Restart();
            }
        }
        return last;
    }

    /// <summary>The other half of the mirror: a NEGATIVE assertion ("this
    /// never happens") bounded by a generous polling window instead of one
    /// blind sleep-then-check — every poll is itself an assertion, so a
    /// probe that fires at any point during the window is caught the
    /// instant it happens, with the real before/after counts in the
    /// failure message, rather than only at one fixed checkpoint.</summary>
    private static void AssertStaysAt(Func<int> value, int expected, string because, int timeoutMs = 1500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Assert.True(value() == expected, $"{because} (expected {expected}, now {value()})");
            Thread.Sleep(5);
        }
    }

    /// <summary>Fix round 2, item 2(a)'s own guarding facts for the two new
    /// helpers that replaced the fixed sleeps — these are test
    /// infrastructure, not production code, but a helper that always passes
    /// (or always waits out its full timeout regardless of the real value)
    /// would make EditingANonSelectedWatchFolderNeverProbesAtAll pass for
    /// the wrong reason, exactly the trap this whole exercise exists to
    /// close.</summary>
    [Fact]
    public void AssertStaysAtFailsWhenTheValueActuallyChanges()
    {
        var calls = 0;
        int Flaky() { calls++; return calls < 3 ? 0 : 5; }   // changes on the third read
        var ex = Record.Exception(() => AssertStaysAt(Flaky, 0, "should catch a real change", timeoutMs: 200));
        Assert.NotNull(ex);
    }

    [Fact]
    public void WaitForStableSettlesOnTheFinalValueOnceChangesStop()
    {
        var calls = 0;
        int Settling() { calls++; return calls < 5 ? calls : 7; }   // changes for the first 4 reads, then holds at 7
        var result = WaitForStable(Settling, "should settle once changes stop", stableForMs: 30);
        Assert.Equal(7, result);
    }

    // ---- 1. the UI thread does not do the enumeration ---------------------

    [Fact]
    public void EditingTheSelectedWatchFolderDoesNotBlockOnTheStatusProbe()
    {
        // A deliberately slow stand-in for FolderMonitor.Status — exactly
        // the shape of a large/stalled (possibly recursive) enumeration.
        // Wired straight into the seam (SettingsViewModel's folderStatus
        // ctor param) so the check below hits it on every keystroke, same
        // as the real UI does.
        var vm = new SettingsViewModel(new Config(), _dialogs,
            folderStatus: (wf, terms) => { Thread.Sleep(300); return FolderMonitor.Status(wf, terms); });
        vm.AddWatchCommand.Execute(null);
        var w = vm.WatchFolders[0];   // AddWatchCommand selects the row it creates

        // A bound control reacts to TilePreviewCount's PropertyChanged
        // SYNCHRONOUSLY, on whatever thread raised it — WPF bindings don't
        // defer unless IsAsync is set. Subscribing and re-reading here
        // reproduces exactly what the real window's binding does, without
        // needing a live window (same technique as
        // SettingsViewModelTests.TypingAPathDoesNotBlockOnTheProbe).
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.TilePreviewCount)) _ = vm.TilePreviewCount; };

        var sw = Stopwatch.StartNew();
        w.Path = _dir;   // a keystroke-shaped edit on the SELECTED row
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50,
            $"editing the selected watch folder's Path blocked for {sw.ElapsedMilliseconds}ms on the UI thread");
    }

    // ---- 2. a keystroke on a non-selected row never probes at all ---------

    [Fact]
    public void EditingANonSelectedWatchFolderNeverProbesAtAll()
    {
        var calls = 0;
        var vm = new SettingsViewModel(new Config(), _dialogs,
            folderStatus: (wf, terms) => { Interlocked.Increment(ref calls); return FolderMonitor.Status(wf, terms); });

        vm.AddWatchCommand.Execute(null);
        var selected = vm.WatchFolders[0];
        selected.Path = _dir;   // real, existing folder — so ITS OWN probe genuinely runs

        vm.AddWatchCommand.Execute(null);
        var other = vm.WatchFolders[1];   // freshly added; briefly selected by AddWatchCommand itself
        vm.SelectedWatch = selected;      // re-select the first row — `other` is now definitely NOT selected

        // Wait for every probe triggered by the setup above (selected.Path,
        // the two AddWatchCommand calls, the re-selection) to genuinely
        // settle before taking the baseline — a fixed sleep here raced
        // system load: a still-pending setup probe landing just after a
        // fixed window ended got miscounted as caused by editing `other`
        // below (fix round 2, item 2(a)).
        var callsBeforeEditingOther = WaitForStable(() => calls,
            "the setup probes (two adds, the path edit, the reselect) should settle before the baseline is taken");

        // exactly the fields the audit named: label, path, colour, filetype
        other.Path = _dir;
        other.Label = "renamed";
        other.Color = "#c0392b";
        other.Recursive = true;
        other.Filetypes = "pdf";

        // generous, bounded window: long past the debounce window and any
        // probe duration, but a poll rather than one blind sleep-then-check.
        AssertStaysAt(() => calls, callsBeforeEditingOther,
            "editing the unselected row must never trigger a new probe");
    }

    // ---- 3. the preview still becomes correct (this is not "never probe") -

    [Fact]
    public void TheTilePreviewEventuallyReflectsTheRealFolderContents()
    {
        var watched = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(watched);
        File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");
        File.WriteAllText(Path.Combine(watched, "b.pdf"), "x");

        var vm = new SettingsViewModel(new Config
        {
            WatchFolders = { new WatchFolder { Label = "Failed", Path = watched } },
        }, _dialogs);
        vm.SelectedWatch = vm.WatchFolders[0];

        WaitFor(() => vm.TilePreviewCount == "2",
            "the tile preview should eventually count the real folder's matching files");
        Assert.Equal("Failed", vm.TilePreviewLabel);
        Assert.Equal("", vm.TilePreviewHint);
    }

    // ---- 4. a discrete change (immediate: true) resolves without waiting
    // out the full debounce window ------------------------------------------

    [Fact]
    public void SelectingADifferentWatchResolvesWithoutWaitingTheFullDebounceWindow()
    {
        var watched = Path.Combine(_dir, "watched");
        Directory.CreateDirectory(watched);
        File.WriteAllText(Path.Combine(watched, "a.pdf"), "x");

        // an artificially huge debounce window — if selecting a row waited
        // it out, the WaitFor below (timeout well under this) would fail
        var vm = new SettingsViewModel(new Config
        {
            WatchFolders =
            {
                new WatchFolder { Label = "Empty" },
                new WatchFolder { Label = "Real", Path = watched },
            },
        }, _dialogs, probeDelayMs: 5000);

        vm.SelectedWatch = vm.WatchFolders[1];   // SettingsViewModel routes this through Resolve(..., immediate: true)

        WaitFor(() => vm.TilePreviewCount == "1",
            "selecting a different watch folder should resolve promptly, not after the full debounce window",
            timeoutMs: 1000);
    }
}

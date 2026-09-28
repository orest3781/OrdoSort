using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Runs background work inline, except while <see cref="Holding"/>
/// is set: then each item is queued, so a test can act while an operation
/// is still in flight and then choose which parked item finishes first.</summary>
file sealed class HoldableWorkScheduler : IWorkScheduler
{
    private readonly ControlledWorkScheduler _held = new();
    public bool Holding { get; set; }

    public Task<T> Run<T>(Func<T> work) =>
        Holding ? _held.Run(work) : Task.FromResult(work());

    public Task Run(Action work)
    {
        if (Holding) return _held.Run(work);
        work();
        return Task.CompletedTask;
    }

    /// <summary>Stop holding, then finish parked work newest first — the
    /// order that lets a later operation overtake an earlier one.</summary>
    public void ReleaseNewestFirst()
    {
        Holding = false;
        while (_held.Queued > 0) _held.ReleaseNewest();
    }
}

/// <summary>Settings OK replaces the session and History and rescans, across
/// awaits that leave the Ready screen live. Start pressed in that window
/// must be refused, not begin a session the apply then tears down.</summary>
public class ApplySettingsBusyTests
{
    [Fact]
    public async Task StartIsRefusedWhileSettingsAreBeingApplied()
    {
        var scheduler = new HoldableWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        Assert.True(fx.Shell.StartEnabled);
        var edited = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fx.Cfg))!;

        scheduler.Holding = true;
        var apply = fx.Shell.ApplySettingsAsync(edited);   // parked on the watcher re-registration
        Assert.False(apply.IsCompleted);
        Assert.False(fx.Shell.StartCommand.CanExecute(null));

        var start = fx.Shell.StartProcessingAsync();       // Start pressed anyway (hotkey)
        scheduler.ReleaseNewestFirst();
        await start;
        await apply;

        // No session was begun underneath the apply — nothing was shown and
        // the screen is the freshly rescanned dashboard, ready to start.
        Assert.Empty(fx.Viewer.Shown);
        Assert.Equal(Screen.Ready, fx.Shell.Screen);
        Assert.False(fx.Shell.IsBusy);
        Assert.True(fx.Shell.StartEnabled);
    }

    /// <summary>Q2-08, the other order: Settings OK while Start was still
    /// scanning switched the config under the scan, and the session opened
    /// on the old inbox's files with the old destinations. The scan now
    /// holds the same busy flag filing does, so the apply is refused and
    /// says so.</summary>
    [Fact]
    public async Task SettingsAreRefusedWhileStartIsStillScanning()
    {
        var scheduler = new HoldableWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        fx.AddInboxFile("20240115--111111.pdf");
        fx.Shell.Initialize();
        var edited = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fx.Cfg))!;
        edited.Inbox = Path.Combine(fx.Dir, "another-inbox");

        scheduler.Holding = true;
        var start = fx.Shell.StartProcessingAsync();       // parked on the inbox scan
        var apply = fx.Shell.ApplySettingsAsync(edited);   // OK pressed meanwhile
        scheduler.ReleaseNewestFirst();
        await apply;
        await start;

        Assert.Contains(fx.Dialogs.Warnings, w => w.Message.Contains("weren't applied"));
        Assert.Equal(fx.Inbox, fx.Shell.Cfg.Inbox);
        Assert.Equal(Screen.Processing, fx.Shell.Screen);
    }

    /// <summary>DW-07: a settings save that failed warned "not saved" but the
    /// app ran on the new settings anyway, until they quietly reverted at the
    /// next restart. Now a failed save changes nothing, and says so.</summary>
    [Fact]
    public async Task SettingsThatCannotBeSavedAreNotPutInUse()
    {
        using var fx = new ShellFixture();
        fx.Shell.Initialize();
        var edited = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fx.Cfg))!;
        edited.Inbox = Path.Combine(fx.Dir, "another-inbox");
        Directory.CreateDirectory(fx.CfgPath);   // config.json can't be written: a folder is in the way

        await fx.Shell.ApplySettingsAsync(edited);

        var warning = Assert.Single(fx.Dialogs.Warnings);
        Assert.Contains("still using your previous settings", warning.Message);
        Assert.Equal(fx.Inbox, fx.Shell.Cfg.Inbox);
    }
}

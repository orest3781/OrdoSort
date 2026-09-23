using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf.Services;

namespace OrdoSort.Wpf.Tests;

/// <summary>Runs background work inline, except while <see cref="Holding"/>
/// is set: then each item is queued until <see cref="ReleaseAll"/>.</summary>
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

    public void ReleaseAll() { Holding = false; _held.ReleaseAll(); }
}

/// <summary>File → Export history is fire-and-forget from the menu. Every
/// way it can stop short must reach the user — a database error as much as
/// a disk one, and a history swap in progress too.</summary>
public class ExportHistoryFailureTests
{
    [Fact]
    public void ADatabaseErrorDuringExportIsReportedNotSwallowed()
    {
        using var fx = new ShellFixture();
        // A database failure rather than a disk one: the history's
        // connection is closed, so the export's SELECT throws a
        // non-IOException — the kind that used to vanish without a word.
        fx.Shell.History.Dispose();
        fx.Dialogs.NextSaveFile = Path.Combine(fx.Dir, "export.csv");

        fx.Shell.ExportHistory();

        var warning = Assert.Single(fx.Dialogs.Warnings);
        Assert.Contains("Exporting the history", warning.Message);
        Assert.Empty(fx.Dialogs.Infos);
    }

    [Fact]
    public async Task ExportDuringAHistorySwapSaysOneMoment()
    {
        var scheduler = new HoldableWorkScheduler();
        using var fx = new ShellFixture(scheduler: scheduler);
        fx.Shell.Initialize();
        var edited = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fx.Cfg))!;
        edited.HistoryDb = Path.Combine(fx.Dir, "moved", "history.db");

        scheduler.Holding = true;
        var apply = fx.Shell.ApplySettingsAsync(edited);   // parked opening the new database
        Assert.True(fx.Shell.HistorySwapping);

        fx.Dialogs.NextSaveFile = Path.Combine(fx.Dir, "export.csv");
        fx.Shell.ExportHistory();

        var info = Assert.Single(fx.Dialogs.Infos);
        Assert.Contains("One moment", info.Message);
        Assert.False(File.Exists(Path.Combine(fx.Dir, "export.csv")));

        scheduler.ReleaseAll();
        await apply;
    }
}

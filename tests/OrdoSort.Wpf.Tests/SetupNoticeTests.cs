using System.Text.Json;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-03 (2026-09-28): a new install has no inbox folder and no
/// destinations. Start still opened a session, "File to:" was empty and Enter
/// silently did nothing. Now the dashboard says what is missing, with a link
/// to Settings, and Start stays off until both are set (owner's call).</summary>
public class SetupNoticeTests
{
    // xUnit's per-test synchronization context would post the shell's
    // continuations past the asserts; released work runs inline without it.
    public SetupNoticeTests() => SynchronizationContext.SetSynchronizationContext(null);

    [Fact]
    public void WithNoDestinationsStartStaysOffAndTheDashboardSaysWhy()
    {
        using var fx = new ShellFixture(c => c.Routes.Clear());
        fx.AddInboxFile();

        fx.Shell.Initialize();

        Assert.False(fx.Shell.StartCommand.CanExecute(null));
        var notice = Assert.Single(fx.Shell.Notices, n => n.Key == "no-destinations");
        Assert.Contains("No destinations yet", notice.Message);
        Assert.Equal("Open Settings", notice.ActionLabel);
        Assert.False(notice.CanDismiss);
    }

    [Fact]
    public void WithNoInboxFolderTheDashboardSaysSo()
    {
        using var fx = new ShellFixture(c => c.Inbox = "");

        fx.Shell.Initialize();

        Assert.False(fx.Shell.StartCommand.CanExecute(null));
        Assert.Contains(fx.Shell.Notices, n => n.Key == "inbox-unset");
    }

    [Fact]
    public void TheNoticeOpensSettings()
    {
        using var fx = new ShellFixture(c => c.Routes.Clear());
        var opened = 0;
        fx.Shell.OpenSettings = () => opened++;
        fx.Shell.Initialize();

        fx.Shell.Notices.Single(n => n.Key == "no-destinations").ActionCommand.Execute(null);

        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task AddingADestinationClearsTheNoticeAndLetsStartRun()
    {
        using var fx = new ShellFixture(c => c.Routes.Clear());
        fx.AddInboxFile();
        fx.Shell.Initialize();

        var edited = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(fx.Cfg))!;
        edited.Routes.Add(new Route { Label = "Filed", Path = fx.RouteDir });
        await fx.Shell.ApplySettingsAsync(edited);

        Assert.DoesNotContain(fx.Shell.Notices, n => n.Key == "no-destinations");
        Assert.True(fx.Shell.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartingWithNoDestinationsOpensNoSession()
    {
        using var fx = new ShellFixture(c => c.Routes.Clear());
        fx.AddInboxFile();
        fx.Shell.Initialize();

        await fx.Shell.StartProcessingAsync();

        Assert.Equal(Screen.Ready, fx.Shell.Screen);
    }
}

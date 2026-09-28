using OrdoSort.Core;
using OrdoSort.TestSupport;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>The offer to save a new password opened with an empty name and
/// Save disabled, so saving meant inventing a name first. It now suggests the
/// next free "Password N" (owner's call, 2026-09-28): one Enter saves it.</summary>
public class UnlockSaveNameTests
{
    private static async Task<UnlockViewModel> RunWithTypedPassword(Config cfg)
    {
        using var dir = new TempDir();
        var vm = new UnlockViewModel(cfg, () => true,
            unlocker: (path, password) => password == "typed"
                ? new Unlock.UnlockResult("ok", path, path, InPlace: true)
                : new Unlock.UnlockResult("wrong_password", path, Message: "That password didn't work."),
            fileSize: _ => 1,
            probe: (path, _) => new Unlock.ProbeResult("needs_password", path, Message: "x"),
            scheduler: new InlineWorkScheduler());
        await vm.AddFilesAsync(new[] { dir.File("a.pdf") });
        vm.Password = "typed";
        await vm.UnlockAsync();
        Assert.True(vm.SaveBannerVisible, "the save offer never appeared — arrangement broken");
        return vm;
    }

    [Fact]
    public async Task TheSaveOfferSuggestsPassword1AndCanSaveStraightAway()
    {
        var vm = await RunWithTypedPassword(new Config());

        Assert.Equal("Password 1", vm.SaveBannerName);
        Assert.True(vm.SaveBannerCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheSuggestionSkipsANameAlreadySaved()
    {
        var cfg = new Config();
        cfg.SavedPasswords.Add(new SavedPassword { Label = "password 1", Password = "other" });
        cfg.SavedPasswords.Add(new SavedPassword { Label = "Password 3", Password = "another" });

        var vm = await RunWithTypedPassword(cfg);

        Assert.Equal("Password 4", vm.SaveBannerName);
    }
}

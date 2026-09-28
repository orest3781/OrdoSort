using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>UX-02 (2026-09-28): Remove deleted a saved password at once and
/// then selected the next one, with the button still under the cursor, so a
/// double-click deleted two passwords every station relies on. Remove now
/// asks by name, with Keep as the default, and leaves nothing selected.</summary>
public class SavedPasswordRemoveTests
{
    private readonly Config _cfg = new();
    private readonly FakeDialogs _dialogs = new();
    private int _saves;

    private UnlockViewModel Vm() => new(_cfg, () => { _saves++; return true; }, dialogs: _dialogs);

    [Fact]
    public void KeepLeavesThePasswordWhereItWas()
    {
        _cfg.SavedPasswords.Add(new SavedPassword { Label = "Acme scans", Password = "pw" });
        var vm = Vm();
        vm.SelectedSavedEntry = vm.Saved[0];
        _dialogs.ConfirmAnswer = false;

        vm.RemoveSavedCommand.Execute(null);

        Assert.Contains("Acme scans", Assert.Single(_dialogs.Confirms).Message);
        Assert.Single(vm.Saved);
        Assert.Single(_cfg.SavedPasswords);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void AfterARemoveNothingIsSelectedSoASecondClickRemovesNothing()
    {
        _cfg.SavedPasswords.Add(new SavedPassword { Label = "Acme scans", Password = "pw1" });
        _cfg.SavedPasswords.Add(new SavedPassword { Label = "Northgate", Password = "pw2" });
        var vm = Vm();
        vm.SelectedSavedEntry = vm.Saved[0];

        vm.RemoveSavedCommand.Execute(null);

        Assert.Equal("Northgate", Assert.Single(vm.Saved).Label);
        Assert.Null(vm.SelectedSavedEntry);
        Assert.False(vm.RemoveSavedCommand.CanExecute(null));
    }
}

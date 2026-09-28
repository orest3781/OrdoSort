using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>D2: the sort list lives twice, once as Settings' choices and
/// once as the config's allowed values. If Settings offered a sort the config
/// doesn't know, choosing it saved a config.json that Config.Load then
/// refused, and the app would not start on any station until someone edited
/// the file by hand. If the config gained one Settings lacked, nobody could
/// pick it. This pins the two lists to the same keys.</summary>
public class SortChoicesDriftTests
{
    [Fact]
    public void SettingsOffersExactlyTheSortsTheConfigAccepts()
    {
        var offered = SettingsViewModel.SortChoices.Select(c => c.Key).Order().ToArray();
        var accepted = Config.Sorts.Order().ToArray();

        Assert.Equal(accepted, offered);
        Assert.Equal(offered.Length, offered.Distinct().Count());
    }
}

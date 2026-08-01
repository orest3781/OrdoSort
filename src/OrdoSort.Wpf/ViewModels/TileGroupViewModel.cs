namespace OrdoSort.Wpf.ViewModels;

/// <summary>One named section of monitored-folder tiles on the Ready
/// dashboard. Groups are projections: the TileViewModel instances are the
/// same objects held in ShellViewModel.Tiles (the flat list that drives
/// flashing and window sizing).</summary>
public sealed class TileGroupViewModel
{
    public TileGroupViewModel(string title) => Title = title;
    public string Title { get; }
    public System.Collections.ObjectModel.ObservableCollection<TileViewModel> Tiles { get; } = new();
}

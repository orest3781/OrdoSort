using System.Collections.ObjectModel;
using OrdoSort.Wpf.Mvvm;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>One spreadsheet column in the "More columns…" list.</summary>
public sealed class ColumnChoice : ObservableObject
{
    public ColumnChoice(string header, bool chosen, bool locked)
    {
        Header = header;
        IsLocked = locked;
        _isChosen = chosen || locked;
    }

    public string Header { get; }

    /// <summary>A name or id column: Review matches needs it to say who a
    /// row is, so it is always shown.</summary>
    public bool IsLocked { get; }

    private bool _isChosen;
    public bool IsChosen
    {
        get => _isChosen;
        set { if (!IsLocked) Set(ref _isChosen, value); }
    }
}

/// <summary>Review matches' "More columns…" chooser (2026-09-26): every
/// spreadsheet column with a tick, in spreadsheet order, narrowed by a
/// search box — rosters run to 15-40 columns. Works on its own copy of the
/// choice; the window applies <see cref="Chosen"/> only on OK.</summary>
public sealed class ColumnChooserViewModel : ObservableObject
{
    private readonly List<ColumnChoice> _all;

    public ColumnChooserViewModel(IEnumerable<string> all, IEnumerable<string> locked, IEnumerable<string> shown)
    {
        var lockedSet = new HashSet<string>(locked, StringComparer.Ordinal);
        var shownSet = new HashSet<string>(shown, StringComparer.Ordinal);
        _all = all.Distinct(StringComparer.Ordinal)
            .Select(h => new ColumnChoice(h, shownSet.Contains(h), lockedSet.Contains(h)))
            .ToList();
        Filter();
    }

    /// <summary>The columns matching <see cref="Search"/>.</summary>
    public ObservableCollection<ColumnChoice> Visible { get; } = new();

    private string _search = "";
    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value ?? "")) Filter(); }
    }

    /// <summary>Every ticked column, searched-away ones included, in
    /// spreadsheet order.</summary>
    public IReadOnlyList<string> Chosen => _all.Where(c => c.IsChosen).Select(c => c.Header).ToList();

    private void Filter()
    {
        var text = _search.Trim();
        Visible.Clear();
        foreach (var choice in _all)
            if (text.Length == 0 || choice.Header.Contains(text, StringComparison.OrdinalIgnoreCase))
                Visible.Add(choice);
    }
}

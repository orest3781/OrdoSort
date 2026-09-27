using System.Windows;
using System.Windows.Input;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

/// <summary>Review matches' "More columns…" chooser. Returns the ticked
/// columns on OK, null on Cancel or Escape.</summary>
public partial class ColumnChooserWindow : Window
{
    private readonly ColumnChooserViewModel _vm;
    private IReadOnlyList<string>? _answer;

    internal ColumnChooserWindow(ColumnChooserViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            _answer = null;
            Close();
        };
        Loaded += (_, _) => SearchBox.Focus();
    }

    internal IReadOnlyList<string>? Answer => _answer;

    /// <summary>Shows the chooser over <paramref name="owner"/>; the chosen
    /// columns, or null when the choice was cancelled.</summary>
    public static IReadOnlyList<string>? Ask(Window owner, IEnumerable<string> all,
        IEnumerable<string> locked, IEnumerable<string> shown)
    {
        var window = new ColumnChooserWindow(new ColumnChooserViewModel(all, locked, shown)) { Owner = owner };
        window.ShowDialog();
        return window._answer;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _answer = _vm.Chosen;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _answer = null;
        Close();
    }
}

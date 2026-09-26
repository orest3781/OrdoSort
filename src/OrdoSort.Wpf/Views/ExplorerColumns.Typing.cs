using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace OrdoSort.Wpf.Views;

internal sealed partial class ExplorerColumns
{
    /// <summary>Letters typed within this long of each other build one
    /// search, as in Explorer.</summary>
    internal static readonly TimeSpan TypeAheadWindow = TimeSpan.FromSeconds(1);

    private string _typed = "";
    private DateTime _lastTyped = DateTime.MinValue;

    partial void WireTypingCore() => _grid.PreviewTextInput += (_, e) =>
    {
        if (Keyboard.Modifiers is ModifierKeys.Control or ModifierKeys.Alt) return;
        if (TypeAhead(e.Text)) e.Handled = true;
    };

    /// <summary>Jumps to the next row whose anchor column (the File list's
    /// File name; otherwise the first visible column) starts with what was
    /// typed, as Explorer matches on Name. Returns false (and leaves the keystroke alone) while a
    /// cell is being edited, or for non-printing input.</summary>
    internal bool TypeAhead(string text)
    {
        if (string.IsNullOrEmpty(text) || char.IsControl(text[0]) || IsEditing()) return false;

        var now = _clock();
        var continuing = now - _lastTyped <= TypeAheadWindow;
        _lastTyped = now;
        // Explorer: the same letter again moves on; a new letter refines.
        _typed = continuing && !(_typed.Length == 1 && _typed == text) ? _typed + text : text;

        var first = (_explicitAnchor as DataGridTextColumn)
            ?? _grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex)
                .FirstOrDefault() as DataGridTextColumn;
        if (first?.Binding is not BindingBase binding) return true;

        var items = _grid.Items.Cast<object>().Where(i => i != CollectionView.NewItemPlaceholder).ToList();
        if (items.Count == 0) return true;
        var start = _grid.SelectedIndex < 0 ? 0 : _grid.SelectedIndex + (_typed.Length == 1 ? 1 : 0);
        for (var step = 0; step < items.Count; step++)
        {
            var item = items[(start + step) % items.Count];
            if (ValueOf(item, binding).StartsWith(_typed, StringComparison.CurrentCultureIgnoreCase))
            {
                _grid.SelectedItem = item;
                _grid.CurrentCell = new DataGridCellInfo(item, first);
                _grid.ScrollIntoView(item);
                break;
            }
        }
        return true;
    }

    private bool IsEditing() =>
        Keyboard.FocusedElement is DependencyObject focused && focused is TextBoxBase
        && FindAncestor<DataGrid>(focused) == _grid;

    partial void ClearSelectionOnEmptySpaceCore(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source) ClearIfEmptySpace(source);
    }

    /// <summary>Clears the selection when <paramref name="source"/> is the
    /// grid's blank area (not a row, header or scrollbar), as a click below
    /// Explorer's last file does.</summary>
    internal bool ClearIfEmptySpace(DependencyObject source)
    {
        if (FindAncestor<DataGridRow>(source) is not null || FindAncestor<DataGridColumnHeader>(source) is not null
            || FindAncestor<ScrollBar>(source) is not null || FindAncestor<DataGrid>(source) != _grid)
            return false;
        _grid.UnselectAll();
        return true;
    }
}

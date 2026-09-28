using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OrdoSort.Wpf.Views;

internal sealed partial class ExplorerColumns
{
    /// <summary>Left plus right cell padding, as Theme/Styles.xaml's
    /// DataGridCell style draws it (12 each side). A test keeps the two equal.</summary>
    public const double CellHorizontalPadding = 24;

    // A little room so a fitted value never trims on rounding.
    private const double FitSlack = 4;

    // Reused to evaluate a column's binding against each item without
    // realizing a row.
    private readonly TextBlock _probe = new();

    partial void WireInputCore()
    {
        _grid.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        _grid.PreviewMouseRightButtonUp += OnPreviewMouseRightButtonUp;
        _grid.PreviewKeyDown += OnPreviewKeyDown;
        WireTypingCore();
    }

    partial void WireTypingCore();

    /// <summary>The width that shows every value of <paramref name="column"/>
    /// whole, and its header, measured with the grid's font. Reads values
    /// through the column's own binding, so formats and indexers come out
    /// as the cells show them.</summary>
    public double MeasureFit(DataGridColumn column)
    {
        var widest = TextWidth(HeaderOf(column) ?? "", FontWeights.SemiBold);
        // A row-number column (the File list's #) binds to its own row, which
        // the detached probe can't reach; its widest value is the last number.
        if (column is DataGridTextColumn { Binding: Binding { RelativeSource: not null } })
            widest = Math.Max(widest, TextWidth(_grid.Items.Count.ToString(CultureInfo.CurrentCulture), FontWeights.Normal));
        else if (column is DataGridTextColumn { Binding: BindingBase binding })
            foreach (var item in _grid.Items)
            {
                if (item == CollectionView.NewItemPlaceholder) continue;
                widest = Math.Max(widest, TextWidth(ValueOf(item, binding), FontWeights.Normal));
            }
        return Math.Max(MinColumnWidth, Math.Ceiling(widest + CellHorizontalPadding + FitSlack));
    }

    /// <summary>Sizes one column to its content (the divider double-click).</summary>
    public void FitColumn(DataGridColumn column) => column.Width = new DataGridLength(MeasureFit(column));

    /// <summary>Sizes every visible column to its content (Ctrl + Plus).</summary>
    public void FitAll()
    {
        foreach (var column in _grid.Columns.Where(c => c.Visibility == Visibility.Visible && !IsControlColumn(c)))
            FitColumn(column);
    }

    private string ValueOf(object item, BindingBase binding)
    {
        _probe.DataContext = item;
        _probe.SetBinding(TextBlock.TextProperty, binding);
        var text = _probe.Text;
        BindingOperations.ClearBinding(_probe, TextBlock.TextProperty);
        return text ?? "";
    }

    private double TextWidth(string text, FontWeight weight)
    {
        if (text.Length == 0) return 0;
        var typeface = new Typeface(_grid.FontFamily, FontStyles.Normal, weight, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
            _grid.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(_grid).PixelsPerDip).WidthIncludingTrailingWhitespace;
    }

    /// <summary>A double-click on a header divider fits the column to the
    /// divider's LEFT, as in Explorer. Returns false when the thumb isn't a
    /// header divider.</summary>
    internal bool TryFitFromGripper(Thumb thumb)
    {
        if (GripperTarget(thumb) is not { } column) return false;
        FitColumn(column);
        return true;
    }

    private DataGridColumn? GripperTarget(Thumb thumb)
    {
        if (FindAncestor<DataGridColumnHeader>(thumb) is not { Column: { } column }) return null;
        if (thumb.Name == "PART_RightHeaderGripper") return column;
        if (thumb.Name == "PART_LeftHeaderGripper")
            return _grid.Columns.Where(c => c.Visibility == Visibility.Visible && c.DisplayIndex < column.DisplayIndex)
                .OrderByDescending(c => c.DisplayIndex).FirstOrDefault();
        return null;
    }

    // WPF's own divider double-click sets the column to Auto (which then
    // re-grows as rows arrive, breaking rule 1). Marking the second press
    // handled doesn't stop that: Control raises MouseDoubleClick on the thumb
    // regardless, and the header answers it with Auto. So fit now, and fit
    // again once that has run.
    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is { } thumb
            && GripperTarget(thumb) is { } column)
        {
            FitColumn(column);
            _grid.Dispatcher.BeginInvoke(() => FitColumn(column), DispatcherPriority.Input);
            e.Handled = true;
            return;
        }
        ClearSelectionOnEmptySpace(e);
    }

    partial void ClearSelectionOnEmptySpaceCore(MouseButtonEventArgs e);

    private void ClearSelectionOnEmptySpace(MouseButtonEventArgs e) => ClearSelectionOnEmptySpaceCore(e);

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { Column: { } column } header
            || IsControlColumn(column))
            return;
        var menu = BuildHeaderMenu(column);
        menu.PlacementTarget = header;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Explorer's header menu: fit this column, fit all, then a
    /// checklist of columns. The anchor column's entry is disabled.</summary>
    public ContextMenu BuildHeaderMenu(DataGridColumn clicked)
    {
        var menu = new ContextMenu();
        var fitOne = new MenuItem { Header = "Size column to fit" };
        fitOne.Click += (_, _) => FitColumn(clicked);
        var fitAll = new MenuItem { Header = "Size all columns to fit" };
        fitAll.Click += (_, _) => FitAll();
        menu.Items.Add(fitOne);
        menu.Items.Add(fitAll);
        menu.Items.Add(new Separator());
        foreach (var column in _grid.Columns.OrderBy(c => c.DisplayIndex))
        {
            if (HeaderOf(column) is not { } header) continue;
            // with a chooser, the menu is the quick way to hide; the chooser
            // is the way to find and show the rest
            if (_chooseColumns is not null && !_visibility.IsShown(column)) continue;
            var item = new MenuItem
            {
                Header = header, IsCheckable = true, IsChecked = _visibility.IsShown(column),
                IsEnabled = column != Anchor && _visibility.CanChange(column),
            };
            var target = column;
            item.Click += (_, _) => _visibility.SetShown(target, !_visibility.IsShown(target));
            menu.Items.Add(item);
        }
        if (_chooseColumns is { } choose)
        {
            menu.Items.Add(new Separator());
            var more = new MenuItem { Header = "More columns…" };
            more.Click += (_, _) => choose();
            menu.Items.Add(more);
        }
        return menu;
    }

    // Ctrl + Plus, on the main keyboard or the keypad; the Menu key or
    // Shift + F10 for the header menu.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add)
        {
            FitAll();
            e.Handled = true;
            return;
        }
        // F10 arrives as a system key.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Apps || (key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
            e.Handled = OpenHeaderMenuFromKeyboard();
    }

    /// <summary>Opens the header menu for the focused cell's column (or the
    /// first column when no cell has focus), under that column's header.
    /// Returns false, leaving the key alone, when the table has a menu of its
    /// own: that is what the key opens there (the File list's Remove and
    /// Undo), and its columns have their own toggles.</summary>
    private bool OpenHeaderMenuFromKeyboard()
    {
        if (_grid.ContextMenu is not null) return false;
        var column = _grid.CurrentCell.Column is { } current && !IsControlColumn(current)
                     && current.Visibility == Visibility.Visible
            ? current
            : FirstDataColumn();
        if (column is null) return false;
        var menu = BuildHeaderMenu(column);
        menu.PlacementTarget = HeaderFor(column) ?? (UIElement)_grid;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        return true;
    }

    private DataGridColumnHeader? HeaderFor(DataGridColumn column)
    {
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(_grid);
        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            if (node is DataGridColumnHeader header && header.Column == column) return header;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                pending.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        return node as T;
    }
}

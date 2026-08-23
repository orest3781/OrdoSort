using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrdoSort.Wpf.Services;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Windows;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Closing += OnClosing;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_vm.TryBuildResult()) DialogResult = true;
    }

    /// <summary>Seven tabs of editing used to vanish on Esc without a word.
    /// Esc is safe in every other window in this app, which is exactly why
    /// reaching for it here was so easy (UI-06).
    ///
    /// Hooked on Closing rather than on the Cancel button, because all three
    /// ways out have to be covered and only one of them is that button: Cancel
    /// and Esc both go through <c>IsCancel</c>, and the title bar's X goes
    /// through neither. Closing is the one place they meet.
    ///
    /// <c>DialogResult == true</c> means OK already built and accepted the
    /// result, so there is nothing to discard and nothing to ask about.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DialogResult == true) return;
        if (!_vm.IsDirty) return;
        if (_vm.Dialogs.Confirm(
                "Discard your changes to the settings?",
                "OrdoSort — unsaved changes"))
            return;
        e.Cancel = true;
    }


    /// <summary>The hotkey box records the actual keystroke instead of free
    /// text — what you press is exactly what will file.</summary>
    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Back or Key.Delete:
                box.SetCurrentValue(TextBox.TextProperty, "");
                UpdateSource(box);
                return;
            case Key.Tab:
                e.Handled = false;   // keep keyboard navigation working
                return;
            case Key.Escape:
                // Decision: Escape closes the dialog (Cancel), not "cancel
                // capture and stay open". There is no separate draft state
                // to unwind here — every recognized key already commits
                // straight into the bound Hotkey property below via
                // UpdateSource, so "cancel capture" has nothing distinct to
                // roll back. Letting Escape through unhandled (same
                // exemption as Tab, just above) restores the ordinary
                // Windows-dialog convention every other control in this
                // window already gets for free via the Cancel button's
                // IsCancel="True" — the previous unconditional
                // e.Handled = true swallowed it before it ever reached that,
                // recording the literal hotkey "Escape" instead.
                e.Handled = false;
                return;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                return;              // modifiers alone aren't a hotkey yet
        }
        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            _ => key.ToString(),
        });
        box.SetCurrentValue(TextBox.TextProperty, string.Join("+", parts));
        UpdateSource(box);
    }

    private static void UpdateSource(TextBox box) =>
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

    private void OnRouteSwatch(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedRoute is { } r && sender is Button { Tag: string color })
            r.Color = color;
    }

    private void OnWatchSwatch(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedWatch is { } w && sender is Button { Tag: string color })
            w.Color = color;
    }

    private void OnSectionRenameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WatchSectionVm h } btn) return;
        _vm.BeginSectionRename(h);
        // focus the edit box once its Visible state has applied
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (btn.Parent is StackPanel { Parent: Grid g })
                foreach (var child in g.Children)
                    if (child is TextBox tb) { tb.Focus(); tb.SelectAll(); }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnSectionEditLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: WatchSectionVm h }) _vm.CommitSectionRename(h);
    }

    private void OnSectionEditKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: WatchSectionVm h }) return;
        if (e.Key == Key.Enter) { _vm.CommitSectionRename(h); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.CancelSectionRename(h); e.Handled = true; }
    }

    private void OnSectionAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WatchSectionVm h }) _vm.AddFolderToSection(h);
    }

    private void OnSectionRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WatchSectionVm h }) _vm.RemoveSection(h);
    }

    private void OnAddSectionClick(object sender, RoutedEventArgs e)
    {
        if (_vm.AddSection() is not { } header) return;
        // container generation is async — focus the header's edit box after
        // the rebuilt list has generated it
        Dispatcher.BeginInvoke(new Action(() =>
        {
            WatchList.ScrollIntoView(header);
            WatchList.UpdateLayout();
            if (WatchList.ItemContainerGenerator.ContainerFromItem(header) is ListBoxItem item
                && FindDescendant<TextBox>(item) is { IsVisible: true } tb)
            {
                tb.Focus();
                tb.SelectAll();
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deep) return deep;
        }
        return null;
    }

    private void OnFontSizePreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string size }) _vm.UiFontSizeText = size;
    }

    private void OnPollPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string secs }) _vm.PollSecondsText = secs;
    }

    // ------------------------------------------- list drag-and-drop reorder
    private Point _dragStart;
    private object? _dragItem;

    private static object? RowItemAt(ListBox list, object? origin)
    {
        var node = origin as DependencyObject;
        while (node is not null and not ListBoxItem)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        return node is ListBoxItem item && list.ItemContainerGenerator.ItemFromContainer(item)
            is { } data && data != DependencyProperty.UnsetValue ? data : null;
    }

    private void List_DragArm(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = RowItemAt((ListBox)sender, e.OriginalSource);
    }

    private void List_DragMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(null) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        if (item is not (RouteEditVm or WatchEditVm)) return;   // headers don't drag
        DragDrop.DoDragDrop((ListBox)sender, item, DragDropEffects.Move);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        var list = (ListBox)sender;
        var over = RowItemAt(list, e.OriginalSource);
        if (list == RouteList && e.Data.GetData(typeof(RouteEditVm)) is RouteEditVm route)
        {
            MoveWithin(_vm.Routes, route, over as RouteEditVm);
            _vm.SelectedRoute = route;
        }
        else if (list == WatchList && e.Data.GetData(typeof(WatchEditVm)) is WatchEditVm watch)
        {
            _vm.DropWatch(watch, over);
        }
    }

    private static void MoveWithin<T>(System.Collections.ObjectModel.ObservableCollection<T> items,
        T dragged, T? target) where T : class
    {
        var from = items.IndexOf(dragged);
        var to = target is null ? items.Count - 1 : items.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to) items.Move(from, to);
    }
}

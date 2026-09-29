using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

/// <summary>BoxLabels.exe's menu (File → Change labels file…, Exit;
/// Settings…) and the line naming which box-labels store is open.
///
/// Null in OrdoSort: there the labels file and the label style are edited on
/// its own Settings page, and a second way to change them here would disagree
/// with that one. Non-null in BoxLabels.exe, where this is the only place
/// they exist.</summary>
/// <param name="LabelsFile">Shown in full; trimmed with a tooltip when too long.</param>
/// <param name="ChangeFile">File → Change labels file…. The host owns everything
/// that follows — picking, validating, and swapping the window.</param>
/// <param name="OpenSettings">Settings… (and Ctrl+,).</param>
public sealed record StandaloneMenu(string LabelsFile, Action ChangeFile, Action OpenSettings);

public partial class LabelMakerWindow : Window
{
    private readonly string _previewTitle;

    /// <summary>The host supplies its own names and says whether this is its
    /// main window.
    ///
    /// In OrdoSort it is an owned modal off the Tools menu, which is why the
    /// XAML says ShowInTaskbar="False" and CenterOwner. In BoxLabels.exe it
    /// IS the application: it has no owner to centre on, and a window with no
    /// taskbar button and no owner is one the user cannot alt-tab back to.
    /// <paramref name="standalone"/> switches those two, and makes the Close
    /// button close a window that is not a dialog.
    ///
    /// <paramref name="standaloneMenu"/> is null in OrdoSort, which is what
    /// keeps that window unchanged — see <see cref="StandaloneMenu"/>.</summary>
    public LabelMakerWindow(LabelMakerViewModel vm, string windowTitle,
        string previewTitle, bool standalone = false, StandaloneMenu? standaloneMenu = null)
    {
        InitializeComponent();
        Title = windowTitle;
        _previewTitle = previewTitle;
        if (standalone)
        {
            ShowInTaskbar = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // IsCancel alone only ends a dialog, and here this is a main
            // window. Not wired in OrdoSort, where IsCancel already closes
            // the dialog and a second Close() would run mid-close.
            CloseButton.Click += (_, _) => Close();
        }
        if (standaloneMenu is not null)
        {
            StoreBar.Visibility = Visibility.Visible;
            StorePathText.Text = standaloneMenu.LabelsFile;
            StorePathText.ToolTip = standaloneMenu.LabelsFile;   // trimmed in the bar; whole on hover
            ChangeFileMenuItem.Click += (_, _) => standaloneMenu.ChangeFile();
            ExitMenuItem.Click += (_, _) => Close();
            SettingsMenuItem.Click += (_, _) => standaloneMenu.OpenSettings();
            InputBindings.Add(new KeyBinding(new RelayCommand(standaloneMenu.OpenSettings),
                Key.OemComma, ModifierKeys.Control));

            // The window has to grow by exactly what the bar takes, or it
            // takes the room from the form instead. The Grid below has a *
            // row, so nothing complains and nothing clips: the last rows of
            // the form simply end up underneath the preview section, and its
            // last row is not on screen at all. Measured rather than a
            // constant because the bar follows the user's configured font,
            // which ranges 6-72.
            StoreBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var barHeight = StoreBar.DesiredSize.Height;
            Height += barHeight;
            MinHeight += barHeight;
        }
        DataContext = vm;
        vm.PrintSheets = PrintSheets;
        vm.RequestIdFocus += () => { IdBox.Focus(); IdBox.SelectAll(); };
        // client edits are settings — they stick even without printing.
        // A duplicate id on screen must keep the window open — otherwise the
        // warning that tells the user to "fix the duplicate before closing"
        // is a lie, and every edit in the session, including unrelated ones,
        // is discarded with it. TryPersist returns false ONLY for that case
        // (never for a store failure the user has no in-window way to fix —
        // see its doc comment) so this stays a plain "block on false."
        Closing += (_, e) => { if (!vm.TryPersist()) e.Cancel = true; };
    }

    private void OnCountPreset(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string n })
            ((LabelMakerViewModel)DataContext).LabelCountText = n;
    }

    /// <summary>In-app printing: the batch becomes a US-letter FixedDocument
    /// (same layout as the PDF and the preview card), shown in a print
    /// preview first — printing happens from there. WPF maps 96 DIPs to one
    /// physical inch, so the output is always at true scale; there is no
    /// "fit to page" step to get wrong.</summary>
    private bool PrintSheets(IReadOnlyList<BoxLabels.Item> items, string jobName)
    {
        var vm = (LabelMakerViewModel)DataContext;
        var preview = new PrintPreviewWindow(LabelPrinting.BuildDocument(items, vm.Style), jobName,
            msg => vm.Dialogs.Warn(msg, vm.AppTitle), _previewTitle,
            extraCopies: async extra => await vm.ClaimCopiesAsync(extra) is { } more
                ? LabelPrinting.BuildDocument(items.Concat(more).ToList(), vm.Style)
                : null);
        preview.Owner = this;
        preview.ShowDialog();
        return preview.Printed;
    }
}

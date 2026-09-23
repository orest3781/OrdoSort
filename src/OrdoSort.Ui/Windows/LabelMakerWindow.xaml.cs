using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

/// <summary>The bar naming which box-labels store is open, and the action
/// that changes it.
///
/// Null in OrdoSort: there the path is a config.json key edited on the
/// Settings page, and a button here that wrote config.json would be a second
/// way to change one setting. Non-null in BoxLabels.exe, where this is the
/// only place that path exists at all.</summary>
/// <param name="Path">Shown in full; trimmed with a tooltip when too long.</param>
/// <param name="ChangeFile">Invoked by the button. The host owns everything
/// that follows — picking, validating, and swapping the window.</param>
public sealed record LabelStoreBar(string Path, Action ChangeFile);

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
    /// <paramref name="standalone"/> switches those two, and nothing else.
    ///
    /// <paramref name="storeBar"/> is null in OrdoSort, which is what keeps
    /// that window unchanged — see <see cref="LabelStoreBar"/>.</summary>
    public LabelMakerWindow(LabelMakerViewModel vm, string windowTitle,
        string previewTitle, bool standalone = false, LabelStoreBar? storeBar = null)
    {
        InitializeComponent();
        Title = windowTitle;
        _previewTitle = previewTitle;
        if (standalone)
        {
            ShowInTaskbar = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (storeBar is not null)
        {
            StoreBar.Visibility = Visibility.Visible;
            StorePathText.Text = storeBar.Path;
            StorePathText.ToolTip = storeBar.Path;   // trimmed in the bar; whole on hover
            ChangeStoreButton.Click += (_, _) => storeBar.ChangeFile();

            // The window has to grow by exactly what the bar takes, or it
            // takes the room from the form instead. The Grid below has a *
            // row, so nothing complains and nothing clips: the last rows of
            // the form simply end up underneath the preview section, and the
            // date-style choice is not on screen at all. Measured rather than
            // a constant because the bar is one line of the user's configured
            // font, which ranges 6-72.
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
        var preview = new PrintPreviewWindow(LabelPrinting.BuildDocument(items, vm.DateStyle), jobName,
            msg => vm.Dialogs.Warn(msg, vm.AppTitle), _previewTitle);
        preview.Owner = this;
        preview.ShowDialog();
        return preview.Printed;
    }
}

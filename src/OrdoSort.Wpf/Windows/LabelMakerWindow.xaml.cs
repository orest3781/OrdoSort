using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;
using OrdoSort.Wpf.Views;

namespace OrdoSort.Wpf.Windows;

public partial class LabelMakerWindow : Window
{
    public LabelMakerWindow(LabelMakerViewModel vm)
    {
        InitializeComponent();
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
            msg => vm.Dialogs.Warn(msg, "OrdoSort — label maker")) { Owner = this };
        preview.ShowDialog();
        return preview.Printed;
    }
}

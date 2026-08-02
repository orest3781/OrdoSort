using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace OrdoSort.Wpf.Windows;

/// <summary>A DocumentViewer whose print button (and Ctrl+P) runs OUR print
/// flow — the window must know whether the job was actually sent, so the
/// label counter only advances for real prints.
///
/// DocumentViewer chrome (Task 2, theme-coverage audit, 2026-08-02): before
/// this task Styles.xaml had no ToolBar style at all, so the toolbar across
/// the top of this control rendered stock Aero2's light-blue background
/// (#FFEEF5FD) regardless of theme — a white island across an otherwise-dark
/// window. Theme/Styles.xaml's new implicit ToolBar Style (Background/
/// Foreground) plus a {x:Static ToolBar.SeparatorStyleKey} Style fix that,
/// confirmed via a live visual-tree dump (every named part inside the stock
/// ToolBar ControlTemplate — MainPanelBorder, the group separators — is
/// TemplateBound to those two properties, no retemplate needed).
///
/// That fix is PARTIAL, and the rest is left stock deliberately, not
/// silently:
/// - The Find toolbar (Ctrl+F inside this preview) is
///   <c>MS.Internal.Documents.FindToolBar</c> — internal to
///   PresentationFramework, so no XAML in this app can name its exact type
///   to key a style to it, and the same dump confirmed it does NOT fall back
///   to the plain ToolBar style despite deriving from ToolBar (its own
///   MainPanelBorder stayed the stock colour while the real ToolBar's
///   identically-named part correctly followed the new style). A
///   reflection-based workaround (resolve FindToolBar's runtime System.Type
///   and register a Style under that exact key) is possible but wasn't taken
///   — it would pin this app's chrome to an undocumented internal type name
///   that could vanish on any .NET update, for a bar that only appears
///   behind an opt-in Ctrl+F, not the first-glance defect the main toolbar
///   was.
/// - The page-layout button group (ActualSize/PageWidth/WholePage/
///   TwoPages) keeps a stock light "chip" background. A matching
///   {x:Static ToolBar.ButtonStyleKey} Style was tried in Styles.xaml and
///   verified inert (see that file's comment) — reaching it would need a
///   full custom retemplate of the native per-button chrome, risking the
///   print/zoom/layout buttons' actual glyph rendering for a cosmetic
///   residual that reads far less jarring than the full-width bar this fix
///   already removes.
///
/// Rendered proof (dark mode, before and after) lives at the task's
/// scratchpad root as printpreview-fixed-dark.png; HighlightContrastTests.
/// PrintPreviewToolBarUsesThemeChrome is the automated regression guard for
/// the reachable part.</summary>
public sealed class PreviewDocumentViewer : DocumentViewer
{
    internal Action? PrintRequested { get; set; }
    protected override void OnPrintCommand() => PrintRequested?.Invoke();
}

/// <summary>Print preview for label sheets: shows the exact FixedDocument
/// that will spool, with the viewer's zoom and page navigation, plus a
/// printer picker and copies — Print spools straight to the chosen queue
/// with no OS dialog (Windows 11's print dialog shows a bogus "no preview"
/// pane for XPS jobs; this window IS the preview). Cancel/Esc leaves the
/// label counter untouched.</summary>
public partial class PrintPreviewWindow : Window
{
    private readonly FixedDocument _doc;
    private readonly string _jobName;
    private readonly Action<string> _warn;

    public bool Printed { get; private set; }

    public PrintPreviewWindow(FixedDocument doc, string jobName, Action<string> warn)
    {
        InitializeComponent();
        _doc = doc;
        _jobName = jobName;
        _warn = warn;
        Viewer.Document = doc;
        Viewer.PrintRequested = PrintNow;
        // the sheet itself carries no instructions — the paper's margins are
        // the bottom row's clearance from the printer's unprintable edge, so
        // the note that used to print up there lives here instead
        PageInfo.Text = $"{doc.Pages.Count} sheet{(doc.Pages.Count == 1 ? "" : "s")}"
            + "   ·   " + OrdoSort.Core.BoxLabels.SheetNote;
        LoadPrinters();
        Loaded += (_, _) => Viewer.FitToMaxPagesAcross(1);
    }

    private void LoadPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            foreach (var q in server.GetPrintQueues(new[]
            {
                EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections,
            }))
                Printers.Items.Add(q.FullName);
            try
            {
                Printers.SelectedItem = server.DefaultPrintQueue.FullName;
            }
            catch { /* no default set */ }
        }
        catch { /* spooler unavailable — handled below */ }

        if (Printers.SelectedIndex < 0 && Printers.Items.Count > 0) Printers.SelectedIndex = 0;
        if (Printers.Items.Count == 0)
        {
            PrintButton.IsEnabled = false;
            PrintNote.Text = "No printers found.";
        }
    }

    private void OnPrint(object sender, RoutedEventArgs e) => PrintNow();

    private void PrintNow()
    {
        if (Printers.SelectedItem is not string printerName) return;
        if (!int.TryParse(Copies.Text.Trim(), out var copies) || copies is < 1 or > 99)
        {
            PrintNote.Text = "Copies must be 1 to 99.";
            return;
        }
        try
        {
            using var server = new LocalPrintServer();
            var queue = new PrintQueue(server, printerName);
            var dlg = new PrintDialog { PrintQueue = queue };
            dlg.PrintTicket.CopyCount = copies;
            // no ShowDialog: this window already chose everything — spool it
            dlg.PrintDocument(_doc.DocumentPaginator, _jobName);
        }
        catch (Exception ex)
        {
            _warn("Printing failed: " + ex.Message);
            return;
        }
        Printed = true;
        DialogResult = true;
    }
}

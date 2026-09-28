using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace OrdoSort.Wpf.Windows;

/// <summary>A DocumentViewer whose print button (and Ctrl+P) runs OUR print
/// flow — the window must know whether the job was actually sent, so the
/// label maker can say whether the sheets went out. The box numbers on them
/// were already claimed before this preview opened; not printing does not
/// give them back.
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
///   <c>MS.Internal.Documents.FindToolBar</c> — internal to the
///   PresentationUI assembly (confirmed by reflection: its Type.Assembly is
///   PresentationUI, not PresentationFramework, even though it derives from
///   the public System.Windows.Controls.ToolBar in PresentationFramework),
///   so no XAML in this app can name its exact type to key a style to it,
///   and the same dump confirmed it does NOT fall back
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

/// <summary>What the print preview found to print to. <see cref="SpoolerError"/>
/// is "" unless the print service itself could not be asked.</summary>
public sealed record PrinterList(IReadOnlyList<string> Names, string? Default, string SpoolerError = "");

/// <summary>Print preview for label sheets: shows the exact FixedDocument
/// that will spool, with the viewer's zoom and page navigation, plus a
/// printer picker and copies — Print spools straight to the chosen queue
/// with no OS dialog (Windows 11's print dialog shows a bogus "no preview"
/// pane for XPS jobs; this window IS the preview). Cancel/Esc sends nothing
/// to the printer, but the box numbers shown were claimed before this window
/// opened and stay used: they are skipped, never issued twice.</summary>
public partial class PrintPreviewWindow : Window
{
    private readonly FixedDocument _doc;
    private readonly string _jobName;
    private readonly Action<string> _warn;
    private readonly Func<int, Task<FixedDocument?>>? _extraCopies;
    private readonly Dictionary<int, FixedDocument> _withCopies = new();
    private bool _claiming;

    public bool Printed { get; private set; }

    /// <summary><paramref name="windowTitle"/> is the caller's, because this
    /// window is shared by two applications and a library has no business
    /// naming either of them. The default is what an unbranded host gets;
    /// OrdoSort and BoxLabels each pass their own, so the title bar never
    /// says the name of a product the user does not have.
    ///
    /// <paramref name="extraCopies"/>, when given, numbers every copy past the
    /// first on its own (QC-15: box labels must never repeat a barcode). It
    /// is handed how many extra copies are wanted and returns the whole job,
    /// first copy included, or null when they could not be numbered; the
    /// job then spools once. Without it, Copies asks the printer to repeat
    /// the document.
    ///
    /// <paramref name="findPrinters"/> asks Windows for the printers; the
    /// default does it on a pool thread. Tests pass their own answer.</summary>
    public PrintPreviewWindow(FixedDocument doc, string jobName, Action<string> warn,
        string windowTitle = "Print preview", Func<int, Task<FixedDocument?>>? extraCopies = null,
        Func<Task<PrinterList>>? findPrinters = null)
    {
        InitializeComponent();
        Title = windowTitle;
        _doc = doc;
        _jobName = jobName;
        _warn = warn;
        _extraCopies = extraCopies;
        Viewer.Document = doc;
        Viewer.PrintRequested = PrintNow;
        // the sheet itself carries no instructions — the paper's margins are
        // the bottom row's clearance from the printer's unprintable edge, so
        // the note that used to print up there lives here instead
        PageInfo.Text = $"{doc.Pages.Count} sheet{(doc.Pages.Count == 1 ? "" : "s")}"
            + "   ·   " + OrdoSort.Core.BoxLabels.SheetNote;
        // Asked off the UI thread (Q2-22): an offline network printer can
        // take a long time to answer, and asked here the whole app stood
        // still before this window even appeared.
        PrintButton.IsEnabled = false;
        PrintNote.Text = "Finding printers…";
        _ = LoadPrintersAsync(findPrinters ?? (() => Task.Run(FindPrinters)));
        Loaded += (_, _) => Viewer.FitToMaxPagesAcross(1);
        Copies.TextChanged += (_, _) => ShowCopiesNote();
        // A claim that lands after the window has gone would count numbers
        // for a print that never happens, so the window waits for it.
        Closing += (_, e) => e.Cancel = _claiming;
    }

    /// <summary>The printers Windows knows of, and its default. When the
    /// print service itself could not be asked (a stopped spooler), the
    /// list is empty and <see cref="PrinterList.SpoolerError"/> says why.
    /// Runs on a pool thread; touches nothing on the window.</summary>
    internal static PrinterList FindPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            var names = server.GetPrintQueues(new[]
            {
                EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections,
            }).Select(q => q.FullName).ToList();
            string? fallback = null;
            try
            {
                fallback = server.DefaultPrintQueue.FullName;
            }
            catch { /* no default set */ }
            return new PrinterList(names, fallback);
        }
        catch (Exception ex)
        {
            return new PrinterList(Array.Empty<string>(), null, ex.Message);
        }
    }

    private async Task LoadPrintersAsync(Func<Task<PrinterList>> findPrinters)
    {
        PrinterList found;
        try { found = await findPrinters(); }
        catch (Exception ex) { found = new PrinterList(Array.Empty<string>(), null, ex.Message); }

        foreach (var name in found.Names) Printers.Items.Add(name);
        if (found.Default is { } preferred && Printers.Items.Contains(preferred))
            Printers.SelectedItem = preferred;
        if (Printers.SelectedIndex < 0 && Printers.Items.Count > 0) Printers.SelectedIndex = 0;
        if (Printers.Items.Count == 0)
        {
            // Two causes, two answers (DW-42): a stopped print service lists
            // nothing either, and "no printers" sent people looking for a
            // printer that was there all along.
            PrintNote.Text = found.SpoolerError.Length > 0
                ? $"Windows' print service isn't answering ({found.SpoolerError}). "
                  + "Start the Print Spooler service, then open this preview again."
                : "No printers found.";
            return;
        }
        PrintButton.IsEnabled = !_claiming;
        PrintNote.Text = "";
        ShowCopiesNote();
    }

    /// <summary>With box labels, says before printing that each extra copy
    /// gets its own numbers (QC-15). Silent until there is a printer, so a
    /// "finding" or "no printers" note is never overwritten.</summary>
    private void ShowCopiesNote()
    {
        if (_extraCopies is null || Printers.Items.Count == 0) return;
        PrintNote.Text = int.TryParse(Copies.Text.Trim(), out var n) && n > 1
            ? "Each extra copy gets its own box numbers when you print."
            : "";
    }

    private void OnPrint(object sender, RoutedEventArgs e) => PrintNow();

    private async void PrintNow()
    {
        if (_claiming) return;
        if (Printers.SelectedItem is not string printerName) return;
        if (!int.TryParse(Copies.Text.Trim(), out var copies) || copies is < 1 or > 99)
        {
            PrintNote.Text = "Copies must be 1 to 99.";
            return;
        }
        var job = _doc;
        var ticketCopies = copies;
        if (_extraCopies is not null && copies > 1)
        {
            // Kept per copy count: a retry after a failed spool reuses the
            // numbers already claimed instead of claiming more.
            if (!_withCopies.TryGetValue(copies, out var numbered))
            {
                _claiming = true;
                PrintButton.IsEnabled = false;
                PrintNote.Text = "Claiming box numbers for the extra copies…";
                try { numbered = await _extraCopies(copies - 1); }
                catch (Exception ex)
                {
                    _warn("Numbering the extra copies failed: " + ex.Message);
                    numbered = null;
                }
                finally
                {
                    _claiming = false;
                    PrintButton.IsEnabled = true;
                }
                if (numbered is null)
                {
                    PrintNote.Text = "The extra copies couldn't be numbered, so nothing was printed.";
                    return;
                }
                _withCopies[copies] = numbered;
                PrintNote.Text = "";
            }
            job = numbered;
            ticketCopies = 1;
        }
        try
        {
            using var server = new LocalPrintServer();
            var queue = new PrintQueue(server, printerName);
            var dlg = new PrintDialog { PrintQueue = queue };
            dlg.PrintTicket.CopyCount = ticketCopies;
            // no ShowDialog: this window already chose everything — spool it
            dlg.PrintDocument(job.DocumentPaginator, _jobName);
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

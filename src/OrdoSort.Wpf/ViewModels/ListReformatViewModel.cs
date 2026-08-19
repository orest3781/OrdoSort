using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;

namespace OrdoSort.Wpf.ViewModels;

/// <summary>Paste a copied spreadsheet column (or row), see it back as a
/// comma-delimited line, one item per line, or joined by any delimiter you
/// like — gaps closed and counted — and copy it with one click. Deliberately
/// the simplest VM on the branch: ListReformat.Reformat is pure string work over
/// whatever's already in memory — no filesystem, no probe generation to
/// race, nothing that could ever be slow enough to need debouncing off the
/// UI thread the way FilenameListViewModel's listing or PageCountsViewModel's
/// counting do — so every setter just recomputes synchronously and inline.</summary>
public sealed class ListReformatViewModel : ObservableObject
{
    private string _inputText = "";
    public string InputText
    {
        get => _inputText;
        set { if (Set(ref _inputText, value)) Recompute(); }
    }

    private bool _quote;
    public bool Quote
    {
        get => _quote;
        set { if (Set(ref _quote, value)) Recompute(); }
    }

    private bool _spaceAfterComma;
    public bool SpaceAfterComma
    {
        get => _spaceAfterComma;
        set { if (Set(ref _spaceAfterComma, value)) Recompute(); }
    }

    private bool _dedupe;
    public bool Dedupe
    {
        get => _dedupe;
        set { if (Set(ref _dedupe, value)) Recompute(); }
    }

    /// <summary>What the window's shape picker binds to, by SelectedValuePath
    /// "Key" over <see cref="ShapeChoices"/> — the KeyValuePair pattern
    /// SettingsViewModel.SortChoices already uses, so the ComboBox needs no
    /// enum converter and the labels stay in the view model where a test can
    /// see them.</summary>
    private ListReformat.OutputShape _shape = ListReformat.OutputShape.CommaLine;
    public ListReformat.OutputShape Shape
    {
        get => _shape;
        set
        {
            if (!Set(ref _shape, value)) return;
            Raise(nameof(IsCustomDelimiter));
            Recompute();
        }
    }

    /// <summary>Starts at ";" rather than empty: an empty delimiter runs the
    /// items together, which should be something the user typed on purpose,
    /// not what they get for picking the shape.</summary>
    private string _customDelimiter = ";";
    public string CustomDelimiter
    {
        get => _customDelimiter;
        set { if (Set(ref _customDelimiter, value)) Recompute(); }
    }

    /// <summary>IsEnabled for the delimiter box — dead weight under either of
    /// the other two shapes.</summary>
    public bool IsCustomDelimiter => Shape == ListReformat.OutputShape.CustomDelimiter;

    public static readonly KeyValuePair<ListReformat.OutputShape, string>[] ShapeChoices =
    {
        new(ListReformat.OutputShape.CommaLine, "Comma-delimited line"),
        new(ListReformat.OutputShape.OnePerLine, "One item per line"),
        new(ListReformat.OutputShape.CustomDelimiter, "Custom delimiter"),
    };

    private string _outputText = "";
    public string OutputText { get => _outputText; private set => Set(ref _outputText, value); }

    private string _countsLine = "";
    public string CountsLine { get => _countsLine; private set => Set(ref _countsLine, value); }

    private string _status = "";
    public string Status { get => _status; private set => Set(ref _status, value); }

    public RelayCommand ClearCommand { get; }

    public ListReformatViewModel()
    {
        ClearCommand = new RelayCommand(() => InputText = "");
    }

    private void Recompute()
    {
        var result = ListReformat.Reformat(InputText,
            new ListReformat.Options(Quote, SpaceAfterComma, Dedupe, Shape, CustomDelimiter));
        OutputText = result.Text;
        CountsLine = FormatCounts(result);
    }

    private static string FormatCounts(ListReformat.Result result)
    {
        if (result.Items == 0) return "";
        var line = $"{result.Items} item{(result.Items == 1 ? "" : "s")}";
        // Blanks before duplicates: that is the order the two happen in, and
        // the blank count is the one the user came here for.
        if (result.BlanksRemoved > 0)
            line += $" · {result.BlanksRemoved} blank row{(result.BlanksRemoved == 1 ? "" : "s")} removed";
        if (result.DuplicatesDropped > 0)
            line += $" · {result.DuplicatesDropped} duplicate{(result.DuplicatesDropped == 1 ? "" : "s")} dropped";
        return line;
    }

    /// <summary>Set by the window's code-behind after Clipboard.SetText
    /// succeeds — Clipboard itself is a WPF/COM type and must never appear
    /// in this class (it isn't safe to touch from the headless MTA tests run
    /// under). <paramref name="converted"/> distinguishes the two buttons
    /// that can trigger this: Paste &amp; copy just overwrote InputText from
    /// the clipboard and produced a fresh OutputText before copying it
    /// (true, "Converted and copied"), while Copy result only copied
    /// whatever OutputText already was (false, "Copied").</summary>
    public void NoteCopied(bool converted) => Status = converted ? "Converted and copied" : "Copied";

    /// <summary>Set by the window's code-behind when Clipboard.SetText
    /// throws COMException — the clipboard is a shared, single-owner OS
    /// resource another app can be holding for a moment; this just says so
    /// rather than losing the failure silently.</summary>
    public void NoteClipboardBusy() => Status = "Clipboard busy — try again";

    /// <summary>Set by the window's code-behind instead of calling
    /// Clipboard.SetText at all when OutputText is empty (nothing was pasted,
    /// or the pasted cells were all blank) — SetText throws on an empty
    /// string, and there is nothing useful to put on the clipboard either
    /// way, so this says so rather than silently doing nothing.</summary>
    public void NoteNothingToCopy() => Status = "nothing to copy";
}

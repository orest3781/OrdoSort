using System.Windows.Controls;

namespace OrdoSort.Wpf.Views;

/// <summary>Caps a set of DataGridColumns' <c>MaxWidth</c> to a share of a
/// reference width — a window's own declared <c>Width</c> for a grid that
/// fills the window (MatchMergeWindow, BulkRenameWindow, HistoryWindow), or a
/// fixed side panel's declared width for one that doesn't (TriageWindow's
/// Candidates grid lives in a 440px column, not across the whole 1150px
/// window). Owner's decision: cap content-sized columns then ellipsis,
/// expressed as a share of the window rather than a magic pixel constant, so
/// the cap scales with however wide a given window is designed to be instead
/// of needing a hunted-down pixel number updated by hand if that ever
/// changes.
///
/// A <see cref="DataGridColumn"/> is not part of the visual or logical tree —
/// it hangs off <c>DataGrid.Columns</c>, not <c>Window.Content</c> — so XAML
/// cannot bind its <c>MaxWidth</c> with a RelativeSource or ElementName the
/// way an ordinary FrameworkElement could; there is no NameScope path to
/// reach it that way. This is the code-behind equivalent of that binding:
/// called once per window, right after <c>InitializeComponent()</c>, using a
/// width that's available synchronously and doesn't depend on layout —
/// <c>Window.Width</c>/<c>ColumnDefinition.Width</c> are plain declared
/// values the instant the constructor runs, unlike <c>ActualWidth</c>, which
/// stays 0 until the first measure/arrange pass completes.</summary>
internal static class DataGridColumnCap
{
    public static void Apply(double referenceWidth, double share, params DataGridColumn[] columns)
    {
        var cap = referenceWidth * share;
        foreach (var column in columns) column.MaxWidth = cap;
    }
}

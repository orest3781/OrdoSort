namespace OrdoSort.Wpf.Services;

/// <summary>The PDF pane, as the view model sees it. The real implementation
/// wraps WebView2; tests substitute a recorder. The one load-bearing member is
/// <see cref="ReleaseAsync"/> — Edge must let go of the file handle BEFORE the
/// commit moves the file.</summary>
public interface IPdfViewer
{
    /// <summary>Display a PDF. No-op when the viewer never initialized.
    /// With the first page's <paramref name="page"/> size the whole page is
    /// shown as large as the pane allows; without it, the engine's own zoom.</summary>
    Task ShowAsync(string path, OrdoSort.Core.PageSize? page = null);

    /// <summary>Navigate away and wait until the engine has actually released
    /// the current document's file handle (bounded by a 2 s fallback).</summary>
    Task ReleaseAsync();

    /// <summary>Show nothing (Ready/Done screens).</summary>
    void Blank();

    /// <summary>Raised once, with a message for the user, when the engine
    /// has gone and the pane can't show documents any more. From then on the
    /// other members do nothing, so filing carries on without a preview.</summary>
    event Action<string>? Stopped;
}

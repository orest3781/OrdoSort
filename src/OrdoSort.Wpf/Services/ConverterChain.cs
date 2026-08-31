using OrdoSort.Core;

namespace OrdoSort.Wpf.Services;

/// <summary>Tries each <see cref="IDocumentConverter"/> in the order it was
/// built with and commits to the first one that claims the extension — the
/// no-silent-downgrade rule. <see cref="Handles"/> is true when ANY link
/// handles the extension; <see cref="ToPdf"/> asks the FIRST link whose own
/// <see cref="IDocumentConverter.Handles"/> says yes and returns exactly
/// what it returns, success or failure. A link is skipped only when it does
/// not recognize the extension AT ALL — never because it tried and failed.
///
/// Why this matters: once <see cref="OfficeConverter"/> claims a type and
/// fails on a specific document (corrupt file, a COM error, whatever), that
/// failure has to stand. Quietly falling through to <see cref="TableToPdf"/>
/// or <see cref="TextToPdf"/> would hand back a PLAUSIBLE-LOOKING PDF for a
/// document the user believes converted properly through Office — a lesser,
/// silently-substituted rendering is worse than a clear, honest failure the
/// user can act on (retry, open the file directly, fix whatever is wrong
/// with it).
///
/// This is also exactly why <see cref="OfficeConverter.Handles"/> deliberately
/// does NOT claim ".csv"/".tsv", even though <see cref="MergeTypes"/> files
/// them under its own Excel group: TableToPdf already converts both
/// deterministically, without an Office cold start, so they fall through to
/// it BY DESIGN — Office was never asked and never failed, it simply never
/// claimed the type, which is the one condition that legitimately reaches
/// the next link.
///
/// Production order (built by <see cref="MergePdfsViewModel"/>):
/// <see cref="OfficeConverter"/> first (best fidelity when installed), then
/// <see cref="ImageToPdf"/>, <see cref="TableToPdf"/>, <see cref="TextToPdf"/>
/// — three converters that need nothing installed and can never fail for a
/// reason Office could have fixed.
///
/// <see cref="IDisposable"/> so whoever holds this chain (a merge window's
/// view model, for the length of a session) has one place to dispose
/// whichever of its links actually need it — today, only
/// <see cref="OfficeConverter"/> does (its own Quit/kill of a session it
/// started, and the flag restoration on a session it borrowed) — without
/// having to know which link that is or how many of them there are.</summary>
public sealed class ConverterChain : IDocumentConverter, IDisposable
{
    private readonly IReadOnlyList<IDocumentConverter> _links;

    public ConverterChain(params IDocumentConverter[] links) : this((IReadOnlyList<IDocumentConverter>)links) { }

    public ConverterChain(IReadOnlyList<IDocumentConverter> links) => _links = links;

    public bool Handles(string extension) => _links.Any(link => link.Handles(extension));

    /// <summary>The extension is read from <paramref name="displayName"/> —
    /// the same spelling every converter and <see cref="PdfMerge"/> itself
    /// use (dot-less, lowercase) — so this class never has to be told the
    /// extension a second time by a caller that already computed it once.
    /// The "no link handles this at all" branch is defence in depth, not the
    /// normal path: <see cref="PdfMerge.AsPdfBytes"/> already checks
    /// <see cref="Handles"/> before ever calling this, so a real merge never
    /// reaches it — but a caller that skips that check (a direct test, a
    /// future one) still gets an honest, non-throwing answer rather than an
    /// IndexOutOfRange-shaped surprise.</summary>
    public ConversionResult ToPdf(byte[] source, string displayName,
        IReadOnlyList<string> candidates, Func<PasswordRequest, string?>? ask)
    {
        var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
        foreach (var link in _links)
            if (link.Handles(extension)) return link.ToPdf(source, displayName, candidates, ask);
        return new("unsupported", null, $"{displayName} isn't a type this PC can convert");
    }

    /// <summary>Restoration warnings from every <see cref="OfficeConverter"/>
    /// link this chain wraps (today, at most one) — see
    /// <see cref="OfficeConverter.RestorationWarnings"/> for what these mean
    /// and why the list is empty until that link has been disposed. Checked
    /// by type rather than through a bespoke marker interface on
    /// <see cref="IDocumentConverter"/> itself: nothing else in this feature
    /// has anything analogous to report, so adding a member every converter
    /// would have to implement — for exactly one real implementer — would be
    /// the wrong trade.</summary>
    public IReadOnlyList<string> RestorationWarnings =>
        _links.OfType<OfficeConverter>().SelectMany(office => office.RestorationWarnings).ToList();

    /// <summary>Disposes every link that needs it. The other three
    /// converters this feature ships (<see cref="ImageToPdf"/>,
    /// <see cref="TableToPdf"/>, <see cref="TextToPdf"/>) hold no unmanaged
    /// resource and are not <see cref="IDisposable"/>, so in practice this
    /// only ever touches an <see cref="OfficeConverter"/> link — and does so
    /// safely even when nothing was ever converted, since
    /// <see cref="OfficeConverter.Dispose"/> degenerates to a handful of null
    /// checks in that case.</summary>
    public void Dispose()
    {
        foreach (var link in _links.OfType<IDisposable>()) link.Dispose();
    }
}

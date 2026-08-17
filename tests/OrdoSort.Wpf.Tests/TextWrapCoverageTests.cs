using System.Xml;
using System.Xml.Linq;

namespace OrdoSort.Wpf.Tests;

/// <summary>2026-08-16, spacing/wrapping audit: UnlockWindow's caption under
/// the password row ("Every saved password is tried automatically…") declared
/// <c>TextWrapping="Wrap"</c> and STILL ran off screen — it sat inside a
/// <c>&lt;StackPanel Orientation="Horizontal"&gt;</c>, and a horizontal
/// StackPanel measures every child at infinite width, so the wrap never
/// engages. The attribute looked right in the diff and did nothing, which is
/// exactly the failure mode FocusRingCoverageTests exists to guard against in
/// its own domain.
///
/// Two source-level facts, checked over every window/view XAML file off disk
/// (same repo-root walk as DataGridWindowCoverageTests; Theme\*.xaml is out of
/// scope — its TextBlocks live inside control templates whose width is the
/// templated parent's, not a prose surface):
///
/// 1. A TextBlock that declares Wrap must not have an ancestor that measures
///    it at infinite width — a horizontal StackPanel, a WrapPanel (horizontal
///    by default), or a ScrollViewer with horizontal scrolling enabled —
///    unless something between them (the TextBlock included) pins Width or
///    MaxWidth. Declaring Wrap is the author saying "this is prose"; an
///    infinite-width ancestor silently vetoes that.
///
/// 2. A TextBlock whose literal Text is sentence-length (60+ chars) must
///    declare TextWrapping or TextTrimming. Sixty characters outgrows a
///    narrow window; without wrap the text clips or overflows, and without
///    trimming it does so invisibly. Bound Text can't be judged from source
///    and is left to the live-tree suites.
///
/// A DockPanel's Left/Right-docked children are also measured at infinity,
/// but every current prose TextBlock in a DockPanel is the fill child or
/// carries TextTrimming, so that case is deliberately not linted here —
/// adding it would mean modelling attached-property semantics for zero
/// current signal.</summary>
public class TextWrapCoverageTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException(
            "couldn't find OrdoSort.sln walking up from " + AppContext.BaseDirectory +
            " — this suite reads window XAML source directly off disk and needs the repo root.");
    }

    private static IEnumerable<string> ProseSurfaceXamlFiles()
    {
        var wpf = Path.Combine(FindRepoRoot(), "src", "OrdoSort.Wpf");
        yield return Path.Combine(wpf, "MainWindow.xaml");
        foreach (var f in Directory.EnumerateFiles(Path.Combine(wpf, "Views"), "*.xaml")) yield return f;
        foreach (var f in Directory.EnumerateFiles(Path.Combine(wpf, "Windows"), "*.xaml")) yield return f;
    }

    private static bool HasWidthPin(XElement e) =>
        e.Attribute("Width") is not null || e.Attribute("MaxWidth") is not null;

    private static bool MeasuresChildrenAtInfiniteWidth(XElement e) => e.Name.LocalName switch
    {
        "StackPanel" => (string?)e.Attribute("Orientation") == "Horizontal",
        "WrapPanel" => (string?)e.Attribute("Orientation") != "Vertical",
        "ScrollViewer" => (string?)e.Attribute("HorizontalScrollBarVisibility")
                              is "Auto" or "Visible",
        _ => false,
    };

    private static bool InsideToolTip(XElement e) =>
        e.Ancestors().Any(a => a.Name.LocalName is "ToolTip" ||
                               a.Name.LocalName.EndsWith(".ToolTip", StringComparison.Ordinal));

    private static string Where(string file, XElement e) =>
        Path.GetFileName(file) + ":" + ((IXmlLineInfo)e).LineNumber;

    /// <summary>Style keys that hand the TextBlock its wrapping/trimming via a
    /// Setter (following BasedOn chains) — e.g. EmptyStateText. An attribute-only
    /// lint would false-positive every call site whose redundant inline
    /// TextWrapping was correctly stripped when the shared style took it over.</summary>
    private static HashSet<string> WrapCarryingStyleKeys(params XDocument[] docs)
    {
        var xKey = XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml");
        var styles = docs.SelectMany(d => d.Descendants())
            .Where(e => e.Name.LocalName == "Style" && e.Attribute(xKey) is not null)
            .ToDictionary(e => (string)e.Attribute(xKey)!, e => e);
        bool Carries(XElement style) =>
            style.Elements().Any(s => s.Name.LocalName == "Setter" &&
                ((string?)s.Attribute("Property") is "TextWrapping" or "TextTrimming"))
            || ((string?)style.Attribute("BasedOn") is { } b &&
                ExtractKey(b) is { } baseKey && styles.TryGetValue(baseKey, out var parent) &&
                Carries(parent));
        return styles.Where(kv => Carries(kv.Value)).Select(kv => kv.Key).ToHashSet();
    }

    private static string? ExtractKey(string resourceRef)
    {
        var i = resourceRef.IndexOf("Resource ", StringComparison.Ordinal);
        return i < 0 ? null : resourceRef[(i + "Resource ".Length)..].TrimEnd('}', ' ');
    }

    private static bool StyleHandsItWrapOrTrim(XElement tb, HashSet<string> wrapStyles) =>
        (string?)tb.Attribute("Style") is { } s && ExtractKey(s) is { } key &&
        wrapStyles.Contains(key);

    [Fact]
    public void WrapDeclaringTextBlocksAreNotMeasuredAtInfiniteWidth()
    {
        var offenders = new List<string>();
        var shared = XDocument.Load(
            Path.Combine(FindRepoRoot(), "src", "OrdoSort.Wpf", "Theme", "Styles.xaml"));
        foreach (var file in ProseSurfaceXamlFiles())
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            var wrapStyles = WrapCarryingStyleKeys(shared, doc);
            foreach (var tb in doc.Descendants().Where(d => d.Name.LocalName == "TextBlock"))
            {
                // wrap OR trim, inline OR via style — both need a finite width
                // to engage, so both are silently vetoed the same way (the two
                // Settings "folder doesn't exist" rows shipped this via
                // StatusText's style-carried Wrap)
                var declares = (string?)tb.Attribute("TextWrapping") == "Wrap"
                    || tb.Attribute("TextTrimming") is not null
                    || StyleHandsItWrapOrTrim(tb, wrapStyles);
                if (!declares) continue;
                if (HasWidthPin(tb)) continue;

                // walk outward until something pins width; an infinite-width
                // measurer seen before any pin defeats the declared wrap
                foreach (var anc in tb.Ancestors())
                {
                    if (MeasuresChildrenAtInfiniteWidth(anc))
                    {
                        offenders.Add(Where(file, tb) + " (inside " + anc.Name.LocalName + ")");
                        break;
                    }
                    if (HasWidthPin(anc)) break;
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "TextBlock declares wrapping/trimming (inline or via style) but an ancestor " +
            "measures it at infinite width, so it never engages and the text runs off " +
            "screen:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void SentenceLengthLiteralsDeclareWrapOrTrim()
    {
        var offenders = new List<string>();
        var shared = XDocument.Load(
            Path.Combine(FindRepoRoot(), "src", "OrdoSort.Wpf", "Theme", "Styles.xaml"));
        foreach (var file in ProseSurfaceXamlFiles())
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            var wrapStyles = WrapCarryingStyleKeys(shared, doc);
            foreach (var tb in doc.Descendants().Where(d => d.Name.LocalName == "TextBlock"))
            {
                var text = (string?)tb.Attribute("Text");
                if (text is null || text.Length < 60 || text.StartsWith("{", StringComparison.Ordinal))
                    continue;
                if (tb.Attribute("TextWrapping") is not null || tb.Attribute("TextTrimming") is not null)
                    continue;
                if (StyleHandsItWrapOrTrim(tb, wrapStyles)) continue;
                if (InsideToolTip(tb)) continue;   // tooltips size to content
                offenders.Add(Where(file, tb) + " \"" + text[..40] + "…\"");
            }
        }
        Assert.True(offenders.Count == 0,
            "sentence-length literal Text without TextWrapping or TextTrimming — " +
            "clips or overflows in a narrow window:\n  " + string.Join("\n  ", offenders));
    }
}

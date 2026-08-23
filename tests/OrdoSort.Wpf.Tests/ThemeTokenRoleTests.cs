using System.Xml;
using System.Xml.Linq;

namespace OrdoSort.Wpf.Tests;

/// <summary>Some theme tokens are BACKGROUND colours and some are FOREGROUND
/// colours, and the palette pairs them deliberately: <c>Warning</c> carries
/// <c>WarningText</c>, <c>Danger</c> carries <c>DangerText</c>. Two of those
/// background tokens are also the obvious-sounding name for a status colour —
/// "Success", "Danger" — and both have been reached for as TEXT, where they
/// sit on WindowBg/Surface instead of their own paired background and fail
/// WCAG AA in the dark schemes.
///
/// That has now happened twice, on two different tokens, and each time the fix
/// was a new foreground-role token rather than a change to the offender:
/// <c>StatusGreen</c> exists because <c>Success</c> as text measures 2.85:1 -
/// 3.71:1 across the dark schemes, and <c>StatusRed</c> exists because
/// <c>Danger</c> as text measures 2.69:1 against Dark.Surface. Both are
/// recorded in ThemePalette.cs's own field comments, and
/// <see cref="ThemeTests.EveryTextPairingMeetsWcagAa"/> enforces the
/// REPLACEMENTS.
///
/// What it could not enforce is the offender's absence. <c>TextPairs()</c> is a
/// list of pairings that ARE shipped; a token used somewhere it shouldn't be
/// simply isn't in that list, so the wall it builds has a hole in exactly the
/// shape of this bug. Both migrations were done by hand, and the 2026-08-22 UI
/// audit found that the Success one had missed two call sites which shipped
/// that way for months (UI-01).
///
/// So this is the complementary check, and it reads source rather than the
/// live tree on purpose: the offending XAML is the artifact, and a call site
/// that is never constructed by any other suite is still caught.
///
/// Scope is FOREGROUND only. <c>Fill</c>/<c>Stroke</c>/<c>Background</c> are
/// legitimate uses of these tokens — DoneView's completion dot is a
/// <c>Success</c>-filled Ellipse, a graphical object under WCAG 1.4.11 where
/// the floor is 3:1, which it clears. Text is the case that fails, and text is
/// what this lints.</summary>
public class ThemeTokenRoleTests
{
    /// <summary>Background-role tokens and the foreground-role token that
    /// exists to replace each one. The message names the replacement, because
    /// "don't use this" without "use that instead" is how the first migration
    /// left two sites behind.</summary>
    private static readonly (string Token, string UseInstead, string Why)[] NotForeground =
    {
        ("Theme.Success", "Theme.StatusGreen",
            "Success is 46,125,50 in every dark scheme and measures 2.85:1-3.71:1 as text there"),
        ("Theme.Danger", "Theme.StatusRed",
            "Danger's shipped job is a BACKGROUND paired with DangerText; as text it measures 2.69:1 on Dark.Surface"),
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException(
            "couldn't find OrdoSort.sln walking up from " + AppContext.BaseDirectory +
            " — this suite reads XAML source directly off disk and needs the repo root.");
    }

    /// <summary>Every XAML the app ships, Theme\ included — unlike
    /// TextWrapCoverageTests, control templates are IN scope here: a Foreground
    /// Setter inside a template paints real text just as a call site does.</summary>
    private static IEnumerable<string> AllShippedXaml()
    {
        var wpf = Path.Combine(FindRepoRoot(), "src", "OrdoSort.Wpf");
        return Directory.EnumerateFiles(wpf, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin"));
    }

    private static string Where(string file, XObject e) =>
        Path.GetFileName(file) + ":" + ((IXmlLineInfo)e).LineNumber;

    /// <summary>A property name that paints text. Covers the attached/dotted
    /// forms (<c>TextElement.Foreground</c>) as well as the plain one.</summary>
    private static bool IsForegroundProperty(string name) =>
        name == "Foreground" || name.EndsWith(".Foreground", StringComparison.Ordinal);

    /// <summary>Both spellings a Foreground can take in XAML: an attribute on
    /// the element itself, and a Setter's Property/Value pair (the form every
    /// Style and ControlTemplate trigger in Theme\Styles.xaml uses).</summary>
    private static IEnumerable<(XObject At, string Value)> ForegroundAssignments(XDocument doc)
    {
        foreach (var e in doc.Descendants())
        {
            foreach (var a in e.Attributes())
                if (IsForegroundProperty(a.Name.LocalName))
                    yield return (a, a.Value);

            if (e.Name.LocalName != "Setter") continue;
            var prop = (string?)e.Attribute("Property");
            if (prop is null || !IsForegroundProperty(prop)) continue;
            var value = (string?)e.Attribute("Value");
            if (value is not null) yield return (e, value);
        }
    }

    [Fact]
    public void NoBackgroundRoleTokenIsUsedAsAForeground()
    {
        var offenders = new List<string>();

        foreach (var file in AllShippedXaml())
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var (at, value) in ForegroundAssignments(doc))
            {
                foreach (var (token, useInstead, why) in NotForeground)
                {
                    // "Theme.Success" must not also match "Theme.SuccessSomething"
                    var hit = value.Contains(token, StringComparison.Ordinal) &&
                              !value.Contains(token + "Text", StringComparison.Ordinal);
                    if (!hit) continue;
                    offenders.Add(
                        $"{Where(file, at)}: Foreground={value.Trim()} — use {useInstead} instead ({why})");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A background-role theme token is painting text. Each of these fails WCAG AA " +
            "4.5:1 in at least one dark scheme, and the replacement token exists precisely " +
            "for this position:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>The lint above is only worth having if the tokens it names are
    /// really unusable as text — otherwise it is a style rule dressed up as a
    /// contract. This measures that directly, so the day someone lightens
    /// Success in the dark palette this test fails and tells them the lint can
    /// go, rather than leaving a rule nobody can re-justify.</summary>
    [Fact]
    public void TheBannedTokensReallyDoFailAsTextInAtLeastOneDarkScheme()
    {
        foreach (var (token, _, _) in NotForeground)
        {
            var field = token["Theme.".Length..];
            var worst = double.MaxValue;
            string? worstWhere = null;

            foreach (var scheme in Theme.ThemePalette.Schemes.Where(s => s.IsDark))
            {
                var fg = (Theme.Rgb)typeof(Theme.ThemePalette).GetProperty(field)!
                    .GetValue(scheme.Palette)!;
                foreach (var (bgName, bg) in new[]
                         {
                             ("WindowBg", scheme.Palette.WindowBg),
                             ("Surface", scheme.Palette.Surface),
                         })
                {
                    var ratio = Theme.ThemePalette.ContrastRatio(fg, bg);
                    if (ratio >= worst) continue;
                    worst = ratio;
                    worstWhere = $"{scheme.Key}/{bgName}";
                }
            }

            Assert.True(worst < 4.5,
                $"{token} now measures {worst:F2}:1 at its worst ({worstWhere}) in the dark " +
                "schemes, which clears WCAG AA. It is no longer a background-only token — " +
                "drop it from NotForeground above, or this lint is banning something safe.");
        }
    }
}

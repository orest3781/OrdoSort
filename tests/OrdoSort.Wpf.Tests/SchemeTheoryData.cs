using OrdoSort.Wpf.Theme;

namespace OrdoSort.Wpf.Tests;

/// <summary>The ONE shared MemberData source every RENDERED contrast-bucket
/// theory suite migrated off bool-dark uses (rendered-contrast-per-scheme
/// migration, 2026-08-09): HighlightContrastTests, DataGridSelectionContrastTests,
/// DataGridNoteColourTests, CaptionSizingTests, BulkRenameDeleteSegLastLabelTests,
/// CopyAndTerminologyTests (its three contrast theories only), and
/// ContentTemplateSetterTests (ClosedComboBoxStillShowsTheSelectedItemLegibly
/// only) — see each file's own doc comment for why it's in this set and
/// <see cref="ThemeTests.TextPairs"/> for the PURE-MATH wall this suite is the
/// RENDERED-brush counterpart of.
///
/// Yields each <see cref="ThemePalette.Schemes"/> KEY (a plain string), never
/// a <see cref="ThemeScheme"/> object: xUnit can serialize/display a string
/// theory parameter (so the test explorer shows and can re-run "paper",
/// "graphite", "ledger", … individually) but cannot do the same for an
/// arbitrary record, which would collapse every case into one indistinguishable
/// "MemberDataRow" entry. Callers resolve the key back to a scheme with
/// <c>ThemePalette.FindScheme(schemeKey)!</c>.
///
/// Adding scheme #8 to the registry grows every theory that references this
/// class automatically, without touching this file or any consumer — the
/// entire point of moving off the old bool-dark pair (which only ever
/// covered paper/graphite, the two schemes that happened to exist when these
/// suites were first written).</summary>
public static class SchemeTheoryData
{
    public static TheoryData<string> SchemeKeys
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var s in ThemePalette.Schemes) data.Add(s.Key);
            return data;
        }
    }
}

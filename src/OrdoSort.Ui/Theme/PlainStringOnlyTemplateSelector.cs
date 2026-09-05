using System.Windows;
using System.Windows.Controls;

namespace OrdoSort.Wpf.Theme;

/// <summary>Applies a template to plain-<see cref="string"/> content ONLY, and
/// deliberately returns null for everything else so WPF's own template
/// resolution carries on as if nothing had been set.
///
/// Why this exists (Task 8b, 2026-08-03; extended to ComboBoxItem in that
/// task's fix round 1 the same day — both implicit styles in Styles.xaml now
/// use one instance each of this class). Those styles need a themed template
/// for the one content shape WPF gets wrong on its own: a plain-string Content
/// is auto-wrapped into a TextBlock that resolves the APPLICATION-level
/// implicit TextBlock style (Theme.Text) instead of inheriting the container's
/// selected Theme.AccentText — a Style Setter outranking property-value
/// inheritance. But expressing that as a blanket <c>ContentTemplate</c> Setter
/// silently broke every list that renders its rows through implicit
/// <c>DataType</c> DataTemplates instead of an ItemTemplate:
/// <see cref="ContentPresenter"/> only performs that DataType-keyed lookup when
/// its ContentTemplate is NULL, so the Setter suppressed it. SettingsWindow's
/// WatchList was exactly that shape and rendered six rows of
/// "OrdoSort.Wpf.ViewModels.WatchSectionVm" for a day.
///
/// A selector is the narrowest fix because of the ORDER
/// <c>ContentPresenter.ChooseTemplate</c> tries things in: ContentTemplate
/// first, then ContentTemplateSelector, and only if BOTH come back null its
/// own built-in default selector — which is what does the implicit DataType
/// lookup (and the string/UIElement/XmlNode auto-wraps). Returning null here
/// therefore leaves that fallthrough completely intact, so all four other
/// shapes behave exactly as they would with no Setter at all: a
/// DataType-templated row, a UIElement row, a row whose list supplies an
/// ItemTemplate, and a row whose list supplies an ItemTemplateSelector.
///
/// That last one is the sharpest illustration that this is a template
/// RESOLUTION rule and NOT a DependencyProperty-precedence rule. An
/// ItemTemplate wins on precedence — ItemsControl assigns it to the container's
/// ContentTemplate as a local value, which ChooseTemplate then finds at step 1.
/// An ItemTemplateSelector is assigned the same way, as a LOCAL
/// ContentTemplateSelector, and equally outranks a Style Setter in
/// precedence — yet under a blanket ContentTemplate Setter it was ignored
/// outright, because step 1 was already satisfied by the Style. Precedence
/// decides what each property HOLDS; ChooseTemplate decides which property is
/// CONSULTED, and it never got that far.
///
/// One narrowing is deliberate and worth knowing: content that is neither a
/// string, nor DataType-templated, nor a UIElement now falls to WPF's own
/// DefaultTemplate, whose generated TextBlock resolves the app-level implicit
/// TextBlock style (Theme.Text) and so does NOT get AccentText on selection the
/// way the old blanket Setter's template did. Call sites that draw their own
/// labels carry the local Foreground binding themselves (WatchList's two
/// DataType templates do); a bare unhandled object would be legible but
/// off-palette when selected.
///
/// ONE ASYMMETRY BETWEEN THE TWO STYLES THIS SERVES, and the reason it is
/// spelled out here rather than left to be rediscovered a third time
/// (Task 8b fix round 2, 2026-08-03). A ListBoxItem's template is only ever
/// used to paint a row. A ComboBoxItem's is also, indirectly, what paints the
/// CLOSED ComboBox: <c>ComboBox.UpdateSelectionBoxItem</c> reads
/// <c>InternalSelectedItem</c> — the ITEM, never the generated container — and
/// if that item is itself a <see cref="ContentControl"/> (which
/// <c>&lt;ComboBoxItem Content="…"/&gt;</c> children are) it unwraps to
/// <c>item.Content</c> and takes <c>item.ContentTemplate</c>; otherwise it
/// takes the ComboBox's own <c>ItemTemplate</c>. That copy lands in
/// <c>SelectionBoxItemTemplate</c>, and there is no
/// SelectionBoxItemTemplateSelector, so THIS class is never consulted out
/// there. Which is exactly as well: a template whose Foreground is a
/// FindAncestor on ComboBoxItem cannot resolve on the selection box — no
/// ComboBoxItem is above it — and an unresolvable FindAncestor leaves the DP
/// at its DEFAULT, which for TextBlock.Foreground is BLACK, not the inherited
/// or styled value. Converting the ComboBoxItem style to this selector
/// therefore repaired a live 1.44:1 dark-mode face rather than risking one.
/// Any template that a ComboBox may use as its <c>ItemTemplate</c> has to
/// survive both hosts — see KvpValueTemplate/FontChoiceTemplate in
/// Styles.xaml, which do it with a PriorityBinding over
/// ComboBoxItem-then-ComboBox. Asserted, not asserted-in-prose, by
/// ContentTemplateSetterTests.SelectionBoxItemTemplateComesFromTheSelectedItemNeverFromItsContainer
/// and ClosedComboBoxStillShowsTheSelectedItemLegibly.</summary>
public sealed class PlainStringOnlyTemplateSelector : DataTemplateSelector
{
    /// <summary>The template to use when — and only when — the content is a
    /// string. Left settable so the template itself stays declared in
    /// Styles.xaml as an ordinary keyed resource the test suite can resolve by
    /// key, rather than being built in code.</summary>
    public DataTemplate? PlainStringTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
        item is string ? PlainStringTemplate : null;
}

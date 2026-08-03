using System.Windows;
using System.Windows.Controls;

namespace OrdoSort.Wpf.Theme;

/// <summary>Applies a template to plain-<see cref="string"/> content ONLY, and
/// deliberately returns null for everything else so WPF's own template
/// resolution carries on as if nothing had been set.
///
/// Why this exists (Task 8b, 2026-08-03). Styles.xaml's implicit ListBoxItem
/// style needs a themed template for the one list shape WPF gets wrong on its
/// own: a plain-string Content is auto-wrapped into a TextBlock that resolves
/// the APPLICATION-level implicit TextBlock style (Theme.Text) instead of
/// inheriting the container's selected Theme.AccentText — a Style Setter
/// outranking property-value inheritance. But expressing that as a blanket
/// <c>ContentTemplate</c> Setter silently broke every list that renders its
/// rows through implicit <c>DataType</c> DataTemplates instead of an
/// ItemTemplate: <see cref="ContentPresenter"/> only performs that
/// DataType-keyed lookup when its ContentTemplate is NULL, so the Setter
/// suppressed it. SettingsWindow's WatchList was exactly that shape and
/// rendered six rows of "OrdoSort.Wpf.ViewModels.WatchSectionVm" for a day.
///
/// A selector is the narrowest fix because of the ORDER
/// <c>ContentPresenter.ChooseTemplate</c> tries things in: ContentTemplate
/// first, then ContentTemplateSelector, and only if BOTH come back null its
/// own built-in default selector — which is what does the implicit DataType
/// lookup (and the string/UIElement/XmlNode auto-wraps). Returning null here
/// therefore leaves that fallthrough completely intact, so a DataType-templated
/// row, a UIElement row, and a row whose list supplies an ItemTemplate all
/// behave exactly as they would with no Setter at all. This is a
/// template-RESOLUTION rule, not a DependencyProperty-precedence rule: an
/// ItemTemplate still wins by being a LOCAL ContentTemplate, which
/// ChooseTemplate consults before ever reaching this selector.</summary>
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

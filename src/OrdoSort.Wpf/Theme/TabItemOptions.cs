using System.Windows;

namespace OrdoSort.Wpf.Theme;

/// <summary>Attached property that toggles the shared TabItem
/// ControlTemplate's (Theme/Styles.xaml) optional selected-state underline
/// Border. False (the default) for every plain TabItem in the app — the
/// underline stays Collapsed and that template renders exactly as it always
/// has. SettingsWindow.xaml's "SectionTab" style sets this True instead of
/// carrying its own full ControlTemplate copy: a template trigger in the
/// shared template reads it, so one shared visual tree serves both the
/// plain rail look and SettingsWindow's underlined variant.</summary>
internal static class TabItemOptions
{
    public static readonly DependencyProperty ShowSelectedUnderlineProperty =
        DependencyProperty.RegisterAttached(
            "ShowSelectedUnderline",
            typeof(bool),
            typeof(TabItemOptions),
            new FrameworkPropertyMetadata(false));

    public static bool GetShowSelectedUnderline(DependencyObject element) =>
        (bool)element.GetValue(ShowSelectedUnderlineProperty);

    public static void SetShowSelectedUnderline(DependencyObject element, bool value) =>
        element.SetValue(ShowSelectedUnderlineProperty, value);
}

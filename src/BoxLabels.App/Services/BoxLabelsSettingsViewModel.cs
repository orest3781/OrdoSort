using OrdoSort.Core;
using OrdoSort.Wpf.Mvvm;
using OrdoSort.Wpf.Theme;
using OrdoSort.Wpf.ViewModels;

namespace BoxLabelsApp.Services;

/// <summary>What Box Labels' Settings window edits: the General tab (theme,
/// text size and font, saved per PC beside the exe; the labels file) and the
/// Label style tab (saved to the shared labels file).</summary>
public sealed class BoxLabelsSettingsViewModel : ObservableObject
{
    /// <param name="labelsFile">The shared labels file in use now.</param>
    /// <param name="theme">"auto", "light" or "dark".</param>
    /// <param name="fontFamily">ui_font_family: "" is the default font.</param>
    /// <param name="fontSize">ui_font_size: 0 is the default size.</param>
    /// <param name="style">The stored label style, or null when the labels
    /// file couldn't be read.</param>
    /// <param name="styleProblem">Why it couldn't be read, or "".</param>
    public BoxLabelsSettingsViewModel(string labelsFile, string theme, string fontFamily, int fontSize,
        BoxLabels.LabelStyle? style, string styleProblem)
    {
        _labelsFile = labelsFile;
        _theme = theme is "light" or "dark" ? theme : "auto";
        _uiFontFamily = fontFamily;
        _uiFontSizeText = fontSize == 0 ? "" : fontSize.ToString();
        LabelStyle = style is { } s ? new LabelStyleEditorViewModel(s) : null;
        LabelStyleProblem = styleProblem;
        _opened = (_labelsFile, _theme, _uiFontFamily, _uiFontSizeText);
    }

    // What the window opened with, for IsChanged.
    private readonly (string LabelsFile, string Theme, string Family, string SizeText) _opened;

    /// <summary>True when anything differs from what the window opened with:
    /// closing then asks before throwing the edits away.</summary>
    public bool IsChanged =>
        (LabelsFile, Theme, UiFontFamily, UiFontSizeText) != _opened || LabelStyle is { IsChanged: true };

    private string _labelsFile;

    /// <summary>Set by Change… in the window; the app switches to it on OK.</summary>
    public string LabelsFile
    {
        get => _labelsFile;
        set
        {
            if (!Set(ref _labelsFile, value)) return;
            Raise(nameof(HasLabelStyle));
            Raise(nameof(LabelStyleNote));
        }
    }

    /// <summary>The style shown was read from the file in use when Settings
    /// opened; once another file is picked it would be the old file's look,
    /// so the tab steps aside and nothing is written (final review, 2026-09-28).</summary>
    public bool LabelsFileChanged =>
        !string.Equals(LabelsFile, _opened.LabelsFile, StringComparison.OrdinalIgnoreCase);

    private string _theme;

    /// <summary>"auto", "light" or "dark".</summary>
    public string Theme => _theme;

    public bool ThemeAuto { get => _theme == "auto"; set { if (value) SetTheme("auto"); } }
    public bool ThemeLight { get => _theme == "light"; set { if (value) SetTheme("light"); } }
    public bool ThemeDark { get => _theme == "dark"; set { if (value) SetTheme("dark"); } }

    private void SetTheme(string theme)
    {
        if (!Set(ref _theme, theme, nameof(Theme))) return;
        Raise(nameof(ThemeAuto));
        Raise(nameof(ThemeLight));
        Raise(nameof(ThemeDark));
    }

    private string _uiFontFamily;

    /// <summary>ui_font_family: one of <see cref="AppFonts.Choices"/>' keys.</summary>
    public string UiFontFamily { get => _uiFontFamily; set => Set(ref _uiFontFamily, value); }

    private string _uiFontSizeText;

    /// <summary>The text size as typed: blank is the default.</summary>
    public string UiFontSizeText { get => _uiFontSizeText; set => Set(ref _uiFontSizeText, value); }

    /// <summary>The size to save (0 = default); only meaningful once
    /// <see cref="Problems"/> is empty.</summary>
    public int UiFontSize => int.TryParse(UiFontSizeText.Trim(), out var n) ? n : 0;

    /// <summary>The Label style tab's editor; null when the labels file
    /// couldn't be read (the tab then shows <see cref="LabelStyleProblem"/>).</summary>
    public LabelStyleEditorViewModel? LabelStyle { get; }

    public string LabelStyleProblem { get; }

    public bool HasLabelStyle => LabelStyle is not null && !LabelsFileChanged;

    /// <summary>What the Label style tab says instead of the editor, or "".</summary>
    public string LabelStyleNote => LabelsFileChanged
        ? "You picked a different labels file. Press OK to switch to it, then open Settings "
        + "again to set its label style."
        : LabelStyleProblem;

    /// <summary>What blocks OK, in words for the user; empty when OK can save.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (AppFonts.SizeProblem(UiFontSizeText) is { Length: > 0 } size) problems.Add(size);
        if (UiFontFamily.Length > 0 && !AppFonts.Choices.Any(f => f.Key == UiFontFamily))
            problems.Add($"\"{UiFontFamily}\" is not one of the app fonts — pick one from the list.");
        return problems;
    }
}

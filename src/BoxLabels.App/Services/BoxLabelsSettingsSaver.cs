using System.IO;
using OrdoSort.Core;

namespace BoxLabelsApp.Services;

/// <summary>Saves what Box Labels' Settings OK accepted: the General tab
/// beside the exe, and the label style (only if changed) to the shared
/// labels file that will be in use. Each half is tried on its own and
/// reported, so one failing never costs the other; nothing here throws.</summary>
public static class BoxLabelsSettingsSaver
{
    /// <param name="AppearanceSaved">Theme, text size and font reached
    /// box-labels-app.json.</param>
    /// <param name="StyleSaved">The label style reached the shared file, or
    /// had not changed.</param>
    /// <param name="Failures">What to tell the user, one line per failure.</param>
    public sealed record Outcome(bool AppearanceSaved, bool StyleSaved, IReadOnlyList<string> Failures);

    public static Outcome Save(string settingsPath, BoxLabelsSettingsViewModel vm)
    {
        var failures = new List<string>();
        var appearanceSaved = true;
        try
        {
            LabelsFileSettings.WriteAppearance(settingsPath, vm.Theme, vm.UiFontFamily, vm.UiFontSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            appearanceSaved = false;
            failures.Add($"Theme and text settings not saved ({settingsPath}): {ex.Message}");
        }

        var styleSaved = true;
        // a newly picked file's style is set after the switch, never from the
        // old file's (vm.HasLabelStyle is false then)
        if (vm.HasLabelStyle && vm.LabelStyle is { IsChanged: true } edited)
        {
            try
            {
                BoxLabelStore.Mutate(vm.LabelsFile, d => { d.Style = edited.Style; return 0; });
            }
            catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
            {
                styleSaved = false;
                failures.Add($"Label style not saved ({vm.LabelsFile}): {ex.Message}");
            }
        }
        return new Outcome(appearanceSaved, styleSaved, failures);
    }
}

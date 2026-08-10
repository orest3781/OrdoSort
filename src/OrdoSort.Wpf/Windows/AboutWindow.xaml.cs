using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace OrdoSort.Wpf.Windows;

/// <summary>Product name, the version this exact assembly carries, and a
/// pointer to the third-party notices shipped beside it (v1.0 release-
/// blockers plan, 2026-08-09, Task 1 Step 2). Reached from MainWindow's
/// _Help menu, the same ShowDialog()-owned-by-MainWindow pattern every
/// other tool window in this app uses.
///
/// The version is read from THIS ASSEMBLY'S OWN metadata at runtime
/// (<see cref="AssemblyInformationalVersionAttribute"/>, which the SDK's
/// GenerateAssemblyInfo target populates from Directory.Build.props'
/// single &lt;Version&gt; — see that file's own comment for how the other
/// three version properties derive from the same source) rather than a
/// literal baked into this file: a hardcoded string here could silently
/// disagree with what actually shipped, which is exactly what
/// AboutWindowVersionTests exists to catch.</summary>
public partial class AboutWindow : Window
{
    /// <summary>The exact string this window displays — exposed so
    /// AboutWindowVersionTests can assert against it without re-parsing UI
    /// text, in addition to reading the real VersionText control.</summary>
    internal string DisplayedVersion { get; }

    public AboutWindow()
    {
        InitializeComponent();
        DisplayedVersion = ReadAssemblyVersion();
        VersionText.Text = $"Version {DisplayedVersion}";
    }

    /// <summary>InformationalVersion first — the attribute Directory.Build.props'
    /// &lt;Version&gt; feeds without the numeric-only truncation
    /// AssemblyVersion/FileVersion require — falling back to the numeric
    /// AssemblyVersion only if a build somehow lacks the attribute
    /// entirely (e.g. a non-SDK-style build of this project).</summary>
    internal static string ReadAssemblyVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString()
            ?? "unknown";
    }

    private void OnViewThirdPartyNotices(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES");
        if (!File.Exists(path)) return;
        try
        {
            // UseShellExecute: this is a plain-text file with no extension,
            // so the shell's own "open with" association handles it —
            // exactly the pattern LabelMakerViewModel/SettingsViewModel
            // already use to open folders.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No handler associated with an extensionless file on this
            // machine — nothing else to fall back to from here, and this
            // button is a convenience, not a load-bearing path.
        }
    }
}

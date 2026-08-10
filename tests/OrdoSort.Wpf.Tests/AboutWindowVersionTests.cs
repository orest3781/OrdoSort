using System.Reflection;
using OrdoSort.Wpf.Windows;

namespace OrdoSort.Wpf.Tests;

/// <summary>v1.0 release-blockers plan (2026-08-09), Task 1 Step 3: proves
/// the About box's displayed version is read from the SHIPPED assembly's
/// own metadata, not a second hardcoded copy of the number sitting next to
/// it — a test comparing two literal "1.0.0" strings would pass whether or
/// not that plumbing exists at all, which is exactly this repo's
/// thirteenth-and-counting "passed for the wrong reason" shape.
///
/// "Expected" is read HERE, independently of AboutWindow's own internal
/// read, via <see cref="Assembly.GetCustomAttribute{T}()"/> against
/// <c>typeof(AboutWindow).Assembly</c> — deliberately NOT
/// <c>Assembly.GetExecutingAssembly()</c>, which from inside this test
/// project would resolve to OrdoSort.Wpf.Tests.dll, not the shipped
/// OrdoSort.dll AboutWindow actually lives in and displays the version of.
/// "Actual" is read off the REAL rendered control (VersionText, reachable
/// directly because OrdoSort.Wpf.csproj's InternalsVisibleTo covers this
/// project — the same access ManageSavedWindowCopyTests already relies on
/// for StorageNote), not just AboutWindow's own DisplayedVersion property,
/// so a bug that sets the property correctly but never applies it to the UI
/// would still be caught.</summary>
[Collection(HighlightContrastTests.Name)]
public class AboutWindowVersionTests
{
    private readonly HighlightContrastFixture _fx;
    public AboutWindowVersionTests(HighlightContrastFixture fx) => _fx = fx;

    [Fact]
    public void DisplayedVersionMatchesTheAssemblysOwnMetadata() => _fx.Invoke(() =>
    {
        var expected = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(expected),
            "OrdoSort.dll carries no AssemblyInformationalVersionAttribute at all — " +
            "Directory.Build.props' <Version> isn't reaching the shipped assembly");

        var window = new AboutWindow();
        try
        {
            Assert.Equal(expected, window.DisplayedVersion);
            Assert.Contains(expected!, window.VersionText.Text);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>Directory.Build.props' own contract (Task 1 Step 1): the
    /// four version properties derive from ONE value, so AssemblyVersion —
    /// read the same independent way as InformationalVersion above — must
    /// agree with it too (modulo AssemblyVersion's mandatory four-part
    /// padding). Guards against a future edit that sets one of the four
    /// properties explicitly on a single csproj, letting it drift from the
    /// other three.</summary>
    [Fact]
    public void AssemblyVersionAgreesWithInformationalVersion() => _fx.Invoke(() =>
    {
        var asm = typeof(AboutWindow).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var assemblyVersion = asm.GetName().Version;
        Assert.False(string.IsNullOrWhiteSpace(informational));
        Assert.NotNull(assemblyVersion);
        Assert.True(Version.TryParse(informational, out var parsedInformational),
            $"InformationalVersion \"{informational}\" isn't a plain Version string");
        Assert.Equal(parsedInformational!.Major, assemblyVersion!.Major);
        Assert.Equal(parsedInformational.Minor, assemblyVersion.Minor);
        Assert.Equal(parsedInformational.Build, assemblyVersion.Build);
    });
}

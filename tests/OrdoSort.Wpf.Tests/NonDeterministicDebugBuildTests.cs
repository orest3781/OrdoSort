using System.Reflection;
using System.Reflection.PortableExecutable;
using OrdoSort.Wpf;

namespace OrdoSort.Wpf.Tests;

/// <summary>Guards Directory.Build.targets' <c>&lt;Deterministic&gt;</c>
/// override, the fix for Windows Smart App Control permanently blocking the
/// app (2026-08-25). SAC blocks unsigned binaries by file hash; a
/// deterministic build reproduces the same hash forever; so a blocked
/// OrdoSort.dll stays blocked through every rebuild until something moves its
/// hash. Turning determinism off in Debug is that something.
///
/// The behaviour under test is invisible from managed metadata — nothing on
/// <see cref="Assembly"/> reports whether the compiler was deterministic — so
/// these tests read the PE debug directory instead. Roslyn emits an
/// <see cref="DebugDirectoryEntryType.Reproducible"/> entry if and ONLY if it
/// compiled deterministically, which makes its presence an exact, inverted
/// readout of the property this file exists to defend.
///
/// Both configurations are asserted, in opposite directions, because the
/// change carries a risk in each direction: losing the override puts Debug
/// back under the permanent block, and letting it leak past its
/// <c>'$(Configuration)' == 'Debug'</c> condition would cost Release — the
/// configuration publish.bat ships — its reproducible bytes. A test that only
/// checked Debug would pass just as happily if the condition were deleted
/// altogether.
///
/// Only one of those halves compiles per run, so "both are asserted" is a
/// claim about automated runs, not about this source file: ci.yml builds
/// Release, then builds Debug and re-runs this class under a filter. Drop
/// that second leg and the Debug assertion stops being executed anywhere,
/// which is the state this test was written in and the reason the leg exists.
///
/// The assemblies inspected are the SHIPPED ones (OrdoSort.dll and
/// OrdoSort.Core.dll, resolved through types that live in them), not
/// OrdoSort.Wpf.Tests.dll, which <see cref="Assembly.GetExecutingAssembly"/>
/// would return: the test assembly's own determinism is beside the point, and
/// the app assembly is the file the CodeIntegrity log actually named.</summary>
public class NonDeterministicDebugBuildTests
{
    public static TheoryData<string> ShippedAssemblies() => new()
    {
        typeof(MainWindow).Assembly.Location,
        typeof(OrdoSort.Core.Route).Assembly.Location,
    };

    [Theory]
    [MemberData(nameof(ShippedAssemblies))]
    public void ShippedAssembliesCarryTheDeterminismStanceTheirConfigurationCallsFor(string path)
    {
        Assert.True(File.Exists(path), $"Expected the shipped assembly beside the tests: {path}");

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var isReproducible = pe.ReadDebugDirectory()
            .Any(entry => entry.Type == DebugDirectoryEntryType.Reproducible);

#if DEBUG
        Assert.False(isReproducible,
            $"{Path.GetFileName(path)} was compiled deterministically in Debug, so every rebuild " +
            "reproduces its exact bytes and a Smart App Control block on that hash can never be " +
            "cleared by rebuilding. Directory.Build.targets' <Deterministic>false</Deterministic> " +
            "is missing, or its '$(Configuration)' == 'Debug' condition stopped matching.");
#else
        Assert.True(isReproducible,
            $"{Path.GetFileName(path)} was compiled non-deterministically in Release. Release is " +
            "what publish.bat ships and its bytes must stay reproducible. Either the Debug-only " +
            "<Deterministic>false</Deterministic> in Directory.Build.targets has leaked past its " +
            "condition, or the build was given -p:Deterministic=false on the command line — a " +
            "global property, which overrides the targets file in every configuration. Check for " +
            "the flag before suspecting the file.");
#endif
    }
}

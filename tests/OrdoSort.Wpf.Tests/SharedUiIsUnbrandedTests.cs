namespace OrdoSort.Wpf.Tests;

/// <summary>OrdoSort.Ui is shared by OrdoSort.exe and BoxLabels.exe, and
/// BoxLabels.exe is handed to someone who does not have OrdoSort and has no
/// reason to have heard of it. A window title or dialog heading naming
/// OrdoSort would leak into that app, and would do it silently — nothing
/// crashes, the wrong word simply appears on screen.
///
/// So the library may not spell OrdoSort's user-facing titles at all. Hosts
/// pass their own names in (LabelMakerViewModel's appTitle,
/// LabelMakerWindow's windowTitle/previewTitle, PrintPreviewWindow's
/// windowTitle); this suite is what stops the next person putting one back.
///
/// It matches the branded FORMS ("OrdoSort — ..." and "OrdoSort labels ..."),
/// not the bare token: namespaces and type names like OrdoSort.Wpf.Windows
/// are all over this assembly and are not branding. If a future comment
/// legitimately needs the em-dash phrasing, reword the comment — do not relax
/// the check, which exists precisely because the mistake it catches is
/// invisible at runtime.</summary>
public class SharedUiIsUnbrandedTests
{
    private static string SharedUiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln")))
            dir = dir.Parent!;
        var root = dir?.FullName ?? throw new InvalidOperationException(
            "couldn't find OrdoSort.sln walking up from " + AppContext.BaseDirectory);
        return Path.Combine(root, "src", "OrdoSort.Ui");
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            // bin/obj hold generated copies of the same source; scanning them
            // would report every hit twice and break on a stale build output
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Theory]
    [InlineData("OrdoSort —")]
    [InlineData("OrdoSort labels")]
    public void TheSharedLibraryDoesNotSpellOrdoSortsTitles(string brandedForm)
    {
        var root = SharedUiRoot();
        Assert.True(Directory.Exists(root), $"OrdoSort.Ui not found at {root}");

        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in SourceFiles(root))
        {
            scanned++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains(brandedForm, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }

        // guards against the scan silently matching nothing at all — an empty
        // file list would make this pass no matter what the library contained
        Assert.True(scanned >= 10, $"only scanned {scanned} files under {root}");
        Assert.True(offenders.Count == 0,
            $"OrdoSort.Ui is shared with BoxLabels.exe and must not name OrdoSort:\n"
            + string.Join("\n", offenders));
    }
}

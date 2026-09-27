namespace OrdoSort.TestSupport;

/// <summary>Where the repository is, for tests that read source files (XAML
/// scans, the brand-assets check).</summary>
public static class Repo
{
    /// <summary>The folder holding OrdoSort.sln, found by walking up from the
    /// test binaries.</summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OrdoSort.sln")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new InvalidOperationException("OrdoSort.sln not found above " + AppContext.BaseDirectory);
        }
    }
}

namespace OrdoSort.Core;

/// <summary>
/// Turns dropped/browsed files and folders into a flat, natural-sorted list
/// of filenames — the tool exists to produce exactly that list for pasting
/// into a manifest or spreadsheet. Built entirely on Task 1's shared intake
/// plumbing (Intake.Expand does the file-vs-folder walk and the extension
/// filter; FolderMonitor.ParseFiletypes turns the free-text filter box into
/// the set Intake wants), so this class is only the two steps intake alone
/// can't do: stem/extension mapping and re-sorting on the produced NAMES
/// rather than the full paths Intake itself sorts by.
/// </summary>
public static class FilenameList
{
    public sealed record Options(bool Recursive, bool IncludeExtension, string ExtensionFilter = "");

    public sealed record Listing(IReadOnlyList<string> Names, int Ignored, string Error = "");

    /// <summary>Never throws — Intake.Expand's own Ignored/Error flow
    /// through unchanged. Duplicate names are kept (same name under two
    /// different folders is still two rows in a filename list, not a set),
    /// so this is a re-sort of the mapped names, not a Distinct().</summary>
    public static Listing Build(IReadOnlyList<string> paths, Options opt)
    {
        var expanded = Intake.Expand(paths, opt.Recursive, FolderMonitor.ParseFiletypes(opt.ExtensionFilter));
        var names = expanded.Files
            .Select(f => opt.IncludeExtension ? Path.GetFileName(f) : Path.GetFileNameWithoutExtension(f))
            .ToList();
        // Intake's own sort is by full PATH, which for IncludeExtension=false
        // (or simply because two different folders share a prefix) does not
        // necessarily match natural order over the mapped NAMES — re-sort on
        // what this list is actually going to show.
        names.Sort(NaturalSort.Instance);
        return new Listing(names, expanded.Ignored, expanded.Error);
    }

    public static string ToText(IEnumerable<string> names) => string.Join(Environment.NewLine, names);
}

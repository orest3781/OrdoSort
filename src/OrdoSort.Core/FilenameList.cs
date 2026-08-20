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

    public sealed record FileRow(
        string Name,
        long? Size,
        DateTime? Modified,
        string Folder,
        string FullPath);

    public sealed record Listing(IReadOnlyList<FileRow> Rows, int Ignored, string Error = "")
    {
        /// <summary>TEMPORARY shim so FilenameListViewModel keeps compiling while
        /// the layers migrate one task at a time. Deleted in Task 6, once the view
        /// model reads Rows directly.</summary>
        public IReadOnlyList<string> Names => Rows.Select(r => r.Name).ToList();
    }

    /// <summary>The per-file metadata read, injectable so a test can force the
    /// failure that is otherwise a race: a file enumerated by Intake.Expand can be
    /// gone, locked or access-denied by the time this runs. Production passes null
    /// and gets the real FileInfo.</summary>
    public static Listing Build(IReadOnlyList<string> paths, Options opt,
        Func<string, (long Size, DateTime Modified)>? stat = null)
    {
        var expanded = Intake.Expand(paths, opt.Recursive, FolderMonitor.ParseFiletypes(opt.ExtensionFilter));
        stat ??= p => { var fi = new FileInfo(p); return (fi.Length, fi.LastWriteTime); };

        var rows = new List<FileRow>(expanded.Files.Count);
        foreach (var file in expanded.Files)
        {
            long? size = null;
            DateTime? modified = null;
            try
            {
                var (s, m) = stat(file);
                size = s;
                modified = m;
            }
            catch (Exception)
            {
                // gone, locked or denied since the walk — an unknown value, not a
                // reason to drop the row or to throw out of a never-throws method
            }

            rows.Add(new FileRow(
                opt.IncludeExtension ? Path.GetFileName(file) : Path.GetFileNameWithoutExtension(file),
                size, modified, "", file));
        }

        // Intake sorts by full PATH; re-sort on the NAME this list actually shows.
        rows.Sort((a, b) => NaturalSort.Instance.Compare(a.Name, b.Name));
        return new Listing(rows, expanded.Ignored, expanded.Error);
    }

    public static string ToText(IEnumerable<string> names) => string.Join(Environment.NewLine, names);
}

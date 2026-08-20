namespace OrdoSort.Core;

using System.Globalization;

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

    public sealed record Listing(IReadOnlyList<FileRow> Rows, int Ignored, string Error = "");

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
                size, modified, FolderFor(file, paths), file));
        }

        // Intake sorts by full PATH; re-sort on the NAME this list actually shows.
        rows.Sort((a, b) => NaturalSort.Instance.Compare(a.Name, b.Name));
        return new Listing(rows, expanded.Ignored, expanded.Error);
    }

    /// <summary>Which optional columns are on. Name is NOT a member: it is always
    /// emitted, so including it would make a HasFlag check trivially true and
    /// leave the list-vs-table rule below unstateable.</summary>
    [Flags]
    public enum Columns
    {
        None = 0,
        Number = 1,
        Size = 2,
        Modified = 4,
        Folder = 8,
        FullPath = 16,
    }

    /// <summary>True once any column carrying DATA is on. Number alone does not
    /// count — a numbered list of names is still a list, which is what lets
    /// "1. invoice-2024.pdf" exist.</summary>
    private static bool IsTable(Columns cols) => (cols & ~Columns.Number) != Columns.None;

    private static string Cell(FileRow row, Columns column, int index) => column switch
    {
        Columns.Number => (index + 1).ToString(CultureInfo.InvariantCulture),
        Columns.Size => row.Size?.ToString(CultureInfo.InvariantCulture) ?? "",
        Columns.Modified => row.Modified?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
        Columns.Folder => row.Folder,
        Columns.FullPath => row.FullPath,
        _ => row.Name,
    };

    // Fixed order, however the flags were combined. Columns.None stands for the
    // always-present Name column in this table.
    private static readonly (Columns Flag, string Header)[] Layout =
    {
        (Columns.Number, "#"),
        (Columns.None, "Name"),
        (Columns.Size, "Size"),
        (Columns.Modified, "Modified"),
        (Columns.Folder, "Folder"),
        (Columns.FullPath, "Full path"),
    };

    private static List<(Columns Flag, string Header)> Active(Columns cols) =>
        Layout.Where(c => c.Flag == Columns.None || (cols & c.Flag) != 0).ToList();

    /// <summary>The clipboard text. One rule: Name alone is a plain list, with
    /// Number as a "1. " prefix; any data column makes it tab-separated with a
    /// header row, and Number becomes a column of its own.</summary>
    public static string ToText(IReadOnlyList<FileRow> rows, Columns cols)
    {
        if (rows.Count == 0) return "";

        if (!IsTable(cols))
        {
            var numbered = (cols & Columns.Number) != 0;
            return string.Join(Environment.NewLine, rows.Select((r, i) =>
                numbered ? $"{i + 1}. {r.Name}" : r.Name));
        }

        var active = Active(cols);
        var lines = new List<string>(rows.Count + 1)
        {
            string.Join("\t", active.Select(c => c.Header)),
        };
        for (var i = 0; i < rows.Count; i++)
            lines.Add(string.Join("\t", active.Select(c => Cell(rows[i], c.Flag, i))));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The .csv export. Always carries a header — a CSV without one is
    /// not a table — and every field goes through Csv.EscapeField, which carries
    /// the Excel formula-injection guard. That guard matters more here than almost
    /// anywhere else in the app: filenames are user-controlled, and a file called
    /// "=cmd...pdf" is something Excel will try to interpret when the exported
    /// file is opened.</summary>
    public static string ToCsv(IReadOnlyList<FileRow> rows, Columns cols)
    {
        var active = Active(cols);
        var lines = new List<string>(rows.Count + 1)
        {
            Csv.WriteRow(active.Select(c => c.Header)),
        };
        for (var i = 0; i < rows.Count; i++)
            lines.Add(Csv.WriteRow(active.Select(c => Cell(rows[i], c.Flag, i))));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The directory of <paramref name="file"/> relative to whichever
    /// root it arrived under. Roots can nest — someone drops a folder and then a
    /// subfolder of it — so the LONGEST match wins and Folder stays as short as it
    /// can be. A root that IS the file, an individually added file, has no folder
    /// to be relative to.</summary>
    internal static string FolderFor(string file, IReadOnlyList<string> roots)
    {
        var best = "";
        foreach (var root in roots)
        {
            var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(trimmed, file, StringComparison.OrdinalIgnoreCase))
                return "";   // the file was added directly

            var prefix = trimmed + Path.DirectorySeparatorChar;
            if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && trimmed.Length > best.Length)
                best = trimmed;
        }

        if (best.Length == 0) return "";

        var dir = Path.GetDirectoryName(file);
        if (dir is null) return "";

        // "C:" is drive-RELATIVE in Windows, not the drive root: GetRelativePath
        // resolves it against the process's per-drive current directory. Anchor it
        // at the root here, at the point of use — putting the separator back on
        // `trimmed` above would double it into "C:\\" and stop the prefix test
        // matching anything.
        var relativeTo = best.Length == 2 && best[1] == ':'
            ? best + Path.DirectorySeparatorChar
            : best;

        var relative = Path.GetRelativePath(relativeTo, dir);
        // GetRelativePath returns "." when dir IS the root — that is not a folder.
        return relative == "." ? "" : relative;
    }
}

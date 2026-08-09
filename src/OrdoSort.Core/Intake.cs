namespace OrdoSort.Core;

/// <summary>
/// Turns whatever a drop or Browse dialog handed the app — a mix of file
/// and folder paths — into one flat, deterministically ordered file list.
/// Shared by the utility tools being added alongside this: each one would
/// otherwise carry its own "is it a file, is it a folder, does the
/// extension match" logic; this is that logic written once. Extension
/// matching mirrors FolderMonitor.TypeMatches exactly (built from a set
/// callers make with the existing FolderMonitor.ParseFiletypes — not
/// reimplemented here). Never throws: like FolderMonitor.Status and
/// Scanner.Scan, a filesystem failure comes back in Error, with whatever
/// was already gathered still returned rather than lost.
/// </summary>
public static class Intake
{
    public sealed record Expanded(List<string> Files, int Ignored, string Error = "");

    public static Expanded Expand(IEnumerable<string> paths, bool recursive, ISet<string>? extensions)
    {
        var files = new List<string>();
        var ignored = 0;
        try
        {
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    AddIfMatches(path, extensions, files, ref ignored);
                }
                else if (Directory.Exists(path))
                {
                    var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    foreach (var f in Directory.EnumerateFiles(path, "*", option))
                        AddIfMatches(f, extensions, files, ref ignored);
                }
                else
                {
                    ignored++;   // neither a file nor a folder — gone, or never existed
                }
            }
        }
        catch (Exception ex)
        {
            // whatever was gathered before the failure is still worth returning
            files.Sort(NaturalSort.Instance);
            return new Expanded(files, ignored,
                $"Couldn't finish reading the dropped items: {ex.Message}");
        }

        files.Sort(NaturalSort.Instance);
        return new Expanded(files, ignored, "");
    }

    private static void AddIfMatches(
        string file, ISet<string>? extensions, List<string> files, ref int ignored)
    {
        if (TypeMatches(file, extensions)) files.Add(file);
        else ignored++;
    }

    /// <summary>Same rule as FolderMonitor.TypeMatches: a null or empty
    /// extension set accepts everything; otherwise the file's extension
    /// (lowercased, dot-less) must be in the set the caller built.</summary>
    private static bool TypeMatches(string file, ISet<string>? extensions) =>
        extensions is null || extensions.Count == 0 ||
        extensions.Contains(Path.GetExtension(file).TrimStart('.').ToLowerInvariant());
}

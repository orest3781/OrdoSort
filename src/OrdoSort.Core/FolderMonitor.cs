namespace OrdoSort.Core;

/// <summary>Computes the live state of a monitored folder for the Ready
/// dashboard: how many matching files it holds and which filenames trip an
/// alert term. Pure filesystem read; never throws.</summary>
public static class FolderMonitor
{
    public sealed record FolderStatus(
        string Label, string Path, string? Color, int Count, string Error,
        IReadOnlyList<string> Matches, string Section, IReadOnlyList<string>? AlertFoldersRaw = null)
    {
        /// <summary>Tiles only appear while the folder holds matching files.</summary>
        public bool HasFiles => Count > 0;
        public bool Alerting => Matches.Count > 0;

        /// <summary>Distinct subfolders (relative to the watched folder, in
        /// discovery order) holding alerting files — so a recursive watch can
        /// say WHERE the urgent file sits. Empty for top-level alerts.</summary>
        public IReadOnlyList<string> AlertFolders => AlertFoldersRaw ?? Array.Empty<string>();
    }

    /// <summary>Extensions (no dot, lower) a folder counts; empty set = any file.</summary>
    /// <summary>Trim('*', '.') rather than the old TrimStart('.') (audit FL-02):
    /// every caller feeds this free text a person typed — the filename list's
    /// "Only these types" box, a watch folder's Filetypes, the Settings field —
    /// and "*.pdf" is what a Windows user types. The old parser left the '*' on,
    /// producing the token "*.pdf", which matches no extension that has ever
    /// existed: zero results, no error, no clue why. Trimming both characters
    /// from both ends also collapses "*" and "*.*" to nothing, which is right —
    /// an empty set already means "accept everything" to TypeMatches, so a bare
    /// star lands on the meaning the user intended instead of its exact
    /// opposite. Where() then drops those now-empty tokens.</summary>
    public static HashSet<string> ParseFiletypes(string filetypes) =>
        filetypes.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('*', '.').ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToHashSet();

    private static bool TypeMatches(string file, HashSet<string> types) =>
        types.Count == 0 ||
        types.Contains(System.IO.Path.GetExtension(file).TrimStart('.').ToLowerInvariant());

    /// <summary>True if <paramref name="name"/> contains any alert term
    /// (case-insensitive). Blank terms are ignored.</summary>
    public static bool IsAlerting(string name, IEnumerable<string> alertTerms) =>
        alertTerms.Any(t => !string.IsNullOrWhiteSpace(t) &&
            name.Contains(t.Trim(), StringComparison.OrdinalIgnoreCase));

    public static FolderStatus Status(WatchFolder wf, IEnumerable<string> alertTerms)
    {
        var terms = alertTerms.ToList();
        if (string.IsNullOrWhiteSpace(wf.Path))
            return new(wf.Label, wf.Path, wf.Color, 0, "no path configured", Array.Empty<string>(), wf.Section);
        if (!Directory.Exists(wf.Path))
            return new(wf.Label, wf.Path, wf.Color, 0, $"folder not available: {wf.Path}", Array.Empty<string>(), wf.Section);

        try
        {
            var types = ParseFiletypes(wf.Filetypes);
            // Same fix as Intake.cs:33-49, and for the same reason: the bare
            // SearchOption overload aborts the WHOLE walk on the first
            // unreadable subfolder, so this dashboard tile would go blank
            // instead of alerting on a readable sibling. AttributesToSkip = 0
            // matters here too — see that comment.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = wf.Recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            };
            var files = Directory.EnumerateFiles(wf.Path, "*", options)
                .Where(f => TypeMatches(f, types))
                .ToList();
            // matches are RELATIVE paths (just the name for top-level files),
            // so a recursive watch shows where an alerting file actually sits
            var matches = new List<string>();
            var alertFolders = new List<string>();
            foreach (var file in files)
            {
                var name = System.IO.Path.GetFileName(file);
                if (!IsAlerting(name, terms)) continue;
                var rel = System.IO.Path.GetRelativePath(wf.Path, file);
                matches.Add(rel);
                var dir = System.IO.Path.GetDirectoryName(rel) ?? "";
                if (dir.Length > 0 && !alertFolders.Contains(dir)) alertFolders.Add(dir);
            }
            return new(wf.Label, wf.Path, wf.Color, files.Count, "", matches, wf.Section, alertFolders);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(wf.Label, wf.Path, wf.Color, 0, $"can't read folder: {ex.Message}", Array.Empty<string>(), wf.Section);
        }
    }

    /// <summary>Status for every configured watch folder, in order.</summary>
    public static List<FolderStatus> All(IEnumerable<WatchFolder> folders, IEnumerable<string> alertTerms)
    {
        var terms = alertTerms.ToList();
        return folders.Select(f => Status(f, terms)).ToList();
    }
}

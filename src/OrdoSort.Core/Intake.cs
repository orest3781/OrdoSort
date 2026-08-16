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
                    // The SearchOption overload aborts the WHOLE walk with
                    // UnauthorizedAccessException at the first subfolder it
                    // can't open (denied ACL, cloud placeholder, junction) —
                    // losing every file already found, not just the
                    // unreadable subtree. EnumerationOptions with
                    // IgnoreInaccessible = true skips just that subfolder
                    // instead. AttributesToSkip = 0 is required alongside it:
                    // the EnumerationOptions default silently skips
                    // Hidden|System files, which the old SearchOption
                    // overload never did — leaving it at 0 keeps this an
                    // exact match for the old behavior everywhere but the abort.
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = recursive,
                        IgnoreInaccessible = true,
                        AttributesToSkip = 0,
                    };
                    foreach (var f in Directory.EnumerateFiles(path, "*", options))
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

    /// <summary>What one Add call did. Files are canonical and in input
    /// order — only the ones actually accepted. The four counts are the
    /// reasons a path was turned away, kept apart rather than summed,
    /// because every tool previously collapsed them into one number and then
    /// had to write a sentence that hedged across all of them ("not PDFs, or
    /// already listed"). Ignored is still there for the callers that only
    /// want the total.</summary>
    public sealed record Added(
        List<string> Files, int AlreadyListed, int WrongType, int Missing, int Unusable)
    {
        public int Ignored => AlreadyListed + WrongType + Missing + Unusable;

        /// <summary>The one status line every tool shows after a drop.
        /// <paramref name="itemNoun"/> is the singular thing this tool takes
        /// ("PDF", "zip", "file"), pluralised with an "s". Empty when there
        /// is nothing to report, which is what every tool's existing "no
        /// note" case already meant.</summary>
        public string Note(string itemNoun)
        {
            if (Ignored == 0) return "";
            var why = new List<string>();
            if (AlreadyListed > 0) why.Add($"{AlreadyListed} already listed");
            if (WrongType > 0)
                why.Add(WrongType == 1 ? $"1 isn't a {itemNoun}" : $"{WrongType} aren't {itemNoun}s");
            if (Missing > 0)
                why.Add(Missing == 1 ? "1 doesn't exist" : $"{Missing} don't exist");
            if (Unusable > 0)
                why.Add(Unusable == 1 ? "1 unusable path" : $"{Unusable} unusable paths");
            var reasons = string.Join(" · ", why);
            return Files.Count == 0
                ? $"nothing added — {reasons}"
                : $"{Files.Count} added · {Ignored} ignored ({reasons})";
        }
    }

    /// <summary>Add <paramref name="incoming"/> to a list that already holds
    /// <paramref name="existing"/>, keeping it free of duplicates — including
    /// the ones that only differ in spelling. Everything is stored canonical
    /// (see PathIdentity), so the invariant "this list never holds two paths
    /// naming the same file" survives for the list's lifetime instead of
    /// holding only at the moment of the add.
    ///
    /// Deduplicates within the batch as well as against the existing list:
    /// one drop containing both "scan.pdf" and "SCAN.pdf" adds one entry.
    /// Every tool's hand-written version already behaved this way.
    ///
    /// <paramref name="exists"/> is a predicate, not a call, for the reason
    /// Naming.BuildTarget takes one: it keeps this whole module sync and
    /// disk-free, so its tests need no temp directory and the tools that
    /// already run their intake off-thread keep scheduling it themselves.
    /// Null means "don't check" — which is FilenameList's deliberate
    /// behaviour, since a source that has gone since it was added is its to
    /// report later, not intake's to reject now.
    ///
    /// Order of judgement is fixed so the reported reason is deterministic:
    /// unusable, then wrong type, then missing, then already listed. A
    /// missing .txt in a PDF tool reads as "isn't a PDF", which is the more
    /// useful of the two true statements.</summary>
    public static Added Add(
        IEnumerable<string> existing,
        IEnumerable<string> incoming,
        ISet<string>? extensions = null,
        Func<string, bool>? exists = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in existing)
            if (PathIdentity.Canonical(e) is { } already) seen.Add(already);

        var accepted = new List<string>();
        int alreadyListed = 0, wrongType = 0, missing = 0, unusable = 0;

        foreach (var path in incoming)
        {
            var canonical = PathIdentity.Canonical(path);
            if (canonical is null) { unusable++; continue; }
            if (!TypeMatches(canonical, extensions)) { wrongType++; continue; }
            if (exists is not null && !exists(canonical)) { missing++; continue; }
            if (!seen.Add(canonical)) { alreadyListed++; continue; }
            accepted.Add(canonical);
        }

        return new Added(accepted, alreadyListed, wrongType, missing, unusable);
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

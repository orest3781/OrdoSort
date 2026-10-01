using System.Globalization;

namespace OrdoSort.Core;

/// <summary>
/// Moves the originals of a finished merge out of the way, into a dated
/// folder beside each one: "merged_archive_YYYYMMDD" (owner request
/// 2026-10-01). The same shape Unlock gives a locked original
/// (<see cref="Unlock.ArchiveFolderFor"/>), so a folder holds one kind of
/// leftover per day.
///
/// Files are only MOVED. Nothing is deleted, and a name already in the
/// archive is never replaced: the newcomer gets the " (2)" counter. The
/// archive is beside the file, so the move stays on one volume.
/// </summary>
public static class MergeArchive
{
    /// <summary>What every archive folder's name starts with.</summary>
    public const string FolderPrefix = "merged_archive_";

    /// <summary>What happened to one original.</summary>
    /// <param name="Source">Where it was.</param>
    /// <param name="MovedTo">Where it is now; null when it stayed.</param>
    /// <param name="Problem">Why it stayed; empty when it moved.</param>
    public sealed record Moved(string Source, string? MovedTo, string Problem);

    /// <summary>The archive folder for <paramref name="source"/> on
    /// <paramref name="day"/>. Invariant digits: this becomes a real folder
    /// name, and two stations with different Windows locales must land on
    /// the same name for the same day.</summary>
    public static string FolderFor(string source, DateTime day) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!,
            FolderPrefix + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

    /// <summary>Moves each of <paramref name="sources"/> into its archive
    /// folder, one result per file, in the order given. Never throws: a file
    /// that can't be moved stays where it is and says why, and the rest
    /// still move.</summary>
    /// <param name="sources">The files that went into the merge.</param>
    /// <param name="mergedOutput">The PDF the merge wrote, or null. A source
    /// at that path was saved over by the merge (Merge to…): it is the new
    /// document now, so it is left out.</param>
    /// <param name="day">The day the folder is named for.</param>
    public static List<Moved> MoveOriginals(IReadOnlyList<string> sources, string? mergedOutput, DateTime day)
    {
        var results = new List<Moved>();
        foreach (var source in sources)
        {
            if (PathIdentity.Same(source, mergedOutput)) continue;
            results.Add(MoveOne(source, day));
        }
        return results;
    }

    private static Moved MoveOne(string source, DateTime day)
    {
        try
        {
            if (!File.Exists(source))
                return new Moved(source, null, "it is no longer there");
            var folder = FolderFor(source, day);
            Directory.CreateDirectory(folder);
            var target = Collision.FreeFile(Path.Combine(folder, Path.GetFileName(source)));
            File.Move(source, target);   // never overwrites: a name taken meanwhile fails, and both files stay
            return new Moved(source, target, "");
        }
        catch (IOException ex) when (Unlock.IsInUse(ex))
        {
            return new Moved(source, null, Unlock.InUseMessage(source, "move it by hand"));
        }
        catch (Exception ex)
        {
            return new Moved(source, null, ex.Message);
        }
    }
}

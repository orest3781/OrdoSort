namespace OrdoSort.Core;

/// <summary>
/// The " (2)", " (3)", ... counter idiom for picking a free name without
/// overwriting anything. Originally private inside Unlock (see
/// Unlock.CollisionFree, now a one-line delegate to <see cref="FreeFile"/>)
/// and promoted here so the utility tools being added alongside it can
/// share the same behavior instead of re-implementing it. Same caveat as
/// the original: this only proves the name is free AT CHECK TIME — on a
/// shared folder another station can claim it before the caller creates
/// anything there.
/// </summary>
public static class Collision
{
    /// <summary>Free FILE name for <paramref name="target"/>: unchanged if
    /// nothing's there, else "stem (2).ext", "stem (3).ext", ... until one
    /// probes free. Extension-aware — the counter goes before the dot.</summary>
    public static string FreeFile(string target)
    {
        if (!File.Exists(target)) return target;
        var dir = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Same idiom for a DIRECTORY: no extension to keep separate,
    /// so the counter is appended to the whole folder name.</summary>
    public static string FreeDirectory(string target)
    {
        if (!Directory.Exists(target)) return target;
        var parent = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(parent, $"{name} ({n})");
            if (!Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>The collision counter's "is this name taken?" for one
    /// folder. Folders count as taken too: a move can't land on one, and a
    /// folder on the name used to surface as a raw move error (DW-18).
    ///
    /// The first candidate costs one check. Only when it is taken is the
    /// folder listed, once, for names starting the same way, so each further
    /// " (n)" is answered from that list instead of one more trip to a share
    /// per number (DW-17). A name claimed after the listing is still caught
    /// by Commit's last-instant check before the move, and the
    /// name is built again.</summary>
    public static Func<string, bool> TakenIn(string folder)
    {
        HashSet<string>? listed = null;
        var askEachName = false;
        return name =>
        {
            if (listed is not null) return listed.Contains(name);
            if (!Path.Exists(Path.Combine(folder, name))) return false;
            if (askEachName) return true;
            try
            {
                listed = new HashSet<string>(
                    Directory.EnumerateFileSystemEntries(folder, Path.GetFileNameWithoutExtension(name) + "*")
                        .Select(entry => Path.GetFileName(entry)),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The listing is only a shortcut: asking name by name gives
                // the same answers, with more round trips.
                askEachName = true;
            }
            return true;
        };
    }
}

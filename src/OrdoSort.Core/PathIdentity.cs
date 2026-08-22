namespace OrdoSort.Core;

/// <summary>
/// The one answer to "are these two paths the same file".
///
/// Windows resolves a path case-insensitively, and tolerates several
/// spellings that don't name anything different: "." and ".." segments,
/// forward slashes, doubled separators, a trailing separator on a folder.
/// Before this there were three different answers in this codebase —
/// BulkRename.SameFile compared raw strings ordinal-insensitively, the ten
/// tool intake sites each compared raw strings with their own comparer (two
/// of them case-SENSITIVELY, which let one file on disk become two rows
/// headed for a move), and exactly one caller canonicalised first
/// (SettingsViewModel.AdoptRepointedSection, which stays as it is — config
/// repointing is a different concern with a different lifetime).
///
/// Deliberately string-only. Path.GetFullPath does not touch the filesystem,
/// so this stays sync, disk-free, and testable without a temp directory —
/// the same reasoning that makes Naming.BuildTarget take its collision check
/// as an injected predicate rather than calling File.Exists itself.
///
/// What this does NOT resolve, by decision rather than oversight: a mapped
/// drive against its UNC form, a symlink or junction against its target, an
/// 8.3 short name against its long one. Each needs a file handle per path,
/// which would put disk I/O back in the middle of a drop handler. If a real
/// report ever justifies it, it belongs behind these same two members and
/// nothing above them has to change.
/// </summary>
public static class PathIdentity
{
    /// <summary>The comparable form of <paramref name="path"/>, or null when
    /// there isn't one — invalid characters, past the length limit, a
    /// malformed UNC. Never throws: this sits under a drop handler, where an
    /// escaping exception is a crash on a user gesture, so an unusable path
    /// is a value the caller can count rather than something every caller
    /// has to guard.</summary>
    public static string? Canonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            // GetFullPath resolves . and .., turns / into \, and collapses
            // doubled separators — but it does NOT drop a trailing one, so
            // "C:\jobs" and "C:\jobs\" would still compare unequal without
            // the trim. TrimEndingDirectorySeparator leaves a bare root
            // ("C:\", "\\server\share\") alone, which is what we want.
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Whether both paths name the same file. When either side has
    /// no canonical form this falls back to the raw ordinal-insensitive
    /// compare that BulkRename.SameFile applied to every path before this —
    /// an unusable path stays equal to itself rather than becoming equal to
    /// nothing, which is the safer answer for the callers that guard a
    /// File.Move with it.</summary>
    public static bool Same(string? a, string? b)
    {
        var ca = Canonical(a);
        var cb = Canonical(b);
        return ca is not null && cb is not null
            ? string.Equals(ca, cb, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The comparer a HashSet/Dictionary needs to dedupe by path
    /// identity instead of re-deciding the question this class exists to
    /// answer once. QC-12: Session.Extend used to build a raw case-sensitive
    /// HashSet&lt;string&gt; — a second, stricter answer than Same itself gives.</summary>
    public sealed class PathComparer : IEqualityComparer<string>
    {
        public static readonly PathComparer Instance = new();
        public bool Equals(string? a, string? b) => Same(a, b);
        // Must agree with Same's own fallback: hash whichever form Same
        // would actually compare, canonical when there is one.
        public int GetHashCode(string obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(Canonical(obj) ?? obj);
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OrdoSort.Core;

/// <summary>Letter case for a standardised name.</summary>
public enum NameCase
{
    /// <summary>SMITH-JOHN: what Standardise names always did.</summary>
    Upper,
    /// <summary>Smith-John, O'Brien, St.Clair.</summary>
    Title,
    /// <summary>As the letters already are.</summary>
    AsIs,
}

/// <summary>Where the date goes in a standardised name.</summary>
public enum DatePlacement { Front, Back, None }

/// <summary>Which date a standardised name gets.</summary>
public enum DateSource
{
    /// <summary>The date typed in the window (today by default).</summary>
    Typed,
    /// <summary>The 8-digit date the name already starts with; the typed
    /// date for a name that has none.</summary>
    InName,
    /// <summary>The file's last-modified date.</summary>
    Modified,
}

/// <summary>How Standardise names rebuilds a name. The defaults are the
/// fixed rule the tool had before it had controls (upper case, dashes, the
/// typed date in front), so a name comes out the same unless a control is
/// changed.</summary>
/// <param name="TypedDate">YYYYMMDD, already checked with
/// <see cref="BulkRename.IsRealDate"/>; used for <see cref="DateSource.Typed"/>
/// and as the fallback for the other sources.</param>
public sealed record StandardiseOptions(
    string TypedDate,
    NameCase Case = NameCase.Upper,
    BulkRename.SegmentJoin Separator = BulkRename.SegmentJoin.Dash,
    DatePlacement Date = DatePlacement.Front,
    DateSource Source = DateSource.Typed);

/// <summary>
/// Standardise names' rule (owner request 2026-09-29: preview, letter case,
/// separator, date options, segment chips), as pure functions over a file
/// name's stem. What stays fixed, from the tool's original rule
/// (<see cref="BulkRename.TidyStem"/>):
///
/// - A real 8-digit date at the front is taken off before anything else, so
///   standardising a name this tool already produced replaces its date
///   rather than stacking a second one. Eight digits that aren't a real
///   date are part of the name and stay.
/// - Words are what lies between spaces, commas, underscores and dashes.
///   Periods, apostrophes and parentheses are part of a word: O'BRIEN,
///   ST. CLAIR and SMITH (JR) keep them. Treating them as separators would
///   lose them for good, since nothing afterwards can tell an apostrophe
///   that was stripped from one that was never there.
///
/// Everything else is a control: which words to drop (segment chips), the
/// letter case, the separator between words, and the date's place and
/// source.
/// </summary>
public static partial class Standardise
{
    [GeneratedRegex(@"^(?<digits>\d{8})(?=$|[-_ ,])[-_ ,]*")]
    private static partial Regex LeadingDateRegex();

    [GeneratedRegex(@"[-_ ,]+(?<digits>\d{8})[-_ ,]*$")]
    private static partial Regex TrailingDateRegex();

    [GeneratedRegex(@"[-_ ,]+")]
    private static partial Regex WordBreakRegex();

    /// <summary>A stem split the way this tool reads it.</summary>
    /// <param name="LeadingDate">The real date the stem started with, or null.</param>
    /// <param name="Words">What is left, word by word; these are the
    /// segment chips, numbered from 1.</param>
    /// <param name="TrailingDate">The real date the stem ended with, when
    /// it was read with the date at the end; otherwise null.</param>
    public sealed record ReadStem(string? LeadingDate, IReadOnlyList<string> Words, string? TrailingDate = null);

    /// <summary>Splits <paramref name="stem"/> into its leading date (if it
    /// has a real one) and its words. With <paramref name="dateAtEnd"/>, a
    /// real date the stem ends with comes off too, so a name this tool
    /// already gave a date at the end gets it replaced, not a second one.
    /// Only then: otherwise a trailing date is part of the name, as the
    /// original rule had it.</summary>
    public static ReadStem Read(string stem, bool dateAtEnd = false)
    {
        string? leadingDate = null;
        var rest = stem;
        var leading = LeadingDateRegex().Match(rest);
        if (leading.Success && BulkRename.IsRealDate(leading.Groups["digits"].Value))
        {
            leadingDate = leading.Groups["digits"].Value;
            rest = rest[leading.Length..];
        }
        string? trailingDate = null;
        if (dateAtEnd)
        {
            var trailing = TrailingDateRegex().Match(rest);
            if (trailing.Success && BulkRename.IsRealDate(trailing.Groups["digits"].Value))
            {
                trailingDate = trailing.Groups["digits"].Value;
                rest = rest[..trailing.Index];
            }
        }
        var words = WordBreakRegex().Split(rest).Where(word => word.Length > 0).ToList();
        return new ReadStem(leadingDate, words, trailingDate);
    }

    /// <summary>The date a file gets under <paramref name="options"/>.
    /// <paramref name="modified"/> is the file's last-write time, read by the
    /// caller (it is a disk, often network, round trip); ignored unless the
    /// source is <see cref="DateSource.Modified"/>.</summary>
    public static string DateFor(ReadStem read, StandardiseOptions options, DateTime? modified) =>
        options.Source switch
        {
            DateSource.InName => read.LeadingDate ?? read.TrailingDate ?? options.TypedDate,
            DateSource.Modified when modified is { } when => when.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            _ => options.TypedDate,
        };

    /// <summary>The new stem for <paramref name="stem"/>: its words minus
    /// <paramref name="dropped"/> (1-based), in the chosen case, joined by
    /// the chosen separator, with the date in its place. Nothing left and no
    /// date gives "", which the planner reports as "new name would be empty".</summary>
    public static string Stem(string stem, StandardiseOptions options,
        IReadOnlySet<int>? dropped = null, DateTime? modified = null)
    {
        var read = Read(stem, dateAtEnd: options.Date == DatePlacement.Back);
        var kept = new List<string>();
        for (var i = 0; i < read.Words.Count; i++)
            if (dropped is null || !dropped.Contains(i + 1)) kept.Add(ApplyCase(read.Words[i], options.Case));

        var separator = Joiner(options.Separator);
        var name = string.Join(separator, kept);
        if (options.Date == DatePlacement.None) return name;

        var date = DateFor(read, options, modified);
        if (name.Length == 0) return date;
        return options.Date == DatePlacement.Front ? date + separator + name : name + separator + date;
    }

    /// <summary>What goes between words. Keep-original has no meaning here
    /// (the separators are what gets standardised), so it reads as a dash.</summary>
    public static string Joiner(BulkRename.SegmentJoin separator) => separator switch
    {
        BulkRename.SegmentJoin.Underscore => "_",
        BulkRename.SegmentJoin.Space => " ",
        _ => "-",
    };

    /// <summary>A clash counter in the same style as the name: "-2" or "_2"
    /// beside dashes and underscores, Explorer's " (2)" beside spaces.</summary>
    public static BulkRename.CollisionSuffixStyle CounterStyle(BulkRename.SegmentJoin separator) => separator switch
    {
        BulkRename.SegmentJoin.Space => BulkRename.CollisionSuffixStyle.Parenthesized,
        BulkRename.SegmentJoin.Underscore => BulkRename.CollisionSuffixStyle.Underscored,
        _ => BulkRename.CollisionSuffixStyle.Dashed,
    };

    /// <summary>Upper and title case are invariant: a file name must not
    /// come out differently on a PC with a Turkish locale.</summary>
    public static string ApplyCase(string word, NameCase nameCase) => nameCase switch
    {
        NameCase.Upper => word.ToUpperInvariant(),
        NameCase.Title => TitleCase(word),
        _ => word,
    };

    /// <summary>A capital at the start of the word and after a character
    /// that isn't a letter (St.Clair, (Jr)), the rest lower case. After an
    /// apostrophe only when at least two letters follow, so O'Brien and
    /// D'Angelo, but Smith's and Rock'n.</summary>
    public static string TitleCase(string word)
    {
        var result = new StringBuilder(word.Length);
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (!char.IsLetter(c))
            {
                result.Append(c);
                continue;
            }
            var startsPart = i == 0 || !char.IsLetter(word[i - 1]);
            // this letter and at least two more: O'Brien, not Smith'S
            if (startsPart && i > 0 && word[i - 1] == '\'')
                startsPart = LettersFrom(word, i) >= 3;
            result.Append(startsPart ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return result.ToString();
    }

    private static int LettersFrom(string word, int start)
    {
        var count = 0;
        while (start + count < word.Length && char.IsLetter(word[start + count])) count++;
        return count;
    }

    /// <summary>The rename plan for a batch: each file's standardised name,
    /// through <see cref="BulkRename.Plan"/> so a preview gets the same
    /// clash, empty-name, illegal-character and case-only handling Bulk
    /// rename's preview has. <paramref name="modified"/> maps a file to its
    /// last-write time (only needed for <see cref="DateSource.Modified"/>);
    /// <paramref name="dropped"/> maps a file to the words (1-based) left out.</summary>
    public static List<BulkRename.PlannedRename> Plan(
        IReadOnlyList<string> paths, StandardiseOptions options,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? dropped = null,
        IReadOnlyDictionary<string, DateTime>? modified = null)
    {
        var stems = new Dictionary<string, string>(PathIdentity.PathComparer.Instance);
        foreach (var path in paths)
        {
            IReadOnlySet<int>? words = null;
            dropped?.TryGetValue(path, out words);
            DateTime? when = modified is not null && modified.TryGetValue(path, out var w) ? w : null;
            stems[path] = Stem(Path.GetFileNameWithoutExtension(path), options, words, when);
        }
        var plans = BulkRename.Plan(paths, new BulkRename.RenameOp(), overrides: stems,
            suffixStyle: CounterStyle(options.Separator));
        // Plan marks every override "Manual" (a hand-typed name in Bulk
        // rename); here every name is the tool's own
        return plans.Select(p => p with { Manual = false }).ToList();
    }
}

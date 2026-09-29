using System.Globalization;
using System.Text.RegularExpressions;

namespace OrdoSort.Core;

/// <summary>
/// Bulk filename editing: plan -> preview -> execute -> (optional) revert.
/// Operations transform the STEM only; the extension is preserved. Nothing is
/// ever overwritten: a taken target name (on disk, or claimed by an earlier
/// file in the same batch) gets a counter — " (2)" by default (see
/// CollisionSuffixStyle for the one caller that needs a different shape).
/// Every rename is per-file fail-soft.
/// </summary>
public static partial class BulkRename
{
    // Generation tokens, longest-first so VIII wins over VII over VI over V.
    private const string Gen =
        @"(?:JUNIOR|SENIOR|JR\.?|SR\.?|VIII|VII|VI|IX|IV|III|II|X|V|2ND|3RD|4TH|5TH)";

    // Surname particles: separator-joined multi-part last names (VAN_DYKE,
    // DE_LA_CRUZ) are recognized when led by these. Backtracking keeps
    // two-token names right; three full tokens without a particle stay
    // ambiguous and are skipped.
    private const string Particle =
        @"(?:VANDER|VANDEN|VANDE|VAN|VON|DELLA|DEL|DEN|DER|DE|DI|DA|DOS|DAS|DO|DU" +
        @"|LA|LE|LOS|MAC|MC|SAINT|SANTA|SAN|ST|TER|TEN|EL|BIN|IBN)";

    [GeneratedRegex(
        @"^(?<last>(?:" + Particle + @"[_ ]+)*[A-Za-z'\-]+)[_ ]+" +
        @"(?:(?<gen>" + Gen + @")[_ ]+)?" +
        @"(?<first>[A-Za-z'\-]+)" +
        @"(?:[_ ]+(?<gen2>" + Gen + @"))?" +
        @"(?:[_ ]+[A-Za-z])*" +                          // middle initial(s)
        @"[_ ]+(?<m>\d{1,2})[_ ](?<d>\d{1,2})[_ ](?<y>(?:19|20)\d{2})(?:[_ ]|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReviewRegex();

    // TidyStem step 1: a CANDIDATE leading 8-digit run, and the dash right
    // after it if there is one — captured separately so the digits can be
    // checked against IsRealDate before anything is stripped. See
    // TidyStem's own doc comment for why exactly 8, no more and no fewer,
    // is what makes a name this tool already produced idempotent under a
    // second drop, and for why "candidate" — not every 8-digit run is one.
    [GeneratedRegex(@"^(?<digits>\d{8})-?")]
    private static partial Regex LeadingDateRegex();

    /// <summary>Whether <paramref name="yyyyMMdd"/> is a real calendar date
    /// in that exact 8-digit shape — the one test that decides both what
    /// TidyStem's step 1 is allowed to strip and what the Standardise names
    /// date box is allowed to accept (StandardiseNamesViewModel.IsDateValid
    /// delegates here), so the two can never quietly disagree about what
    /// counts as a date. Public, not internal: OrdoSort.Core has no
    /// InternalsVisibleTo grant to OrdoSort.Wpf (only to its own test
    /// assembly), and this is a genuine, small, single-purpose contract —
    /// not an implementation detail — so a public surface is the honest
    /// shape rather than a workaround.</summary>
    public static bool IsRealDate(string yyyyMMdd) =>
        DateTime.TryParseExact(yyyyMMdd, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // TidyStem step 4: collapse a run of two or more dashes (what step 3's
    // substitution leaves behind at "DOE,  JANE" -> "DOE--JANE") to one.
    [GeneratedRegex(@"-{2,}")]
    private static partial Regex DashRunRegex();

    // Segment separators: underscore, space and dash. A run of them is one
    // break, so "A__B" and "A_ B" are two pieces, never an empty one between.
    [GeneratedRegex(@"[_ \-]+")]
    private static partial Regex SegmentSeparatorRegex();

    /// <summary>What goes between the pieces of a name that Bulk rename
    /// rebuilds. <see cref="Original"/> keeps each piece's own separator.</summary>
    public enum SegmentJoin { Original, Dash, Underscore, Space }

    /// <summary>One piece of a filename stem.</summary>
    /// <param name="Text">The piece itself, never empty.</param>
    /// <param name="SeparatorBefore">The separator run in front of it in the
    /// original name ("" for a first piece with nothing before it), kept so
    /// <see cref="SegmentJoin.Original"/> can rejoin exactly.</param>
    public sealed record StemSegment(string Text, string SeparatorBefore);

    public sealed record RenameOp(
        string Find = "", string Replace = "",
        string Prefix = "", string Suffix = "",
        string Case = "keep",       // keep | upper | lower
        SegmentJoin Join = SegmentJoin.Original,
        string DatePrefix = "");    // YYYYMMDD put in front, or "" for none

    public sealed record PlannedRename(
        string Source, string Target, bool Changed, string Note = "", bool Manual = false);

    public sealed record RenameOutcome(string Source, string? Final, string Error = "");

    /// <summary>How Execute spells a taken name's disambiguating counter.
    /// The RULE, not a roster of callers — naming every call site here has
    /// already gone stale twice in two rounds (first missed MatchMerge.cs
    /// entirely, then missed MatchMergeViewModel.DoMerge too), which is the
    /// signal that enumerating them in a comment is the wrong shape: it
    /// goes stale the moment someone adds a caller, silently, since nothing
    /// forces this comment to be touched when that happens. Parenthesized
    /// (Explorer's own " (2)", " (3)", … convention) is the default, and
    /// every caller that passes no argument keeps seeing exactly that,
    /// unchanged. Standardise names opts into Dashed or Underscored so a
    /// counter looks like the rest of the name it is attached to, rather
    /// than bringing back the space and parentheses it just took out.</summary>
    public enum CollisionSuffixStyle { Parenthesized, Dashed, Underscored }

    private static string CollisionSuffix(CollisionSuffixStyle style, int counter) => style switch
    {
        CollisionSuffixStyle.Dashed => $"-{counter}",
        CollisionSuffixStyle.Underscored => $"_{counter}",
        _ => $" ({counter})",
    };

    /// <summary>(last, first) from a review-file filename stem, or null when
    /// the stem doesn't follow the layout. A multi-part last name comes back
    /// space-joined: "VAN DYKE". Match &amp; Merge reads names with it;
    /// Bulk rename stopped using it when its segment controls replaced the
    /// one-click review rebuild (2026-09-25).</summary>
    public static (string Last, string First)? ParseReviewStem(string stem)
    {
        var m = ReviewRegex().Match(stem);
        if (!m.Success) return null;
        var month = int.Parse(m.Groups["m"].Value);
        var day = int.Parse(m.Groups["d"].Value);
        if (month is < 1 or > 12 || day is < 1 or > 31) return null;
        var last = string.Join(' ',
            m.Groups["last"].Value.Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return (last, m.Groups["first"].Value);
    }

    /// <summary>Remove 1-indexed segments (stem split on '-', empties kept —
    /// "a--b" is three segments) plus optionally the last segment. Out-of-range
    /// positions are ignored; the last segment of a one-segment stem stays.</summary>
    internal static string DeleteSegmentsFromStem(
        string stem, IReadOnlyCollection<int> positions, bool deleteLast)
    {
        if (positions.Count == 0 && !deleteLast) return stem;
        var parts = stem.Split('-');
        if (parts.Length <= 1) return stem;
        var drop = new HashSet<int>(positions.Where(p => p >= 1 && p <= parts.Length));
        if (deleteLast) drop.Add(parts.Length);
        if (drop.Count >= parts.Length) return stem;   // deleting every segment is never meaningful
        var kept = parts.Where((_, i) => !drop.Contains(i + 1)).ToArray();
        return string.Join('-', kept);
    }

    /// <summary>The pieces of <paramref name="stem"/>, split at runs of
    /// underscores, spaces and dashes. Separators at the very start or end
    /// don't make empty pieces.</summary>
    public static IReadOnlyList<StemSegment> SplitSegments(string stem)
    {
        var pieces = new List<StemSegment>();
        var position = 0;
        var separator = "";
        foreach (Match match in SegmentSeparatorRegex().Matches(stem))
        {
            if (match.Index > position)
                pieces.Add(new StemSegment(stem[position..match.Index], separator));
            separator = match.Value;
            position = match.Index + match.Length;
        }
        if (position < stem.Length) pieces.Add(new StemSegment(stem[position..], separator));
        return pieces;
    }

    private static string Joiner(SegmentJoin join) => join switch
    {
        SegmentJoin.Underscore => "_",
        SegmentJoin.Space => " ",
        _ => "-",   // Dash, and Original's choice for a date it adds itself
    };

    /// <summary>The stem with <paramref name="dropped"/> (1-based) pieces
    /// removed and the rest joined per <paramref name="join"/>. Left exactly
    /// as it was when nothing is dropped and the join is Original, so a
    /// name nobody asked to change is never tidied behind their back.</summary>
    private static string ApplySegments(string stem, IReadOnlySet<int>? dropped, SegmentJoin join)
    {
        var anyDropped = dropped is { Count: > 0 };
        if (!anyDropped && join == SegmentJoin.Original) return stem;
        var kept = SplitSegments(stem).Where((_, i) => !(anyDropped && dropped!.Contains(i + 1))).ToList();
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < kept.Count; i++)
        {
            if (i > 0) result.Append(join == SegmentJoin.Original ? kept[i].SeparatorBefore : Joiner(join));
            result.Append(kept[i].Text);
        }
        return result.ToString();
    }

    /// <summary>Whether <paramref name="dropped"/> removes every piece of
    /// <paramref name="stem"/>.</summary>
    private static bool DropsEverything(string stem, IReadOnlySet<int>? dropped)
    {
        if (dropped is not { Count: > 0 }) return false;
        var count = SplitSegments(stem).Count;
        return count > 0 && Enumerable.Range(1, count).All(dropped.Contains);
    }

    /// <summary>Order: dropped segments + join -> find/replace -> affixes ->
    /// date prefix -> case.</summary>
    public static string TransformStem(string stem, RenameOp op, IReadOnlySet<int>? dropped = null)
    {
        var outp = ApplySegments(stem, dropped, op.Join);
        if (!string.IsNullOrEmpty(op.Find))
            outp = outp.Replace(op.Find, op.Replace);
        outp = $"{op.Prefix}{outp}{op.Suffix}";
        if (op.DatePrefix.Length > 0)
            outp = $"{op.DatePrefix}{Joiner(op.Join)}{outp}";
        return op.Case switch
        {
            "upper" => outp.ToUpperInvariant(),
            "lower" => outp.ToLowerInvariant(),
            _ => outp,
        };
    }

    /// <summary>Turn a messy dropped-file stem into the owner's
    /// YYYYMMDD-LASTNAME-FIRSTNAME-CONTROLID shape for a supplied
    /// <paramref name="date"/> (8 digits, already validated by the caller —
    /// see IsRealDate — this function trusts it the
    /// same way TransformStem trusts RenameOp.DatePrefix). Pure: no
    /// filesystem, no clock, so the same input always produces the same
    /// output, which is what makes re-dropping a file this tool already
    /// produced a no-op rather than a guess.
    ///
    /// Applied in exactly this order:
    ///  1. Drop a leading 8-digit run, and the dash right after it if there
    ///     is one — but ONLY when those 8 digits parse as a real calendar
    ///     date (IsRealDate: the same DateTime.TryParseExact "yyyyMMdd"
    ///     test the Standardise names date box applies to what the owner
    ///     types). A stem's own case or claim number can happen to be 8
    ///     digits long too, and "the requirements say drop a leading
    ///     8-digit DATE" is read strictly on purpose: stripping any 8
    ///     digits unconditionally would silently and irreversibly destroy
    ///     an ID that isn't a date the moment this tool touches the file,
    ///     with no way to notice from the result alone. When the run is not
    ///     a real date it is left alone as ordinary content, so
    ///     "12345678-REPORT" + "20260901" becomes "20260901-12345678-REPORT"
    ///     — visible and undoable — never "20260901-REPORT" with the ID
    ///     gone. This is still what makes step 5 idempotent: a name this
    ///     tool already produced starts with the batch's own REAL date, and
    ///     without this step a second drop would stack
    ///     "20260115-20251201-SMITH" onto it rather than replace it.
    ///  2. Uppercase, invariant — a filename, not prose, so this must not
    ///     vary by the machine's culture (Turkish "İ"/"ı" is the classic
    ///     trap: current-culture upper/lowercasing depends on the Windows
    ///     locale, which would make the same dropped file produce a
    ///     different name on a different PC).
    ///  3. Replace ONLY spaces, commas and underscores with dashes —
    ///     nothing else. This is the rule most tempting to "improve", and
    ///     the one that must not be: periods, apostrophes and parentheses
    ///     are ordinary, legal characters in a person's name, and Windows
    ///     accepts them in a filename outright, so O'BRIEN, ST. CLAIR and
    ///     SMITH (JR) keep their punctuation untouched. Widen this list —
    ///     treat a period or an apostrophe as a separator too — and
    ///     O'BRIEN silently becomes O-BRIEN: not tidying, data loss,
    ///     because nothing downstream of this function can tell an
    ///     apostrophe that was stripped apart from one that was never
    ///     there.
    ///  4. Collapse runs of dashes to one, and trim dashes from both ends —
    ///     undoes the pile-up step 3 leaves behind ("DOE,  JANE" -> two
    ///     separators back to back) and drops a lone leading or trailing
    ///     separator.
    ///  5. Prepend "{date}-" — unless nothing survived steps 1-4, in which
    ///     case just "{date}" with no dash. A stem that is empty, only a
    ///     date, or made only of characters steps 3/4 remove ("---",
    ///     "   ", a lone "_") collapses to "" by step 4: those steps never
    ///     invent content, so arriving at nothing is correct, not a bug.
    ///     Prepending the separator unconditionally at that point would
    ///     produce "{date}-" — a name that LOOKS complete but is actually
    ///     the date plus a dash pointing at nothing — which is worse than
    ///     the honest "{date}" alone: still a legal, non-empty filename,
    ///     still exactly the batch's date, nothing invented to fill the
    ///     gap. Punctuation OUTSIDE that set (a stem of only periods,
    ///     "...") is never touched by steps 3/4 and survives into the
    ///     result untouched, for the same reason step 3 leaves it alone
    ///     anywhere else.</summary>
    public static string TidyStem(string stem, string date)
    {
        var outp = stem;
        var leadingRun = LeadingDateRegex().Match(stem);
        if (leadingRun.Success && IsRealDate(leadingRun.Groups["digits"].Value))
            outp = stem[leadingRun.Length..];

        outp = outp.ToUpperInvariant();
        outp = outp.Replace(' ', '-').Replace(',', '-').Replace('_', '-');
        outp = DashRunRegex().Replace(outp, "-").Trim('-');
        return outp.Length == 0 ? date : $"{date}-{outp}";
    }

    /// <summary>Guards the File.Move decisions in Plan/Execute/UndoBatch.
    /// Was a raw ordinal-insensitive string compare, which was correct only
    /// as long as every path reaching it happened to be spelled the same way
    /// — now correct by construction. See PathIdentity.</summary>
    private static bool SameFile(string a, string b) => PathIdentity.Same(a, b);

    /// <summary>Compute the batch, in input order. Touches nothing on disk
    /// beyond existence checks. <paramref name="overrides"/> maps a source
    /// path to a hand-edited target STEM that beats the operation;
    /// <paramref name="droppedSegments"/> maps a source path to the 1-based
    /// pieces (<see cref="SplitSegments"/>) to leave out of that file's name.
    /// <paramref name="included"/> is the set of files the operation and the
    /// segment drops apply to (Bulk rename's ticked files); null means every
    /// file. A file outside it keeps its name, unless it has a hand edit —
    /// that is the file's own explicit choice — and its name stays claimed,
    /// so no ticked file is renamed onto it. <paramref name="suffixStyle"/>
    /// is the clash counter's shape, as for <see cref="Execute"/>.</summary>
    public static List<PlannedRename> Plan(
        IEnumerable<string> paths, RenameOp op,
        IReadOnlyDictionary<string, string>? overrides = null,
        IReadOnlyDictionary<string, IReadOnlySet<int>>? droppedSegments = null,
        IReadOnlySet<string>? included = null,
        CollisionSuffixStyle suffixStyle = CollisionSuffixStyle.Parenthesized)
    {
        var planned = new List<PlannedRename>();
        var taken = new Dictionary<string, HashSet<string>>();
        HashSet<string> Claimed(string dir)
        {
            if (!taken.TryGetValue(dir.ToLowerInvariant(), out var claimed))
                taken[dir.ToLowerInvariant()] = claimed = new(StringComparer.OrdinalIgnoreCase);
            return claimed;
        }

        var sources = paths.ToList();
        foreach (var source in sources)
            if (included is not null && !included.Contains(source) && overrides?.ContainsKey(source) != true)
                Claimed(Path.GetDirectoryName(source) ?? "").Add(Path.GetFileName(source));

        foreach (var source in sources)
        {
            var dir = Path.GetDirectoryName(source) ?? "";
            var ext = Path.GetExtension(source);
            var stem = Path.GetFileNameWithoutExtension(source);

            var manual = overrides is not null && overrides.ContainsKey(source);
            if (!manual && included is not null && !included.Contains(source))
            {
                planned.Add(new PlannedRename(source, source, false, ""));
                continue;
            }
            IReadOnlySet<int>? dropped = null;
            droppedSegments?.TryGetValue(source, out dropped);
            if (!manual && DropsEverything(stem, dropped))
            {
                planned.Add(new PlannedRename(source, source, false, "every segment dropped — skipped"));
                continue;
            }
            var newStem = manual ? overrides![source] : TransformStem(stem, op, dropped);

            if (string.IsNullOrWhiteSpace(newStem))
            {
                planned.Add(new PlannedRename(source, source, false,
                    "new name would be empty — skipped", manual));
                continue;
            }
            try
            {
                Naming.RejectIllegal(newStem);   // colon etc: readable skip
            }
            catch (ArgumentException ex)
            {
                planned.Add(new PlannedRename(source, source, false,
                    ex.Message, manual));
                continue;
            }
            var candidate = Path.Combine(dir, newStem + ext);
            // Ordinal, case-SENSITIVE. SameFile is case-insensitive by design,
            // so Case = upper on "smith.pdf" (or Find "smith" -> "Smith")
            // was reported as "unchanged" and silently skipped. SameFile
            // still guards the on-disk collision test in Free() below, which
            // is what lets a case-only target (which File.Exists reports as
            // taken — by the source itself) through without a counter.
            if (string.Equals(Path.GetFileName(candidate), Path.GetFileName(source), StringComparison.Ordinal))
            {
                planned.Add(new PlannedRename(source, source, false, "", manual));
                continue;
            }

            var claimed = Claimed(dir);

            bool Free(string p) =>
                !claimed.Contains(Path.GetFileName(p)) &&
                (!File.Exists(p) || SameFile(p, source));

            var final = candidate;
            var note = "";
            var counter = 2;
            while (!Free(final))
            {
                final = Path.Combine(dir, $"{newStem}{CollisionSuffix(suffixStyle, counter)}{ext}");
                counter++;
            }
            if (!SameFile(final, candidate))
                note = "name was taken — using a counter";
            claimed.Add(Path.GetFileName(final));
            planned.Add(new PlannedRename(source, final, true, note, manual));
        }
        return planned;
    }

    /// <summary>Rename everything changed, per-file fail-soft. A target that
    /// appeared since planning gets the counter bumped at the last instant.
    /// <paramref name="suffixStyle"/> is the counter's own shape — see
    /// CollisionSuffixStyle for why this is a parameter with the app's
    /// existing " (2)" as its default rather than a second code path.</summary>
    public static List<RenameOutcome> Execute(
        IEnumerable<PlannedRename> plans,
        CollisionSuffixStyle suffixStyle = CollisionSuffixStyle.Parenthesized)
    {
        var outcomes = new List<RenameOutcome>();
        foreach (var pr in plans)
        {
            if (!pr.Changed) continue;
            var dir = Path.GetDirectoryName(pr.Target) ?? "";
            var ext = Path.GetExtension(pr.Target);
            var stem = Path.GetFileNameWithoutExtension(pr.Target);
            var target = pr.Target;
            var counter = 2;
            while (File.Exists(target) && !SameFile(target, pr.Source))
            {
                target = Path.Combine(dir, $"{stem}{CollisionSuffix(suffixStyle, counter)}{ext}");
                counter++;
            }
            try
            {
                File.Move(pr.Source, target);
                outcomes.Add(new RenameOutcome(pr.Source, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(new RenameOutcome(pr.Source, null, ex.Message));
            }
        }
        return outcomes;
    }

    /// <summary>Undo a batch (newest first). Returns readable problems, empty
    /// if all names were restored.</summary>
    public static List<string> Revert(IReadOnlyList<RenameOutcome> outcomes) =>
        RevertEach(outcomes).Where(r => r.Problem.Length > 0).Select(r => r.Problem).ToList();

    /// <summary>What happened to one renamed file on undo.</summary>
    /// <param name="Problem">Why it was left under its new name; empty when
    /// it is back under its old one.</param>
    public sealed record RevertOutcome(RenameOutcome Outcome, string Problem)
    {
        public bool Restored => Problem.Length == 0;
    }

    /// <summary><see cref="Revert"/>, file by file (newest first), so a caller
    /// can tell which files went back without asking the disk again: a
    /// name taken again by another file exists either way.</summary>
    public static List<RevertOutcome> RevertEach(IReadOnlyList<RenameOutcome> outcomes)
    {
        var results = new List<RevertOutcome>();
        for (var i = outcomes.Count - 1; i >= 0; i--)
        {
            var o = outcomes[i];
            if (o.Final is null) continue;
            if (File.Exists(o.Source) && !SameFile(o.Source, o.Final))
            {
                results.Add(new RevertOutcome(o,
                    $"{Path.GetFileName(o.Source)} exists again — left as " + Path.GetFileName(o.Final)));
                continue;
            }
            try
            {
                File.Move(o.Final, o.Source);
                results.Add(new RevertOutcome(o, ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new RevertOutcome(o, $"Couldn't restore {Path.GetFileName(o.Source)}: {ex.Message}"));
            }
        }
        return results;
    }
}

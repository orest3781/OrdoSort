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

    // TidyStem step 1: a leading 8-digit date, and the dash right after it
    // if there is one — see TidyStem's own doc comment for why exactly 8,
    // no more and no fewer, is what makes a name this tool already
    // produced idempotent under a second drop.
    [GeneratedRegex(@"^\d{8}-?")]
    private static partial Regex LeadingDateRegex();

    // TidyStem step 4: collapse a run of two or more dashes (what step 3's
    // substitution leaves behind at "DOE,  JANE" -> "DOE--JANE") to one.
    [GeneratedRegex(@"-{2,}")]
    private static partial Regex DashRunRegex();

    public sealed record RenameOp(
        string Find = "", string Replace = "",
        string Prefix = "", string Suffix = "",
        string Case = "keep",       // keep | upper | lower
        string ReceivedDate = "",   // YYYYMMDD -> review-file rebuild
        IReadOnlyCollection<int>? DeleteSegments = null, bool DeleteLastSegment = false);

    public sealed record PlannedRename(
        string Source, string Target, bool Changed, string Note = "", bool Manual = false);

    public sealed record RenameOutcome(string Source, string? Final, string Error = "");

    /// <summary>How Execute spells a taken name's disambiguating counter.
    /// Parenthesized — Explorer's own convention (" (2)", " (3)", …) — is
    /// the default, because every OTHER caller of Execute (the Bulk rename
    /// tool) must keep seeing exactly that, unchanged. The Standardise
    /// names tool passes Dashed instead: Execute's counter is the one
    /// place a collision can hand back a name containing a space and
    /// parentheses — the two characters TidyStem exists to strip out — so
    /// for that tool alone the suffix has to look like the rest of the
    /// name it is attached to.</summary>
    public enum CollisionSuffixStyle { Parenthesized, Dashed }

    private static string CollisionSuffix(CollisionSuffixStyle style, int counter) =>
        style == CollisionSuffixStyle.Dashed ? $"-{counter}" : $" ({counter})";

    /// <summary>(last, first) from a review-file filename stem, or null when
    /// the stem doesn't follow the layout. A multi-part last name comes back
    /// space-joined: "VAN DYKE".</summary>
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

    /// <summary>Order: review-file rebuild -> segment deletion -> find/replace -> affixes -> case.
    /// Returns null when review mode is on and the stem doesn't match the
    /// layout (the caller skips the file, readably).</summary>
    public static string? TransformStem(string stem, RenameOp op)
    {
        var outp = stem;
        if (!string.IsNullOrEmpty(op.ReceivedDate))
        {
            var parts = ParseReviewStem(outp);
            if (parts is null) return null;
            outp = $"{op.ReceivedDate}-{parts.Value.Last.ToUpperInvariant()}" +
                   $"-{parts.Value.First.ToUpperInvariant()}";
        }
        outp = DeleteSegmentsFromStem(outp, op.DeleteSegments ?? Array.Empty<int>(), op.DeleteLastSegment);
        if (!string.IsNullOrEmpty(op.Find))
            outp = outp.Replace(op.Find, op.Replace);
        outp = $"{op.Prefix}{outp}{op.Suffix}";
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
    /// see StandardiseDateWindow.IsValidDate — this function trusts it the
    /// same way TransformStem trusts RenameOp.ReceivedDate). Pure: no
    /// filesystem, no clock, so the same input always produces the same
    /// output, which is what makes re-dropping a file this tool already
    /// produced a no-op rather than a guess.
    ///
    /// Applied in exactly this order:
    ///  1. Drop a leading 8-digit date, and the dash right after it if
    ///     there is one. This is what makes step 5 idempotent: a name this
    ///     tool already produced starts with a date, and without this step
    ///     a second drop would stack "20260115-20251201-SMITH" onto it
    ///     rather than replace it.
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
        var outp = LeadingDateRegex().Replace(stem, "");
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
    /// path to a hand-edited target STEM that beats the operation.</summary>
    public static List<PlannedRename> Plan(
        IEnumerable<string> paths, RenameOp op,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var planned = new List<PlannedRename>();
        var taken = new Dictionary<string, HashSet<string>>();

        foreach (var source in paths)
        {
            var dir = Path.GetDirectoryName(source) ?? "";
            var ext = Path.GetExtension(source);
            var stem = Path.GetFileNameWithoutExtension(source);

            var manual = overrides is not null && overrides.ContainsKey(source);
            var newStem = manual ? overrides![source] : TransformStem(stem, op);

            if (newStem is null)
            {
                planned.Add(new PlannedRename(source, source, false,
                    "doesn't match the review-file layout — skipped"));
                continue;
            }
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
            if (SameFile(Path.GetFileName(candidate), Path.GetFileName(source)))
            {
                planned.Add(new PlannedRename(source, source, false, "", manual));
                continue;
            }

            if (!taken.TryGetValue(dir.ToLowerInvariant(), out var claimed))
                taken[dir.ToLowerInvariant()] = claimed = new(StringComparer.OrdinalIgnoreCase);

            bool Free(string p) =>
                !claimed.Contains(Path.GetFileName(p)) &&
                (!File.Exists(p) || SameFile(p, source));

            var final = candidate;
            var note = "";
            var counter = 2;
            while (!Free(final))
            {
                final = Path.Combine(dir, $"{newStem} ({counter}){ext}");
                counter++;
            }
            if (!SameFile(final, candidate))
                note = "name was taken — using a counter";
            claimed.Add(Path.GetFileName(final));
            planned.Add(new PlannedRename(source, final, true, note, manual));
        }
        return planned;
    }

    /// <summary>Build the Standardise names tool's rename plan straight
    /// from TidyStem, for one already-validated <paramref name="date"/> —
    /// bypassing Plan/TransformStem/RenameOp entirely.
    ///
    /// Why not go through Plan(): TidyStem's fixed (stem, date) shape has
    /// nowhere to plug into RenameOp's five free-form knobs
    /// (Find/Replace/affixes/Case/ReceivedDate) without inventing a field
    /// for a transform none of the others share — ReceivedDate already
    /// means something else (a last/first REBUILD off ParseReviewStem's
    /// regex), so reusing it here would silently change what review mode
    /// does. What Plan() does that this deliberately leaves out: pre-
    /// resolving a same-batch collision against a "taken" set before
    /// anything touches disk. Plan() needs that because it renders a live
    /// PREVIEW ahead of a separate Rename click — the name it shows has to
    /// match what Execute will later do to a filesystem that has not moved
    /// yet. This tool has no such gap: StandardiseNamesViewModel.AddFilesAsync
    /// goes straight from this method to Execute, so Execute's own
    /// sequential, disk-real collision loop (below) is already exactly
    /// right — by the time it reaches the second of two sources that
    /// tidied to the same target, the first has already landed on disk and
    /// File.Exists sees it, bumping the counter for real. Pre-resolving
    /// here would just be a second copy of that same decision, made too
    /// early to be the one that actually counts.
    ///
    /// RejectIllegal stays as a real guard, not vestigial defensiveness:
    /// TidyStem's own postcondition (never empty, never introduces a
    /// character the source filename didn't already legally have) makes it
    /// unreachable for a WELL-FORMED date — but <paramref name="date"/>
    /// itself is this method's caller's responsibility, not TidyStem's
    /// (see TidyStem's own doc comment), so a caller that skips validation
    /// — a test, or code written after this one — still fails readably
    /// here instead of handing File.Move a name Windows will refuse.</summary>
    public static List<PlannedRename> PlanTidy(IEnumerable<string> paths, string date)
    {
        var planned = new List<PlannedRename>();
        foreach (var source in paths)
        {
            var dir = Path.GetDirectoryName(source) ?? "";
            var ext = Path.GetExtension(source);
            var stem = Path.GetFileNameWithoutExtension(source);
            var newStem = TidyStem(stem, date);

            try
            {
                Naming.RejectIllegal(newStem);
            }
            catch (ArgumentException ex)
            {
                planned.Add(new PlannedRename(source, source, false, ex.Message));
                continue;
            }

            var target = Path.Combine(dir, newStem + ext);
            var changed = !SameFile(Path.GetFileName(target), Path.GetFileName(source));
            planned.Add(new PlannedRename(source, changed ? target : source, changed));
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
    public static List<string> Revert(IReadOnlyList<RenameOutcome> outcomes)
    {
        var problems = new List<string>();
        for (var i = outcomes.Count - 1; i >= 0; i--)
        {
            var o = outcomes[i];
            if (o.Final is null) continue;
            if (File.Exists(o.Source) && !SameFile(o.Source, o.Final))
            {
                problems.Add(
                    $"{Path.GetFileName(o.Source)} exists again — left as " +
                    Path.GetFileName(o.Final));
                continue;
            }
            try { File.Move(o.Final, o.Source); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"Couldn't restore {Path.GetFileName(o.Source)}: {ex.Message}");
            }
        }
        return problems;
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OrdoSort.Core;

/// <summary>
/// Match &amp; merge: pair PDFs with a roster spreadsheet by name, then merge
/// each person's Control ID into the filename. Renames go through
/// <see cref="BulkRename"/> so collision counters, never-overwrite, and
/// per-file fail-soft all apply here too.
/// </summary>
public static partial class MatchMerge
{
    [GeneratedRegex(@"^\d{8}-(?<rest>.+)$")]
    private static partial Regex DatedStemRegex();

    [GeneratedRegex(@"-\d+$")]
    private static partial Regex TrailingIdRegex();

    // No accent folding here, deliberately: Norm feeds the EXACT lookup, and
    // folding would make GARCÍA an exact auto-merge match for GARCIA — a
    // widening of the one boundary this tool promises never to widen. Folding
    // lives in CleanTokens, where a hit is only ever a human-confirmed
    // suggestion.
    //
    // QC-16: splitting on the array-less Split(' ', ...) overload used to
    // catch ONLY ASCII 0x20 -- a roster cell pasted from a web portal with a
    // non-breaking space between name segments never normalized to the same
    // key an ASCII-space cell does, and the exact lookup silently missed.
    // MatchMergeViewModel.Tokenize already treats any Unicode whitespace as
    // a separator for headers; the null-separator-array overload here does
    // the same for Split, using framework's own whitespace definition.
    private static string Norm(string name) =>
        string.Join(' ', name.Replace('_', ' ').ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public sealed record Candidate(string ControlId, IReadOnlyDictionary<string, string> Row);

    /// <summary>A token-pass candidate with the reason it qualified, in words —
    /// in this domain a match you can't explain is a match you can't trust.</summary>
    public sealed record Suggestion(Candidate Candidate, string Reason);

    public sealed class Roster
    {
        public required IReadOnlyList<string> Headers { get; init; }
        public Dictionary<(string, string), List<Candidate>> People { get; } = new();

        public IReadOnlyList<Candidate> Lookup(string last, string first) =>
            People.TryGetValue((Norm(last), Norm(first)), out var c)
                ? c : Array.Empty<Candidate>();
    }

    /// <summary>Read a CSV roster (Excel-style UTF-8 BOM tolerated). Throws
    /// <see cref="RosterException"/> with a dialog-ready message on any
    /// problem. Rows missing a name or id are ignored; duplicate rows with the
    /// same name AND id collapse to one candidate.</summary>
    public static Roster LoadRoster(string path, string firstHeader,
        string lastHeader, string controlHeader)
    {
        var rows = ReadTable(path);
        if (rows.Count == 0)
            throw new RosterException("The spreadsheet is empty.");
        var headers = rows[0].Select(h => h.Trim()).ToList();

        // A duplicate or blank column header is only unsafe when it collides
        // with one of the three columns actually being mapped: LoadRoster
        // resolves First/Last/Control by name via IndexOf (first occurrence
        // wins), so if the DUPLICATED name is one of those three, the wrong
        // occurrence's values can silently become "the" answer for who a
        // document is filed against. A stray duplicate or blank column that
        // has nothing to do with First/Last/Control carries no such risk —
        // nobody can even address a specific occurrence of a repeated name
        // through this tool, since headers are always deduplicated by name
        // downstream (the column picker, ChosenColumns) — so refusing the
        // whole file over it only costs the user a roster that used to
        // load. The row builder below breaks any such harmless tie the same
        // way (first occurrence), so behaviour stays deterministic either
        // way; nothing addressable is ever lost.
        var mappedRoles = new[] { firstHeader, lastHeader, controlHeader };
        var unsafeDuplicates = headers.GroupBy(h => h)
            .Where(g => g.Count() > 1 && mappedRoles.Contains(g.Key))
            .Select(g => g.Key)
            .ToList();
        if (unsafeDuplicates.Count > 0)
        {
            var named = unsafeDuplicates.Where(d => d.Length > 0).ToList();
            var blank = unsafeDuplicates.Any(d => d.Length == 0);
            var parts = new List<string>();
            if (named.Count > 0)
                parts.Add("These column headers appear more than once and are needed for the First/Last/" +
                    "Control mapping, so mapping them would silently lose a column's data: " +
                    string.Join(", ", named));
            if (blank)
                parts.Add("A blank column header appears more than once and one of those blanks is " +
                    "needed for the First/Last/Control mapping");
            throw new RosterException(string.Join(". ", parts) + ". Rename the duplicates and try again.");
        }

        var missing = new[] { firstHeader, lastHeader, controlHeader }
            .Where(h => !headers.Contains(h)).ToList();
        if (missing.Count > 0)
            throw new RosterException(
                "These headers aren't in the spreadsheet: " + string.Join(", ", missing) +
                ".\nHeaders found: " + (headers.Count > 0 ? string.Join(", ", headers) : "(none)"));

        var fi = headers.IndexOf(firstHeader);
        var li = headers.IndexOf(lastHeader);
        var ci = headers.IndexOf(controlHeader);

        var roster = new Roster { Headers = headers };
        foreach (var cells in rows.Skip(1))
        {
            string Cell(int i) => i < cells.Count ? cells[i].Trim() : "";
            var first = Cell(fi);
            var last = Cell(li);
            var control = Cell(ci);
            if (first.Length == 0 || last.Length == 0 || control.Length == 0) continue;

            // First occurrence wins — the same rule headers.IndexOf already
            // applies when resolving fi/li/ci above, so a harmless duplicate
            // name (one that isn't First/Last/Control, see the guard above)
            // resolves identically everywhere instead of "whichever column
            // happened to be assigned last".
            var row = new Dictionary<string, string>();
            for (var i = 0; i < headers.Count; i++)
                if (!row.ContainsKey(headers[i])) row[headers[i]] = Cell(i);

            var key = (Norm(last), Norm(first));
            if (!roster.People.TryGetValue(key, out var list))
                roster.People[key] = list = new();
            if (list.All(c => c.ControlId != control))
                list.Add(new Candidate(control, row));
        }
        return roster;
    }

    /// <summary>Possible (last, first) readings of a stem, most likely first.
    /// A trailing "-&lt;id&gt;" is ignored for the reading.</summary>
    public static List<(string Last, string First)> NameCandidates(string stem)
    {
        var readings = new List<(string, string)>();
        var dated = DatedStemRegex().Match(TrailingIdRegex().Replace(stem, ""));
        if (dated.Success)
        {
            var parts = dated.Groups["rest"].Value.Split('-');
            for (var i = parts.Length - 1; i >= 1; i--)
                readings.Add((string.Join('-', parts[..i]), string.Join('-', parts[i..])));
        }
        var review = BulkRename.ParseReviewStem(stem);
        if (review is { } r && !readings.Contains((r.Last, r.First)))
            readings.Add((r.Last, r.First));
        return readings;
    }

    public sealed record MatchResult(
        string Source, string Status, string Last = "", string First = "",
        IReadOnlyList<Candidate>? Candidates = null, string NewStem = "",
        IReadOnlyList<Suggestion>? Suggestions = null, string Note = "");

    public static string MergedStem(string stem, string controlId) => $"{stem}-{controlId}";

    /// <summary>Strips combining marks (accents) via Unicode decomposition —
    /// GARCÍA and GARCIA become the same token here. This is CleanTokens-only:
    /// it feeds suggestions, never the exact lookup, so it can never widen
    /// what auto-merges.</summary>
    private static string FoldAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Splits on '-', '_' and any Unicode whitespace (QC-16 — see
    /// Norm's comment; this is the file-and-roster-shared sibling of that
    /// same fix), then drops single-letter and all-digit tokens — the one
    /// cleaning rule the file side and the roster side must share, so a
    /// hyphenated name never glues into one token on one side and splits
    /// apart on the other.</summary>
    private static IEnumerable<string> CleanTokens(string text) =>
        string.Concat(FoldAccents(text).ToUpperInvariant()
                .Select(c => char.IsWhiteSpace(c) ? ' ' : c))
            .Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1 && !t.All(char.IsDigit));

    /// <summary>Name tokens of a stem, order-free: the date prefix, a trailing
    /// -id, all-digit tokens and single letters are all dropped — none of them
    /// is evidence of WHO a document is about.</summary>
    internal static List<string> NameTokens(string stem)
    {
        var trimmed = TrailingIdRegex().Replace(stem, "");
        var dated = DatedStemRegex().Match(trimmed);
        if (dated.Success) trimmed = dated.Groups["rest"].Value;
        return CleanTokens(trimmed).ToList();
    }

    /// <summary>One edit apart: a letter added, removed or changed, or two
    /// adjacent letters swapped (optimal string alignment, distance 1).
    /// Callers gate on length — short tokens get no typo tolerance.</summary>
    internal static bool WithinOneEdit(string a, string b)
    {
        if (a == b) return false;                       // identical is not a near-miss
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        if (a.Length == b.Length)
        {
            var diffs = new List<int>();
            for (var i = 0; i < a.Length && diffs.Count <= 2; i++)
                if (a[i] != b[i]) diffs.Add(i);
            if (diffs.Count == 1) return true;          // one letter changed
            return diffs.Count == 2 && diffs[1] == diffs[0] + 1
                && a[diffs[0]] == b[diffs[1]] && a[diffs[1]] == b[diffs[0]];   // adjacent swap
        }
        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        for (int i = 0, j = 0, skipped = 0; i <= shorter.Length; )
        {
            if (i == shorter.Length) return true;       // remainder is the inserted letter
            if (j >= longer.Length) return false;
            if (shorter[i] == longer[j]) { i++; j++; }
            else if (skipped++ == 0) j++;               // skip the inserted letter once
            else return false;
        }
        return true;
    }

    /// <summary>The token pass: order-free comparison of name segments,
    /// producing SUGGESTIONS only — nothing here ever auto-merges. At least 2
    /// tokens must agree; at most one of them may be a near-miss.</summary>
    public static List<Suggestion> TokenMatches(string stem, Roster roster)
    {
        var fileTokens = NameTokens(stem);
        if (fileTokens.Count < 2) return new();

        var ranked = new List<(int Rank, int Agreed, Suggestion S)>();
        // Dictionary<TKey,TValue> enumerates in insertion order in practice
        // (absent removals, which Roster never does) — the later DistinctBy
        // relies on that to keep each person's BEST-ranked entry. That's an
        // implementation detail, not a language guarantee: if People ever
        // gains a Remove, this loop needs an explicit sort key instead.
        foreach (var ((last, first), candidates) in roster.People)
        {
            var personTokens = CleanTokens($"{last} {first}").Distinct().ToList();

            var agreed = fileTokens.Intersect(personTokens).ToList();
            var fileLeft = fileTokens.Except(agreed).ToList();
            var personLeft = personTokens.Except(agreed).ToList();

            // at most one near-miss pair, tokens of 4+ letters only
            (string File, string Person)? near = null;
            foreach (var f in fileLeft.Where(t => t.Length >= 4))
            {
                var p = personLeft.FirstOrDefault(t => t.Length >= 4 && WithinOneEdit(f, t));
                if (p is not null) { near = (f, p); break; }
            }
            // The floor counts only substantial agreement: two 3+ letter
            // tokens (a near-miss is 4+ by rule and counts as one). DE + LA
            // agreeing is not evidence of WHO — every DE LA … person in the
            // roster would otherwise qualify, with the shortest surname
            // ranked first. Short tokens still join the reason strings and
            // the same-set/containment classification; they just can't carry
            // a suggestion by themselves.
            var substantial = agreed.Count(t => t.Length >= 3) + (near is null ? 0 : 1);
            if (substantial < 2) continue;
            var agreeCount = agreed.Count + (near is null ? 0 : 1);

            if (near is { } n)
            {
                fileLeft.Remove(n.File);
                personLeft.Remove(n.Person);
            }

            // rank per the spec's tiers; overlap-with-extras-on-both-sides sits
            // between containment and near-miss
            var rank = near is not null ? 4
                : fileLeft.Count == 0 && personLeft.Count == 0 ? 1
                : fileLeft.Count == 0 || personLeft.Count == 0 ? 2
                : 3;

            var reason = rank == 1
                ? "all segments agree"
                : string.Join(", ", agreed) + " agree"
                  + (fileLeft.Count > 0 ? " · " + string.Join(", ", fileLeft) + " not in roster" : "")
                  + (personLeft.Count > 0 ? " · roster also has " + string.Join(", ", personLeft) : "")
                  + (near is { } m ? $" · {m.File} is one letter from {m.Person}" : "");

            foreach (var c in candidates)
                ranked.Add((rank, agreeCount, new Suggestion(c, reason)));
        }
        return ranked
            .OrderBy(r => r.Rank).ThenByDescending(r => r.Agreed)
            .Select(r => r.S)
            // a person listed under two roster keys (e.g. both name-column
            // orders by mistake) must not appear twice; stable sort above
            // means the first occurrence here is always the best-ranked one
            .DistinctBy(s => s.Candidate.ControlId)
            .ToList();
    }

    /// <summary>Whether stem's trailing "-&lt;id&gt;" is trustworthy evidence
    /// that this candidate was already merged in, versus an unrelated file
    /// whose native suffix coincidentally equals someone's control id. The
    /// two cases are byte-identical — no amount of upstream name-matching can
    /// tell them apart, since a name match (exact reading or fuzzy
    /// suggestion) is exactly what got the file to this check either way.
    /// The only lever left is entropy: a one- or two-character id is common
    /// enough to collide by chance and never counts; anything at or past the
    /// tests' own shortest real id (3) is specific enough that the
    /// coincidence becomes implausible — not impossible, see
    /// <see cref="AlreadyMergedNote"/>.</summary>
    private const int MinTrustworthyIdLength = 3;

    private static bool AlreadyCarries(string stem, string controlId) =>
        controlId.Length >= MinTrustworthyIdLength
        && stem.EndsWith($"-{controlId}", StringComparison.Ordinal);

    /// <summary>The note MatchResult carries for status "already". A suffix
    /// match is a coincidence check, not proof (see <see cref="AlreadyCarries"/>)
    /// — the wording asks the human to verify rather than asserting the file
    /// was genuinely merged before.</summary>
    public const string AlreadyMergedNote =
        "already carries this id — verify it was filed, not a coincidence";

    /// <summary>Classify every file, in input order. Touches nothing.</summary>
    public static List<MatchResult> MatchFiles(IEnumerable<string> paths, Roster roster)
    {
        var results = new List<MatchResult>();
        foreach (var source in paths)
        {
            var stem = Path.GetFileNameWithoutExtension(source);
            var readings = NameCandidates(stem);

            // Every reading that hits the roster, not just the first one: two
            // roster rows can each align under a DIFFERENT split of the same
            // hyphenated stem — the surname/first-name boundary is genuinely
            // ambiguous — and stopping at the first hit used to merge onto
            // whichever person happened to read first, silently. Union by
            // ControlId so the same person matched under two readings still
            // counts once.
            var hits = new List<(string Last, string First, IReadOnlyList<Candidate> Found)>();
            foreach (var (last, first) in readings)
            {
                var found = roster.Lookup(last, first);
                if (found.Count > 0) hits.Add((last, first, found));
            }

            if (hits.Count == 0)
            {
                // every reading missed the exact lookup — the token pass may
                // still SUGGEST, which reaches the review screen and never
                // merges by itself
                var suggestions = TokenMatches(stem, roster);
                var (dl, df) = readings.Count > 0 ? readings[0] : ("", "");
                if (suggestions.Any(s => AlreadyCarries(stem, s.Candidate.ControlId)))
                    // mirrors the exact path's already-merged guard below: a
                    // file that already carries a suggested candidate's id was
                    // confirmed once already and must never re-suggest itself
                    results.Add(new MatchResult(source, "already", dl, df, Note: AlreadyMergedNote));
                else if (suggestions.Count > 0)
                    results.Add(new MatchResult(source, "suggested", dl, df,
                        Suggestions: suggestions));
                else if (readings.Count > 0)
                    results.Add(new MatchResult(source, "no_match", readings[0].Last, readings[0].First));
                else
                    results.Add(new MatchResult(source, "no_name"));
                continue;
            }

            var (hl, hf, _) = hits[0];   // most-likely reading, for display only
            var candidates = hits.SelectMany(h => h.Found).DistinctBy(c => c.ControlId).ToList();
            if (candidates.Any(c => AlreadyCarries(stem, c.ControlId)))
                results.Add(new MatchResult(source, "already", hl, hf, candidates, Note: AlreadyMergedNote));
            else if (candidates.Count == 1)
                results.Add(new MatchResult(source, "merge", hl, hf, candidates,
                    MergedStem(stem, candidates[0].ControlId)));
            else
                // either one reading's roster key alone covers >1 person
                // (unchanged behaviour), or two DIFFERENT readings each
                // resolved to a different person — either way, more than one
                // distinct person is a live possibility and this is not this
                // tool's call to make silently
                results.Add(new MatchResult(source, "ambiguous", hl, hf, candidates));
        }
        return results;
    }

    /// <summary>Rename every unambiguous match, with the full bulk-rename safety.</summary>
    public static List<BulkRename.RenameOutcome> ExecuteMerges(IEnumerable<MatchResult> results)
    {
        var toDo = results.Where(r => r.Status == "merge").ToList();
        var overrides = toDo.ToDictionary(r => r.Source, r => r.NewStem);
        var plans = BulkRename.Plan(toDo.Select(r => r.Source), new BulkRename.RenameOp(), overrides);
        return BulkRename.Execute(plans);
    }

    /// <summary>A single review decision: merge this control id into the file.</summary>
    public static List<BulkRename.RenameOutcome> MergeOne(string source, string controlId)
    {
        var overrides = new Dictionary<string, string>
        {
            [source] = MergedStem(Path.GetFileNameWithoutExtension(source), controlId),
        };
        var plans = BulkRename.Plan(new[] { source }, new BulkRename.RenameOp(), overrides);
        return BulkRename.Execute(plans);
    }

    /// <summary>First row of the spreadsheet — the headers — for either
    /// format, with the same dialog-ready errors as a full load. This is also
    /// the ONLY header preview: the view model used to read the first line
    /// with a naive comma split, which misparsed quoted headers.</summary>
    public static List<string> ReadHeaders(string path)
    {
        var rows = ReadTable(path);
        if (rows.Count == 0) throw new RosterException("The spreadsheet is empty.");
        return rows[0].Select(h => h.Trim()).ToList();
    }

    private static List<List<string>> ReadTable(string path)
    {
        // old binary/zip-hybrid Excel formats aren't xlsx — reading them as
        // either CSV text or a zip of XML produces mojibake, not a readable
        // error, so reject them by extension before either path is tried
        if (path.EndsWith(".xls", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase))
            throw new RosterException("That format isn't supported — save it as .xlsx or .csv.");
        try
        {
            return Csv.ReadTable(path);
        }
        catch (RosterException) { throw; }
        catch (Exception ex)
        {
            throw new RosterException($"Couldn't read the spreadsheet: {ex.Message}");
        }
    }
}

public sealed class RosterException : Exception
{
    public RosterException(string message) : base(message) { }
}

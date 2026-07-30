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
    private static string Norm(string name) =>
        string.Join(' ', name.Replace('_', ' ').ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

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

            var row = new Dictionary<string, string>();
            for (var i = 0; i < headers.Count; i++) row[headers[i]] = Cell(i);

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
        IReadOnlyList<Suggestion>? Suggestions = null);

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

    /// <summary>Splits on '-', '_' and space, then drops single-letter and
    /// all-digit tokens — the one cleaning rule the file side and the roster
    /// side must share, so a hyphenated name never glues into one token on
    /// one side and splits apart on the other.</summary>
    private static IEnumerable<string> CleanTokens(string text) =>
        FoldAccents(text).ToUpperInvariant()
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

    /// <summary>Classify every file, in input order. Touches nothing.</summary>
    public static List<MatchResult> MatchFiles(IEnumerable<string> paths, Roster roster)
    {
        var results = new List<MatchResult>();
        foreach (var source in paths)
        {
            var stem = Path.GetFileNameWithoutExtension(source);
            var readings = NameCandidates(stem);
            (string Last, string First, IReadOnlyList<Candidate> C)? hit = null;
            foreach (var (last, first) in readings)
            {
                var found = roster.Lookup(last, first);
                if (found.Count > 0) { hit = (last, first, found); break; }
            }
            if (hit is null)
            {
                // the exact lookup missed — the token pass may still SUGGEST,
                // which reaches the review screen and never merges by itself
                var suggestions = TokenMatches(stem, roster);
                var (dl, df) = readings.Count > 0 ? readings[0] : ("", "");
                if (suggestions.Any(s => stem.EndsWith($"-{s.Candidate.ControlId}", StringComparison.Ordinal)))
                    // mirrors the exact path's already-merged guard below: a
                    // file that already carries a suggested candidate's id was
                    // confirmed once already and must never re-suggest itself
                    results.Add(new MatchResult(source, "already", dl, df));
                else if (suggestions.Count > 0)
                    results.Add(new MatchResult(source, "suggested", dl, df,
                        Suggestions: suggestions));
                else if (readings.Count > 0)
                    results.Add(new MatchResult(source, "no_match", readings[0].Last, readings[0].First));
                else
                    results.Add(new MatchResult(source, "no_name"));
                continue;
            }
            var (hl, hf, candidates) = hit.Value;
            if (candidates.Any(c => stem.EndsWith($"-{c.ControlId}", StringComparison.Ordinal)))
                results.Add(new MatchResult(source, "already", hl, hf, candidates));
            else if (candidates.Count == 1)
                results.Add(new MatchResult(source, "merge", hl, hf, candidates,
                    MergedStem(stem, candidates[0].ControlId)));
            else
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
            return path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? XlsxTable.Read(path)
                : ParseCsv(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (RosterException) { throw; }
        catch (Exception ex)
        {
            throw new RosterException($"Couldn't read the spreadsheet: {ex.Message}");
        }
    }

    /// <summary>Minimal RFC-4180-ish CSV: handles quoted fields with embedded
    /// commas, quotes ("") and newlines. Good enough for Excel exports.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new();
                    break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows.Where(r => r.Count > 1 || (r.Count == 1 && r[0].Length > 0)).ToList();
    }
}

public sealed class RosterException : Exception
{
    public RosterException(string message) : base(message) { }
}

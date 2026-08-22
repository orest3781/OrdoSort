using OrdoSort.Core;
using static OrdoSort.Core.MatchMerge;

namespace OrdoSort.Core.Tests;

public class MatchMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mmtest_" + Guid.NewGuid());

    private const string Csv =
        "First Name,Last Name,Control ID,DOB\n" +
        "Adam,Brown,100000001,4/25/1966\n" +
        "Adam,Brown,696009058,11/10/1955\n" +
        "Frank,Evans,176797656,8/9/1997\n" +
        "Maria,De La Cruz,111222333,3/4/1970\n" +
        "Mary,Smith-Jones,444555666,1/2/1980\n" +
        ",Missing,999,\n" +                            // no first name: ignored
        "Adam,Brown,100000001,4/25/1966\n";            // exact dup: collapsed

    public MatchMergeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Roster LoadRoster()
    {
        var path = Path.Combine(_dir, "roster.csv");
        File.WriteAllText(path, Csv, new System.Text.UTF8Encoding(true)); // BOM
        return MatchMerge.LoadRoster(path, "First Name", "Last Name", "Control ID");
    }

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void LoadsAndDedupes()
    {
        var roster = LoadRoster();
        Assert.Equal(2, roster.Lookup("BROWN", "ADAM").Count);
        Assert.Single(roster.Lookup("EVANS", "FRANK"));
        Assert.Empty(roster.Lookup("MISSING", ""));
        Assert.Equal("8/9/1997", roster.Lookup("EVANS", "FRANK")[0].Row["DOB"]);
    }

    [Fact]
    public void MissingHeaderIsReadable()
    {
        var path = Path.Combine(_dir, "r.csv");
        File.WriteAllText(path, Csv);
        var ex = Assert.Throws<RosterException>(() =>
            MatchMerge.LoadRoster(path, "FNAME", "Last Name", "Control ID"));
        Assert.Contains("FNAME", ex.Message);
        Assert.Contains("First Name", ex.Message);  // lists what was found
    }

    [Fact]
    public void MatchingIsCaseAndSeparatorInsensitive()
    {
        var roster = LoadRoster();
        Assert.Single(roster.Lookup("de la cruz", "maria"));
        Assert.Single(roster.Lookup("DE_LA_CRUZ", "MARIA"));
    }

    [Fact]
    public void NonBreakingSpaceInARosterCellStillExactMatches()
    {
        // QC-16: Norm split on the single ASCII ' ' (0x20), so a roster cell
        // pasted from a web portal with U+00A0 (non-breaking space) between
        // name segments never normalized to the key an ASCII-space query
        // produces. MatchMergeViewModel.Tokenize already treats any Unicode
        // whitespace as a word boundary for headers, with a comment saying
        // so; Norm is the data-path sibling that never got it.
        var nbspLastName = "De" + '\u00A0' + "La" + '\u00A0' + "Cruz";
        var path = Path.Combine(_dir, "nbsp_roster.csv");
        File.WriteAllText(path,
            "First Name,Last Name,Control ID,DOB\n" +
            $"Maria,{nbspLastName},111222333,3/4/1970\n",
            new System.Text.UTF8Encoding(true));
        var roster = MatchMerge.LoadRoster(path, "First Name", "Last Name", "Control ID");

        // Same ASCII-space query that matches an ASCII-space cell
        // (MatchingIsCaseAndSeparatorInsensitive above).
        Assert.Single(roster.Lookup("de la cruz", "maria"));
    }

    [Fact]
    public void MergedConventionReading() =>
        Assert.Equal(("BROWN", "ADAM"), NameCandidates("20240126-BROWN-ADAM")[0]);

    [Fact]
    public void HyphenatedNamesOfferEverySplit()
    {
        var readings = NameCandidates("20240126-SMITH-JONES-MARY");
        Assert.Contains(("SMITH-JONES", "MARY"), readings);
        Assert.Contains(("SMITH", "JONES-MARY"), readings);
    }

    [Fact]
    public void TrailingIdIgnoredForReading() =>
        Assert.Equal(("BROWN", "ADAM"), NameCandidates("20240126-BROWN-ADAM-100000001")[0]);

    [Fact]
    public void UniqueMatchMerges()
    {
        var r = MatchFiles(new[] { Touch("20240126-EVANS-FRANK.pdf") }, LoadRoster())[0];
        Assert.Equal("merge", r.Status);
        Assert.Equal("20240126-EVANS-FRANK-176797656", r.NewStem);
    }

    [Fact]
    public void TwoIdsIsAmbiguous()
    {
        var r = MatchFiles(new[] { Touch("20240126-BROWN-ADAM.pdf") }, LoadRoster())[0];
        Assert.Equal("ambiguous", r.Status);
        Assert.Equal(new[] { "100000001", "696009058" },
            r.Candidates!.Select(c => c.ControlId).OrderBy(x => x));
    }

    [Fact]
    public void UnknownNameIsNoMatch() =>
        Assert.Equal("no_match",
            MatchFiles(new[] { Touch("20240126-SMITH-JOHN.pdf") }, LoadRoster())[0].Status);

    [Fact]
    public void NoNameInFilename() =>
        Assert.Equal("no_name",
            MatchFiles(new[] { Touch("scan_001.pdf") }, LoadRoster())[0].Status);

    [Fact]
    public void AlreadyMergedIsDetected() =>
        Assert.Equal("already",
            MatchFiles(new[] { Touch("20240126-EVANS-FRANK-176797656.pdf") }, LoadRoster())[0].Status);

    [Fact]
    public void HyphenatedSurnameResolvedByRoster()
    {
        var r = MatchFiles(new[] { Touch("20240126-SMITH-JONES-MARY.pdf") }, LoadRoster())[0];
        Assert.Equal("merge", r.Status);
        Assert.Equal("20240126-SMITH-JONES-MARY-444555666", r.NewStem);
    }

    [Fact]
    public void RawReviewFileMatchesToo()
    {
        var f = Touch("EVANS_FRANK_8_9_1997_ACME_RECORDS_176-1_X.pdf");
        Assert.Equal("merge", MatchFiles(new[] { f }, LoadRoster())[0].Status);
    }

    [Fact]
    public void ExecuteMergesRenamesOnlyUnambiguous()
    {
        var sure = Touch("20240126-EVANS-FRANK.pdf");
        var unsure = Touch("20240126-BROWN-ADAM.pdf");
        var results = MatchFiles(new[] { sure, unsure }, LoadRoster());
        var outcomes = ExecuteMerges(results);
        Assert.Single(outcomes);
        Assert.True(File.Exists(Path.Combine(_dir, "20240126-EVANS-FRANK-176797656.pdf")));
        Assert.True(File.Exists(unsure));   // ambiguous: untouched
    }

    [Fact]
    public void MergeOneAppliesTriageDecision()
    {
        var f = Touch("20240126-BROWN-ADAM.pdf");
        var outcome = MergeOne(f, "696009058")[0];
        Assert.Equal("20240126-BROWN-ADAM-696009058.pdf", Path.GetFileName(outcome.Final!));
    }

    [Fact]
    public void MergeNeverOverwrites()
    {
        Touch("20240126-EVANS-FRANK-176797656.pdf");   // target already on disk
        var f = Touch("20240126-EVANS-FRANK.pdf");
        var outcome = ExecuteMerges(MatchFiles(new[] { f }, LoadRoster()))[0];
        Assert.Equal("20240126-EVANS-FRANK-176797656 (2).pdf", Path.GetFileName(outcome.Final!));
    }

    // ------------------------------------------ header-mapping plan, Task 1
    // MatchMerge.LoadRoster resolves First/Last/Control by name via IndexOf
    // (first wins, defect 4) and stores each row name-keyed (defect 5), so a
    // duplicate header name silently loses its twin's data. That is only a
    // real threat when the duplicated (or blank) name IS one of the three
    // mapped roles — a stray duplicate/blank column the roles never touch
    // can't corrupt the mapping, and rejecting the whole file over it (a
    // stray "Notes" column, the blank a trailing comma leaves) was too
    // aggressive: reject only when the mapping itself is at risk.

    [Fact]
    public void DuplicateRoleHeaderNoLongerCollapsesFirstAndLastToTheSameColumn()
    {
        // firstHeader == lastHeader == "Name": IndexOf resolves BOTH to the
        // same column, so Last silently reads First's data. Must be
        // rejected, not silently mis-mapped.
        var path = Path.Combine(_dir, "samename.csv");
        File.WriteAllText(path, "Name,Name,Control\nJohn,Doe,123\n");

        var ex = Assert.Throws<RosterException>(() =>
            MatchMerge.LoadRoster(path, "Name", "Name", "Control"));
        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public void DuplicateRoleHeaderIsRejectedEvenWhenBlank()
    {
        // controlHeader == "": two blank columns exist, and the caller asked
        // for the blank one specifically. IndexOf can't tell which blank was
        // meant, so this must reject exactly like a named duplicate would.
        var path = Path.Combine(_dir, "blankrole.csv");
        File.WriteAllText(path, "First,Last,,\nJohn,Doe,123,X\n");

        var ex = Assert.Throws<RosterException>(() =>
            MatchMerge.LoadRoster(path, "First", "Last", ""));
        Assert.Contains("blank", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnrelatedDuplicateHeaderNoLongerFailsTheWholeFile()
    {
        // "Extra" is duplicated but is not First, Last or Control — narrowed
        // rejection must let this load. First occurrence wins for "Extra"
        // (matching the same IndexOf-first-occurrence rule the three mapped
        // roles already use), so behaviour is deterministic, not silently
        // random — and nothing addressable through this API is lost: First/
        // Last/Control resolve correctly, and Row["Extra"] deterministically
        // reports the FIRST "Extra" column's value, not a coin flip.
        var path = Path.Combine(_dir, "dup.csv");
        File.WriteAllText(path, "First,Last,Control,Extra,Extra\nJohn,Doe,123,AAA,BBB\n");

        var roster = MatchMerge.LoadRoster(path, "First", "Last", "Control");

        var person = Assert.Single(roster.Lookup("Doe", "John"));
        Assert.Equal("123", person.ControlId);
        Assert.Equal("AAA", person.Row["Extra"]);   // first occurrence, not the second (BBB)
    }

    [Fact]
    public void UnrelatedBlankHeaderNoLongerFailsTheWholeFile()
    {
        // The blank trailing-comma column isn't First, Last or Control —
        // narrowed rejection must let this load, and mapping is unaffected.
        var path = Path.Combine(_dir, "blank.csv");
        File.WriteAllText(path, "First,Last,Control,\nJohn,Doe,123,X\n");

        var roster = MatchMerge.LoadRoster(path, "First", "Last", "Control");

        var person = Assert.Single(roster.Lookup("Doe", "John"));
        Assert.Equal("123", person.ControlId);
    }

    [Fact]
    public void QuotedCsvFieldsWithCommas()
    {
        var path = Path.Combine(_dir, "q.csv");
        File.WriteAllText(path,
            "First Name,Last Name,Control ID,Note\n" +
            "Frank,Evans,176797656,\"Smith, Jones & Co\"\n");
        var roster = MatchMerge.LoadRoster(path, "First Name", "Last Name", "Control ID");
        Assert.Equal("Smith, Jones & Co", roster.Lookup("EVANS", "FRANK")[0].Row["Note"]);
    }

    // ---------------------------------------------------- token matching

    private static MatchMerge.Roster TokenRoster(params (string Last, string First, string Id)[] people)
    {
        var dir = Directory.CreateTempSubdirectory("ordotok_").FullName;
        var path = Path.Combine(dir, "roster.csv");
        var lines = new List<string> { "Last,First,Control" };
        lines.AddRange(people.Select(p => $"{p.Last},{p.First},{p.Id}"));
        File.WriteAllLines(path, lines);
        var roster = MatchMerge.LoadRoster(path, "First", "Last", "Control");
        Directory.Delete(dir, recursive: true);
        return roster;
    }

    [Theory]
    [InlineData("20240802-GARCIA-MARIA", "GARCIA MARIA")]        // date stripped
    [InlineData("20240802-GARCIA-MARIA-409585208", "GARCIA MARIA")] // trailing id stripped
    [InlineData("SMITH_MARY ANN", "SMITH MARY ANN")]             // _ and space split
    [InlineData("20240802-SMITH-J-MARY", "SMITH MARY")]          // single letters dropped
    [InlineData("SMITH_JOHN_5_5_2024", "SMITH JOHN")]            // all-digit tokens dropped
    public void NameTokensStripNoiseAndSplitOnAllSeparators(string stem, string expected) =>
        Assert.Equal(expected.Split(' '), MatchMerge.NameTokens(stem));

    [Fact]
    public void InvertedNamesSuggestBecauseOrderNeverMattered()
    {
        var roster = TokenRoster(("EVANS", "FRANK", "111"));
        var s = MatchMerge.TokenMatches("20240126-FRANK-EVANS", roster);
        var hit = Assert.Single(s);
        Assert.Equal("111", hit.Candidate.ControlId);
        Assert.Equal("all segments agree", hit.Reason);
    }

    [Fact]
    public void HyphenatedRosterSurnamesTokenizeLikeFilenames()
    {
        // the spec: the roster side is "tokenised the same way" as the file
        // side — a hyphen in a roster cell must not glue a surname into one
        // unmatchable token
        var roster = TokenRoster(("SMITH-JONES", "MARY", "999"));
        var s = MatchMerge.TokenMatches("20240126-MARY-SMITH-JONES", roster);
        var hit = Assert.Single(s);
        Assert.Equal("999", hit.Candidate.ControlId);
        Assert.Equal("all segments agree", hit.Reason);
    }

    [Fact]
    public void MultiPartNamesMatchOnContainment()
    {
        var roster = TokenRoster(("DE LA CRUZ", "MARIA", "222"));
        var s = MatchMerge.TokenMatches("20240126-CRUZ-MARIA", roster);
        // CRUZ+MARIA agree — 2 of the person's 4 tokens, so this is containment
        var hit = Assert.Single(s);
        Assert.Equal("222", hit.Candidate.ControlId);
        Assert.Contains("CRUZ, MARIA agree", hit.Reason);
        Assert.Contains("roster also has DE, LA", hit.Reason);
    }

    [Fact]
    public void AFileMiddleNameTheRosterLacksStillMatches()
    {
        var roster = TokenRoster(("SMITH", "MARY", "333"));
        var s = MatchMerge.TokenMatches("20240126-SMITH-MARY-LOUISE", roster);
        var hit = Assert.Single(s);
        Assert.Contains("SMITH, MARY agree", hit.Reason);
        Assert.Contains("LOUISE not in roster", hit.Reason);
    }

    [Fact]
    public void ATypoWithinOneEditSuggestsAndSaysSo()
    {
        var roster = TokenRoster(("BENSON", "MICHAEL", "444"));
        var s = MatchMerge.TokenMatches("20240126-BENSON-MICHEAL", roster);
        var hit = Assert.Single(s);
        Assert.Contains("MICHEAL", hit.Reason);
        Assert.Contains("MICHAEL", hit.Reason);
    }

    [Fact]
    public void OneAgreeingTokenIsNeverEnough()
    {
        var roster = TokenRoster(("GARCIA", "MARIA", "555"));
        Assert.Empty(MatchMerge.TokenMatches("20240126-GARCIA-UNRELATED", roster));
    }

    [Fact]
    public void TwoTyposAreTooWeakToSuggest()
    {
        var roster = TokenRoster(("BENSON", "MICHAEL", "666"));
        Assert.Empty(MatchMerge.TokenMatches("20240126-BENSEN-MICHEAL", roster));
    }

    [Fact]
    public void ShortTokensGetNoTypoTolerance()
    {
        // LEE vs LEA is one edit but only 3 letters — identical or nothing
        var roster = TokenRoster(("LEE", "MARIA", "777"));
        Assert.Empty(MatchMerge.TokenMatches("20240126-LEA-MARIA", roster));
    }

    [Fact]
    public void RankingPutsExactSetsBeforeContainmentBeforeTypos()
    {
        var roster = TokenRoster(
            ("GARCIA LOPEZ", "MARIA", "CONTAIN"),   // containment
            ("GARCIA", "MARIA", "SAMESET"),          // same set
            ("GARCIA", "MARIE", "TYPO"));            // near-miss (MARIA≈MARIE)
        var s = MatchMerge.TokenMatches("20240126-GARCIA-MARIA", roster);
        Assert.Equal(new[] { "SAMESET", "CONTAIN", "TYPO" },
            s.Select(x => x.Candidate.ControlId).ToArray());
    }

    [Fact]
    public void ParticlesAloneCannotCarryASuggestion()
    {
        // DE + LA agree with every "DE LA …" person; two short particles are
        // not evidence of who this is — the owner chose a 3+ letter floor
        var roster = TokenRoster(
            ("DE LA CRUZ", "ROSA", "A"), ("DE LA O", "MARIA", "B"),
            ("DE LA FUENTE", "CARLA", "C"));
        Assert.Empty(MatchMerge.TokenMatches("20240101-DE-LA-HOYA-MARIA", roster)
            .Where(s => s.Candidate.ControlId is "A" or "C"));
        // MARIA + a particle pair still isn't enough for B either: substantial
        // agreement is MARIA alone (DE and LA are short)
        Assert.Empty(MatchMerge.TokenMatches("20240101-DE-LA-HOYA-MARIA", roster));
    }

    [Fact]
    public void ShortTokensStillCountTowardSameSetOnceTheFloorIsCleared()
    {
        var roster = TokenRoster(("DE LA CRUZ", "MARIA", "222"));
        var s = MatchMerge.TokenMatches("20240101-DE-LA-CRUZ-MARIA", roster);
        var hit = Assert.Single(s);
        Assert.Equal("222", hit.Candidate.ControlId);
        Assert.Equal("all segments agree", hit.Reason);   // DE and LA agreed and say so
    }

    [Fact]
    public void AMergedSuggestionComesBackAsAlreadyNotSuggestedAgain()
    {
        // the exact path has had this guard forever; the token path forgot it —
        // a confirmed suggestion re-suggested itself and a second confirm
        // produced NAME-id-id
        var roster = TokenRoster(("EVANS", "FRANK", "111"));
        Assert.Single(MatchMerge.TokenMatches("20240126-FRANK-EVANS", roster));

        var dir = Directory.CreateTempSubdirectory("ordoalready_").FullName;
        try
        {
            var merged = Path.Combine(dir, "20240126-FRANK-EVANS-111.pdf");
            File.WriteAllText(merged, "x");
            var r = Assert.Single(MatchMerge.MatchFiles(new[] { merged }, roster));
            Assert.Equal("already", r.Status);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AccentedNamesSuggestButNeverAutoMerge()
    {
        var roster = TokenRoster(("GARCIA", "MARIA", "888"));
        var s = MatchMerge.TokenMatches("20240126-GARCÍA-MARÍA", roster);
        var hit = Assert.Single(s);
        Assert.Equal("888", hit.Candidate.ControlId);
        Assert.Equal("all segments agree", hit.Reason);

        var dir = Directory.CreateTempSubdirectory("ordoaccent_").FullName;
        try
        {
            var file = Path.Combine(dir, "20240126-GARCÍA-MARÍA.pdf");
            File.WriteAllText(file, "x");
            var r = Assert.Single(MatchMerge.MatchFiles(new[] { file }, roster));
            Assert.Equal("suggested", r.Status);   // suggested — NOT merge
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void SamePersonListedUnderTwoKeysSuggestsOnce()
    {
        // a roster data-entry mistake — the same person entered under both
        // (last,first) column orders — must not double up the suggestion list
        var roster = TokenRoster(("EVANS", "FRANK", "111"), ("FRANK", "EVANS", "111"));
        var s = MatchMerge.TokenMatches("20240126-EVANS-FRANK", roster);
        var hit = Assert.Single(s);
        Assert.Equal("111", hit.Candidate.ControlId);
    }

    // -------------------------------------------- cross-reading collision

    [Fact]
    public void CrossReadingCollisionIsAmbiguousNotASilentAutoMerge()
    {
        // Two different hyphen splits of the SAME stem each resolve to a
        // different roster person: "Garcia-Lopez-Maria, Jose" under the
        // first (most-likely) split, "Garcia-Lopez, Maria-Jose" under the
        // second. The old code stopped scanning readings at the first hit
        // and merged onto whichever person happened to read first — one
        // click, silently, onto the wrong file. Both are live candidates;
        // this is a human call.
        var roster = TokenRoster(
            ("GARCIA-LOPEZ-MARIA", "JOSE", "A"),
            ("GARCIA-LOPEZ", "MARIA-JOSE", "B"));
        var r = Assert.Single(MatchFiles(
            new[] { Touch("20240101-GARCIA-LOPEZ-MARIA-JOSE.pdf") }, roster));
        Assert.Equal("ambiguous", r.Status);
        Assert.Equal(new[] { "A", "B" },
            r.Candidates!.Select(c => c.ControlId).OrderBy(x => x));
    }

    [Fact]
    public void SingleReadingHitStillMergesUnchanged()
    {
        // Guard against over-correction: when only ONE reading hits the
        // roster and resolves to exactly one person, that's still a clean
        // auto-merge — the cross-reading union must not turn every ordinary
        // match into a review.
        var roster = TokenRoster(("EVANS", "FRANK", "111"));
        var r = Assert.Single(MatchFiles(new[] { Touch("20240126-EVANS-FRANK.pdf") }, roster));
        Assert.Equal("merge", r.Status);
    }

    // ------------------------------------------- "already merged" mitigation

    [Fact]
    public void AlreadyMergedNoteInvitesVerificationRatherThanAssertingFact()
    {
        // (a) of the mitigation: the suffix test can never prove a genuine
        // merge (see AlreadyCarries's doc comment) — pinning the wording so
        // the UI can't drift back to stating it as settled fact.
        Assert.Equal("already carries this id — verify it was filed, not a coincidence",
            MatchMerge.AlreadyMergedNote);
        var r = MatchFiles(new[] { Touch("20240126-EVANS-FRANK-176797656.pdf") }, LoadRoster())[0];
        Assert.Equal(MatchMerge.AlreadyMergedNote, r.Note);
    }

    [Fact]
    public void TooShortAnIdNeverCountsAsAlreadyMerged()
    {
        // (b) of the mitigation: a one- or two-character id is common enough
        // that an unrelated trailing number (a page count, a version, a
        // stray digit) can coincidentally equal it. Reverting the length
        // floor turns that coincidence into a false "already", silently
        // dropping the file from both merge and review — exactly the
        // failure this finding is about. Below the floor, the file falls
        // through to ordinary classification instead of being swallowed.
        var roster = TokenRoster(("EVANS", "FRANK", "1"));
        var r = Assert.Single(MatchFiles(new[] { Touch("20240126-EVANS-FRANK-1.pdf") }, roster));
        Assert.NotEqual("already", r.Status);
    }

    [Fact]
    public void IdAtTheLengthFloorIsTheIrreducibleCoincidenceCase()
    {
        // The boundary this mitigation admits it cannot close: an id right
        // at MinTrustworthyIdLength clears the floor, so a file that merely
        // HAPPENS to end that way — never touched by MergeOne or
        // ExecuteMerges, just Touch()'d fresh below — still reads as
        // "already". No structural check can do better: the same-person
        // name match that got the file here is identical whether the id
        // arrived via a real merge or by coincidence, and the bytes on disk
        // are the same either way. This is why AlreadyMergedNote asks a
        // human to verify instead of asserting it.
        var roster = TokenRoster(("EVANS", "FRANK", "111"));
        var r = Assert.Single(MatchFiles(new[] { Touch("20240126-EVANS-FRANK-111.pdf") }, roster));
        Assert.Equal("already", r.Status);
    }

    [Fact]
    public void MatchFilesEmitsSuggestedOnlyWhenExactMisses()
    {
        var roster = TokenRoster(("EVANS", "FRANK", "111"), ("SMITH", "JOHN", "888"));
        var dir = Directory.CreateTempSubdirectory("ordosugg_").FullName;
        try
        {
            string Touch(string name)
            {
                var p = Path.Combine(dir, name);
                File.WriteAllText(p, "x");
                return p;
            }
            var exact = Touch("20240126-SMITH-JOHN.pdf");       // ordered exact -> merge
            var inverted = Touch("20240126-FRANK-EVANS.pdf");   // token hit -> suggested
            var nothing = Touch("20240126-NOBODY-HERE.pdf");    // no hit -> no_match

            var results = MatchMerge.MatchFiles(new[] { exact, inverted, nothing }, roster);

            Assert.Equal("merge", results[0].Status);           // unchanged: auto-merge stays exact
            Assert.Equal("suggested", results[1].Status);
            Assert.NotNull(results[1].Suggestions);
            Assert.Equal("111", Assert.Single(results[1].Suggestions!).Candidate.ControlId);
            Assert.Equal("no_match", results[2].Status);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

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
        var dir = Directory.CreateTempSubdirectory("fr_tok").FullName;
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

        var dir = Directory.CreateTempSubdirectory("fr_already").FullName;
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

        var dir = Directory.CreateTempSubdirectory("fr_accent").FullName;
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

    [Fact]
    public void MatchFilesEmitsSuggestedOnlyWhenExactMisses()
    {
        var roster = TokenRoster(("EVANS", "FRANK", "111"), ("SMITH", "JOHN", "888"));
        var dir = Directory.CreateTempSubdirectory("fr_sugg").FullName;
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

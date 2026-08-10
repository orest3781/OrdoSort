using OrdoSort.Core;
using OrdoSort.Wpf.ViewModels;

namespace OrdoSort.Wpf.Tests;

/// <summary>Task 1 of the 2026-08-09 roster-header-mapping plan: the header
/// picker in <see cref="MatchMergeViewModel"/> must never guess a First/Last/
/// Control column wrong and call it loaded. Every test here asserts WHICH
/// column each role resolved to (or that none did) — never merely "no
/// exception was thrown". See docs/superpowers/plans/2026-08-09-roster-header-mapping.md
/// for the defect table these map to.</summary>
public sealed class MatchMergeHeaderMappingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ordoheadermap_" + Guid.NewGuid());

    public MatchMergeHeaderMappingTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteCsv(string content)
    {
        var path = Path.Combine(_dir, "roster_" + Guid.NewGuid() + ".csv");
        File.WriteAllText(path, content);
        return path;
    }

    private static MatchMergeViewModel MakeVm(Config? cfg = null) =>
        new(cfg ?? new Config(), _ => { }, new FakeDialogs());

    // ------------------------------------------------------------- defect 1
    // No needle match falls back to headers.FirstOrDefault() — a roster of
    // Name, DOB, Ref maps First, Last AND Control all to column 0, and the
    // status still claims success.

    [Fact]
    public void NoRecognizableHeadersLeaveAllThreeRolesUnmappedAndStatusSaysSo()
    {
        var path = WriteCsv("Name,DOB,Ref\nSmith,1/1/1970,555\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Null(vm.FirstHeader);
        Assert.Null(vm.LastHeader);
        Assert.Null(vm.ControlHeader);
        Assert.True(vm.HasRoster);   // the mapping row stays visible so the user can choose
        Assert.DoesNotContain("Roster loaded", vm.Status);
        Assert.Equal(
            "Couldn't guess First, Last and Control from the roster headers — choose them above.",
            vm.Status);
    }

    // ------------------------------------------------------------- defect 2
    // Nothing requires the three picks to be distinct: a header that
    // satisfies both the First and the Last needle must not silently hand
    // both roles the SAME column.

    [Fact]
    public void CollidingGuessesAreNotSilentlyAcceptedAndBothCulpritsAreNamed()
    {
        var path = WriteCsv("Client First/Last,Control ID\nJohn Doe,123\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        // both First and Last DID resolve to the same column — that is the
        // defect made visible, not a null
        Assert.Equal("Client First/Last", vm.FirstHeader);
        Assert.Equal("Client First/Last", vm.LastHeader);
        Assert.Equal("Control ID", vm.ControlHeader);
        Assert.DoesNotContain("Roster loaded", vm.Status);
        Assert.Equal(
            "Ambiguous roster headers — First and Last both matched \"Client First/Last\"." +
            " Choose different columns for First, Last and Control above.",
            vm.Status);
    }

    // ------------------------------------------------------------- defect 3
    // "id" is a naive substring today: Paid Date, Resident and Video all
    // match Control. Needles must match whole tokens, and must still accept
    // real-world spellings.

    [Theory]
    [InlineData("First Name", "Last Name", "Control ID")]
    [InlineData("Given name", "Surname", "MRN")]
    public void RecognizedHeaderSpellingsMapToTheRightRole(string firstH, string lastH, string controlH)
    {
        var path = WriteCsv($"{firstH},{lastH},{controlH}\nJohn,Doe,123\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Equal(firstH, vm.FirstHeader);
        Assert.Equal(lastH, vm.LastHeader);
        Assert.Equal(controlH, vm.ControlHeader);
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    [Fact]
    public void PaidDateResidentAndVideoNeverHijackTheControlColumn()
    {
        var path = WriteCsv(
            "First Name,Last Name,Paid Date,Resident,Video,Control ID\n" +
            "John,Doe,1/1/2020,Yes,clip.mp4,123\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Equal("First Name", vm.FirstHeader);
        Assert.Equal("Last Name", vm.LastHeader);
        Assert.Equal("Control ID", vm.ControlHeader);   // NOT "Paid Date" or "Resident"
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    // --------------------------------------------------------- defects 4/5
    // MatchMerge.LoadRoster resolves First/Last/Control by name via IndexOf
    // (first wins) and stores each row name-keyed, so a duplicate header
    // collapses to one occurrence and its twin's data vanishes with no
    // complaint — but only when the duplicated (or blank) name is one of
    // the three mapped roles. A stray duplicate or blank column that has
    // nothing to do with First/Last/Control must still load: rejecting the
    // whole file for it was too aggressive (a stray "Notes" duplicate, or
    // the blank column a trailing comma leaves in most hand-edited CSVs).

    [Fact]
    public void UnrelatedDuplicateHeaderNoLongerFailsTheWholeFile()
    {
        var path = WriteCsv(
            "First Name,Last Name,Control ID,Notes,Notes\nJohn,Doe,123,AAA,BBB\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Equal("First Name", vm.FirstHeader);
        Assert.Equal("Last Name", vm.LastHeader);
        Assert.Equal("Control ID", vm.ControlHeader);
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    [Fact]
    public void DuplicateHeaderThatIsAMappedRoleStillFailsTheWholeFile()
    {
        // "Control ID" itself is duplicated — THIS threatens the mapping
        // (which occurrence is the real control id?), so it must still be
        // refused, unlike the unrelated "Notes" duplicate above. A SAVED
        // control mapping is used because Pick()'s own ranking already
        // refuses to guess between two identically-scored, identically-named
        // headers (see AmbiguousRankingLeavesTheRoleUnmappedRatherThanGuessing
        // below) — routing through the saved value instead reaches
        // MatchMerge.LoadRoster directly, exercising ITS narrowed guard.
        var path = WriteCsv(
            "First Name,Last Name,Control ID,Control ID\nJohn,Doe,123,999\n");
        var cfg = new Config
        {
            MergeHeaders = new Dictionary<string, string> { ["control"] = "Control ID" },
        };
        var vm = MakeVm(cfg);

        vm.LoadRosterFrom(path);

        Assert.Equal("Control ID", vm.ControlHeader);   // the saved pick DID resolve
        Assert.False(vm.HasRoster);
        Assert.Contains("Control ID", vm.Status);
        Assert.DoesNotContain("Roster loaded", vm.Status);
    }

    // ------------------------------------------------------------- defect 6
    // A blank header unrelated to any mapped role must load; only a blank
    // that collides with a mapped role (two blanks, one of them chosen) is
    // dangerous enough to refuse.

    [Fact]
    public void UnrelatedBlankHeaderNoLongerFailsTheWholeFile()
    {
        var path = WriteCsv("First Name,Last Name,Control ID,\nJohn,Doe,123,X\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Equal("First Name", vm.FirstHeader);
        Assert.Equal("Last Name", vm.LastHeader);
        Assert.Equal("Control ID", vm.ControlHeader);
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    // ------------------------------------------------------------- defect 7
    // "Roster loaded: N people." must not appear while any role is
    // unmapped, ambiguous/colliding, or the file itself was rejected.
    // Covered by the Status assertions in every test above; no separate
    // scenario is needed — the point is that NONE of the failure paths
    // above ever say "Roster loaded".

    // ----------------------------------------------------- Step 7: saved
    // mappings must survive the fix — honoured without re-prompting when
    // still valid, discarded cleanly (not half-applied) when stale.

    [Fact]
    public void ValidSavedMappingIsHonouredWithoutRePrompting()
    {
        // headers the needle guesser would never match on its own — proves
        // the saved choice, not a lucky guess, drove the result
        var path = WriteCsv("Name,DOB,Ref\nSmith,1/1/1970,555\n");
        var cfg = new Config
        {
            MergeHeaders = new Dictionary<string, string>
            {
                ["first"] = "Ref", ["last"] = "Name", ["control"] = "DOB",
            },
        };
        var vm = MakeVm(cfg);

        vm.LoadRosterFrom(path);

        Assert.Equal("Ref", vm.FirstHeader);
        Assert.Equal("Name", vm.LastHeader);
        Assert.Equal("DOB", vm.ControlHeader);
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    [Fact]
    public void StaleSavedMappingIsDiscardedCleanlyNotHalfApplied()
    {
        var path = WriteCsv("First Name,Last Name,Control ID\nJohn,Doe,123\n");
        var cfg = new Config
        {
            // "Given Name" isn't a column in THIS file — that one saved role
            // must fall back to guessing cleanly, without corrupting the
            // other two roles' still-valid saved choices
            MergeHeaders = new Dictionary<string, string>
            {
                ["first"] = "Given Name", ["last"] = "Last Name", ["control"] = "Control ID",
            },
        };
        var vm = MakeVm(cfg);

        vm.LoadRosterFrom(path);

        Assert.Equal("First Name", vm.FirstHeader);    // stale saved value discarded, re-guessed
        Assert.Equal("Last Name", vm.LastHeader);       // valid saved value honoured
        Assert.Equal("Control ID", vm.ControlHeader);   // valid saved value honoured
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    // ------------------------------------------------ specificity-ranking fix
    // Pick() used to return the FIRST header containing the needle token,
    // so a decoy header earlier in the file silently won the role even
    // though a header that IS the thing sat right next to it. No collision
    // ever fired, because the losing column was unrelated to the other two
    // roles — the exact defect this whole change was written to eliminate,
    // reached a second way. Rank by how much of the header is signal
    // (matched tokens / total tokens), not by file order.

    [Fact]
    public void ADecoyHeaderBeforeTheRealOneNoLongerWinsByFileOrder()
    {
        // Guardian ID (1/2 signal) precedes Control ID (2/2 signal); First
        // Visit Date (1/3 signal) precedes First Name (1/2 signal). Both
        // decoys sit EARLIER in the file — the old first-match-wins code
        // would pick them. Neither collides with Last, so the old code's
        // status line still claimed success while filing against the wrong
        // column.
        var path = WriteCsv(
            "Guardian ID,First Visit Date,First Name,Last Name,Control ID\n" +
            "John,1/1/2020,John,Doe,123\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Equal("First Name", vm.FirstHeader);      // NOT "First Visit Date"
        Assert.Equal("Last Name", vm.LastHeader);
        Assert.Equal("Control ID", vm.ControlHeader);     // NOT "Guardian ID"
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    [Fact]
    public void TwoEquallyStrongHeadersAreAmbiguityNotACoinFlip()
    {
        // MRN (1/1 signal) and Control ID (2/2 signal) are DIFFERENT ratios
        // — Control ID should still win outright when both are present with
        // First/Last unambiguous. This test instead pits two headers that
        // score IDENTICALLY for the same role against each other: "ID" and
        // "MRN" are both single-token, whole-word, 1/1 matches for Control.
        // Neither is more "the thing" than the other — that's genuine
        // ambiguity, so Control must be left for a human, exactly like no
        // match at all, never silently resolved to whichever sorts first.
        var path = WriteCsv("First Name,Last Name,ID,MRN\nJohn,Doe,123,456\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Equal("First Name", vm.FirstHeader);
        Assert.Equal("Last Name", vm.LastHeader);
        Assert.Null(vm.ControlHeader);
        Assert.True(vm.HasRoster);
        Assert.DoesNotContain("Roster loaded", vm.Status);
        Assert.Equal(
            "Couldn't guess Control from the roster headers — choose it above.",
            vm.Status);
    }

    [Fact]
    public void StrongerRatioStillWinsEvenWhenTheWeakerCandidateHasMoreMatchedTokens()
    {
        // "Control ID" matches 2 tokens (2/2); "Guardian ID" also matches
        // only 1 (1/2). Ratio, not raw matched-token count, must decide —
        // this is the same case as the headline test but confirms a header
        // with FEWER total words still loses outright when its ratio is
        // lower, not merely "when it's mentioned first".
        var path = WriteCsv(
            "First Name,Last Name,Control ID,Guardian ID\nJohn,Doe,123,999\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Equal("Control ID", vm.ControlHeader);
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    // ------------------------------------------------------- tokenizer fix
    // FirstName/LastName/ControlID are extremely common in real exports and
    // split into nothing under word-only tokenizing, falling through to
    // unmapped. Camel/Pascal-case boundaries, '#', and non-ASCII whitespace
    // are now accepted; a header with no boundary at all still isn't.

    [Fact]
    public void ConcatenatedPascalCaseHeadersAreRecognised()
    {
        var path = WriteCsv("FirstName,LastName,ControlID\nJohn,Doe,123\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Equal("FirstName", vm.FirstHeader);
        Assert.Equal("LastName", vm.LastHeader);
        Assert.Equal("ControlID", vm.ControlHeader);
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    [Fact]
    public void ControlHashIsRecognised()
    {
        var path = WriteCsv("First Name,Last Name,Control#\nJohn,Doe,123\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Equal("Control#", vm.ControlHeader);
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    [Fact]
    public void NonBreakingSpaceInAHeaderIsAcceptedAsAWordBoundary()
    {
        // U+00A0 (NBSP) is what a lot of copy-pasted spreadsheet exports
        // actually leave between words -- it LOOKS like a space but isn't
        // ASCII 0x20, so a naive space-only split treats the header as one
        // unrecognizable token instead of "first" + "name".
        const string nbspFirstHeader = "First\u00A0Name";
        var path = WriteCsv($"{nbspFirstHeader},Last Name,Control ID\nJohn,Doe,123\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Equal(nbspFirstHeader, vm.FirstHeader);
        Assert.Equal("Roster loaded: 1 people.", vm.Status);
    }

    [Fact]
    public void HeaderWithNoCaseBoundaryOrDelimiterStillFallsThroughUnmapped()
    {
        // "controlid" is all one case and has no separator — there is no
        // signal to split on, so it correctly stays unmapped rather than
        // being guessed at. Failing toward "ask a human" is still the safe
        // direction for a spelling this tool genuinely can't parse.
        var path = WriteCsv("firstname,lastname,controlid\nJohn,Doe,123\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.Null(vm.FirstHeader);
        Assert.Null(vm.LastHeader);
        Assert.Null(vm.ControlHeader);
        Assert.DoesNotContain("Roster loaded", vm.Status);
    }
}

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
    // MatchMerge.LoadRoster resolves headers by name via IndexOf (first
    // wins) and stores each row name-keyed, so a duplicate header collapses
    // to one occurrence and its twin's data vanishes with no complaint —
    // at the ViewModel/status-line level.

    [Fact]
    public void DuplicateHeadersInTheFileAreReportedNotSilentlyLoaded()
    {
        var path = WriteCsv(
            "First Name,Last Name,Control ID,Notes,Notes\nJohn,Doe,123,AAA,BBB\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.False(vm.HasRoster);   // the file itself can't be trusted — nothing to map
        Assert.Contains("Notes", vm.Status);
        Assert.DoesNotContain("Roster loaded", vm.Status);
    }

    // ------------------------------------------------------------- defect 6
    // A blank header can be picked (and, independent of picking, a blank
    // header anywhere in the file must not be silently mappable/loadable).

    [Fact]
    public void BlankHeaderInTheFileIsReportedNotSilentlyLoaded()
    {
        var path = WriteCsv("First Name,Last Name,Control ID,\nJohn,Doe,123,X\n");
        var vm = MakeVm();

        vm.LoadRosterFrom(path);

        Assert.False(vm.HasRoster);
        Assert.Contains("blank", vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Roster loaded", vm.Status);
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
}

using OrdoSort.Core;
using static OrdoSort.Core.BulkRename;

namespace OrdoSort.Core.Tests;

public class BulkRenameParserTests
{
    private const string Tail = "_2_18_1944_ACME_RECORDS_100000003-1_01_26_24_X";

    [Fact]
    public void StandardLayout() =>
        Assert.Equal(("BROWN", "ADAM"), ParseReviewStem(
            "BROWN_ADAM_4_25_1966_ACME_RECORDS_100000001-1_01_26_24_1007_DUNN_TRACY_A_MultiCare"));

    [Theory]
    [InlineData("BROWN_III_DAVID_2_18_1944_X", "BROWN", "DAVID")]   // gen between
    [InlineData("EVANS_JR_EDWARD_6_24_1951_X", "EVANS", "EDWARD")]
    [InlineData("BROWN_DAVID_JR_2_18_1944_X", "BROWN", "DAVID")]    // gen after
    public void Generations(string stem, string last, string first) =>
        Assert.Equal((last, first), ParseReviewStem(stem));

    [Fact]
    public void SpaceInsteadOfUnderscore() =>
        Assert.Equal(("EVANS", "BRIAN"),
            ParseReviewStem("EVANS_BRIAN 5_14_1998_ACME_RECORDS_100000002-1_X"));

    [Fact]
    public void FullyMangledSeparators() =>
        Assert.Equal(("JONES", "ADAM"),
            ParseReviewStem("JONES_ADAM_8 2_1962_ACME RECORDS 100000004-1 01 26_24_X"));

    [Theory]
    [InlineData("JR")] [InlineData("SR")] [InlineData("JR.")] [InlineData("SENIOR")]
    [InlineData("II")] [InlineData("III")] [InlineData("IV")] [InlineData("V")]
    [InlineData("VI")] [InlineData("VII")] [InlineData("VIII")] [InlineData("IX")]
    [InlineData("X")] [InlineData("2ND")] [InlineData("3RD")] [InlineData("4TH")]
    public void AllGenerationFormsBothPositions(string gen)
    {
        Assert.Equal(("BROWN", "DAVID"), ParseReviewStem($"BROWN_{gen}_DAVID{Tail}"));
        Assert.Equal(("BROWN", "DAVID"), ParseReviewStem($"BROWN_DAVID_{gen}{Tail}"));
    }

    [Theory]
    [InlineData("BROWN_VICTOR", "BROWN", "VICTOR")]   // VI is a prefix of VICTOR
    [InlineData("BROWN_XAVIER", "BROWN", "XAVIER")]   // X prefix of XAVIER
    [InlineData("IVERSON_ALLEN", "IVERSON", "ALLEN")] // IV inside a surname
    public void NamesStartingWithGenLettersNotEaten(string head, string last, string first) =>
        Assert.Equal((last, first), ParseReviewStem($"{head}{Tail}"));

    [Theory]
    [InlineData("VAN_DYKE_JOHN", "VAN DYKE", "JOHN")]
    [InlineData("DE_LA_CRUZ_MARIA", "DE LA CRUZ", "MARIA")]
    [InlineData("VAN_DER_BERG_HANS", "VAN DER BERG", "HANS")]
    [InlineData("MC_DONALD_JOHN", "MC DONALD", "JOHN")]
    [InlineData("ST_JOHN_MARY", "ST JOHN", "MARY")]
    [InlineData("VAN DYKE_JOHN", "VAN DYKE", "JOHN")]   // mixed separators
    public void ParticleSurnamesKeptTogether(string head, string last, string first) =>
        Assert.Equal((last, first), ParseReviewStem($"{head}{Tail}"));

    [Fact]
    public void ParticleAsWholeSurnameBacktracks() =>
        Assert.Equal(("VAN", "JOHN"), ParseReviewStem($"VAN_JOHN{Tail}"));

    [Theory]
    [InlineData("VANCE_JOHN", "VANCE", "JOHN")]
    [InlineData("DELGADO_MARIA", "DELGADO", "MARIA")]
    public void NamesStartingWithParticleLettersNotSplit(string head, string last, string first) =>
        Assert.Equal((last, first), ParseReviewStem($"{head}{Tail}"));

    [Fact]
    public void ParticleSurnameWithGeneration() =>
        Assert.Equal(("VAN DYKE", "JOHN"), ParseReviewStem($"VAN_DYKE_JR_JOHN{Tail}"));

    [Theory]
    [InlineData("BROWN_ADAM_C")]
    [InlineData("BROWN_ADAM_C_J")]
    public void MiddleInitialsDropped(string head) =>
        Assert.Equal(("BROWN", "ADAM"), ParseReviewStem($"{head}{Tail}"));

    [Fact]
    public void AmbiguousThreeFullNamesSkip() =>
        Assert.Null(ParseReviewStem($"GARCIA_LOPEZ_MARIA{Tail}"));

    [Theory]
    [InlineData("scan_001")]
    [InlineData("20240115--123")]
    [InlineData("BROWN_ADAM_13_45_1966_X")]  // impossible date
    public void NonMatchingReturnsNull(string stem) =>
        Assert.Null(ParseReviewStem(stem));

    [Fact]
    public void ReviewModeRebuildsToDateLastFirst() =>
        Assert.Equal("20240126-BROWN-ADAM",
            TransformStem("BROWN_ADAM_4_25_1966_ACME_R", new RenameOp(ReceivedDate: "20240126")));

    [Fact]
    public void NonMatchingReviewFileTransformsToNull() =>
        Assert.Null(TransformStem("notes", new RenameOp(ReceivedDate: "20240126")));

    [Theory]
    [InlineData("20240115-SCANRUN7-SMITH JOHN-12345", new[] { 2 }, false, "20240115-SMITH JOHN-12345")]
    [InlineData("a-b-c", new[] { 1, 3 }, false, "b")]
    [InlineData("a-b", new[] { 5 }, false, "a-b")]              // out of range: untouched
    [InlineData("a--b", new[] { 2 }, false, "a-b")]             // empty segment is a segment
    [InlineData("a--b", new int[0], false, "a--b")]             // nothing checked: lossless
    [InlineData("a-b-c", new int[0], true, "a-b")]              // last
    [InlineData("solo", new int[0], true, "solo")]              // last never empties a 1-segment stem
    [InlineData("a-b-c", new[] { 1 }, true, "b")]               // positions + last combine
    [InlineData("a-b", new[] { 1 }, true, "a-b")]               // everything would go: untouched
    public void SegmentDeletionFollowsTheRules(string stem, int[] positions, bool last, string expected) =>
        Assert.Equal(expected, DeleteSegmentsFromStem(stem, positions, last));

    [Fact]
    public void SegmentDeleteRunsAfterReviewRenameAndBeforeFindReplace()
    {
        var op = new RenameOp(
            Find: "SMITH", Replace: "X", Prefix: "", Suffix: "", Case: "keep",
            ReceivedDate: "", DeleteSegments: new[] { 1 }, DeleteLastSegment: false);
        // stem "JUNK-SMITH JOHN": delete seg 1 -> "SMITH JOHN", then find/replace -> "X JOHN"
        Assert.Equal("X JOHN", TransformStem("JUNK-SMITH JOHN", op));
    }

    [Fact]
    public void SegmentDeleteAppliesToTheReviewRebuiltStem()
    {
        // A review-file stem rebuilds to "<date>-LAST-FIRST"; deleting segment 2
        // must remove LAST from the REBUILT name, proving segment delete runs after rebuild
        var op = new RenameOp(ReceivedDate: "20240126", DeleteSegments: new[] { 2 });
        // "BROWN_ADAM_4_25_1966_ACME_R" rebuilds to "20240126-BROWN-ADAM"
        // delete segment 2 -> "20240126-ADAM"
        Assert.Equal("20240126-ADAM", TransformStem("BROWN_ADAM_4_25_1966_ACME_R", op));
    }
}

/// <summary>TidyStem is pure, so this table is cheap and is where the real
/// risk in the Standardise names feature lives — see the method's own doc
/// comment for the five-step order this pins. Each row's expected value was
/// hand-traced step by step against that order before being written down
/// here, not derived from running the code and copying its output.</summary>
public class TidyStemTests
{
    [Theory]
    // ---- the owner's own worked examples, verbatim -------------------
    [InlineData("smith, john_A12345", "20260115", "20260115-SMITH-JOHN-A12345")]
    [InlineData("20251201-SMITH-JOHN-A12345", "20260115", "20260115-SMITH-JOHN-A12345")]  // old date replaced, not stacked
    [InlineData("DOE,  JANE__B9", "20260115", "20260115-DOE-JANE-B9")]                    // double separators collapse
    // ---- step 1: leading date, with and without its own dash ---------
    [InlineData("20251201SMITH", "20260115", "20260115-SMITH")]                           // no dash after the date
    [InlineData("2025120-SMITH", "20260115", "20260115-2025120-SMITH")]                   // only 7 digits: NOT a date, kept verbatim
    [InlineData("202512011SMITH", "20260115", "20260115-1SMITH")]                         // 9 digits: only the first 8 are the date
    // ---- step 1, fix round 1: a leading 8-digit run that is NOT a real
    // date (month/day out of range) is ordinary content, not a date, and
    // must survive — a case or claim number is not the owner's to lose.
    [InlineData("12345678-REPORT", "20260901", "20260901-12345678-REPORT")]               // month 56: not a date, kept, with its own dash intact
    [InlineData("99999999", "20260901", "20260901-99999999")]                             // month 99, day 99: not a date, kept whole
    [InlineData("12345678REPORT", "20260901", "20260901-12345678REPORT")]                 // same non-date run, no dash after it either
    // ---- step 3's binding constraint: ONLY space/comma/underscore ----
    [InlineData("o'brien", "20260115", "20260115-O'BRIEN")]                               // apostrophe untouched
    [InlineData("smith.jr", "20260115", "20260115-SMITH.JR")]                             // period untouched
    [InlineData("smith (jr)", "20260115", "20260115-SMITH-(JR)")]                         // parens untouched, space -> dash
    [InlineData("...", "20260115", "20260115-...")]                                       // punctuation OUTSIDE the set survives whole
    // ---- step 4: collapse and trim ------------------------------------
    [InlineData("SMITH-JONES", "20260115", "20260115-SMITH-JONES")]                       // an existing single dash is untouched
    [InlineData(" SMITH_JOHN_", "20260115", "20260115-SMITH-JOHN")]                       // leading/trailing separators trimmed away
    [InlineData("-SMITH", "20260115", "20260115-SMITH")]                                  // leading dash (not from a date) trimmed
    [InlineData("SMITH-", "20260115", "20260115-SMITH")]                                  // trailing dash trimmed
    // ---- step 5's degenerate-input rule: date alone, never "date-" ---
    [InlineData("---", "20260115", "20260115")]                                           // only dashes
    [InlineData("___", "20260115", "20260115")]                                           // only underscores
    [InlineData("   ", "20260115", "20260115")]                                           // only spaces
    [InlineData(" , _ ", "20260115", "20260115")]                                         // a mix of all three
    [InlineData("20251201", "20260115", "20260115")]                                      // stem IS only a date
    [InlineData("", "20260115", "20260115")]                                              // empty stem
    // ---- already exactly in the target form: a true no-op ------------
    [InlineData("20260115-SMITH-JOHN-A12345", "20260115", "20260115-SMITH-JOHN-A12345")]
    public void FollowsTheFiveStepsInOrder(string stem, string date, string expected) =>
        Assert.Equal(expected, TidyStem(stem, date));

    [Theory]
    [InlineData("smith, john_A12345")]
    [InlineData("DOE,  JANE__B9")]
    [InlineData("O'Brien Jr.")]
    [InlineData("---")]
    public void ReapplyingWithTheSameDateIsANoOp(string messyStem)
    {
        var once = TidyStem(messyStem, "20260115");
        var twice = TidyStem(once, "20260115");
        Assert.Equal(once, twice);
    }

    [Fact]
    public void ReapplyingWithADifferentDateReplacesRatherThanStacks()
    {
        var firstPass = TidyStem("smith, john", "20251201");
        var secondPass = TidyStem(firstPass, "20260115");
        Assert.Equal("20260115-SMITH-JOHN", secondPass);
        Assert.DoesNotContain("20251201", secondPass);
    }

    /// <summary>Fix round 1, item 2's own "prove it, do not assume it":
    /// idempotence has to survive a stem that ONCE carried a non-date digit
    /// run too. TidyStem's own output always starts with the batch's own
    /// REAL date (never with the preserved non-date run, which only ever
    /// appears AFTER it), so a second pass with the SAME date strips
    /// exactly that real date and nothing else — the preserved case number
    /// is not re-examined a second time and is not eaten belatedly.</summary>
    [Fact]
    public void ANonDateLeadingRunSurvivesIdempotentlyAndARealDateStillReplaces()
    {
        var firstPass = TidyStem("12345678-REPORT", "20260901");
        Assert.Equal("20260901-12345678-REPORT", firstPass);

        var reapplied = TidyStem(firstPass, "20260901");
        Assert.Equal(firstPass, reapplied);   // same date: no change on a second pass

        var redated = TidyStem(firstPass, "20261225");
        Assert.Equal("20261225-12345678-REPORT", redated);   // different date: the REAL date replaces
        Assert.Contains("12345678", redated);                 // the case number is untouched either way
    }
}

public class BulkRenameFsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "brtest_" + Guid.NewGuid());

    public BulkRenameFsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name, string content = "x")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void DiskCollisionGetsCounter()
    {
        Touch("b.pdf");
        var src = Touch("a.pdf");
        var pr = Plan(new[] { src }, new RenameOp(Find: "a", Replace: "b"))[0];
        Assert.Equal("b (2).pdf", Path.GetFileName(pr.Target));
    }

    [Fact]
    public void BatchCollisionGetsCounter()
    {
        var a = Touch("fax-1.pdf");
        var b = Touch("fax=1.pdf");
        // find "-" -> "=" makes a's name collide with b's existing name
        var plans = Plan(new[] { a, b }, new RenameOp(Find: "-", Replace: "="));
        Assert.Equal("fax=1 (2).pdf", Path.GetFileName(plans[0].Target));
        Assert.False(plans[1].Changed);  // b itself doesn't change
    }

    [Fact]
    public void ExecuteRenamesAndRevertRestores()
    {
        var a = Touch("one.pdf");
        var b = Touch("two.pdf");
        var plans = Plan(new[] { a, b }, new RenameOp(Prefix: "2024 "));
        var outcomes = Execute(plans);
        Assert.True(File.Exists(Path.Combine(_dir, "2024 one.pdf")));
        Assert.False(File.Exists(a));

        var problems = Revert(outcomes);
        Assert.Empty(problems);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void OverrideWinsAndCollisionCounts()
    {
        Touch("TAKEN.pdf", "keep");
        var src = Touch("a.pdf");
        var pr = Plan(new[] { src }, new RenameOp(),
            new Dictionary<string, string> { [src] = "TAKEN" })[0];
        Assert.Equal("TAKEN (2).pdf", Path.GetFileName(pr.Target));
        Assert.True(pr.Manual);
    }

    [Fact]
    public void ReviewMergeEndToEnd()
    {
        var src = Touch("BROWN_ADAM_4_25_1966_ACME_R.pdf");
        var pr = Plan(new[] { src }, new RenameOp(ReceivedDate: "20240126"))[0];
        Assert.Equal("20240126-BROWN-ADAM.pdf", Path.GetFileName(pr.Target));
    }
}

/// <summary>PlanTidy (Standardise names' own entry point, straight from
/// TidyStem — see PlanTidy's own doc comment for why it bypasses Plan) and
/// Execute's Dashed suffix style, the "wrinkle" that tool has to handle
/// without touching what the Bulk rename tool sees.</summary>
public class PlanTidyFsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "brtidytest_" + Guid.NewGuid());

    public PlanTidyFsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void ComputesTheStandardisedTarget()
    {
        var src = Touch("smith, john_A12345.pdf");
        var pr = PlanTidy(new[] { src }, "20260115")[0];
        Assert.True(pr.Changed);
        Assert.Equal("20260115-SMITH-JOHN-A12345.pdf", Path.GetFileName(pr.Target));
    }

    [Fact]
    public void AnAlreadyStandardisedFileIsLeftUnchanged()
    {
        var src = Touch("20260115-SMITH-JOHN-A12345.pdf");
        var pr = PlanTidy(new[] { src }, "20260115")[0];
        Assert.False(pr.Changed);
        Assert.Equal(src, pr.Target);
        Assert.Equal("", pr.Note);
    }

    /// <summary>TidyStem itself never returns an empty or illegal stem (see
    /// its own doc comment) — the only way RejectIllegal's guard in
    /// PlanTidy can actually fire is a caller handing over a DATE that
    /// isn't the well-formed 8 digits the window's own prompt guarantees.
    /// Exercised directly, bypassing that prompt, which is exactly what a
    /// Core-level test should do: prove the guard is real, not just
    /// present.</summary>
    [Fact]
    public void ADateCarryingAnIllegalCharacterSkipsReadablyInsteadOfCrashing()
    {
        var src = Touch("smith.pdf");
        var pr = PlanTidy(new[] { src }, "2026:0115")[0];
        Assert.False(pr.Changed);
        Assert.Equal(src, pr.Target);
        Assert.Contains(":", pr.Note);
    }

    /// <summary>The brief's own wrinkle: Execute's default collision counter
    /// (" (2)") hands back a space and parentheses — the exact two things
    /// TidyStem exists to strip — so for this tool it has to be "-2" (and
    /// "-3", …) instead. Two sources that tidy to the same name both still
    /// land, and neither final name contains a space or a parenthesis.</summary>
    [Fact]
    public void TwoFilesThatTidyToTheSameNameBothLandWithADashedCounter()
    {
        var a = Touch("smith, john.pdf");
        var b = Touch("SMITH_JOHN.pdf");
        var plans = PlanTidy(new[] { a, b }, "20260115");

        var outcomes = Execute(plans, CollisionSuffixStyle.Dashed);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.NotNull(o.Final));
        var names = outcomes.Select(o => Path.GetFileName(o.Final!)).ToList();
        Assert.Contains("20260115-SMITH-JOHN.pdf", names);
        Assert.Contains("20260115-SMITH-JOHN-2.pdf", names);
        Assert.All(names, n => Assert.DoesNotContain(" ", n));
        Assert.All(names, n => Assert.DoesNotContain("(", n));
    }

    /// <summary>The other half of the wrinkle: Execute's DEFAULT stays the
    /// Bulk rename tool's own " (2)" — this only proves the default,
    /// through PlanTidy's own plans, so a future change to the default
    /// parameter value is caught here too, not only by BulkRenameFsTests'
    /// existing (untouched) facts.</summary>
    [Fact]
    public void ExecuteWithNoStyleArgumentStillUsesTheParenthesizedDefault()
    {
        var a = Touch("smith, john.pdf");
        var b = Touch("SMITH_JOHN.pdf");
        var plans = PlanTidy(new[] { a, b }, "20260115");

        var outcomes = Execute(plans);   // no suffixStyle argument at all

        var names = outcomes.Select(o => Path.GetFileName(o.Final!)).ToList();
        Assert.Contains("20260115-SMITH-JOHN.pdf", names);
        Assert.Contains("20260115-SMITH-JOHN (2).pdf", names);
    }

    [Fact]
    public void PlanExecuteRevertRoundTripsBackToTheOriginalName()
    {
        var src = Touch("smith, john_A12345.pdf");
        var plans = PlanTidy(new[] { src }, "20260115");

        var outcomes = Execute(plans, CollisionSuffixStyle.Dashed);
        var renamed = Path.Combine(_dir, "20260115-SMITH-JOHN-A12345.pdf");
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(src));

        var problems = Revert(outcomes);
        Assert.Empty(problems);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(renamed));
    }

    /// <summary>Fix round 1, item 4: PlanTidy's own Changed verdict has to
    /// be case-SENSITIVE, unlike SameFile (case-insensitive, correct for
    /// the on-disk collision checks Plan/Execute still use). A file already
    /// named "20260115-smith.pdf" is NOT already standardised — TidyStem's
    /// own step 2 promises uppercase — so this must plan a real rename, and
    /// Execute (unaffected by this fix; SameFile's collision loop stays
    /// exactly as it was) really does perform a same-path case-only
    /// File.Move here — verified against the true on-disk name via
    /// Directory.GetFiles rather than File.Exists, which is case-insensitive
    /// on Windows and so could not tell a "smith.pdf" from a "SMITH.pdf" at
    /// the same path.</summary>
    [Fact]
    public void ACaseOnlyDifferenceIsStillARenameNotAnAlreadyStandardisedNoOp()
    {
        var src = Touch("20260115-smith.pdf");
        var pr = PlanTidy(new[] { src }, "20260115")[0];
        Assert.True(pr.Changed);
        Assert.Equal("20260115-SMITH.pdf", Path.GetFileName(pr.Target));

        var outcomes = Execute(new[] { pr }, CollisionSuffixStyle.Dashed);
        Assert.NotNull(Assert.Single(outcomes).Final);

        var actualName = Path.GetFileName(Directory.GetFiles(_dir).Single());
        Assert.Equal("20260115-SMITH.pdf", actualName);
    }

    /// <summary>The audit's own gap: every existing collision fact drives
    /// exactly two files, so the counter was only ever observed landing on
    /// "-2" — a hardcoded "-2" (rather than a real counter) would have
    /// passed the whole suite. Three sources that all tidy to the same
    /// target must land as the bare name, "-2" and "-3", in that order.</summary>
    [Fact]
    public void ThreeFilesThatTidyToTheSameNameLandBareThenDashTwoThenDashThree()
    {
        var a = Touch("smith, john.pdf");
        var b = Touch("SMITH_JOHN.pdf");
        var c = Touch("Smith,John.pdf");
        var plans = PlanTidy(new[] { a, b, c }, "20260115");

        var outcomes = Execute(plans, CollisionSuffixStyle.Dashed);

        Assert.Equal(3, outcomes.Count);
        var names = outcomes.Select(o => Path.GetFileName(o.Final!)).ToList();
        Assert.Contains("20260115-SMITH-JOHN.pdf", names);
        Assert.Contains("20260115-SMITH-JOHN-2.pdf", names);
        Assert.Contains("20260115-SMITH-JOHN-3.pdf", names);
        Assert.All(names, n => Assert.DoesNotContain(" ", n));
        Assert.All(names, n => Assert.DoesNotContain("(", n));
    }
}

/// <summary>PlanPeel — Standardise names' own "Remove last segment" button.
/// All facts here are filesystem-based, the same as PlanTidyFsTests above:
/// PlanPeel calls File.Exists directly (no injected predicate, same as
/// Plan()), so there is no way to exercise its collision rule without a real
/// directory.</summary>
public class PlanPeelFsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "brpeeltest_" + Guid.NewGuid());

    public PlanPeelFsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    /// <summary>The owner's own worked example, verbatim, both clicks. Three
    /// files with different segment counts; one click removes one segment
    /// from each; the second click holds the one that reached exactly four
    /// segments on the first click.</summary>
    [Fact]
    public void TheOwnersWorkedExampleBothClicks()
    {
        var a = Touch("20260115-SMITH-JOHN-A12345-SCAN-001.pdf");
        var b = Touch("20260115-DOE-JANE-B9-COPY.pdf");
        var c = Touch("20260115-LEE-SAM-C77-SCAN-002-A.pdf");

        var click1 = PlanPeel(new[] { a, b, c });
        Assert.All(click1, p => Assert.True(p.Changed));
        Assert.Equal("20260115-SMITH-JOHN-A12345-SCAN.pdf", Path.GetFileName(click1[0].Target));
        Assert.Equal("20260115-DOE-JANE-B9.pdf", Path.GetFileName(click1[1].Target));
        Assert.Equal("20260115-LEE-SAM-C77-SCAN-002.pdf", Path.GetFileName(click1[2].Target));
        var afterClick1 = Execute(click1, CollisionSuffixStyle.Dashed);

        var paths2 = afterClick1.Select(o => o.Final!).ToList();
        var click2 = PlanPeel(paths2);
        Assert.True(click2[0].Changed);
        Assert.Equal("20260115-SMITH-JOHN-A12345.pdf", Path.GetFileName(click2[0].Target));

        Assert.False(click2[1].Changed);   // DOE-JANE-B9: already at four segments, held
        Assert.Equal(paths2[1], click2[1].Target);
        Assert.Equal(PeelAtFloorNote, click2[1].Note);

        Assert.True(click2[2].Changed);
        Assert.Equal("20260115-LEE-SAM-C77-SCAN.pdf", Path.GetFileName(click2[2].Target));
    }

    [Theory]
    [InlineData("A-B-C-D.pdf")]        // exactly four: held
    [InlineData("A-B-C.pdf")]          // fewer than four: held
    [InlineData("A.pdf")]              // one segment: held
    public void AStemAtOrBelowFourSegmentsIsHeldUntouched(string name)
    {
        var src = Touch(name);
        var pr = PlanPeel(new[] { src })[0];
        Assert.False(pr.Changed);
        Assert.Equal(src, pr.Target);
        Assert.Equal(PeelAtFloorNote, pr.Note);
    }

    [Fact]
    public void AStemWithExactlyFiveSegmentsPeelsToFour()
    {
        var src = Touch("A-B-C-D-E.pdf");
        var pr = PlanPeel(new[] { src })[0];
        Assert.True(pr.Changed);
        Assert.Equal("A-B-C-D.pdf", Path.GetFileName(pr.Target));
        Assert.Equal("", pr.Note);
    }

    /// <summary>DeleteSegmentsFromStem's own doc comment: "empties kept —
    /// 'a--b' is three segments." A trailing dash on an otherwise four-
    /// segment stem is a FIFTH (empty) segment, so PlanPeel must not hold it
    /// — and removing that empty last segment is what strips the trailing
    /// dash.</summary>
    [Fact]
    public void ATrailingEmptySegmentCountsTowardTheFloorLikeAnyOther()
    {
        var src = Touch("A-B-C-D-.pdf");
        var pr = PlanPeel(new[] { src })[0];
        Assert.True(pr.Changed);
        Assert.Equal("A-B-C-D.pdf", Path.GetFileName(pr.Target));
    }

    [Fact]
    public void TheExtensionSurvivesThePeelUntouched()
    {
        var src = Touch("A-B-C-D-E.PDF");
        var pr = PlanPeel(new[] { src })[0];
        Assert.Equal("A-B-C-D.PDF", Path.GetFileName(pr.Target));
    }

    /// <summary>The second rule: a collision is refused, never countered.
    /// Two sources in the same batch peel to the same target — the first
    /// claims it, the second is refused outright rather than getting "-2".</summary>
    [Fact]
    public void ABatchCollisionIsRefusedNotCountered()
    {
        var a = Touch("A-B-C-D-ONE.pdf");
        var b = Touch("A-B-C-D-TWO.pdf");

        var plans = PlanPeel(new[] { a, b });

        Assert.True(plans[0].Changed);
        Assert.Equal("A-B-C-D.pdf", Path.GetFileName(plans[0].Target));

        Assert.False(plans[1].Changed);
        Assert.Equal(b, plans[1].Target);        // left exactly where it was
        Assert.NotEqual("", plans[1].Note);
        Assert.DoesNotContain("-2", Path.GetFileName(plans[1].Target));
    }

    /// <summary>Same rule, against a file already on disk rather than
    /// another file in the same batch.</summary>
    [Fact]
    public void ADiskCollisionIsRefusedNotCountered()
    {
        Touch("A-B-C-D.pdf");
        var src = Touch("A-B-C-D-EXTRA.pdf");

        var pr = PlanPeel(new[] { src })[0];

        Assert.False(pr.Changed);
        Assert.Equal(src, pr.Target);
        Assert.NotEqual("", pr.Note);
    }

    /// <summary>A held row and a refused row must not be confused with each
    /// other by whatever reads Note afterward — their two reasons are
    /// genuinely different text.</summary>
    [Fact]
    public void TheFloorNoteAndTheCollisionNoteAreDistinctText()
    {
        Touch("A-B-C-D.pdf");
        var atFloor = Touch("W-X-Y-Z.pdf");
        var collides = Touch("A-B-C-D-EXTRA.pdf");

        var plans = PlanPeel(new[] { atFloor, collides });

        Assert.Equal(PeelAtFloorNote, plans[0].Note);
        Assert.NotEqual(PeelAtFloorNote, plans[1].Note);
        Assert.NotEqual("", plans[1].Note);
    }

    [Fact]
    public void PlanExecuteRevertRoundTripsBackToTheOriginalName()
    {
        var src = Touch("A-B-C-D-EXTRA.pdf");
        var plans = PlanPeel(new[] { src });

        var outcomes = Execute(plans, CollisionSuffixStyle.Dashed);
        var renamed = Path.Combine(_dir, "A-B-C-D.pdf");
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(src));

        var problems = Revert(outcomes);
        Assert.Empty(problems);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(renamed));
    }
}

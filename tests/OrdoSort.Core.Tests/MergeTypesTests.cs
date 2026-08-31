namespace OrdoSort.Core.Tests;

public class MergeTypesTests
{
    [Theory]
    [InlineData("pdf", "pdf")] [InlineData("docx", "word")] [InlineData("rtf", "word")]
    [InlineData("csv", "excel")] [InlineData("xlsx", "excel")] [InlineData("pptx", "powerpoint")]
    [InlineData("TIF", "images")] [InlineData("json", "text")]
    public void EveryHandledExtensionKnowsItsGroup(string extension, string group) =>
        Assert.Equal(group, MergeTypes.GroupOf(extension));

    [Theory]
    [InlineData("exe")] [InlineData("mp4")] [InlineData("")]
    public void AForeignTypeHasNoGroup(string extension) =>
        Assert.Null(MergeTypes.GroupOf(extension));

    // A deliberate v1 non-goal, not a silent omission: opening a web
    // document in Word fetches remote resources, which is both a hang
    // surface and a beaconing surface AutomationSecurity does not cover,
    // and this repo has a PHI history. Locked in as a test so a future
    // change cannot silently re-add them without someone noticing.
    [Theory]
    [InlineData("htm")] [InlineData("html")]
    public void WebDocumentsAreNotInTheWordGroup(string extension) =>
        Assert.Null(MergeTypes.GroupOf(extension));

    [Fact]
    public void NoExtensionBelongsToTwoGroups()
    {
        var all = MergeTypes.AllGroups.SelectMany(MergeTypes.ExtensionsOf).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // NothingStoredDefaultsToPdfAndZipOnly and UntickingEverythingSurvivesA
    // RoundTripInsteadOfReadingAsNeverConfigured together pin down the
    // sentinel decision: null/blank (nothing ever saved) and
    // MergeTypes.NoneStored (the user turned every group off on purpose)
    // must load as two different things, or unticking every box would come
    // back re-enabled to the default on the next launch.

    /// <summary>2026-08-31, owner's decision (asked directly): the
    /// conservative default is PDF and Zip only, not every group — every
    /// type this feature added starts opt-in, so the window behaves exactly
    /// as it did before this feature shipped until the user deliberately
    /// ticks a box. Renamed from NothingStoredMeansEverythingIsOn, which
    /// pinned the OLD contract this fact replaces rather than extends —
    /// flipping the expected value under the old name would have left a
    /// fact whose name lied about what it checks.</summary>
    [Fact]
    public void NothingStoredDefaultsToPdfAndZipOnly() =>
        Assert.Equal(new[] { MergeTypes.Pdf, MergeTypes.Zip }.OrderBy(g => g),
                     MergeTypes.Load(null).OrderBy(g => g));

    /// <summary>The default is conservative, not merely short — every group
    /// this feature added (Word, Excel, PowerPoint, Images, Text) must be
    /// ABSENT from it, not just "Pdf and Zip happen to be present". Without
    /// this, a regression that made the default "every group" would still
    /// pass NothingStoredDefaultsToPdfAndZipOnly's own Assert.Equal only by
    /// coincidence if that fact used Contains rather than a set equality —
    /// it doesn't, but this fact pins the "nothing else" half explicitly and
    /// independently, extension group by extension group, rather than
    /// leaning on one Assert.Equal to carry the whole claim.</summary>
    [Theory]
    [InlineData(MergeTypes.Word)] [InlineData(MergeTypes.Excel)] [InlineData(MergeTypes.PowerPoint)]
    [InlineData(MergeTypes.Images)] [InlineData(MergeTypes.Text)]
    public void EveryNewerGroupStartsOffByDefault(string group) =>
        Assert.DoesNotContain(group, MergeTypes.Load(null));

    [Fact]
    public void TheEnabledSetSurvivesARoundTrip()
    {
        var chosen = new[] { MergeTypes.Pdf, MergeTypes.Images };
        Assert.Equal(chosen.OrderBy(g => g),
                     MergeTypes.Load(MergeTypes.Save(chosen)).OrderBy(g => g));
    }

    [Fact]
    public void AGroupNameFromALaterVersionIsIgnoredRatherThanBreakingTheLoad() =>
        Assert.Equal(new[] { "pdf" }, MergeTypes.Load("pdf,hologram"));

    [Fact]
    public void UntickingEverythingSurvivesARoundTripInsteadOfReadingAsNeverConfigured()
    {
        // The defect this guards: an empty set stored as "" is
        // indistinguishable from "never set", so a user who unticks every
        // type gets the default (pdf, zip) back on the next launch instead
        // of staying at genuinely nothing.
        var stored = MergeTypes.Save(Array.Empty<string>());
        Assert.False(string.IsNullOrWhiteSpace(stored),
            "an empty selection must not be stored as an empty string");
        Assert.Empty(MergeTypes.Load(stored));
        Assert.NotEmpty(MergeTypes.Load(null));      // never configured is different
    }
}
